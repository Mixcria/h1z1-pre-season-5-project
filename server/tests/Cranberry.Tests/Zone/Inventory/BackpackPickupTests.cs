using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Inventory;

public sealed class BackpackPickupTests
{
    private const uint Military = 2124;
    private const uint Small = 2116;
    private const uint Ammunition = 1429; // two bulk per round

    [Theory]
    [InlineData(2112u, 150)]
    [InlineData(2116u, 150)]
    [InlineData(2118u, 500)]
    [InlineData(2124u, 500)]
    [InlineData(2125u, 100)]
    public void SpareBackpacksKeepTheMilitaryPackAndCostTheirOwnCargoBulk(uint spareId, int bulk)
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(Military, 1, out var military);
        inventory.TryPickUp(Ammunition, 670, out _);
        int capacity = inventory.Capacity.Max;

        InventoryPlacement plan = inventory.TryPickUp(spareId, 1, out var spare);

        Assert.Equal(InventoryPlacementKind.Container, plan.Kind);
        Assert.Equal(0ul, plan.DisplacedItemGuid);
        Assert.Same(military, inventory.LoadoutSlots[SurvivorLoadout.Backpack]);
        Assert.Same(military, inventory.EquipmentSlots[BodySlots.Backpack]);
        Assert.Equal(inventory.BaseBag!.Guid, spare!.ContainerGuid);
        Assert.Equal(0u, spare.LoadoutSlotId);
        Assert.Equal(0u, spare.EquipmentSlotId);
        Assert.Equal((1340 + bulk, capacity), inventory.Capacity);
        Assert.Equal(1u, spare.Count);
    }

    [Theory]
    [InlineData(975, InventoryPlacementKind.Container)] // 1950 + 150 == current 2100 capacity
    [InlineData(976, InventoryPlacementKind.Refused)]
    public void SparePickupUsesTheCurrentBagsActualBulkLimit(int rounds, InventoryPlacementKind expected)
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(Military, 1, out var military);
        inventory.TryPickUp(Ammunition, (uint)rounds, out _);
        int count = inventory.Items.Count;

        InventoryPlacement plan = inventory.TryPickUp(Small, 1, out var spare);

        Assert.Equal(expected, plan.Kind);
        Assert.Same(military, inventory.LoadoutSlots[SurvivorLoadout.Backpack]);
        Assert.Equal(2100, inventory.Capacity.Max);
        if (expected == InventoryPlacementKind.Refused)
        {
            Assert.Null(spare);
            Assert.Equal(ContainerErrorCode.InteractionValidationFailed, plan.Error);
            Assert.Equal(count, inventory.Items.Count);
            Assert.Equal(rounds * 2, inventory.Capacity.Used);
        }
        else
        {
            Assert.NotNull(spare);
            Assert.Equal(2100, inventory.Capacity.Used);
        }
    }

    [Fact]
    public void SparePickupStillNeedsAFreeContainerSlot()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(Military, 1, out var military);
        InventoryContainer bag = inventory.BaseBag!;
        // Zero-bulk test cargo fills the real slot count independently of the bulk limit.
        for (uint slot = 1; slot <= bag.Definition.MaximumSlots; slot++)
            bag.Place(inventory.CreateInstance(uint.MaxValue, 1), slot);
        Assert.Equal(0, inventory.Capacity.Used);

        InventoryPlacement plan = inventory.TryPickUp(Small, 1, out var spare);

        Assert.Equal(InventoryPlacementKind.Refused, plan.Kind);
        Assert.Equal(ContainerErrorCode.UnknownContainerSlot, plan.Error);
        Assert.Null(spare);
        Assert.Same(military, inventory.LoadoutSlots[SurvivorLoadout.Backpack]);
    }

    [Fact]
    public void BackpackUpgradeStillEquipsAndKeepsTheOldPackEvenWhenCurrentBagIsFull()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(Small, 1, out var small);
        inventory.TryPickUp(Ammunition, 550, out _);
        Assert.Equal((1100, 1100), inventory.Capacity);

        InventoryPlacement plan = inventory.TryPickUp(Military, 1, out var military);

        Assert.Equal(InventoryPlacementKind.LoadoutSlot, plan.Kind);
        Assert.Equal(small!.Guid, plan.DisplacedItemGuid);
        Assert.Same(military, inventory.LoadoutSlots[SurvivorLoadout.Backpack]);
        Assert.Equal(inventory.BaseBag!.Guid, small.ContainerGuid);
        Assert.Equal((1250, 2100), inventory.Capacity);
    }

    [Fact]
    public void AnExplicitEquipCanStillChooseTheSmallerSpareWhenTheContentsFit()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(Military, 1, out var military);
        inventory.TryPickUp(Ammunition, 200, out _);
        inventory.TryPickUp(Small, 1, out var small);
        Assert.Equal(inventory.BaseBag!.Guid, small!.ContainerGuid);

        ItemActionResult plan = ItemVerbs.Equip(inventory, small, ItemUseOptionKind.EquipItem);
        InventoryActions.Apply(inventory, plan);

        Assert.Equal(ItemActionKind.Equip, plan.Kind);
        Assert.False(plan.DisplacedToGround);
        Assert.Same(small, inventory.LoadoutSlots[SurvivorLoadout.Backpack]);
        Assert.Equal(inventory.BaseBag.Guid, military!.ContainerGuid);
        Assert.Equal((900, 1100), inventory.Capacity);
    }

    private static PlayerInventory Fresh()
    {
        ulong next = 0x4000;
        var inventory = new PlayerInventory(0x1001, () => ++next,
            new InventoryOptions { StarterOutfit = [] });
        inventory.Bootstrap();
        return inventory;
    }
}
