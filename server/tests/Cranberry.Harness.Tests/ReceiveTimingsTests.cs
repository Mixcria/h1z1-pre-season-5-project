using Cranberry.Harness;
using Cranberry.Harness.Runtime;
using Cranberry.Harness.Soe;

namespace Cranberry.Harness.Tests;

public sealed class ReceiveTimingsTests
{
    [Fact]
    public void DefaultsDoNotEnableRecording()
    {
        Assert.False(new HarnessOptions().CaptureReceiveTimings);
        Assert.False(new SoeClientOptions { ProtocolName = "fixture", LinkName = "fixture" }.CaptureReceiveTimings);
    }

    [Fact]
    public void ZeroBoundariesAndOverflowRetainExactCountAndSum()
    {
        var histogram = new ReceiveTimingHistogram();
        foreach (long ticks in new long[] { 0, 1000, 1001, 100000000, 100000001 }) histogram.Record(TimeSpan.FromTicks(ticks));
        var snapshot = histogram.Snapshot();
        Assert.Equal(5, snapshot.Count);
        Assert.Equal(200002002, snapshot.SumTicks);
        Assert.Equal(2, snapshot.BucketCounts[0]);
        Assert.Equal(1, snapshot.BucketCounts[1]);
        Assert.Equal(1, snapshot.BucketCounts[^1]);
        Assert.True(snapshot.Summarize().P99Overflow);
        Assert.Null(snapshot.Summarize().P99UpperBoundMs);
    }

    [Fact]
    public void DifferenceRemovesEarlierOutlierAndMergeUsesCountsNotAverageOfAverages()
    {
        var a = new ReceiveTimings();
        a.DatagramQueue.Record(TimeSpan.FromSeconds(20));
        var start = a.Snapshot();
        for (int i = 0; i < 3; i++) a.DatagramQueue.Record(TimeSpan.FromMilliseconds(2));
        var phase = ReceiveTimingSnapshot.Difference(a.Snapshot(), start);
        Assert.Equal(2d, phase.DatagramQueue.Summarize().P99UpperBoundMs);
        Assert.Equal(0, phase.DatagramQueue.Summarize().OverflowCount);
        var b = new ReceiveTimings(); b.DatagramQueue.Record(TimeSpan.FromMilliseconds(10));
        var merged = ReceiveTimingSnapshot.Merge([phase, b.Snapshot()]);
        Assert.Equal(4, merged.DatagramQueue.Count);
        Assert.Equal(4d, merged.DatagramQueue.Summarize().AverageMs);
        Assert.Equal(10d, merged.DatagramQueue.Summarize().P99UpperBoundMs);
        Assert.Equal(0, merged.ApplicationQueue.Count);
    }

    [Fact]
    public void NegativeAndIncompatibleSnapshotsAreRejected()
    {
        var histogram = new ReceiveTimingHistogram();
        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.Record(TimeSpan.FromTicks(-1)));
        var before = histogram.Snapshot(); histogram.Record(TimeSpan.FromMilliseconds(2));
        Assert.Throws<ArgumentException>(() => ReceiveHistogramSnapshot.Difference(before, histogram.Snapshot()));
        Assert.Throws<ArgumentException>(() => new ReceiveHistogramSnapshot(1, 1, [1]));
        long[] buckets = new long[16]; buckets[^1] = 1;
        Assert.Throws<ArgumentException>(() => new ReceiveHistogramSnapshot(1, 0, buckets));
    }

    [Fact]
    public async Task ConcurrentSnapshotsHaveCoherentCountsSumsAndBuckets()
    {
        var histogram = new ReceiveTimingHistogram();
        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 10000; i++) histogram.Record(TimeSpan.FromTicks(1234));
        })).ToArray();
        var all = Task.WhenAll(writers);
        do
        {
            var cut = histogram.Snapshot();
            Assert.Equal(cut.Count * 1234, cut.SumTicks);
            Assert.Equal(cut.Count, cut.BucketCounts.Sum());
            Assert.Equal(cut.Count, cut.BucketCounts[1]);
            await Task.Yield();
        } while (!all.IsCompleted);
        await all;
        Assert.Equal(40000, histogram.Snapshot().Count);
    }

    [Fact]
    public void EmptySummariesAndSnapshotCopiesRemainStable()
    {
        var histogram = new ReceiveTimingHistogram();
        var empty = histogram.Snapshot();
        histogram.Record(TimeSpan.Zero);
        Assert.Equal(0, empty.Count);
        Assert.Null(empty.Summarize().AverageMs);
        Assert.Null(empty.Summarize().P99UpperBoundMs);
        Assert.False(empty.Summarize().P99Overflow);
        Assert.Equal(0.1d, histogram.Snapshot().Summarize().P99UpperBoundMs);
    }
}
