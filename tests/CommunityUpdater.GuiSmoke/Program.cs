using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cranberry.Launcher.Core;

if (args.Length != 4)
    throw new ArgumentException("Usage: CommunityUpdater.GuiSmoke <seed-package> <signed-good-directory> <signed-broken-directory> <new-evidence-directory>");
string seed = Path.GetFullPath(args[0]);
string evidence = Path.GetFullPath(args[3]);
if (Directory.Exists(evidence) || File.Exists(evidence))
    throw new IOException("Choose a fresh evidence directory; existing files are preserved.");
Directory.CreateDirectory(evidence);
string installation = Path.Combine(evidence, "install");
string data = Path.Combine(evidence, "player-data");
CopyPackage(seed, installation);
var trust = CommunityUpdateSettings.Load(installation);
if (trust.Sequence != 1) throw new InvalidDataException("The seed package must be sequence 1.");
var good = SignedAssets.Read(args[1], trust, 2);
var broken = SignedAssets.Read(args[2], trust, 3);
var store = new CommunityUpdateStateStore(installation, trust);
var reports = new List<RunReport>();
var json = new JsonSerializerOptions { WriteIndented = true };
string reportPath = Path.Combine(evidence, "gui-update-smoke.json");
try
{
    await Run("signed-good", good, expectedRollback: false, expectedArchiveRequests: 1, expectedChildren: 1);
    await Run("signed-broken-fallback", broken, expectedRollback: true, expectedArchiveRequests: 1, expectedChildren: 2);
    await Run("offline-accepted", null, expectedRollback: false, expectedArchiveRequests: 0, expectedChildren: 1);
    var final = store.Read();
    Require(final.Active?.Sequence == 2 && final.HighestAccepted == 2 && final.Pending is null,
        "Final accepted state must remain the working sequence 2.");
    Require(final.Failed.Contains(broken.Release.Sha256), "Broken signed release was not recorded as failed.");
    await Save(true, null);
    Console.WriteLine($"PASS: genuine GUI/server signed update, rollback and offline startup; report {reportPath}");
}
catch (Exception error)
{
    await Save(false, error.ToString());
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

async Task Run(string name, SignedAssets? assets, bool expectedRollback, int expectedArchiveRequests, int expectedChildren)
{
    var starts = new List<ChildReport>();
    using var handler = new LocalFeedHandler(trust.Repository, assets);
    using var feed = new CommunityReleaseFeed(trust, handler);
    var bootstrap = new CommunityBootstrap(installation, data, gameIsOpen: () => false);
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    int ready = 0;
    var watch = Stopwatch.StartNew();
    CommunityRunResult? result = null;
    string? failure = null;
    try
    {
        result = await bootstrap.Run(feed, request =>
        {
            var report = new ChildReport { PackageDirectory = request.PackageDirectory, Notice = request.Notice };
            starts.Add(report);
            return new AutoClosingChild(request, report);
        }, ready: () => ready++, cancellation: deadline.Token);
        Require(result.Sequence == 2 && result.RolledBack == expectedRollback && result.ExitCode == 0,
            $"{name}: unexpected launch result: {result}.");
        Require(ready == 1, $"{name}: exactly one real GUI/host readiness acknowledgement is required.");
        Require(starts.Count == expectedChildren, $"{name}: unexpected child/fallback count.");
        Require(starts[^1].PackageDirectory == store.BundlePath(good.Release), $"{name}: did not run staged good package.");
        Require(starts.Count(s => s.Ready) == 1, $"{name}: unexpected ready child count.");
        if (expectedRollback)
        {
            Require(starts[0].PackageDirectory == store.BundlePath(broken.Release) && !starts[0].Ready,
                "The signed broken package was not actually attempted before fallback.");
            Require(starts[0].Failure is not null, "The broken GUI did not report a startup failure.");
        }
        Require(handler.ArchiveRequests == expectedArchiveRequests, $"{name}: unexpected archive downloads.");
        foreach (var child in starts)
        {
            Require(child.Launcher is not null && child.Launcher.Closed, $"{name}: owned launcher closure unverified.");
            Require(child.Host is null || child.Host.Closed, $"{name}: owned host closure unverified.");
        }
    }
    catch (Exception error) { failure = error.ToString(); throw; }
    finally
    {
        bool runFailed = failure is not null;
        PortsReport? ports = null;
        try { ports = ProbePorts(data); }
        catch (Exception error) { failure ??= "Local ports were not released: " + error; }
        reports.Add(new(name, result, ready, starts, handler.ApiRequests, handler.ManifestRequests,
            handler.ArchiveRequests, ports, watch.ElapsedMilliseconds, failure));
        await Save(false, failure);
        Console.WriteLine($"{name}: ready={ready}, children={starts.Count}, archives={handler.ArchiveRequests}, elapsed={watch.ElapsedMilliseconds}ms");
        if (!runFailed) Require(ports is { Released: true }, "Local ports were not released: " + failure);
    }
}

Task Save(bool passed, string? failure) => File.WriteAllTextAsync(reportPath,
    JsonSerializer.Serialize(new { passed, failure, nativeGameLaunched = false, externalRequests = 0,
        installation, dataDirectory = data, runs = reports }, json));

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

static void CopyPackage(string source, string destination)
{
    if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
    if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException("The seed package cannot be a link.");
    Directory.CreateDirectory(destination);
    var pending = new Stack<string>(); pending.Push(source);
    while (pending.TryPop(out string? directory))
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            string relative = Path.GetRelativePath(source, path).Replace('\\', '/');
            if (relative.StartsWith(".community-updates", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The smoke seed must be a pristine package without existing updater state.");
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Seed package contains a link.");
            string target = GameInstaller.SafePath(destination, relative);
            if ((attributes & FileAttributes.Directory) != 0) { Directory.CreateDirectory(target); pending.Push(path); }
            else File.Copy(path, target, overwrite: false);
        }
}

static PortsReport ProbePorts(string data)
{
    var host = JsonSerializer.Deserialize<HostPort>(File.ReadAllText(Path.Combine(data, "launcher-host.json")))
        ?? throw new InvalidDataException("Missing local host ports.");
    var ports = JsonSerializer.Deserialize<ZonePorts>(File.ReadAllText(Path.Combine(data, "local-ports.json")))
        ?? throw new InvalidDataException("Missing local UDP ports.");
    if (host.BindAddress != "127.0.0.1") throw new InvalidDataException("Smoke host is not loopback-only.");
    var listener = new TcpListener(IPAddress.Loopback, host.Port);
    try
    {
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        using var login = new UdpClient(AddressFamily.InterNetwork);
        login.ExclusiveAddressUse = true; login.Client.Bind(new IPEndPoint(IPAddress.Loopback, ports.Login));
        using var gateway = new UdpClient(AddressFamily.InterNetwork);
        gateway.ExclusiveAddressUse = true; gateway.Client.Bind(new IPEndPoint(IPAddress.Loopback, ports.Gateway));
        using var lease = new FileStream(Path.Combine(data, "local-edition.lock"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        return new(host.Port, ports.Login, ports.Gateway, true);
    }
    finally { listener.Stop(); }
}

sealed record SignedAssets(CommunityRelease Release, string Archive, byte[] Manifest)
{
    public static SignedAssets Read(string directory, CommunityUpdateSettings trust, long sequence)
    {
        string manifest = GameInstaller.SafePath(directory, CommunityRelease.ManifestFileName);
        if (new FileInfo(manifest).Length > 64 * 1024) throw new InvalidDataException("Oversized smoke manifest.");
        byte[] bytes = File.ReadAllBytes(manifest);
        var release = JsonSerializer.Deserialize<CommunityRelease>(bytes, CommunityRelease.Json)
            ?? throw new InvalidDataException("Missing smoke release.");
        release.Verify(trust.PublicKey);
        if (release.Sequence != sequence) throw new InvalidDataException("Unexpected smoke release sequence.");
        return new(release, GameInstaller.SafePath(directory, CommunityRelease.ArchiveFileName), bytes);
    }
}

sealed class LocalFeedHandler(string repository, SignedAssets? assets) : HttpMessageHandler
{
    public int ApiRequests { get; private set; }
    public int ManifestRequests { get; private set; }
    public int ArchiveRequests { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Headers.Authorization is not null || request.Headers.Contains("Cookie"))
            throw new InvalidDataException("Smoke update requests must not contain authentication.");
        Uri uri = request.RequestUri!;
        string prefix = $"https://github.com/{repository}/releases/download/gui-smoke-{assets?.Release.Sequence}/";
        if (uri.AbsoluteUri == $"https://api.github.com/repos/{repository}/releases?per_page=100&page=1")
        {
            ApiRequests++;
            if (assets is null) throw new HttpRequestException("Intentional offline smoke phase; no network was contacted.");
            byte[] listing = JsonSerializer.SerializeToUtf8Bytes(new[] { new { draft = false, prerelease = false,
                assets = new[] {
                    new { name = CommunityRelease.ManifestFileName, size = (long)assets.Manifest.Length,
                        browser_download_url = prefix + CommunityRelease.ManifestFileName },
                    new { name = CommunityRelease.ArchiveFileName, size = assets.Release.Size,
                        browser_download_url = prefix + CommunityRelease.ArchiveFileName } } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(listing) });
        }
        if (assets is not null && uri.AbsoluteUri == prefix + CommunityRelease.ManifestFileName)
        {
            ManifestRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(assets.Manifest) });
        }
        if (assets is not null && uri.AbsoluteUri == prefix + CommunityRelease.ArchiveFileName)
        {
            ArchiveRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(assets.Archive)) });
        }
        throw new InvalidDataException("Unexpected injected request: " + uri);
    }
}

