using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Cranberry.Launcher.Core;

/// <summary>A standalone installation with fresh local identity and persistent per-user saves.</summary>
public sealed class LocalEdition : IDisposable
{
    public sealed record Release(int Version, string ReleaseId, string GameManifestSha256, string AppearanceSha256);
    private sealed record HostSettings(int Port, string BindAddress, string JoinCode, string OwnerCode,
        string PackageDirectory, string CertificateFile);
    private sealed record Ports(int Login, int Gateway);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly FileStream _lease;
    private readonly object _gate = new();
    private Process? _process;
    private bool _disposed;
    private readonly Ports _ports;
    private readonly HostSettings _host;
    private readonly Dictionary<string, string> _environment;
    public string Root { get; }
    public string PackageRoot { get; }
    public string ProfilePath => Path.Combine(Root, "launcher.json");
    public string PackageProfilePath => Path.Combine(Root, "launcher-defaults.json");
    public LauncherSettings Settings { get; }
    public Release Version { get; }
    public int? HostProcessId { get { lock (_gate) return _process is { HasExited: false } ? _process.Id : null; } }

    private LocalEdition(string package, string root, FileStream lease, Release release,
        Ports ports, HostSettings host, Dictionary<string, string> environment, LauncherSettings settings)
    {
        PackageRoot = package; Root = root; _lease = lease; Version = release;
        _ports = ports; _host = host; _environment = environment; Settings = settings;
    }

