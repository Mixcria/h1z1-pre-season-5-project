using Cranberry.Transport;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Movement;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // The August native RequestUseItem binding sends no request for the added footwear
    // option. Use the existing inventory WindowEvent, then the normal salvage handler.
    private void HandleFootwearShred(SoeConnection connection, GatewaySessionState state,
        PlayerInventory inventory, ulong guid)
    {
        uint definition = 0;
        if (inventory.Items.TryGetValue(guid, out var item))
            definition = item.DefinitionId;
        else if (state.Loot.TryGet(guid, out var ground))
            definition = ground.ItemDefinitionId;
        else
            foreach (var bag in state.BodyBags.Values)
                if (bag.Items.ContainsKey(guid))
                {
                    definition = bag.GameplayDefinitionFor(guid);
                    break;
                }

        // Resolve the real source in this session; the menu cannot choose a definition,
        // yield, quantity, owner or a different item-use option. Stealth stays native.
        if (Footwear.TierFor(definition) is not (FootwearTier.Fast or FootwearTier.Sturdy))
        {
            RefuseInventoryAction(connection, state, "missing or ineligible footwear");
            return;
        }

        _log.Info($"{connection} inventory: footwear shred requested for {definition} guid={guid}");
        HandleItemUseAction(connection, state,
            new RequestUseItem(1, 63, state.Guid, state.Guid, state.Guid, guid, false, 1, 0));
    }
}
