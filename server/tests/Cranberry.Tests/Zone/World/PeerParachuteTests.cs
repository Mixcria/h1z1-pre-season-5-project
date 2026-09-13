using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

public sealed class PeerParachuteTests
{
    [Fact]
    public void MountedMovementSpawnsAnObserverCanopyAndUsesItsTransientWithoutGrantingControl()
    {
        var (service, subject, viewer, outsider) = Fixture();
        Call(service, "RelayPeerPose", subject);
        var sent = ((Sink)viewer.Sink).Sent;
        var canopy = Assert.Single(sent, p => p[0] == 0xd7);
        Assert.Equal(subject.ParachuteGuid, BinaryPrimitives.ReadUInt64LittleEndian(canopy.AsSpan(1)));
        Assert.True(viewer.View.Transients.TryGet(new(subject.ParachuteGuid), out uint transient));
        Assert.NotEqual(2u, transient); // 2 remains the observer's own managed parachute.
        Assert.True(viewer.View.Transients.TryGet(subject.Key, out uint riderTransient));
        Assert.NotEqual(riderTransient, transient);
        var mount = Assert.Single(sent, p => p[0] == 0x70 && p[1] == 2);
        Assert.Equal(subject.CharacterGuid, BinaryPrimitives.ReadUInt64LittleEndian(mount.AsSpan(2)));
        Assert.Equal(subject.ParachuteGuid, BinaryPrimitives.ReadUInt64LittleEndian(mount.AsSpan(10)));
        Assert.Equal(PeerBurst.Pose(transient, subject.Pose), sent[^1]);
        Assert.DoesNotContain(sent, p => p[0] == 0x88 && p[1] == 0x19); // AutoMount is owner-only.
        Assert.DoesNotContain(sent, p => p[0] == 0x88 && p[1] is 1 or 2); // Owner/Occupy mutate the receiver's manager.
        Assert.DoesNotContain(sent, p => p[0] == 0x0f && p[1] == 0x3b); // No managed ownership grant.
        Assert.Empty(((Sink)subject.Sink).Sent);
        Assert.Empty(((Sink)outsider.Sink).Sent);
        int definitions = sent.Count(p => p[0] == 0xd7);
        Call(service, "RelayPeerPose", subject);
        Assert.Equal(definitions, sent.Count(p => p[0] == 0xd7));
    }

    [Fact]
    public void AFullVehicleRequestIsAnsweredOnlyWhileTheCanopyIsInThatView()
    {
        var (service, subject, viewer, outsider) = Fixture();
        Call(service, "RelayPeerPose", subject);
        var sent = ((Sink)viewer.Sink).Sent;
        sent.Clear();
        Assert.True((bool)Call(service, "SendPeerParachuteFull", viewer, subject.ParachuteGuid)!);
        Assert.Equal(0xdb, Assert.Single(sent)[0]);
        Assert.False((bool)Call(service, "SendPeerParachuteFull", outsider, subject.ParachuteGuid)!);
        Call(service, "RemovePeerParachute", viewer, subject.CharacterGuid);
        Assert.False((bool)Call(service, "SendPeerParachuteFull", viewer, subject.ParachuteGuid)!);
    }

    [Fact]
    public void CanopyRemovalPrecedesIdReuseAndKeepsTheRiderKnown()
    {
        var (service, subject, viewer, _) = Fixture();
        Call(service, "RelayPeerPose", subject);
        Assert.True(viewer.View.Transients.TryGet(new(subject.ParachuteGuid), out uint transient));
        var sent = ((Sink)viewer.Sink).Sent;
        sent.Clear();
        Call(service, "RemovePeerParachute", viewer, subject.CharacterGuid);
        Assert.Equal(2, sent.Count);
        Assert.Equal(new byte[] { 0x70, 4 }, sent[0][..2]);
        Assert.Equal(PeerBurst.Leave(subject.ParachuteGuid), sent[1]);
        Assert.False(viewer.View.Transients.TryResolve(transient, out _));
        Assert.True(viewer.View.Knows(subject.Key));
        Assert.True(viewer.View.Transients.TryGet(subject.Key, out _));
        Call(service, "RemovePeerParachute", viewer, subject.CharacterGuid);
        Assert.Equal(2, sent.Count); // repeated dismiss/leave is harmless.
    }

    [Fact]
    public void AChangedCanopyRemovesTheOldActorBeforeIntroducingTheNewOne()
    {
        var (service, subject, viewer, _) = Fixture();
        Call(service, "RelayPeerPose", subject);
        ulong previous = subject.ParachuteGuid;
        subject.ParachuteGuid++;
        var sent = ((Sink)viewer.Sink).Sent;
        sent.Clear();
        Call(service, "RelayPeerPose", subject);
        Assert.Equal(new byte[] { 0x70, 4 }, sent[0][..2]);
        Assert.Equal(PeerBurst.Leave(previous), sent[1]);
        Assert.Equal(0xd7, sent[2][0]);
        Assert.Equal(subject.ParachuteGuid, BinaryPrimitives.ReadUInt64LittleEndian(sent[2].AsSpan(1)));
        Assert.False(viewer.View.Transients.TryGet(new(previous), out _));
    }