sealed class AutoClosingChild : ICommunityRunningChild
{
    private readonly CommunityChildProcess _child;
    private readonly CommunityStartRequest _request;
    private readonly ChildReport _report;
    public AutoClosingChild(CommunityStartRequest request, ChildReport report)
    {
        _request = request; _report = report;
        var start = new ProcessStartInfo(GameInstaller.SafePath(request.PackageDirectory, "Cranberry.Launcher.exe"))
        {
            WorkingDirectory = request.PackageDirectory, WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true, ArgumentList = { "--local-data", request.StateDirectory }
        };
        _child = CommunityChildProcess.Start(request, start, gameIsOpen: () => false);
    }

    public async Task WaitForReady(CancellationToken cancellation)
    {
        try { await _child.WaitForReady(cancellation); _report.Ready = true; }
        catch (Exception error) { _report.Failure = error.Message; throw; }
        finally { CaptureReceipt(); }
    }
    public async Task<int> WaitForExit(CancellationToken cancellation)
    {
        await Task.Delay(500, cancellation);
        await Stop(cancellation);
        return await _child.WaitForExit(cancellation);
    }
    public async Task Stop(CancellationToken cancellation)
    {
        CaptureReceipt();
        await _child.Stop(cancellation);
        CheckProcesses();
    }
    public async ValueTask DisposeAsync()
    {
        CaptureReceipt();
        await _child.DisposeAsync();
        CheckProcesses();
    }
    private void CaptureReceipt()
    {
        string sessions = GameInstaller.SafePath(_request.UpdateDirectory, "sessions");
        foreach (string path in Directory.EnumerateFiles(sessions, "*.json"))
        {
            if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("Oversized GUI smoke receipt.");
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var value = document.RootElement;
            int launcherId = value.GetProperty("LauncherId").GetInt32();
            if (_report.Launcher is not null && _report.Launcher.Id != launcherId) continue;
            _report.Launcher ??= ProcessReport.Capture(launcherId, null);
            if (value.GetProperty("HostId").ValueKind == JsonValueKind.Number)
                _report.Host ??= ProcessReport.Capture(value.GetProperty("HostId").GetInt32(),
                    value.GetProperty("HostStartTicks").GetInt64());
            _report.Failure ??= value.GetProperty("Failure").GetString();
        }
    }
    private void CheckProcesses()
    {
        _report.Launcher?.CheckClosed();
        _report.Host?.CheckClosed();
    }
}

