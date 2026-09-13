using Cranberry.Transport;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // Ctrl/Shift looting and an explicit drag carry a count and destination. The F-key
    // auto-pickup path cannot represent those choices and must not replace this request.
    private void TransferGroundLoot(SoeConnection connection, GatewaySessionState state,
        PlayerInventory inventory, GroundLootItem source, MoveItemRequest move)
    {
        if (state.DeathSent || state.AirdropCrates.ContainsKey(source.WorldGuid)
            || move.TargetCharacterGuid != state.Guid || move.ItemGuid != source.WorldGuid
            || move.Count == 0 || move.Count > source.Count
            || !WithinPickupReach(state, source.Position, out _))
        {
            SendTunnel(connection, new ContainerError(state.Guid, ContainerErrorCode.InteractionValidationFailed).WriteTo);
            return;
        }
        bool hasKey = state.StreamedLoot.TryGetClaimKey(source.WorldGuid, out var key);
        if (hasKey && state.StreamedLoot.IsTaken(key)) return;
        InventoryPlacement plan = BodyBagTransfer.Plan(inventory, source.ItemDefinitionId, move);
        if (plan.Kind == InventoryPlacementKind.Refused)
        {
            SendTunnel(connection, new ContainerError(state.Guid, plan.Error).WriteTo);
            _log.Info($"{connection} inventory: ground transfer REFUSED {move.Count} x {source.ItemDefinitionId}: {plan.Rule}");
            return;
        }

        // Planning, placement and ownership run synchronously on the listener thread.
        // No world item is removed until a validated inventory placement has succeeded.
        uint previousAbility = ActiveHandAbilityId(inventory);
        ulong previousHand = inventory.WieldedItemGuid;
        inventory.ApplyPickup(source.ItemDefinitionId, move.Count, plan, out var received);
        if (received is null) throw new InvalidOperationException("Validated ground placement was not applied.");
        GroundLootItem? remaining = null;
        if (move.Count == source.Count)
        {
            if (!TryClaimSharedLoot(state, source.WorldGuid, out _))
                throw new InvalidOperationException("Ground ownership changed during a listener-thread transfer.");
        }
        else
        {
            remaining = state.Loot.ReduceCount(source.WorldGuid, source.Count - move.Count);
            if (hasKey)
            {
                state.StreamedLoot.NoteRemaining(key, remaining.Count);
                if (_sharedLootMembership.TryGetValue(state, out ulong matchId))
                {
                    foreach (var (viewer, peer) in _sharedLootMatches[matchId].Members)
                    {
                        if (ReferenceEquals(viewer, state)) continue;
                        ulong guid = viewer.StreamedLoot.FindVisibleGuid(key);
                        if (guid == 0 || !viewer.Loot.TryGet(guid, out _)) continue;
                        var changed = viewer.Loot.ReduceCount(guid, remaining.Count);
                        SendGroundInventoryItem(peer, viewer, changed);
                        if (_options.SendProximateItems) SendProximateItems(peer, viewer);
                    }
                }
            }
        }
        PublishLootPickup(connection, state, inventory, source with { Count = move.Count },
            received, plan, previousAbility, "Proximity transfer", removeWorldObject: remaining is null,
            remainingGround: remaining, previousHandGuid: previousHand);
    }
}
