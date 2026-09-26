using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cranberry.Launcher.Core;

/// <summary>Only manifest-listed files can be written. Each replacement is hashed before atomic promotion.</summary>
public sealed class GameInstaller(HttpClient http) : IDisposable
{
    private readonly bool _usesContentOrigin;
    private readonly int _concurrentDownloads = 1;
    private bool _disposed;

    private GameInstaller(HttpClient http, bool usesContentOrigin, int concurrentDownloads) : this(http)
    {
        _usesContentOrigin = usesContentOrigin;
        _concurrentDownloads = concurrentDownloads;
    }

    /// <summary>
    /// Borrows the authenticated API client by default. An explicitly configured content origin
    /// instead gets a private client owned by this installer; disposing never closes the API client.
    /// </summary>
    public static GameInstaller Create(HttpClient apiHttp, LauncherSettings settings) => Create(apiHttp, settings, null);

    internal static GameInstaller Create(HttpClient apiHttp, LauncherSettings settings, HttpMessageHandler? contentHandler)
    {
        ArgumentNullException.ThrowIfNull(apiHttp);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.ConcurrentDownloads is < 1 or > 8)
            throw new InvalidDataException("ConcurrentDownloads must be between 1 and 8.");
        Uri? contentBase = LauncherSettings.ValidateContentBase(settings.ContentBaseUrl);
        if (contentBase is null) return new GameInstaller(apiHttp);
        return new GameInstaller(CreateContentHttp(contentBase, contentHandler),
            usesContentOrigin: true, settings.ConcurrentDownloads);
    }

    internal static HttpClient CreateContentHttp(Uri contentBase, HttpMessageHandler? handler = null) => new(handler ?? CreateContentHandler())
    {
        BaseAddress = contentBase,
        Timeout = TimeSpan.FromMinutes(20),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
    };

    internal static HttpClientHandler CreateContentHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        MaxConnectionsPerServer = 8,
        Credentials = null
        // Normal platform TLS verification: no game certificate callback or pin.
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_usesContentOrigin) http.Dispose();
    }

    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Contains(':')
            || relative.Any(c => c < 32) || relative.Split('/').Any(p => string.IsNullOrEmpty(p)
                || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0
                || IsDevice(p)))
            throw new InvalidDataException("Unsafe game file path.");
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes installation.");
        for (string? current = result; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Game installation cannot pass through a symbolic link or junction.");
        return result;
    }

    private static bool IsDevice(string name)
    {
        string stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4
            && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsAsciiDigit(stem[3]);
    }

    public static void Validate(GameManifest manifest)
    {
        if (manifest.Version != 1 || manifest.ClientVersion != "0.0.118.208059"
            || manifest.Files is null || manifest.Files.Count is < 1 or > 100_000
            || !manifest.Files.Any(f => f.Path == "H1Z1.exe")
            || manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count)
            throw new InvalidDataException("This is not a supported August 2017 game manifest.");
        foreach (var file in manifest.Files)
        {
            SafePath(Path.GetTempPath(), file.Path);
            if (file.Size < 0 || file.Size > 20L * 1024 * 1024 * 1024 || file.Sha256.Length != 64
                || file.Sha256.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("Invalid file metadata.");
        }
    }

    public static async Task<bool> Matches(string path, GameFile file, CancellationToken cancellation = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation))
            .Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public async Task Install(GameManifest manifest, string directory, IProgress<InstallProgress>? progress,
        CancellationToken cancellation = default, bool verifyOnly = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Validate(manifest);
        Directory.CreateDirectory(directory);
        // A second launcher must not repair files while this one is installing or running the game.
        await using var lease = new FileStream(SafePath(directory, ".cranberry.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        long total = manifest.Files.Sum(f => f.Size), done = 0;
        long[] completed = new long[manifest.Files.Count];
        var progressGate = new object();
        void Report(int index, string message, long bytes)
        {
            lock (progressGate)
            {
                long current = Math.Clamp(bytes, 0, manifest.Files[index].Size);
                done += current - completed[index];
                completed[index] = current;
                progress?.Report(new(message, done, total));
            }
        }
        // A hash names the resume file. Keep entries with identical content in the same
        // worker so duplicate assets can never race over that shared partial file.
        var groups = Enumerable.Range(0, manifest.Files.Count)
            .GroupBy(i => manifest.Files[i].Sha256, StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(groups, new ParallelOptions
        {
            MaxDegreeOfParallelism = verifyOnly ? 1 : _concurrentDownloads,
            CancellationToken = cancellation
        }, async (indices, ct) =>
        {
            foreach (int index in indices)
            {
                ct.ThrowIfCancellationRequested();
                GameFile file = manifest.Files[index];
                string target = SafePath(directory, file.Path);
                Report(index, $"Checking {file.Path}", 0);
                if (!await Matches(target, file, ct).ConfigureAwait(false))
                {
                    if (verifyOnly) throw new InvalidDataException($"Install / repair is needed: {file.Path}");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    string part = SafePath(directory, $".cranberry-downloads/{file.Sha256}.part");
                    Directory.CreateDirectory(Path.GetDirectoryName(part)!);
                    await DownloadVerified(file, part, 0, file.Size,
                        new FileProgress(p => Report(index, p.Message, p.Complete)), ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    SafePath(directory, file.Path);
                    File.Move(part, target, overwrite: true);
                }
                Report(index, $"Verified {file.Path}", file.Size);
            }
        }).ConfigureAwait(false);
        string receipt = SafePath(directory, ".cranberry-install.json");
        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new { manifest.BuildId, VerifiedUtc = DateTimeOffset.UtcNow }), cancellation);
    }

    private sealed class FileProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }

    private async Task DownloadVerified(GameFile file, string part, long done, long total,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        const int attempts = 3;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await Download(file, part, done, total, progress, ct);
                if (await Matches(part, file, ct)) return;
                // Only a complete corrupt object is discarded. Short/transient responses keep
                // their validated range prefix so the next attempt requests the remaining bytes.
                File.Delete(part);
                if (attempt == attempts)
                    throw new InvalidDataException($"Verification failed for {file.Path} after {attempts} attempts.");
            }
            catch (HttpRequestException ex) when (attempt < attempts &&
                (ex.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                    || (int)ex.StatusCode.Value >= 500)) { }
            catch (IOException) when (attempt < attempts) { }
            catch (OperationCanceledException) when (attempt < attempts && !ct.IsCancellationRequested) { }
            progress?.Report(new($"Retrying {file.Path} ({attempt + 1}/{attempts})", done, total));
            await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
        }
    }

    internal static async Task<HttpResponseMessage> RequestContent(HttpClient http, string route, long offset,
        bool usesContentOrigin, CancellationToken ct)
    {
        Uri uri = new(http.BaseAddress!, route);
        for (int redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri)
            {
                Version = http.DefaultRequestVersion,
                VersionPolicy = http.DefaultVersionPolicy
            };
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!usesContentOrigin || response.StatusCode is not (HttpStatusCode.MovedPermanently
                or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect)) return response;
            Uri? location = response.Headers.Location;
            response.Dispose();
            if (redirects >= 3 || location is null)
                throw new InvalidDataException("Content redirect is missing or exceeds three hops.");
            uri = new Uri(uri, location);
            if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidDataException("Content redirects must use HTTPS without credentials or fragments.");
            // This is the private content client: no game certificate pin, account headers,
            // cookies or default credentials are forwarded to an object-store/CDN redirect.
        }
    }

    private async Task Download(GameFile file, string part, long done, long total,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        long offset = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset == file.Size && await Matches(part, file, ct)) return;
        if (offset >= file.Size) offset = 0;
        // Publish-Game uses uppercase SHA-256 object names. Object stores have case-sensitive keys.
        string route = _usesContentOrigin ? file.Sha256.ToUpperInvariant() : "api/content/" + file.Sha256;
        using var response = await RequestContent(http, route, offset, _usesContentOrigin, ct);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (response.Content.Headers.ContentRange?.From != offset
                || response.Content.Headers.ContentRange?.Length != file.Size)
                throw new InvalidDataException("Download server returned an invalid resume range.");
        }
        else offset = 0;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(part, offset == 0 ? FileMode.Create : FileMode.Open,
            FileAccess.Write, FileShare.None, 1024 * 1024, true);
        output.Position = offset;
        byte[] buffer = new byte[1024 * 1024];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Position + count > file.Size) throw new InvalidDataException("Download exceeds expected size.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            progress?.Report(new($"Downloading {file.Path}", done + output.Position, total));
        }
        await output.FlushAsync(ct);
        if (output.Position != file.Size)
            throw new EndOfStreamException($"Download of {file.Path} ended early; saved {output.Position} of {file.Size} bytes.");
    }
}
