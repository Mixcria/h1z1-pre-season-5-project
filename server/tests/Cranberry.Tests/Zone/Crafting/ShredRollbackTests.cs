using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Crafting;

public sealed class ShredRollbackTests
{
    private const uint Hoodie = 3405;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoRoomRestoresWornHoodieAndItsOriginalPosture(bool hoodUp)
    {
        PlayerInventory inventory = Fresh(baseBulk: 1);
        var hoodie = inventory.LoadoutSlots[SurvivorLoadout.Chest];
        Assert.True(inventory.TrySetHood(hoodUp));
        var before = Snapshot(inventory);
        var capacity = inventory.Capacity;

        CraftOutcome outcome = ShredTable.Shred(inventory, hoodie.Guid);

        Assert.False(outcome.Succeeded);
        Assert.Equal(CraftRefusal.NoRoom, outcome.Refusal);
        Assert.NotEqual(ContainerErrorCode.None, outcome.ContainerError);
        Assert.Empty(outcome.Changes);
        Assert.Equal(0u, outcome.ClearedLoadoutSlotId);
        Assert.Equal(before, Snapshot(inventory));
        Assert.Equal(capacity, inventory.Capacity);
        Assert.Same(hoodie, inventory.LoadoutSlots[SurvivorLoadout.Chest]);
        Assert.Same(hoodie, inventory.EquipmentSlots[BodySlots.Chest]);
        Assert.Empty(inventory.BaseBag!.Slots);
        Assert.Equal(hoodUp, inventory.HoodUp);
    }

    [Fact]
    public void SuccessfulWornHoodieShredClearsPostureAndGrantsExistingYieldOnce()
    {
        PlayerInventory inventory = Fresh(baseBulk: 100);
        var hoodie = inventory.LoadoutSlots[SurvivorLoadout.Chest];
        Assert.True(inventory.TrySetHood(true));
        Assert.True(ShredTable.IsShreddable(Hoodie, out RecipeIngredient yield));

        CraftOutcome outcome = ShredTable.Shred(inventory, hoodie.Guid);

        Assert.True(outcome.Succeeded);
        Assert.Equal(SurvivorLoadout.Chest, outcome.ClearedLoadoutSlotId);
        Assert.False(inventory.HoodUp);
        Assert.DoesNotContain(hoodie.Guid, inventory.Items.Keys);
        Assert.DoesNotContain(SurvivorLoadout.Chest, inventory.LoadoutSlots.Keys);
        Assert.DoesNotContain(BodySlots.Chest, inventory.EquipmentSlots.Keys);
        Assert.Equal(yield.Quantity, Assert.Single(inventory.Items.Values,
            i => i.DefinitionId == yield.ItemDefinitionId).Count);
        var committed = Snapshot(inventory);
        Assert.False(ShredTable.Shred(inventory, hoodie.Guid).Succeeded);
        Assert.Equal(committed, Snapshot(inventory));
    }

    [Fact]
    public void RefusedBaggedHoodieShredPreservesADifferentWornHood()
    {
        PlayerInventory inventory = Fresh(baseBulk: 100);
        var worn = inventory.LoadoutSlots[SurvivorLoadout.Chest];
        Assert.True(inventory.TrySetHood(true));
        var spare = inventory.CreateInstance(Hoodie, 1);
        Assert.True(inventory.TryStow(spare));
        // Force the same placement refusal used by gateway capacity regressions.
        Assert.True(inventory.TryStow(inventory.CreateInstance(73, 9999)));
        var before = Snapshot(inventory);

        CraftOutcome outcome = ShredTable.Shred(inventory, spare.Guid);

        Assert.Equal(CraftRefusal.NoRoom, outcome.Refusal);
        Assert.Equal(before, Snapshot(inventory));
        Assert.Same(worn, inventory.LoadoutSlots[SurvivorLoadout.Chest]);
        Assert.Same(worn, inventory.EquipmentSlots[BodySlots.Chest]);
        Assert.Same(spare, inventory.BaseBag!.Slots[spare.ContainerSlotId]);
        Assert.True(inventory.HoodUp);
    }

    [Fact]
    public void MissingBaseBagRefusesBeforeUnbindingTheWornHoodie()
    {
        ulong next = 100;
        var inventory = new PlayerInventory(4099, () => ++next);
        var hoodie = inventory.CreateInstance(Hoodie, 1);
        inventory.BindLoadout(hoodie, SurvivorLoadout.Chest, BodySlots.Chest);
        Assert.True(inventory.TrySetHood(true));
        var before = Snapshot(inventory);
        Assert.Null(inventory.BaseBag);

        CraftOutcome outcome = ShredTable.Shred(inventory, hoodie.Guid);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ContainerErrorCode.SlotDoesNotContainItem, outcome.ContainerError);
        Assert.Empty(outcome.Changes);
        Assert.Equal(before, Snapshot(inventory));
        Assert.Same(hoodie, inventory.LoadoutSlots[SurvivorLoadout.Chest]);
        Assert.Same(hoodie, inventory.EquipmentSlots[BodySlots.Chest]);
        Assert.True(inventory.HoodUp);
    }

    private static PlayerInventory Fresh(int baseBulk)
    {
        ulong next = 100;
        var inventory = new PlayerInventory(4099, () => ++next,
            new InventoryOptions { StarterOutfit = [Hoodie], BaseCarryBulk = baseBulk });
        inventory.Bootstrap();
        return inventory;
    }

    private static ItemState[] Snapshot(PlayerInventory inventory) => inventory.Items.Values
        .OrderBy(i => i.Guid).Select(i => new ItemState(i.Guid, i.DefinitionId, i.Count,
            i.ContainerGuid, i.ContainerDefinitionId, i.ContainerSlotId, i.LoadoutSlotId, i.EquipmentSlotId)).ToArray();

    private sealed record ItemState(ulong Guid, uint Definition, uint Count, ulong Container,
        uint ContainerDefinition, uint ContainerSlot, uint LoadoutSlot, uint EquipmentSlot);
}
