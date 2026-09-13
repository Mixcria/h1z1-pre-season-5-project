using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cranberry.Launcher.Core;

/// <summary>Resumable, authenticated updates with an atomic replacement and startup rollback.</summary>
public sealed class LauncherUpdater
{
    private const string ReceiptVariable = "CRANBERRY_LAUNCHER_UPDATE_RECEIPT";
    private const string HashVariable = "CRANBERRY_LAUNCHER_UPDATE_HASH";
    private const string SequenceVariable = "CRANBERRY_LAUNCHER_UPDATE_SEQUENCE";
    private readonly HttpClient _http;
    private readonly string _executable, _directory;
    private readonly long _sequence;
    private readonly Action<LauncherRelease> _verify;

    public LauncherUpdater(HttpClient http, string executable, long sequence)
        : this(http, executable, sequence, r => r.Verify()) { }

    internal LauncherUpdater(HttpClient http, string executable, long sequence, Action<LauncherRelease> verify)
    {
        _http = http; _executable = Path.GetFullPath(executable); _sequence = sequence; _verify = verify;
        _directory = GameInstaller.SafePath(Path.GetDirectoryName(_executable)!, ".cranberry-updates");
    }

    private string PathFor(string name) => GameInstaller.SafePath(_directory, name);

    public async Task<LauncherRelease?> Check(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var response = await _http.GetAsync("api/launcher/manifest", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 16 * 1024) throw new InvalidDataException("Launcher manifest is too large.");
        await response.Content.LoadIntoBufferAsync(16 * 1024, timeout.Token);
        var release = JsonSerializer.Deserialize<LauncherRelease>(await response.Content.ReadAsStringAsync(timeout.Token), LauncherRelease.Json)
            ?? throw new InvalidDataException("Launcher manifest is empty.");
        _verify(release);
        if (release.Sequence <= _sequence) return null;
        string failed = PathFor(release.Sha256 + ".failed");
        if (File.Exists(failed) && File.GetLastWriteTimeUtc(failed) > DateTime.UtcNow.AddHours(-6))
            throw new InvalidOperationException("The last launcher update could not start. Your previous version is ready; the update will retry later.");
        return release;
    }

