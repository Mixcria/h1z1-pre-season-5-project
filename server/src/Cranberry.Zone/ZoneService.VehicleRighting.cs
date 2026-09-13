using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    internal const int VehicleRightingDurationMs = 1200;
    private const int VehicleRightingFrameMs = 40;
    private readonly Dictionary<MatchVehicle, VehicleRightingMotion> _vehicleRightings = [];

    private sealed record VehicleRightingMotion(VehicleFleet Fleet, SharedLootMatch? SharedMatch,
        GatewaySessionState Initiator, SoeConnection Connection, int WorldGeneration,
        Vector3 Position, Quaternion Rotation, float Yaw, uint ClientTime, long StartedAtMs)
    {
        public long LastFrameAtMs { get; set; } = StartedAtMs;
    }

    private bool TryRightVehicle(SoeConnection connection, GatewaySessionState state,
        VehicleFleet fleet, MatchVehicle vehicle, VehicleEntrySource source)
    {
        long now = Environment.TickCount64;
        // All interaction sources and players must wait until the body finishes rolling.
        if (_vehicleRightings.ContainsKey(vehicle)) return true;
        // A single F press carries both interaction packets. Absorb its second half even if
        // the native physics update has already reported the vehicle upright.
        if (vehicle.LastRightingPlayer == state.Guid && vehicle.LastRightingMs != long.MinValue
            && now - vehicle.LastRightingMs < 1500) return true;
        if (source is not (VehicleEntrySource.InteractRequest or VehicleEntrySource.PlayerSelect)) return false;
        if (vehicle.LastRotation is not Quaternion rotation || VehicleFlipDetector.UpDot(rotation) > 0.25f) return false;
        if (state.DeathSent || state.Hitpoints == 0 || state.Match != MatchStep.InMatch
            || vehicle.Health == 0 || vehicle.OccupantCount != 0 || fleet.TryGetForOccupant(state.Guid, out _)
            || MathF.Abs(vehicle.LastSpeed) > 3f || !state.StreamedVehicles.IsStreamed(vehicle.Guid)
            || state.Movement.Player?.Position is not Vector3 position
            || Vector3.DistanceSquared(position, vehicle.Position) > 36f) return true;
        if (vehicle.LastRightingMs != long.MinValue && now - vehicle.LastRightingMs < 1500) return true;
        if (Post is null)
        {
            _log.Warn($"{connection} vehicles: animated righting needs the listener-thread dispatcher");
            return true;
        }

        // 70/0e alone never recovered the body; one final 11/23 did, but visibly snapped it.
        // Hold simulation released while sending a short eased roll through the proven
        // controller-transform path. Native physics resumes only after the last frame.
        ulong previous = vehicle.CoastingOwnerGuid;
        if (previous != 0 && previous != state.Guid) TransferVehicleSimulation(state, vehicle, previous);
        if (previous == state.Guid) ReleaseVehicleSimulation(connection, state, vehicle, broadcastParked: false);
        vehicle.EndCoast();
        vehicle.LastSpeed = 0;
        vehicle.LastPoseMs = long.MinValue;
        vehicle.MovementVersion = unchecked((byte)(vehicle.MovementVersion + 1));
        vehicle.LastRightingPlayer = state.Guid;
        vehicle.LastRightingMs = now;
        vehicle.LastFlipPulseMs = vehicle.UpsideDownSinceMs = now;
        var shared = _sharedLootMembership.TryGetValue(state, out ulong matchId) ? _sharedLootMatches[matchId] : null;
        _vehicleRightings.Add(vehicle, new(fleet, shared, state, connection, state.WorldGeneration,
            vehicle.Position, Quaternion.Normalize(rotation), vehicle.Yaw, vehicle.LastClientTime, now));
        // Initial frame keeps the original orientation and position; it only establishes
        // the new movement version. No upright pose is sent ahead of the animation.
        SendVehicleControlToViewers(connection, state, vehicle, VehicleManagedLocation.At(vehicle).WriteTo);
        ScheduleVehicleRighting(vehicle);
        _log.Info($"{connection} vehicles: rolling {vehicle.Guid} upright over {VehicleRightingDurationMs} ms");
        return true;
    }

    private void ScheduleVehicleRighting(MatchVehicle vehicle)
    {
        if (!Later(null, VehicleRightingFrameMs, () =>
        {
            if (AdvanceVehicleRighting(vehicle, Environment.TickCount64)) ScheduleVehicleRighting(vehicle);
        })) _vehicleRightings.Remove(vehicle);
    }

    // The timer and tests use the same monotonic step; late callbacks skip missed frames.
    internal bool AdvanceVehicleRighting(MatchVehicle vehicle, long nowMs)
    {
        if (!_vehicleRightings.TryGetValue(vehicle, out var motion)) return false;
        KeyValuePair<GatewaySessionState, SoeConnection>[] members = motion.SharedMatch?.Members.ToArray()
            ?? [new(motion.Initiator, motion.Connection)];
        var viewers = members.Where(pair => pair.Value.State == ConnectionState.Open
            && ReferenceEquals(pair.Key.Fleet, motion.Fleet) && pair.Key.Match == MatchStep.InMatch
            && (motion.SharedMatch is not null || pair.Key.WorldGeneration == motion.WorldGeneration)).ToArray();
        if (vehicle.Health == 0 || vehicle.OccupantCount != 0
            || !motion.Fleet.TryGet(vehicle.Guid, out var current) || !ReferenceEquals(current, vehicle)
            || viewers.Length == 0)
        {
            _vehicleRightings.Remove(vehicle);
            return false;
        }
        if (nowMs <= motion.LastFrameAtMs) return true;
        motion.LastFrameAtMs = nowMs;
        long elapsed = Math.Clamp(nowMs - motion.StartedAtMs, 0, VehicleRightingDurationMs);
        float fraction = elapsed / (float)VehicleRightingDurationMs;
        float eased = fraction * fraction * (3f - 2f * fraction);
        var upright = Quaternion.CreateFromAxisAngle(Vector3.UnitY, motion.Yaw);
        vehicle.LastRotation = Quaternion.Normalize(Quaternion.Slerp(motion.Rotation, upright, eased));
        // Lift smoothly through the roll so the roof/side clears the ground, then leave
        // the same small final clearance that the owner's successful recovery used.
        float lift = elapsed == VehicleRightingDurationMs ? 0.75f
            : 0.75f * eased + 0.65f * MathF.Sin(MathF.PI * eased);
        vehicle.SetPose(motion.Position + Vector3.UnitY * lift, motion.Yaw);
        vehicle.LastClientTime = unchecked(motion.ClientTime + (uint)elapsed);
        void Send(Action<Cranberry.Protocol.PacketWriter> write)
        {
            foreach (var (viewer, link) in viewers)
                if (viewer.StreamedVehicles.IsStreamed(vehicle.Guid)) SendTunnel(link, write);
        }
        Send(VehicleManagedLocation.At(vehicle).WriteTo);
        if (elapsed < VehicleRightingDurationMs) return true;

        _vehicleRightings.Remove(vehicle);
        motion.Fleet.NoteAttitude(vehicle, upright, _options.VehicleDamage.UpsideDownDotThreshold);
        // Refresh the stale-UDP guard at handoff: a delayed timer must not shorten it.
        vehicle.LastRightingMs = nowMs;
        // The paired interaction was absorbed during the roll. A fresh F press can
        // now mount the upright car while the movement-version guard remains active.
        vehicle.LastRightingPlayer = 0;
        var parked = VehiclePoseRelay.Parked(vehicle);
        Send(parked.WriteTo);
        Send(parked.WriteTo);

        // Prefer the player who pressed F, but a remaining viewer can settle the body
        // if that player disconnected or left this match during the animation.
        var simulator = viewers.OrderBy(pair => pair.Key != motion.Initiator).FirstOrDefault(pair =>
            !pair.Key.DeathSent && pair.Key.Hitpoints != 0 && pair.Key.StreamedVehicles.IsStreamed(vehicle.Guid));
        if (simulator.Key is not { } state || simulator.Value is not { } connection) return false;

        vehicle.CoastingOwnerGuid = state.Guid;
        vehicle.CoastStartedMs = nowMs;
        vehicle.CoastRestSinceMs = long.MinValue;
        state.Movement.RegisterManagedEntity(vehicle.TransientId, vehicle.Guid);
        SendTunnel(connection, CharacterManagedObject.Grant(vehicle.Guid, state.Guid).WriteTo);
        SendTunnel(connection, new ManagedObjectResponseControl(true, vehicle.Guid).WriteTo);
        _log.Info($"{connection} vehicles: finished rolling {vehicle.Guid} upright; native physics settling");
        return false;
    }
}
