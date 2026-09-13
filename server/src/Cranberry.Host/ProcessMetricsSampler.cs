using System.Diagnostics;

namespace Cranberry.Host;

/// <summary>Process/runtime observations, independent of listener progress. CPU 100% = one logical CPU.</summary>
public sealed class ProcessMetricsSampler : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private long _lastStamp = Stopwatch.GetTimestamp();
    private TimeSpan _lastCpu;
    private long _lastAllocated = GC.GetTotalAllocatedBytes(false);
    private TimeSpan _lastPause = GC.GetTotalPauseDuration();
    private readonly int[] _lastCollections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    private Dictionary<int, TimeSpan> _threadCpu = [];

    public ProcessMetricsSampler() => _lastCpu = _process.TotalProcessorTime;

    public object Capture()
    {
        _process.Refresh();
        long now = Stopwatch.GetTimestamp();
        double seconds = Math.Max(Stopwatch.GetElapsedTime(_lastStamp, now).TotalSeconds, 0.000001);
        TimeSpan cpu = _process.TotalProcessorTime;
        long allocated = GC.GetTotalAllocatedBytes(false);
        TimeSpan pause = GC.GetTotalPauseDuration();
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        int[] collectionCounts = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        var threads = new List<ThreadCpuSample>();
        var nextThreads = new Dictionary<int, TimeSpan>();
        bool threadCpuAvailable = true;
        int? threadCount = null;
        try
        {
            ProcessThreadCollection observedThreads = _process.Threads;
            threadCount = observedThreads.Count;
            foreach (ProcessThread thread in observedThreads)
            {
                using (thread)
                {
                    try
                    {
                        TimeSpan threadTime = thread.TotalProcessorTime;
                        int id = thread.Id;
                        nextThreads[id] = threadTime;
                        if (_threadCpu.TryGetValue(id, out TimeSpan previous) && threadTime >= previous)
                            threads.Add(new(id, (threadTime - previous).TotalSeconds / seconds * 100));
                    }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
                    { threadCpuAvailable = false; }
                }
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        { threadCpuAvailable = false; }

        var sample = new
        {
            windowSeconds = seconds,
            cpuPercent = Math.Max(0, (cpu - _lastCpu).TotalSeconds / seconds * 100),
            cpuSeconds = cpu.TotalSeconds, cpuPercentPerLogicalProcessor = 100,
            logicalProcessorsAvailable = Environment.ProcessorCount,
            rssBytes = _process.WorkingSet64, peakRssBytes = _process.PeakWorkingSet64,
            privateBytes = _process.PrivateMemorySize64,
            threads = threadCount, sampledThreadCpuCount = nextThreads.Count, threadCpuAvailable,
            busiestThreads = threads.OrderByDescending(t => t.CpuPercent).Take(10).ToArray(),
            gcHeapBytes = GC.GetTotalMemory(false), gcCommittedBytes = gc.TotalCommittedBytes,
            gcFragmentedBytes = gc.FragmentedBytes, gcLastCompletedIndex = gc.Index,
            allocatedBytes = Math.Max(0, allocated - _lastAllocated),
            allocatedBytesPerSecond = Math.Max(0, allocated - _lastAllocated) / seconds,
            gcPauseMs = Math.Max(0, (pause - _lastPause).TotalMilliseconds),
            gcCollections = collectionCounts.Select((count, generation) => count - _lastCollections[generation]).ToArray(),
            threadPoolThreads = ThreadPool.ThreadCount, threadPoolPendingWork = ThreadPool.PendingWorkItemCount,
            threadPoolCompletedWork = ThreadPool.CompletedWorkItemCount,
        };
        _threadCpu = nextThreads; _lastStamp = now; _lastCpu = cpu; _lastAllocated = allocated; _lastPause = pause;
        collectionCounts.CopyTo(_lastCollections, 0);
        return sample;
    }

    public void Dispose() => _process.Dispose();
    public sealed record ThreadCpuSample(int ThreadId, double CpuPercent);
}
