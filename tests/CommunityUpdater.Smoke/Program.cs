using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Cranberry.Launcher.Core;

if (args.FirstOrDefault() == "--child")
{
    // A real separate process exercises the same private ownership pipe/host receipt as the GUI.
    var session = CommunityChildSession.Open(args[1] + Path.DirectorySeparatorChar, args[2])
        ?? throw new Exception("Child ownership session missing.");
    try
    {
        using var edition = LocalEdition.Prepare(args[1], args[2]);
        await edition.Start(hostStarted: session.HostStarted);
        if (args[3] == "fail") session.Failed(new IOException("Deliberate smoke startup failure."));
        else if (args[3] != "timeout") session.Ready();
        if (args[3] == "hang") await Task.Delay(Timeout.Infinite);
        else await session.WaitForOwnerExit();
    }
    catch (Exception error) { session.Failed(error); Environment.ExitCode = 1; }
    return;
}

if (args.Length != 2)
    throw new ArgumentException("Usage: CommunityUpdater.Smoke <built-package> <new-evidence-directory>");
string source = Path.GetFullPath(args[0]), evidence = Path.GetFullPath(args[1]);
if (Directory.Exists(evidence)) throw new IOException("Choose a new evidence directory.");
Directory.CreateDirectory(evidence);
var results = new List<string>();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    results.Add(message); Console.WriteLine("PASS: " + message);
}

string packageA = Path.Combine(evidence, "release-a"), packageB = Path.Combine(evidence, "release-b");
CopyPackage(source, packageA);
CopyPackage(source, packageB);
string manifestB = Path.Combine(packageB, "package", "game-manifest.json");
var gameB = JsonSerializer.Deserialize<GameManifest>(File.ReadAllText(manifestB), json)!;
File.WriteAllText(manifestB, JsonSerializer.Serialize(gameB with { BuildId = gameB.BuildId + "-handoff-b" }, json));
File.WriteAllText(Path.Combine(packageB, "package", "download-host.json"),
    "{\"version\":1,\"contentBaseUrl\":\"https://release-b.invalid/content/\",\"launcherContentBaseUrl\":\"https://release-b.invalid/content/\"}");
var releaseB = JsonSerializer.Deserialize<LocalEdition.Release>(File.ReadAllText(Path.Combine(packageB, "local-edition.json")))!;
File.WriteAllText(Path.Combine(packageB, "local-edition.json"), JsonSerializer.Serialize(releaseB with
{
    ReleaseId = releaseB.ReleaseId + "-handoff-b",
    GameManifestSha256 = Hash(manifestB),
}));

string root = Path.Combine(evidence, "player"), otherRoot = Path.Combine(evidence, "other-player");
string game = Path.Combine(evidence, "retained-game");
Directory.CreateDirectory(game);
string sentinel = Path.Combine(game, "client-preservation.sentinel");
File.WriteAllText(sentinel, "Existing client files belong to the player; never replace them during a bundle update.");
string sentinelHash = Hash(sentinel);
string password = "Smoke-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
string accountId, certificate, hostIdentityHash, portsHash;
const string userConfig = "{\"movement\":{\"sprintAccel\":0.36}}";
string environment;

using var independent = LocalEdition.Prepare(packageA, otherRoot);
await independent.Start();
using var independentHost = Process.GetProcessById(independent.HostProcessId ?? throw new Exception("Independent host is missing."));
using var independentHttp = LauncherConnection.CreateHttp(independent.Settings);
using (var edition = LocalEdition.Prepare(packageA, root))
{
    await edition.Start();
    using var ownedHost = Process.GetProcessById(edition.HostProcessId ?? throw new Exception("Owned host is missing."));
    using var http = LauncherConnection.CreateHttp(edition.Settings);
    using var identity = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "launcher-host.json")));
    string owner = identity.RootElement.GetProperty("OwnerCode").GetString()!;
    using var registration = await http.PostAsJsonAsync("api/register", new Credentials("HandoffTester", password, owner));
    registration.EnsureSuccessStatusCode();
    accountId = (await registration.Content.ReadFromJsonAsync<AuthSession>())!.AccountId;
    certificate = edition.Settings.CertificateSha256;
    hostIdentityHash = Hash(Path.Combine(root, "launcher-host.json"));
    portsHash = Hash(Path.Combine(root, "local-ports.json"));
    environment = File.ReadAllText(Path.Combine(root, "environment.json"));
    LauncherProfile.Save(edition.ProfilePath, edition.Settings with { Name = "HandoffTester", VoiceVolume = 37, InstallDirectory = game });
    File.WriteAllText(Path.Combine(root, "cranberry.json"), userConfig);
    Check(!string.IsNullOrWhiteSpace(accountId), "Release A creates a fresh isolated account");

    edition.StopHost();
    Check(ownedHost.HasExited && edition.HostProcessId is null, "StopHost confirms its owned process has exited");
    edition.StopHost();
    Check(ownedHost.HasExited, "Repeated StopHost is harmless");
    bool leaseBlocked = false;
    try { using var unexpected = LocalEdition.Prepare(packageB, root); }
    catch (IOException) { leaseBlocked = true; }
    Check(leaseBlocked, "Stopping the host retains the data lease until Dispose");
    using var health = await independentHttp.GetAsync("health");
    Check(!independentHost.HasExited && health.IsSuccessStatusCode,
        "Stopping one edition leaves the independently owned host running");
}

