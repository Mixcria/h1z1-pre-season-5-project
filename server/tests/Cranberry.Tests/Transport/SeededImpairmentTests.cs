using System.Buffers.Binary;
using System.Net;
using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

/// <summary>Virtual-time SOE impairment, independent of OS scheduling. Synthetic messages only;
/// exercises the production outbound channel and encrypted connection reader together.</summary>
public sealed class SeededImpairmentTests
{
    [Theory]
    [InlineData(1148, 32)]
    [InlineData(1315, 128)]
    [InlineData(20260912, 32)]
    public void EncryptedBundlesAndFragmentsSurviveLossJitterReorderDuplicationBunchingAndBlackout(int seed, int window)
    {
        using var impaired = new VirtualPeer(seed, window, impaired: true);
        using var healthy = new VirtualPeer(seed + 1, window, impaired: false);
        for (long now = 0; now < 40_000; now += 5)
        {
            impaired.Step(now, generate: now < 4000 && now % 20 == 0);
            healthy.Step(now, generate: now < 4000 && now % 20 == 0);
            if (now == 1800)
                Assert.True(healthy.Received.Count > impaired.Received.Count,
                    "The separate peer must continue during the first peer's blackout.");
            if (now >= 4000 && impaired.Complete && healthy.Complete) break;
        }
        foreach (var peer in new[] { impaired, healthy })
        {
            Assert.True(peer.Complete, $"seed={seed}, window={window}, delivered={peer.Received.Count}/{peer.Expected.Count}");
            Assert.Equal(peer.Expected, peer.Received);
            Assert.Equal(peer.Expected.Sum(message => (long)message.Length), peer.Cipher.Position);
            Assert.False(peer.Outbound.PeerLost);
            Assert.Equal(0, peer.Outbound.PendingBytes);
        }
        Assert.True(impaired.Dropped > 0);
        Assert.True(impaired.Duplicated > 0);
        Assert.True(impaired.Outbound.DatagramsResent > 0);
        var counters = impaired.Diagnostics.TakeCounters();
        Assert.True(counters[nameof(SoeDiagnosticCounter.ReliableAhead)] > 0);
        Assert.True(counters[nameof(SoeDiagnosticCounter.ReliableAheadDuplicate)]
            + counters[nameof(SoeDiagnosticCounter.ReliableBehind)] > 0);
    }

    [Fact]
    public void ExhaustedRetryBudgetCanAcceptLateAckBeforeSilenceDeadlineAndThenResume()
    {
        var sent = new List<byte[]>();
        var sender = new OutboundChannel(new(), bytes => sent.Add(bytes.ToArray()))
        {
            MaxResends = 2, ResendIntervalMs = 100, MinResendIntervalMs = 100,
            MaxResendIntervalMs = 100, PeerSilenceLimitMs = 1000, SendWindow = 1
        };
        try
        {
            sender.Send([1], null, 0); sender.Send([2], null, 0);
            sender.Tick(100); sender.Tick(200); sender.Tick(300);
            Assert.Equal(2, sender.DatagramsResent);
            Assert.False(sender.PeerLost);
            Assert.Equal(sent[0], sent[1]); Assert.Equal(sent[0], sent[2]);
            sender.Acknowledge(0, 900);
            Assert.Equal(4, sent.Count);
            Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(sent[^1].AsSpan(2)));
            sender.Acknowledge(1, 950);
            sender.Tick(2000);
            Assert.False(sender.PeerLost);
            Assert.Equal(0, sender.PendingCount);
        }
        finally { sender.Close(); }
    }

    private sealed class VirtualPeer : IDisposable
    {
        private readonly Random _random;
        private readonly bool _impaired;
        private readonly PriorityQueue<(bool Forward, byte[] Bytes), (long Due, long Order)> _wire = new();
        private readonly Recorder _service = new();
        private readonly SoeConnection _receiver;
        private long _now, _order;
        public readonly SoeDiagnosticCollector Diagnostics = new();
        public readonly OutboundChannel Outbound;
        public readonly Rc4Cipher Cipher;
        public readonly List<byte[]> Expected = [];
        public List<byte[]> Received => _service.Messages;
        public int Dropped, Duplicated;
        public bool Complete => Received.Count == Expected.Count && Outbound.PendingCount == 0 && _wire.Count == 0;

        public VirtualPeer(int seed, int window, bool impaired)
        {
            _random = new(seed); _impaired = impaired;
            byte[] key = [1, 2, 3, 4, (byte)seed];
            Cipher = new(key);
            Outbound = new(new(), bytes => Schedule(true, bytes)) { SendWindow = window };
            var request = new SessionRequest(0, (uint)seed, 512, "Synthetic");
            _receiver = new(new IPEndPoint(IPAddress.Loopback, 1), in request, new(),
                SessionDecision.Encrypted(key), _service, new SilentLog(), (_, bytes) => Schedule(false, bytes), 0);
            _receiver.EnableDiagnostics(Diagnostics, 1);
        }

        public void Step(long now, bool generate)
        {
            _now = now;
            if (generate)
            {
                for (int n = 0; n < 2; n++)
                {
                    int index = Expected.Count;
                    byte[] message = new byte[index % 20 == 0 ? 8192 : 48];
                    for (int i = 0; i < message.Length; i++) message[i] = (byte)(index * 31 + i);
                    Expected.Add(message);
                    Outbound.SendBuffered(message, Cipher, now);
                }
                Outbound.FlushBuffered(now);
            }
            if (now % 20 == 0) Outbound.Tick(now);
            while (_wire.TryPeek(out var item, out var priority) && priority.Due <= now)
            {
                _wire.Dequeue();
                if (item.Forward) _receiver.HandleDatagram(item.Bytes, now);
                else
                {
                    ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(item.Bytes.AsSpan(2));
                    if (item.Bytes[1] == (byte)SoeOpcode.Ack) Outbound.Acknowledge(sequence, now);
                    else if (item.Bytes[1] == (byte)SoeOpcode.OutOfOrder) Outbound.ResendBefore(sequence, now);
                    else Assert.Fail("Unexpected control message on the synthetic reliable link.");
                }
            }
        }

        private void Schedule(bool forward, ReadOnlyMemory<byte> bytes)
        {
            if (_impaired && (_now is >= 1200 and < 2000 || _random.NextDouble() < .03))
            { Dropped++; return; }
            long due = _now + (_impaired ? 40 + _random.Next(-20, 21) : 5);
            if (_impaired && _random.NextDouble() < .15) due = (due + 149) / 150 * 150;
            _wire.Enqueue((forward, bytes.ToArray()), (due, _order++));
            if (_impaired && _random.NextDouble() < .10)
            {
                Duplicated++;
                _wire.Enqueue((forward, bytes.ToArray()), (due + _random.Next(1, 21), _order++));
            }
            Assert.True(_wire.Count < 16384, "Synthetic wire must stay bounded.");
        }

        public void Dispose() { Outbound.Close(); _receiver.Close(DisconnectCause.ServerRequested); }
    }

    private sealed class Recorder : ISoeService
    {
        public readonly List<byte[]> Messages = [];
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection connection) { }
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) { }
        public void OnMessage(SoeConnection connection, Span<byte> message) => Messages.Add(message.ToArray());
    }

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }
}
