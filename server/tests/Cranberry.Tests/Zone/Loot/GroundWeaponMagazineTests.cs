using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Tests.Zone.Combat;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Loot;

public sealed partial class InteractionFeedbackTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(30, 0, false)]
    [InlineData(12, 0, false)]
    [InlineData(12, 1, false)]
    [InlineData(12, 2, false)]
    [InlineData(12, 0, true)]
    [InlineData(12, 1, true)]
    [InlineData(12, 2, true)]
    public void DroppedWeaponKeepsLoadedRoundsThroughFQuickLootAndDrag(int magazine, int pickupPath, bool reloading)
    {
        using var world = new Fixture(inventoryOptions: new() { WieldFirstWeapon = true });
        world.MoveTo(Vector3.Zero);
        var source = world.Loot.Spawn(2425, 1, Vector3.Zero, skinRewardItemId: 2601);
        world.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(source.WorldGuid)]);
        var gun = Assert.Single(world.Inventory.Items.Values, item => item.DefinitionId == 2425);
        world.Combat.Shooter.DeclareWeapon(gun.Guid, 2425, magazine);
        Assert.True(world.Inventory.TryStow(world.Inventory.CreateInstance(1429, 6)));
        var ammo = new PlayerAmmoContext(world.Inventory, 0x1001, AmmoOptions.Default);
        PendingWeaponReload? pending = null;
        if (reloading)
        {
            var results = new List<WeaponArmResult>();
            WeaponFireArm.Handle(world.Combat, ShootingPacketBuilder.ReloadRequest(gun.Guid),
                CombatOptions.Default, 2425, gun.Guid, Vector3.Zero, 100, results, ammo);
            pending = Assert.IsType<PendingWeaponReload>(Assert.Single(results).ReloadWork);
        }

        world.Send(GroundWeaponUse(4, gun.Guid, 0x1001));
        Assert.DoesNotContain(gun.Guid, world.Inventory.Items.Keys);
        Assert.Equal(-1, world.Combat.Shooter.AmmoOf(gun.Guid));
        var dropped = Assert.Single(world.Loot.Items, item => item.ItemDefinitionId == 2425);
        world.Sent.Clear();
        byte[] pickup = pickupPath switch
        {
            1 => GroundWeaponUse(59, dropped.WorldGuid, dropped.WorldGuid),
            2 => GroundWeaponMove(dropped.WorldGuid),
            _ => [0x09, 0x07, 0, .. BitConverter.GetBytes(dropped.WorldGuid)],
        };
        world.Send(pickup);
        world.Send(pickup);

        var received = Assert.Single(world.Inventory.Items.Values, item => item.DefinitionId == 2425);
        Assert.NotEqual(gun.Guid, received.Guid);
        Assert.Equal(2601u, received.DisplayDefinitionId);
        Assert.Equal(magazine, world.Weapons.MagazineSource!(received.Guid, received.DefinitionId));
        Assert.Equal(6, ammo.Count(1429));
        Assert.False(world.Loot.TryGet(dropped.WorldGuid, out _));
        Assert.Single(world.Sent, p => Is(p, 0x0f, 0x43));
        byte[] grant = Assert.Single(world.Sent,
            p => Is(p, 0x11, 0x02) && BitConverter.ToUInt64(p, 23) == received.Guid);
        Assert.Equal((uint)magazine, BitConverter.ToUInt32(grant, 15 + InventoryItem.BaseLength + 5));
        Assert.Null(world.Combat.Reload);
        if (pending is not null)
            Assert.Null(WeaponFireArm.AdvanceReload(world.Combat, pending, pending.DueAtMs, received.Guid, world.Inventory));
        Assert.Equal(magazine + 6,
            world.Weapons.MagazineSource(received.Guid, received.DefinitionId) + ammo.Count(1429));
    }

    private static byte[] GroundWeaponUse(uint option, ulong item, ulong source)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(ItemUseOpcodes.ItemsBase); writer.WriteByte(ItemUseOpcodes.RequestUseItemSub);
        writer.WriteUInt32(1); writer.WriteUInt32(0);
        writer.WriteUInt32(option); writer.WriteUInt64(0x1001); writer.WriteUInt64(source);
        writer.WriteUInt64(0x1001); writer.WriteUInt64(item); writer.WriteByte(1);
        return writer.Written.ToArray();
    }

    private static byte[] GroundWeaponMove(ulong item)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(0xc8); writer.WriteUInt16(1); writer.WriteUInt64(0);
        writer.WriteUInt64(item); writer.WriteUInt64(item); writer.WriteUInt64(0x1001);
        writer.WriteUInt32(1); writer.WriteInt32(-1);
        return writer.Written.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    public void LegacyPickupPreservesExplicitMagazineEvenWhenFreshGunsDefaultFull(int magazine)
    {
        var options = CombatOptions.Default with { Ammo = AmmoOptions.Default with { GunsSpawnEmpty = false } };
        using var world = new Fixture(containers: false, combatOptions: options);
        var ground = world.Loot.Spawn(2425, 1, Vector3.Zero, magazineRounds: magazine);
        byte[] pickup = [0x09, 0x07, 0, .. BitConverter.GetBytes(ground.WorldGuid)];
        world.Send(pickup);
        world.Send(pickup);
        byte[] grant = Assert.Single(world.Sent, p => Is(p, 0x11, 2));
        Assert.Equal((uint)magazine, BitConverter.ToUInt32(grant, 15 + InventoryItem.BaseLength + 5));
        Assert.Equal(magazine, world.Combat.Shooter.AmmoOf(BitConverter.ToUInt64(grant, 23)));
        Assert.Single(world.Sent, p => Is(p, 0x0f, 0x43));
        Assert.DoesNotContain(world.Sent, IsReload);
        Assert.Null(world.Combat.Reload);
    }

    [Fact]
    public void RefusedLoadedPickupRetainsItsMagazineUntilThereIsRoom()
    {
        using var world = new Fixture();
        for (int slot = 0; slot < 3; slot++) world.Inventory.TryPickUp(2425, 1, out _);
        Assert.True(world.Inventory.TryStow(world.Inventory.CreateInstance(1429, 50)));
        var ground = world.Loot.Spawn(2425, 1, Vector3.Zero, magazineRounds: 17);
        byte[] pickup = [0x09, 0x07, 0, .. BitConverter.GetBytes(ground.WorldGuid)];
        world.Send(pickup);
        Assert.True(world.Loot.TryGet(ground.WorldGuid, out var retained));
        Assert.Equal(17, retained.MagazineRounds);
        Assert.DoesNotContain(world.Sent, p => Is(p, 0x0f, 0x43));
        var freed = world.Inventory.LoadoutSlots[SurvivorLoadout.Wheel3];
        world.Inventory.RemoveUnits(freed.Guid, 1);
        world.Sent.Clear();
        world.Send(pickup);
        var received = world.Inventory.LoadoutSlots[SurvivorLoadout.Wheel3];
        Assert.Equal(17, world.Combat.Shooter.AmmoOf(received.Guid));
        Assert.Single(world.Sent, p => Is(p, 0x0f, 0x43));
        Assert.Equal(50, new PlayerAmmoContext(world.Inventory, 0x1001, AmmoOptions.Default).Count(1429));
    }
}

