using Cranberry.Transport;
using Cranberry.Zone.Match;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    /// <summary>
    /// Both scheduled world streaming and explicit console spawns initialize the same match fleet.
    /// A command arriving before the paced landing burst must retain the entire parking plan.
    /// </summary>
    private VehicleFleet EnsureVehicleFleet(
        SoeConnection connection, GatewaySessionState state, bool populate)
    {
        SharedLootMatch? shared = _sharedLootMembership.TryGetValue(state, out ulong sharedId)
            ? _sharedLootMatches[sharedId] : null;
        state.Fleet ??= shared?.Fleet;
        if (state.Fleet is VehicleFleet existing) return existing;

        var fleet = new VehicleFleet(VehicleRosterData.Value, _options.VehicleFleet);
        if (populate)
        {
            ulong vehicleSeed = state.MatchSeed == 0 ? _options.LootSeed
                : MatchSeeds.For(state.MatchSeed, 0x56454849434C4553UL);
            IReadOnlyList<PlannedVehicle> plan = VehicleSpawnPlanner.Plan(
                VehicleAnchorData.Value, fleet.Roster, vehicleSeed, _options.VehiclePlan);
            fleet.Populate(plan,
                index => VehicleWorldGuidBase + (ulong)index,
                index => VehicleTransientIdBase + (uint)index,
                vehicleSeed);
            string policy = _options.VehiclePlan.SpawnChance is double chance
                ? FormattableString.Invariant($"{chance:P0} independent chance per pad")
                : $"legacy target {_options.VehiclePlan.Count}";
            _log.Info($"{connection} vehicles: planned {fleet.Count} over "
                + $"{VehicleAnchorData.Value.Count:N0} eligible spawn anchors ({policy}, seed {vehicleSeed})");
        }

        state.Fleet = fleet;
        if (shared is not null) shared.Fleet = fleet;
        return fleet;
    }
}
