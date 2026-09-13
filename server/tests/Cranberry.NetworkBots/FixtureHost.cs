using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cranberry.NetworkBots;

internal sealed record FixtureAddress(int Population, int LoginPort, int GatewayPort, float[] Landing, long TickOffset,
    bool IndividualAccounts = false, TlsFixtureAddress? Tls = null);

internal static class FixtureHost
{
    public static async Task Run(int population, string output, long tickOffset, Func<uint> tick, CancellationToken ct,
        bool individualAccounts = false, bool useTls = false, int matchPopulation = 0, int simultaneousMatches = 1)
    {
        await using var tls = useTls ? new TlsFixture(population, output) : null;
        using var server = new LocalServer(population, output, individualAccounts, tls?.Accounts, tls?.AccountIds,
            matchPopulation, simultaneousMatches) { Tick = tick };
        using var process = Process.GetCurrentProcess();
        var address = new FixtureAddress(population, server.LoginEndPoint.Port, server.GatewayEndPoint.Port,
            [server.Landing.X, server.Landing.Y, server.Landing.Z], tickOffset, individualAccounts || useTls,
            tls is null ? null : await tls.Start(server, ct));
        File.WriteAllText(Path.Combine(output, "endpoint.json"), JsonSerializer.Serialize(address));
        Console.WriteLine("Isolated fixture server ready: " + Path.Combine(output, "endpoint.json"));
        try
        {
            while (!ct.IsCancellationRequested && !File.Exists(Path.Combine(output, "stop.request")))
            {
                process.Refresh();
                object productionDiagnostics;
                try { productionDiagnostics = await server.CaptureProductionDiagnostics().WaitAsync(TimeSpan.FromMilliseconds(500), ct); }
                catch (TimeoutException) { productionDiagnostics = new { Pending = true }; }
                File.AppendAllText(Path.Combine(output, "production-metrics.jsonl"), JsonSerializer.Serialize(new
                {
                    utc = DateTimeOffset.UtcNow, productionDiagnostics, launcher = tls?.CaptureDiagnostics(),
                    processCpuSeconds = process.TotalProcessorTime.TotalSeconds, rss = process.WorkingSet64,
                    gcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes()
                }) + Environment.NewLine);
                File.WriteAllText(Path.Combine(output, "server-profile.json"), JsonSerializer.Serialize(new
                {
                    server.Messages, server.Bytes, server.MaxPendingBytes, server.MaxPendingPackets, server.Resends,
                    server.ReplacedPoses, server.CommittedPoses, server.PendingPoses,
                    server.GatewayConnections, server.LoginConnections,
                    transport = server.TransportDiagnostics,
                    setupEvents = server.SetupEvents.ToArray(),
                    process = new { pid = process.Id, cpuSeconds = process.TotalProcessorTime.TotalSeconds,
                        workingSetBytes = process.WorkingSet64, peakWorkingSetBytes = process.PeakWorkingSet64,
                        privateBytes = process.PrivateMemorySize64, managedBytes = GC.GetTotalMemory(false),
                        allocatedBytes = GC.GetTotalAllocatedBytes(), gen2Collections = GC.CollectionCount(2),
                        serverGc = System.Runtime.GCSettings.IsServerGC, gcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds,
                        runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                        logicalProcessors = Environment.ProcessorCount, threads = process.Threads.Count,
                        poolThreads = ThreadPool.ThreadCount, pendingWork = ThreadPool.PendingWorkItemCount },
                    poseTimings = server.PoseTimings, errors = server.Errors.ToArray(),
                }, new JsonSerializerOptions { WriteIndented = true }));
                File.AppendAllText(Path.Combine(output, "transport-timeline.jsonl"), JsonSerializer.Serialize(new
                {
                    utc = DateTimeOffset.UtcNow, server.GatewayConnections, server.Messages, server.Bytes,
                    server.Resends, server.MaxPendingPackets, transport = server.TransportDiagnostics,
                    cpuSeconds = process.TotalProcessorTime.TotalSeconds, workingSetBytes = process.WorkingSet64,
                    managedBytes = GC.GetTotalMemory(false), gcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds,
                    poolThreads = ThreadPool.ThreadCount, pendingWork = ThreadPool.PendingWorkItemCount,
                }) + Environment.NewLine);
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        Console.WriteLine("Isolated fixture server stopped.");
    }
}