public sealed partial class BodyBagGatewayTests
{
    [Fact]
    public void LoadedGroundDropSurvivesSharedRestreamAndHasOnlyOneRecipient()
    {
        using var world = new World();
        var donor = world.AddPlayer(shared: true);
        var recipient = world.AddPlayer(shared: true);
        donor.Inventory.TryPickUp(2425, 1, out var original);
        Assert.NotNull(original);
        Assert.True(donor.Inventory.TrySetItemSkin(original.Guid, 2601));
        Get<SessionCombat>(donor.State, "Combat").Shooter.DeclareWeapon(original.Guid, 2425, 13);
        donor.Send(Use(donor, original.Guid, 4));
        var donorView = Assert.Single(donor.Loot.Items, item => item.ItemDefinitionId == 2425);
        Assert.Equal(13, donorView.MagazineRounds);
        var burst = new List<Action>();
        world.Call("PlanSharedDrops", recipient.Connection, recipient.State, burst);
        Assert.Single(burst)();
        var oldView = Assert.Single(recipient.Loot.Items, item => item.ItemDefinitionId == 2425);
        Assert.Equal(13, oldView.MagazineRounds);
        Get<MatchLoot>(recipient.State, "StreamedLoot").NoteEvicted(oldView.WorldGuid);
        world.Call("EvictGroundLoot", recipient.Connection, recipient.State, oldView.WorldGuid);
        burst.Clear();
        recipient.Sent.Clear();
        world.Call("PlanSharedDrops", recipient.Connection, recipient.State, burst);
        Assert.Single(burst)();
        var visible = Assert.Single(recipient.Loot.Items, item => item.ItemDefinitionId == 2425);
        Assert.NotEqual(oldView.WorldGuid, visible.WorldGuid);
        Assert.Equal(13, visible.MagazineRounds);
        byte[] preview = Assert.Single(recipient.Sent, p => Is(p, 0x11, 2));
        Assert.Equal(13u, BitConverter.ToUInt32(preview, 15 + InventoryItem.BaseLength + 5));
        recipient.Sent.Clear();
        byte[] pickup = [0x09, 0x07, 0, .. BitConverter.GetBytes(visible.WorldGuid)];
        recipient.Send(pickup);
        recipient.Send(pickup);
        donor.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(donorView.WorldGuid)]);
        var received = Assert.Single(recipient.Inventory.Items.Values, item => item.DefinitionId == 2425);
        var combat = Get<SessionCombat>(recipient.State, "Combat");
        Assert.Equal(13, combat.Shooter.AmmoOf(received.Guid));
        Assert.Equal(2601u, received.DisplayDefinitionId);
        Assert.DoesNotContain(donor.Inventory.Items.Values, item => item.DefinitionId == 2425);
        Assert.Empty(donor.Loot.Items);
        Assert.Empty(recipient.Loot.Items);
        Assert.Single(recipient.Sent, p => Is(p, 0x0f, 0x43));

        recipient.Inventory.BindLoadout(received, received.LoadoutSlotId, BodySlots.RightHand);
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(combat, ShootingPacketBuilder.Fire(received.Guid, 0, 0, 0, [1]),
            CombatOptions.Default, 2425, received.Guid, Vector3.Zero, Environment.TickCount64,
            results, new PlayerAmmoContext(recipient.Inventory, recipient.Guid, AmmoOptions.Default));
        Assert.True(Assert.Single(results).LaunchRelay);
        var restore = typeof(ZoneService).GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Single(method => method.Name == "RestoreGroundWeaponMagazine" && method.GetParameters().Length == 3);
        restore.Invoke(null, [recipient.State, visible, received]);
        Assert.Equal(12, combat.Shooter.AmmoOf(received.Guid)); // duplicate publication cannot restore the spent round
    }
}
