using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

public sealed class ProductionDiagnosticsTests
{
    private static readonly byte[] Request = [0, 1, 0, 0, 0, 3, 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 2, 0, (byte)'T', 0];

    [Fact]
    public void TimingRetainsTrueOverflowMaximumAndResetsIndependentWindows()
    {
        var timing = new DiagnosticTiming();
        timing.RecordTicks(0);
        timing.RecordTicks(7 * Stopwatch.Frequency);
        timing.RecordTicks(-1); // A bad diagnostic sample is ignored, not a gameplay exception.
        DiagnosticTimingSnapshot first = timing.TakeSnapshot();
        Assert.Equal(2, first.Count);
        Assert.Equal(7000, first.MaxMs);
        Assert.Equal(7000, first.SumMs);
        Assert.Equal(3500, first.AverageMs);
        Assert.Equal(1, first.BucketCounts[0]);
        Assert.Equal(1, first.BucketCounts[^1]);
        Assert.Equal(first.Count, first.BucketCounts.Sum());
        first.BucketUpperBoundsMs[0] = 999; // Snapshot arrays cannot mutate the accumulator.
        first.BucketCounts[0] = 999;

        DiagnosticTimingSnapshot second = timing.TakeSnapshot();
        Assert.Equal(0, second.Count);
        Assert.Equal(0, second.MaxMs);
        Assert.Equal(0, second.SumMs);
        Assert.Equal(0.1, second.BucketUpperBoundsMs[0]);
        Assert.All(second.BucketCounts, value => Assert.Equal(0, value));
    }

    [Fact]
    public void SizeHistogramRetainsOverflowAndIndependentWindowCounts()
    {
        var sizes = new DiagnosticHistogram(64, 512, 1024);
        sizes.Record(64); sizes.Record(512); sizes.Record(4096); sizes.Record(-1);
        var first = sizes.TakeSnapshot();
        Assert.Equal(3, first.Count);
        Assert.Equal(4672, first.Sum);
        Assert.Equal(4096, first.Max);
        Assert.Equal(new long[] { 1, 1, 0, 1 }, first.BucketCounts);
        first.BucketUpperBounds[0] = 999;
        sizes.Record(65);
        var second = sizes.TakeSnapshot();
        Assert.Equal(1, second.Count);
        Assert.Equal(65, second.Max);
        Assert.Equal(64, second.BucketUpperBounds[0]);
        Assert.Equal(new long[] { 0, 1, 0, 0 }, second.BucketCounts);
    }

    [Fact]
    public async Task ConcurrentRecordAndWindowResetDoNotLoseOrDuplicateSamples()
    {
        var timing = new DiagnosticTiming();
        const int writers = 4, perWriter = 10_000;
        Task writing = Task.Run(() => Parallel.For(0, writers, _ =>
        {
            for (int i = 0; i < perWriter; i++) timing.RecordTicks(Stopwatch.Frequency);
        }));
        long samples = 0;
        double sumMs = 0;
        for (int i = 0; i < 32; i++)
        {
            DiagnosticTimingSnapshot window = timing.TakeSnapshot();
            Assert.Equal(window.Count, window.BucketCounts.Sum());
            samples += window.Count;
            sumMs += window.SumMs;
            await Task.Yield();
        }
        await writing;
        DiagnosticTimingSnapshot final = timing.TakeSnapshot();
        samples += final.Count;
        sumMs += final.SumMs;
        Assert.Equal(writers * perWriter, samples);
        Assert.Equal(writers * perWriter * 1000d, sumMs);
    }