Check(Directory.GetFiles(Path.Combine(root, "logs"), "*.log").Any(path => File.ReadAllText(path).Contains("stopped")),
    "Release A shutdown completes the host's normal flush path");
foreach ((string package, string label) in new[] { (packageB, "Release B"), (packageA, "Rollback A") })
{
    using var edition = LocalEdition.Prepare(package, root);
    Check(edition.PackageRoot == package, label + " takes the released data lease from its own package directory");
    Check(edition.Settings.CertificateSha256 == certificate
        && Hash(Path.Combine(root, "launcher-host.json")) == hostIdentityHash
        && Hash(Path.Combine(root, "local-ports.json")) == portsHash,
        label + " preserves server identity, certificate and ports");
    Check(edition.Settings.Name == "HandoffTester" && edition.Settings.VoiceVolume == 37
        && edition.Settings.InstallDirectory == game && Hash(sentinel) == sentinelHash,
        label + " preserves player preferences, game directory and client sentinel");
    Check(File.ReadAllText(Path.Combine(root, "cranberry.json")) == userConfig
        && File.ReadAllText(Path.Combine(root, "environment.json")) == environment,
        label + " preserves user gameplay and environment settings");
    Check(Hash(Path.Combine(root, "launcher-release", "manifest.json")) == Hash(Path.Combine(package, "package", "game-manifest.json"))
        && Hash(Path.Combine(root, "download-host.json")) == Hash(Path.Combine(package, "package", "download-host.json")),
        label + " restores its matching game and download manifests");
    await edition.Start();
    using var http = LauncherConnection.CreateHttp(edition.Settings);
    using var login = await http.PostAsJsonAsync("api/login", new Credentials("HandoffTester", password));
    login.EnsureSuccessStatusCode();
    Check((await login.Content.ReadFromJsonAsync<AuthSession>())!.AccountId == accountId,
        label + " reaches health readiness and authenticates the original local account");
    using var ownedHost = Process.GetProcessById(edition.HostProcessId ?? throw new Exception("Replacement host is missing."));
    edition.StopHost();
    Check(ownedHost.HasExited && !independentHost.HasExited, label + " stops only its own host before the next handoff");
}
independent.StopHost();
Check(independentHost.HasExited, "Independent host is stopped by its own owner during test cleanup");

