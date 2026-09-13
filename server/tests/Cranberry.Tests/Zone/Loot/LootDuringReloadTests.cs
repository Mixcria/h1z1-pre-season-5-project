using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Tests.Zone.Combat;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Loot;

public sealed partial class InteractionFeedbackTests
{
    public static IEnumerable<object[]> RifleLootCases()
    {
        foreach (uint item in new uint[] { 1429, 2046, 2124, 2229 })
        foreach (int magazine in new[] { 0, 5 })
        foreach (bool inventoryUi in new[] { false, true })
            yield return [item, magazine, inventoryUi];
    }

    [Theory]
    [MemberData(nameof(RifleLootCases))]
    public void RifleReloadSurvivesGroundAndInventoryPickups(uint itemId, int magazine, bool inventoryUi)
    {
        var options = CombatOptions.Default with { MagazineResync = false, ShippedRefireGate = true,
            ShippedReloadTime = true, RefireJitterMs = 16 };
        using var world = new Fixture(proximity: true,
            inventoryOptions: new() { WieldFirstWeapon = true }, combatOptions: options);
        var gun = world.Inventory.CreateInstance(2425, 1);
        world.Inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        Assert.True(world.Inventory.TryStow(world.Inventory.CreateInstance(1429, 40)));
        world.Combat.Shooter.DeclareWeapon(gun.Guid, 2425, magazine);
        var ammo = new PlayerAmmoContext(world.Inventory, 0x1001, options.Ammo);
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(world.Combat, ShootingPacketBuilder.ReloadRequest(gun.Guid),
            options, 2425, gun.Guid, Vector3.Zero, 100, results, ammo);
        var pending = Assert.IsType<PendingWeaponReload>(Assert.Single(results).ReloadWork);
        long deadline = pending.DueAtMs;
        ulong counter = world.Combat.Shooter.ReloadCountOf(gun.Guid);
        var loot = world.Loot.Spawn(itemId, 1, Vector3.Zero);

        if (inventoryUi)
        {
            using var move = new PacketWriter();
            move.WriteByte(0xc8); move.WriteUInt16(1); move.WriteUInt64(0);
            move.WriteUInt64(loot.WorldGuid); move.WriteUInt64(loot.WorldGuid); move.WriteUInt64(0x1001);
            move.WriteUInt32(1); move.WriteInt32(-1);
            world.Send(move.Written.ToArray());
            world.Send(move.Written.ToArray()); // duplicate full transfer
        }
        else
            world.Send([0x09, 0x15, 0, .. BitConverter.GetBytes(0x1001ul), .. BitConverter.GetBytes(loot.WorldGuid)]);
        world.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(loot.WorldGuid)]);
        world.Send([0x09, 0x08, 0]);

        Assert.False(world.Loot.TryGet(loot.WorldGuid, out _));
        Assert.Single(world.Sent, p => Is(p, 0x0f, 0x43));
        Assert.Same(gun, world.Inventory.EquipmentSlots[BodySlots.RightHand]);
        Assert.Same(pending, world.Combat.Reload);
        Assert.Equal(deadline, pending.DueAtMs);
        Assert.Equal(counter, world.Combat.Shooter.ReloadCountOf(gun.Guid));
        Assert.Equal(magazine, world.Combat.Shooter.AmmoOf(gun.Guid));
        Assert.DoesNotContain(world.Sent, IsReload);
        Assert.NotNull(WeaponFireArm.AdvanceReload(world.Combat, pending, deadline, gun.Guid, world.Inventory));
        Assert.Equal(30, world.Combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(magazine + 40 + (itemId == 1429 ? 1 : 0) - 30, ammo.Count(1429));
        Assert.Null(world.Combat.Reload);
        Assert.Null(WeaponFireArm.AdvanceReload(world.Combat, pending, deadline, gun.Guid, world.Inventory));
    }

    [Fact]
    public void RefusedPickupCannotCancelOrRestartAnEmptyRifleReload()
    {
        using var world = new Fixture();
        var gun = world.Inventory.CreateInstance(2425, 1);
        world.Inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        Assert.True(world.Inventory.TryStow(world.Inventory.CreateInstance(1429, 6)));
        var ammo = new PlayerAmmoContext(world.Inventory, 0x1001, AmmoOptions.Default);
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(world.Combat, ShootingPacketBuilder.ReloadRequest(gun.Guid),
            CombatOptions.Default, 2425, gun.Guid, Vector3.Zero, 100, results, ammo);
        var pending = Assert.IsType<PendingWeaponReload>(Assert.Single(results).ReloadWork);
        long deadline = pending.DueAtMs;
        var loot = world.Loot.Spawn(1429, 1, Vector3.Zero, count: 10000);
        world.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(loot.WorldGuid)]);
        Assert.True(world.Loot.TryGet(loot.WorldGuid, out _));
        Assert.DoesNotContain(world.Sent, p => Is(p, 0x0f, 0x43) || IsReload(p));
        Assert.Same(pending, world.Combat.Reload);
        Assert.Equal(deadline, pending.DueAtMs);
        Assert.NotNull(WeaponFireArm.AdvanceReload(world.Combat, pending, deadline, gun.Guid, world.Inventory));
        Assert.Equal(6, world.Combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(0, ammo.Count(1429));
    }

    [Theory]
    [InlineData(1511u, 0)] // merge newly looted shells into the active reserve
    [InlineData(1511u, 2)]
    [InlineData(2046u, 0)] // equip a hat without changing the held weapon
    [InlineData(2046u, 2)]
    [InlineData(2124u, 0)] // equip a container while the ammo context remains live
    [InlineData(2124u, 2)]
    public void GroundPickupKeepsTheReloadAndItsOriginalDeadline(uint itemId, int completedShells)
    {
        using var world = new Fixture();
        var (gun, ammo, pending) = StartLootReload(world, completedShells);
        long due = pending.DueAtMs;
        var loot = world.Loot.Spawn(itemId, 1, Vector3.Zero);

        world.Send([0x09, 0x15, 0, .. BitConverter.GetBytes(0x1001ul), .. BitConverter.GetBytes(loot.WorldGuid)]);
        world.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(loot.WorldGuid)]);
        world.Send([0x09, 0x08, 0]);

        Assert.False(world.Loot.TryGet(loot.WorldGuid, out _));
        Assert.Same(gun, world.Inventory.EquipmentSlots[BodySlots.RightHand]);
        Assert.Same(pending, world.Combat.Reload);
        Assert.Equal(due, pending.DueAtMs);
        Assert.Equal(completedShells, world.Combat.Shooter.AmmoOf(gun.Guid));
        Assert.DoesNotContain(world.Sent, IsReload);
        Assert.NotNull(WeaponFireArm.AdvanceReload(world.Combat, pending, due, gun.Guid, world.Inventory));
        Assert.Equal(completedShells + 1, world.Combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(6 + (itemId == 1511 ? 1 : 0) - completedShells - 1, ammo.Count(1511));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void AcceptedVehicleEntryStopsReloadWithoutSpendingPendingShells(int completedShells)
    {
        using var world = new Fixture();
        var (gun, ammo, pending) = StartLootReload(world, completedShells);

        world.StopReloadForVehicleEntry();

        Assert.Null(world.Combat.Reload);
        Assert.Equal(completedShells, world.Combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(6 - completedShells, ammo.Count(1511));
        Assert.Single(world.Sent, IsReload);
        Assert.Single(world.Sent, p => p.Length >= 6 && p[0] == 0x82 && p[5] == 0x0b);
        Assert.Null(WeaponFireArm.AdvanceReload(world.Combat, pending, pending.DueAtMs, gun.Guid, world.Inventory));
        int packets = world.Sent.Count;
        world.StopReloadForVehicleEntry();
        Assert.Equal(packets, world.Sent.Count);
    }

    private static (InventoryItemInstance Gun, PlayerAmmoContext Ammo, PendingWeaponReload Pending)
        StartLootReload(Fixture world, int completedShells)
    {
        var gun = world.Inventory.CreateInstance(1374, 1);
        world.Inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        world.Inventory.TryStow(world.Inventory.CreateInstance(1511, 6));
        var ammo = new PlayerAmmoContext(world.Inventory, 0x1001, AmmoOptions.Default);
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(world.Combat, ShootingPacketBuilder.ReloadRequest(gun.Guid),
            CombatOptions.Default, 1374, gun.Guid, Vector3.Zero, 0, results, ammo);
        var pending = Assert.IsType<PendingWeaponReload>(Assert.Single(results).ReloadWork);
        for (int shell = 1; shell <= completedShells; shell++)
            Assert.NotNull(WeaponFireArm.AdvanceReload(world.Combat, pending, pending.DueAtMs, gun.Guid, world.Inventory));
        return (gun, ammo, pending);
    }
}