    internal async Task<string> Download(LauncherRelease release, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        _verify(release);
        Directory.CreateDirectory(_directory);
        string target = PathFor(release.Sha256 + ".exe"), partial = PathFor(release.Sha256 + ".part");
        if (await release.Matches(target, ct)) return target;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset > release.Size) { File.Delete(partial); offset = 0; }
                if (offset < release.Size)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "api/launcher/content/" + release.Sha256);
                    if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode == HttpStatusCode.OK) offset = 0;
                    else if (response.StatusCode != HttpStatusCode.PartialContent
                        || response.Content.Headers.ContentRange is not { } range
                        || range.From != offset || range.To != release.Size - 1 || range.Length != release.Size)
                        throw new InvalidDataException("Invalid launcher download range.");
                    if (response.Content.Headers.ContentLength is long length && length != release.Size - offset)
                        throw new InvalidDataException("Unexpected launcher download size.");
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    await using var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append,
                        FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
                    byte[] buffer = new byte[128 * 1024];
                    int count;
                    while ((count = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        if (offset + count > release.Size) throw new InvalidDataException("Launcher download exceeded its signed size.");
                        await output.WriteAsync(buffer.AsMemory(0, count), ct); offset += count;
                        progress?.Report(new($"Updating launcher to {release.Version}", offset, release.Size));
                    }
                    await output.FlushAsync(ct);
                    output.Flush(flushToDisk: true);
                    if (offset != release.Size) throw new IOException("Launcher download was interrupted.");
                }
                if (!await release.Matches(partial, ct))
                {
                    File.Delete(partial);
                    throw new InvalidDataException("Launcher download failed verification. The current launcher has been kept.");
                }
                File.Move(partial, target, overwrite: true);
                return target;
            }
            catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested
                && ex is HttpRequestException or IOException && ex is not InvalidDataException)
            { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct); }
        }
        throw new IOException("Unable to download the launcher update.");
    }

    public async Task Apply(LauncherRelease release, string[] arguments, IProgress<InstallProgress>? progress,
        CancellationToken ct, Action? beforeReplace = null)
    {
        Directory.CreateDirectory(_directory);
        await using var lease = new FileStream(PathFor("update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (release.Sequence <= _sequence) throw new InvalidDataException("Cannot install an older launcher release.");
        using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        downloadTimeout.CancelAfter(TimeSpan.FromMinutes(15));
        string candidate = await Download(release, progress, downloadTimeout.Token);
        ct.ThrowIfCancellationRequested();
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string receipt = PathFor(nonce + ".ready"), backup = PathFor("previous.exe"), failed = PathFor(release.Sha256 + ".failed");
        // Record the attempt before replacement so an abnormal exit cannot create a restart loop.
        beforeReplace?.Invoke();
        File.WriteAllText(failed, release.Version);
        progress?.Report(new("Starting updated launcher...", release.Size, release.Size));
        await ReplaceAndStart(candidate, _executable, backup, async () =>
        {
            var start = new ProcessStartInfo(_executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(_executable)! };
            foreach (string arg in arguments) start.ArgumentList.Add(arg);
            start.Environment[ReceiptVariable] = nonce; start.Environment[HashVariable] = release.Sha256;
            start.Environment[SequenceVariable] = release.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var child = Process.Start(start) ?? throw new IOException("The updated launcher could not start.");
            try
            {
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                while (!File.Exists(receipt) || await File.ReadAllTextAsync(receipt, startup.Token) != nonce)
                {
                    if (child.HasExited) throw new IOException("The updated launcher exited before it was ready.");
                    await Task.Delay(100, startup.Token);
                }
            }
            catch
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
                throw;
            }
        });
        // The replacement is already running: a cleanup error must never trigger a rollback.
        try { File.Delete(failed); File.Delete(receipt); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static async Task ReplaceAndStart(string candidate, string executable, string backup, Func<Task> start)
    {
        File.Replace(candidate, executable, backup);
        try { await start(); }
        catch (Exception startupError)
        {
            try { File.Replace(backup, executable, null); }
            catch (Exception rollbackError)
            { throw new IOException($"Update startup and restoration failed. Your previous launcher is at {backup}.", new AggregateException(startupError, rollbackError)); }
            throw new IOException("The updated launcher could not start. Your previous version has been restored.", startupError);
        }
    }

    public static bool HasStartupReceipt => Environment.GetEnvironmentVariable(ReceiptVariable) is not null;

    /// <summary>Called after the replacement has built its main window and reached the UI event loop.</summary>
    public static void AcknowledgeStartup(long sequence)
    {
        string? nonce = Environment.GetEnvironmentVariable(ReceiptVariable), hash = Environment.GetEnvironmentVariable(HashVariable);
        string? expectedSequence = Environment.GetEnvironmentVariable(SequenceVariable);
        Environment.SetEnvironmentVariable(ReceiptVariable, null); Environment.SetEnvironmentVariable(HashVariable, null);
        Environment.SetEnvironmentVariable(SequenceVariable, null);
        if (nonce is null) return;
        if (nonce.Length != 64 || !nonce.All(char.IsAsciiHexDigit) || hash is null || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)
            || !long.TryParse(expectedSequence, out long expected) || expected != sequence)
            throw new InvalidDataException("Invalid launcher startup receipt.");
        using var stream = File.OpenRead(Environment.ProcessPath!);
        if (Convert.ToHexString(SHA256.HashData(stream)) != hash) throw new InvalidDataException("Updated launcher does not match the verified release.");
        string directory = GameInstaller.SafePath(AppContext.BaseDirectory, ".cranberry-updates");
        File.WriteAllText(GameInstaller.SafePath(directory, nonce + ".ready"), nonce);
    }
}
