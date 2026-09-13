using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Cranberry.Launcher.Core;

namespace Cranberry.Launcher.Tests;

public sealed class ContentDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-content-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Data = "verified-game-test-content"u8.ToArray();
    // Lowercase in a manifest is valid; published object names must still be canonical uppercase.
    private static GameFile Entry => new("H1Z1.exe", Data.Length, Convert.ToHexString(SHA256.HashData(Data)).ToLowerInvariant());
    private static GameManifest Manifest => new(1, "content-origin-test", "0.0.118.208059", [Entry]);
    private const string ContentBase = "https://downloads.example.invalid/aug2017/content";

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static HttpClient Api(Handler handler)
    {
        var api = new HttpClient(handler) { BaseAddress = new("https://game.example.invalid/server/") };
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-api-token");
        api.DefaultRequestHeaders.Add("Cookie", "api-session=test-cookie");
        api.DefaultRequestHeaders.Add("X-Api-Only", "test-header");
        return api;
    }

    private void PartialFile()
    {
        string part = GameInstaller.SafePath(_root, $".cranberry-downloads/{Entry.Sha256}.part");
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        File.WriteAllBytes(part, Data[..5]);
    }

    private static HttpResponseMessage DownloadResponse(bool resume)
    {
        var content = new ByteArrayContent(resume ? Data[5..] : Data);
        if (resume) content.Headers.ContentRange = new ContentRangeHeaderValue(5, Data.Length - 1, Data.Length);
        return new(resume ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
    }

    [Theory]
    [InlineData("https://downloads.example.invalid", "https://downloads.example.invalid/")]
    [InlineData("https://downloads.example.invalid/", "https://downloads.example.invalid/")]
    [InlineData(ContentBase, ContentBase + "/")]
    [InlineData(ContentBase + "/", ContentBase + "/")]
    [InlineData("https://downloads.example.invalid/early%20august/content", "https://downloads.example.invalid/early%20august/content/")]
    public void ContentBasePreservesDirectoryPrefix(string configured, string expected)
    {
        Uri contentBase = LauncherSettings.ValidateContentBase(configured)!;
        Assert.Equal(expected, contentBase.AbsoluteUri);
        Assert.Equal(expected + Entry.Sha256.ToUpperInvariant(), new Uri(contentBase, Entry.Sha256.ToUpperInvariant()).AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingContentBaseKeepsApiDownloads(string? configured) => Assert.Null(LauncherSettings.ValidateContentBase(configured));

    [Theory]
    [InlineData("http://downloads.example.invalid/content/")]
    [InlineData("http://localhost/content/")]
    [InlineData("file:///content/")]
    [InlineData("ftp://downloads.example.invalid/content/")]
    [InlineData("//downloads.example.invalid/content/")]
    [InlineData("content/")]
    [InlineData("https://")]
    [InlineData("https://user:secret@downloads.example.invalid/content/")]
    [InlineData("https://@downloads.example.invalid/content/")]
    [InlineData("https://downloads.example.invalid/content?token=secret")]
    [InlineData("https://downloads.example.invalid/content?")]
    [InlineData("https://downloads.example.invalid/content#next")]
    [InlineData("https://downloads.example.invalid/content#")]
    [InlineData("https://downloads.example.invalid\\content\\")]
    [InlineData(" https://downloads.example.invalid/content/")]
    [InlineData("https://downloads.example.invalid/content/\r\n")]
    public void ContentBaseRejectsCredentialsRedirectHintsAndNonHttps(string configured)
    {
        Assert.Throws<InvalidDataException>(() => LauncherSettings.ValidateContentBase(configured));
        using var api = Api(new Handler(_ => throw new Exception("Invalid configuration must not make a request.")));
        Assert.Throws<InvalidDataException>(() => GameInstaller.Create(api, new() { ContentBaseUrl = configured }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredOriginDownloadsFullOrResumedContentWithoutApiCredentials(bool resume)
    {
        if (resume) PartialFile();
        var apiHandler = new Handler(request =>
        {
            Assert.Equal("https://game.example.invalid/server/api/manifest", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-api-token", request.Headers.Authorization.Parameter);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Manifest) };
        });
        using var api = Api(apiHandler);
        // The manifest remains on the authenticated API, matching MainForm.InstallOrPlay.
        var manifest = await api.GetFromJsonAsync<GameManifest>("api/manifest");
        var contentHandler = new Handler(request =>
        {
            Assert.Equal(ContentBase + "/" + Entry.Sha256.ToUpperInvariant(), request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("X-Api-Only"));
            if (resume) Assert.Equal(5, request.Headers.Range!.Ranges.Single().From);
            else Assert.Null(request.Headers.Range);
            return DownloadResponse(resume);
        });
        using (var installer = GameInstaller.Create(api, new()
        {
            ContentBaseUrl = ContentBase,
            CertificateSha256 = new string('A', 64)
        }, contentHandler))
        {
            await installer.Install(manifest!, _root, null);
        }

        Assert.Equal(Data, File.ReadAllBytes(Path.Combine(_root, "H1Z1.exe")));
        Assert.True(File.Exists(Path.Combine(_root, ".cranberry-install.json")));
        Assert.Equal(1, contentHandler.Calls);
        Assert.True(contentHandler.Disposed);
        Assert.False(apiHandler.Disposed);
        Assert.NotNull(await api.GetFromJsonAsync<GameManifest>("api/manifest"));
        Assert.Equal(2, apiHandler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultFactoryPreservesAuthenticatedRelativeApiRouteAndClientLifetime(bool resume)
    {
        if (resume) PartialFile();
        var handler = new Handler(request =>
        {
            Assert.Equal("https://game.example.invalid/server/api/content/" + Entry.Sha256, request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-api-token", request.Headers.Authorization!.Parameter);
            if (resume) Assert.Equal(5, request.Headers.Range!.Ranges.Single().From);
            else Assert.Null(request.Headers.Range);
            return DownloadResponse(resume);
        });
        using var api = Api(handler);
        using (var installer = GameInstaller.Create(api, new LauncherSettings()))
            await installer.Install(Manifest, _root, null);
        Assert.False(handler.Disposed);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(Data, File.ReadAllBytes(Path.Combine(_root, "H1Z1.exe")));
    }

    [Fact]
    public void ContentTransportCannotUseGamePinsCookiesDefaultCredentialsOrUncheckedRedirects()
    {
        using var handler = GameInstaller.CreateContentHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseDefaultCredentials);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
        Assert.Empty(handler.ClientCertificates);
    }

    [Theory]
    [InlineData("http://other.example.invalid/untrusted")]
    [InlineData("https://user:password@other.example.invalid/untrusted")]
    [InlineData("https://other.example.invalid/untrusted#fragment")]
    public async Task UnsafeContentRedirectFailsWithoutApiFallback(string location)
    {
        using var api = Api(new Handler(_ => throw new Exception("Content must not fall back to the game API.")));
        var handler = new Handler(_ => new(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri(location) }
        });
        using var installer = GameInstaller.Create(api, new() { ContentBaseUrl = ContentBase }, handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.Install(Manifest, _root, null));
        Assert.Equal(1, handler.Calls);
        Assert.False(File.Exists(Path.Combine(_root, "H1Z1.exe")));
        Assert.False(File.Exists(Path.Combine(_root, ".cranberry-install.json")));
    }

    [Fact]
    public async Task HttpsContentRedirectPreservesRangeWithoutAccountCredentials()
    {
        PartialFile();
        using var api = Api(new Handler(_ => throw new Exception("No API content request expected.")));
        var handler = new Handler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("X-Api-Only"));
            Assert.Equal(5, request.Headers.Range!.Ranges.Single().From);
            return request.RequestUri!.Host == "downloads.example.invalid"
                ? new(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://cdn.example.invalid/object") } }
                : DownloadResponse(resume: true);
        });
        using var installer = GameInstaller.Create(api, new() { ContentBaseUrl = ContentBase }, handler);
        await installer.Install(Manifest, _root, null);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(Data, File.ReadAllBytes(Path.Combine(_root, "H1Z1.exe")));
    }

    [Fact]
    public async Task ShortResponseResumesAutomaticallyAndNextInstallDownloadsNothing()
    {
        int attempt = 0;
        using var api = Api(new Handler(_ => throw new Exception("No API content request expected.")));
        var handler = new Handler(request =>
        {
            if (++attempt == 1)
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Data[..5]) };
            Assert.Equal(5, request.Headers.Range!.Ranges.Single().From);
            return DownloadResponse(resume: true);
        });
        using var installer = GameInstaller.Create(api, new() { ContentBaseUrl = ContentBase }, handler);
        await installer.Install(Manifest, _root, null);
        await installer.Install(Manifest, _root, null);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(Data, File.ReadAllBytes(Path.Combine(_root, "H1Z1.exe")));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task TransientStatusRetriesAndCorruptCompleteObjectRecovers(HttpStatusCode status)
    {
        int attempt = 0;
        using var api = Api(new Handler(_ => throw new Exception("No API content request expected.")));
        var handler = new Handler(request =>
        {
            Assert.Null(request.Headers.Range);
            return ++attempt switch
            {
                1 => new(status),
                2 => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[Data.Length]) },
                _ => DownloadResponse(resume: false)
            };
        });
        using var installer = GameInstaller.Create(api, new() { ContentBaseUrl = ContentBase }, handler);
        await installer.Install(Manifest, _root, null);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(Data, File.ReadAllBytes(Path.Combine(_root, "H1Z1.exe")));
    }

    [Fact]
    public async Task RedirectLoopIsBoundedAndPersistentFailureKeepsPartialFile()
    {
        using var api = Api(new Handler(_ => throw new Exception("No API content request expected.")));
        var loop = new Handler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/again", UriKind.Relative) } });
        using (var installer = GameInstaller.Create(api, new() { ContentBaseUrl = ContentBase }, loop))
            await Assert.ThrowsAsync<InvalidDataException>(() => installer.Install(Manifest, _root, null));
        Assert.Equal(4, loop.Calls);
        PartialFile();
        var unavailable = new Handler(_ => new(HttpStatusCode.ServiceUnavailable));
        using (var installer = GameInstaller.Create(api, new() { ContentBaseUrl = ContentBase }, unavailable))
            await Assert.ThrowsAsync<HttpRequestException>(() => installer.Install(Manifest, _root, null));
        Assert.Equal(3, unavailable.Calls);
        Assert.Equal(Data[..5], File.ReadAllBytes(GameInstaller.SafePath(_root, $".cranberry-downloads/{Entry.Sha256}.part")));
    }

    [Fact]
    public async Task ContentOriginStillVerifiesHashesBeforeReplacingClient()
    {
        Directory.CreateDirectory(_root);
        string target = Path.Combine(_root, "H1Z1.exe");
        File.WriteAllText(target, "keep existing client");
        using var api = Api(new Handler(_ => throw new Exception("Unexpected API download.")));
        using var installer = GameInstaller.Create(api, new() { ContentBaseUrl = ContentBase },
            new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[Data.Length]) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.Install(Manifest, _root, null));
        Assert.Equal("keep existing client", File.ReadAllText(target));
        Assert.False(File.Exists(Path.Combine(_root, ".cranberry-install.json")));
    }

    [Fact]
    public async Task IgnoredRangeRestartsAndVerifiesTheFullObject()
    {
        PartialFile();
        using var api = Api(new Handler(_ => throw new Exception("Unexpected API download.")));
        using var installer = GameInstaller.Create(api, new() { ContentBaseUrl = ContentBase }, new Handler(request =>
        {
            Assert.Equal(5, request.Headers.Range!.Ranges.Single().From);
            return DownloadResponse(resume: false);
        }));
        await installer.Install(Manifest, _root, null);
        Assert.Equal(Data, File.ReadAllBytes(Path.Combine(_root, "H1Z1.exe")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContentResumeRejectsWrongOffsetOrObjectLength(bool wrongLength)
    {
        PartialFile();
        using var api = Api(new Handler(_ => throw new Exception("Unexpected API download.")));
        using var installer = GameInstaller.Create(api, new() { ContentBaseUrl = ContentBase }, new Handler(_ =>
        {
            var content = new ByteArrayContent(Data[5..]);
            content.Headers.ContentRange = new ContentRangeHeaderValue(wrongLength ? 5 : 4, Data.Length - 1,
                wrongLength ? Data.Length + 1 : Data.Length);
            return new(HttpStatusCode.PartialContent) { Content = content };
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.Install(Manifest, _root, null));
        Assert.False(File.Exists(Path.Combine(_root, "H1Z1.exe")));
    }

    [Fact]
    public void OlderSettingsDefaultToApiAndExplicitContentSettingRoundTrips()
    {
        Assert.Equal("", JsonSerializer.Deserialize<LauncherSettings>("{}")!.ContentBaseUrl);
        string path = Path.Combine(_root, "launcher.json");
        var settings = new LauncherSettings { ContentBaseUrl = ContentBase };
        LauncherProfile.Save(path, settings);
        Assert.Equal(ContentBase, LauncherProfile.Load(path, Path.Combine(_root, "missing-package.json")).ContentBaseUrl);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
