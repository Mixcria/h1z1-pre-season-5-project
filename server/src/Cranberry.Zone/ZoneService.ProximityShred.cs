using Cranberry.Transport;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private bool HandleProximityShred(SoeConnection connection, GatewaySessionState state,
        PlayerInventory inventory, RequestUseItem request)
    {
        if (request.Kind != ItemUseOptionKind.SalvageItem || inventory.Items.ContainsKey(request.ItemGuid))
            return false;

        void Refuse(ContainerErrorCode error = ContainerErrorCode.InteractionValidationFailed) =>
            SendTunnel(connection, new ContainerError(state.Guid, error).WriteTo);

        GroundLootItem? ground = null;
        BodyBag? bodyBag = null;
        uint definition = 0;
        foreach (var (owner, bag) in state.BodyBags)
            if (bag.Items.ContainsKey(request.ItemGuid) && state.Loot.TryGet(owner, out ground))
            {
                bodyBag = bag;
                definition = bag.GameplayDefinitionFor(request.ItemGuid);
                break;
            }
        if (bodyBag is null && state.Loot.TryGet(request.ItemGuid, out ground))
            definition = ground.ItemDefinitionId;

        if (request.CharacterGuid != state.Guid || request.Count > 1 || state.DeathSent
            || state.Match != MatchStep.InMatch || ground is null
            || !inventory.Options.AnswerSalvageItem || !WithinPickupReach(state, ground.Position, out _)
            || !InventoryItemFacts.TryGet(definition, out var fact)
            || !(ItemUseOptionTable.Allows(definition, request.ItemUseOptionId)
                || Movement.Footwear.AllowsShred(definition, request.ItemUseOptionId))
            || !ShredTable.IsShreddable(definition, out var yield, inventory.Options.ShredEveryOfferedItem))
        {
            Refuse();
            return true;
        }
        if (RefuseInteractionDuringLogout(connection, state)) return true;
        if (state.ShredBusyUntil > Environment.TickCount64 || state.PendingMedicalCast is not null)
        {
            Refuse(ContainerErrorCode.ContainerInUse);
            return true;
        }
        var placement = inventory.Plan(yield.ItemDefinitionId, yield.Quantity);
        if (placement.Kind == InventoryPlacementKind.Refused) { Refuse(placement.Error); return true; }

        var action = ItemVerbs.Salvage(inventory, new(request.ItemGuid, definition, 1, fact),
            request.Kind, request.ItemUseOptionId);
        int duration = Math.Max(0, action.BusyMilliseconds);
        int world = state.WorldGeneration, interaction = state.InteractionGeneration;
        long sequence = ++state.ProximityShredSequence;
        state.ShredBusyUntil = Environment.TickCount64 + Math.Max(1, duration);
        bool armed = Later(connection, duration, () =>
        {
            if (state.ProximityShredSequence != sequence || state.InteractionGeneration != interaction
                || state.WorldGeneration != world || !ReferenceEquals(state.Inventory, inventory)) return;
            try
            {
                if (state.DeathSent || state.Match != MatchStep.InMatch
                    || !state.Loot.TryGet(ground.WorldGuid, out var current)
                    || !WithinPickupReach(state, current.Position, out _)
                    || (bodyBag is not null && (!state.BodyBags.TryGetValue(ground.WorldGuid, out var liveBag)
                        || !ReferenceEquals(liveBag, bodyBag) || !bodyBag.Items.ContainsKey(request.ItemGuid))))
                { Refuse(); return; }

                var finalPlacement = inventory.Plan(yield.ItemDefinitionId, yield.Quantity);
                if (finalPlacement.Kind == InventoryPlacementKind.Refused) { Refuse(finalPlacement.Error); return; }
                // The input never enters the player's inventory. Claim it only after the yield
                // fits, on the listener thread, so competing pickups/shreds have one winner.
                if (bodyBag is not null)
                {
                    var source = bodyBag.Items[request.ItemGuid];
                    if (source.Count == 1) bodyBag.Remove(request.ItemGuid);
                    else bodyBag.Items[request.ItemGuid] = source with { Count = source.Count - 1 };
                    PublishBodyBagChange(connection, state, bodyBag, request.ItemGuid);
                }
                else if (current.Count == 1)
                {
                    if (!TryClaimSharedLoot(state, current.WorldGuid, out _)) { Refuse(); return; }
                    state.StreamedLoot.NoteTaken(current.WorldGuid);
                    state.FullNpcSent.Remove(current.WorldGuid);
                    SendTunnel(connection, new RemovePlayer(current.WorldGuid).WriteTo);
                }
                else ReduceProximityShredStack(connection, state, current);

                inventory.ApplyPickup(yield.ItemDefinitionId, yield.Quantity, finalPlacement, out var produced);
                if (produced is null) throw new InvalidOperationException("Validated shred yield was not applied.");
                ApplyCraft(connection, state, inventory, new CraftOutcome
                {
                    Requested = 1, Crafted = 1,
                    Changes = [new(produced.Guid, finalPlacement.Kind == InventoryPlacementKind.Stack
                        ? CraftItemChangeKind.GrantedIntoStack : CraftItemChangeKind.Granted, produced)],
                });
                if (_options.SendProximateItems) SendProximateItems(connection, state);
                _log.Info($"{connection} inventory: proximity shred {definition} -> {yield.Quantity} x {yield.ItemDefinitionId}");
            }
            finally
            {
                state.ShredBusyUntil = 0;
                SendInteractionStops(connection, state);
            }
        });
        if (!armed)
        {
            state.ShredBusyUntil = 0;
            Refuse(ContainerErrorCode.ContainerInUse);
            return true;
        }
        SendTunnel(connection, new InteractionStart(state.Guid, duration, fact.NameId,
            action.InteractionAnimationId).WriteTo);
        return true;
    }

    private void ReduceProximityShredStack(SoeConnection connection, GatewaySessionState state, GroundLootItem source)
    {
        var remaining = state.Loot.ReduceCount(source.WorldGuid, source.Count - 1);
        SendGroundInventoryItem(connection, state, remaining);
        if (!state.StreamedLoot.TryGetClaimKey(source.WorldGuid, out var key)) return;
        state.StreamedLoot.NoteRemaining(key, remaining.Count);
        if (!_sharedLootMembership.TryGetValue(state, out ulong matchId)) return;
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
