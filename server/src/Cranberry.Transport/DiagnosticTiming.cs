using System.Diagnostics;

namespace Cranberry.Transport;

/// <summary>
/// Fixed-memory, thread-safe timing accumulator. RecordTicks allocates nothing; TakeSnapshot
/// atomically copies and resets the measurement window. Buckets are non-cumulative and the
/// final count is overflow, not a cap on MaxMs. Samples use Stopwatch ticks, not TimeSpan ticks.
/// </summary>
public sealed class DiagnosticTiming
{
    private static readonly double[] Bounds = [0.1, 0.25, 0.5, 1, 2, 5, 10, 20, 50, 100, 250, 500, 1000, 5000];
    private readonly object _gate = new();
    private readonly long[] _buckets = new long[Bounds.Length + 1];
    private long _count, _max;
    private double _sumTicks;

    public void RecordTicks(long elapsedTicks)
    {
        // A malformed diagnostic sample must not take down a game/timer callback.
        if (elapsedTicks < 0) return;
        double ms = elapsedTicks * 1000d / Stopwatch.Frequency;
        int bucket = 0;
        while (bucket < Bounds.Length && ms > Bounds[bucket]) bucket++;
        lock (_gate)
        {
            _count++;
            _sumTicks += elapsedTicks;
            _max = Math.Max(_max, elapsedTicks);
            _buckets[bucket]++;
        }
    }

    public DiagnosticTimingSnapshot TakeSnapshot()
    {
        lock (_gate)
        {
            double sumMs = _sumTicks * 1000d / Stopwatch.Frequency;
            var snapshot = new DiagnosticTimingSnapshot(_count, sumMs,
                _count == 0 ? 0 : sumMs / _count, _max * 1000d / Stopwatch.Frequency,
                (double[])Bounds.Clone(), (long[])_buckets.Clone());
            _count = _max = 0;
            _sumTicks = 0;
            Array.Clear(_buckets);
            return snapshot;
        }
    }
}

public sealed record DiagnosticTimingSnapshot(long Count, double SumMs, double AverageMs,
    double MaxMs, double[] BucketUpperBoundsMs, long[] BucketCounts);
