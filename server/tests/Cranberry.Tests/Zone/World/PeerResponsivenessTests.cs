using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

public sealed class PeerResponsivenessTests
{
    [Fact]
    public void EveryPoseReachesKnownViewersWithoutEchoingOrCrossingMatches()
    {
        var log = new SilentLog();
        var service = new ZoneService(log, log, new GatewayTicketRegistry());
        var subject = Join(service, 1, 7);
        var viewer = Join(service, 2, 7);
        var otherMatch = Join(service, 3, 8);
        service.PeerRegistry.Sweep(viewer, [], []);
        service.PeerRegistry.Sweep(otherMatch, [], []);
        MethodInfo relay = typeof(ZoneService).GetMethod("RelayPeerPose", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int record = 1; record <= 4; record++)
        {
            subject.MovementRecords = record;
            subject.SetPose([(byte)record, 2, 3, 4]);
            relay.Invoke(service, [subject]);
            var sent = ((Sink)viewer.Sink).Sent;
            Assert.Equal(record, sent.Count);
            Assert.Equal(0x78, sent[^1][0]);
            Assert.Equal(subject.Pose.ToArray(), sent[^1][^4..]);
        }
        Assert.Empty(((Sink)subject.Sink).Sent);
        Assert.Empty(((Sink)otherMatch.Sink).Sent);
    }

    private static PeerSession Join(ZoneService service, ulong guid, ulong match)
    {
        var peer = service.PeerRegistry.Register(guid, new Sink());
        peer.InMatch = true;
        peer.MatchId = match;
        peer.ModelId = 9469;
        peer.Position = Vector3.Zero;
        peer.SetPose([1, 2, 3, 4]);
        return peer;
    }

    private sealed class Sink : IPeerSink
    {
        public bool IsOpen => true;
        public List<byte[]> Sent { get; } = [];
        public void Send(byte[] packet) => Sent.Add(packet);
    }

    private sealed class SilentLog : ITransportLog, IPacketRecorder
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(System.Net.IPEndPoint remote, in SessionRequest request) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
    }
}
