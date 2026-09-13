using Cranberry.Protocol;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // Reuses the existing August vehicle/mount writers. Only the owning character receives
    // AutoMount or managed-control grants. Observers get a distinct transient in their own table.
    private uint EnsurePeerParachute(PeerSession viewer, PeerSession subject)
    {
        if (!_options.Peers.Spawn || subject.ParachuteGuid == 0
            || ReferenceEquals(viewer, subject) || viewer.MatchId != subject.MatchId
            || !viewer.View.Knows(subject.Key))
            return 0;

        if (viewer.VisibleParachutes.TryGetValue(subject.CharacterGuid, out var known))
        {
            if (known.Guid == subject.ParachuteGuid) return known.TransientId;
            RemovePeerParachute(viewer, subject.CharacterGuid);
        }

        ulong guid = subject.ParachuteGuid;
        viewer.Sink.ForgetPose(subject.CharacterGuid);
        uint transient = viewer.View.Transients.Acquire(new EntityId(guid));
        viewer.VisibleParachutes.Add(subject.CharacterGuid, (guid, transient));
        using var writer = new PacketWriter();
        new AddLightweightVehicle(guid, transient, _options.ParachuteModelId, subject.Position,
            subject.Rotation, _options.ParachuteVehicleId, OwnerGuid: subject.CharacterGuid,
            PositionUpdate: PositionUpdateBlock.AtRest(subject.Position, subject.Heading) with
                { Byte6 = subject.MovementVersion },
            ShaderParameterGroupId: _options.ParachuteShaderParameterGroupId).WriteTo(writer);
        viewer.Sink.Send(writer.Written.ToArray());
        if (_options.SendFullVehicleRecord)
        {
            using var full = new PacketWriter();
            new LightweightToFullVehicle(transient, guid,
                Occupants: [new VehicleOccupantSlot(0, subject.CharacterGuid)]).WriteTo(full);
            viewer.Sink.Send(full.Written.ToArray());
        }
        using var mount = new PacketWriter();
        new MountResponse(subject.CharacterGuid, guid).WriteTo(mount);
        viewer.Sink.Send(mount.Written.ToArray());
        // 88/01 clears the local ownership manager before its owner test; 88/02 writes
        // local possession without a rider test. Remote attachment uses only 70/02.
        // 140c5ac20 defers internally while either actor's model/seat data is loading.
        return transient;
    }

    private static void RemovePeerParachute(PeerSession viewer, ulong rider)
    {
        if (!viewer.VisibleParachutes.Remove(rider, out var known)) return;
        viewer.Sink.ForgetPose(known.Guid);
        using var dismount = new PacketWriter();
        new DismountResponse(rider, known.Guid).WriteTo(dismount);
        viewer.Sink.Send(dismount.Written.ToArray());
        viewer.Sink.Send(PeerBurst.Leave(known.Guid));
        // Release only after queuing the removal, before this id can name a different actor.
        viewer.View.Transients.Release(new EntityId(known.Guid));
    }

    private static bool SendPeerParachuteFull(PeerSession viewer, ulong guid)
    {
        foreach (var (rider, known) in viewer.VisibleParachutes)
        {
            if (known.Guid != guid) continue;
            using var writer = new PacketWriter();
            new LightweightToFullVehicle(known.TransientId, guid,
                Occupants: [new VehicleOccupantSlot(0, rider)]).WriteTo(writer);
            viewer.Sink.Send(writer.Written.ToArray());
            return true;
        }
        return false;
    }

    private void EndPeerParachute(GatewaySessionState state)
    {
        if (state.Peer is not { ParachuteGuid: not 0 } subject) return;
        subject.ParachuteGuid = 0;
        subject.ResetRelaySnapshot();
        _peers.CollectViewers(subject, _peerViewers);
        foreach (PeerViewer viewer in _peerViewers)
            RemovePeerParachute(viewer.Viewer, subject.CharacterGuid);
    }
}
