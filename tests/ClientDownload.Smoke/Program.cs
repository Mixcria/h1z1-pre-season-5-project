using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Cranberry.Launcher.Core;

if (args.Length is < 2 or > 3)
    throw new ArgumentException("Usage: ClientDownload.Smoke <built-package> <new-test-directory> [existing-client-to-seed]");
string package = Path.GetFullPath(args[0]), evidence = Path.GetFullPath(args[1]);
if (Directory.Exists(evidence)) throw new IOException("Choose a new test directory.");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
string manifestPath = Path.Combine(package, "package", "game-manifest.json");
var release = JsonSerializer.Deserialize<LocalEdition.Release>(File.ReadAllText(Path.Combine(package, "local-edition.json")))!;
if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath))).Equals(release.GameManifestSha256, StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("The package manifest hash changed.");
var manifest = JsonSerializer.Deserialize<GameManifest>(File.ReadAllText(manifestPath), json)!;
GameInstaller.Validate(manifest);
var download = JsonSerializer.Deserialize<DownloadConfiguration>(File.ReadAllText(Path.Combine(package, "package", "download-host.json")), json)!.Validate();
if (string.IsNullOrEmpty(download.ContentBaseUrl)) throw new InvalidDataException("No content origin supplied.");
string game = Path.Combine(evidence, "Game");
Directory.CreateDirectory(game);
int seeded = 0;
GameFile forced = manifest.Files.Where(f => f.Size > 1024 && !f.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).MinBy(f => f.Size)!;
if (args.Length == 3)
{
    // Create links only in the new test directory. Installer replacements promote a new
    // file over a link; they never write through the link into the existing installation.
    foreach (var file in manifest.Files)
    {
        if (file.Path == forced.Path) continue;
        string source = GameInstaller.SafePath(args[2], file.Path);
        if (!File.Exists(source)) continue;
        string target = GameInstaller.SafePath(game, file.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!Native.CreateHardLink(target, source, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        seeded++;
    }
}
Console.WriteLine($"Seeded {seeded} files. Downloading and verifying {manifest.Files.Count} manifest entries.");
using var api = new HttpClient { BaseAddress = new Uri("https://local-api-unused.invalid/") };
using var installer = GameInstaller.Create(api, new LauncherSettings { ContentBaseUrl = download.ContentBaseUrl });
var watch = Stopwatch.StartNew();
await installer.Install(manifest, game, new ProgressPrinter());
double installSeconds = watch.Elapsed.TotalSeconds;
Console.WriteLine("PASS: CDN installation completed with every expected size and SHA-256 verified.");
var network = new NoNetwork();
using var offlineHttp = new HttpClient(network) { BaseAddress = new Uri("https://downloads-forbidden.invalid/") };
using var offline = new GameInstaller(offlineHttp);
watch.Restart();
await offline.Install(manifest, game, new ProgressPrinter());
if (network.Requests != 0) throw new Exception("Offline installer attempted a download.");
Console.WriteLine("PASS: Complete real client verified with download requests forbidden.");
await File.WriteAllTextAsync(Path.Combine(evidence, "results.json"), JsonSerializer.Serialize(new
{
    completedUtc = DateTimeOffset.UtcNow, buildId = manifest.BuildId, files = manifest.Files.Count,
    bytes = manifest.Files.Sum(f => f.Size), seededFiles = seeded, forcedDownloadPath = forced.Path,
    installSeconds, offlineSeconds = watch.Elapsed.TotalSeconds, offlineRequests = network.Requests,
    nativeGameLaunched = false
}, new JsonSerializerOptions { WriteIndented = true }));

sealed class ProgressPrinter : IProgress<InstallProgress>
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    public void Report(InstallProgress progress)
    {
        if (_watch.Elapsed.TotalSeconds < 15) return;
        _watch.Restart();
        Console.WriteLine($"{progress.Complete * 100.0 / progress.Total:F1}% - {progress.Message}");
    }
}
sealed class NoNetwork : HttpMessageHandler
{
    public int Requests;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    { Interlocked.Increment(ref Requests); throw new InvalidOperationException("Download attempted during offline verification."); }
}
static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateHardLink(string newName, string existingName, IntPtr attributes);
}
