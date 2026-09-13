using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private sealed record CloudVisual(ulong Guid, uint TransientId, uint ModelId,
        Detonation Detonation, GatewaySessionState Owner, SoeConnection Connection,
        SharedLootMatch? Match, MatchStep Phase)
    {
        public Dictionary<GatewaySessionState, (SoeConnection Connection, int Generation)> Viewers { get; } = [];
    }

    // These IDs are allocated by this ZoneService, not by a player's loot world. The existing
    // reserved 0x4900... / 3,100,000 namespace can therefore be shared by all cloud viewers.
    private readonly Dictionary<ulong, CloudVisual> _cloudVisuals = [];

    private ulong StartCloudVisual(SoeConnection connection, GatewaySessionState state,
        uint modelId, Detonation detonation)
    {
        var match = _sharedLootMembership.TryGetValue(state, out ulong id) ? _sharedLootMatches[id] : null;
        var cloud = new CloudVisual(_nextCloudAnchorGuid++, _nextCloudAnchorTransientId++, modelId,
            detonation, state, connection, match, state.Match);
        _cloudVisuals.Add(cloud.Guid, cloud);
        SyncCloudVisual(cloud.Guid);
        return cloud.Guid;
    }

    private void SyncCloudVisual(ulong guid)
    {
        if (!_cloudVisuals.TryGetValue(guid, out var cloud)) return;
        KeyValuePair<GatewaySessionState, SoeConnection>[] members = cloud.Match?.Members.ToArray()
            ?? [new(cloud.Owner, cloud.Connection)];
        var visible = new HashSet<GatewaySessionState>();
        foreach (var (viewer, link) in members)
        {
            if (link.State != ConnectionState.Open
                || (viewer.Match != cloud.Phase && viewer.Match != MatchStep.Ended)) continue;
            if (!ReferenceEquals(viewer, cloud.Owner)
                && (GameplayDamagePosition(viewer) is not Vector3 position
                    || !(Vector3.Distance(position, cloud.Detonation.Position) <= Rulings.Throwables.SpawnRangeUnits)))
                continue;
            visible.Add(viewer);
            if (cloud.Viewers.TryGetValue(viewer, out var known) && known.Generation == viewer.WorldGeneration)
                continue;
            var body = new LightweightEntityBody(ZoneOpcodes.AddLightweightNpc, cloud.Guid,
                cloud.TransientId, cloud.ModelId, cloud.Detonation.Position, new Vector4(0, 0, 0, 1));
            SendTunnel(link, body.WriteTo);
            SendTunnel(link, new AddEffectTagCompositeEffect(cloud.Guid, cloud.Detonation.Fact.CloudEffectId).WriteTo);
            cloud.Viewers[viewer] = (link, viewer.WorldGeneration);
        }
        foreach (var viewer in cloud.Viewers.Keys.Where(viewer => !visible.Contains(viewer)).ToArray())
            RemoveCloudViewer(cloud, viewer);
    }

    private void RemoveCloudViewer(CloudVisual cloud, GatewaySessionState viewer)
    {
        if (!cloud.Viewers.Remove(viewer, out var seen) || seen.Connection.State != ConnectionState.Open
            || seen.Generation != viewer.WorldGeneration) return;
        SendTunnel(seen.Connection, new RemoveEffectTagCompositeEffect(
            cloud.Guid, cloud.Detonation.Fact.CloudEffectId).WriteTo);
        SendTunnel(seen.Connection, new RemovePlayer(cloud.Guid).WriteTo);
    }

    private void EndCloudVisual(ulong guid)
    {
        if (!_cloudVisuals.Remove(guid, out var cloud)) return;
        foreach (var viewer in cloud.Viewers.Keys.ToArray()) RemoveCloudViewer(cloud, viewer);
    }

    private void LeaveCloudVisuals(GatewaySessionState state)
    {
        foreach (var cloud in _cloudVisuals.Values.ToArray())
        {
            if (ReferenceEquals(cloud.Owner, state)) EndCloudVisual(cloud.Guid);
            else RemoveCloudViewer(cloud, state);
        }
    }
}
