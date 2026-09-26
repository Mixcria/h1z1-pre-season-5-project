using System.Net;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.World;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.World;

public sealed class PeerFanoutTranscriptTests
{
    // Hand-authored wire prefixes, independent of the production varint writer.
    private static readonly (uint Id, byte[] Prefix)[] Ids =
    [
        (63, [0xfc]), (64, [0x01, 0x01]),
        (16383, [0xfd, 0xff]), (16384, [0x02, 0x00, 0x01]),
        (4194303, [0xfe, 0xff, 0xff]), (4194304, [0x03, 0x00, 0x00, 0x01]),
        (1073741823, [0xff, 0xff, 0xff, 0xff]),
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealSessionSinkPreservesSparseRecordsAndRecipientWidthsInOrder(bool diagnostics)
    {
        using var f = new Fixture(diagnostics);
        var subject = f.Add();
        foreach (var (id, _) in Ids) f.Observe(f.Add(), subject, id);
        byte[][] records =
        [
            [0, 0, 100, 0, 0, 0, 5], // header only, no synthesized fields
            [1, 0, 101, 0, 0, 0, 6, 1, 1], // two-byte stop flag
            [0, 0x80, 102, 0, 0, 0, 6, 0xaa, 0xbb], // opaque sparse bit/tail
            [1, 0, 103], // incomplete header remains an ordered opaque record
        ];
        foreach (byte[] record in records) f.Relay(subject, record);
        Assert.Equal(records.Length * Ids.Length, f.Log.Messages.Count);
        for (int row = 0; row < records.Length; row++)
            for (int recipient = 0; recipient < Ids.Length; recipient++)
            {
                var actual = f.Log.Messages[row * Ids.Length + recipient];
                Assert.Same(f.Connections[recipient + 1], actual.Connection);
                byte[] expected = [5, 0x78, .. Ids[recipient].Prefix, .. records[row]];
                Assert.Equal(expected, actual.Bytes);
            }
        Assert.DoesNotContain(f.Log.Messages, m => ReferenceEquals(m.Connection, f.Connections[0]));
        if (diagnostics)
        {
            var snapshot = JsonSerializer.SerializeToElement(f.Service.CaptureDiagnostics(16));
            var replication = snapshot.GetProperty("Replication");
            Assert.Equal(28, replication.GetProperty("OrderedRecordOffers").GetInt64());
            Assert.Equal(0, replication.GetProperty("CompleteSnapshotOffers").GetInt64());
            var phase = Assert.Single(snapshot.GetProperty("MovementWire")
                .GetProperty("PeerPoseOffersByReceiverPhase").EnumerateArray());
            Assert.Equal(21, phase.GetProperty("Records").GetInt64());
            Assert.Equal(7, phase.GetProperty("VersionChanges").GetInt64());
            Assert.Equal(7, phase.GetProperty("HeaderOnlyRecords").GetInt64());
            Assert.Equal(7, phase.GetProperty("StopFlagRecords").GetInt64());
        }
    }

    [Fact]
    public void BudgetExcludesGatewayAndStopsAtFirstUnaffordableRecipientWithoutSkipping()
    {
        using var f = new Fixture(true, budget: 19);
        var subject = f.Add();
        var a = f.Add(); var b = f.Add(); var c = f.Add();
        f.Observe(a, subject, 63); // nine zone bytes
        f.Observe(b, subject, 4194304); // twelve zone bytes
        f.Observe(c, subject, 62); // nine zone bytes; would fit after a, must not bypass b
        byte[] first = [0, 0, 1, 0, 0, 0, 0];
        f.Relay(subject, first);
        Assert.Equal(new byte[] { 5, 0x78, 0xfc, 0, 0, 1, 0, 0, 0, 0 }, Assert.Single(f.Log.Messages).Bytes);
        Assert.Equal(1, subject.RelayCursor);
        f.Log.Messages.Clear();
        byte[] second = [0, 0, 2, 0, 0, 0, 0];
        f.Relay(subject, second);
        Assert.Same(f.Connections[2], Assert.Single(f.Log.Messages).Connection);
        Assert.Equal((byte[])[5, 0x78, 3, 0, 0, 1, .. second], f.Log.Messages[0].Bytes);
        Assert.Equal(2, subject.RelayCursor);
    }

    [Fact]
    public void ExactBudgetFitSendsTwoRecipientsAndResumesAtDeferredThird()
    {
        using var f = new Fixture(false, budget: 19);
        var subject = f.Add();
        f.Observe(f.Add(), subject, 63); // 9
        f.Observe(f.Add(), subject, 64); // 10
        f.Observe(f.Add(), subject, 65); // 10
        byte[] record = [0, 0, 1, 0, 0, 0, 0];
        f.Relay(subject, record);
        Assert.Equal(2, f.Log.Messages.Count);
        Assert.Equal(2, subject.RelayCursor);
        f.Log.Messages.Clear();
        f.Relay(subject, record);
        Assert.Equal(2, f.Log.Messages.Count);
        Assert.Same(f.Connections[3], f.Log.Messages[0].Connection);
        Assert.Same(f.Connections[1], f.Log.Messages[1].Connection);
        Assert.Equal(1, subject.RelayCursor);
    }

    [Fact]
    public void OrderedRecordFlushesLatestSnapshotBeforeItAndRecorderOwnsBytes()
    {
        using var f = new Fixture(true);
        var recipient = f.Add();
        byte[] snapshot = [0, 0, 10, 0, 0, 0, 1];
        byte[] sparse = [0, 0, 11, 0, 0, 0, 2];
        recipient.Sink.SendPose(50, 64, snapshot, true);
        Assert.Empty(f.Log.Messages);
        recipient.Sink.SendPose(50, 64, sparse, false);
        Assert.Equal(2, f.Log.Messages.Count);
        Assert.Equal((byte[])[5, 0x78, 1, 1, .. snapshot], f.Log.Messages[0].Bytes);
        Assert.Equal((byte[])[5, 0x78, 1, 1, .. sparse], f.Log.Messages[1].Bytes);
        Array.Fill(snapshot, (byte)0xff); Array.Fill(sparse, (byte)0xff);
        Assert.Equal(10, f.Log.Messages[0].Bytes[6]);
        Assert.Equal(11, f.Log.Messages[1].Bytes[6]);
    }

    [Fact]
    public void RecorderCallbackChangingReusablePoseKeepsLaterDiagnosticsAlignedWithEmittedBytes()
    {
        using var f = new Fixture(true);
        var subject = f.Add();
        f.Observe(f.Add(), subject, 63);
        f.Observe(f.Add(), subject, 64);
        byte[] initial = [0, 0, 1, 0, 0, 0, 3];
        byte[] changed = [0, 0, 2, 0, 0, 0, 9];
        f.Log.AfterRecord = () => { f.Log.AfterRecord = null; subject.SetPose(changed); };
        f.Relay(subject, initial);
        Assert.Equal((byte[])[5, 0x78, 0xfc, .. initial], f.Log.Messages[0].Bytes);
        Assert.Equal((byte[])[5, 0x78, 1, 1, .. changed], f.Log.Messages[1].Bytes);
        var snapshot = JsonSerializer.SerializeToElement(f.Service.CaptureDiagnostics(16));
        var phase = Assert.Single(snapshot.GetProperty("MovementWire")
            .GetProperty("PeerPoseOffersByReceiverPhase").EnumerateArray());
        Assert.Equal(2, phase.GetProperty("Records").GetInt64());
        Assert.Equal(3, phase.GetProperty("FirstVersion").GetInt64());
        Assert.Equal(9, phase.GetProperty("LastVersion").GetInt64());
        Assert.Equal(2, phase.GetProperty("LastClientTime").GetInt64());
    }

    private sealed class Fixture : IDisposable
    {
        public Recorder Log { get; } = new();
        public ZoneService Service { get; }
        public List<SoeConnection> Connections { get; } = [];
        public Fixture(bool diagnostics, int budget = 16384)
        {
            Service = new(Log, Log, options: new ZoneOptions
            {
                Peers = PeerOptions.Default with { RelayBudgetBytes = budget },
            }) { DiagnosticsEnabled = diagnostics };
        }
        public PeerSession Add()
        {
            uint index = (uint)Connections.Count + 1;
            var request = new SessionRequest(3, index, 512, ZoneService.ProtocolName);
            var c = new SoeConnection(new(IPAddress.Loopback, 16000 + (int)index), in request,
                new(), SessionDecision.Clear, Service, Log, (_, _) => { }, 0);
            Service.OnConnected(c);
            Connections.Add(c);
            var type = typeof(ZoneService).GetNestedType("SessionPeerSink", BindingFlags.NonPublic)!;
            var sink = (IPeerSink)Activator.CreateInstance(type,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: [Service, c], culture: null)!;
            var peer = Service.PeerRegistry.Register(1000 + index, sink);
            peer.InMatch = true; peer.MatchId = 7; peer.Position = Vector3.Zero;
            return peer;
        }
        public void Observe(PeerSession viewer, PeerSession subject, uint id)
        {
            // Seed the allocator at a width boundary without millions of dummy entities.
            typeof(TransientIdTable).GetField("_next", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewer.View.Transients, id);
            Assert.Equal(id, viewer.View.Transients.Acquire(subject.Key));
        }
        public void Relay(PeerSession subject, byte[] record)
        {
            subject.MovementRecords++;
            subject.SetPose(record);
            typeof(ZoneService).GetMethod("RelayPeerPose", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Service, [subject]);
        }
        public void Dispose() { foreach (var c in Connections) c.Disconnect(); }
    }
    private sealed class Recorder : ITransportLog, IPacketRecorder
    {
        public List<(SoeConnection Connection, byte[] Bytes)> Messages { get; } = [];
        public Action? AfterRecord { get; set; }
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            if (direction != "s2c") return;
            Messages.Add((connection, bytes.ToArray()));
            AfterRecord?.Invoke();
        }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
    }
}
