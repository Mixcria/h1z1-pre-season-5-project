using System.Net;
using System.Text;
using System.Text.Json;
using Cranberry.Launcher.Core;

namespace CommunityUpdater.Tests;

public sealed class ReleaseFeedTests
{
    [Fact]
    public async Task ChoosesHighestSignedSequenceNotApiOrderAndSkipsDraftPrereleaseAndBadSignature()
    {
        using var f = new UpdateFixture();
        var old = f.CreateBundle(1); var newest = f.CreateBundle(8); var middle = f.CreateBundle(3);
        var draft = f.CreateBundle(99); var preview = f.CreateBundle(100); var invalid = f.CreateBundle(101);
        var candidates = new[] { old.Release, newest.Release, middle.Release, draft.Release, preview.Release,
            invalid.Release with { Signature = Convert.ToBase64String(new byte[64]) } };
        var handler = FeedHandler(candidates, r => r.Sequence == 99, r => r.Sequence == 100);
        using var feed = new CommunityReleaseFeed(f.Trust, handler);
        var result = await feed.Check(2);
        Assert.Equal(8, result!.Release.Sequence);
        Assert.Null(await feed.Check(8));
        Assert.DoesNotContain(handler.Requests, u => u.AbsolutePath.Contains("/v99/") || u.AbsolutePath.Contains("/v100/"));
        using var previewFeed = new CommunityReleaseFeed(f.Trust with { IncludePrereleases = true },
            FeedHandler(candidates, r => r.Sequence == 99, r => r.Sequence == 100));
        Assert.Equal(100, (await previewFeed.Check(0))!.Release.Sequence);
    }

    [Fact]
    public async Task RejectsConflictingSignedSequenceAndRespectsInstalledFloor()
    {
        using var f = new UpdateFixture();
        var first = f.CreateBundle(7);
        var conflict = f.Sign(first.Release with { Version = "1.0.1" });
        int n = 0;
        var releases = new[] { first.Release, conflict };
        var handler = new UpdateHttpHandler(uri => uri.Host == "api.github.com"
            ? Json(releases.Select((r, i) => Listing(r, tag: "duplicate" + i)).ToArray())
            : Json(releases[n++]));
        using var feed = new CommunityReleaseFeed(f.Trust, handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => feed.Check(0));
        using var installed = new CommunityReleaseFeed(f.Trust with { Sequence = 7 }, FeedHandler(new[] { first.Release }));
        Assert.Null(await installed.Check(0));
    }

    [Theory]
    [InlineData("https://github.com/other/project/releases/download/v1/community-release.json")]
    [InlineData("http://github.com/example/project/releases/download/v1/community-release.json")]
    [InlineData("https://github.com.evil.test/example/project/releases/download/v1/community-release.json")]
    [InlineData("https://user:secret@github.com/example/project/releases/download/v1/community-release.json")]
    [InlineData("https://github.com/example/project/releases/download/v1/community-release.json?token=x")]
    public async Task NeverRequestsManifestOutsideConfiguredRepository(string manifestUri)
    {
        using var f = new UpdateFixture(); var bundle = f.CreateBundle();
        var handler = new UpdateHttpHandler(uri => uri.Host == "api.github.com"
            ? Json(new[] { Listing(bundle.Release, manifestUri: manifestUri) }) : throw new Exception("Unscoped request"));
        using var feed = new CommunityReleaseFeed(f.Trust, handler);
        Assert.Null(await feed.Check(0));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("https://evil.test/download")]
    [InlineData("http://release-assets.githubusercontent.com/github-production-release-asset/1/file")]
    [InlineData("https://release-assets.githubusercontent.com/other/file")]
    [InlineData("https://github.com/other/project/releases/download/v1/Cranberry-Local-update.zip")]
    [InlineData("https://github.com/example/project/releases/download/v1/Cranberry-Local-update.zip#fragment")]
    public async Task RefusesUnsafeDownloadRedirectWithoutFollowingIt(string location)
    {
        using var f = new UpdateFixture(); var bundle = f.CreateBundle();
        var handler = new UpdateHttpHandler(_ => Redirect(location));
        using var feed = new CommunityReleaseFeed(f.Trust, handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => feed.Download(new(bundle.Release, Asset(1)), Path.Combine(f.Root, "cache")));
        Assert.Single(handler.Requests);
        Assert.Empty(Directory.GetFiles(Path.Combine(f.Root, "cache")));
    }

