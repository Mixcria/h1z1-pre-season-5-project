using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cranberry.Launcher.Core;

/// <summary>Public GitHub release discovery. No launcher credentials, cookies or certificate bypasses.</summary>
public sealed class CommunityReleaseFeed : IDisposable
{
    private const int MaximumJson = 2 * 1024 * 1024;
    private const int MaximumManifest = 64 * 1024;
    private readonly CommunityUpdateSettings _settings;
    private readonly HttpClient _http;

    public CommunityReleaseFeed(CommunityUpdateSettings settings, HttpMessageHandler? handler = null)
    {
        settings.Validate();
        using var key = CommunityRelease.ReadPublicKey(settings.PublicKey);
        _settings = settings;
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
            Credentials = null, AutomaticDecompression = DecompressionMethods.None
        }) { Timeout = TimeSpan.FromMinutes(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Cranberry-Community-Updater/1");
    }

    public async Task<CommunityReleaseDownload?> Check(long newerThan, CancellationToken ct = default)
    {
        if (newerThan < 0) throw new ArgumentOutOfRangeException(nameof(newerThan));
        var candidates = new Dictionary<long, CommunityReleaseDownload>();
        for (int page = 1; page <= 10; page++)
        {
            var uri = new Uri($"https://api.github.com/repos/{_settings.Repository}/releases?per_page=100&page={page}");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            byte[] bytes = await ReadBounded(response.Content, MaximumJson, ct);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 100)
                throw new InvalidDataException("Invalid GitHub release listing.");
            foreach (var item in document.RootElement.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                if (!TryBoolean(item, "draft", out bool draft) || draft
                    || !TryBoolean(item, "prerelease", out bool prerelease)
                    || (prerelease && !_settings.IncludePrereleases)) continue;
                CommunityReleaseDownload? candidate;
                try { candidate = await ReadCandidate(item, ct); }
                catch (Exception ex) when (ex is InvalidDataException or JsonException or HttpRequestException)
                { continue; } // An unrelated, unsigned or malformed release cannot authorize execution.
                if (candidate is null) continue;
                if (candidates.TryGetValue(candidate.Release.Sequence, out var prior)
                    && !prior.Release.SigningPayload().AsSpan().SequenceEqual(candidate.Release.SigningPayload()))
                    throw new InvalidDataException("Conflicting signed community releases use the same sequence.");
                candidates[candidate.Release.Sequence] = candidate;
            }
            if (document.RootElement.GetArrayLength() < 100)
                return candidates.Values.Where(c => c.Release.Sequence > Math.Max(newerThan, _settings.Sequence))
                    .OrderByDescending(c => c.Release.Sequence).FirstOrDefault();
        }
        throw new InvalidDataException("The GitHub release listing exceeds the updater limit.");
    }

    private async Task<CommunityReleaseDownload?> ReadCandidate(JsonElement item, CancellationToken ct)
    {
        if (!item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array
            || assets.GetArrayLength() > 128) return null;
        var manifests = assets.EnumerateArray().Where(a => String(a, "name") == CommunityRelease.ManifestFileName).ToArray();
        var archives = assets.EnumerateArray().Where(a => String(a, "name") == CommunityRelease.ArchiveFileName).ToArray();
        if (manifests.Length != 1 || archives.Length != 1) return null;
        if (!TrySize(manifests[0], out long manifestSize) || manifestSize is < 1 or > MaximumManifest
            || !TrySize(archives[0], out long archiveSize) || archiveSize is < 1 or > CommunityRelease.MaximumSize)
            return null;
        Uri manifestUri = AssetUri(String(manifests[0], "browser_download_url"), CommunityRelease.ManifestFileName);
        Uri archiveUri = AssetUri(String(archives[0], "browser_download_url"), CommunityRelease.ArchiveFileName);
        using var response = await OpenAsset(manifestUri, ct);
        byte[] bytes = await ReadBounded(response.Content, MaximumManifest, ct);
        if (bytes.LongLength != manifestSize) throw new InvalidDataException("Release manifest length changed.");
        var release = JsonSerializer.Deserialize<CommunityRelease>(bytes, CommunityRelease.Json)
            ?? throw new InvalidDataException("Missing community release metadata.");
        release.Verify(_settings.PublicKey);
        if (release.Size != archiveSize) throw new InvalidDataException("Release archive length does not match its signed identity.");
        return new(release, archiveUri);
    }

    public async Task<string> Download(CommunityReleaseDownload candidate, string cacheDir,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        candidate.Release.Verify(_settings.PublicKey);
        Uri uri = AssetUri(candidate.ArchiveUri.AbsoluteUri, CommunityRelease.ArchiveFileName);
        Directory.CreateDirectory(cacheDir);
        string filename = $"{candidate.Release.Sequence}-{candidate.Release.Sha256}.zip";
        string target = GameInstaller.SafePath(cacheDir, filename);
        if (await Matches(target, candidate.Release, ct)) return target;
        if (File.Exists(target)) throw new InvalidDataException("An existing cached update has an invalid hash.");
        string temporary = GameInstaller.SafePath(cacheDir, $".download-{Guid.NewGuid():N}.part");
        try
        {
            using var response = await OpenAsset(uri, ct);
            if (response.Content.Headers.ContentLength is long length && length != candidate.Release.Size)
                throw new InvalidDataException("Community update download length does not match its signature.");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[128 * 1024];
                long count = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    count += read;
                    if (count > candidate.Release.Size) throw new InvalidDataException("Community update exceeds its signed length.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress?.Report(new("Downloading community update", count, candidate.Release.Size));
                }
                if (count != candidate.Release.Size
                    || Convert.ToHexString(hash.GetHashAndReset()) != candidate.Release.Sha256)
                    throw new InvalidDataException("Community update download failed its signed hash check.");
                await output.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            GameInstaller.SafePath(cacheDir, filename);
            File.Move(temporary, target);
            return target;
        }
        finally
        {
            // Only this invocation's single temporary file is eligible for cleanup.
            if (File.Exists(temporary)) File.Delete(GameInstaller.SafePath(cacheDir, Path.GetFileName(temporary)));
        }
    }

    private async Task<HttpResponseMessage> OpenAsset(Uri initial, CancellationToken ct)
    {
        Uri uri = initial;
        for (int redirects = 0; redirects <= 4; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirects == 4) throw new InvalidDataException("Invalid GitHub asset redirect.");
                Uri next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!AllowedRedirect(next, initial)) throw new InvalidDataException("GitHub asset redirected outside its allowed origin.");
                uri = next;
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("Too many GitHub asset redirects.");
    }

    private Uri AssetUri(string? value, string filename)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !Secure(uri)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Query.Length != 0 || !uri.AbsolutePath.StartsWith($"/{_settings.Repository}/releases/download/", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.EndsWith("/" + filename, StringComparison.Ordinal)
            || !SafeUrlPath(uri))
            throw new InvalidDataException("Community release asset is outside the configured GitHub repository.");
        return uri;
    }

    private static bool AllowedRedirect(Uri uri, Uri initial)
    {
        if (!Secure(uri) || !SafeUrlPath(uri)) return false;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.Equals(initial.AbsolutePath, StringComparison.Ordinal) && uri.Query.Length == 0;
        return (uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            && uri.AbsolutePath.StartsWith("/github-production-release-asset", StringComparison.Ordinal)
            && uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 3;
    }

    private static bool Secure(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    private static bool SafeUrlPath(Uri uri) => !uri.AbsolutePath.Contains("%2f", StringComparison.OrdinalIgnoreCase)
        && !uri.AbsolutePath.Contains("%5c", StringComparison.OrdinalIgnoreCase)
        && !Uri.UnescapeDataString(uri.AbsolutePath).Split('/').Any(p => p is "." or ".." || p.Contains('\\'));
    private static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static bool TryBoolean(JsonElement value, string name, out bool result)
    {
        result = false;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var field)
            || field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        result = field.GetBoolean(); return true;
    }
    private static bool TrySize(JsonElement value, out long size)
    {
        size = 0;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("size", out var field)
            && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out size);
    }
    private static async Task<byte[]> ReadBounded(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("Community update JSON exceeds its limit.");
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Community update JSON exceeds its limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    private static Task<bool> Matches(string path, CommunityRelease release, CancellationToken ct) =>
        GameInstaller.Matches(path, new GameFile(Path.GetFileName(path), release.Size, release.Sha256), ct);
    public void Dispose() => _http.Dispose();
}
