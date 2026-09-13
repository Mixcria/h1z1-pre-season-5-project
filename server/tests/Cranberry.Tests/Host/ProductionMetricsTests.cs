using System.Diagnostics;
using System.Text.Json;
using Cranberry.Host;
using Cranberry.Host.Config;

namespace Cranberry.Tests.Host;

public sealed class ProductionMetricsTests
{
    private static string DirectoryPath() => Path.Combine(Path.GetTempPath(), "cranberry-production-metrics", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StalledOwnerGetsOnePendingRequestAcrossCollectionTimeouts()
    {
        var actions = new List<Action>();
        int captures = 0;
        var owner = new OwnerThreadSnapshot(actions.Add, () => new { value = ++captures });
        var first = await owner.CollectAsync(TimeSpan.FromMilliseconds(15));
        var second = await owner.CollectAsync(TimeSpan.FromMilliseconds(15));
        Assert.Equal("pending", first.Status);
        Assert.Equal("pending", second.Status);
        Assert.Single(actions);
        Assert.Equal(0, captures);
        actions[0]();
        var completed = await owner.CollectAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("ok", completed.Status);
        Assert.True(completed.QueueWaitMs >= 15);
        Assert.Equal(1, captures);
        Assert.Single(actions);
    }

    [Fact]
    public async Task CaptureFailureDoesNotExportExceptionPayloadOrKeepRetryingTheCallback()
    {
        const string secret = "credential-canary-do-not-export";
        var owner = new OwnerThreadSnapshot(work => work(), () => throw new InvalidOperationException(secret));
        var result = await owner.CollectAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("capture-error", result.Status);
        Assert.Equal(nameof(InvalidOperationException), result.ErrorType);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task BoundedWriterRetainsValidJsonAndStopsAtFileLimitWithoutDeletingExistingFiles()
    {
        string directory = DirectoryPath();
        Directory.CreateDirectory(directory);
        string existing = Path.Combine(directory, "previous-run.jsonl");
        await File.WriteAllTextAsync(existing, "previous capture");
        var warnings = new List<string>();
        var writer = new ProductionMetricsWriter(directory, new() { MaxFileMiB = 1, MaxFiles = 1 }, warnings.Add);
        Assert.True(writer.TryWrite(new { Sequence = 1, Payload = new string('a', 600_000) }));
        Assert.True(writer.TryWrite(new { Sequence = 2, Payload = new string('b', 600_000) }));
        await writer.DisposeAsync();
        Assert.Equal(1, writer.WrittenSamples);
        Assert.Equal(1, writer.DroppedSamples);
        Assert.True(writer.CaptureStopped);
        Assert.Single(warnings);
        Assert.Equal("previous capture", await File.ReadAllTextAsync(existing));
        string capture = Assert.Single(Directory.GetFiles(directory, "metrics-*.jsonl"));
        Assert.InRange(new FileInfo(capture).Length, 1, 1 << 20);
        using var json = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(capture)));
        Assert.Equal(1, json.RootElement.GetProperty("sequence").GetInt32());
        Assert.False(writer.TryWrite(new { afterDisposal = true }));
    }

