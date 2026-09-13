using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Loot;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private bool AirdropCrateExpired(long landedAtMs, long nowMs) =>
        _options.LootStream.DroppedItemLifetimeMs > 0
        && nowMs - landedAtMs >= _options.LootStream.DroppedItemLifetimeMs;

    private void ExpireAirdropCrate(GatewaySessionState state, LootStreamKey key)
    {
        if (!_sharedLootMembership.TryGetValue(state, out ulong matchId)) return;
        var match = _sharedLootMatches[matchId];
        // The same shared claim used by opening/destruction makes expiry terminal for late viewers.
        if (match.Claims.TryClaim(key)) RetireSharedLootViews(match, key);
        match.Crates.Remove(key);
    }

    private void SyncAirdropCrateViews(SoeConnection connection, GatewaySessionState state, long nowMs)
    {
        foreach (ulong guid in state.AirdropCrates.Keys.Where(guid => !state.Loot.TryGet(guid, out _)).ToArray())
            state.AirdropCrates.Remove(guid);
        if (!_sharedLootMembership.TryGetValue(state, out ulong matchId)) return;
        var match = _sharedLootMatches[matchId];
        Vector3? position = WorldStreamPosition(state);
        foreach (var (key, crate) in match.Crates.ToArray())
        {
            // UnlockAtMs was anchored to the scheduled landing, not a viewer's later spawn.
            // Re-entering interest must neither reroll contents nor restart either clock.
            if (AirdropCrateExpired(crate.UnlockAtMs - _options.Airdrop.UnlockMs, nowMs))
            {
                ExpireAirdropCrate(state, key);
                continue;
            }
            if (match.Claims.IsTaken(key) || state.Match != MatchStep.InMatch
                || position is not Vector3 centre || state.StreamedLoot.IsStreamed(key)) continue;
            Vector3 delta = centre - crate.Position;
            float radius = _options.LootStream.StreamRadiusMetres;
            if (delta.X * delta.X + delta.Z * delta.Z > radius * radius) continue;
            ulong pending = state.StreamedLoot.FindVisibleGuid(key);
            if (pending != 0 && state.Loot.TryGet(pending, out _)) continue; // deferred eviction still on the wire
            if (state.Loot.TransientIdHeadroom == 0
                || !state.StreamedLoot.TryReserveDrop(key, crate.Position, _options.LootStream.MaxLive)) continue;
            // A shared crate exists only after these tables loaded successfully on initial landing.
            var tables = AirdropData.Value;
            GroundLootItem item = SpawnGroundLoot(connection, state, tables.Crate.ItemDefinitionId,
                tables.Crate.GroundModelId, crate.Position, count: 1, nameId: tables.Crate.NameId);
            NoteSpawned(connection, state, key, item);
            state.AirdropCrates[item.WorldGuid] = crate;
            EnsureAirdropMarker(state, key, crate);
            if (_options.SendProximateItems) SendProximateItems(connection, state);
        }
    }
}
