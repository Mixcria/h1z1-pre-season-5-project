using System.Diagnostics;
using System.Text.Json;

namespace Cranberry.Launcher.Core;

/// <summary>Optional bounded, payload-free launcher timings. Each clock difference is local.</summary>
public sealed class GameTunnelMetrics
{
    private sealed class Timing
    {
        private static readonly double[] Bounds = [.1, .25, .5, 1, 2, 5, 10, 20, 50, 100, 250, 500, 1000, 5000];
        private readonly object _gate = new();
        private readonly long[] _counts = new long[15];
        private long _count;
        private double _sum, _max;
        public void Record(long start)
        {
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            int bucket = 0; while (bucket < Bounds.Length && ms > Bounds[bucket]) bucket++;
            lock (_gate) { _counts[bucket]++; _count++; _sum += ms; _max = Math.Max(_max, ms); }
        }
        public object Take()
        {
            lock (_gate)
            {
                var result = new { count = _count, sumMs = _sum, maxMs = _max,
                    bucketUpperBoundsMs = (double[])Bounds.Clone(), bucketCounts = (long[])_counts.Clone() };
                Array.Clear(_counts); _count = 0; _sum = _max = 0; return result;
            }
        }
    }
    private readonly Timing _udpToWs = new(), _sendWait = new(), _sendWork = new(), _wsToUdp = new();
    private long _upFrames, _upBytes, _downFrames, _downBytes, _lastWindow = Stopwatch.GetTimestamp();
    public string? LogPath { get; private set; }
    public string? WriterError { get; private set; }
    internal void SendWait(long start) => _sendWait.Record(start);
    internal void Sent(long udpReceived, long sendStarted, int bytes)
    {
        _udpToWs.Record(udpReceived); _sendWork.Record(sendStarted);
        Interlocked.Increment(ref _upFrames); Interlocked.Add(ref _upBytes, bytes);
    }
    internal void Delivered(long wsReceived, int bytes)
    {
        _wsToUdp.Record(wsReceived); Interlocked.Increment(ref _downFrames); Interlocked.Add(ref _downBytes, bytes);
    }
    public object Capture()
    {
        long now = Stopwatch.GetTimestamp(), previous = Interlocked.Exchange(ref _lastWindow, now);
        return new { utc = DateTimeOffset.UtcNow, windowSeconds = (now - previous) / (double)Stopwatch.Frequency,
            udpFramesForwarded = Interlocked.Exchange(ref _upFrames, 0), udpBytesForwarded = Interlocked.Exchange(ref _upBytes, 0),
            framesDeliveredToLoopback = Interlocked.Exchange(ref _downFrames, 0), bytesDeliveredToLoopback = Interlocked.Exchange(ref _downBytes, 0),
            udpReceiveToWsSendComplete = _udpToWs.Take(), wsSendSemaphoreWait = _sendWait.Take(),
            wsSendWork = _sendWork.Take(), wsCompleteFrameToUdpSendComplete = _wsToUdp.Take() };
    }
    internal async Task WriteWindows(CancellationToken ct, string? directory = null)
    {
        try
        {
            directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cranberry", "metrics");
            Directory.CreateDirectory(directory);
            LogPath = Path.Combine(directory, $"tunnel-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
            await using var file = new FileStream(LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(LogPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await using var writer = new StreamWriter(file);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(Capture()).AsMemory(), ct).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
                if (file.Position >= 16 * 1024 * 1024) return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { WriterError = ex.GetType().Name; } // Diagnostics must not close the game tunnel.
    }
}