    public static LocalEdition Prepare(string packageDirectory, string? stateDirectory = null)
    {
        string package = Path.GetFullPath(packageDirectory);
        string root = Path.GetFullPath(stateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CranberryCommunity"));
        Release release = Read<Release>(Path.Combine(package, "local-edition.json"));
        if (release.Version != 1 || string.IsNullOrWhiteSpace(release.ReleaseId))
            throw new InvalidDataException("Unsupported local edition package.");
        Verify(Path.Combine(package, "package", "game-manifest.json"), release.GameManifestSha256);
        Verify(Path.Combine(package, "runtime", "Data", "dynamicAppearance.bin"), release.AppearanceSha256);
        if (!File.Exists(Path.Combine(package, "runtime", "Cranberry.Host.exe")))
            throw new FileNotFoundException("The bundled local server is missing. Extract the complete release ZIP.");
        Directory.CreateDirectory(root);
        FileStream lease;
        try { lease = new(Path.Combine(root, "local-edition.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("Cranberry Local is already open for this data folder. Close it before opening another release."); }
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "state", "launcher"));
            Directory.CreateDirectory(Path.Combine(root, "launcher-release"));
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            string hostFile = Path.Combine(root, "launcher-host.json");
            HostSettings host;
            if (File.Exists(hostFile)) host = Read<HostSettings>(hostFile);
            else
            {
                host = new(TcpPort(), "127.0.0.1", Convert.ToHexString(RandomNumberGenerator.GetBytes(12)),
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), "launcher-release", "state/launcher/server.pfx");
                Write(hostFile, host);
            }
            if (host.BindAddress != "127.0.0.1" || host.Port is < 1024 or > 65535
                || host.CertificateFile != "state/launcher/server.pfx" || host.PackageDirectory != "launcher-release"
                || host.JoinCode is null || host.JoinCode.Length < 16 || host.OwnerCode is null || host.OwnerCode.Length < 32)
                throw new InvalidDataException("Invalid local server identity. Local edition requires a loopback address and its own certificate.");
            string portFile = Path.Combine(root, "local-ports.json");
            Ports ports;
            if (File.Exists(portFile)) ports = Read<Ports>(portFile);
            else
            {
                int login = UdpPort(), gateway;
                do { gateway = UdpPort(); } while (gateway == login);
                ports = new(login, gateway); Write(portFile, ports);
            }
            if (ports.Login is < 1024 or > 65535 || ports.Gateway is < 1024 or > 65535 || ports.Login == ports.Gateway)
                throw new InvalidDataException("Invalid local UDP ports.");
            string certificatePath = Path.Combine(root, host.CertificateFile);
            EnsureCertificate(certificatePath);
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, null);
            if (!cert.HasPrivateKey) throw new InvalidDataException("The local server certificate has no private key.");

            // Release assets can change; account databases, user configuration and saves never come from a package.
            File.Copy(Path.Combine(package, "package", "game-manifest.json"), Path.Combine(root, "launcher-release", "manifest.json"), true);
            File.Copy(Path.Combine(package, "package", "download-host.json"), Path.Combine(root, "download-host.json"), true);
            CopyOnce(Path.Combine(package, "package", "gameplay.defaults.json"), Path.Combine(root, "cranberry.json"));
            CopyOnce(Path.Combine(package, "package", "environment.defaults.json"), Path.Combine(root, "environment.json"));
            var environment = Read<Dictionary<string, string>>(Path.Combine(root, "environment.json"));
            if (environment.Any(p => !p.Key.StartsWith("CRANBERRY_", StringComparison.Ordinal) || p.Key.Contains('=')
                || p.Key.Contains('\0') || p.Value is null || p.Value.Contains('\0')))
                throw new InvalidDataException("Local environment.json accepts only Cranberry settings.");

            string defaultsPath = Path.Combine(root, "launcher-defaults.json");
            var defaults = new LauncherSettings
            {
                ServerUrl = $"https://127.0.0.1:{host.Port}/",
                CertificateSha256 = cert.GetCertHashString(HashAlgorithmName.SHA256),
                InstallDirectory = Path.Combine(root, "Game"), JoinCode = host.JoinCode,
            };
            Write(defaultsPath, defaults);
            var saved = LauncherProfile.Load(Path.Combine(root, "launcher.json"), defaultsPath);
            var settings = saved with
            {
                ServerUrl = defaults.ServerUrl, CertificateSha256 = defaults.CertificateSha256,
                JoinCode = host.JoinCode, ContentBaseUrl = "",
                InstallDirectory = string.IsNullOrWhiteSpace(saved.InstallDirectory) ? defaults.InstallDirectory : saved.InstallDirectory,
            };
            LauncherProfile.Save(Path.Combine(root, "launcher.json"), settings);
            return new(package, root, lease, release, ports, host, environment, settings);
        }
        catch { lease.Dispose(); throw; }
    }

    public async Task Start(CancellationToken stop = default)
    {
        stop.ThrowIfCancellationRequested();
        Process process;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is not null) throw new InvalidOperationException("This local server has already been started.");
            EnsurePortsAvailable();
            string runtime = Path.Combine(PackageRoot, "runtime");
            var start = new ProcessStartInfo(Path.Combine(runtime, "Cranberry.Host.exe"))
            {
                WorkingDirectory = runtime, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("CRANBERRY_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            foreach (var item in _environment) start.Environment[item.Key] = item.Value;
            start.Environment["CRANBERRY_CONFIG"] = Path.Combine(Root, "cranberry.json");
            start.Environment["CRANBERRY_ROOT"] = Root;
            start.Environment["CRANBERRY_PORT_LOGIN"] = _ports.Login.ToString();
            start.Environment["CRANBERRY_PORT_GATEWAY"] = _ports.Gateway.ToString();
            start.Environment["CRANBERRY_DYNAMIC_APPEARANCE_SOURCE"] = Path.Combine(runtime, "Data", "dynamicAppearance.bin");
            start.Environment["CRANBERRY_CLIENT_LOGS"] = Path.Combine(Settings.InstallDirectory, "Logs");
            start.Environment["CRANBERRY_WARDROBE_STORE"] = Path.Combine(Root, "state", "wardrobe");
            start.Environment["CRANBERRY_LOCAL_MANAGED"] = "1";
            start.ArgumentList.Add(Path.Combine(Root, "cranberry.json"));
            start.ArgumentList.Add(Root);
            start.ArgumentList.Add(_ports.Login.ToString());
            start.ArgumentList.Add(_ports.Gateway.ToString());
            _process = process = Process.Start(start) ?? throw new IOException("Could not start the bundled server.");
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            _ = Pump(process.StandardOutput, Path.Combine(Root, "logs", $"local-{stamp}.log"));
            _ = Pump(process.StandardError, Path.Combine(Root, "logs", $"local-{stamp}.error.log"));
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var http = LauncherConnection.CreateHttp(Settings);
        try
        {
            while (true)
            {
                if (process.HasExited) throw new IOException($"Local server stopped during startup. See {Path.Combine(Root, "logs")}.");
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    using var response = await http.GetAsync("health", attempt.Token);
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(150, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        { throw new TimeoutException("Local server startup timed out. Read the local logs, then reopen the launcher."); }
    }

    private void EnsurePortsAvailable()
    {
        try
        {
            var tcp = new TcpListener(IPAddress.Loopback, _host.Port);
            try
            {
                tcp.Start();
                using var login = new UdpClient(new IPEndPoint(IPAddress.Loopback, _ports.Login));
                using var gateway = new UdpClient(new IPEndPoint(IPAddress.Loopback, _ports.Gateway));
            }
            finally { tcp.Stop(); }
        }
        catch (SocketException)
        { throw new IOException("A local server port is already in use. Close the other local edition or change its ports; no running server was stopped."); }
    }

    private static async Task Pump(StreamReader reader, string path)
    {
        try
        {
            using var file = new StreamWriter(path, false) { AutoFlush = true };
            int written = 0;
            while (await reader.ReadLineAsync() is string line)
            {
                if (written > 4 * 1024 * 1024) continue;
                await file.WriteLineAsync(line); written += line.Length + 2;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_process is not null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        try
                        {
                            _process.StandardInput.WriteLine("stop");
                            _process.StandardInput.Flush();
                        }
                        catch (IOException) { }
                        finally { _process.StandardInput.Close(); }
                        if (!_process.WaitForExit(8_000))
                        {
                            _process.Kill(entireProcessTree: false);
                            _process.WaitForExit(3_000);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { _process.Dispose(); _process = null; }
            }
            _lease.Dispose();
        }
    }

    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path))
        ?? throw new InvalidDataException($"Invalid local configuration: {Path.GetFileName(path)}");
    private static void Write<T>(string path, T data)
    {
        string temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, Json));
        File.Move(temporary, path, true);
    }
    private static void CopyOnce(string source, string target)
    { if (!File.Exists(target)) File.Copy(source, target); }
    private static void Verify(string path, string hash)
    {
        using var input = File.OpenRead(path);
        if (hash is null || hash.Length != 64 || !Convert.ToHexString(SHA256.HashData(input)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Local package verification failed: {Path.GetFileName(path)}");
    }
    private static int TcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try { listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
    private static int UdpPort()
    { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
    private static void EnsureCertificate(string path)
    {
        if (File.Exists(path)) return;
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=Cranberry Community Local", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
    }
}
