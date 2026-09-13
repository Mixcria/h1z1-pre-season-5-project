namespace Cranberry.Zone.Inventory;

/// <summary>Gameplay stack limits, kept separate from the unmodified August datasheet.</summary>
public static class InventoryStacking
{
    // The client already renders the starter four-count Q stack. Use the same stack
    // behaviour for looted bandages and first aid kits in their dedicated Q/E slots.
    public static int Maximum(InventoryItemFact fact) =>
        fact.DefinitionId is PlayerInventory.StarterBandageItemDefinitionId or 2424 ? 9999 : fact.MaxStackSize;
}
