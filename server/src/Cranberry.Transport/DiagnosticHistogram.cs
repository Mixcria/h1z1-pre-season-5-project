namespace Cranberry.Transport;

/// <summary>Fixed-memory count/size distribution. The last bucket is overflow; Max retains
/// the actual value. Recording allocates nothing and snapshots reset independent windows.</summary>
internal sealed class DiagnosticHistogram(params long[] upperBounds)
{
    private readonly object _gate = new();
    private readonly long[] _bounds = (long[])upperBounds.Clone();
    private readonly long[] _buckets = new long[upperBounds.Length + 1];
    private long _count, _sum, _max;

    public void Record(long value)
    {
        if (value < 0) return;
        int bucket = 0;
        while (bucket < _bounds.Length && value > _bounds[bucket]) bucket++;
        lock (_gate)
        {
            _count++;
            _sum += value;
            _max = Math.Max(_max, value);
            _buckets[bucket]++;
        }
    }

    public DiagnosticHistogramSnapshot TakeSnapshot()
    {
        lock (_gate)
        {
            var snapshot = new DiagnosticHistogramSnapshot(_count, _sum, _max,
                (long[])_bounds.Clone(), (long[])_buckets.Clone());
            _count = _sum = _max = 0;
            Array.Clear(_buckets);
            return snapshot;
        }
    }
}

internal sealed record DiagnosticHistogramSnapshot(long Count, long Sum, long Max,
    long[] BucketUpperBounds, long[] BucketCounts);
