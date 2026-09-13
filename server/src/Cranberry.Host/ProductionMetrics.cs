using System.Diagnostics;
using Cranberry.Host.Config;
using Cranberry.Transport;
using Cranberry.Zone;

namespace Cranberry.Host;

/// <summary>One bounded collector for the existing host; no HTTP metrics endpoint is exposed.</summary>
public sealed class ProductionMetrics : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly ProductionMetricsWriter _writer;
    private readonly ProcessMetricsSampler _process;
    private readonly OwnerThreadSnapshot _login, _gateway;
    private readonly Func<object>? _launcher;
    private readonly Func<long> _captureDrops;
    private readonly object _identity;
    private readonly int _intervalMs;
    private readonly Action<string> _warning;
    private readonly Task _collector;
    private int _disposed;
    public string FilePrefix => _writer.FilePrefix;

    public static ProductionMetrics? TryStart(CranberryConfig config, SoeListener login, SoeListener gateway,
        ZoneService zone, ITransportLog log, Func<object>? launcher = null, Func<long>? captureDrops = null)
    {
        if (!config.Metrics.Enabled) return null;
        try { return new(config, login, gateway, zone, log.Warn, launcher, captureDrops); }
        catch (Exception error)
        {
            Warn(log.Warn, $"Production metrics could not start ({error.GetType().Name}); gameplay continues.");
            return null;
        }
    }

    private ProductionMetrics(CranberryConfig config, SoeListener login, SoeListener gateway, ZoneService zone,
        Action<string> warning, Func<object>? launcher, Func<long>? captureDrops)
    {
        _identity = ProductionMetricsIdentity.Create(config);
        _intervalMs = config.Metrics.IntervalMs; _warning = message => Warn(warning, message);
        int maxSessions = config.Metrics.MaxSessions;
        _login = new(login.Post, () => login.CaptureDiagnostics(maxSessions));
        _gateway = new(gateway.Post, () => new
        {
            transport = gateway.CaptureDiagnostics(maxSessions), gameplay = zone.CaptureDiagnostics(maxSessions),
        });
        _launcher = launcher; _captureDrops = captureDrops ?? (() => 0);
        _process = new();
        try { _writer = new(Path.Combine(config.Root.Path, "metrics"), config.Metrics, _warning); }
        catch { _process.Dispose(); throw; }
        _collector = Task.Run(CollectAsync);
    }

    private async Task CollectAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_intervalMs));
        long previous = Stopwatch.GetTimestamp(), sequence = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token) && !_writer.CaptureStopped)
            {
                long started = Stopwatch.GetTimestamp();
                double elapsed = Stopwatch.GetElapsedTime(previous, started).TotalSeconds;
                previous = started;
                OwnerSnapshotResult[] owners = await Task.WhenAll(
                    _login.CollectAsync(TimeSpan.FromMilliseconds(500), _stop.Token),
                    _gateway.CollectAsync(TimeSpan.FromMilliseconds(500), _stop.Token));
                object process;
                try { process = _process.Capture(); }
                catch (Exception error) { process = new { errorType = error.GetType().Name }; }
                object? launcher = null;
                try { launcher = _launcher?.Invoke(); }
                catch (Exception error) { launcher = new { errorType = error.GetType().Name }; }
                _writer.TryWrite(new
                {
                    schemaVersion = 1, type = "production-window", sequence = ++sequence,
                    utc = DateTimeOffset.UtcNow, identity = _identity, collectorWindowSeconds = elapsed,
                    collectionWorkMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    login = owners[0], gateway = owners[1], launcher, process,
                    packetCaptureDroppedRecords = _captureDrops(), exporter = _writer.CaptureStatus(),
                });
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { _warning($"Production metrics collector stopped ({error.GetType().Name}); gameplay continues."); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { await _collector; }
        catch (Exception error) { _warning($"Production metrics collector cleanup failed ({error.GetType().Name})."); }
        try { await _writer.DisposeAsync(); }
        finally { _process.Dispose(); _stop.Dispose(); }
    }

    private static void Warn(Action<string> warning, string message)
    {
        try { warning(message); }
        catch { }
    }
}
