using Cranberry.Zone.Inventory;

namespace Cranberry.Zone.Loot;

public static class BodyBagTransfer
{
    public static InventoryPlacement Plan(PlayerInventory inventory, uint definition, MoveItemRequest move)
    {
        InventoryPlacement Refuse(string reason, ContainerErrorCode error = ContainerErrorCode.InteractionValidationFailed)
            => new(InventoryPlacementKind.Refused, reason, Error: error);
        if (!InventoryItemFacts.TryGet(definition, out var fact) || move.Count == 0
            || move.Count > InventoryStacking.Maximum(fact)) return Refuse("invalid loot stack");
        if (move.ContainerGuid == 0 && move.NewSlotId == -1) return inventory.Plan(definition, move.Count);
        if (definition is PlayerInventory.StarterBandageItemDefinitionId or 2424
            && move.ContainerGuid == inventory.BaseBag?.Guid && move.NewSlotId is -1 or 0)
            return inventory.Plan(definition, move.Count);
        if (move.ContainerGuid == PlayerInventory.EquippedContainerGuid && move.NewSlotId > 0)
        {
            var item = new InventoryItemInstance(0, definition, move.Count, fact);
            var equip = ItemVerbs.Equip(inventory, item, ItemUseOptionKind.EquipItem, (uint)move.NewSlotId);
            if (equip.Kind != ItemActionKind.Equip
                || (equip.DisplacedToGround && equip.BoundLoadoutSlotId != SurvivorLoadout.Feet))
                return Refuse(equip.Rule);
            PickupDisplacement? groundDrop = equip.DisplacedToGround
                && inventory.Items.TryGetValue(equip.DisplacedItemGuid, out var old)
                ? new(old.DefinitionId, old.DisplayDefinitionId, old.Count) : null;
            return new(InventoryPlacementKind.LoadoutSlot, "body bag equipment drag",
                LoadoutSlotId: equip.BoundLoadoutSlotId, EquipmentSlotId: equip.BoundEquipmentSlotId,
                DisplacedItemGuid: equip.DisplacedItemGuid, GroundDrop: groundDrop);
        }
        if (inventory.BaseBag is not { } bag || move.ContainerGuid != bag.Guid)
            return Refuse("unknown loot destination", ContainerErrorCode.UnknownContainer);
        if (move.NewSlotId < -1)
            return Refuse("invalid destination slot", ContainerErrorCode.UnknownContainerSlot);
        if ((long)bag.BulkUsed + (long)fact.Bulk * move.Count > inventory.MaxBulkOf(bag))
            return Refuse("not enough carrying capacity");
        // August DropManager uses -1 for a blank bag-grid drop and 0 for a drop
        // onto an InventoryItemData row (its slot getter returns 0). Both request
        // placement within this bag; neither is permission to auto-equip.
        bool chooseBagSlot = move.NewSlotId is -1 or 0;
        if (chooseBagSlot)
        {
            foreach (var (existingSlot, existing) in bag.Slots)
            {
                if (existing.DefinitionId == definition
                    && (ulong)existing.Count + move.Count <= (ulong)InventoryStacking.Maximum(fact))
                    return new(InventoryPlacementKind.Stack, "body bag automatic stack drag",
                        ContainerGuid: bag.Guid, ContainerDefinitionId: bag.DefinitionId,
                        ContainerSlotId: existingSlot, StackTargetItemGuid: existing.Guid);
            }
        }
        uint slot = chooseBagSlot ? bag.NextFreeSlot() : (uint)move.NewSlotId;
        if (slot > bag.Definition.MaximumSlots) return Refuse("destination slot out of range");
        if (bag.Slots.TryGetValue(slot, out var target))
        {
            if (target.DefinitionId != definition || (ulong)target.Count + move.Count > (ulong)InventoryStacking.Maximum(fact))
                return Refuse("destination slot occupied");
            return new(InventoryPlacementKind.Stack, "body bag stack drag", StackTargetItemGuid: target.Guid);
        }
        return new(InventoryPlacementKind.Container, "body bag inventory drag", ContainerGuid: bag.Guid,
            ContainerDefinitionId: bag.DefinitionId, ContainerSlotId: slot);
    }
}
