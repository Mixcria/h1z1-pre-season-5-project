using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // August Models.txt 33: InvisibleTriangle.adr, the same mesh used by the authored
    // RallyPointSmoke_Green actor, with no implicit EffectList to double the explicit tag.
    private const uint AirdropMarkerAnchorModelId = 33;

    private sealed record AirdropMarker(SharedLootMatch Match, LootStreamKey Key,
        AirdropCrateState Crate, ulong Guid, uint TransientId, uint EffectId)
    {
        public Dictionary<GatewaySessionState, (SoeConnection Connection, int Generation)> Viewers { get; } = [];
    }

    // The shared crate, rather than its first viewer or viewer-local loot actor, owns the marker.
    // The marker never grants/claims loot or advances the crate's unlock/expiry clock.
    private readonly Dictionary<(SharedLootMatch Match, LootStreamKey Key), AirdropMarker> _airdropMarkers = [];

    private void EnsureAirdropMarker(GatewaySessionState state, LootStreamKey key, AirdropCrateState crate)
    {
        if (!_options.Airdrop.Enabled || _options.Airdrop.MarkerEffectId == 0
            || !_sharedLootMembership.TryGetValue(state, out ulong matchId)) return;
        SharedLootMatch match = _sharedLootMatches[matchId];
        if (match.Claims.IsTaken(key) || !match.Crates.TryGetValue(key, out var owned)
            || !ReferenceEquals(owned, crate)) return;
        var identity = (match, key);
        if (!_airdropMarkers.ContainsKey(identity))
            _airdropMarkers.Add(identity, new(match, key, crate, _nextCloudAnchorGuid++,
                _nextCloudAnchorTransientId++, _options.Airdrop.MarkerEffectId));
        SyncAirdropMarkers(state);
    }

    private void SyncAirdropMarkers(GatewaySessionState state)
    {
        if (!_sharedLootMembership.TryGetValue(state, out ulong matchId)
            || !_sharedLootMatches.TryGetValue(matchId, out var match)
            || !match.Members.TryGetValue(state, out var connection)) return;
        foreach (AirdropMarker marker in _airdropMarkers.Values.Where(m => ReferenceEquals(m.Match, match)).ToArray())
        {
            if (match.Claims.IsTaken(marker.Key) || !match.Crates.TryGetValue(marker.Key, out var crate)
                || !ReferenceEquals(crate, marker.Crate))
            {
                RetireAirdropMarker(match, marker.Key);
                continue;
            }
            bool known = marker.Viewers.TryGetValue(state, out var seen)
                && seen.Generation == state.WorldGeneration && ReferenceEquals(seen.Connection, connection);
            float radius = _options.Airdrop.MarkerRangeMetres
                + (known ? _options.Airdrop.MarkerHysteresisMetres : 0);
            bool visible = connection.State == ConnectionState.Open
                && state.Match is MatchStep.InMatch or MatchStep.Ended
                && WorldStreamPosition(state) is Vector3 position
                && Vector3.DistanceSquared(position, marker.Crate.Position) <= radius * radius;
            if (!visible)
            {
                RemoveAirdropMarkerViewer(marker, state);
                continue;
            }
            if (known) continue;
            var body = new LightweightEntityBody(ZoneOpcodes.AddLightweightNpc, marker.Guid,
                marker.TransientId, AirdropMarkerAnchorModelId, marker.Crate.Position + new Vector3(0, 0.15f, 0),
                new Vector4(0, 0, 0, 1),
                RenderDistance: _options.Airdrop.MarkerRangeMetres + _options.Airdrop.MarkerHysteresisMetres);
            SendTunnel(connection, body.WriteTo);
            SendTunnel(connection, new AddEffectTagCompositeEffect(marker.Guid, marker.EffectId).WriteTo);
            marker.Viewers[state] = (connection, state.WorldGeneration);
        }
    }

    private void RemoveAirdropMarkerViewer(AirdropMarker marker, GatewaySessionState viewer)
    {
        if (!marker.Viewers.Remove(viewer, out var seen) || seen.Connection.State != ConnectionState.Open
            || seen.Generation != viewer.WorldGeneration) return;
        SendTunnel(seen.Connection, new RemoveEffectTagCompositeEffect(marker.Guid, marker.EffectId).WriteTo);
        SendTunnel(seen.Connection, new RemovePlayer(marker.Guid).WriteTo);
    }

    private void RetireAirdropMarker(SharedLootMatch match, LootStreamKey key)
    {
        if (!_airdropMarkers.Remove((match, key), out var marker)) return;
        foreach (var viewer in marker.Viewers.Keys.ToArray()) RemoveAirdropMarkerViewer(marker, viewer);
    }

    private void LeaveAirdropMarkers(GatewaySessionState state)
    {
        foreach (var marker in _airdropMarkers.Values.ToArray())
        {
            RemoveAirdropMarkerViewer(marker, state);
            if (marker.Match.Members.Count == 1 && marker.Match.Members.ContainsKey(state))
                RetireAirdropMarker(marker.Match, marker.Key);
        }
    }
}
