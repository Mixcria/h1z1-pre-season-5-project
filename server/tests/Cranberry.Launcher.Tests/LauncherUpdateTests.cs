using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Cranberry.Launcher.Core;
using Cranberry.Launcher.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cranberry.Launcher.Tests;

public sealed class LauncherUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-updater-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public LauncherUpdateTests() => Directory.CreateDirectory(_root);
    public void Dispose() { _key.Dispose(); Directory.Delete(_root, true); }
    private void Verify(LauncherRelease release) => release.Verify(_key.ExportSubjectPublicKeyInfo());
    private LauncherRelease Sign(byte[] data, long sequence = 2)
    {
        var release = new LauncherRelease(1, sequence, "2026.9.11.1", "win-x64", data.Length, Convert.ToHexString(SHA256.HashData(data)), "");
        return release with { Signature = Convert.ToBase64String(_key.SignData(release.SigningPayload(), HashAlgorithmName.SHA256)) };
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
    private HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> reply) => new(new Handler(reply)) { BaseAddress = new("https://test.invalid/") };
    private static HttpResponseMessage Manifest(LauncherRelease release) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(release, LauncherRelease.Json)) };

    [Fact]
    public void SignatureCoversAllExecutableMetadataAndRejectsForeignKeys()
    {
        var release = Sign("launcher"u8.ToArray()); Verify(release);
        foreach (var altered in new[] { release with { Sequence = 3 }, release with { Version = "changed" },
            release with { Size = release.Size + 1 }, release with { Sha256 = new('A', 64) },
            release with { Platform = "linux-x64" }, release with { Schema = 2 }, release with { Signature = "invalid" } })
            Assert.Throws<InvalidDataException>(() => Verify(altered));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidDataException>(() => release.Verify(other.ExportSubjectPublicKeyInfo()));
        Assert.Throws<InvalidDataException>(() => release.Verify());
    }

    [Fact]
    public async Task ChecksBeforeLoginRejectReplayAndHandleOlderServers()
    {
        var release = Sign("launcher"u8.ToArray());
        using var http = Http(request => { Assert.Null(request.Headers.Authorization); return Manifest(release); });
        Assert.Null(await new LauncherUpdater(http, Path.Combine(_root, "Launcher.exe"), 2, Verify).Check());
        Assert.Null(await new LauncherUpdater(http, Path.Combine(_root, "Launcher.exe"), 3, Verify).Check());
        Assert.Equal(release, await new LauncherUpdater(http, Path.Combine(_root, "Launcher.exe"), 1, Verify).Check());
        using var old = Http(_ => new(HttpStatusCode.NotFound));
        Assert.Null(await new LauncherUpdater(old, Path.Combine(_root, "Launcher.exe"), 1, Verify).Check());
        using var forged = Http(_ => Manifest(release with { Sequence = 9 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new LauncherUpdater(forged, Path.Combine(_root, "Launcher.exe"), 1, Verify).Check());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedDownloadResumesOrSafelyRestartsIfRangeIgnored(bool rangeSupported)
    {
        byte[] data = RandomNumberGenerator.GetBytes(8192);
        var release = Sign(data);
        string stage = Path.Combine(_root, ".cranberry-updates"); Directory.CreateDirectory(stage);
        await File.WriteAllBytesAsync(Path.Combine(stage, release.Sha256 + ".part"), data[..333]);
        using var http = Http(request =>
        {
            Assert.Equal("/api/launcher/content/" + release.Sha256, request.RequestUri!.AbsolutePath);
            Assert.Equal(333, request.Headers.Range!.Ranges.Single().From);
            var response = new HttpResponseMessage(rangeSupported ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { Content = new ByteArrayContent(rangeSupported ? data[333..] : data) };
            if (rangeSupported) response.Content.Headers.ContentRange = new(333, data.Length - 1, data.Length);
            return response;
        });
        string downloaded = await new LauncherUpdater(http, Path.Combine(_root, "Launcher.exe"), 1, Verify).Download(release, null, default);
        Assert.Equal(data, await File.ReadAllBytesAsync(downloaded));
    }

    [Fact]
    public async Task CorruptionCannotReplaceLauncherAndProfileIsUntouched()
    {
        byte[] data = "new launcher"u8.ToArray(); var release = Sign(data);
        string exe = Path.Combine(_root, "Launcher.exe"), profile = Path.Combine(_root, "launcher.json");
        await File.WriteAllTextAsync(exe, "old launcher"); await File.WriteAllTextAsync(profile, "saved settings");
        using var http = Http(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[data.Length]) });
        await Assert.ThrowsAsync<InvalidDataException>(() => new LauncherUpdater(http, exe, 1, Verify).Apply(release, [], null, default));
        Assert.Equal("old launcher", await File.ReadAllTextAsync(exe));
        Assert.Equal("saved settings", await File.ReadAllTextAsync(profile));
    }

    [Fact]
    public async Task WrongRangeAndOversizedBodyAreRejected()
    {
        byte[] data = "new launcher"u8.ToArray(); var release = Sign(data);
        using var wrongRange = Http(_ => new(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data) });
        await Assert.ThrowsAsync<InvalidDataException>(() => new LauncherUpdater(wrongRange, Path.Combine(_root, "Launcher.exe"), 1, Verify).Download(release, null, default));
        using var tooLarge = Http(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[data.Length + 1]) });
        await Assert.ThrowsAsync<InvalidDataException>(() => new LauncherUpdater(tooLarge, Path.Combine(_root, "Launcher.exe"), 1, Verify).Download(release, null, default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AtomicHandoffKeepsBackupAndRestoresItWhenStartupFails(bool starts)
    {
        string candidate = Path.Combine(_root, "candidate.exe"), exe = Path.Combine(_root, "Launcher.exe"), backup = Path.Combine(_root, "previous.exe");
        File.WriteAllText(candidate, "new"); File.WriteAllText(exe, "old");
        Task Launch()
        {
            Assert.Equal("new", File.ReadAllText(exe)); Assert.Equal("old", File.ReadAllText(backup));
            if (!starts) throw new IOException("fixture startup failure"); return Task.CompletedTask;
        }
        if (starts) await LauncherUpdater.ReplaceAndStart(candidate, exe, backup, Launch);
        else await Assert.ThrowsAsync<IOException>(() => LauncherUpdater.ReplaceAndStart(candidate, exe, backup, Launch));
        Assert.Equal(starts ? "new" : "old", File.ReadAllText(exe));
    }

    [Fact]
    public async Task ValidlySignedButUnstartableExecutableRollsBackAndDoesNotLoop()
    {
        byte[] data = "This is deliberately not an executable"u8.ToArray(); var release = Sign(data);
        string exe = Path.Combine(_root, "Launcher.exe"); File.WriteAllText(exe, "old launcher");
        using var http = Http(request => request.RequestUri!.AbsolutePath.EndsWith("manifest") ? Manifest(release)
            : new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        var updater = new LauncherUpdater(http, exe, 1, Verify);
        await Assert.ThrowsAsync<IOException>(() => updater.Apply(release, [], null, default));
        Assert.Equal("old launcher", File.ReadAllText(exe));
        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.Check());
    }

    [Fact]
    public async Task ConcurrentUpdaterCannotStartAnotherDownload()
    {
        var release = Sign("launcher"u8.ToArray()); string stage = Path.Combine(_root, ".cranberry-updates"); Directory.CreateDirectory(stage);
        using var lease = new FileStream(Path.Combine(stage, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var http = Http(_ => throw new InvalidOperationException("Must not contact the server while another updater owns the directory."));
        await Assert.ThrowsAsync<IOException>(() => new LauncherUpdater(http, Path.Combine(_root, "Launcher.exe"), 1, Verify).Apply(release, [], null, default));
    }

    [Fact]
    public async Task PublicFeedServesCachedMetadataAndResumableImmutableReleases()
    {
        string feed = Path.Combine(_root, "launcher-updates"); Directory.CreateDirectory(Path.Combine(feed, "content"));
        byte[] data = RandomNumberGenerator.GetBytes(16384); var release = Sign(data);
        void Publish(LauncherRelease r)
        {
            File.WriteAllBytes(Path.Combine(feed, "content", r.Sha256), data);
            File.WriteAllText(Path.Combine(feed, "content", r.Sha256 + ".json"), JsonSerializer.Serialize(r, LauncherRelease.Json));
            File.WriteAllText(Path.Combine(feed, "latest.json"), JsonSerializer.Serialize(r, LauncherRelease.Json));
        }
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build(); LauncherUpdateFeed.Map(app, _root, Verify); await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        Assert.Equal(HttpStatusCode.NoContent, (await http.GetAsync("api/launcher/manifest")).StatusCode);
        Publish(release);
        using var manifest = await http.GetAsync("api/launcher/manifest"); Assert.True(manifest.IsSuccessStatusCode);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "api/launcher/manifest"); conditional.Headers.IfNoneMatch.Add(manifest.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(conditional)).StatusCode);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, 1000), new ParallelOptions { MaxDegreeOfParallelism = 25 }, async (i, ct) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, i % 2 == 0 ? "api/launcher/manifest" : "api/launcher/content/" + release.Sha256);
            if (i % 2 != 0) request.Headers.Range = new RangeHeaderValue(100, 4195);
            using var response = await http.SendAsync(request, ct);
            Assert.Equal(i % 2 == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent, response.StatusCode);
            if (i % 2 != 0) Assert.Equal(data[100..4196], await response.Content.ReadAsByteArrayAsync(ct));
        });
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
        byte[] oldData = data; data = [.. data, 1];
        var newer = Sign(data, 3); Publish(newer);
        Assert.Contains("\"sequence\":3", await http.GetStringAsync("api/launcher/manifest"));
        Assert.Equal(oldData, await http.GetByteArrayAsync("api/launcher/content/" + release.Sha256));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("api/launcher/content/" + new string('0', 64))).StatusCode);
        await app.StopAsync();
    }
}
