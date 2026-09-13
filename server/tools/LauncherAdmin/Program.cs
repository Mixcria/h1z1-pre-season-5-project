using Cranberry.Launcher.Service;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using Cranberry.Launcher.Core;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;

if (args.Length != 2 || args[0] is not ("init" or "verify-download"))
{ Console.Error.WriteLine("Usage: LauncherAdmin init|verify-download <server-root>"); return 1; }
string root = Path.GetFullPath(args[1]);
if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
if (args[0] == "verify-download")
{
    string scratch = Path.Combine(root, "out", "launcher-download-check-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
    Directory.CreateDirectory(scratch);
    var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
    int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
    var testOptions = new LauncherHostOptions { Port = port, PackageDirectory = Path.Combine(root, "launcher-release") };
    File.WriteAllText(Path.Combine(scratch, "launcher-host.json"), JsonSerializer.Serialize(testOptions));
    var recorder = new QuietRecorder();
    var zone = new ZoneService(recorder, recorder, new(), new()) { Post = action => action() };
    await using var host = new LauncherHost(scratch, new LocalAccountDirectory(), zone, 20052, 20053);
    await host.StartAsync();
    using var http = LauncherConnection.CreateHttp(new LauncherSettings { ServerUrl = $"https://localhost:{port}/", CertificateSha256 = host.CertificateSha256 });
    var response = await http.PostAsJsonAsync("api/register", new Credentials("PackageCheck", Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), testOptions.JoinCode));
    response.EnsureSuccessStatusCode();
    var account = (await response.Content.ReadFromJsonAsync<AuthSession>())!;
    http.DefaultRequestHeaders.Authorization = new("Bearer", account.Token);
    var manifest = (await http.GetFromJsonAsync<GameManifest>("api/manifest"))!;
    string client = Path.Combine(scratch, "Client");
    await new GameInstaller(http).Install(manifest, client, null);
    await new GameInstaller(http).Install(manifest, client, null, verifyOnly: true);
    Console.WriteLine($"PASS: {manifest.Files.Count} files downloaded over pinned HTTPS and verified twice; {manifest.Files.Sum(f => f.Size)} bytes.");
    Console.WriteLine($"Verified client: {client}");
    return 0;
}
var options = LauncherHostOptions.Load(root);
using var certificate = options.Certificate(root);
Console.WriteLine($"Prepared launcher HTTPS on {options.BindAddress}:{options.Port}.");
Console.WriteLine($"Public certificate SHA256: {certificate.GetCertHashString(HashAlgorithmName.SHA256)}");
Console.WriteLine("Private registration codes and certificate remain in the server root.");
return 0;

sealed class QuietRecorder : ITransportLog, IPacketRecorder
{
    public bool IsEnabled(TransportLogLevel level) => false;
    public void Log(TransportLogLevel level, string message) { }
    public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
    public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
    public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) { }
}
