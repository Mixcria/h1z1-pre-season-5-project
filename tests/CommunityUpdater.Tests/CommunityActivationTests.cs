using System.Net;
using System.Text.Json;
using Cranberry.Launcher.Core;

namespace CommunityUpdater.Tests;

public sealed class CommunityActivationTests
{
    [Fact]
    public async Task OfflineStartupKeepsOriginalPackageAndPlayerData()
    {
        using var fixture = new UpdateFixture();
        var setup = Install(fixture);
        Directory.CreateDirectory(setup.Data);
        string saved = Path.Combine(setup.Data, "player-sentinel.txt");
        await File.WriteAllTextAsync(saved, "existing player data");
        using var handler = new FeedHandler(offline: true);
        using var feed = new CommunityReleaseFeed(setup.Trust, handler);
        var child = new Child { ExitCode = 17 };
        int ready = 0;
        CommunityStartRequest? request = null;

        var result = await setup.Bootstrap.Run(feed, r => { request = r; return child; }, ready: () => ready++);

        Assert.Equal(1, result.Sequence);
        Assert.False(result.RolledBack);
        Assert.Equal(17, result.ExitCode);
        Assert.Contains("unavailable", result.Notice);
        Assert.Equal(setup.Installation, request!.PackageDirectory);
        Assert.Equal(setup.Data, request.StateDirectory);
        Assert.Equal("existing player data", await File.ReadAllTextAsync(saved));
        Assert.Equal(1, ready);
        Assert.True(child.Disposed);
        Assert.Null(setup.Store.Read().Active);
        Assert.Equal(1, setup.Store.Read().HighestAccepted);
        using var availableAgain = setup.Store.AcquireLease();
    }

    [Fact]
    public async Task CandidateCommitsOnlyAfterReadyAndRetainsPriorRelease()
    {
        using var fixture = new UpdateFixture();
        var setup = Install(fixture);
        var active = fixture.CreateBundle(2);
        var candidate = fixture.CreateBundle(3);
        await Stage(setup, active);
        setup.Store.Write(new(1, 2, active.Release, null, null, []));
        using var handler = new FeedHandler(candidate.Release, File.ReadAllBytes(candidate.Archive));
        using var feed = new CommunityReleaseFeed(setup.Trust, handler);
        var child = new Child
        {
            OnReady = _ =>
            {
                var beforeReady = setup.Store.Read();
                Assert.Equal(active.Release, beforeReady.Active);
                Assert.Equal(2, beforeReady.HighestAccepted);
                Assert.Equal(candidate.Release, beforeReady.Pending);
                Assert.Contains(candidate.Release.Sha256, beforeReady.Failed);
                return Task.CompletedTask;
            },
            ExitCode = 4,
        };

        var result = await setup.Bootstrap.Run(feed, request =>
        {
            Assert.Equal(setup.Store.BundlePath(candidate.Release), request.PackageDirectory);
            Assert.True(File.Exists(Path.Combine(request.PackageDirectory, "runtime", "Cranberry.Host.exe")));
            return child;
        });

        var accepted = setup.Store.Read();
        Assert.Equal(3, result.Sequence);
        Assert.Equal(4, result.ExitCode);
        Assert.False(result.RolledBack);
        Assert.Equal(3, accepted.HighestAccepted);
        Assert.Equal(candidate.Release, accepted.Active);
        Assert.Equal(active.Release, accepted.Previous);
        Assert.Null(accepted.Pending);
        Assert.Empty(accepted.Failed);
        Assert.Equal(1, handler.ArchiveRequests);
        Assert.True(child.Disposed);
        await CommunityBundleStore.ValidatePackage(setup.Store.BundlePath(active.Release), setup.Trust, active.Release);
    }