string updateRoot = Path.Combine(evidence, "process-handshake");
Directory.CreateDirectory(updateRoot);
foreach (string mode in new[] { "ready", "fail", "timeout", "hang" })
{
    var request = new CommunityStartRequest(packageA, root, updateRoot, null);
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        WorkingDirectory = AppContext.BaseDirectory, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        ArgumentList = { "--child", packageA, root, mode },
    };
    await using var child = CommunityChildProcess.Start(request, start, gameIsOpen: () => false);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(mode == "timeout" ? 4 : 30));
    bool ready = false, failed = false;
    try { await child.WaitForReady(timeout.Token); ready = true; }
    catch (IOException) when (mode == "fail") { failed = true; }
    catch (OperationCanceledException) when (mode == "timeout") { failed = true; }
    Check(mode is "ready" or "hang" ? ready : failed, mode + ": parent receives genuine host readiness or startup failure");
    // In hang mode the child deliberately ignores EOF: only its recorded host/launcher may be killed.
    await child.Stop(CancellationToken.None);
    await child.WaitForExit(CancellationToken.None);
    Check(true, mode + ": child and owned host exit before the handoff returns");
    using var restored = LocalEdition.Prepare(packageA, root);
    await restored.Start();
    using var http = LauncherConnection.CreateHttp(restored.Settings);
    using var login = await http.PostAsJsonAsync("api/login", new Credentials("HandoffTester", password));
    login.EnsureSuccessStatusCode();
    Check((await login.Content.ReadFromJsonAsync<AuthSession>())!.AccountId == accountId && Hash(sentinel) == sentinelHash,
        mode + ": replacement starts on the same ports with the original account and untouched game sentinel");
    // Release the replacement lease before child.DisposeAsync performs its final ownership check.
    restored.Dispose();
}
{
    // Exercise the force-stop gate with real owned processes and a simulated game state.
    // No native game is started or inspected by this test.
    bool gameOpen = true;
    var request = new CommunityStartRequest(packageA, root, updateRoot, null);
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        WorkingDirectory = AppContext.BaseDirectory, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        ArgumentList = { "--child", packageA, root, "hang" },
    };
    await using var child = CommunityChildProcess.Start(request, start, gameIsOpen: () => gameOpen);
    try
    {
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await child.WaitForReady(startup.Token);
        bool protectedSession = false;
        var shutdown = Stopwatch.StartNew();
        try { await child.Stop(CancellationToken.None); }
        catch (IOException error) when (error.Message.Contains("running session was kept open")) { protectedSession = true; }
        Check(protectedSession && shutdown.Elapsed >= TimeSpan.FromSeconds(14),
            "Active-game guard refuses forced shutdown after the owned child's graceful-stop window");
        var settings = LauncherProfile.Load(Path.Combine(root, "launcher.json"), Path.Combine(root, "launcher-defaults.json"));
        if (!new Uri(settings.ServerUrl).IsLoopback) throw new Exception("Smoke health requests must remain local.");
        using var http = LauncherConnection.CreateHttp(settings);
        using var health = await http.GetAsync("health");
        Check(health.IsSuccessStatusCode, "Protected session keeps its owned host healthy after forced shutdown is refused");
        bool blocked = false;
        try { using var takeover = LocalEdition.Prepare(packageB, root); }
        catch (IOException) { blocked = true; }
        Check(blocked, "Protected session retains the data lease and prevents replacement takeover");
    }
    finally
    {
        gameOpen = false;
        await child.Stop(CancellationToken.None);
    }
    await child.WaitForExit(CancellationToken.None);
    using var restored = LocalEdition.Prepare(packageB, root);
    await restored.Start();
    using var restoredHttp = LauncherConnection.CreateHttp(restored.Settings);
    using var login = await restoredHttp.PostAsJsonAsync("api/login", new Credentials("HandoffTester", password));
    login.EnsureSuccessStatusCode();
    Check((await login.Content.ReadFromJsonAsync<AuthSession>())!.AccountId == accountId && Hash(sentinel) == sentinelHash,
        "After simulated game exit, only owned processes stop and the replacement preserves account and game data");
    restored.Dispose();
}
await File.WriteAllTextAsync(Path.Combine(evidence, "results.json"), JsonSerializer.Serialize(new
{
    completedUtc = DateTimeOffset.UtcNow,
    checks = results,
    nativeGameLaunched = false,
    publicNetworkRequests = false,
    scope = "Real local host A to B to A lifecycle, unchanged-schema persistence, child/host readiness, startup failure, timeout, forced owned-child shutdown, simulated active-game shutdown protection and account preservation. Native game and GUI clicks are not exercised.",
}, json));
Console.WriteLine($"All {results.Count} lifecycle checks passed.");

static string Hash(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static void CopyPackage(string source, string target)
{
    Directory.CreateDirectory(target);
    // Runtime and package metadata only: the game and player state never enter a release fixture.
    foreach (string folder in new[] { "runtime", "package" })
    {
        string sourceFolder = Path.Combine(source, folder), targetFolder = Path.Combine(target, folder);
        Directory.CreateDirectory(targetFolder);
        foreach (string path in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(targetFolder, Path.GetRelativePath(sourceFolder, path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination);
        }
    }
    File.Copy(Path.Combine(source, "local-edition.json"), Path.Combine(target, "local-edition.json"));
}
