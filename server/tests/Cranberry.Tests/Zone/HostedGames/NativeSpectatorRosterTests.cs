using System.Numerics;
using System.Text;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.HostedGames;

public sealed class SpectatorRosterPacketTests
{
    [Fact]
    public void NativeRosterMatchesIndependentlyEncodedAugustFixture()
    {
        // Independent native-parser fixture: native-roster-one-player.bin, SHA256
        // ac8b174e3af9541c55e54dc21371e3fa1a0007d19fe510490abe5360ebd88d22.
        byte[] expected = Convert.FromHexString("e2020000000000010000000210000000000000110000004e6174697665526f7374657250726f6265000000004b000000000000008003640014009cff00000000000000000000");
        using var writer = new PacketWriter();
        new SpectatorRosterPacket([new(0x1002, "NativeRosterProbe", 75, 128, 100, 20, -100, 3)]).WriteTo(writer);
        Assert.Equal(70, writer.Position);
        Assert.Equal(expected, writer.Written.ToArray());
    }

    [Fact]
    public void EmptyReplacementContainsTheMandatoryFinalMapCount()
    {
        using var writer = new PacketWriter();
        new SpectatorRosterPacket([]).WriteTo(writer);
        Assert.Equal(Convert.FromHexString("e20200000000000000000000000000"), writer.Written.ToArray());
    }

    [Fact]
    public void Utf8NamesAndMaximumRosterRespectTheNativeVariableLengthLayout()
    {
        var players = Enumerable.Range(1, 150).Select(i => new SpectatorPlayerRow((ulong)i, "雪 Friend", 100, 0, -100, 400, 123)).ToArray();
        using var writer = new PacketWriter();
        new SpectatorRosterPacket(players).WriteTo(writer);
        Assert.Equal(15 + 150 * (38 + Encoding.UTF8.GetByteCount("雪 Friend")), writer.Position);
        using var oversized = new PacketWriter();
        Assert.Throws<ArgumentException>(() => new SpectatorRosterPacket([.. players, players[0]]).WriteTo(oversized));
        Assert.Equal(0, oversized.Position);
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(3.1415927f, 128)]
    [InlineData(-1.5707964f, 191)]
    [InlineData(float.NaN, 0)]
    public void HeadingUsesNativeByteToFullTurnScale(float radians, byte expected) =>
        Assert.Equal(expected, SpectatorRosterPacket.HeadingFromRadians(radians));

    [Theory]
    [InlineData(-40000f, -32768)]
    [InlineData(40000f, 32767)]
    [InlineData(-100.1f, -100)]
    [InlineData(float.NaN, 0)]
    public void CoordinatesCannotOverflowTheSignedNativeMapPosition(float value, short expected) =>
        Assert.Equal(expected, SpectatorRosterPacket.Coordinate(value));
}

public sealed partial class HostedGameGatewayTests
{
    private sealed record NativeRosterRow(ulong Guid, string Name, byte Health, byte Heading, short X, short Y, short Z);

    private static bool IsNativeRoster(byte[] packet) => packet.Length >= 16 && packet[1] == 0xe2
        && packet[2] == 2 && packet[3] == 0;

    private static NativeRosterRow[] NativeRoster(byte[] packet)
    {
        var reader = new PacketReader(packet.AsSpan(1));
        Assert.Equal(0xe2, reader.ReadByte()); Assert.Equal(2, reader.ReadUInt16());
        Assert.Equal(0u, reader.ReadUInt32()); // replaces BaseClient.Spectator
        uint count = reader.ReadUInt32();
        var rows = new List<NativeRosterRow>();
        for (uint i = 0; i < count; i++)
        {
            ulong guid = reader.ReadUInt64(); string name = reader.ReadString();
            Assert.Equal(string.Empty, reader.ReadString());
            byte health = reader.ReadByte(); Assert.InRange(health, (byte)0, (byte)100);
            Assert.Equal(0, reader.ReadByte()); Assert.Equal(0u, reader.ReadUInt32()); Assert.Equal(0, reader.ReadUInt16());
            byte heading = reader.ReadByte(); Assert.Equal(0, reader.ReadByte()); // combat flags deliberately unset
            short x = unchecked((short)reader.ReadUInt16()); short y = unchecked((short)reader.ReadUInt16()); short z = unchecked((short)reader.ReadUInt16());
            Assert.Equal(0, reader.ReadByte()); Assert.Equal(0, reader.ReadByte()); Assert.Equal(0u, reader.ReadUInt32());
            rows.Add(new(guid, name, health, heading, x, y, z));
        }
        Assert.Equal(0u, reader.ReadUInt32()); Assert.True(reader.AtEnd);
        return rows.ToArray();
    }

