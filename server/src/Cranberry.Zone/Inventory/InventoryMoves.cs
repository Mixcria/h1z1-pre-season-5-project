namespace Cranberry.Zone.Inventory;

/// <summary>The retail inventory's click and equipment-slot drag requests.</summary>
public static class InventoryMoves
{
    public static ItemActionResult Resolve(PlayerInventory inventory, MoveItemRequest move)
    {
        if (move.SourceCharacterGuid != inventory.CharacterGuid
            || move.TargetCharacterGuid != inventory.CharacterGuid
            || !inventory.Items.TryGetValue(move.ItemGuid, out var item))
            return new(ItemActionKind.Refused, ItemUseOptionKind.MoveItem,
                "move does not name an item owned by this character",
                Error: ContainerErrorCode.SlotDoesNotContainItem);

        if (move.Count == 0 || move.Count != item.Count)
            return ItemVerbs.Refused(ItemUseOptionKind.MoveItem, item,
                "equipment moves require the complete item stack", ContainerErrorCode.InteractionValidationFailed);

        // Generic auto-destination requests use container 0 / slot -1. The stock UI only
        // emits these for inspected loot. Our carried-helmet click and retail equipment
        // dragging both use container -1 / the explicit head loadout slot instead.
        if (move.ContainerGuid == 0 && move.NewSlotId == -1)
            return ItemVerbs.Move(inventory, item, ItemUseOptionKind.MoveItem);

        if (move.ContainerGuid == PlayerInventory.EquippedContainerGuid && move.NewSlotId > 0)
            return ItemVerbs.Equip(inventory, item, ItemUseOptionKind.EquipItem, (uint)move.NewSlotId);

        if (move.ContainerGuid == inventory.BaseBag?.Guid && item.LoadoutSlotId != 0)
            return ItemVerbs.Unequip(inventory, item, ItemUseOptionKind.RemoveItem);

        return ItemVerbs.Refused(ItemUseOptionKind.MoveItem, item,
            "unsupported inventory destination", ContainerErrorCode.UnknownContainerSlot);
    }
}
