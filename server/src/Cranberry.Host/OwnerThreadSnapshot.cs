using System.Diagnostics;

namespace Cranberry.Host;

/// <summary>
/// At most one request can be queued on an owner. A timeout reports a missing sample;
/// it does not enqueue another callback behind a stalled game thread.
/// </summary>
public sealed class OwnerThreadSnapshot(Action<Action> post, Func<object> capture)
{
    private Task<OwnerSnapshotResult>? _pending;
    private long _requested;

    // A single collector calls this method; only the callback runs on another thread.
    public async Task<OwnerSnapshotResult> CollectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_pending is null)
        {
            _requested = Stopwatch.GetTimestamp();
            long requested = _requested;
            var completion = new TaskCompletionSource<OwnerSnapshotResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = completion.Task;
            try
            {
                post(() =>
                {
                    double queueMs = Stopwatch.GetElapsedTime(requested).TotalMilliseconds;
                    long started = Stopwatch.GetTimestamp();
                    try
                    {
                        object data = capture();
                        completion.TrySetResult(new("ok", queueMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                            DateTimeOffset.UtcNow, data));
                    }
                    catch (Exception error)
                    {
                        // Exception text can contain game/account data. Only retain its type.
                        completion.TrySetResult(new("capture-error", queueMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                            DateTimeOffset.UtcNow, null, error.GetType().Name));
                    }
                });
            }
            catch (Exception error)
            {
                completion.TrySetResult(new("post-error", 0, 0, DateTimeOffset.UtcNow, null, error.GetType().Name));
            }
        }

        try
        {
            OwnerSnapshotResult result = await _pending.WaitAsync(timeout, cancellationToken);
            _pending = null;
            return result;
        }
        catch (TimeoutException)
        {
            return new("pending", Stopwatch.GetElapsedTime(_requested).TotalMilliseconds, 0, null, null);
        }
    }
}

public sealed record OwnerSnapshotResult(string Status, double QueueWaitMs, double CaptureWorkMs,
    DateTimeOffset? CapturedUtc, object? Data, string? ErrorType = null);
