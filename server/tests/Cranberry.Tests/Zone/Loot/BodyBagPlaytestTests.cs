using System.Collections.Concurrent;
using System.Numerics;
using Cranberry.Tests.Zone.Combat;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

public sealed partial class BodyBagGatewayTests
{
    [Fact]
    public void SwappingWeaponSlotsThroughTheGatewayPreservesTheCurrentReloadAndBothClientItems()
    {
        using var world = new World();
        var player = world.AddPlayer();
        player.Inventory.TryPickUp(1374, 1, out var shotgun);
        player.Inventory.TryPickUp(2425, 1, out var rifle);
        player.Inventory.TrySelectLoadoutSlot(1, out _);
        player.Inventory.TryStow(player.Inventory.CreateInstance(1511, 6));
        var combat = Get<SessionCombat>(player.State, "Combat");
        var ammo = new PlayerAmmoContext(player.Inventory, player.Guid, AmmoOptions.Default);
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(combat, ShootingPacketBuilder.ReloadRequest(shotgun!.Guid),
            CombatOptions.Default, 1374, shotgun.Guid, Vector3.Zero, 0, results, ammo);
        var pending = Assert.IsType<PendingWeaponReload>(Assert.Single(results).ReloadWork);
        player.Sent.Clear();
        player.Send(Move(player.Guid, rifle!.Guid, player.Guid, 1, PlayerInventory.EquippedContainerGuid, 1));
        Assert.Same(rifle, player.Inventory.LoadoutSlots[1]);
        Assert.Same(shotgun, player.Inventory.LoadoutSlots[2]);
        Assert.Same(pending, combat.Reload);
        Assert.Same(shotgun, player.Inventory.EquipmentSlots[BodySlots.RightHand]);
        Assert.DoesNotContain(player.Sent, p => Is(p, 0x11, 4));
        Assert.NotNull(WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs, shotgun.Guid, player.Inventory));
        Assert.Equal(1, combat.Shooter.AmmoOf(shotgun.Guid));
    }

    [Theory]
    [InlineData(2423u, SurvivorLoadout.QuickUse1, false)]
    [InlineData(2423u, SurvivorLoadout.QuickUse1, true)]
    [InlineData(2424u, SurvivorLoadout.QuickUse2, false)]
    [InlineData(2424u, SurvivorLoadout.QuickUse2, true)]
    public void BodyBagMedicalRowsCombineAndAutomaticBagDestinationUsesDedicatedSlot(
        uint definition, uint slot, bool empty)
    {
        using var world = new World();
        var player = world.AddPlayer();
        if (player.Inventory.LoadoutSlots.TryGetValue(slot, out var starter))
            player.Inventory.RemoveUnits(starter.Guid, 0);
        if (!empty) player.Inventory.TryPickUp(definition, 4, out _);
        var bag = world.SpawnBag(player, (definition, 3), (definition, 7), (1429, 4));
        var medical = Assert.Single(bag.Items.Values, i => i.DefinitionId == definition);
        Assert.Equal(10u, medical.Count);
        player.Send(Move(Assert.Single(player.Bags).Key, medical.ItemGuid, player.Guid,
            medical.Count, player.Inventory.BaseBag!.Guid, -1));
        Assert.Equal(empty ? 10u : 14u, player.Inventory.LoadoutSlots[slot].Count);
        Assert.Single(player.Inventory.Items.Values, i => i.DefinitionId == definition);
        Assert.DoesNotContain(player.Inventory.BaseBag.Slots.Values, i => i.DefinitionId == definition);
        Assert.DoesNotContain(bag.Items.Values, i => i.DefinitionId == definition);
        Assert.DoesNotContain(player.Sent, p => Is(p, 0xc8, 3));
    }

    [Theory]
    [InlineData(-2)] // F-key interaction
    [InlineData(-1)] // Automatic inventory-grid destination
    [InlineData(0)] // Inventory-row destination
    public void RepeatedGroundFirstAidPickupsUpdateTheSameDedicatedSlot(int destination)
    {
        using var world = new World();
        var player = world.AddPlayer();
        ulong kitGuid = 0;
        for (uint count = 1; count <= 2; count++)
        {
            var ground = player.Loot.Spawn(2424, 1, Vector3.Zero);
            player.Sent.Clear();
            if (destination == -2)
                player.Send([0x09, 0x15, 0, .. BitConverter.GetBytes(player.Guid), .. BitConverter.GetBytes(ground.WorldGuid)]);
            else
                player.Send(Move(ground.WorldGuid, ground.WorldGuid, player.Guid, 1,
                    player.Inventory.BaseBag!.Guid, destination));
            var kit = player.Inventory.LoadoutSlots[SurvivorLoadout.QuickUse2];
            Assert.Equal(count, kit.Count);
            if (count == 1) kitGuid = kit.Guid;
            Assert.Equal(kitGuid, kit.Guid);
            Assert.Single(player.Inventory.Items.Values, i => i.DefinitionId == 2424);
            Assert.DoesNotContain(player.Inventory.BaseBag!.Slots.Values, i => i.DefinitionId == 2424);
            Assert.False(player.Loot.TryGet(ground.WorldGuid, out _));
            Assert.DoesNotContain(player.Sent, p => Is(p, 0xc8, 3));
            if (count > 1)
            {
                Assert.Contains(player.Sent, p => p.SequenceEqual(ItemUpdate.ForStack(player.Guid, kit).ToBytes()));
                Assert.DoesNotContain(player.Sent, p => Is(p, 0x11, 4));
            }
        }
    }

    [Theory]
    [InlineData(false, 2172u, CraftingCatalog.ArmorScrap)]
    [InlineData(true, 2172u, CraftingCatalog.ArmorScrap)]
    [InlineData(false, 2209u, CraftingCatalog.CompositeFabric)]
    [InlineData(true, 2209u, CraftingCatalog.CompositeFabric)]
    [InlineData(false, 2215u, CraftingCatalog.CompositeFabric)]
    [InlineData(true, 2215u, CraftingCatalog.CompositeFabric)]
    public async Task ProximityShredConsumesTheSourceAndGrantsTwoMaterials(bool bagged, uint definition, uint output)
    {
        using var world = new World();
        var player = world.AddPlayer();
        var posted = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Service.Post = work => posted.TrySetResult(work);
        ulong source = bagged
            ? Assert.Single(world.SpawnBag(player, (definition, 1)).Items.Values).ItemGuid
            : player.Loot.Spawn(definition, 1, Vector3.Zero).WorldGuid;
        player.Sent.Clear();
        player.Send(ProximityShred(player, source, definition));
        Assert.DoesNotContain(player.Inventory.Items.Values, i => i.DefinitionId == definition);
        var complete = await posted.Task.WaitAsync(TimeSpan.FromSeconds(4));
        complete();
        Assert.Empty(player.Loot.Items);
        Assert.Equal(2u, Assert.Single(player.Inventory.Items.Values, i => i.DefinitionId == output).Count);
        Assert.DoesNotContain(player.Sent, p => Is(p, 0xc8, 3));
        // Repeated native requests cannot create another yield.
        player.Send(ProximityShred(player, source, definition));
        Assert.Equal(2u, player.Inventory.Items.Values.Single(i => i.DefinitionId == output).Count);
    }

    [Theory]
    [InlineData("distance")]
    [InlineData("picked_up")]
    [InlineData("capacity")]
    public async Task ProximityShredRechecksTheSourceAndCapacityAtCompletion(string change)
    {
        using var world = new World();
        var player = world.AddPlayer();
        var posted = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Service.Post = work => posted.TrySetResult(work);
        var source = player.Loot.Spawn(2172, 1, Vector3.Zero);
        player.Send(ProximityShred(player, source.WorldGuid, 2172));
        if (change == "distance") player.Move(new(40, 0, 0));
        if (change == "picked_up") player.Loot.TryClaim(source.WorldGuid, out _);
        if (change == "capacity") player.Inventory.TryStow(player.Inventory.CreateInstance(73, 9999));
        var complete = await posted.Task.WaitAsync(TimeSpan.FromSeconds(4));
        complete();
        Assert.DoesNotContain(player.Inventory.Items.Values, i => i.DefinitionId == CraftingCatalog.ArmorScrap);
        Assert.Equal(change != "picked_up", player.Loot.TryGet(source.WorldGuid, out _));
        Assert.Equal(0, Get<long>(player.State, "ShredBusyUntil"));
    }

    [Fact]
    public async Task TwoPlayersShreddingOneSharedBodyBagHelmetReceiveOnlyOneYield()
    {
        using var world = new World();
        var first = world.AddPlayer(shared: true);
        var second = world.AddPlayer(shared: true);
        var queue = new ConcurrentQueue<Action>();
        using var signal = new SemaphoreSlim(0);
        world.Service.Post = work => { queue.Enqueue(work); signal.Release(); };
        var source = Assert.Single(world.SpawnBag(first, (2172, 1)).Items.Values).ItemGuid;
        Assert.Single(second.Bags);
        first.Send(ProximityShred(first, source, 2172));
        second.Send(ProximityShred(second, source, 2172));
        for (int i = 0; i < 2; i++)
        {
            Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(4)));
            Assert.True(queue.TryDequeue(out var complete));
            complete();
        }
        Assert.Equal(2L, world.Players.SelectMany(p => p.Inventory.Items.Values)
            .Where(i => i.DefinitionId == CraftingCatalog.ArmorScrap).Sum(i => (long)i.Count));
        Assert.Empty(first.Bags);
        Assert.Empty(second.Bags);
    }

    [Theory]
    [InlineData(73u)]
    [InlineData(1384u)]
    public void DroppedFuelUsesTheAugustJerryCanActor(uint definition)
    {
        using var world = new World();
        var player = world.AddPlayer();
        player.Inventory.TryPickUp(definition, 1, out var fuel);
        player.Send(Use(player, fuel!.Guid, 4));
        Assert.Equal(9135u, Assert.Single(player.Loot.Items).GroundModelId);
    }

    [Theory]
    [InlineData(2124u, 4u)]
    [InlineData(2112u, 2u)]
    public void BackpackShredYieldMatchesItsCapacityFamily(uint definition, uint expected)
    {
        Assert.True(ShredTable.IsShreddable(definition, out var yield));
        Assert.Equal(CraftingCatalog.CompositeFabric, yield.ItemDefinitionId);
        Assert.Equal(expected, yield.Quantity);
    }

    private static byte[] ProximityShred(Player player, ulong guid, uint definition)
    {
        uint option = Cranberry.Zone.Movement.Footwear.AllowsShred(definition, 63) ? 63 : ItemUseOptionTable.OptionsForItem(definition)
            .Single(id => ItemUseOptionTable.KindOf(id) == ItemUseOptionKind.SalvageItem);
        using var writer = new PacketWriter();
        writer.WriteByte(0xac); writer.WriteByte(0x2c);
        writer.WriteUInt32(1); writer.WriteUInt32(0); writer.WriteUInt32(option);
        writer.WriteUInt64(player.Guid); writer.WriteUInt64(guid); writer.WriteUInt64(player.Guid);
        writer.WriteUInt64(guid); writer.WriteBool(true);
        return writer.Written.ToArray();
    }
}
