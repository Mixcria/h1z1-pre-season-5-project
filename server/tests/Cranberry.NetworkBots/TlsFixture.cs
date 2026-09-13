using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Cranberry.Launcher.Core;
using Cranberry.Launcher.Service;
using Cranberry.Login;

namespace Cranberry.NetworkBots;

internal sealed record TlsFixtureAddress(LauncherSettings Settings, string[] Tokens);

// Loopback-only, disposable accounts. Pre-provisioning uses the real SocialStore so this
// capacity fixture does not spend hours waiting for the public registration abuse limiter.
internal sealed class TlsFixture : IAsyncDisposable
{
    public LocalAccountDirectory Accounts { get; } = new(allowLoopbackDevelopment: false);
    public IReadOnlyList<string> AccountIds => _sessions.Select(s => s.AccountId).ToArray();
    private readonly List<AuthSession> _sessions = [];
    private readonly string _root;
    private readonly SocialStore _social;
    private LauncherHost? _host;
    private readonly LauncherHostOptions _options;

    public TlsFixture(int population, string output)
    {
        _root = Path.Combine(output, "tls"); Directory.CreateDirectory(_root);
        var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
        _options = new LauncherHostOptions { Port = ((IPEndPoint)port.LocalEndpoint).Port };
        port.Stop();
        File.WriteAllText(Path.Combine(_root, "launcher-host.json"), JsonSerializer.Serialize(_options));
        _social = new SocialStore(Path.Combine(_root, "state", "launcher", "social.json"), _options.JoinCode, _options.OwnerCode, Accounts);
        for (int i = 0; i < population; i++)
        {
            _sessions.Add(_social.Register(new Credentials($"TlsBot{i:D5}", Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), _options.JoinCode)));
            if (i % 100 == 99) Console.WriteLine($"Provisioned TLS accounts: {i + 1}/{population}");
        }
        Directory.CreateDirectory(Path.Combine(_root, "launcher-release"));
        File.WriteAllText(Path.Combine(_root, "launcher-release", "manifest.json"), JsonSerializer.Serialize(
            new GameManifest(1, "network-fixture", "0.0.118.208059", [new("H1Z1.exe", 1, new('0', 64))])));
    }
    public async Task<TlsFixtureAddress> Start(LocalServer server, CancellationToken ct)
    {
        _host = new LauncherHost(_root, Accounts, server.Zone, server.LoginEndPoint.Port, server.GatewayEndPoint.Port, _social,
            server.LoginListener, server.GatewayListener, enableDiagnostics: true);
        if (WireAudit.Enabled)
        {
            var monitored = _sessions.Select((s, i) => (s.AccountId, Index: i)).Where(p => WireAudit.Selected(p.Index)).ToDictionary(p => p.AccountId, p => p.Index);
            _host.ObserveTunnelDatagram = (account, channel, stage, bytes) =>
            {
                if (monitored.TryGetValue(account, out int index)) WireAudit.Tunnel(index, channel, stage, bytes);
            };
        }
        await _host.StartAsync(ct);
        // The disposable gameplay fixture uses the existing loopback console only to grant
        // test ammunition. Production LauncherHost keeps its owner-only console restriction.
        server.Zone.LocalOwnerAccountId = null;
        return new(new LauncherSettings { ServerUrl = $"https://127.0.0.1:{_options.Port}/", CertificateSha256 = _host.CertificateSha256 },
            _sessions.Select(s => s.Token).ToArray());
    }
    public async ValueTask DisposeAsync() { if (_host is not null) await _host.DisposeAsync(); }
    public object? CaptureDiagnostics() => _host?.CaptureDiagnostics();

    public static async Task<(HttpClient Http, GameTunnel Tunnel, GameLaunch Launch)> Connect(TlsFixtureAddress fixture, int index, CancellationToken ct)
    {
        if (!LauncherSettings.ValidateServer(fixture.Settings.ServerUrl).IsLoopback) throw new InvalidDataException("Fixture TLS must stay on loopback.");
        var http = LauncherConnection.CreateHttp(fixture.Settings);
        var tunnel = new GameTunnel();
        if (WireAudit.Enabled && WireAudit.Selected(index))
            tunnel.ObserveDatagram = (channel, stage, bytes) => WireAudit.Tunnel(index, channel, stage, bytes);
        try
        {
            http.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Tokens[index]);
            await tunnel.Connect(fixture.Settings, fixture.Tokens[index], ct);
            using var response = await http.PostAsJsonAsync("api/launch", new LaunchRequest(tunnel.GatewayPort), ct);
            response.EnsureSuccessStatusCode();
            return (http, tunnel, (await response.Content.ReadFromJsonAsync<GameLaunch>(ct))!);
        }
        catch { await tunnel.DisposeAsync(); http.Dispose(); throw; }
    }
}
