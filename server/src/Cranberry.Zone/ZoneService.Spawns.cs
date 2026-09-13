using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private DropParticipant[] SpawnRoster(SharedLootMatch match) => match.Members
        .Where(p => p.Value.State == ConnectionState.Open
            && p.Key.Match is not (MatchStep.Menu or MatchStep.Ended)
            && p.Key.PendingLogout is null && !p.Key.LogoutPrepared)
        .Select(p => new DropParticipant(p.Key.Guid, TeamGuid(p.Key))).ToArray();

    private void PreparePopulationPlan(GatewaySessionState state)
    {
        state.MatchSeed = MatchSeeds.Draw(_options.MatchSeed, state.BountyAdmission.MatchId, 0);
        if (_options.Drop.Enabled && _sharedLootMembership.TryGetValue(state, out ulong id)
            && _drop.Places(out _) is { Count: > 0 } places)
        {
            var match = _sharedLootMatches[id];
            if (match.PopulationPlan is null)
            {
                match.PopulationPlan = PopulationMatchPlan.Create(places, _options.Drop, _options.Gas,
                    _options.EnableGas, state.MatchSeed, SpawnRoster(match), TeamSize(state));
                match.Spawns = match.PopulationPlan.Spawns;
                var plan = match.PopulationPlan;
                _log.Info($"match area: {id}, frozen population {plan.Population}, dynamic {plan.Dynamic}, "
                    + $"opening radius {plan.Schedule.InitialCircle.Radius:F0} m, "
                    + $"first safe radius {plan.Schedule.Phase(1).Target.Radius:F0} m");
            }
            state.Schedule = match.PopulationPlan.Schedule;
            return;
        }
        state.Schedule = GasSchedule.Create(_options.Gas, MatchSeeds.For(state.MatchSeed, MatchSeeds.GasSalt));
    }

    private bool TryPopulationDrop(GatewaySessionState state, out DropPlan plan, out string? reason)
    {
        plan = null!;
        reason = null;
        if (!_options.Drop.Enabled) return false;
        var places = _drop.Places(out reason);
        if (places is null || places.Count == 0) return false;
        if (!_sharedLootMembership.TryGetValue(state, out ulong id))
            return _drop.TryPlan(_options.EnableGas ? state.Schedule : null, state.MatchSeed, out plan, out reason);
        var match = _sharedLootMatches[id];
        match.Spawns ??= new(places, _options.Drop,
            _options.EnableGas ? state.Schedule?.Phase(1).Target : null, state.MatchSeed);
        var roster = SpawnRoster(match);
        match.Spawns.Assign(roster, TeamSize(state));
        if (!match.Spawns.Players.TryGetValue(state.Guid, out plan!)) return false;
        _log.Info($"match spawns: {id}, {match.Spawns.Players.Count} players, radius {match.Spawns.RadiusMetres:F0} m, "
            + $"expanded {match.Spawns.ExpandedPlacements}, reduced spacing {match.Spawns.ReducedSpacingPlacements}");
        return true;
    }

    private Vector4 StagingPosition(GatewaySessionState state) =>
        state.StagingPosition == default ? _options.StagingSpawn : state.StagingPosition;

    private void ChooseStagingPosition(GatewaySessionState state)
    {
        // An explicitly relocated staging area must retain its configured mark.
        if (_options.StagingSpawn != new Vector4(-233.83f, 506.36f, -4892.03f, 1))
        {
            state.StagingPosition = _options.StagingSpawn;
            return;
        }
        var occupied = _sharedLootMembership.TryGetValue(state, out ulong id)
            ? _sharedLootMatches[id].Members.Keys.Where(s => !ReferenceEquals(s, state)
                && s.Match is MatchStep.Zoning or MatchStep.Lobby)
                .Select(s => StagingPosition(s)).ToArray()
            : Array.Empty<Vector4>();
        state.StagingPosition = LobbySpawnPlanner.Choose((ulong)Random.Shared.NextInt64(), occupied, state.StagingPosition);
    }
}
