using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Cranberry.Transport;
using Xunit.Abstractions;

namespace Cranberry.Tests.Transport;

public sealed class Capacity150Tests(ITestOutputHelper output)
{
    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }

    private sealed class Echo : ISoeService
    {
        public bool Fanout;
        private readonly List<SoeConnection> _peers = [];
        public int Opened;
        public int Received;
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection c) { _peers.Add(c); Interlocked.Increment(ref Opened); }
        public void OnDisconnected(SoeConnection c, DisconnectCause cause) { }
        public void OnMessage(SoeConnection c, Span<byte> bytes)
        {
            if (Fanout)
            {
                foreach (var peer in _peers) if (peer != c) peer.SendBuffered(bytes);
            }
            else c.Send(bytes);
            Interlocked.Increment(ref Received);
        }
    }

    private sealed class Capture : ISoeService
    {
        public List<byte[]> Messages { get; } = [];
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection c) { }
        public void OnDisconnected(SoeConnection c, DisconnectCause cause) { }
        public void OnMessage(SoeConnection c, Span<byte> bytes) => Messages.Add(bytes.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BufferedMessagesSurviveReorderingDuplicationAndNormalSendBarriers(bool encrypted)
    {
        byte[] key = [1, 2, 3, 4, 5];
        var capture = new Capture();
        var request = new SessionRequest(3, 1, 512, "Test");
        var decision = encrypted ? SessionDecision.Encrypted(key) : SessionDecision.Clear;
        var datagrams = new List<byte[]>();
        var settings = new SessionSettings();
        var sender = new OutboundChannel(settings, d => datagrams.Add(d.ToArray())) { SendWindow = 4096 };
        var cipher = encrypted ? new Rc4Cipher(key) : null;
        var receiver = new SoeConnection(new(IPAddress.Loopback, 1), in request, settings,
            decision, capture, new SilentLog(), (_, _) => { }, 0);
        var expected = new List<byte[]>();
        for (int n = 0; n < 1000; n++)
        {
            byte[] message = new byte[n % 11 == 0 ? 255 : 24];
            BinaryPrimitives.WriteInt32LittleEndian(message, n);
            expected.Add(message);
            if (n % 31 == 0) sender.Send(message, cipher, 0);
            else sender.SendBuffered(message, cipher, 0);
        }
        sender.Tick(20);
        Assert.True(datagrams.Count < 200, $"Expected batching, got {datagrams.Count} packets.");
        foreach (var packet in datagrams.AsEnumerable().Reverse()) receiver.HandleDatagram(packet.ToArray(), 20);
        foreach (var packet in datagrams) receiver.HandleDatagram(packet.ToArray(), 20);
        Assert.Equal(expected.Count, capture.Messages.Count);
        for (int n = 0; n < expected.Count; n++) Assert.Equal(expected[n], capture.Messages[n]);
        sender.Close(); receiver.Disconnect();
    }

    [Fact]
    public void BacklogRejectsWholeMessageBeforeCipherOrSequenceAdvances()
    {
        var channel = new OutboundChannel(new(), _ => { }) { MaxPendingDatagrams = 4 };
        var cipher = new Rc4Cipher([1, 2, 3, 4]);
        channel.Send(new byte[1000], cipher, 0);
        long before = cipher.Position;
        ushort sequence = channel.NextSequence;
        long bytes = channel.PendingBytes;
        Assert.Throws<SoeProtocolException>(() => channel.Send(new byte[2000], cipher, 0));
        Assert.Equal(before, cipher.Position);
        Assert.Equal(sequence, channel.NextSequence);
        Assert.Equal(bytes, channel.PendingBytes);
        channel.Acknowledge(1, 1);
        Assert.Equal(0, channel.PendingBytes);
        channel.Send([1], cipher, 2);
        channel.Close();
        Assert.Equal(0, channel.PendingBytes);
    }

    [Fact]
    public void RepeatedOutOfOrderSignalsCannotMultiplyTheResendBudget()
    {
        var channel = new OutboundChannel(new(), _ => { });
        for (int n = 0; n < channel.SendWindow; n++) channel.Send([1], null, 0);
        for (int n = 0; n < 10000; n++) channel.ResendBefore((ushort)(channel.SendWindow - 1), 300);
        channel.Tick(300);
        Assert.InRange(channel.DatagramsResent, 1, channel.MaxResendsPerTick);
        long resends = channel.DatagramsResent;
        channel.ResendBefore(1000, 320);
        Assert.Equal(resends, channel.DatagramsResent);
        channel.Close();
    }

    [Fact]
    public void AStalledPeerIsClosedWithoutStoppingAHealthyConnection()
    {
        var service = new Capture();
        var request = new SessionRequest(3, 1, 512, "Test");
        var slow = new SoeConnection(new(IPAddress.Loopback, 1), in request, new(),
            SessionDecision.Clear, service, new SilentLog(), (_, _) => { }, 0);
        var healthy = new SoeConnection(new(IPAddress.Loopback, 2), in request, new(),
            SessionDecision.Clear, service, new SilentLog(), (_, _) => { }, 0);
        byte[] load = new byte[256000];
        for (ushort n = 0; n < 100; n++)
        {
            slow.Send(load);
            healthy.Send([1, 2, 3]);
            healthy.HandleDatagram([0, 0x15, (byte)(n >> 8), (byte)n], n);
        }
        Assert.Equal(ConnectionState.Closed, slow.State);
        Assert.Equal(0, slow.PendingBytes);
        Assert.Equal(ConnectionState.Open, healthy.State);
        Assert.Equal(100, healthy.MessagesSent);
        Assert.Equal(0, healthy.PendingDatagrams);
        healthy.Disconnect();
    }

    [Fact]
    public void NestedMultipacketsCannotExhaustTheListenerStack()
    {
        var request = new SessionRequest(3, 1, 512, "Test");
        var receiver = new SoeConnection(new(IPAddress.Loopback, 1), in request, new(),
            SessionDecision.Clear, new Capture(), new SilentLog(), (_, _) => { }, 0);
        byte[] nested = [0, 6];
        for (int n = 0; n < 20; n++) nested = [0, 3, (byte)nested.Length, .. nested];
        Assert.Throws<SoeProtocolException>(() => receiver.HandleDatagram(nested, 0));
        receiver.Disconnect();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneHundredFiftyUdpSessionsExchangeReliableTrafficAndRetryHandshake(bool fanout)
    {
        var service = new Echo { Fanout = fanout };
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), service, new SilentLog());
        listener.Start();
        var clients = new List<UdpClient>();
        var receivers = new List<(SoeConnection Connection, Capture Capture)>();
        try
        {
            byte[] request = [0, 1, 0, 0, 0, 3, 1, 2, 3, 4, 0, 0, 2, 0, (byte)'T', 0];
            for (int n = 0; n < 150; n++)
            {
                var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                clients.Add(client);
                client.Client.ReceiveTimeout = 5000;
                client.Connect(listener.LocalEndPoint);
                client.Send(request);
                IPEndPoint from = new(IPAddress.Any, 0);
                Assert.Equal((byte)SoeOpcode.SessionReply, client.Receive(ref from)[1]);
                var capture = new Capture();
                var session = new SessionRequest(3, 0x01020304, 512, "T");
                var receiver = new SoeConnection(listener.LocalEndPoint, in session, new(),
                    SessionDecision.Clear, capture, new SilentLog(),
                    (_, bytes) => { client.Send(bytes.Span); }, 0);
                receivers.Add((receiver, capture));
            }
            clients[0].Send(request);
            IPEndPoint retryFrom = new(IPAddress.Any, 0);
            Assert.Equal((byte)SoeOpcode.SessionReply, clients[0].Receive(ref retryFrom)[1]);
            Assert.Equal(150, Volatile.Read(ref service.Opened));

            var elapsed = new List<double>();
            var watch = Stopwatch.StartNew();
            for (ushort tick = 0; tick < 100; tick++)
            {
                long start = Stopwatch.GetTimestamp();
                byte[] packet = new byte[36];
                packet[1] = 9; packet[2] = (byte)(tick >> 8); packet[3] = (byte)tick;
                packet[4] = 0x78; packet[5] = (byte)tick; packet[6] = 0x42;
                foreach (var client in clients) client.Send(packet);
                for (int index = 0; index < clients.Count; index++)
                {
                    var client = clients[index];
                    var (receiver, capture) = receivers[index];
                    capture.Messages.Clear();
                    IPEndPoint from = new(IPAddress.Any, 0);
                    bool replayed = false;
                    while (capture.Messages.Count < (fanout ? 149 : 1))
                    {
                        byte[] received = client.Receive(ref from);
                        if (received[1] != (byte)SoeOpcode.Data) continue;
                        // These raw sockets must behave like reliable clients: late retries
                        // are acknowledged and discarded by sequence, not counted as new
                        // application traffic. Under a loaded full suite a previous tick's
                        // retransmission otherwise makes this test falsely report corruption.
                        receiver.HandleDatagram(received, Environment.TickCount64);
                        if (tick == 0 && !replayed)
                        {
                            receiver.HandleDatagram(received, Environment.TickCount64);
                            replayed = true;
                        }
                    }
                    Assert.Equal(fanout ? 149 : 1, capture.Messages.Count);
                    Assert.All(capture.Messages, message => Assert.True(message.AsSpan().SequenceEqual(packet.AsSpan(4))));
                }
                elapsed.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            Assert.Equal(15000, Volatile.Read(ref service.Received));
            elapsed.Sort();
            output.WriteLine($"150 UDP clients, fanout={fanout}, {15000L * (fanout ? 149 : 1):N0} reliable deliveries in {watch.Elapsed.TotalSeconds:F2}s; batch p50={elapsed[50]:F2}ms p95={elapsed[95]:F2}ms max={elapsed[^1]:F2}ms.");
        }
        finally
        {
            foreach (var (receiver, _) in receivers) receiver.Disconnect();
            foreach (var client in clients) client.Dispose();
        }
    }
}
