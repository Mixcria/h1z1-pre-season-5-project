using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Cranberry.Launcher.Core;

if (args.Length != 2)
    throw new ArgumentException("Usage: LocalEdition.Smoke <built-package> <new-evidence-directory>");
string package = Path.GetFullPath(args[0]), evidence = Path.GetFullPath(args[1]);
if (Directory.Exists(evidence)) throw new IOException("Choose a new evidence directory.");
Directory.CreateDirectory(evidence);
string root = Path.Combine(evidence, "local-data");
var results = new List<string>();
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    results.Add(message); Console.WriteLine("PASS: " + message);
}
var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
string password = "Smoke-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
AuthSession first;
string certificate;
int processId;
var edition = LocalEdition.Prepare(package, root);
try
{
    Check(edition.Settings.InstallDirectory == Path.Combine(root, "Game"), "Game and saves use the isolated data directory");
    Check(new Uri(edition.Settings.ServerUrl).IsLoopback, "Local API uses loopback");
    Check(!File.Exists(Path.Combine(root, "state", "launcher", "social.json")), "No live account database is imported");
    bool duplicate = false;
    try { using var unexpected = LocalEdition.Prepare(package, root); }
    catch (IOException) { duplicate = true; }
    Check(duplicate, "A second launcher cannot take over the same data directory");
    certificate = edition.Settings.CertificateSha256;
    using (var independent = LocalEdition.Prepare(package, Path.Combine(evidence, "other-player")))
        Check(independent.Settings.CertificateSha256 != certificate && independent.Settings.JoinCode != edition.Settings.JoinCode,
            "Different installations receive distinct identity and credentials");

    await edition.Start();
    processId = edition.HostProcessId ?? throw new Exception("Host process is missing");
    using var http = LauncherConnection.CreateHttp(edition.Settings);
    Check((await http.GetAsync("health")).IsSuccessStatusCode, "Bundled host reaches health readiness");
    using (var wrongPin = LauncherConnection.CreateHttp(edition.Settings with { CertificateSha256 = new string('0', 64) }))
    {
        bool refused = false;
        try { using var response = await wrongPin.GetAsync("health"); }
        catch (HttpRequestException) { refused = true; }
        Check(refused, "API rejects an incorrect certificate pin");
    }
    Check((await http.GetAsync("api/manifest")).StatusCode == HttpStatusCode.Unauthorized, "Manifest requires a local account");
    using var identity = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "launcher-host.json")));
    string owner = identity.RootElement.GetProperty("OwnerCode").GetString()!;
    using (var registration = await http.PostAsJsonAsync("api/register", new Credentials("LocalTester", password, owner)))
    {
        registration.EnsureSuccessStatusCode();
        first = (await registration.Content.ReadFromJsonAsync<AuthSession>())!;
    }
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.Token);
    Check(first.Name == "LocalTester" && !string.IsNullOrWhiteSpace(first.AccountId), "Fresh local administrator registration succeeds");
    using (var accounts = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "data", "local-accounts.json"))))
        Check(accounts.RootElement.GetProperty("LocalAccountId").GetString() == first.AccountId, "First account owns the local developer console");
    var manifest = (await http.GetFromJsonAsync<GameManifest>("api/manifest"))!;
    var bundled = JsonSerializer.Deserialize<GameManifest>(File.ReadAllText(Path.Combine(package, "package", "game-manifest.json")), web)!;
    Check(JsonSerializer.Serialize(manifest) == JsonSerializer.Serialize(bundled), "Local API serves the exact bundled client manifest");
    var downloads = await DownloadConfiguration.ReadTrustedAsync(http, edition.Settings);
    var bundledDownloads = JsonSerializer.Deserialize<DownloadConfiguration>(File.ReadAllText(Path.Combine(package, "package", "download-host.json")), web)!;
    Check(downloads == bundledDownloads.Validate(), "Local API supplies the bundled Cloudflare content origin");
    await using (var tunnel = new GameTunnel())
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await tunnel.Connect(edition.Settings, first.Token, deadline.Token);
        using var response = await http.PostAsJsonAsync("api/launch", new LaunchRequest(tunnel.GatewayPort, BidirectionalDoors.ProtocolVersion), deadline.Token);
        response.EnsureSuccessStatusCode();
        var launch = (await response.Content.ReadFromJsonAsync<GameLaunch>(deadline.Token))!;
        Check(launch.BuildId == bundled.BuildId && launch.LoginAddress.StartsWith("127.0.0.1:") && launch.Ticket.Length > 0,
            "Authenticated local game tunnel issues a launch ticket for the bundled build");
        // Prepare the actual launch configuration without starting the native game.
        GameProcess.StartInfo(edition.Settings.InstallDirectory, launch);
        Check(Directory.Exists(Path.Combine(edition.Settings.InstallDirectory, "Logs"))
            && File.ReadAllText(Path.Combine(edition.Settings.InstallDirectory, "CranberryClient.ini")).Contains("LocalLogLevel=9"),
            "Fresh client launch enables the local startup log required by the door readiness helper");
        using var ready = await http.PostAsJsonAsync("api/client/doors-ready",
            new DoorClientReadyRequest(launch.Ticket, BidirectionalDoors.ProtocolVersion), deadline.Token);
        Check(ready.IsSuccessStatusCode, "Bundled host accepts readiness for the authenticated game launch");
    }
    // A complete installation must not contact any origin, even when CDN discovery is configured.
    string installed = Path.Combine(evidence, "offline-fixture");
    Directory.CreateDirectory(installed);
    byte[] fixture = "offline installer fixture"u8.ToArray();
    await File.WriteAllBytesAsync(Path.Combine(installed, "H1Z1.exe"), fixture);
    var small = new GameManifest(1, "smoke-offline", "0.0.118.208059", [new("H1Z1.exe", fixture.Length, Convert.ToHexString(SHA256.HashData(fixture)))]);
    var network = new NoNetwork();
    using (var offlineHttp = new HttpClient(network) { BaseAddress = new Uri("https://unavailable.invalid/") })
    using (var installer = new GameInstaller(offlineHttp))
        await installer.Install(small, installed, null);
    Check(network.Requests == 0, "Complete installation verifies with all download requests forbidden");
    var saved = edition.Settings with { Name = "LocalTester", VoiceVolume = 37 };
    LauncherProfile.Save(edition.ProfilePath, saved);
    string userConfig = "{\"movement\":{\"sprintAccel\":0.36}}";
    await File.WriteAllTextAsync(Path.Combine(root, "cranberry.json"), userConfig);
}
finally { edition.Dispose(); }
Check(!IsRunning(processId), "Closing the launcher stops its owned host process");
Check(Directory.GetFiles(Path.Combine(root, "logs"), "*.log").Any(p => File.ReadAllText(p).Contains("stopped")),
    "Host exits through the normal flush and shutdown path");
