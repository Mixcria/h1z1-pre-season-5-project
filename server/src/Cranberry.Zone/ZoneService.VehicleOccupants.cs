using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // Mount responses address actors; Vehicle.Owner/Occupy address the receiving client's
    // vehicle manager (140c9a380/140c99f20). Never use the latter as observer attachment packets.
    private void SyncVehicleAttachments(SoeConnection connection, GatewaySessionState state, MatchVehicle car,
        ulong onlyRider = 0)
    {
        if (!_options.Peers.Spawn || state.Peer is not { InMatch: true } viewer
            || !state.StreamedVehicles.IsSpawned(car.Guid)) return;

        foreach (var (rider, attached) in viewer.VehicleAttachments.ToArray())
            if (attached.VehicleGuid == car.Guid
                && (car.SeatOf(rider) < 0 || !viewer.View.Knows(new(rider))))
                RemoveVehicleAttachment(viewer, rider);

        foreach (var occupant in car.Occupants())
        {
            ulong rider = occupant.CharacterGuid;
            if ((onlyRider != 0 && rider != onlyRider) || rider == state.Guid || !viewer.View.Knows(new(rider))
                || _peers.Find(rider) is not { IsReplicable: true } subject
                || subject.MatchId != viewer.MatchId) continue;
            var next = (VehicleGuid: car.Guid, Seat: (int)occupant.SeatIndex);
            bool known = viewer.VehicleAttachments.TryGetValue(rider, out var previous);
            if (known && previous == next) continue;
            if (known && previous.VehicleGuid != car.Guid)
            {
                RemoveVehicleAttachment(viewer, rider);
                known = false;
            }
            viewer.Sink.ForgetPose(rider);
            uint driver = car.Definition.Seats[next.Seat].IsDriver ? 1u : 0u;
            if (known)
                SendTunnel(connection, new SeatChangeResponse(rider, car.Guid, (uint)next.Seat, driver).WriteTo);
            else
                SendTunnel(connection, new MountResponse(rider, car.Guid, (uint)next.Seat, IsDriver: driver).WriteTo);
            viewer.VehicleAttachments[rider] = next;
        }
    }

    private static void RemoveVehicleAttachment(PeerSession viewer, ulong rider)
    {
        if (!viewer.VehicleAttachments.Remove(rider, out var attached)) return;
        viewer.Sink.ForgetPose(rider);
        using var writer = new PacketWriter();
        new DismountResponse(rider, attached.VehicleGuid).WriteTo(writer);
        viewer.Sink.Send(writer.Written.ToArray());
    }

    private void SyncPeerVehicleAttachment(PeerSession viewer, PeerSession subject)
    {
        if (viewer.Sink is SessionPeerSink sink && sink.Connection.Tag is GatewaySessionState state
            && state.Fleet?.TryGetForOccupant(subject.CharacterGuid, out var car) == true)
            // Sweep has reserved IDs for its entire enter batch. Only this rider's d5 has
            // necessarily been sent so far; later riders are attached by their own enter.
            SyncVehicleAttachments(sink.Connection, state, car, subject.CharacterGuid);
    }

    private KeyValuePair<GatewaySessionState, SoeConnection>[] VehicleMembers(
        SoeConnection connection, GatewaySessionState state) =>
        _sharedLootMembership.TryGetValue(state, out ulong matchId)
            ? _sharedLootMatches[matchId].Members.Where(member => ReferenceEquals(member.Key.Fleet, state.Fleet)).ToArray()
            : [new(state, connection)];

    // Update interest positions, not foot motion records: the car's flags/version belong to
    // the managed actor. Attached riders follow the native seat, without a second pose relay.
    private void NoteVehicleOccupantPositions(SoeConnection connection, GatewaySessionState state, MatchVehicle car)
    {
        foreach (var (member, _) in VehicleMembers(connection, state))
        {
            member.StreamedVehicles.InvalidateCandidates();
            if (car.SeatOf(member.Guid) < 0) continue;
            member.Movement.PinPlayer(car.Position);
            if (member.Peer is { } peer) peer.Position = car.Position;
            PublishTeamPose(member); // Seated channel-2 movement is suppressed; retain the 1 Hz team throttle.
        }
    }

    private void PublishVehicleOccupants(SoeConnection connection, GatewaySessionState state, MatchVehicle car)
    {
        NoteVehicleOccupantPositions(connection, state, car);
        var occupants = car.Occupants();
        foreach (var (viewer, link) in VehicleMembers(connection, state))
        {
            if (link.State != ConnectionState.Open) continue;
            SyncVehicleAttachments(link, viewer, car);
            if (car.SeatOf(viewer.Guid) >= 0) PublishTeamHudStatus(viewer);
            // The actor already received its local transition. Refresh the remaining riders'
            // seat lists using THEIR character GUIDs; a bystander receives neither packet.
            if (ReferenceEquals(viewer, state) || car.SeatOf(viewer.Guid) < 0) continue;
            if (car.DriverGuid == viewer.Guid)
                SendTunnel(link, new VehicleOwnerState(car.Guid, viewer.Guid, car.Definition.VehicleId, occupants).WriteTo);
            SendTunnel(link, new VehicleOccupantState(car.Guid, viewer.Guid, car.Definition.VehicleId,
                car.Definition.SeatCount, occupants).WriteTo);
        }
    }

    private void ResetVehicleRiderPose(GatewaySessionState state, MatchVehicle car)
    {
        state.Movement.PinPlayer(car.Position);
        if (state.Peer is not { } subject) return;
        subject.Position = car.Position;
        subject.ResetRelaySnapshot();
        _peers.CollectViewers(subject, _peerViewers);
        foreach (var viewer in _peerViewers) viewer.Viewer.Sink.ForgetPose(subject.CharacterGuid);
    }

    private void EvictDepartingVehicleRider(GatewaySessionState state)
    {
        if (state.Fleet is not { } fleet) return;
        if (!_sharedLootMembership.TryGetValue(state, out ulong matchId)
            || !_sharedLootMatches[matchId].Members.TryGetValue(state, out var connection))
        {
            var removed = fleet.Evict(state.Guid);
            if (removed is not null) state.Movement.RemoveManagedEntity(removed.TransientId);
            return;
        }
        foreach (var car in fleet.Vehicles)
            if (car.CoastingOwnerGuid == state.Guid) ReleaseVehicleSimulation(connection, state, car);
        if (!fleet.TryGetForOccupant(state.Guid, out var departing)) return;
        bool driver = departing.DriverGuid == state.Guid;
        ResetVehicleRiderPose(state, departing);
        if (driver)
        {
            ReleaseBoost(connection, state, departing, "driver departed");
            StopVehicleEngineForViewers(connection, state, departing);
            SetVehicleHorn(connection, state, departing, false);
        }
        fleet.Evict(state.Guid);
        if (driver) ReleaseVehicleSimulation(connection, state, departing);
        PublishVehicleOccupants(connection, state, departing);
    }

    private AddLightweightVehicle VehicleSpawn(MatchVehicle car) => new(
        car.Guid, car.TransientId, car.Definition.ModelId, car.Position, car.Rotation,
        car.Definition.VehicleId, OwnerGuid: 0,
        PositionUpdate: _options.VehiclePositionBlock
            ? PositionUpdateBlock.AtRest(car.Position, car.Yaw, car.LastRotation) with
                { Byte6 = car.MovementVersion, SequenceTime = car.LastClientTime } : null,
        SpawnFlags1: _options.VehicleSpawnFlags1,
        BodyShaderGroupId: _options.VehicleShader
            ? car.SkinShaderGroup ?? VehicleShaderGroups.For(car.Definition.VehicleId) : 0,
        RenderDistance: _options.VehicleRenderDistance);
}