    [Fact]
    public async Task AllowsScopedGithubCdnAndVerifiesDownloadedBytesBeforePromotion()
    {
        using var f = new UpdateFixture(); var bundle = f.CreateBundle();
        byte[] archive = File.ReadAllBytes(bundle.Archive);
        var handler = new UpdateHttpHandler(uri => uri.Host == "github.com"
            ? Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/123/test?sig=public-url")
            : Bytes(archive));
        using var feed = new CommunityReleaseFeed(f.Trust, handler);
        var progress = new CollectProgress();
        string path = await feed.Download(new(bundle.Release, Asset(1)), Path.Combine(f.Root, "cache"), progress);
        Assert.Equal(archive, File.ReadAllBytes(path));
        Assert.Equal(bundle.Release.Size, progress.Last!.Complete);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(path, await feed.Download(new(bundle.Release, Asset(1)), Path.Combine(f.Root, "cache")));
        Assert.Equal(2, handler.Requests.Count);
        File.WriteAllText(path, "bad existing cache");
        await Assert.ThrowsAsync<InvalidDataException>(() => feed.Download(new(bundle.Release, Asset(1)), Path.Combine(f.Root, "cache")));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RejectsTruncatedCorruptAndOversizedDownload(int adjustment)
    {
        using var f = new UpdateFixture(); var bundle = f.CreateBundle();
        byte[] content = File.ReadAllBytes(bundle.Archive);
        Array.Resize(ref content, content.Length + adjustment);
        if (adjustment == 0) content[0] ^= 1;
        using var feed = new CommunityReleaseFeed(f.Trust, new UpdateHttpHandler(_ => Bytes(content)));
        string cache = Path.Combine(f.Root, "cache");
        await Assert.ThrowsAsync<InvalidDataException>(() => feed.Download(new(bundle.Release, Asset(1)), cache));
        Assert.Empty(Directory.GetFiles(cache));
    }

    [Fact]
    public async Task RejectsOversizedJsonAndDoesNotFollowApiRedirects()
    {
        using var f = new UpdateFixture();
        using var feed = new CommunityReleaseFeed(f.Trust, new UpdateHttpHandler(_ => Bytes(new byte[2 * 1024 * 1024 + 1])));
        await Assert.ThrowsAsync<InvalidDataException>(() => feed.Check(0));
        var redirects = new UpdateHttpHandler(_ => Redirect("https://evil.test/releases"));
        using var redirectFeed = new CommunityReleaseFeed(f.Trust, redirects);
        await Assert.ThrowsAsync<HttpRequestException>(() => redirectFeed.Check(0));
        Assert.Single(redirects.Requests);
    }

    [Fact]
    public async Task EnforcesDownloadAndJsonLimitsWithoutContentLengthHeaders()
    {
        using var f = new UpdateFixture(); var bundle = f.CreateBundle();
        static HttpResponseMessage Unbounded(byte[] bytes) => new(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableStream(bytes)) };
        using var listing = new CommunityReleaseFeed(f.Trust,
            new UpdateHttpHandler(_ => Unbounded(new byte[2 * 1024 * 1024 + 1])));
        await Assert.ThrowsAsync<InvalidDataException>(() => listing.Check(0));
        byte[] extra = File.ReadAllBytes(bundle.Archive).Concat(new byte[] { 1 }).ToArray();
        using var download = new CommunityReleaseFeed(f.Trust, new UpdateHttpHandler(_ => Unbounded(extra)));
        string cache = Path.Combine(f.Root, "stream-cache");
        await Assert.ThrowsAsync<InvalidDataException>(() => download.Download(new(bundle.Release, Asset(1)), cache));
        Assert.Empty(Directory.GetFiles(cache));
    }

    [Fact]
    public async Task CancellationDoesNotRequestOrPromoteDownload()
    {
        using var f = new UpdateFixture(); var bundle = f.CreateBundle();
        var handler = new UpdateHttpHandler(_ => throw new Exception("Canceled request was sent"));
        using var feed = new CommunityReleaseFeed(f.Trust, handler);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => feed.Download(new(bundle.Release, Asset(1)),
            Path.Combine(f.Root, "cache"), ct: canceled.Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(f.Root, "cache")));
    }

    internal static Uri Asset(long sequence) => new($"https://github.com/example/project/releases/download/v{sequence}/{CommunityRelease.ArchiveFileName}");
    internal static object Listing(CommunityRelease release, bool draft = false, bool prerelease = false,
        string? tag = null, string? manifestUri = null)
    {
        tag ??= "v" + release.Sequence;
        string prefix = $"https://github.com/example/project/releases/download/{tag}/";
        return new
        {
            draft, prerelease, assets = new[]
            {
                new { name = CommunityRelease.ManifestFileName, size = (long)JsonSerializer.SerializeToUtf8Bytes(release, CommunityRelease.Json).Length,
                    browser_download_url = manifestUri ?? prefix + CommunityRelease.ManifestFileName },
                new { name = CommunityRelease.ArchiveFileName, size = release.Size, browser_download_url = prefix + CommunityRelease.ArchiveFileName }
            }
        };
    }
    internal static UpdateHttpHandler FeedHandler(IEnumerable<CommunityRelease> values, Func<CommunityRelease, bool>? draft = null,
        Func<CommunityRelease, bool>? preview = null)
    {
        var releases = values.ToArray();
        return new(uri => uri.Host == "api.github.com"
            ? Json(releases.Select(r => Listing(r, draft?.Invoke(r) ?? false, preview?.Invoke(r) ?? false)).ToArray())
            : Json(releases.Single(r => uri.AbsolutePath.Contains($"/v{r.Sequence}/", StringComparison.Ordinal))));
    }
    internal static HttpResponseMessage Json(object value) => Bytes(JsonSerializer.SerializeToUtf8Bytes(value, CommunityRelease.Json));
    internal static HttpResponseMessage Bytes(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
    private static HttpResponseMessage Redirect(string uri)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(uri); return response;
    }
    private sealed class CollectProgress : IProgress<InstallProgress>
    {
        public InstallProgress? Last { get; private set; }
        public void Report(InstallProgress value) => Last = value;
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}

internal sealed class UpdateHttpHandler(Func<Uri, HttpResponseMessage> reply) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
        Requests.Add(request.RequestUri!);
        return Task.FromResult(reply(request.RequestUri!));
    }
}