    [Fact]
    public void DisconnectingInTheAirRemovesTheCanopyBeforeTheRider()
    {
        var (service, subject, viewer, _) = Fixture();
        Call(service, "RelayPeerPose", subject);
        var sent = ((Sink)viewer.Sink).Sent;
        sent.Clear();
        Call(service, "PeerLinkClosed", null, State(subject));
        Assert.Equal(3, sent.Count);
        Assert.Equal(new byte[] { 0x70, 4 }, sent[0][..2]);
        Assert.Equal(PeerBurst.Leave(subject.ParachuteGuid), sent[1]);
        Assert.Equal(PeerBurst.Leave(subject.CharacterGuid), sent[2]);
        Assert.False(viewer.View.Transients.TryGet(new(subject.ParachuteGuid), out _));
        Assert.False(viewer.View.Knows(subject.Key));
        Assert.Equal(2, service.PeerRegistry.Count);
    }

    [Fact]
    public void ReturningToTheMenuClearsTheDepartingRidersCanopyAndItsObservedCanopies()
    {
        var (service, subject, viewer, _) = Fixture();
        viewer.ParachuteGuid = 0x1012;
        service.PeerRegistry.Sweep(subject, [], []);
        Call(service, "RelayPeerPose", subject);
        Call(service, "RelayPeerPose", viewer);
        ulong canopy = subject.ParachuteGuid;
        Call(service, "NotePeerInMatch", State(subject), false);
        Assert.False(subject.InMatch);
        Assert.Equal(0ul, subject.MatchId);
        Assert.Equal(0ul, subject.ParachuteGuid);
        Assert.False(viewer.View.Transients.TryGet(new(canopy), out _));
        Assert.False(subject.View.Transients.TryGet(new(viewer.ParachuteGuid), out _));
    }

    [Fact]
    public void LeavingTheInterestBandRemovesBothRiderAndCanopy()
    {
        var (service, subject, viewer, _) = Fixture();
        Call(service, "RelayPeerPose", subject);
        var sent = ((Sink)viewer.Sink).Sent;
        sent.Clear();
        viewer.Position += new Vector3(3000, 0, 0);
        Call(service, "RunPeerInterest", null, State(viewer), viewer);
        Assert.Equal(3, sent.Count);
        Assert.Equal(new byte[] { 0x70, 4 }, sent[0][..2]);
        Assert.Equal(PeerBurst.Leave(subject.ParachuteGuid), sent[1]);
        Assert.Equal(PeerBurst.Leave(subject.CharacterGuid), sent[2]);
        Assert.False(viewer.View.Transients.TryGet(new(subject.ParachuteGuid), out _));
        Assert.False(viewer.View.Knows(subject.Key));
    }

    private static object State(PeerSession peer)
    {
        Type type = typeof(ZoneService).GetNestedType("GatewaySessionState", BindingFlags.NonPublic)!;
        object state = Activator.CreateInstance(type, nonPublic: true)!;
        type.GetProperty("Guid")!.SetValue(state, peer.CharacterGuid);
        type.GetProperty("Peer")!.SetValue(state, peer);
        return state;
    }

    private static (ZoneService, PeerSession, PeerSession, PeerSession) Fixture()
    {
        var log = new NullRecorder();
        var service = new ZoneService(log, log, new GatewayTicketRegistry());
        PeerSession Join(ulong guid, ulong match)
        {
            var peer = service.PeerRegistry.Register(guid, new Sink());
            peer.InMatch = true; peer.MatchId = match;
            peer.ModelId = 9469; peer.Position = new(100, 500, 100);
            peer.SetPose([0, 0, 1, 0, 0, 0, 0]); peer.MovementRecords = 1;
            return peer;
        }
        var subject = Join(0x1001, 1);
        subject.ParachuteGuid = 0x1002;
        var viewer = Join(0x1011, 1);
        var outsider = Join(0x1021, 2);
        service.PeerRegistry.Sweep(viewer, [], []);
        service.PeerRegistry.Sweep(outsider, [], []);
        return (service, subject, viewer, outsider);
    }

    private static object? Call(ZoneService service, string name, params object?[] args) =>
        typeof(ZoneService).GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(service, args);
    private sealed class Sink : IPeerSink
    {
        public bool IsOpen => true;
        public List<byte[]> Sent { get; } = [];
        public void Send(byte[] packet) => Sent.Add(packet);
    }
    private sealed class NullRecorder : ITransportLog, IPacketRecorder
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
    }
}