    [Fact]
    public async Task OversizeSampleStopsCaptureInsteadOfWritingAnUnboundedLine()
    {
        string directory = DirectoryPath();
        var writer = new ProductionMetricsWriter(directory, new() { MaxFileMiB = 1 });
        Assert.True(writer.TryWrite(new { payload = new string('x', (1 << 20) + 1) }));
        await writer.DisposeAsync();
        Assert.True(writer.CaptureStopped);
        Assert.Equal(0, writer.WrittenSamples);
        Assert.Equal(1, writer.DroppedSamples);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task RollingWriterContinuesAtLimitAndPreservesEveryOtherRunsFiles()
    {
        string directory = DirectoryPath(); Directory.CreateDirectory(directory);
        string other = Path.Combine(directory, "metrics-another-run.000.jsonl");
        await File.WriteAllTextAsync(other, "preserve");
        var writer = new ProductionMetricsWriter(directory, new() { MaxFileMiB = 1, MaxFiles = 1, Rolling = true });
        for (int sequence = 1; sequence <= 3; sequence++)
            Assert.True(writer.TryWrite(new { sequence, payload = new string('x', 600_000) }));
        await writer.DisposeAsync();
        Assert.False(writer.CaptureStopped);
        Assert.Equal(3, writer.WrittenSamples);
        Assert.Equal(0, writer.DroppedSamples);
        Assert.Equal("preserve", await File.ReadAllTextAsync(other));
        string own = Assert.Single(Directory.GetFiles(directory), p => p != other);
        using var json = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(own)));
        Assert.Equal(3, json.RootElement.GetProperty("sequence").GetInt32());
    }

    [Fact]
    public async Task FailedTelemetryWarningCannotAbortServerShutdown()
    {
        var writer = new ProductionMetricsWriter(DirectoryPath(), new() { MaxFileMiB = 1 },
            _ => throw new IOException("logger disk unavailable"));
        Assert.True(writer.TryWrite(new { payload = new string('x', (1 << 20) + 1) }));
        await writer.DisposeAsync();
        Assert.True(writer.CaptureStopped);
    }

    [Fact]
    public void MetricsConfigIsOptInClampedAndUsesTheExistingFileEnvironmentPrecedence()
    {
        var defaults = CranberryConfig.Defaults();
        Assert.False(defaults.Metrics.Enabled);
        var configured = CranberryConfig.Load([], key => key switch
        {
            "CRANBERRY_METRICS_MAX_SESSIONS" => "999",
            "CRANBERRY_METRICS_INTERVAL_MS" => "-5",
            "CRANBERRY_METRICS_NODE_ID" => "bad\nlabel",
            _ => null,
        }, """{"metrics":{"enabled":true,"maxSessions":3,"nodeId":"eu-1","maxFiles":2}}""");
        Assert.True(configured.Metrics.Enabled);
        Assert.Equal(64, configured.Metrics.MaxSessions);
        Assert.Equal(1000, configured.Metrics.IntervalMs);
        Assert.Equal(2, configured.Metrics.MaxFiles);
        Assert.Equal("local", configured.Metrics.NodeId);
    }

    [Fact]
    public void ConfigFingerprintExcludesLocalPathsNamesAndNodeIdentityButIncludesGameSettings()
    {
        var baseline = CranberryConfig.Defaults();
        var privateConfig = baseline with
        {
            Root = baseline.Root with { Path = "private-path-canary", SeedCharacterName = "private-name-canary" },
            Metrics = baseline.Metrics with { NodeId = "eu-2" },
        };
        using var first = JsonSerializer.SerializeToDocument(ProductionMetricsIdentity.Create(baseline));
        using var second = JsonSerializer.SerializeToDocument(ProductionMetricsIdentity.Create(privateConfig));
        var changedGame = CranberryConfig.Load([], _ => null, """{"gas":{"enabled":false}}""");
        using var third = JsonSerializer.SerializeToDocument(ProductionMetricsIdentity.Create(changedGame));
        Assert.Equal(first.RootElement.GetProperty("configurationSha256").GetString(),
            second.RootElement.GetProperty("configurationSha256").GetString());
        Assert.NotEqual(first.RootElement.GetProperty("configurationSha256").GetString(),
            third.RootElement.GetProperty("configurationSha256").GetString());
        Assert.DoesNotContain("private-path-canary", second.RootElement.GetRawText());
        Assert.DoesNotContain("private-name-canary", second.RootElement.GetRawText());
    }

    [Fact]
    public void ProcessSampleUsesMeasuredElapsedTimeAndContainsFiniteResourceValues()
    {
        using var sampler = new ProcessMetricsSampler();
        using var sample = JsonSerializer.SerializeToDocument(sampler.Capture());
        var root = sample.RootElement;
        Assert.True(root.GetProperty("windowSeconds").GetDouble() > 0);
        Assert.True(double.IsFinite(root.GetProperty("cpuPercent").GetDouble()));
        Assert.True(root.GetProperty("rssBytes").GetInt64() > 0);
        Assert.Equal(100, root.GetProperty("cpuPercentPerLogicalProcessor").GetInt32());
        Assert.InRange(root.GetProperty("busiestThreads").GetArrayLength(), 0, 10);
    }
}