    [Fact]
    public void ObserverEnablePublishesOnlyTheNativeRosterOfItsAuthorizedCurrentRound()
    {
        using var f = new Fixture();
        var game = f.Host("host"); var otherGame = f.Host("outsider");
        var host = f.Player("host"); var guest = f.Player("雪 Friend");
        var wrongRound = f.Player("wrong-round"); var outsider = f.Player("outsider");
        f.Admit(game, guest); f.Admit(game, wrongRound);
        f.Transfer(host, game.WorldId); f.Transfer(guest, game.WorldId);
        f.Transfer(wrongRound, game.WorldId); f.Transfer(outsider, otherGame.WorldId);
        Eliminated(host, Vector3.One);
        Loaded(guest, new Vector3(100, 20, -100), "InMatch");
        Loaded(wrongRound, Vector3.One, "InMatch"); Loaded(outsider, Vector3.One, "InMatch");
        Set(wrongRound.Tag!, "BountyAdmission", Admission(wrongRound) with { MatchId = Admission(wrongRound).MatchId + 1 });
        Set(guest.Tag!, "Hitpoints", 7500u);
        using var yaw = new PacketWriter();
        yaw.WriteUInt16((ushort)MovementFieldMask.Orientation); yaw.WriteUInt32(1); yaw.WriteByte(0); yaw.WriteSingle(MathF.PI);
        Get<SessionMovementState>(guest.Tag!, "Movement").ApplyPlayer(ClientMovementUpdate.Parse(yaw.Written));
        f.Sent.Clear();
        Assert.True(f.HostCommand(host, $"fly {game.WorldId} on").Ok);
        var snapshot = Assert.Single(f.Sent, item => IsNativeRoster(item.Packet));
        Assert.Equal(host, snapshot.Connection);
        var rows = NativeRoster(snapshot.Packet);
        Assert.Equal(2, rows.Length);
        Assert.Equal((byte)0, Assert.Single(rows, row => row.Guid == Get<ulong>(host.Tag!, "Guid")).Health);
        Assert.Equal(new NativeRosterRow(Get<ulong>(guest.Tag!, "Guid"), "雪 Friend", 75, 128, 100, 20, -100),
            Assert.Single(rows, row => row.Guid == Get<ulong>(guest.Tag!, "Guid")));
        Assert.DoesNotContain(f.Sent, item => item.Packet.Length > 3 && item.Packet[1] == 0xe2 && item.Packet[2] is 6 or 7);
        f.Sent.Clear();
        Call(f.Service, "SendHostedSpectatorRoster", guest, guest.Tag, true);
        Assert.DoesNotContain(f.Sent, item => IsNativeRoster(item.Packet));
    }