sealed class ProcessReport
{
    public int Id { get; init; }
    public long? StartTicks { get; init; }
    public bool Closed { get; private set; }
    public static ProcessReport Capture(int id, long? startTicks)
    {
        if (startTicks is null)
        {
            try { using var process = Process.GetProcessById(id); startTicks = process.StartTime.ToUniversalTime().Ticks; }
            catch (ArgumentException) { }
        }
        return new() { Id = id, StartTicks = startTicks };
    }
    public void CheckClosed()
    {
        try
        {
            using var process = Process.GetProcessById(Id);
            Closed = process.HasExited || StartTicks is not null && process.StartTime.ToUniversalTime().Ticks != StartTicks;
        }
        catch (ArgumentException) { Closed = true; }
    }
}
sealed class ChildReport
{
    public string PackageDirectory { get; init; } = "";
    public string? Notice { get; init; }
    public bool Ready { get; set; }
    public string? Failure { get; set; }
    public ProcessReport? Launcher { get; set; }
    public ProcessReport? Host { get; set; }
}
sealed record RunReport(string Name, CommunityRunResult? Result, int ReadyCallbacks, List<ChildReport> Children,
    int ApiRequests, int ManifestRequests, int ArchiveRequests, PortsReport? Ports, long ElapsedMilliseconds, string? Failure);
sealed record PortsReport(int Https, int Login, int Gateway, bool Released);
sealed record HostPort(int Port, string BindAddress);
sealed record ZonePorts(int Login, int Gateway);
