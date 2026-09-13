using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

public sealed class TunnelBackpressureTests
{
    private static readonly byte[] Request = [0, 1, 0, 0, 0, 3, 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 2, 0, (byte)'T', 0];

    private sealed class Service : ISoeService
    {
        public readonly TaskCompletionSource<SoeConnection> Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection connection) => Connected.TrySetResult(connection);
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) { }
        public void OnMessage(SoeConnection connection, Span<byte> message) { }
    }

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }

    private static async Task OnOwner(SoeListener listener, Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Post(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception error) { done.SetException(error); }
        });
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StalledReaderRecoversWithoutReliableRetriesFillingItsQueue()
    {
        var service = new Service();
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), service, new SilentLog(),
            new SoeListenerOptions { EnableDiagnostics = true });
        listener.Start();
        using var reserved = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var peer = listener.OpenLocalPeer((IPEndPoint)reserved.Client.LocalEndPoint!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        peer.Send(Request);
        Assert.Equal((byte)SoeOpcode.SessionReply, (await peer.ReceiveAsync(deadline.Token))[1]);
        SoeConnection connection = await service.Connected.Task.WaitAsync(deadline.Token);
        byte[] payload = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 17)).ToArray();

        await OnOwner(listener, () =>
        {
            connection.Send(payload);
            long start = Environment.TickCount64;
            // Advance retry time on the owning thread while the tunnel cannot drain.
            // The previous code queues several copies of each of the 128 in-flight
            // fragments and closes the route well before this ten-second pause ends.
            for (int elapsed = 20; elapsed <= 10_000; elapsed += 20)
                connection.Tick(start + elapsed);
            Assert.False(peer.IsClosed);
            Assert.Equal(ConnectionState.Open, connection.State);
            var counters = listener.DiagnosticCollector!.TakeCounters();
            Assert.Equal(0, counters["LocalOutputBacklogRejected"]);
            Assert.True(counters["LocalOutputRetriesCoalesced"] > 512);
        });

        var recovered = new List<byte>(payload.Length);
        ushort expected = 0;
        while (recovered.Count < payload.Length)
        {
            byte[] packet = await peer.ReceiveAsync(deadline.Token);
            Assert.Equal((byte)SoeOpcode.DataFragment, packet[1]);
            ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
            Assert.Equal(expected++, sequence);
            if (sequence == 0)
                Assert.Equal(payload.Length, BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(4)));
            recovered.AddRange(packet[(sequence == 0 ? 8 : 4)..]);
            peer.Send([0, (byte)SoeOpcode.Ack, (byte)(sequence >> 8), (byte)sequence]);
        }
        Assert.Equal(payload, recovered);
        await peer.DisposeAsync();
        Assert.Equal(0, listener.ConnectionCount);
        var queues = System.Text.Json.JsonSerializer.SerializeToElement(listener.Diagnostics);
        Assert.Equal(0, queues.GetProperty("localQueuedBytes").GetInt64());
    }

    [Theory]
    [InlineData(9, 0)]
    [InlineData(9, 1)]
    [InlineData(13, 0)]
    [InlineData(13, 1)]
    public async Task OnlyIdenticalQueuedCopiesInTheSameConnectionAreCoalesced(int opcode, int compression)
    {
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), new Service(), new SilentLog(),
            new SoeListenerOptions { EnableDiagnostics = true });
        listener.Start();
        using var reserved = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var remote = (IPEndPoint)reserved.Client.LocalEndPoint!;
        await using var peer = listener.OpenLocalPeer(remote);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var settings = new SessionSettings { Compression = (ushort)compression };
        SoeConnection Connection() => new(remote, default, settings, SessionDecision.Clear, new Service(),
            new SilentLog(), (_, _) => { }, Environment.TickCount64);
        SoeConnection first = Connection(), replacement = Connection();
        byte[] a = new byte[settings.ReliableHeaderLength + 5];
        a[1] = (byte)opcode;
        BinaryPrimitives.WriteUInt16BigEndian(a.AsSpan(settings.ReliableHeaderLength - 2), ushort.MaxValue);
        a[^1] = 42;
        byte[] b = (byte[])a.Clone(); b[^1] = 43;

        Assert.True(peer.TryWriteResponse(first, a));
        Assert.True(peer.TryWriteResponse(first, a)); // Identical retry is redundant.
        Assert.True(peer.TryWriteResponse(first, b)); // Sequence alone cannot establish identity.
        Assert.Equal(a, await peer.ReceiveAsync(deadline.Token));
        Assert.True(peer.TryWriteResponse(first, b)); // Dequeuing a must retain the pending b entry.
        Assert.Equal(b, await peer.ReceiveAsync(deadline.Token));
        Assert.True(peer.TryWriteResponse(first, b)); // Once dequeued, a retry can repair downstream loss.
        Assert.True(peer.TryWriteResponse(replacement, b)); // New session may reuse sequence and bytes.
        Assert.Equal(b, await peer.ReceiveAsync(deadline.Token));
        Assert.Equal(b, await peer.ReceiveAsync(deadline.Token));

        byte[] ack = [0, (byte)SoeOpcode.Ack, 0, 0];
        Assert.True(peer.TryWriteResponse(first, ack));
        Assert.True(peer.TryWriteResponse(first, ack));
        Assert.Equal(ack, await peer.ReceiveAsync(deadline.Token));
        Assert.Equal(ack, await peer.ReceiveAsync(deadline.Token));
        Assert.Equal(2, listener.DiagnosticCollector!.TakeCounters()["LocalOutputRetriesCoalesced"]);
        Assert.Equal(0, System.Text.Json.JsonSerializer.SerializeToElement(listener.Diagnostics)
            .GetProperty("localQueuedBytes").GetInt64());
    }

    [Fact]
    public async Task DistinctOutputStillHasABoundedBacklogAndDoesNotCloseAnotherRoute()
    {
        var service = new Service();
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), service, new SilentLog(),
            new SoeListenerOptions { EnableDiagnostics = true });
        listener.Start();
        using var badPort = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var goodPort = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var bad = listener.OpenLocalPeer((IPEndPoint)badPort.Client.LocalEndPoint!);
        await using var good = listener.OpenLocalPeer((IPEndPoint)goodPort.Client.LocalEndPoint!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bad.Send(Request);
        _ = await bad.ReceiveAsync(deadline.Token);
        SoeConnection connection = await service.Connected.Task.WaitAsync(deadline.Token);
        await OnOwner(listener, () =>
        {
            byte[] packet = new byte[512]; packet[1] = (byte)SoeOpcode.Data;
            for (ushort i = 0; i < 512; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), i);
                Assert.True(bad.TryWriteResponse(connection, packet));
            }
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 512);
            Assert.False(bad.TryWriteResponse(connection, packet));
        });
        Assert.True(bad.IsClosed);
        good.Send(Request);
        Assert.Equal((byte)SoeOpcode.SessionReply, (await good.ReceiveAsync(deadline.Token))[1]);
        await bad.DisposeAsync();
        Assert.False(good.IsClosed);
        Assert.Equal(1, listener.ConnectionCount);
        Assert.Equal(0, System.Text.Json.JsonSerializer.SerializeToElement(listener.Diagnostics)
            .GetProperty("localQueuedBytes").GetInt64());
        Assert.Equal(1, listener.DiagnosticCollector!.TakeCounters()["LocalOutputBacklogRejected"]);
    }
}
