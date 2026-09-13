using System.Buffers.Binary;
using Cranberry.Protocol;
using Cranberry.Zone.Match;
using Cranberry.Zone.World;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.Inventory;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed partial class WorldModeRoutingTests
{
    [Fact]
    public void TeamStatusMatchesTheAugustSparseReaderAndOneBasedPalette()
    {
        using var writer = new PacketWriter();
        new GroupMemberHudStatus(0x0102030405060708, 3, false, true, 75).WriteTo(writer);
        Assert.Equal(Convert.FromHexString("1324020807060504030201070003024B"), writer.Written.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => new GroupMemberHudStatus(1, 0, false, false, 100).WriteTo(writer));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GroupMemberHudStatus(1, 6, false, false, 100).WriteTo(writer));
    }

    private static bool IsTeamHudStatus(byte[] packet) => packet.Length == 23 && packet[1] == 0x13 && packet[2] == 0x24;
    private static ulong HudSubject(byte[] packet) => BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(4));

    [Fact]
    public void TeamEquipmentUsesItemDefinitionAndExplicitClearsInNativeFieldOrder()
    {
        using var writer = new PacketWriter();
        new GroupMemberHudStatus(0x0102030405060708, 3, false, true, 75, 2425, true, false).WriteTo(writer);
        Assert.Equal(Convert.FromHexString("13240208070605040302013F0003024B790900000100"), writer.Written.ToArray());
    }

    [Theory]
    [InlineData(6u, 2)]
    [InlineData(7u, 5)]
    public void EquipmentChangesReachTeammatesImmediatelyWithoutMovementOrPeerInterest(uint world, int size)
    {
        var (f, players) = TeamMatch(world, size + 1);
        var subject = players[0];
        ulong nextItem = 5000;
        var inventory = new PlayerInventory(Get<ulong>(subject.Tag!, "Guid"), () => ++nextItem);
        inventory.Bootstrap();
        Set(subject.Tag!, "Inventory", inventory);
        var gun = inventory.CreateInstance(2425, 1);
        inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        var armor = inventory.CreateInstance(2205, 1);
        inventory.BindLoadout(armor, SurvivorLoadout.ChestArmor, BodySlots.ChestArmor);
        var helmet = inventory.CreateInstance(2170, 1);
        inventory.BindLoadout(helmet, SurvivorLoadout.Head, BodySlots.Head);

        // Exercise the real appearance hook without requiring a movement/interest tick.
        void Redress()
        {
            f.Recorded.Clear();
            Call(f.Service, "SendDressTunnel", subject, subject.Tag,
                (Action<PacketWriter>)(w => new SetCharacterEquipment(Get<ulong>(subject.Tag!, "Guid")).WriteTo(w)),
                "team equipment regression", Array.Empty<CharacterEquipmentAttachment>(), Array.Empty<EquipmentSlotRow>());
        }
        void Check(uint item, bool protectedBody, bool protectedHead)
        {
            var packets = f.Recorded.Where(r => IsTeamHudStatus(r.Bytes)).ToArray();
            Assert.Equal(size, packets.Length);
            Assert.DoesNotContain(packets, r => r.Connection == players[^1]);
            Assert.All(packets, r =>
            {
                Assert.Equal(item, BinaryPrimitives.ReadUInt32LittleEndian(r.Bytes.AsSpan(17)));
                Assert.Equal(protectedBody ? 1 : 0, r.Bytes[21]);
                Assert.Equal(protectedHead ? 1 : 0, r.Bytes[22]);
            });
        }

        Redress(); Check(2425, true, true);
        Assert.True(inventory.TrySelectLoadoutSlot(SurvivorLoadout.Fists, out _));
        inventory.RemoveUnits(armor.Guid, 0);
        inventory.RemoveUnits(helmet.Guid, 0);
        // The mesh packet is deliberately identical; the HUD must still clear its old icons.
        Redress(); Check(PlayerInventory.SurvivorFistsItemDefinitionId, false, false);
        Assert.True(inventory.TrySelectLoadoutSlot(SurvivorLoadout.Wheel1, out _));
        Redress(); Check(2425, false, false);
    }

    [Theory]
    [InlineData(6u, 2)]
    [InlineData(7u, 5)]
    public void VehicleMovementAndDismountUpdateTeamHudWithoutFootPackets(uint world, int size)
    {
        var (f, players) = TeamMatch(world, size + 1);
        var fleet = new VehicleFleet(VehicleRoster.LoadDefault());
        var car = new MatchVehicle(500, 99, fleet.Roster.Require(1), new(100, 20, 100), 0, 100000, 5000);
        fleet.Add(car);
        foreach (var player in players) Set(player.Tag!, "Fleet", fleet);
        ulong driver = Get<ulong>(players[0].Tag!, "Guid"), passenger = Get<ulong>(players[1].Tag!, "Guid");
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(car.Guid, driver, 0, 10000, out _, out _));
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(car.Guid, passenger, 1, 20000, out _, out _));

        // The managed vehicle path is the only movement source; seated channel 2 is suppressed.
        Call(f.Service, "NoteVehicleOccupantPositions", players[0], players[0].Tag, car);
        var received = f.Recorded.Where(r => r.Connection == players[1]).Select(r => r.Bytes).ToArray();
        var status = Assert.Single(received, p => IsTeamHudStatus(p) && HudSubject(p) == driver);
        Assert.Equal(new byte[] { 1, 2, 100 }, status.AsSpan(14, 3).ToArray());
        using var expected = new PacketWriter();
        new GroupMemberUpdate(new(driver, Get<string>(players[0].Tag!, "CharacterName"), car.Position, 0)).WriteTo(expected);
        var pose = Assert.Single(received, p => p.Length > 12 && p[1] == 0x13 && p[2] == 0x14 && HudSubject(p) == driver);
        Assert.Equal(expected.Written.ToArray(), pose[1..]);
        Assert.DoesNotContain(f.Recorded, r => r.Connection == players[^1]);

        // A transition owes its status immediately, even inside the one-second pose throttle.
        f.Recorded.Clear();
        Assert.NotNull(fleet.Evict(driver));
        Call(f.Service, "SendVehicleClearingBurst", players[0], players[0].Tag, car);
        var cleared = Assert.Single(f.Recorded, r => r.Connection == players[1]
            && IsTeamHudStatus(r.Bytes) && HudSubject(r.Bytes) == driver).Bytes;
        Assert.Equal(new byte[] { 1, 0, 100 }, cleared.AsSpan(14, 3).ToArray());

        f.Recorded.Clear();
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(car.Guid, driver, 0, 30000, out _, out _));
        Call(f.Service, "PublishVehicleOccupants", players[0], players[0].Tag, car);
        var mounted = f.Recorded.Where(r => r.Connection == players[1]
            && IsTeamHudStatus(r.Bytes) && HudSubject(r.Bytes) == driver).ToArray();
        Assert.NotEmpty(mounted);
        Assert.All(mounted, r => Assert.Equal(new byte[] { 1, 2, 100 }, r.Bytes.AsSpan(14, 3).ToArray()));
        Assert.DoesNotContain(f.Recorded, r => r.Connection == players[^1]);
    }

    [Theory]
    [InlineData(6u, 2)]
    [InlineData(7u, 5)]
    public void TeamRosterPublishesEveryColorToItsOwnSquadAfterTheLastRowReset(uint world, int size)
    {
        var (f, players) = TeamMatch(world, size + 1);
        Call(f.Service, "PublishTeamRoster", players[0].Tag);
        Assert.DoesNotContain(f.Recorded, record => record.Connection == players[^1]);
        foreach (var player in players[..size])
        {
            var packets = f.Recorded.Where(record => record.Connection == player).Select(record => record.Bytes).ToArray();
            var statuses = packets.Where(IsTeamHudStatus).ToArray();
            Assert.Equal(size, statuses.Length);
            Assert.Equal(Enumerable.Range(1, size).Select(slot => (byte)slot), statuses.Select(packet => packet[14]));
            foreach (var status in statuses)
            {
                int before = Array.FindLastIndex(packets, packet => packet.Length > 12 && packet[1] == 0x13
                    && packet[2] == 0x14 && HudSubject(packet) == HudSubject(status));
                Assert.True(before >= 0);
                Assert.Same(status, packets[before + 1]);
                Assert.Equal(0, status[15]); // Alive, not driving.
                Assert.Equal(100, status[16]);
            }
        }

        f.Recorded.Clear();
        Call(f.Service, "PublishTeamPose", players[0].Tag);
        Assert.Equal(size, f.Recorded.Count(record => IsTeamHudStatus(record.Bytes)));
        Assert.All(f.Recorded.Where(record => IsTeamHudStatus(record.Bytes)), record => Assert.Equal(1, record.Bytes[14]));
    }

    [Theory]
    [InlineData(6u, 2)]
    [InlineData(7u, 5)]
    public void LeavingAndReplacingATeammateKeepsSurvivorsColors(uint world, int size)
    {
        var (f, players) = TeamMatch(world, size);
        Call(f.Service, "LeaveSharedLoot", players[0].Tag);
        foreach (var record in f.Recorded.Where(record => IsTeamHudStatus(record.Bytes)))
            Assert.Equal((byte)HudSubject(record.Bytes), record.Bytes[14]);

        var replacement = f.Player();
        Set(replacement.Tag!, "BountyAdmission", Get<MatchAdmissionContext>(players[1].Tag!, "BountyAdmission"));
        var phase = replacement.Tag!.GetType().GetProperty("Match")!;
        phase.SetValue(replacement.Tag, Enum.Parse(phase.PropertyType, "InMatch"));
        f.Recorded.Clear();
        Call(f.Service, "JoinSharedLoot", replacement, replacement.Tag);
        var colors = f.Recorded.Where(record => record.Connection == replacement && IsTeamHudStatus(record.Bytes))
            .ToDictionary(record => HudSubject(record.Bytes), record => record.Bytes[14]);
        Assert.Equal(size, colors.Count);
        Assert.Equal(1, colors[Get<ulong>(replacement.Tag!, "Guid")]);
        foreach (var survivor in players[1..])
        {
            ulong guid = Get<ulong>(survivor.Tag!, "Guid");
            Assert.Equal((byte)guid, colors[guid]);
        }
    }

    [Fact]
    public void DamagedAndEliminatedPlayersKeepTheirColorAndUpdateTeammates()
    {
        var (f, players) = TeamMatch(6, 3);
        f.Service.ForTest(players[0]).Damage(2500, DamageCause.ToxicGas);
        var damaged = f.Recorded.Last(record => record.Connection == players[1] && IsTeamHudStatus(record.Bytes)).Bytes;
        Assert.Equal(1, damaged[14]);
        Assert.Equal(0, damaged[15]);
        Assert.Equal(75, damaged[16]);
        Assert.DoesNotContain(f.Recorded, record => record.Connection == players[2] && IsTeamHudStatus(record.Bytes));
        f.Service.ForTest(players[0]).Damage(10_000, DamageCause.ToxicGas);
        var dead = f.Recorded.Last(record => record.Connection == players[1] && IsTeamHudStatus(record.Bytes)
            && HudSubject(record.Bytes) == Get<ulong>(players[0].Tag!, "Guid")).Bytes;
        Assert.Equal(1, dead[14]);
        Assert.Equal(1, dead[15]);
        Assert.Equal(0, dead[16]);
    }

    [Fact]
    public void SoloRosterDoesNotCreateTeamColors()
    {
        var (f, players) = TeamMatch(1, 2);
        Call(f.Service, "PublishTeamRoster", players[0].Tag);
        f.Service.ForTest(players[0]).Damage(2500, DamageCause.ToxicGas);
        Assert.DoesNotContain(f.Recorded, record => IsTeamHudStatus(record.Bytes));
    }
}
