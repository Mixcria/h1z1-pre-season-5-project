using System.Buffers.Binary;
using System.Net;
using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

public sealed class LatestMessageTests
{
    private sealed class Capture : ISoeService, ITransportLog
    {
        public List<byte[]> Messages = [];
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection c) { }
        public void OnDisconnected(SoeConnection c, DisconnectCause cause) { }
        public void OnMessage(SoeConnection c, Span<byte> bytes) => Messages.Add(bytes.ToArray());
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }

    private static SoeConnection Connection(Capture service, List<byte[]> sent)
    {
        var request = new SessionRequest(3, 7, 512, "Test");
        return new(new(IPAddress.Loopback, 1234), in request, new(), SessionDecision.Encrypted([1, 2, 3, 4]),
            service, service, (_, bytes) => sent.Add(bytes.ToArray()), Environment.TickCount64);
    }

    [Fact]
    public void ReplacedPlaintextDoesNotConsumeCipherAndCriticalEventsStayAheadOfPendingState()
    {
        var capture = new Capture(); var sent = new List<byte[]>();
        var sender = Connection(capture, sent); var receiver = Connection(capture, []);
        sender.SendLatest(1, [5, 0x78, 10]);
        sender.SendLatest(1, [5, 0x78, 11]);
        sender.Send([5, 0x11, 99]);
        sender.Tick(Environment.TickCount64);
        sender.Send([5, 0x70, 4]);
        foreach (var packet in sent.AsEnumerable().Reverse()) receiver.HandleDatagram(packet.ToArray(), Environment.TickCount64);
        Assert.Equal(3, capture.Messages.Count);
        Assert.Equal(new byte[] { 5, 0x11, 99 }, capture.Messages[0]);
        Assert.Equal(new byte[] { 5, 0x78, 11 }, capture.Messages[1]);
        Assert.Equal(new byte[] { 5, 0x70, 4 }, capture.Messages[2]);
        Assert.Equal(1, sender.LatestMessagesReplaced);
        Assert.Equal(1, sender.LatestMessagesCommitted);
        sender.Disconnect(); receiver.Disconnect();
    }

    [Fact]
    public void AnUnacknowledgedBacklogDefersMovementButKeepsTheLatestVersionForEveryEntity()
    {
        var capture = new Capture(); var sent = new List<byte[]>();
        var sender = Connection(capture, sent); var receiver = Connection(capture, []);
        for (int i = 0; i < 48; i++) sender.Send([5, 0x11, (byte)i]);
        for (int version = 0; version < 100; version++)
            for (ulong id = 1; id <= 150; id++) sender.SendLatest(id, [5, 0x78, (byte)id, (byte)version]);
        sender.Tick(Environment.TickCount64);
        Assert.Equal(32, sent.Count);
        Assert.Equal(150, sender.PendingLatestMessages);
        Assert.Equal(150, sender.RetainedLatestMessages);
        byte[] ack = [0, (byte)SoeOpcode.Ack, 0, 31];
        sender.HandleDatagram(ack, Environment.TickCount64);
        sender.Tick(Environment.TickCount64);
        foreach (var packet in sent) receiver.HandleDatagram(packet.ToArray(), Environment.TickCount64);
        var poses = capture.Messages.Where(p => p[1] == 0x78).ToArray();
        Assert.Equal(150, poses.Length);
        Assert.All(poses, p => Assert.Equal(99, p[3]));
        Assert.Equal(150, poses.Select(p => p[2]).Distinct().Count());
        Assert.Equal(0, sender.PendingLatestMessages);
        sender.Disconnect(); receiver.Disconnect();
    }

    [Fact]
    public void HealthyInFlightTrafficDoesNotStallMovementWaitingForWanAcknowledgements()
    {
        var capture = new Capture(); var sent = new List<byte[]>();
        var sender = Connection(capture, sent); var receiver = Connection(capture, []);
        for (int i = 0; i < 16; i++) sender.Send([5, 0x11, (byte)i]);
        sender.SendLatest(1, [5, 0x78, 99]);
        sender.Tick(Environment.TickCount64);
        foreach (var packet in sent) receiver.HandleDatagram(packet.ToArray(), Environment.TickCount64);
        Assert.Contains(capture.Messages, message => message.SequenceEqual(new byte[] { 5, 0x78, 99 }));
        Assert.Equal(0, sender.PendingLatestMessages);
        sender.Disconnect(); receiver.Disconnect();
    }

    [Fact]
    public void ForgetAndExplicitBarriersPreventStaleStateAfterEntityRemovalOrWireModeChange()
    {
        var capture = new Capture(); var sent = new List<byte[]>();
        var sender = Connection(capture, sent); var receiver = Connection(capture, []);
        sender.SendLatest(1, [5, 0x78, 1]);
        sender.ForgetLatest(1);
        sender.Send([5, 0x0f, 1]);
        sender.SendLatest(2, [5, 0x78, 2]);
        sender.FlushLatest(2);
        sender.Send([5, 0x78, 3]);
        sender.SendLatest(3, [5, 0x78, 4]);
        sender.ForgetAllLatest();
        sender.Tick(Environment.TickCount64);
        foreach (var packet in sent) receiver.HandleDatagram(packet.ToArray(), Environment.TickCount64);
        Assert.Equal(3, capture.Messages.Count);
        Assert.Equal(new byte[] { 5, 0x0f, 1 }, capture.Messages[0]);
        Assert.Equal(new byte[] { 5, 0x78, 2 }, capture.Messages[1]);
        Assert.Equal(new byte[] { 5, 0x78, 3 }, capture.Messages[2]);
        Assert.Equal(0, sender.RetainedLatestMessages);
        sender.Disconnect(); receiver.Disconnect();
    }

    [Fact]
    public void LatestStateStorageHasFixedEntityAndMessageBounds()
    {
        var capture = new Capture(); var sender = Connection(capture, []);
        for (ulong i = 0; i < 512; i++) sender.SendLatest(i, new byte[256]);
        Assert.Throws<InvalidOperationException>(() => sender.SendLatest(512, [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => sender.SendLatest(0, new byte[257]));
        Assert.Equal(512, sender.RetainedLatestMessages);
        sender.Disconnect();
        Assert.Equal(0, sender.PendingLatestMessages);
        Assert.Equal(0, sender.RetainedLatestMessages);
    }
}
