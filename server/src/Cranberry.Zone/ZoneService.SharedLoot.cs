using System.Diagnostics.CodeAnalysis;
using Cranberry.Transport;
using Cranberry.Zone.Loot;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private sealed class SharedLootMatch
    {
        public long? LobbyDeadlineMs { get; set; }
        public int DamageBatchDepth { get; set; }
        public bool AliveCountPending { get; set; }
        public Vehicles.VehicleFleet? Fleet { get; set; }
        public Destructibles.DestructibleWorld Destructibles { get; } = new();
        public SharedLootClaims Claims { get; } = new();
        public SharedDroppedLoot Drops { get; } = new();
        public Dictionary<LootStreamKey, BodyBag> BodyBags { get; } = [];
        public Dictionary<LootStreamKey, AirdropCrateState> Crates { get; } = [];
        public Dictionary<LootStreamKey, Combat.FuelCanDurability> FuelCans { get; } = [];
        public Dictionary<GatewaySessionState, SoeConnection> Members { get; } = [];
        public Cranberry.Zone.Match.PopulationDropPlanner? Spawns { get; set; }
        public Cranberry.Zone.Match.PopulationMatchPlan? PopulationPlan { get; set; }
        public bool HadMultiplePlayers { get; set; }
        public Dictionary<GatewaySessionState, ulong> Teams { get; } = [];
        public Dictionary<GatewaySessionState, byte> TeamColorSlots { get; } = [];
        public Dictionary<uint, (ulong Team, int Size)> PartyTeams { get; } = [];
        public ulong NextTeam { get; set; }
        public bool HadMultipleTeams { get; set; }
    }

    private readonly Dictionary<ulong, SharedLootMatch> _sharedLootMatches = [];
    private readonly Dictionary<GatewaySessionState, ulong> _sharedLootMembership = [];
    private ulong _nextInventoryItemGuid = LootWorld.DefaultItemGuidBase;

    private void JoinSharedLoot(SoeConnection connection, GatewaySessionState state)
    {
        ulong id = state.BountyAdmission.MatchId;
        if (id != 0 && _sharedLootMembership.TryGetValue(state, out ulong previous) && previous == id)
        {
            state.StreamedLoot.SharedClaims = _sharedLootMatches[id].Claims;
            return;
        }
        LeaveSharedLoot(state);
        if (id == 0) return;
        if (!_sharedLootMatches.TryGetValue(id, out var match))
            _sharedLootMatches.Add(id, match = new());
        match.Members.Add(state, connection);
        AssignMatchTeam(match, state);
        match.HadMultiplePlayers |= match.Members.Count > 1;
        _sharedLootMembership.Add(state, id);
        state.StreamedLoot.SharedClaims = match.Claims;
        PublishTeamRoster(state);
    }

    private void LeaveSharedLoot(GatewaySessionState state)
    {
        LeaveAirdropMarkers(state);
        LeaveCloudVisuals(state);
        EvictDepartingVehicleRider(state);
        state.VehicleEngineRuntimeGuid = 0;
        state.StreamedLoot.SharedClaims = null;
        state.BodyBags.Clear();
        state.AccessedBodyBag = 0;
        if (IsTeamMode(state) && _sharedLootMembership.TryGetValue(state, out ulong currentId)
            && _sharedLootMatches[currentId].Members.TryGetValue(state, out var connection)
            && connection.State == ConnectionState.Open)
            SendTunnel(connection, new Cranberry.Zone.Match.RemoveGroup(ClientTeamId(state)).WriteTo);
        var formerTeammate = IsTeamMode(state) ? TeamMembers(state).FirstOrDefault(member => !ReferenceEquals(member, state)) : null;
        if (!_sharedLootMembership.Remove(state, out ulong id)) return;
        if (_sharedLootMatches.TryGetValue(id, out var match))
        {
            _pendingTeamDeaths.Remove(state);
            _teamsRemainingSent.Remove(state);
            _teamPoseSent.Remove(state);
            match.Members.Remove(state);
            match.Teams.Remove(state);
            match.TeamColorSlots.Remove(state);
            if (state.MatchPartyId != 0 && !match.Members.Keys.Any(member => member.MatchPartyId == state.MatchPartyId))
                match.PartyTeams.Remove(state.MatchPartyId);
            if (match.Members.Count == 0) _sharedLootMatches.Remove(id);
            else PublishSharedAlive(match);
            if (formerTeammate is not null) PublishTeamRoster(formerTeammate);
        }
    }

    // Called only after the reach/capacity checks, synchronously with inventory application.
    // Each viewer has a different world guid; the stable marker/box key is the authority.
    private bool TryClaimSharedLoot(GatewaySessionState state, ulong worldGuid,
        [NotNullWhen(true)] out GroundLootItem? item)
    {
        item = null;
        if (state.DeathSent) return false;
        if (!state.Loot.TryGet(worldGuid, out _)) return false;
        if (!state.StreamedLoot.TryGetClaimKey(worldGuid, out var key)
            || !_sharedLootMembership.TryGetValue(state, out ulong id))
        {
            bool claimed = state.Loot.TryClaim(worldGuid, out item);
            if (claimed) state.FuelCans.Remove(worldGuid);
            return claimed;
        }

        var match = _sharedLootMatches[id];
        if (!match.Claims.TryClaim(key)) return false;
        if (!state.Loot.TryClaim(worldGuid, out item))
            throw new InvalidOperationException("Loot changed during a listener-thread claim.");
        if (key.Kind == LootStreamKeyKind.Dropped) match.Drops.Remove(key, item.Position);
        RetireSharedLootViews(match, key, state);
        return true;
    }

    private void RetireSharedLootViews(SharedLootMatch match, LootStreamKey key, GatewaySessionState? except = null)
    {
        RetireAirdropMarker(match, key);
        match.FuelCans.Remove(key);
        foreach (var (viewer, connection) in match.Members)
        {
            if (ReferenceEquals(viewer, except)) continue;
            ulong visibleGuid = viewer.StreamedLoot.FindVisibleGuid(key);
            if (visibleGuid != 0)
            {
                viewer.StreamedLoot.NoteTaken(visibleGuid);
                viewer.Loot.TryClaim(visibleGuid, out _);
                viewer.FullNpcSent.Remove(visibleGuid);
                viewer.AirdropCrates.Remove(visibleGuid);
                viewer.BodyBags.Remove(visibleGuid);
                if (viewer.AccessedBodyBag == visibleGuid) CloseBodyBag(connection, viewer);
                SendTunnel(connection, writer => new RemovePlayer(visibleGuid).WriteTo(writer));
                if (_options.SendProximateItems) SendProximateItems(connection, viewer);
            }
            viewer.StreamedLoot.NoteTaken(key);
        }
    }

    // Check when the deferred slice runs, before spawning any bytes.
    private void SpawnSharedLoot(SoeConnection connection, GatewaySessionState state,
        LootStreamKey key, Func<GroundLootItem> spawn)
    {
        if (state.StreamedLoot.IsTaken(key)) return;
        var item = NoteSpawned(connection, state, key, spawn());
        if (_sharedLootMembership.TryGetValue(state, out ulong id)
            && _sharedLootMatches[id].BodyBags.TryGetValue(key, out var bag))
            state.BodyBags[item.WorldGuid] = bag;
    }

    private void SharePlayerDrop(SoeConnection connection, GatewaySessionState state, GroundLootItem item)
    {
        var key = _sharedLootMembership.TryGetValue(state, out ulong id)
            ? _sharedLootMatches[id].Drops.Add(item, _options.LootStream.DroppedItemLifetimeMs > 0
                ? Environment.TickCount64 + _options.LootStream.DroppedItemLifetimeMs : 0)
            : LootStreamKey.ForDropped(item.WorldGuid);
        NoteSpawned(connection, state, key, item);
    }

    private static LootStreamKey SharedCrateKey(int index, int payloadIndex) =>
        LootStreamKey.ForDropped(0x6000_0000_0000_0000UL | ((ulong)(uint)index << 32) | (uint)payloadIndex);

    private AirdropCrateState ResolveSharedCrate(GatewaySessionState state, LootStreamKey key, AirdropCrateState candidate)
    {
        if (!_sharedLootMembership.TryGetValue(state, out ulong id)) return candidate;
        var crates = _sharedLootMatches[id].Crates;
        if (!crates.TryGetValue(key, out var crate))
            crates.Add(key, crate = candidate);
        return crate;
    }

    private bool PlanSharedDrops(SoeConnection connection, GatewaySessionState state, List<Action> burst)
    {
        if (state.Match != MatchStep.InMatch || !state.LandingLootDrained
            || WorldStreamPosition(state) is not { } position
            || !_sharedLootMembership.TryGetValue(state, out ulong id)) return false;
        int added = 0;
        var match = _sharedLootMatches[id];
        foreach (var expired in match.Drops.Expire(Environment.TickCount64))
            if (match.Claims.TryClaim(expired))
            {
                RetireSharedLootViews(match, expired);
                match.BodyBags.Remove(expired);
            }
        foreach (var (key, item) in match.Drops.Nearby(position, _options.LootStream.StreamRadiusMetres))
        {
            if (added >= 8 || state.Loot.TransientIdHeadroom <= added) break;
            if (!state.StreamedLoot.TryReserveDrop(key, item.Position, _options.LootStream.MaxLive)) continue;
            burst.Add(() => SpawnSharedLoot(connection, state, key, () => SpawnGroundLoot(connection, state,
                item.ItemDefinitionId, item.GroundModelId, item.Position,
                state.StreamedLoot.RemainingCount(key, item.Count), item.NameId,
                skinRewardItemId: item.SkinRewardItemId, magazineRounds: item.MagazineRounds)));
            added++;
        }
        return added != 0;
    }
}