    [Fact]
    public async Task DisabledByDefaultAndSnapshotRequiresOwningThread()
    {
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), new Echo(), new SilentLog());
        Assert.Throws<InvalidOperationException>(() => listener.CaptureDiagnostics());
        listener.Start();
        Assert.Throws<InvalidOperationException>(() => listener.CaptureDiagnostics());
        JsonElement snapshot = await Snapshot(listener);
        Assert.False(snapshot.GetProperty("enabled").GetBoolean());
        Assert.Single(snapshot.EnumerateObject());
    }

    [Fact]
    public async Task RawAndReliableMessagesCountOnceWithoutChangingTheWire()
    {
        var service = new Echo();
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), service, new SilentLog(),
            new() { EnableDiagnostics = true });
        listener.Start();
        using var reserved = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var peer = listener.OpenLocalPeer((IPEndPoint)reserved.Client.LocalEndPoint!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        peer.Send(Request);
        Assert.Equal((byte)SoeOpcode.SessionReply, (await peer.ReceiveAsync(deadline.Token))[1]);
        _ = await Snapshot(listener);

        peer.Send([0x46, 0xA1]);
        Assert.Equal(new byte[] { 0x46, 0xA1 }, (await peer.ReceiveAsync(deadline.Token))[4..]);
        peer.Send([0, 9, 0, 0, 0x46, 0xB2]);
        byte[] echo = await peer.ReceiveAsync(deadline.Token);
        Assert.Equal(new byte[] { 0x46, 0xB2 }, echo[4..]);
        JsonElement first = await Snapshot(listener);
        Assert.Equal(2, Counter(first, "ApplicationMessagesReceived"));
        Assert.Equal(1, Counter(first, "UnreliableApplicationMessagesReceived"));
        Assert.Equal(4, Counter(first, "ApplicationBytesReceived"));
        Assert.Equal(2, Counter(first, "ReceivedDatagrams"));
        Assert.Equal(8, Counter(first, "ReceivedBytes"));
        Assert.Equal(2, first.GetProperty("timings").GetProperty("applicationDispatchWork").GetProperty("Count").GetInt64());
        Assert.True(first.GetProperty("timings").GetProperty("localSendEnqueueWork").GetProperty("Count").GetInt64() >= 2);
        Assert.Equal(0, first.GetProperty("timings").GetProperty("udpSendWork").GetProperty("Count").GetInt64());
        Assert.Equal(8, first.GetProperty("distributions").GetProperty("receivedDatagramBytes").GetProperty("Sum").GetInt64());
        Assert.Equal(4, first.GetProperty("distributions").GetProperty("receivedApplicationBytes").GetProperty("Sum").GetInt64());
        JsonElement row = Assert.Single(first.GetProperty("sampledSessions").EnumerateArray());
        Assert.Equal(128, row.GetProperty("SendWindow").GetInt32());
        Assert.Equal(2, row.GetProperty("ApplicationMessagesReceived").GetInt64());
        Assert.Equal(1, row.GetProperty("UnreliableApplicationMessagesReceived").GetInt64());
        Assert.False(row.TryGetProperty("RemoteEndPoint", out _));
        Assert.False(row.TryGetProperty("SessionId", out _));

        JsonElement second = await Snapshot(listener);
        Assert.Equal(0, Counter(second, "ApplicationMessagesReceived"));
        Assert.Equal(0, Assert.Single(second.GetProperty("sampledSessions").EnumerateArray())
            .GetProperty("ApplicationMessagesReceived").GetInt64());
    }

    [Fact]
    public async Task PostedAndLocalQueuesMeasureTheirOwnWaitingTime()
    {
        var service = new Echo();
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), service, new SilentLog(),
            new() { EnableDiagnostics = true });
        listener.Start();
        using var reserved = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var peer = listener.OpenLocalPeer((IPEndPoint)reserved.Client.LocalEndPoint!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        peer.Send(Request);
        _ = await peer.ReceiveAsync(deadline.Token);
        _ = await Snapshot(listener);

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        listener.Post(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            listener.Post(() => { });
            peer.Send([0x46, 0xCC]);
            await Task.Delay(40, deadline.Token);
        }
        finally { release.Set(); }
        await service.Message.Task.WaitAsync(deadline.Token);
        await Task.Delay(40, deadline.Token); // Hold the already queued response in the output bridge.
        Assert.Equal(new byte[] { 0x46, 0xCC }, (await peer.ReceiveAsync(deadline.Token))[4..]);

        JsonElement snapshot = await Snapshot(listener);
        JsonElement timings = snapshot.GetProperty("timings");
        foreach (string name in new[] { "postedQueueWait", "postedWork", "localInputQueueWait", "localOutputQueueWait", "listenerWork" })
            Assert.True(timings.GetProperty(name).GetProperty("MaxMs").GetDouble() >= 20, name);
        Assert.True(snapshot.GetProperty("queues").GetProperty("postedHighWater").GetInt32() >= 1);
        Assert.Equal(1, Counter(snapshot, "ApplicationMessagesReceived"));
        Assert.True(timings.GetProperty("transportTickWork").GetProperty("Count").GetInt64() > 0);
    }

    [Fact]
    public async Task SessionSamplingRotatesAndOmittedSessionsStillResetTheirWindows()
    {
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), new Echo(), new SilentLog(),
            new() { EnableDiagnostics = true });
        listener.Start();
        using var port1 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var port2 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var peer1 = listener.OpenLocalPeer((IPEndPoint)port1.Client.LocalEndPoint!);
        await using var peer2 = listener.OpenLocalPeer((IPEndPoint)port2.Client.LocalEndPoint!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        peer1.Send(Request);
        peer2.Send(Request);
        _ = await peer1.ReceiveAsync(deadline.Token);
        _ = await peer2.ReceiveAsync(deadline.Token);
        JsonElement first = await Snapshot(listener, 1);
        JsonElement second = await Snapshot(listener, 1);
        JsonElement firstRow = Assert.Single(first.GetProperty("sampledSessions").EnumerateArray());
        JsonElement secondRow = Assert.Single(second.GetProperty("sampledSessions").EnumerateArray());
        Assert.NotEqual(firstRow.GetProperty("DiagnosticSessionId").GetInt64(), secondRow.GetProperty("DiagnosticSessionId").GetInt64());
        Assert.Equal(0, secondRow.GetProperty("ReceivedDatagrams").GetInt64());
        Assert.Equal(1, first.GetProperty("omittedSessions").GetInt32());
        JsonElement none = await Snapshot(listener, 0);
        Assert.Empty(none.GetProperty("sampledSessions").EnumerateArray());
        Assert.Equal(2, none.GetProperty("omittedSessions").GetInt32());
    }

    private static long Counter(JsonElement snapshot, string name) => snapshot.GetProperty("counters").GetProperty(name).GetInt64();

    private static async Task<JsonElement> Snapshot(SoeListener listener, int sessions = 16)
    {
        var done = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Post(() =>
        {
            try { done.SetResult(listener.CaptureDiagnostics(sessions)); }
            catch (Exception ex) { done.SetException(ex); }
        });
        // Snapshot serialization deliberately runs outside the listener thread.
        return JsonSerializer.SerializeToElement(await done.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private sealed class Echo : ISoeService
    {
        public readonly TaskCompletionSource Message = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection connection) { }
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) { }
        public void OnMessage(SoeConnection connection, Span<byte> message)
        { connection.Send(message); Message.TrySetResult(); }
    }

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }
}