    [Fact]
    public void RosterRefreshIsThrottledAndReplacesDepartedPlayersWithoutMovingCorpses()
    {
        using var f = new Fixture();
        var game = f.Host("host"); var host = f.Player("host"); var guest = f.Player("guest");
        f.Admit(game, guest); f.Transfer(host, game.WorldId); f.Transfer(guest, game.WorldId);
        Eliminated(host, new Vector3(20, 30, 40)); Loaded(guest, new Vector3(100, 200, 300), "InMatch");
        Assert.True(f.HostCommand(host, $"fly {game.WorldId} on").Ok);
        Set(host.Tag!, "HostedCameraPosition", new Vector3(4000, 800, 4000));
        f.Sent.Clear();
        Set(host.Tag!, "NextHostedSpectatorRosterMs", Environment.TickCount64 + 60_000);
        Call(f.Service, "SendHostedSpectatorRoster", host, host.Tag, false);
        Assert.DoesNotContain(f.Sent, item => IsNativeRoster(item.Packet));
        Set(guest.Tag!, "Hitpoints", 1u); Set(host.Tag!, "NextHostedSpectatorRosterMs", 0L);
        Call(f.Service, "SendHostedSpectatorRoster", host, host.Tag, false);
        var rows = NativeRoster(Assert.Single(f.Sent, item => IsNativeRoster(item.Packet)).Packet);
        Assert.Equal((byte)1, Assert.Single(rows, row => row.Guid == Get<ulong>(guest.Tag!, "Guid")).Health);
        Assert.Equal((short)20, Assert.Single(rows, row => row.Guid == Get<ulong>(host.Tag!, "Guid")).X);
        Call(f.Service, "AbandonMatch", guest, guest.Tag, "leave");
        f.Sent.Clear();
        Call(f.Service, "SendHostedSpectatorRoster", host, host.Tag, true);
        rows = NativeRoster(Assert.Single(f.Sent, item => IsNativeRoster(item.Packet)).Packet);
        Assert.Single(rows); Assert.Equal(Get<ulong>(host.Tag!, "Guid"), rows[0].Guid);
        f.Sent.Clear();
        Call(f.Service, "AbandonMatch", host, host.Tag, "return to menu");
        Assert.Empty(NativeRoster(Assert.Single(f.Sent, item => IsNativeRoster(item.Packet)).Packet));
    }

    [Fact]
    public void RevokingAnObserverClearsTheNativeRosterBeforeClosingItsConnection()
    {
        using var f = new Fixture();
        var game = f.Host("host"); var observer = f.Player("moderator");
        f.Admit(game, observer);
        var grant = f.Store.IssuePlayerKey("host", false, game.WorldId, moderator: true);
        Assert.True(f.Store.Redeem("moderator", grant.Secret!).Success);
        f.Transfer(observer, game.WorldId); Eliminated(observer, Vector3.One);
        Assert.True(f.HostCommand(observer, $"fly {game.WorldId} on").Ok);
        f.Sent.Clear();
        Assert.True(f.Store.RevokeKey("host", false, grant.Key!.Id).Success);
        Assert.False(f.Validate(observer));
        Assert.Empty(NativeRoster(Assert.Single(f.Sent, item => IsNativeRoster(item.Packet)).Packet));
        Assert.Equal(ConnectionState.Closed, observer.State);
        Assert.False(Get<bool>(observer.Tag!, "HostedSpectatorRosterSent"));
    }

    [Fact]
    public void NativeRosterTracksMountedPositionAndHeadingInsteadOfTheStaleOnFootPose()
    {
        using var f = new Fixture();
        var game = f.Host("host"); var host = f.Player("host"); var driver = f.Player("driver");
        f.Admit(game, driver); f.Transfer(host, game.WorldId); f.Transfer(driver, game.WorldId);
        Eliminated(host, Vector3.One); Loaded(driver, new Vector3(10, 20, 30), "InMatch");
        var roster = VehicleRoster.LoadDefault(); var fleet = new VehicleFleet(roster);
        var car = new MatchVehicle(0x2000, 4242, roster.Require(1), new Vector3(100, 200, 300), MathF.PI,
            health: fleet.Options.MaxHealth, fuel: fleet.Options.MaxFuel);
        fleet.Add(car);
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(car.Guid, Get<ulong>(driver.Tag!, "Guid"), 0, 0, out _, out _));
        Set(driver.Tag!, "Fleet", fleet);
        f.Sent.Clear();
        Assert.True(f.HostCommand(host, $"fly {game.WorldId} on").Ok);
        var rows = NativeRoster(Assert.Single(f.Sent, item => IsNativeRoster(item.Packet)).Packet);
        Assert.Equal(new NativeRosterRow(Get<ulong>(driver.Tag!, "Guid"), "driver", 100, 128, 100, 200, 300),
            Assert.Single(rows, row => row.Guid == Get<ulong>(driver.Tag!, "Guid")));
    }
}
