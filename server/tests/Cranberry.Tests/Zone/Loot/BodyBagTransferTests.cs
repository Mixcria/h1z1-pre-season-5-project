using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

public sealed class BodyBagTransferTests
{
    private const uint Ammunition = 1429; // August 7.62mm: two bulk per round.
    private const uint Rifle = 1889;

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void BagAutomaticSlotMergesRequestedCountIntoExistingStack(int destinationSlot)
    {
        var inventory = Fresh();
        inventory.TryPickUp(Ammunition, 10, out var existing);

        var plan = BodyBagTransfer.Plan(inventory, Ammunition, BagMove(inventory, 3, destinationSlot));
        inventory.ApplyPickup(Ammunition, 3, plan, out var received);

        Assert.Equal(InventoryPlacementKind.Stack, plan.Kind);
        Assert.Same(existing, received);
        Assert.Equal(13u, existing!.Count);
        Assert.Single(inventory.BaseBag!.Slots);
        Assert.Equal(inventory.BaseBag.Guid, plan.ContainerGuid);
        Assert.Equal(existing.ContainerSlotId, plan.ContainerSlotId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void BagAutomaticSlotStoresAnEquippableWeaponWithoutAutoEquipping(int destinationSlot)
    {
        var inventory = Fresh(10_000);
        var before = inventory.LoadoutSlots.ToDictionary(pair => pair.Key, pair => pair.Value.Guid);

        var plan = BodyBagTransfer.Plan(inventory, Rifle, BagMove(inventory, 1, destinationSlot));
        inventory.ApplyPickup(Rifle, 1, plan, out var received);

        Assert.Equal(InventoryPlacementKind.Container, plan.Kind);
        Assert.Equal(inventory.BaseBag!.Guid, received!.ContainerGuid);
        Assert.Equal(0u, received.LoadoutSlotId);
        Assert.Equal(0u, received.EquipmentSlotId);
        Assert.Equal(before.OrderBy(pair => pair.Key), inventory.LoadoutSlots
            .ToDictionary(pair => pair.Key, pair => pair.Value.Guid).OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void BagAutomaticSlotSkipsAFullStackAndAllocatesAFreeSlot(int destinationSlot)
    {
        var inventory = Fresh(100_000);
        Assert.True(InventoryItemFacts.TryGet(Ammunition, out var fact));
        inventory.TryPickUp(Ammunition, (uint)fact.MaxStackSize, out var full);

        var plan = BodyBagTransfer.Plan(inventory, Ammunition, BagMove(inventory, 1, destinationSlot));
        inventory.ApplyPickup(Ammunition, 1, plan, out var received);

        Assert.Equal(InventoryPlacementKind.Container, plan.Kind);
        Assert.NotSame(full, received);
        Assert.Equal((uint)fact.MaxStackSize, full!.Count);
        Assert.Equal(1u, received!.Count);
        Assert.Equal(2, inventory.BaseBag!.Slots.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void BagAutomaticMergeStillChecksTheRequestedBulk(int destinationSlot)
    {
        var inventory = Fresh();
        inventory.TryPickUp(Ammunition, 49, out var existing);

        var fits = BodyBagTransfer.Plan(inventory, Ammunition, BagMove(inventory, 1, destinationSlot));
        var tooMuch = BodyBagTransfer.Plan(inventory, Ammunition, BagMove(inventory, 2, destinationSlot));

        Assert.Equal(InventoryPlacementKind.Stack, fits.Kind);
        Assert.Equal(InventoryPlacementKind.Refused, tooMuch.Kind);
        Assert.Equal(ContainerErrorCode.InteractionValidationFailed, tooMuch.Error);
        Assert.Equal(49u, existing!.Count); // Planning/refusal must not mutate.
        Assert.Single(inventory.BaseBag!.Slots);
    }

    [Fact]
    public void PositiveBagSlotIsHonoredInsteadOfMergingSomewhereElse()
    {
        var inventory = Fresh();
        inventory.TryPickUp(Ammunition, 10, out var existing);

        var plan = BodyBagTransfer.Plan(inventory, Ammunition, BagMove(inventory, 3, 7));
        inventory.ApplyPickup(Ammunition, 3, plan, out var received);

        Assert.Equal(InventoryPlacementKind.Container, plan.Kind);
        Assert.Equal(7u, received!.ContainerSlotId);
        Assert.Equal(10u, existing!.Count);
        Assert.Equal(3u, received.Count);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(10_000)]
    public void InvalidExplicitBagSlotRemainsARefusal(int destinationSlot)
    {
        var inventory = Fresh();

        var plan = BodyBagTransfer.Plan(inventory, Ammunition, BagMove(inventory, 1, destinationSlot));

        Assert.Equal(InventoryPlacementKind.Refused, plan.Kind);
        Assert.Empty(inventory.BaseBag!.Slots);
    }

    [Fact]
    public void ExplicitOccupiedBagSlotDoesNotFallBackToAnotherSlot()
    {
        var inventory = Fresh(10_000);
        inventory.TryPickUp(Ammunition, 10, out var existing);

        var plan = BodyBagTransfer.Plan(inventory, Rifle,
            BagMove(inventory, 1, (int)existing!.ContainerSlotId));

        Assert.Equal(InventoryPlacementKind.Refused, plan.Kind);
        Assert.Single(inventory.BaseBag!.Slots);
        Assert.Equal(10u, existing.Count);
    }

    [Fact]
    public void EquippedContainerStillUsesTheRequestedEquipmentSlot()
    {
        var inventory = Fresh(10_000);
        var move = new MoveItemRequest(PlayerInventory.EquippedContainerGuid, 0x2001, 0x3001,
            inventory.CharacterGuid, 1, (int)SurvivorLoadout.Head);

        var plan = BodyBagTransfer.Plan(inventory, 2168, move);

        Assert.Equal(InventoryPlacementKind.LoadoutSlot, plan.Kind);
        Assert.Equal(SurvivorLoadout.Head, plan.LoadoutSlotId);
    }

    private static MoveItemRequest BagMove(PlayerInventory inventory, uint count, int slot) =>
        new(inventory.BaseBag!.Guid, 0x2001, 0x3001, inventory.CharacterGuid, count, slot);

    private static PlayerInventory Fresh(int bulk = 100)
    {
        ulong next = 0x4000;
        var inventory = new PlayerInventory(0x1001, () => ++next,
            new InventoryOptions { StarterOutfit = [], BaseCarryBulk = bulk });
        inventory.Bootstrap();
        return inventory;
    }
}
