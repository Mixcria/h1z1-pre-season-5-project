namespace Cranberry.Harness.Runtime;

public sealed record ReceiveTimingSummary(long Count, double SumMs, double? AverageMs,
    double? P99UpperBoundMs, long OverflowCount, bool P99Overflow);

/// <summary>Immutable cumulative histogram. Fixed schema, milliseconds; final bucket is overflow.</summary>
public sealed class ReceiveHistogramSnapshot
{
    internal static readonly long[] BoundsTicks = [1000, 2500, 5000, 10000, 20000, 50000,
        100000, 200000, 500000, 1000000, 2500000, 5000000, 10000000, 50000000, 100000000];
    public long Count { get; }
    public long SumTicks { get; }
    public IReadOnlyList<long> BucketCounts { get; }
    public ReceiveHistogramSnapshot(long count, long sumTicks, IEnumerable<long> bucketCounts)
    {
        long[] buckets = bucketCounts.ToArray();
        if (count < 0 || sumTicks < 0 || buckets.Length != BoundsTicks.Length + 1 || buckets.Any(n => n < 0)
            || buckets.Aggregate(0L, (a, b) => checked(a + b)) != count || count == 0 && sumTicks != 0)
            throw new ArgumentException("Invalid receive histogram snapshot.");
        Count = count; SumTicks = sumTicks; BucketCounts = Array.AsReadOnly(buckets);
        decimal minimum = 0, maximum = 0;
        for (int i = 0; i < buckets.Length; i++)
        {
            minimum += (decimal)buckets[i] * (i == 0 ? 0 : BoundsTicks[i - 1] + 1);
            if (i < BoundsTicks.Length) maximum += (decimal)buckets[i] * BoundsTicks[i];
        }
        if (sumTicks < minimum || buckets[^1] == 0 && sumTicks > maximum)
            throw new ArgumentException("Histogram sum is incompatible with its buckets.");
    }
    public static ReceiveHistogramSnapshot Difference(ReceiveHistogramSnapshot end, ReceiveHistogramSnapshot start)
        => new(checked(end.Count - start.Count), checked(end.SumTicks - start.SumTicks),
            end.BucketCounts.Zip(start.BucketCounts, (a, b) => checked(a - b)));
    public static ReceiveHistogramSnapshot Merge(IEnumerable<ReceiveHistogramSnapshot> snapshots)
    {
        long count = 0, sum = 0; long[] buckets = new long[BoundsTicks.Length + 1];
        foreach (var snapshot in snapshots)
        {
            count = checked(count + snapshot.Count); sum = checked(sum + snapshot.SumTicks);
            for (int i = 0; i < buckets.Length; i++) buckets[i] = checked(buckets[i] + snapshot.BucketCounts[i]);
        }
        return new(count, sum, buckets);
    }
    public ReceiveTimingSummary Summarize()
    {
        long cumulative = 0;
        // ceil(Count * .99) without multiplication overflow or floating point rounding.
        long target = Count - Count / 100;
        for (int i = 0; Count > 0 && i < BucketCounts.Count; i++)
        {
            cumulative += BucketCounts[i];
            if (cumulative >= target)
                return new(Count, SumTicks / (double)TimeSpan.TicksPerMillisecond,
                    SumTicks / (double)TimeSpan.TicksPerMillisecond / Count,
                    i < BoundsTicks.Length ? BoundsTicks[i] / (double)TimeSpan.TicksPerMillisecond : null,
                    BucketCounts[^1], i == BoundsTicks.Length);
        }
        return new(0, 0, null, null, 0, false);
    }
}

public sealed class ReceiveTimingHistogram
{
    private readonly object _gate = new();
    private readonly long[] _buckets = new long[ReceiveHistogramSnapshot.BoundsTicks.Length + 1];
    private long _count, _sumTicks;
    public void Record(TimeSpan elapsed)
    {
        if (elapsed.Ticks < 0) throw new ArgumentOutOfRangeException(nameof(elapsed));
        int bucket = 0;
        while (bucket < ReceiveHistogramSnapshot.BoundsTicks.Length && elapsed.Ticks > ReceiveHistogramSnapshot.BoundsTicks[bucket]) bucket++;
        lock (_gate)
        {
            long count = checked(_count + 1), sum = checked(_sumTicks + elapsed.Ticks);
            _buckets[bucket]++; _count = count; _sumTicks = sum;
        }
    }
    public ReceiveHistogramSnapshot Snapshot()
    {
        lock (_gate) return new(_count, _sumTicks, _buckets);
    }
}

public sealed record ReceiveTimingSnapshot(ReceiveHistogramSnapshot DatagramQueue,
    ReceiveHistogramSnapshot ApplicationQueue, ReceiveHistogramSnapshot ApplicationHandler)
{
    public static ReceiveTimingSnapshot Difference(ReceiveTimingSnapshot end, ReceiveTimingSnapshot start) => new(
        ReceiveHistogramSnapshot.Difference(end.DatagramQueue, start.DatagramQueue),
        ReceiveHistogramSnapshot.Difference(end.ApplicationQueue, start.ApplicationQueue),
        ReceiveHistogramSnapshot.Difference(end.ApplicationHandler, start.ApplicationHandler));
    public static ReceiveTimingSnapshot Merge(IEnumerable<ReceiveTimingSnapshot> snapshots)
    {
        var values = snapshots.ToArray();
        return new(ReceiveHistogramSnapshot.Merge(values.Select(s => s.DatagramQueue)),
            ReceiveHistogramSnapshot.Merge(values.Select(s => s.ApplicationQueue)),
            ReceiveHistogramSnapshot.Merge(values.Select(s => s.ApplicationHandler)));
    }
    public object Summarize() => new { DatagramQueue = DatagramQueue.Summarize(),
        ApplicationQueue = ApplicationQueue.Summarize(), ApplicationHandler = ApplicationHandler.Summarize() };
}

/// <summary>Each stage snapshots coherently; cross-stage cuts need not represent the same message.</summary>
public sealed class ReceiveTimings
{
    public ReceiveTimingHistogram DatagramQueue { get; } = new();
    public ReceiveTimingHistogram ApplicationQueue { get; } = new();
    public ReceiveTimingHistogram ApplicationHandler { get; } = new();
    public ReceiveTimingSnapshot Snapshot() => new(DatagramQueue.Snapshot(), ApplicationQueue.Snapshot(), ApplicationHandler.Snapshot());
}
