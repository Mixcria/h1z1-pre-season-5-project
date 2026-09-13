using System.Net;
using System.Text.Json;

namespace Cranberry.Launcher.Core;

/// <summary>Download locations supplied by the trusted game API, independently of player preferences.</summary>
public sealed record DownloadConfiguration(int Version, string ContentBaseUrl, string LauncherContentBaseUrl)
{
    public static readonly DownloadConfiguration Default = new(1, "", "");
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DownloadConfiguration Validate()
    {
        if (Version != 1) throw new InvalidDataException("Unsupported download configuration.");
        return this with
        {
            ContentBaseUrl = LauncherSettings.ValidateContentBase(ContentBaseUrl)?.AbsoluteUri ?? "",
            LauncherContentBaseUrl = LauncherSettings.ValidateContentBase(LauncherContentBaseUrl)?.AbsoluteUri ?? ""
        };
    }

    public static async Task<DownloadConfiguration> ReadTrustedAsync(HttpClient api, LauncherSettings settings,
        CancellationToken ct = default)
    {
        // The caller uses LauncherConnection's TLS-verified/pinned API client. Never discover
        // an origin from the game manifest, a redirect, or an unauthenticated third party.
        Uri server = LauncherSettings.ValidateServer(settings.ServerUrl);
        if (api.BaseAddress != server) throw new InvalidDataException("Download configuration must use the trusted game API.");
        Uri? localOverride = LauncherSettings.ValidateContentBase(settings.ContentBaseUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var response = await api.GetAsync("api/downloads", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        DownloadConfiguration config;
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
            config = Default; // Older hosts have no content configuration endpoint.
        else
        {
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(8192, timeout.Token).ConfigureAwait(false);
            config = (JsonSerializer.Deserialize<DownloadConfiguration>(
                await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false), Json)
                ?? throw new InvalidDataException("Download configuration is empty.")).Validate();
        }
        // Explicit operator overrides remain possible. Discovered URLs are never saved to
        // player preferences, so existing players pick up changes and rollbacks on next use.
        return localOverride is null ? config : config with { ContentBaseUrl = localOverride.AbsoluteUri };
    }
}
