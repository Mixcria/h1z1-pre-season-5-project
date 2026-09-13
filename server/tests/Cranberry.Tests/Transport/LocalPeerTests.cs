using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

public sealed class LocalPeerTests
{
    private static readonly byte[] Request = [0, 1, 0, 0, 0, 3, 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 2, 0, (byte)'T', 0];
    private sealed class Echo : ISoeService
    {
        public readonly ConcurrentQueue<byte[]> Received = new();
        public readonly ConcurrentQueue<DisconnectCause> Closed = new();
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection connection) { }
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) => Closed.Enqueue(cause);
        public void OnMessage(SoeConnection connection, Span<byte> message)
        { Received.Enqueue(message.ToArray()); connection.Send(message); }
    }
    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }
    private static async Task Barrier(SoeListener listener)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Post(() => done.SetResult());
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReliableBurstPreservesOrderCopiesInputAndRetiresSession(bool forceRetransmission)
    {
        var service = new Echo();
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), service, new SilentLog());
        listener.Start();
        using var reserved = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var peer = listener.OpenLocalPeer((IPEndPoint)reserved.Client.LocalEndPoint!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        peer.Send(Request);
        Assert.Equal((byte)SoeOpcode.SessionReply, (await peer.ReceiveAsync(deadline.Token))[1]);
        for (ushort i = 0; i < 200; i++)
        {
            byte[] data = [0, 9, 0, 0, 0xAB, 0, 0];
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), i);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(5), i);
            peer.Send(data);
            Array.Fill(data, (byte)0xFF);
        }
        var echoes = new List<ushort>();
        bool acknowledged = false;
        bool withholdingAck = forceRetransmission;
        int retransmissions = 0;
        while (echoes.Count < 200 || !acknowledged)
        {
            byte[] data = await peer.ReceiveAsync(deadline.Token);
            ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
            if (data[1] == (byte)SoeOpcode.Ack) { acknowledged |= sequence == 199; continue; }
            Assert.Equal((byte)SoeOpcode.Data, data[1]);
            Assert.Equal(0xAB, data[4]);
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(5));
            Assert.Equal(sequence, value);
            if (sequence < echoes.Count)
            {
                // A busy test runner can delay ACKs long enough for a valid retry.
                // Its payload must be identical; it is not a new application message.
                Assert.Equal(echoes[sequence], value);
                retransmissions++;
                withholdingAck = false;
            }
            else
            {
                Assert.Equal(echoes.Count, sequence);
                echoes.Add(value);
            }
            if (!withholdingAck)
            {
                ushort cumulative = (ushort)(echoes.Count - 1);
                peer.Send([0, (byte)SoeOpcode.Ack, (byte)(cumulative >> 8), (byte)cumulative]);
            }
        }
        if (forceRetransmission) Assert.True(retransmissions > 0);
        Assert.Equal(Enumerable.Range(0, 200).Select(i => (ushort)i), echoes);
        Assert.Equal(200, service.Received.Count);
        await peer.DisposeAsync();
        Assert.Equal(0, listener.ConnectionCount);
        Assert.Equal(DisconnectCause.PeerRequested, Assert.Single(service.Closed));
    }

    [Fact]
    public async Task RawUdpCannotInjectIntoAnAuthenticatedLocalRoute()
    {
        var service = new Echo();
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), service, new SilentLog());
        listener.Start();
        using var reserved = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var peer = listener.OpenLocalPeer((IPEndPoint)reserved.Client.LocalEndPoint!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        peer.Send(Request);
        _ = await peer.ReceiveAsync(deadline.Token);
        reserved.Send([0x46, 0xAA], listener.LocalEndPoint);
        peer.Send([0x46, 0xBB]);
        byte[] reply = await peer.ReceiveAsync(deadline.Token);
        Assert.Equal(new byte[] { 0x46, 0xBB }, reply[4..]);
        await Barrier(listener);
        Assert.Equal(new byte[] { 0x46, 0xBB }, Assert.Single(service.Received));
    }

    [Fact]
    public async Task AuthenticatedTunnelCanSendMoreThan32MessagesBeforeTheFirstAck()
    {
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), new Echo(), new SilentLog());
        listener.Start();
        using var reserved = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var peer = listener.OpenLocalPeer((IPEndPoint)reserved.Client.LocalEndPoint!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        peer.Send(Request);
        Assert.Equal((byte)SoeOpcode.SessionReply, (await peer.ReceiveAsync(deadline.Token))[1]);
        for (byte i = 0; i < 96; i++) peer.Send([0, 9, 0, i, 0xAB, i]);
        for (int count = 0; count < 96;)
        {
            byte[] packet = await peer.ReceiveAsync(deadline.Token);
            if (packet[1] == (byte)SoeOpcode.Ack) continue;
            Assert.Equal((byte)SoeOpcode.Data, packet[1]);
            Assert.Equal(count, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
            Assert.Equal(count++, packet[5]);
        }
    }

    [Fact]
    public async Task OversizedInputBacklogClosesOnlyItsSender()
    {
        var service = new Echo();
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), service, new SilentLog());
        listener.Start();
        using var badPort = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var goodPort = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var bad = listener.OpenLocalPeer((IPEndPoint)badPort.Client.LocalEndPoint!);
        await using var good = listener.OpenLocalPeer((IPEndPoint)goodPort.Client.LocalEndPoint!);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        listener.Post(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            byte[] data = new byte[65507]; data[0] = 0x46;
            for (int i = 0; i < 4; i++) bad.Send(data);
            Assert.Throws<IOException>(() => bad.Send(data));
            Assert.True(bad.IsClosed);
            good.Send(Request);
        }
        finally { release.Set(); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Equal((byte)SoeOpcode.SessionReply, (await good.ReceiveAsync(deadline.Token))[1]);
        await bad.DisposeAsync();
        Assert.Equal(1, listener.ConnectionCount);
        Assert.False(good.IsClosed);
    }

    [Fact]
    public async Task ListenerStopCompletesIdleRoutesAndReleasesQueuedBytes()
    {
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), new Echo(), new SilentLog());
        listener.Start();
        using var port = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var peer = listener.OpenLocalPeer((IPEndPoint)port.Client.LocalEndPoint!);
        peer.Send(Request);
        await Barrier(listener);
        listener.Stop();
        await peer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(peer.IsClosed);
        var diagnostics = System.Text.Json.JsonSerializer.SerializeToElement(listener.Diagnostics);
        Assert.Equal(0, diagnostics.GetProperty("localPeers").GetInt32());
        Assert.Equal(0, diagnostics.GetProperty("localQueuedBytes").GetInt64());
    }
}
