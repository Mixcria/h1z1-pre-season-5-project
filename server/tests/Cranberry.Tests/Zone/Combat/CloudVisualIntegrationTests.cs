using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    [Fact]
    public void ReplayedSmokeFireAfterCloudActivationConsumesOneUnitAndCreatesOneCloud()
    {
        using var f = new Fixture();
        f.Service.Post = _ => { }; // No asynchronous fallback races; reports drive this case.
        var owner = f.Add(1, Vector3.Zero);
        var peer = f.Add(2, new(5, 0, 0));
        var inventory = new PlayerInventory(1, Get<LootWorld>(owner.Tag!, "Loot").NextItemGuid,
            new InventoryOptions { StarterOutfit = [] });
        inventory.Bootstrap();
        var stack = inventory.CreateInstance(2236, 3);
        Set(owner.Tag!, "Inventory", inventory);
        Set(owner.Tag!, "HeldWeaponItemGuid", stack.Guid);
        var combat = Get<SessionCombat>(owner.Tag!, "Combat");
        void Handle(byte[] packet, long now)
        {
            WeaponFireArm.Handle(combat, packet, CombatOptions.Default, 2236, stack.Guid,
                Vector3.Zero, now, Get<List<WeaponArmResult>>(owner.Tag!, "WeaponArmResults"), null);
            Call(f.Service, "DrainCombatArm", owner, owner.Tag);
        }
        byte[] fire = ShootingPacketBuilder.Fire(stack.Guid, 0, 0, 0, [7], gameTime: 1000);
        Handle(fire, 1000);
        Assert.Equal(2u, stack.Count);
        Handle(ThrowableArmTests.GuidedExplode(7, 3, 0, 0), 2100);
        Handle(fire, 2200);
        Handle(ThrowableArmTests.GuidedExplode(7, 4, 0, 0), 2300);
        Assert.Equal(2u, stack.Count);
        Assert.Equal(1, combat.Grenades.Thrown);
        foreach (var viewer in new[] { owner, peer })
            Assert.Single(f.Recorder.Routed, p => ReferenceEquals(p.Connection, viewer)
                && p.Packet.Length >= 15 && p.Packet[1] == 0x0f && p.Packet[2] == 0x15);
        Assert.Equal(10000u, f.Health(owner));
        Assert.Equal(10000u, f.Health(peer));
    }

    [Theory]
    [InlineData(2236u, false)]
    [InlineData(2237u, false)]
    [InlineData(14u, false)]
    [InlineData(2236u, true)]
    public void LingeringCloudReachesNearbyAndLateViewersThenExpiresForEveryone(uint item, bool ownerLeaves)
    {
        using var f = new Fixture();
        var owner = f.Add(1, Vector3.Zero);
        var near = f.Add(2, new(60, 0, 0));
        var far = f.Add(3, new(500, 0, 0));
        var otherMatch = f.Add(4, new(60, 0, 0), matchId: 2);
        var inventory = new PlayerInventory(1, Get<LootWorld>(owner.Tag!, "Loot").NextItemGuid,
            new InventoryOptions { StarterOutfit = [] });
        inventory.Bootstrap();
        Set(owner.Tag!, "Inventory", inventory);
        Assert.True(AugustThrowables.TryGet(item, out var fact));
        var cloud = new Detonation(fact, new(50, 0, 0), true, false, 1);
        Call(f.Service, "StartCloud", owner, owner.Tag, cloud);

        bool TagFor(SoeConnection link, byte sub, (SoeConnection Connection, byte[] Packet) r) =>
            ReferenceEquals(link, r.Connection) && r.Packet.Length >= 15
            && r.Packet[1] == 0x0f && r.Packet[2] == sub
            && BitConverter.ToUInt32(r.Packet, 11) == fact.CloudEffectId;
        var first = Assert.Single(f.Recorder.Routed, r => TagFor(owner, 0x15, r));
        ulong guid = BitConverter.ToUInt64(first.Packet, 3);
        Assert.Single(f.Recorder.Routed, r => TagFor(near, 0x15, r));
        Assert.DoesNotContain(f.Recorder.Routed, r => TagFor(far, 0x15, r) || TagFor(otherMatch, 0x15, r));
        int generation = Get<int>(owner.Tag!, "WorldGeneration");
        void Tick(int remaining) => Call(f.Service, "CloudTick", owner, owner.Tag, cloud, guid,
            remaining, Rulings.Throwables.CloudTickMs, generation);

        var late = f.Add(5, new(60, 0, 0));
        f.Move(far, new(60, 0, 0));
        Tick(3);
        Assert.Single(f.Recorder.Routed, r => TagFor(far, 0x15, r));
        Assert.Single(f.Recorder.Routed, r => TagFor(late, 0x15, r));
        Assert.Single(f.Recorder.Routed, r => TagFor(owner, 0x15, r));
        Assert.Single(f.Recorder.Routed, r => TagFor(near, 0x15, r));
        f.Move(far, new(500, 0, 0));
        Tick(2);
        Assert.Single(f.Recorder.Routed, r => TagFor(far, 0x16, r));
        if (ownerLeaves) Call(f.Service, "LeaveSharedLoot", owner.Tag);
        else Tick(1);
        foreach (var viewer in new[] { owner, near, late })
            Assert.Single(f.Recorder.Routed, r => TagFor(viewer, 0x16, r));
        Assert.DoesNotContain(f.Recorder.Routed, r => TagFor(otherMatch, 0x15, r));
        int ended = f.Recorder.Routed.Count;
        Tick(1);
        Assert.Equal(ended, f.Recorder.Routed.Count);
    }

    [Fact]
    public void LeavingTheWorldClearsTheCharactersFireState()
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var owner = f.Add(1, Vector3.Zero);
        Call(f.Service, "TouchCharacterFire", owner, owner.Tag);
        Assert.True(Get<bool>(owner.Tag!, "CharacterFireActive"));
        Call(f.Service, "AbandonMatch", owner, owner.Tag, "burning lifecycle test");
        Assert.False(Get<bool>(owner.Tag!, "CharacterFireActive"));
        Assert.Equal(0, Get<long>(owner.Tag!, "CharacterFireUntilMs"));
    }

    [Fact]
    public void GrenadeDamageUsesTheOccupiedVehiclePositionInsteadOfTheRidersDummyPose()
    {
        using var f = new Fixture();
        var owner = f.Add(1, Vector3.Zero);
        var rider = f.Add(2, Vector3.Zero);
        var fleet = new VehicleFleet(VehicleRoster.LoadDefault());
        var car = new MatchVehicle(100, 101, fleet.Roster.Require(1), new(100, 0, 0), 0, 100000, 7500);
        fleet.Add(car);
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(100, 2, 0, 0, out _, out _));
        Set(rider.Tag!, "Fleet", fleet);
        Assert.True(AugustThrowables.TryGet(14, out var fact));
        var blast = new Detonation(fact, car.Position, true, false, 1);
        Call(f.Service, "ApplyBlast", owner, owner.Tag, blast, true);
        Assert.Equal(9000u, f.Health(rider));
        Assert.Equal(10000u, f.Health(owner));
    }
}
