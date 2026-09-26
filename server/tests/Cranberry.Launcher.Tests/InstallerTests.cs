using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Cranberry.Launcher.Core;

namespace Cranberry.Launcher.Tests;

public sealed class InstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-install-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Data = "verified-game-test-content"u8.ToArray();
    private static GameFile Entry => new("H1Z1.exe", Data.Length, Convert.ToHexString(SHA256.HashData(Data)));
    private static GameManifest Manifest => new(1, "test", "0.0.118.208059", [Entry]);
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> response) => new(new Handler(response)) { BaseAddress = new("https://localhost/") };

    [Theory]
    [InlineData("../outside")][InlineData("/absolute")][InlineData("C:/outside")][InlineData("Resources/../../outside")]
    [InlineData("x\\..\\outside")][InlineData("file:stream")][InlineData("CON.txt")][InlineData("assets/file.")]
    public void ManifestPathsCannotEscapeOrUseWindowsSpecialPaths(string path) => Assert.Throws<InvalidDataException>(() => GameInstaller.SafePath(_root, path));

    [Fact]
    public async Task CorruptDownloadNeverReplacesExistingClientOrWritesReceipt()
    {
        Directory.CreateDirectory(_root); string target = Path.Combine(_root, "H1Z1.exe"); File.WriteAllText(target, "keep me");
        using var http = Http(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[Data.Length]) });
        await Assert.ThrowsAsync<InvalidDataException>(() => new GameInstaller(http).Install(Manifest, _root, null));
        Assert.Equal("keep me", File.ReadAllText(target));
        Assert.False(File.Exists(Path.Combine(_root, ".cranberry-install.json")));
    }

    [Fact]
    public async Task InterruptedDownloadResumesAndRepairPreservesPersonalOptions()
    {
        string part = GameInstaller.SafePath(_root, $".cranberry-downloads/{Entry.Sha256}.part");
        Directory.CreateDirectory(Path.GetDirectoryName(part)!); File.WriteAllBytes(part, Data[..5]);
        File.WriteAllText(Path.Combine(_root, "UserOptions.ini"), "my settings");
        using var http = Http(request =>
        {
            Assert.Equal(5, request.Headers.Range!.Ranges.Single().From);
            var content = new ByteArrayContent(Data[5..]); content.Headers.ContentRange = new ContentRangeHeaderValue(5, Data.Length - 1, Data.Length);
            return new(HttpStatusCode.PartialContent) { Content = content };
        });
        await new GameInstaller(http).Install(Manifest, _root, null);
        Assert.Equal(Data, File.ReadAllBytes(Path.Combine(_root, "H1Z1.exe")));
        Assert.Equal("my settings", File.ReadAllText(Path.Combine(_root, "UserOptions.ini")));
        Assert.True(File.Exists(Path.Combine(_root, ".cranberry-install.json")));
    }

    [Fact]
    public async Task CorrectInstallationPerformsNoDownloadAndWrongBuildIsRefused()
    {
        Directory.CreateDirectory(_root); File.WriteAllBytes(Path.Combine(_root, "H1Z1.exe"), Data);
        using var http = Http(_ => throw new Exception("Unexpected download"));
        await new GameInstaller(http).Install(Manifest, _root, null, verifyOnly: true);
        Assert.Throws<InvalidDataException>(() => GameInstaller.Validate(Manifest with { ClientVersion = "2019" }));
        Assert.Throws<InvalidDataException>(() => GameInstaller.Validate(Manifest with { Files = [Entry, Entry with { Path = "h1z1.exe" }] }));
    }

    [Fact]
    public void LaunchUsesArgumentListAndLeavesOriginalConfigurationUntouched()
    {
        Directory.CreateDirectory(_root); string original = "Server=old:42\n[Paths]\nPathScripts=abc\n";
        File.WriteAllText(Path.Combine(_root, "ClientConfig.ini"), original);
        var info = GameProcess.StartInfo(_root, new("cb1." + new string('A', 64), "127.0.0.1:40404", "test"));
        Assert.False(info.UseShellExecute); Assert.Equal(6, info.ArgumentList.Count);
        Assert.Equal(original, File.ReadAllText(Path.Combine(_root, "ClientConfig.ini")));
        Assert.Contains("Server=127.0.0.1:40404", File.ReadAllText(Path.Combine(_root, "CranberryClient.ini")));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