using (var reopened = LocalEdition.Prepare(package, root))
{
    Check(reopened.Settings.CertificateSha256 == certificate && reopened.Settings.Name == "LocalTester" && reopened.Settings.VoiceVolume == 37,
        "Reopening preserves local identity and player preferences");
    Check(File.ReadAllText(Path.Combine(root, "cranberry.json")).Contains("0.36"), "Release startup preserves user gameplay settings");
    await reopened.Start();
    using var http = LauncherConnection.CreateHttp(reopened.Settings);
    using var login = await http.PostAsJsonAsync("api/login", new Credentials("LocalTester", password));
    login.EnsureSuccessStatusCode();
    var second = (await login.Content.ReadFromJsonAsync<AuthSession>())!;
    Check(second.AccountId == first.AccountId, "Local account survives a server restart");
}
using (var conflict = LocalEdition.Prepare(package, root))
{
    var listener = new TcpListener(IPAddress.Loopback, new Uri(conflict.Settings.ServerUrl).Port);
    try
    {
        listener.Start(); bool refused = false;
        try { await conflict.Start(); }
        catch (IOException) { refused = true; }
        Check(refused && conflict.HostProcessId is null && listener.Server.IsBound, "Port conflict is reported without stopping the existing listener");
    }
    finally { listener.Stop(); }
}
await File.WriteAllTextAsync(Path.Combine(evidence, "results.json"), JsonSerializer.Serialize(new
{
    completedUtc = DateTimeOffset.UtcNow,
    packageRelease = JsonSerializer.Deserialize<LocalEdition.Release>(File.ReadAllText(Path.Combine(package, "local-edition.json")))!.ReleaseId,
    checks = results,
    nativeGameLaunched = false
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"All {results.Count} checks passed.");

static bool IsRunning(int pid)
{
    try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}
sealed class NoNetwork : HttpMessageHandler
{
    public int Requests;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    { Interlocked.Increment(ref Requests); throw new InvalidOperationException("Download attempted during an offline test."); }
}
