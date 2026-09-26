using System.Net;
using System.Net.Http.Json;
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

public sealed class DownloadConfigurationTests
{
    private static LauncherSettings Settings => new() { ServerUrl = "https://game.invalid/" };
    private static DownloadConfiguration Config => new(1, "https://downloads.invalid/content", "https://downloads.invalid/content");
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
    private static HttpClient Api(Func<HttpRequestMessage, HttpResponseMessage> reply) => new(new Handler(reply)) { BaseAddress = new(Settings.ServerUrl) };

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task OldServersRemainCompatible(HttpStatusCode status)
    {
        using var api = Api(_ => new(status));
        Assert.Equal(DownloadConfiguration.Default, await DownloadConfiguration.ReadTrustedAsync(api, Settings));
    }

    [Fact]
    public async Task ReturningPlayersDiscoverNewLocationsAndRollbackWithoutSavingPreferences()
    {
        var config = Config;
        using var api = Api(request =>
        {
            Assert.Equal("https://game.invalid/api/downloads", request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(config) };
        });
        var saved = Settings with { Name = "ExistingPlayer", InstallDirectory = @"D:\Games\Cranberry" };
        Assert.Equal(Config.Validate(), await DownloadConfiguration.ReadTrustedAsync(api, saved));
        config = DownloadConfiguration.Default;
        Assert.Equal(config, await DownloadConfiguration.ReadTrustedAsync(api, saved));
        Assert.Equal("", saved.ContentBaseUrl);
        Assert.Equal("ExistingPlayer", saved.Name);
    }

    [Fact]
    public async Task ExplicitGameOriginRemainsAnOperatorOverride()
    {
        using var api = Api(_ => new(HttpStatusCode.OK) { Content = JsonContent.Create(Config) });
        var config = await DownloadConfiguration.ReadTrustedAsync(api, Settings with { ContentBaseUrl = "https://override.invalid/game/" });
        Assert.Equal("https://override.invalid/game/", config.ContentBaseUrl);
        Assert.Equal(Config.Validate().LauncherContentBaseUrl, config.LauncherContentBaseUrl);
    }

    [Theory]
    [InlineData("http://downloads.invalid/content/")]
    [InlineData("https://user:secret@downloads.invalid/content/")]
    [InlineData("https://downloads.invalid/content/?token=secret")]
    public async Task InvalidRemoteOriginCannotSilentlyFallBackToServer(string address)
    {
        using var api = Api(_ => new(HttpStatusCode.OK) { Content = JsonContent.Create(Config with { ContentBaseUrl = address }) });
        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadConfiguration.ReadTrustedAsync(api, Settings));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task FailedOrRedirectedConfigurationCannotSilentlyFallBack(HttpStatusCode status)
    {
        using var api = Api(_ => new(status));
        await Assert.ThrowsAsync<HttpRequestException>(() => DownloadConfiguration.ReadTrustedAsync(api, Settings));
    }

    [Fact]
    public async Task WrongApiAndOversizedConfigurationAreRejected()
    {
        using var api = Api(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 8193)) });
        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadConfiguration.ReadTrustedAsync(api, Settings with { ServerUrl = "https://other.invalid/" }));
        await Assert.ThrowsAsync<HttpRequestException>(() => DownloadConfiguration.ReadTrustedAsync(api, Settings));
    }

    [Fact]
    public async Task HostReadsAtomicConfigurationChangesWithoutRestartAndSendsNoStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-download-feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        DownloadConfigurationFeed.Map(app, root);
        try
        {
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/";
            using var api = new HttpClient { BaseAddress = new(address) };
            var settings = Settings with { ServerUrl = address };
            Assert.Equal(DownloadConfiguration.Default, await DownloadConfiguration.ReadTrustedAsync(api, settings));
            string path = Path.Combine(root, "download-host.json");
            foreach (var config in new[] { Config, DownloadConfiguration.Default })
            {
                await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(config, DownloadConfiguration.Json));
                File.Move(path + ".tmp", path, overwrite: true);
                Assert.Equal(config.Validate(), await DownloadConfiguration.ReadTrustedAsync(api, settings));
            }
            using var response = await api.GetAsync("api/downloads");
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        finally { await app.StopAsync(); Directory.Delete(root, true); }
    }
}
