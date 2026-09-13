using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

public sealed class ListenerWakeTests
{
    private static readonly byte[] Request = [0, 1, 0, 0, 0, 3, 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 2, 0, (byte)'T', 0];
    private static readonly byte[] Ping = [0, (byte)SoeOpcode.Ping];
    private sealed class Service : ISoeService
    {
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection connection) { }
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) { }
        public void OnMessage(SoeConnection connection, Span<byte> message) => connection.SendBuffered(message);
    }
    private sealed class TestLog : ITransportLog
    {
        public readonly ConcurrentQueue<string> Errors = new();
        public bool IsEnabled(TransportLogLevel level) => true;
        public void Log(TransportLogLevel level, string message)
        { if (level == TransportLogLevel.Error) Errors.Enqueue(message); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleTunnelAndPostedWorkWakeBeforeLongMaintenanceDeadline(bool ipv6)
    {
        var log = new TestLog();
        int observed = 0;
        using var listener = new SoeListener(new(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0), new Service(), log,
            new() { TickIntervalMs = 5000, EnableDiagnostics = true,
                ObserveDatagram = (_, _, _) => Interlocked.Increment(ref observed) });
        listener.Start();
        using var reserved = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var peer = listener.OpenLocalPeer((IPEndPoint)reserved.Client.LocalEndPoint!);
        await Task.Delay(50); // The listener is idle, well inside the five-second timer.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        peer.Send(Request);
        Assert.Equal((byte)SoeOpcode.SessionReply, (await peer.ReceiveAsync(deadline.Token))[1]);
        await Task.Delay(30);
        peer.Send(Ping);
        Assert.Equal(Ping, await peer.ReceiveAsync(deadline.Token));
        await Task.Delay(30);
        var snapshot = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Post(() => snapshot.SetResult(JsonSerializer.SerializeToElement(listener.CaptureDiagnostics())));
        var counters = (await snapshot.Task.WaitAsync(deadline.Token)).GetProperty("counters");
        Assert.Equal(2, counters.GetProperty("ReceivedDatagrams").GetInt64());
        Assert.Equal(2, counters.GetProperty("SentDatagrams").GetInt64());
        Assert.Equal(0, counters.GetProperty("InvalidDatagramSize").GetInt64());
        Assert.Equal(0, counters.GetProperty("RouteMismatch").GetInt64());
        Assert.Equal(4, Volatile.Read(ref observed)); // Wake notifications are not game traffic.
        await peer.DisposeAsync().AsTask().WaitAsync(deadline.Token);
        Assert.Equal(0, listener.ConnectionCount);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public async Task MixedUdpTunnelAndConcurrentPostsMakeProgressAcrossRepeatedIdlePeriods()
    {
        var log = new TestLog();
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), new Service(), log,
            new() { TickIntervalMs = 5000, MaxDatagramsPerPass = 8, MaxPostedActionsPerPass = 4 });
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        int posts = 0;
        async Task Exchange(bool local)
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await using var peer = local ? listener.OpenLocalPeer((IPEndPoint)udp.Client.LocalEndPoint!) : null;
            if (!local) udp.Connect(listener.LocalEndPoint);
            async Task<byte[]> RoundTrip(byte[] bytes)
            {
                if (peer is not null) peer.Send(bytes);
                else await udp.SendAsync(bytes.AsMemory(), deadline.Token);
                return peer is not null ? await peer.ReceiveAsync(deadline.Token) : (await udp.ReceiveAsync(deadline.Token)).Buffer;
            }
            Assert.Equal((byte)SoeOpcode.SessionReply, (await RoundTrip(Request))[1]);
            for (int round = 0; round < 30; round++)
            {
                await Task.Delay(round % 3, deadline.Token);
                Assert.Equal(Ping, await RoundTrip(Ping));
                listener.Post(() => Interlocked.Increment(ref posts));
            }
        }
        await Task.WhenAll(Exchange(true), Exchange(false), Exchange(true), Exchange(false));
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Post(() => barrier.SetResult());
        await barrier.Task.WaitAsync(deadline.Token);
        Assert.Equal(120, Volatile.Read(ref posts));
        Assert.Empty(log.Errors);
    }

    [Fact]
    public async Task FrequentWakeupsDoNotPostponeBufferedOutputPastMaintenance()
    {
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), new Service(), new TestLog(),
            new() { TickIntervalMs = 40 });
        listener.Start();
        using var reserved = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var peer = listener.OpenLocalPeer((IPEndPoint)reserved.Client.LocalEndPoint!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        peer.Send(Request);
        _ = await peer.ReceiveAsync(deadline.Token);
        using var stopPosts = new CancellationTokenSource();
        var posting = Task.Run(async () =>
        {
            while (!stopPosts.IsCancellationRequested)
            {
                listener.Post(() => { });
                await Task.Delay(1);
            }
        });
        try
        {
            peer.Send([0x46, 0xAB]);
            var response = await peer.ReceiveAsync(deadline.Token);
            Assert.Equal((byte)SoeOpcode.Data, response[1]);
            Assert.Equal(new byte[] { 0, (byte)SoeOpcode.Bundle, 2, 0x46, 0xAB }, response[4..]);
        }
        finally { stopPosts.Cancel(); await posting; }
    }

    [Fact]
    public async Task StopWakesAnIdleListenerAndCanRestartWithoutStaleNotifications()
    {
        var log = new TestLog();
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), new Service(), log,
            new() { TickIntervalMs = 5000 });
        for (int attempt = 0; attempt < 3; attempt++)
        {
            listener.Start();
            await Task.Delay(50);
            await Task.Run(listener.Stop).WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Empty(log.Errors);
    }
}
