using Cranberry.Zone;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Inventory;

public sealed class InventoryMovesTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(7u)]
    public void OccupiedWeaponSlotsExchangeEvenWhenNeitherGunFitsInTheBag(uint heldSlot)
    {
        var inventory = Fresh();
        inventory.TryPickUp(1374, 1, out var shotgun);
        inventory.TryPickUp(2425, 1, out var rifle);
        Assert.True(inventory.TrySelectLoadoutSlot(heldSlot, out _));
        inventory.EquipmentSlots.TryGetValue(BodySlots.RightHand, out var held);
        var equipment = inventory.EquipmentSlots.ToArray();
        uint count = (uint)inventory.Items.Count;
        Assert.True(inventory.Capacity.Max < shotgun!.Fact.Bulk);
        var plan = InventoryMoves.Resolve(inventory, new MoveItemRequest(
            PlayerInventory.EquippedContainerGuid, inventory.CharacterGuid,
            rifle!.Guid, inventory.CharacterGuid, 1, 1));
        Assert.True(plan.SwapLoadoutSlots);
        InventoryActions.Apply(inventory, plan);
        Assert.Same(rifle, inventory.LoadoutSlots[1]);
        Assert.Same(shotgun, inventory.LoadoutSlots[2]);
        inventory.EquipmentSlots.TryGetValue(BodySlots.RightHand, out var afterHeld);
        Assert.Same(held, afterHeld);
        Assert.Equal(equipment, inventory.EquipmentSlots.ToArray());
        Assert.Equal(heldSlot == 1 ? 2u : heldSlot == 2 ? 1u : heldSlot, inventory.CurrentLoadoutSlotId);
        Assert.Equal(0ul, rifle.ContainerGuid);
        Assert.Equal(0ul, shotgun.ContainerGuid);
        Assert.Equal(count, (uint)inventory.Items.Count);
    }

    [Theory]
    [InlineData(2423u, SurvivorLoadout.QuickUse2)]
    [InlineData(2424u, SurvivorLoadout.QuickUse1)]
    public void MedicalCannotBeDraggedIntoTheOtherMedicalSlot(uint itemId, uint wrongSlot)
    {
        var inventory = Fresh();
        inventory.TryPickUp(itemId, 1, out var item);
        uint originalSlot = item!.LoadoutSlotId;
        var plan = InventoryMoves.Resolve(inventory, new MoveItemRequest(
            PlayerInventory.EquippedContainerGuid, inventory.CharacterGuid,
            item.Guid, inventory.CharacterGuid, 1, (int)wrongSlot));
        Assert.Equal(ItemActionKind.Refused, plan.Kind);
        InventoryActions.Apply(inventory, plan);
        Assert.Equal(originalSlot, item.LoadoutSlotId);
        Assert.False(inventory.LoadoutSlots.TryGetValue(wrongSlot, out var occupant) && occupant == item);
    }

    [Fact]
    public void RemovingAHelmetKeepsAnEmptyHeadDestinationInTheClientSlotMap()
    {
        var inventory = Fresh();
        inventory.TryPickUp(2172, 1, out var helmet);
        inventory.RemoveUnits(helmet!.Guid, 0);

        var slots = inventory.ToLoadoutSlots().Slots;
        Assert.Equal(LoadoutSlotTable.Slots(inventory.LoadoutId).Select(s => s.SlotId), slots.Select(s => s.Key));
        var head = Assert.Single(slots, s => s.Key == SurvivorLoadout.Head).Slot;
        Assert.Equal(0ul, head.ItemGuid);
        Assert.Equal(0u, head.ItemDefinitionId);
        Assert.Equal(inventory.LoadoutId, head.LoadoutId);
        Assert.Equal(SurvivorLoadout.Head, head.SlotId);
    }

    private static PlayerInventory Fresh()
    {
        ulong next = 0x4000;
        var inventory = new PlayerInventory(0x1001, () => ++next,
            new InventoryOptions { StarterOutfit = [] });
        inventory.Bootstrap();
        return inventory;
    }

    [Fact]
    public void HelmetPickupReplacesAHatEvenWhenTheHelmetWouldNotFitInTheBag()
    {
        var inventory = Fresh();
        inventory.TryPickUp(2484, 1, out var hat);
        Assert.Equal(SurvivorLoadout.Head, hat!.LoadoutSlotId);
        Assert.True(inventory.Capacity.Max < InventoryItemFacts.All.Single(i => i.DefinitionId == 2172).Bulk);

        var pickup = inventory.TryPickUp(2172, 1, out var helmet);

        Assert.Equal(InventoryPlacementKind.LoadoutSlot, pickup.Kind);
        Assert.Equal(hat.Guid, pickup.DisplacedItemGuid);
        Assert.Same(helmet, inventory.LoadoutSlots[SurvivorLoadout.Head]);
        Assert.Equal(inventory.BaseBag!.Guid, hat.ContainerGuid);
        Assert.Equal(0u, hat.LoadoutSlotId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BaggedHelmetEquipsIntoAVacatedHeadSlotByClickOrDrag(bool drag)
    {
        var inventory = Fresh();
        inventory.TryPickUp(2112, 1, out _);
        inventory.TryPickUp(2172, 1, out var broken);
        inventory.TryPickUp(2170, 1, out var spare);
        Assert.Equal(inventory.BaseBag!.Guid, spare!.ContainerGuid);
        inventory.RemoveUnits(broken!.Guid, 0);
        Assert.False(inventory.LoadoutSlots.ContainsKey(SurvivorLoadout.Head));

        var plan = InventoryMoves.Resolve(inventory, new MoveItemRequest(
            drag ? PlayerInventory.EquippedContainerGuid : 0, inventory.CharacterGuid,
            spare.Guid, inventory.CharacterGuid, 1, drag ? (int)SurvivorLoadout.Head : -1));
        Assert.Equal(ItemActionKind.Equip, plan.Kind);
        InventoryActions.Apply(inventory, plan);

        Assert.Same(spare, inventory.EquipmentSlots[BodySlots.Head]);
        Assert.Same(spare, inventory.LoadoutSlots[SurvivorLoadout.Head]);
        Assert.Equal(0ul, spare.ContainerGuid);
        Assert.DoesNotContain(inventory.BaseBag.Slots.Values, item => item.Guid == spare.Guid);
    }

    [Fact]
    public void ExplicitHelmetDragCanReplaceAnotherHelmetWithoutLosingEitherItem()
    {
        var inventory = Fresh();
        inventory.TryPickUp(2112, 1, out _);
        inventory.TryPickUp(2172, 1, out var old);
        inventory.TryPickUp(2170, 1, out var spare);
        var plan = InventoryMoves.Resolve(inventory, new MoveItemRequest(
            PlayerInventory.EquippedContainerGuid, inventory.CharacterGuid,
            spare!.Guid, inventory.CharacterGuid, 1, (int)SurvivorLoadout.Head));
        Assert.Equal(ItemActionKind.Equip, plan.Kind);
        InventoryActions.Apply(inventory, plan);
        Assert.Same(spare, inventory.LoadoutSlots[SurvivorLoadout.Head]);
        Assert.Equal(inventory.BaseBag!.Guid, old!.ContainerGuid);
    }

    [Theory]
    [InlineData(13)] // helmet onto feet
    [InlineData(7)]  // protected fists
    [InlineData(999)]
    public void IncompatibleEquipmentTargetsLeaveTheInventoryUnchanged(int slot)
    {
        var inventory = Fresh();
        var helmet = inventory.CreateInstance(2172, 1);
        inventory.TryStow(helmet);
        var plan = InventoryMoves.Resolve(inventory, new MoveItemRequest(
            PlayerInventory.EquippedContainerGuid, inventory.CharacterGuid,
            helmet.Guid, inventory.CharacterGuid, 1, slot));
        Assert.Equal(ItemActionKind.Refused, plan.Kind);
        InventoryActions.Apply(inventory, plan);
        Assert.Equal(inventory.BaseBag!.Guid, helmet.ContainerGuid);
        Assert.Equal(0u, helmet.LoadoutSlotId);
    }

    [Fact]
    public void MovingAWeaponToAnotherHotbarSlotRemovesItsOldBinding()
    {
        var inventory = Fresh();
        inventory.TryPickUp(2229, 1, out var gun);
        Assert.Equal(SurvivorLoadout.Wheel1, gun!.LoadoutSlotId);
        var plan = InventoryMoves.Resolve(inventory, new MoveItemRequest(
            PlayerInventory.EquippedContainerGuid, inventory.CharacterGuid,
            gun.Guid, inventory.CharacterGuid, 1, 2));
        Assert.Equal(ItemActionKind.Equip, plan.Kind);
        InventoryActions.Apply(inventory, plan);
        Assert.False(inventory.LoadoutSlots.ContainsKey(SurvivorLoadout.Wheel1));
        Assert.Same(gun, inventory.LoadoutSlots[2]);
        Assert.Single(inventory.EquipmentSlots.Values, item => item.Guid == gun.Guid);
    }
}