    [Fact]
    public async Task FailedCandidateStopsBeforeFallbackAndIsNotRetried()
    {
        using var fixture = new UpdateFixture();
        var setup = Install(fixture);
        var active = fixture.CreateBundle(2);
        var candidate = fixture.CreateBundle(3);
        await Stage(setup, active);
        setup.Store.Write(new(1, 2, active.Release, null, null, []));
        using var handler = new FeedHandler(candidate.Release, File.ReadAllBytes(candidate.Archive));
        using var feed = new CommunityReleaseFeed(setup.Trust, handler);
        var failed = new Child { OnReady = _ => throw new IOException("Candidate host failed before readiness.") };
        int starts = 0;

        var result = await setup.Bootstrap.Run(feed, request =>
        {
            starts++;
            if (request.PackageDirectory == setup.Store.BundlePath(candidate.Release)) return failed;
            Assert.True(failed.Stopped);
            Assert.True(failed.Disposed);
            Assert.Equal(setup.Store.BundlePath(active.Release), request.PackageDirectory);
            return new Child();
        });

        Assert.True(result.RolledBack);
        Assert.Equal(2, result.Sequence);
        Assert.Equal(2, starts);
        Assert.Equal(1, failed.StopCalls);
        Assert.Equal(active.Release, setup.Store.Read().Active);
        Assert.Equal(2, setup.Store.Read().HighestAccepted);
        Assert.Null(setup.Store.Read().Pending);
        Assert.Contains(candidate.Release.Sha256, setup.Store.Read().Failed);
        using var retryHandler = new FeedHandler(candidate.Release, File.ReadAllBytes(candidate.Archive));
        using var retryFeed = new CommunityReleaseFeed(setup.Trust, retryHandler);
        var retry = await setup.Bootstrap.Run(retryFeed, request =>
        {
            Assert.Equal(setup.Store.BundlePath(active.Release), request.PackageDirectory);
            return new Child();
        });
        Assert.False(retry.RolledBack);
        Assert.Equal(0, retryHandler.ArchiveRequests);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RollbackPreservesHighestAcceptedAndRejectsReplay(long offeredSequence)
    {
        using var fixture = new UpdateFixture();
        var setup = Install(fixture);
        var previous = fixture.CreateBundle(2);
        var active = fixture.CreateBundle(3);
        await Stage(setup, previous);
        await Stage(setup, active);
        setup.Store.Write(new(1, 3, active.Release, previous.Release, null, []));
        var offered = offeredSequence == 2 ? previous : active;
        using var handler = new FeedHandler(offered.Release, File.ReadAllBytes(offered.Archive));
        using var feed = new CommunityReleaseFeed(setup.Trust, handler);
        var failed = new Child { OnReady = _ => throw new IOException("Previously accepted host now fails startup.") };

        var result = await setup.Bootstrap.Run(feed, request =>
            request.PackageDirectory == setup.Store.BundlePath(active.Release) ? failed : new Child());

        Assert.True(result.RolledBack);
        Assert.Equal(2, result.Sequence);
        var rolledBack = setup.Store.Read();
        Assert.Equal(3, rolledBack.HighestAccepted);
        Assert.Equal(previous.Release, rolledBack.Active);
        Assert.Null(rolledBack.Previous);
        Assert.Equal(0, handler.ArchiveRequests);
        using var repeatHandler = new FeedHandler(active.Release, File.ReadAllBytes(active.Archive));
        using var repeatFeed = new CommunityReleaseFeed(setup.Trust, repeatHandler);
        var repeat = await setup.Bootstrap.Run(repeatFeed, request =>
        {
            Assert.Equal(setup.Store.BundlePath(previous.Release), request.PackageDirectory);
            return new Child();
        });
        Assert.Equal(2, repeat.Sequence);
        Assert.Equal(3, setup.Store.Read().HighestAccepted);
        Assert.Equal(0, repeatHandler.ArchiveRequests);
    }

    [Fact]
    public async Task InterruptedPendingAttemptIsQuarantinedBeforeDiscovery()
    {
        using var fixture = new UpdateFixture();
        var setup = Install(fixture);
        var active = fixture.CreateBundle(2);
        var pending = fixture.CreateBundle(3);
        await Stage(setup, active);
        await Stage(setup, pending);
        setup.Store.Write(new(1, 2, active.Release, null, pending.Release, []));
        using var handler = new FeedHandler(pending.Release, File.ReadAllBytes(pending.Archive));
        using var feed = new CommunityReleaseFeed(setup.Trust, handler);

        var result = await setup.Bootstrap.Run(feed, request =>
        {
            Assert.Null(setup.Store.Read().Pending);
            Assert.Contains(pending.Release.Sha256, setup.Store.Read().Failed);
            Assert.Equal(setup.Store.BundlePath(active.Release), request.PackageDirectory);
            return new Child();
        });

        Assert.Equal(2, result.Sequence);
        Assert.Equal(0, handler.ArchiveRequests);
        Assert.Equal(2, setup.Store.Read().HighestAccepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunningSessionExcludesAnotherBootstrap(bool separateInstallation)
    {
        using var fixture = new UpdateFixture();
        var setup = Install(fixture);
        var second = separateInstallation ? Install(fixture, "second", setup.Data) : setup;
        using var handler = new FeedHandler();
        using var feed = new CommunityReleaseFeed(setup.Trust, handler);
        using var secondHandler = new FeedHandler();
        using var secondFeed = new CommunityReleaseFeed(second.Trust, secondHandler);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new Child { OnExit = ct => { waiting.SetResult(); return exit.Task.WaitAsync(ct); } };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<CommunityRunResult> first = setup.Bootstrap.Run(feed, _ => child, cancellation: deadline.Token);
        try
        {
            await waiting.Task.WaitAsync(deadline.Token);
            await Assert.ThrowsAsync<IOException>(() => second.Bootstrap.Run(secondFeed,
                _ => throw new Exception("A concurrent bootstrap must never start a child."), cancellation: deadline.Token));
            Assert.Equal(0, secondHandler.Requests);
            Assert.False(child.Disposed);
        }
        finally
        {
            exit.TrySetResult(0);
            await first;
        }
        Assert.True(child.Disposed);
        var afterExit = await second.Bootstrap.Run(secondFeed, _ => new Child(), cancellation: deadline.Token);
        Assert.Equal(1, afterExit.Sequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDisposesChildReleasesLeasesAndPreservesAcceptanceBoundary(bool afterReady)
    {
        using var fixture = new UpdateFixture();
        var setup = Install(fixture);
        var candidate = fixture.CreateBundle(2);
        using var handler = new FeedHandler(candidate.Release, File.ReadAllBytes(candidate.Archive));
        using var feed = new CommunityReleaseFeed(setup.Trust, handler);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var child = new Child
        {
            OnReady = ct => afterReady ? Task.CompletedTask : UntilCancelled(ct),
            OnExit = async ct => { await UntilCancelled(ct); return 0; },
        };
        async Task UntilCancelled(CancellationToken ct)
        {
            waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        Task<CommunityRunResult> run = setup.Bootstrap.Run(feed, _ => child, cancellation: cancellation.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(child.Disposed);
        var state = setup.Store.Read();
        Assert.Equal(afterReady ? 2 : 1, state.HighestAccepted);
        Assert.Equal(afterReady ? candidate.Release : null, state.Active);
        Assert.Equal(afterReady ? null : candidate.Release, state.Pending);
        using var installationLease = setup.Store.AcquireLease();
        using var sessionLease = new FileStream(Path.Combine(setup.Data, "community-launch.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task UnconfirmedChildStopPreventsFallback()
    {
        using var fixture = new UpdateFixture();
        var setup = Install(fixture);
        var candidate = fixture.CreateBundle(2);
        using var handler = new FeedHandler(candidate.Release, File.ReadAllBytes(candidate.Archive));
        using var feed = new CommunityReleaseFeed(setup.Trust, handler);
        var child = new Child
        {
            OnReady = _ => throw new IOException("Startup failed."),
            OnStop = _ => throw new IOException("Owned child shutdown could not be confirmed."),
        };
        int starts = 0;

        var error = await Assert.ThrowsAsync<IOException>(() => setup.Bootstrap.Run(feed, _ => { starts++; return child; }));

        Assert.Contains("shutdown", error.Message);
        Assert.Equal(1, starts);
        Assert.Equal(1, child.StopCalls);
        Assert.True(child.Disposed);
        Assert.Null(setup.Store.Read().Active);
        Assert.Equal(1, setup.Store.Read().HighestAccepted);
    }

    [Fact]
    public async Task ExistingLegacyEditionOrOpenGamePreventsDiscoveryAndChildStartup()
    {
        using var fixture = new UpdateFixture();
        var setup = Install(fixture);
        Directory.CreateDirectory(setup.Data);
        using var handler = new FeedHandler();
        using var feed = new CommunityReleaseFeed(setup.Trust, handler);
        using (var legacy = new FileStream(Path.Combine(setup.Data, "local-edition.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => setup.Bootstrap.Run(feed,
                _ => throw new Exception("An existing local edition must not be interrupted.")));
        var gameOpen = new CommunityBootstrap(setup.Installation, setup.Data, gameIsOpen: () => true);
        await Assert.ThrowsAsync<IOException>(() => gameOpen.Run(feed,
            _ => throw new Exception("A running game must not be interrupted.")));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task GameStartingDuringDownloadPreventsActivation()
    {
        using var fixture = new UpdateFixture();
        var setup = Install(fixture);
        var candidate = fixture.CreateBundle(2);
        bool gameOpen = false;
        var bootstrap = new CommunityBootstrap(setup.Installation, setup.Data, () => gameOpen);
        using var handler = new FeedHandler(candidate.Release, File.ReadAllBytes(candidate.Archive))
        {
            BeforeResponse = request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/" + CommunityRelease.ArchiveFileName)) gameOpen = true;
            },
        };
        using var feed = new CommunityReleaseFeed(setup.Trust, handler);
        int starts = 0;

        await Assert.ThrowsAsync<IOException>(() => bootstrap.Run(feed, _ => { starts++; return new Child(); }));

        Assert.Equal(0, starts);
        Assert.Null(setup.Store.Read().Active);
        Assert.Null(setup.Store.Read().Pending);
        Assert.Equal(1, setup.Store.Read().HighestAccepted);
    }

    private static Setup Install(UpdateFixture fixture, string name = "installed", string? data = null)
    {
        var original = fixture.CreateBundle(1);
        string directory = Path.Combine(fixture.Root, name);
        foreach (var file in original.Files)
        {
            string path = Path.Combine(directory, file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Value);
        }
        var trust = CommunityUpdateSettings.Load(directory);
        string state = data ?? Path.Combine(fixture.Root, "player");
        return new(directory, state, trust, new(directory, trust), new(directory, state, () => false));
    }

    private static Task<string> Stage(Setup setup,
        (string Archive, CommunityRelease Release, Dictionary<string, byte[]> Files) bundle) =>
        CommunityBundleStore.Stage(bundle.Archive, setup.Store.Versions, bundle.Release, setup.Trust);

    private sealed record Setup(string Installation, string Data, CommunityUpdateSettings Trust,
        CommunityUpdateStateStore Store, CommunityBootstrap Bootstrap);

    private sealed class Child : ICommunityRunningChild
    {
        public Func<CancellationToken, Task>? OnReady;
        public Func<CancellationToken, Task<int>>? OnExit;
        public Func<CancellationToken, Task>? OnStop;
        public int ExitCode, StopCalls;
        public bool Stopped, Disposed;
        public Task WaitForReady(CancellationToken cancellation) => OnReady?.Invoke(cancellation) ?? Task.CompletedTask;
        public Task<int> WaitForExit(CancellationToken cancellation) => OnExit?.Invoke(cancellation) ?? Task.FromResult(ExitCode);
        public async Task Stop(CancellationToken cancellation)
        {
            StopCalls++;
            if (OnStop is not null) await OnStop(cancellation);
            Stopped = true;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FeedHandler(CommunityRelease? release = null, byte[]? archive = null, bool offline = false) : HttpMessageHandler
    {
        public int Requests, ArchiveRequests;
        public Action<HttpRequestMessage>? BeforeResponse;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Requests++;
            BeforeResponse?.Invoke(request);
            if (offline) throw new HttpRequestException("Offline fixture.");
            byte[] body;
            if (request.RequestUri!.Host == "api.github.com")
            {
                if (release is null) body = "[]"u8.ToArray();
                else
                {
                    byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(release, CommunityRelease.Json);
                    body = JsonSerializer.SerializeToUtf8Bytes(new[] { new { draft = false, prerelease = false,
                        assets = new[] {
                            new { name = CommunityRelease.ManifestFileName, size = (long)manifest.Length,
                                browser_download_url = $"https://github.com/example/project/releases/download/v{release.Sequence}/{CommunityRelease.ManifestFileName}" },
                            new { name = CommunityRelease.ArchiveFileName, size = release.Size,
                                browser_download_url = $"https://github.com/example/project/releases/download/v{release.Sequence}/{CommunityRelease.ArchiveFileName}" }
                        } } });
                }
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/" + CommunityRelease.ManifestFileName))
                body = JsonSerializer.SerializeToUtf8Bytes(release, CommunityRelease.Json);
            else if (request.RequestUri.AbsolutePath.EndsWith("/" + CommunityRelease.ArchiveFileName))
            {
                ArchiveRequests++;
                body = archive ?? throw new Exception("No archive was configured.");
            }
            else throw new Exception("Unexpected fixture request: " + request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}
