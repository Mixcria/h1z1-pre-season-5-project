using System.Numerics;
using Cranberry.Transport;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private float VehicleInitialRadius(GatewaySessionState state)
    {
        float radius = _options.VehicleStream.Enabled
            ? Math.Max(_options.VehicleRadius, _options.VehicleStream.StreamRadiusMetres)
            : _options.VehicleRadius;
        return state.ChuteGuid != 0 && state.Released
            ? Math.Max(radius, _options.VehicleAirborneRadius) : radius;
    }

    private Vehicles.VehicleStreamOptions VehicleStreamingOptions(GatewaySessionState state)
    {
        var options = _options.VehicleStream;
        if (state.ChuteGuid == 0 || !state.Released) return options;
        float radius = Math.Max(options.StreamRadiusMetres, _options.VehicleAirborneRadius);
        return options with
        {
            StreamRadiusMetres = radius,
            DespawnRadiusMetres = Math.Max(options.DespawnRadiusMetres, radius + 100f),
            MaxLive = Math.Max(options.MaxLive, 64),
            MaxPerRestream = Math.Max(options.MaxPerRestream, 16),
        };
    }

    /// <summary>
    /// Interest follows the client-owned chute during descent. Channel 2 is deliberately ignored
    /// while mounted, so its retained player pose can still belong to the staging area.
    /// This only chooses a streaming centre; it does not move the player or its interaction reach.
    /// </summary>
    private Vector3? WorldStreamPosition(GatewaySessionState state)
    {
        if (state.HostedObserverActive && state.HostedCameraPosition is { } camera) return camera;
        if (state.ChuteGuid != 0 && state.Released)
        {
            if (state.Movement.TryGetManaged(_options.ParachuteTransientId, out var chute)
                && chute.Guid == state.ChuteGuid
                && chute.Movement?.Position is Vector3 position)
                return position;

            // Release is already beyond SynchronizedTeleport.ClientReady. Until the first managed
            // pose, the server knows the release X/Z from the current match's chosen drop.
            Vector4 drop = state.Drop != default ? state.Drop : _options.MatchDropSpawn;
            return new Vector3(drop.X, state.ChuteAirY, drop.Z);
        }

        return state.Movement.Player?.Position;
    }

    private Vector3 WorldStreamCentre(GatewaySessionState state)
    {
        Vector4 drop = state.Drop != default ? state.Drop : _options.MatchDropSpawn;
        return WorldStreamPosition(state) ?? new Vector3(drop.X, drop.Y, drop.Z);
    }

    private void ArmDescentWorld(SoeConnection connection, GatewaySessionState state)
    {
        if (!state.Released || state.Match != MatchStep.InMatch || state.ChuteGuid == 0
            || Post is null || !state.WorldStreamStartup.TryArmDescent(state.WorldGeneration)) return;

        ArmGroundLoot(connection, state, "parachute descent", duringDescent: true);
    }

    /// <summary>
    /// Starts the normal world during descent, using the same paced burst and shared pump as
    /// ground streaming. Landing can still add development drops and practice targets.
    /// </summary>
    private void ArmGroundLoot(
        SoeConnection connection, GatewaySessionState state, string trigger, bool duringDescent = false)
    {
        bool real = _options.GroundLootRadius > 0f
            && _options.GroundLootMaxPerBurst > 0
            && !state.RealGroundLootArmed;
        bool dev = !duringDescent && _options.DevGroundLootMs > 0
            && _options.DevGroundLootCount > 0
            && !state.DevGroundLootArmed;
        Vector3 centre = WorldStreamCentre(state);
        bool doors = _options.SendDoors
            && (state.Doors?.ShouldRestream(centre, _options.DoorRadius) ?? true);
        bool vehicles = _options.SendVehicles && !state.VehiclesArmed;

        // Descent may have armed every normal world lane already. Practice targets still belong
        // to landing, even when it has no additional loot, doors or vehicles to send.
        if (!duringDescent) ArmPracticeTargets(connection, state);
        if (!real && !dev && !doors && !vehicles) return;

        state.RealGroundLootArmed |= real;
        state.DevGroundLootArmed |= dev;
        state.VehiclesArmed |= vehicles;
        int delayMs = real || doors || vehicles
            ? Math.Max(0, _options.GroundLootDelayMs)
            : Math.Max(0, _options.DevGroundLootMs);
        _log.Info($"{connection} loot: ground loot armed by {trigger} in {delayMs} ms - "
            + (real
                ? $"the {_options.GroundLootMaxPerBurst} nearest live Z2 markers within "
                    + $"{_options.GroundLootRadius} m (seed {_options.LootSeed})"
                : "no real Z2 roll")
            + (dev ? $"; dev drop {_options.DevGroundLootCount} x item {_options.GroundLootItemDefinitionId}" : string.Empty)
            + (doors ? $"; doors within {_options.DoorRadius} m" : string.Empty)
            + (vehicles ? $"; vehicles within {_options.VehicleRadius} m" : string.Empty));

        int generation = state.WorldGeneration;
        state.WorldStreamStartup.BeginBurst(generation);
        state.LandingLootDrained = false;
        Later(connection, delayMs, () =>
        {
            if (generation != state.WorldGeneration) return;
            // Plan at dispatch time so a steering parachute uses its latest horizontal position.
            // All map queries use X/Z; high altitude does not hide ground actors under the canopy.
            var burst = new List<Action>();
            if (real) SpawnRealGroundLoot(connection, state, trigger, burst);
            if (doors) SpawnNearbyDoors(connection, state, trigger, burst);
            if (vehicles) SpawnNearbyVehicles(connection, state, trigger, burst);
            if (dev) burst.Add(() => SpawnDevGroundLoot(connection, state));

            // Both the initial loot and vehicle sets adopt entries as slices are sent. A landing
            // burst finishing first must not allow the pump to duplicate a pending descent spawn.
            burst.Add(() =>
            {
                if (state.WorldStreamStartup.CompleteBurst(generation)) state.LandingLootDrained = true;
            });
            DrainBurst(connection, state, burst, from: 0, trigger,
                republishProximateItems: real || dev, worldGeneration: generation);
        });

        // Movement never creates another timer chain. Each existing pump arm retains its own
        // interval, radius, eviction band and object/byte budget throughout descent and walking.
        int worldPumpMs = WorldPumpIntervalMs;
        if (worldPumpMs > 0 && !state.WorldPumping)
            state.WorldPumping = Later(connection, worldPumpMs, () => PumpWorld(connection, state, generation));
    }
}
