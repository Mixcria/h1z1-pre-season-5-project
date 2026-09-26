using System.Numerics;
using Cranberry.Tests.Zone.Combat;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Loot;

public sealed partial class InteractionFeedbackTests
{
    [Theory]
    [InlineData(1373u, "cargo", "interact")]
    [InlineData(1373u, "cargo", "quick-loot")]
    [InlineData(1373u, "cargo", "drag")]
    [InlineData(1373u, "ammo", "interact")]
    [InlineData(1373u, "apparel", "interact")]
    [InlineData(1373u, "spare", "interact")]
    [InlineData(2425u, "cargo", "interact")]
    [InlineData(2425u, "cargo", "quick-loot")]
    [InlineData(2425u, "cargo", "drag")]
    [InlineData(2425u, "ammo", "interact")]
    [InlineData(2425u, "apparel", "interact")]
    [InlineData(2425u, "spare", "interact")]
    [InlineData(1374u, "cargo", "interact")]
    [InlineData(1374u, "cargo", "quick-loot")]
    [InlineData(1374u, "cargo", "drag")]
    [InlineData(1374u, "ammo", "interact")]
    [InlineData(1374u, "apparel", "interact")]
    [InlineData(1374u, "spare", "interact")]
    public void LootReload_PickupWithoutClientInterruptPreservesHeldReload(uint weapon, string kind, string route)
    {
        using var world = new Fixture(inventoryOptions: new()
        {
            StarterOutfit = [], BaseCarryBulk = 10000, WieldFirstWeapon = true
        });
        world.MoveTo(Vector3.Zero);
        var gun = world.Inventory.CreateInstance(weapon, 1);
        world.Inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        world.Combat.Shooter.DeclareWeapon(gun.Guid, weapon, 2);
        uint ammunition = AmmoTypes.AmmoItemFor(weapon);
        Assert.NotEqual(0u, ammunition);
        Assert.True(world.Inventory.TryStow(world.Inventory.CreateInstance(ammunition, 6)));
        var ammo = new PlayerAmmoContext(world.Inventory, 0x1001, AmmoOptions.Default);
        var pending = LootReloadStart(world, gun, ammo);
        long deadline = pending.DueAtMs;
        ulong counter = world.Combat.Shooter.ReloadCountOf(gun.Guid);
        uint pickupDefinition = kind switch
        {
            "ammo" => ammunition,
            "apparel" => 2170,
            "spare" => weapon == 1374 ? 1373u : 1374u,
            _ => 134
        };
        uint count = kind == "ammo" ? 3u : 1u;
        var ground = world.Loot.Spawn(pickupDefinition, 1, Vector3.Zero, count);
        uint beforeUnits = world.Inventory.Items.Values.Where(i => i.DefinitionId == pickupDefinition)
            .Aggregate(0u, (sum, i) => sum + i.Count);
        world.Sent.Clear();

        // Synthetic gateway requests isolate server publication. An actual native F attempt
        // may send 82/09 first; that distinct sequence is covered below, not omitted as a fix.
        byte[] pickup = route switch
        {
            "quick-loot" => GroundWeaponUse(59, ground.WorldGuid, ground.WorldGuid),
            "drag" => GroundWeaponMove(ground.WorldGuid),
            _ => [0x09, 0x15, 0, .. BitConverter.GetBytes(0x1001ul), .. BitConverter.GetBytes(ground.WorldGuid)]
        };
        world.Send(pickup);
        world.Send(pickup); // Duplicate transport/application interaction must not re-grant.
        world.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(ground.WorldGuid)]);
        world.Send([0x09, 0x08, 0]);

        Assert.False(world.Loot.TryGet(ground.WorldGuid, out _));
        Assert.Equal(beforeUnits + count, world.Inventory.Items.Values.Where(i => i.DefinitionId == pickupDefinition)
            .Aggregate(0u, (sum, i) => sum + i.Count));
        Assert.Single(world.Sent, p => Is(p, 0x0f, 0x43));
        Assert.Same(pending, world.Combat.Reload);
        Assert.Equal(deadline, pending.DueAtMs);
        Assert.Equal(gun.Guid, world.Inventory.WieldedItemGuid);
        Assert.Equal(counter, world.Combat.Shooter.ReloadCountOf(gun.Guid));
        Assert.Equal(2, world.Combat.Shooter.AmmoOf(gun.Guid));
        int reserve = 6 + (kind == "ammo" ? 3 : 0);
        Assert.Equal(reserve, ammo.Count(ammunition));
        Assert.DoesNotContain(world.Sent, p => LootReloadWeaponReply(p, gun.Guid));
        Assert.DoesNotContain(world.Sent, p => p.Length >= 31 && Is(p, 0x11, 2)
            && BitConverter.ToUInt64(p, 23) == gun.Guid);
        Assert.DoesNotContain(world.Sent, p => p.Length >= 19 && Is(p, 0x11, 4)
            && BitConverter.ToUInt64(p, 11) == gun.Guid);

        int steps = 0;
        while (ReferenceEquals(world.Combat.Reload, pending))
        {
            Assert.True(++steps <= 64);
            Assert.NotNull(WeaponFireArm.AdvanceReload(world.Combat, pending, pending.DueAtMs,
                gun.Guid, world.Inventory));
        }
        int loaded = Math.Min(RetailBalance.ClipSize(weapon) - 2, reserve);
        Assert.Equal(2 + loaded, world.Combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(reserve - loaded, ammo.Count(ammunition));
    }

    [Theory]
    [InlineData(1373u)]
    [InlineData(2425u)]
    [InlineData(1374u)]
    public void LootReload_ExplicitNativeInterruptBeforePickupStopsOnlyUncompletedWork(uint weapon)
    {
        using var world = new Fixture(inventoryOptions: new() { StarterOutfit = [], BaseCarryBulk = 10000 });
        world.MoveTo(Vector3.Zero);
        var gun = world.Inventory.CreateInstance(weapon, 1);
        world.Inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        world.Combat.Shooter.DeclareWeapon(gun.Guid, weapon, 2);
        uint ammunition = AmmoTypes.AmmoItemFor(weapon);
        Assert.True(world.Inventory.TryStow(world.Inventory.CreateInstance(ammunition, 6)));
        var ammo = new PlayerAmmoContext(world.Inventory, 0x1001, AmmoOptions.Default);
        var pending = LootReloadStart(world, gun, ammo);
        ulong counter = world.Combat.Shooter.ReloadCountOf(gun.Guid);
        var ground = world.Loot.Spawn(134, 1, Vector3.Zero);
        world.Sent.Clear();

        world.Send([.. ShootingPacketBuilder.Header(0x09), .. BitConverter.GetBytes(gun.Guid)]);
        Assert.Null(world.Combat.Reload); // The interrupt arrived before any pickup decision.
        world.Send([0x09, 0x15, 0, .. BitConverter.GetBytes(0x1001ul), .. BitConverter.GetBytes(ground.WorldGuid)]);

        Assert.False(world.Loot.TryGet(ground.WorldGuid, out _));
        Assert.Single(world.Inventory.Items.Values, i => i.DefinitionId == 134);
        Assert.Equal(counter + 1, world.Combat.Shooter.ReloadCountOf(gun.Guid));
        Assert.Equal(2, world.Combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(6, ammo.Count(ammunition));
        Assert.Null(WeaponFireArm.AdvanceReload(world.Combat, pending, pending.DueAtMs,
            gun.Guid, world.Inventory));
        Assert.Equal(6, ammo.Count(ammunition));
        Assert.Single(world.Sent, p => LootReloadWeaponReply(p, gun.Guid));
    }

    private static PendingWeaponReload LootReloadStart(Fixture world, InventoryItemInstance gun, PlayerAmmoContext ammo)
    {
        // Use the production reload arm with an explicit clock; no background dispatcher or sleeps.
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(world.Combat, ShootingPacketBuilder.ReloadRequest(gun.Guid),
            CombatOptions.Default, gun.DefinitionId, gun.Guid, Vector3.Zero,
            Environment.TickCount64 + 10000, results, ammo);
        return Assert.IsType<PendingWeaponReload>(Assert.Single(results).ReloadWork);
    }

    private static bool LootReloadWeaponReply(byte[] packet, ulong guid) => packet.Length >= 14
        && packet[0] == 0x82 && packet[5] is 8 or WeaponReplyPackets.SubReloadRejected
        && BitConverter.ToUInt64(packet, 6) == guid;
}
