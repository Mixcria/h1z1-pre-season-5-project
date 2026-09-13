using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

/// <summary>A real UDP socket against the listener on loopback: handshake, one message, echo, ack.</summary>
public class ListenerLoopbackTests
{
    private sealed class CountingService(int expected) : ISoeService
    {
        public readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly HashSet<int> Received = [];
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection connection) { }
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) { }
        public void OnMessage(SoeConnection connection, Span<byte> message)
        {
            if (message.Length == 5) Received.Add(BinaryPrimitives.ReadInt32LittleEndian(message[1..]));
            if (Received.Count == expected) Finished.TrySetResult();
        }
    }

    [Fact]
    public async Task ReorderedBurstProducesBoundedGapReportsThenCumulativeProgress()
    {
        const int count = 80;
        var service = new CountingService(count);
        using var listener = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), service, new SilentLog());
        listener.Start();
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Client.ReceiveBufferSize = 1 << 20;
        client.Connect(listener.LocalEndPoint);
        client.Send([0, 1, 0, 0, 0, 3, 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 2, 0, (byte)'T', 0]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = await client.ReceiveAsync(deadline.Token);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        listener.Post(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(2)); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        byte[] Packet(int sequence)
        {
            byte[] bytes = new byte[9]; bytes[1] = 9; bytes[4] = 0x46;
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)sequence);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), sequence);
            return bytes;
        }
        try { for (int i = 1; i < count; i++) client.Send(Packet(i)); }
        finally { release.Set(); }
        byte[] gap = (await client.ReceiveAsync(deadline.Token)).Buffer;
        Assert.Equal((byte)SoeOpcode.OutOfOrder, gap[1]);
        client.Send(Packet(0));
        int reports = 1;
        while (true)
        {
            byte[] packet = (await client.ReceiveAsync(deadline.Token)).Buffer;
            if (packet[1] == (byte)SoeOpcode.OutOfOrder) { reports++; continue; }
            Assert.Equal((byte)SoeOpcode.Ack, packet[1]);
            Assert.Equal(count - 1, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
            break;
        }
        await service.Finished.Task.WaitAsync(deadline.Token);
        Assert.Equal(count, service.Received.Count);
        Assert.InRange(reports, 1, 4); // A few bounded passes, not one resend request per packet.
    }

    [Fact]
    public async Task IncomingBurstMakesProgressWhilePostedWorkRemainsBusy()
    {
        const int count = 1024;
        var service = new CountingService(count);
        using var listener = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), service, new SilentLog());
        listener.Start();
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Connect(listener.LocalEndPoint);
        client.Send([0, 1, 0, 0, 0, 3, 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 2, 0, (byte)'T', 0]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        Assert.Equal((byte)SoeOpcode.SessionReply, (await client.ReceiveAsync(deadline.Token)).Buffer[1]);
        using var busy = new CancellationTokenSource();
        int posted = 0;
        void Work()
        {
            if (busy.IsCancellationRequested) return;
            Interlocked.Increment(ref posted);
            Thread.Sleep(5);
            listener.Post(Work);
        }
        listener.Post(Work);
        try
        {
            byte[] packet = new byte[5]; packet[0] = 0x46;
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(1), i);
                client.Send(packet);
            }
            await service.Finished.Task.WaitAsync(deadline.Token);
            Assert.True(Volatile.Read(ref posted) > 0);
            Assert.Equal(count, service.Received.Count);
        }
        finally { busy.Cancel(); }
    }

    private sealed class EchoService : ISoeService
    {
        public List<string> Events { get; } = new();

        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request)
        {
            Events.Add($"request {request.ProtocolName}");
            return SessionDecision.Clear;
        }

        public void OnConnected(SoeConnection connection) => Events.Add("connected");

        public void OnMessage(SoeConnection connection, Span<byte> message)
        {
            Events.Add($"message {message.Length}");
            connection.Send(message);
        }

        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) => Events.Add($"disconnected {cause}");
    }

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;

        public void Log(TransportLogLevel level, string message)
        {
        }
    }

    private sealed class RecordingLog : ITransportLog
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public bool IsEnabled(TransportLogLevel level) => true;
        public void Log(TransportLogLevel level, string message) => Messages.Enqueue(message);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(2048)]
    public void MalformedUdpLogsTheBoundedOuterFramingAndStillDisconnects(int datagramLength)
    {
        var service = new EchoService();
        var log = new RecordingLog();
        using var listener = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), service, log);
        listener.Start();
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Client.ReceiveTimeout = 3000;
        client.Connect(listener.LocalEndPoint);
        byte[] request = [0, 1, 0, 0, 0, 3, 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 2, 0, (byte)'T', 0];
        client.Send(request);
        IPEndPoint from = new(IPAddress.Any, 0);
        Assert.Equal((byte)SoeOpcode.SessionReply, client.Receive(ref from)[1]);

        byte[] malformed = Enumerable.Repeat((byte)0xAB, datagramLength).ToArray();
        malformed[0] = 0;
        malformed[1] = (byte)SoeOpcode.Multi;
        malformed[2] = 1; // A child cannot hold its two-byte transport opcode.
        client.Send(malformed);
        byte[] reply = client.Receive(ref from);
        Assert.Equal((byte)SoeOpcode.Disconnect, reply[1]);
        Assert.Equal(DisconnectReason.ProtocolError, DisconnectPacket.Parse(reply.AsSpan(2)).Reason);
        listener.Stop();

        string warning = Assert.Single(log.Messages, message => message.Contains("udpPrefix=", StringComparison.Ordinal));
        int captured = Math.Min(datagramLength, 512);
        Assert.Contains($"udpBytes={datagramLength} udpPrefixBytes={captured}", warning, StringComparison.Ordinal);
        Assert.EndsWith("udpPrefix=" + Convert.ToHexString(malformed.AsSpan(0, captured)), warning, StringComparison.Ordinal);
        Assert.DoesNotContain(service.Events, entry => entry.StartsWith("message ", StringComparison.Ordinal));
    }

    [Fact]
    public void HandshakeThenEchoOverLoopback()
    {
        var service = new EchoService();
        using var listener = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), service, new SilentLog());
        listener.Start();

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Client.ReceiveTimeout = 3000;
        var server = listener.LocalEndPoint;

        byte[] request =
        [
            0x00, 0x01,
            0x00, 0x00, 0x00, 0x03,
            0xCA, 0xFE, 0xBA, 0xBE,
            0x00, 0x00, 0x02, 0x00,
            (byte)'T', (byte)'e', (byte)'s', (byte)'t', (byte)'U', (byte)'d', (byte)'p', 0x00,
        ];
        client.Send(request, request.Length, server);

        IPEndPoint from = new(IPAddress.Any, 0);
        byte[] reply = client.Receive(ref from);
        Assert.Equal(SessionReply.Length, reply.Length);
        Assert.Equal((ushort)SoeOpcode.SessionReply, BinaryPrimitives.ReadUInt16BigEndian(reply));
        Assert.Equal(0xCAFEBABEu, BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(2)));

        byte[] payload = [0xAA, 0xBB, 0xCC];
        byte[] data = [0x00, 0x09, 0x00, 0x00, .. payload];
        client.Send(data, data.Length, server);

        byte[] a = client.Receive(ref from);
        byte[] b = client.Receive(ref from);
        byte[] ack = a[1] == (byte)SoeOpcode.Ack ? a : b;
        byte[] echo = a[1] == (byte)SoeOpcode.Data ? a : b;

        Assert.Equal(new byte[] { 0x00, 0x15, 0x00, 0x00 }, ack);
        Assert.Equal(new byte[] { 0x00, 0x09, 0x00, 0x00, 0xAA, 0xBB, 0xCC }, echo);

        byte[] ackTheEcho = [0x00, 0x15, 0x00, 0x00];
        client.Send(ackTheEcho, ackTheEcho.Length, server);

        byte[] disconnect = [0x00, 0x05, 0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x06];
        client.Send(disconnect, disconnect.Length, server);

        SpinWait.SpinUntil(() => service.Events.Contains("disconnected PeerRequested"), 2000);
        listener.Stop();

        Assert.Equal(["request TestUdp", "connected", "message 3", "disconnected PeerRequested"], service.Events);
    }
}
