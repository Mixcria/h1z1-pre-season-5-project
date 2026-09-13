using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Weapons;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private void DropPlayerBodyBag(SoeConnection connection, GatewaySessionState state)
    {
        CloseBodyBag(connection, state);
        if (state.Inventory is not { } inventory) return;
        var bag = new BodyBag();
        foreach (var item in inventory.Items.Values.ToArray())
        {
            if (item.DefinitionId is PlayerInventory.SurvivorFistsItemDefinitionId or 3156) continue;
            bag.Add(item.Guid, item.DefinitionId, item.Count, item.DisplayDefinitionId);
            // Preserve loaded rounds as lootable reserve; looted weapons start empty.
            int loaded = state.Combat.Shooter.AmmoOf(item.Guid);
            uint ammo = AmmoTypes.AmmoItemFor(item.DefinitionId);
            if (loaded > 0 && ammo != 0)
            {
                bag.Add(state.Loot.NextItemGuid(), ammo, (uint)loaded);
                state.Combat.Shooter.Unload(item.Guid);
            }
            inventory.RemoveUnits(item.Guid, 0);
            SendTunnel(connection, new ItemDelete(state.Guid, item.Guid).WriteTo);
        }
        var drop = state.Movement.Player?.Position ?? new Vector3(state.Drop.X, state.Drop.Y, state.Drop.Z);
        SpawnBodyBag(connection, state, bag, drop);
    }

    private void DropPracticeBodyBag(SoeConnection connection, GatewaySessionState state, PracticeTarget target)
    {
        var bag = new BodyBag();
        // Practice supply is intentional: ordinary targets have no player inventory.
        bag.Add(state.Loot.NextItemGuid(), 2423, 5);
        if (target.FullKit)
        {
            bag.Add(state.Loot.NextItemGuid(), PracticeTargetKit.Backpack, 1);
            bag.Add(state.Loot.NextItemGuid(), PracticeTargetKit.Rifle, 1);
            bag.Add(state.Loot.NextItemGuid(), AmmoTypes.AmmoItemFor(PracticeTargetKit.Rifle), 60);
        }
        if (target.HelmetItemId != 0 && target.Armour.HelmetIntact)
            bag.Add(state.Loot.NextItemGuid(), target.HelmetItemId, 1);
        if (target.BodyArmourItemId != 0 && target.Armour.BodyAbsorbsLeft > 0)
            bag.Add(state.Loot.NextItemGuid(), target.BodyArmourItemId, 1);
        SpawnBodyBag(connection, state, bag, target.Position);
    }

    private void SpawnBodyBag(SoeConnection connection, GatewaySessionState state, BodyBag bag, Vector3 position)
    {
        if (bag.Items.Count == 0) return;
        bag.Position = position;
        var item = SpawnGroundLoot(connection, state, BodyBag.ItemDefinitionId, BodyBag.ModelId, position);
        state.BodyBags[item.WorldGuid] = bag;
        if (_sharedLootMembership.TryGetValue(state, out ulong id))
        {
            var match = _sharedLootMatches[id];
            var key = match.Drops.Add(item, _options.LootStream.DroppedItemLifetimeMs > 0
                ? Environment.TickCount64 + _options.LootStream.DroppedItemLifetimeMs : 0);
            match.BodyBags.Add(key, bag);
            NoteSpawned(connection, state, key, item);
            foreach (var (viewer, peer) in match.Members)
            {
                if (ReferenceEquals(viewer, state) || viewer.Match != MatchStep.InMatch
                    || WorldStreamPosition(viewer) is not { } at
                    || Vector3.DistanceSquared(at, position) > MathF.Pow(_options.LootStream.StreamRadiusMetres, 2)
                    || viewer.Loot.TransientIdHeadroom <= 0
                    || !viewer.StreamedLoot.TryReserveDrop(key, position, _options.LootStream.MaxLive)) continue;
                SpawnSharedLoot(peer, viewer, key, () => SpawnGroundLoot(peer, viewer,
                    BodyBag.ItemDefinitionId, BodyBag.ModelId, position));
                if (_options.SendProximateItems) SendProximateItems(peer, viewer);
            }
        }
        else NoteSpawned(connection, state, LootStreamKey.ForDropped(item.WorldGuid), item);
        if (_options.SendProximateItems) SendProximateItems(connection, state);
        _log.Info($"{connection} body bag: spawned {item.WorldGuid} with {bag.Items.Count} stacks at {FormatPosition(position)}");
    }

    private void AddBodyBagProximityRows(SoeConnection connection, GatewaySessionState state,
        List<ProximateItem> rows, GroundLootItem ground)
    {
        if (!state.BodyBags.TryGetValue(ground.WorldGuid, out var bag))
        {
            rows.Add(ToProximateItem(ground));
            return;
        }
        foreach (var item in bag.Records(ground.WorldGuid))
        {
            if (_options.LootStream.PanelMaxRows > 0 && rows.Count >= _options.LootStream.PanelMaxRows) break;
            // The native resolver needs the remote item before the proximity row is clicked.
            SendBodyBagItem(connection, state, ground.WorldGuid, item);
            rows.Add(new(0x80000000u | (uint)item.ItemGuid, item, ground.WorldGuid));
        }
    }

    private bool TryOpenBodyBag(SoeConnection connection, GatewaySessionState state, ulong guid)
    {
        if (!state.BodyBags.TryGetValue(guid, out var bag)) return false;
        if (state.DeathSent || !state.Loot.TryGet(guid, out var ground)
            || !WithinPickupReach(state, ground.Position, out _) || !EnsureInventory(connection, state, "body bag"))
            return true;
        if (state.AccessedBodyBag == guid) return true; // F emits two requests.
        CloseBodyBag(connection, state);
        state.AccessedBodyBag = guid;
        SendBodyBagInventory(connection, state, guid, bag, open: true);
        if (_options.SendProximateItems) SendProximateItems(connection, state);
        return true;
    }

    private void SendBodyBagInventory(SoeConnection connection, GatewaySessionState state,
        ulong guid, BodyBag bag, bool open = false)
    {
        var records = bag.Records(guid);
        SendTunnel(connection, new BeginCharacterAccess(guid, state.Guid, DontOpenInventory: !open, Items: records).WriteTo);
        foreach (var item in records) SendBodyBagItem(connection, state, guid, item);
        SendTunnel(connection, bag.Containers(guid).WriteTo);
    }

    private void SendBodyBagItem(SoeConnection connection, GatewaySessionState state, ulong owner, InventoryItem item)
    {
        var tail = state.Weapons.CreateTail(item.DefinitionId, magazine: 0);
        if (tail is null) SendTunnel(connection, new ItemAdd(owner, item).WriteTo);
        else
        {
            SendWeaponDefinitionsOnce(connection, state);
            SendTunnel(connection, new WeaponItemAdd(owner, item, tail).WriteTo);
        }
    }

    private void CloseBodyBag(SoeConnection connection, GatewaySessionState state)
    {
        if (state.AccessedBodyBag == 0) return;
        SendTunnel(connection, new EndCharacterAccess(state.AccessedBodyBag).WriteTo);
        state.AccessedBodyBag = 0;
        state.CharacterAccessGranted = false;
        GrantSelfInventoryAccess(connection, state);
    }

    private void RefreshBodyBagMovement(SoeConnection connection, GatewaySessionState state)
    {
        if (state.AccessedBodyBag != 0
            && (!state.Loot.TryGet(state.AccessedBodyBag, out var ground)
                || !WithinPickupReach(state, ground.Position, out _))) CloseBodyBag(connection, state);
        long now = Environment.TickCount64;
        if (state.BodyBags.Count == 0 || now < state.NextBodyBagPanelMs) return;
        state.NextBodyBagPanelMs = now + 250;
        // Rebuilding the native list under a held mouse button can cancel a drag. Walking
        // within the same set of nearby items must not republish an identical list.
        if (_options.SendProximateItems && state.BodyBagPanelSignature != BodyBagPanelSignature(state))
            SendProximateItems(connection, state);
    }

    private int BodyBagPanelSignature(GatewaySessionState state)
    {
        var stream = _options.LootStream;
        var nearby = new List<GroundLootItem>();
        if (state.Movement.Player?.Position is Vector3 at && stream.PanelRadiusMetres > 0)
            state.Loot.SelectPanelRows(at, stream.PanelRadiusMetres, stream.PanelHeightMetres, stream.PanelMaxRows, nearby);
        else nearby.AddRange(state.Loot.Items);
        var hash = new HashCode();
        foreach (var item in nearby.OrderBy(i => i.WorldGuid))
        {
            hash.Add(item.WorldGuid);
            if (state.BodyBags.TryGetValue(item.WorldGuid, out var bag))
                foreach (var content in bag.Items.Values) { hash.Add(content.ItemGuid); hash.Add(content.Count); }
        }
        return hash.ToHashCode();
    }

    private bool HandleBodyBagItemUse(SoeConnection connection, GatewaySessionState state, RequestUseItem request)
    {
        ulong owner = state.BodyBags.ContainsKey(request.SourceCharacterGuid) ? request.SourceCharacterGuid
            : state.BodyBags.ContainsKey(request.TargetCharacterGuid) ? request.TargetCharacterGuid : state.AccessedBodyBag;
        if (!state.BodyBags.TryGetValue(owner, out var bag) || !bag.Items.ContainsKey(request.ItemGuid)) return false;
        if (request.CharacterGuid != state.Guid || request.Kind is not (ItemUseOptionKind.LootItem or ItemUseOptionKind.RemoveItem))
        {
            SendTunnel(connection, new ContainerError(state.Guid, ContainerErrorCode.InteractionValidationFailed).WriteTo);
            return true;
        }
        return HandleBodyBagMove(connection, state, new(0, owner, request.ItemGuid, state.Guid,
            request.Count == 0 ? bag.Items[request.ItemGuid].Count : request.Count, -1));
    }

    private bool HandleBodyBagMove(SoeConnection connection, GatewaySessionState state, MoveItemRequest move)
    {
        if (!state.BodyBags.TryGetValue(move.SourceCharacterGuid, out var bag)) return false;
        if (state.DeathSent || state.Inventory is not { } inventory || move.TargetCharacterGuid != state.Guid
            || !state.Loot.TryGet(move.SourceCharacterGuid, out var ground)
            || !WithinPickupReach(state, ground.Position, out _)
            || !bag.Items.TryGetValue(move.ItemGuid, out var source) || move.Count == 0 || move.Count > source.Count)
        {
            SendTunnel(connection, new ContainerError(state.Guid, ContainerErrorCode.InteractionValidationFailed).WriteTo);
            return true;
        }
        uint previousAbility = ActiveHandAbilityId(inventory);
        ulong previousHand = inventory.WieldedItemGuid;
        uint gameplayDefinition = bag.GameplayDefinitionFor(source.ItemGuid);
        var plan = BodyBagTransfer.Plan(inventory, gameplayDefinition, move);
        inventory.ApplyPickup(gameplayDefinition, move.Count, plan, out var received);
        if (received is null)
        {
            _log.Info($"{connection} inventory: body bag transfer REFUSED {move.Count} x {source.DefinitionId} "
                + $"from {move.SourceCharacterGuid}/{move.ItemGuid}: {plan.Rule}");
            SendTunnel(connection, new ContainerError(state.Guid, plan.Error).WriteTo);
            return true;
        }
        if (AmmoTypes.AmmoItemFor(gameplayDefinition) != 0)
            state.Combat.Shooter.EnsureDeclared(received.Guid, received.DefinitionId, 0);
        if (source.Count == move.Count) bag.Remove(source.ItemGuid);
        else bag.Items[source.ItemGuid] = source with { Count = source.Count - move.Count };
        PublishLootPickup(connection, state, inventory,
            ground with { ItemDefinitionId = gameplayDefinition, SkinRewardItemId = source.DefinitionId,
                Count = move.Count }, received, plan,
            previousAbility, "body bag transfer", removeWorldObject: false, previousHandGuid: previousHand);
        PublishBodyBagChange(connection, state, bag, source.ItemGuid);
        return true;
    }

    private void PublishBodyBagChange(SoeConnection connection, GatewaySessionState state, BodyBag bag, ulong changedItem)
    {
        if (bag.Items.Count == 0 && _sharedLootMembership.TryGetValue(state, out ulong matchId))
        {
            var match = _sharedLootMatches[matchId];
            foreach (var (key, sharedBag) in match.BodyBags.ToArray())
                if (ReferenceEquals(bag, sharedBag))
                {
                    match.Claims.TryClaim(key);
                    match.Drops.Remove(key, bag.Position);
                    match.BodyBags.Remove(key);
                    foreach (var member in match.Members.Keys) member.StreamedLoot.NoteTaken(key);
                }
        }
        IEnumerable<KeyValuePair<GatewaySessionState, SoeConnection>> viewers =
            _sharedLootMembership.TryGetValue(state, out ulong id) ? _sharedLootMatches[id].Members
                : new Dictionary<GatewaySessionState, SoeConnection> { [state] = connection };
        foreach (var (viewer, peer) in viewers)
        foreach (var (guid, visibleBag) in viewer.BodyBags.ToArray())
        {
            if (!ReferenceEquals(bag, visibleBag) || !viewer.Loot.TryGet(guid, out _)) continue;
            SendTunnel(peer, new ItemDelete(guid, changedItem).WriteTo);
            if (bag.Items.Count == 0)
            {
                viewer.BodyBags.Remove(guid);
                viewer.Loot.TryClaim(guid, out _);
                viewer.StreamedLoot.NoteTaken(guid);
                viewer.FullNpcSent.Remove(guid);
                if (viewer.AccessedBodyBag == guid) CloseBodyBag(peer, viewer);
                SendTunnel(peer, new RemovePlayer(guid).WriteTo);
            }
            else if (viewer.AccessedBodyBag == guid) SendBodyBagInventory(peer, viewer, guid, bag);
            if (_options.SendProximateItems) SendProximateItems(peer, viewer);
        }

    }
}
