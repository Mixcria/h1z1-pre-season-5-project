using System.Buffers.Binary;
using Cranberry.Transport;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private bool HandleMovementVersionRequest(SoeConnection connection, GatewaySessionState state, ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 2 || packet[0] != 0x0f || packet[1] != 0x57) return false;
        if (packet.Length != 10 || state.Peer is not { IsReplicable: true } viewer) return true;
        ulong guid = BinaryPrimitives.ReadUInt64LittleEndian(packet[2..]);
        var subject = _peers.Find(guid);
        // Never alter the requesting client's local motion/physics owner. Managed canopy/vehicle
        // versions need their own native acceptance; this known reply is scoped to remote infantry.
        if (subject is null || ReferenceEquals(subject, viewer) || !subject.IsReplicable
            || subject.ParachuteGuid != 0 || subject.MatchId != viewer.MatchId || !viewer.View.Knows(subject.Key)) return true;
        long now = Environment.TickCount64;
        if (viewer.MovementVersionReplies.TryGetValue(guid, out var previous)
            && previous.Version == subject.MovementVersion && now - previous.At < 250) return true;
        if (viewer.MovementVersionReplies.Count >= 256) viewer.MovementVersionReplies.Clear();
        viewer.MovementVersionReplies[guid] = (subject.MovementVersion, now);
        connection.FlushLatest(guid); // Retain reliable/RC4 order; never discard committed datagrams.
        SendTunnel(connection, new CharacterMovementVersion(guid, subject.MovementVersion).WriteTo);
        if (_productionDiagnostics is { } diagnostics) diagnostics.MovementVersionReplies++;
        _log.Info($"{connection} peers: movement-version reply guid={guid}, observed version={subject.MovementVersion}");
        return true;
    }
}
