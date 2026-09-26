using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Cranberry.Launcher.Core;

namespace Cranberry.Launcher.Tests;

public sealed class ParallelDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-parallel-" + Guid.NewGuid().ToString("N"));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => reply(request, ct);
    }
    private sealed class Progress(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }
    private static readonly byte[][] Bytes = Enumerable.Range(0, 12).Select(i => Enumerable.Repeat((byte)i, 4096).ToArray()).ToArray();
    private static GameManifest Manifest => new(1, "parallel", "0.0.118.208059", Bytes.Select((data, i) =>
        new GameFile(i == 0 ? "H1Z1.exe" : $"Resources/file{i}.pack", data.Length, Convert.ToHexString(SHA256.HashData(data)))).ToArray());
    private static HttpClient Api() => new(new Handler((_, _) => throw new Exception("CDN downloads must not use the game server.")))
        { BaseAddress = new("https://game.invalid/") };
    private static LauncherSettings Settings(int count = 4) => new() { ContentBaseUrl = "https://downloads.invalid/content/", ConcurrentDownloads = count };

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task DownloadWorkersFillTheirLimitAndReportAccurateAggregateProgress(int concurrency)
    {
        var manifest = Manifest;
        var objects = manifest.Files.Select((f, i) => (f.Sha256, Data: Bytes[i])).ToDictionary(x => x.Sha256, x => x.Data);
        int active = 0, peak = 0, calls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var samples = new ConcurrentQueue<InstallProgress>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var api = Api();
        using var installer = GameInstaller.Create(api, Settings(concurrency), new Handler(async (request, ct) =>
        {
            int now = Interlocked.Increment(ref active);
            lock (gate) peak = Math.Max(peak, now);
            if (Interlocked.Increment(ref calls) == concurrency) gate.SetResult();
            try
            {
                await gate.Task.WaitAsync(ct);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(objects[request.RequestUri!.Segments.Last()]) };
            }
            finally { Interlocked.Decrement(ref active); }
        }));
        await installer.Install(manifest, _root, new Progress(samples.Enqueue), deadline.Token);
        Assert.Equal(concurrency, peak);
        Assert.Equal(manifest.Files.Count, calls);
        Assert.All(samples, p => Assert.InRange(p.Complete, 0, p.Total));
        Assert.Equal(manifest.Files.Sum(f => f.Size), samples.Last().Complete);
        foreach (var file in manifest.Files) Assert.True(await GameInstaller.Matches(Path.Combine(_root, file.Path), file));
        await installer.Install(manifest, _root, null, deadline.Token);
        Assert.Equal(manifest.Files.Count, calls); // Already correct files cause no further requests.
    }

    [Fact]
    public async Task DuplicateHashesCannotRaceOverTheirResumeFile()
    {
        var first = Manifest.Files[0];
        var manifest = Manifest with { Files = [first, first with { Path = "Resources/duplicate.pack" }] };
        int active = 0;
        using var api = Api();
        using var installer = GameInstaller.Create(api, Settings(), new Handler((_, _) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            Interlocked.Decrement(ref active);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes[0]) });
        }));
        await installer.Install(manifest, _root, null);
        foreach (var file in manifest.Files) Assert.True(await GameInstaller.Matches(Path.Combine(_root, file.Path), file));
    }

    [Fact]
    public async Task CancellationStopsAllWorkersAndPreservesExistingClientAndResumeData()
    {
        var manifest = Manifest;
        Directory.CreateDirectory(Path.Combine(_root, ".cranberry-downloads"));
        string partial = Path.Combine(_root, ".cranberry-downloads", manifest.Files[0].Sha256 + ".part");
        File.WriteAllBytes(partial, Bytes[0][..20]);
        File.WriteAllText(Path.Combine(_root, "H1Z1.exe"), "existing client");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0, stopped = 0;
        using var cancel = new CancellationTokenSource();
        using var api = Api();
        using var installer = GameInstaller.Create(api, Settings(), new Handler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref count) == 4) started.SetResult();
            try { return await never.Task.WaitAsync(ct); }
            finally { Interlocked.Increment(ref stopped); }
        }));
        var installing = installer.Install(manifest, _root, null, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installing);
        Assert.Equal(4, stopped);
        Assert.Equal("existing client", File.ReadAllText(Path.Combine(_root, "H1Z1.exe")));
        Assert.Equal(Bytes[0][..20], File.ReadAllBytes(partial));
        Assert.False(File.Exists(Path.Combine(_root, ".cranberry-install.json")));
        // Cancellation has released the installation lease.
        using var lease = File.Open(Path.Combine(_root, ".cranberry.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void InvalidConcurrencyIsRejected(int count)
    {
        using var api = Api();
        Assert.Throws<InvalidDataException>(() => GameInstaller.Create(api, Settings(count)));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
