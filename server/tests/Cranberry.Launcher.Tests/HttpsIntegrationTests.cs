using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Cranberry.Launcher.Core;
using Cranberry.Launcher.Service;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;

namespace Cranberry.Launcher.Tests;

public sealed class HttpsIntegrationTests
{
    private sealed class Recorder : ITransportLog, IPacketRecorder
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) { }
    }

    private sealed class LocalEcho : ISoeService
    {
        public IPEndPoint? Source;
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request)
        { Source = remote; return SessionDecision.Clear; }
        public void OnConnected(SoeConnection connection) { }
        public void OnMessage(SoeConnection connection, Span<byte> message) => connection.Send(message);
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InProcessTlsBridgePreservesBothSoeChannelsAndAccountBinding(bool diagnostics)
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-local-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "launcher-release"));
        var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
        var options = new LauncherHostOptions { Port = ((IPEndPoint)port.LocalEndpoint).Port }; port.Stop();
        File.WriteAllText(Path.Combine(root, "launcher-host.json"), JsonSerializer.Serialize(options));
        File.WriteAllText(Path.Combine(root, "launcher-release", "manifest.json"), JsonSerializer.Serialize(
            new GameManifest(1, "test", "0.0.118.208059", [new("H1Z1.exe", 1, new('0', 64))])));
        var log = new Recorder();
        var loginEcho = new LocalEcho(); var gatewayEcho = new LocalEcho();
        using var login = new SoeListener(new(IPAddress.Loopback, 0), loginEcho, log);
        using var gateway = new SoeListener(new(IPAddress.Loopback, 0), gatewayEcho, log);
        login.Start(); gateway.Start();
        var accounts = new LocalAccountDirectory("owner");
        var zone = new ZoneService(log, log, new GatewayTicketRegistry(), new ZoneOptions()) { Post = a => a() };
        try
        {
            await using var host = new LauncherHost(root, accounts, zone, login.LocalEndPoint.Port, gateway.LocalEndPoint.Port,
                loginListener: login, gatewayListener: gateway);
            await host.StartAsync();
            var settings = new LauncherSettings { ServerUrl = $"https://127.0.0.1:{options.Port}/",
                CertificateSha256 = host.CertificateSha256, TransportDiagnostics = diagnostics };
            using var http = LauncherConnection.CreateHttp(settings);
            using var registration = await http.PostAsJsonAsync("api/register", new Credentials("Alice", "integration-password", options.JoinCode));
            registration.EnsureSuccessStatusCode();
            var account = (await registration.Content.ReadFromJsonAsync<AuthSession>())!;
            http.DefaultRequestHeaders.Authorization = new("Bearer", account.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using (var tunnel = new GameTunnel())
            {
            await tunnel.Connect(settings, account.Token);
                using var launchResponse = await http.PostAsJsonAsync("api/launch", new LaunchRequest(tunnel.GatewayPort, BidirectionalDoors.ProtocolVersion));
            launchResponse.EnsureSuccessStatusCode();
            var launch = (await launchResponse.Content.ReadFromJsonAsync<GameLaunch>())!;
            byte[] request = [0, 1, 0, 0, 0, 3, 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 2, 0, (byte)'T', 0];
            foreach (int destination in new[] { tunnel.LoginPort, tunnel.GatewayPort })
            {
                using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                game.Connect(IPAddress.Loopback, destination);
                await game.SendAsync(request, deadline.Token);
                Assert.Equal((byte)SoeOpcode.SessionReply, (await game.ReceiveAsync(deadline.Token)).Buffer[1]);
                await game.SendAsync(new byte[] { 0, 9, 0, 0, 0xAB, 0xCD }, deadline.Token);
                byte[] first = (await game.ReceiveAsync(deadline.Token)).Buffer;
                byte[] second = (await game.ReceiveAsync(deadline.Token)).Buffer;
                byte[] echo = first[1] == (byte)SoeOpcode.Data ? first : second;
                Assert.Equal(new byte[] { 0xAB, 0xCD }, echo[4..]);
            }
            Assert.Equal(loginEcho.Source, gatewayEcho.Source);
            Assert.True(accounts.TryResolveConnection(launch.Ticket, loginEcho.Source!, out string actor, out string? destinationOverride));
            Assert.Equal(account.AccountId, actor);
            Assert.Equal("127.0.0.1:" + tunnel.GatewayPort, destinationOverride);
            Assert.False(accounts.TryResolveConnection("development-ticket", loginEcho.Source!, out _, out _));
            if (diagnostics)
            {
                var metrics = JsonSerializer.SerializeToElement(tunnel.Metrics!.Capture());
                Assert.True(metrics.GetProperty("udpFramesForwarded").GetInt64() >= 4);
                Assert.True(metrics.GetProperty("framesDeliveredToLoopback").GetInt64() >= 6);
                Assert.True(metrics.GetProperty("udpReceiveToWsSendComplete").GetProperty("count").GetInt64() >= 4);
                Assert.True(metrics.GetProperty("wsCompleteFrameToUdpSendComplete").GetProperty("count").GetInt64() >= 6);
            }
            else Assert.Null(tunnel.Metrics);
            }
            while (login.ConnectionCount != 0 || gateway.ConnectionCount != 0) await Task.Delay(20, deadline.Token);
        }
        finally
        {
            // This path was created above beneath the temporary directory for this test only.
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LogoutReturnsJsonAndRevokesTheAuthenticatedSession()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-logout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var options = new LauncherHostOptions { Port = port };
        File.WriteAllText(Path.Combine(root, "launcher-host.json"), JsonSerializer.Serialize(options));
        var log = new Recorder();
        var zone = new ZoneService(log, log, new GatewayTicketRegistry(), new ZoneOptions()) { Post = a => a() };
        try
        {
            await using var host = new LauncherHost(root, new LocalAccountDirectory("owner"), zone, 20042, 20043);
            await host.StartAsync();
            using var http = LauncherConnection.CreateHttp(new LauncherSettings
                { ServerUrl = $"https://localhost:{port}/", CertificateSha256 = host.CertificateSha256 });
            using var registration = await http.PostAsJsonAsync("api/register",
                new Credentials("Alice", "integration-password", options.JoinCode));
            registration.EnsureSuccessStatusCode();
            var session = (await registration.Content.ReadFromJsonAsync<AuthSession>())!;
            http.DefaultRequestHeaders.Authorization = new("Bearer", session.Token);
            using var before = await http.GetAsync("api/state");
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

            using var logout = await http.PostAsync("api/logout", null);
            Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
            using var after = await http.GetAsync("api/state");
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
            Assert.Equal("application/json", logout.Content.Headers.ContentType?.MediaType);
            Assert.True((await logout.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PinnedHttpsProtectsDownloadsAndBothGameChannelsAndRejectsBadCredentials()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-https-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "launcher-release", "content"));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var options = new LauncherHostOptions { Port = port };
        File.WriteAllText(Path.Combine(root, "launcher-host.json"), JsonSerializer.Serialize(options));
        byte[] content = "package-test-content"u8.ToArray(); string hash = Convert.ToHexString(SHA256.HashData(content));
        File.WriteAllBytes(Path.Combine(root, "launcher-release", "content", hash), content);
        File.WriteAllText(Path.Combine(root, "launcher-release", "manifest.json"), JsonSerializer.Serialize(
            new GameManifest(1, "test", "0.0.118.208059", [new("H1Z1.exe", content.Length, hash)])));
        var log = new Recorder(); var zone = new ZoneService(log, log, new GatewayTicketRegistry(), new ZoneOptions()) { Post = a => a() };
        var accounts = new LocalAccountDirectory("owner");
        using var loginUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var gatewayUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int loginPort = ((IPEndPoint)loginUdp.Client.LocalEndPoint!).Port, gatewayPort = ((IPEndPoint)gatewayUdp.Client.LocalEndPoint!).Port;
        try
        {
            await using var host = new LauncherHost(root, accounts, zone, loginPort, gatewayPort);
            await host.StartAsync();
            var settings = new LauncherSettings { ServerUrl = $"https://localhost:{port}/", CertificateSha256 = host.CertificateSha256 };
            using var http = LauncherConnection.CreateHttp(settings);
            Assert.True((await http.GetAsync("health")).IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("api/manifest")).StatusCode);
            using var wrongCertificate = LauncherConnection.CreateHttp(settings with { CertificateSha256 = new('0', 64) });
            await Assert.ThrowsAsync<HttpRequestException>(() => wrongCertificate.GetAsync("health"));
            // Registration failures must reach the launcher as readable JSON, without reserving
            // the name: the same player can fix the field and submit successfully below.
            using var missingJoinCode = await http.PostAsJsonAsync("api/register", new Credentials("Alice", "integration-password"));
            Assert.Equal(HttpStatusCode.BadRequest, missingJoinCode.StatusCode);
            Assert.Equal("The join code is incorrect.", (await missingJoinCode.Content.ReadFromJsonAsync<ApiError>())?.Error);
            using var shortPassword = await http.PostAsJsonAsync("api/register", new Credentials("Alice", "short", options.JoinCode));
            Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
            Assert.Equal("Use a password between 15 and 128 characters.", (await shortPassword.Content.ReadFromJsonAsync<ApiError>())?.Error);
            var registration = await http.PostAsJsonAsync("api/register", new Credentials("Alice", "integration-password", options.JoinCode));
            registration.EnsureSuccessStatusCode(); var alice = (await registration.Content.ReadFromJsonAsync<AuthSession>())!;
            http.DefaultRequestHeaders.Authorization = new("Bearer", alice.Token);
            Assert.True((await http.GetAsync("api/state")).IsSuccessStatusCode);
            using var download = new HttpRequestMessage(HttpMethod.Get, "api/content/" + hash);
            download.Headers.Range = new RangeHeaderValue(3, null);
            var response = await http.SendAsync(download); Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(content[3..], await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("api/content/" + new string('0', 64))).StatusCode);
            await using var tunnel = new GameTunnel(); await tunnel.Connect(settings, alice.Token);
            using var oldLaunch = await http.PostAsJsonAsync("api/launch", new LaunchRequest(tunnel.GatewayPort));
            Assert.Equal(HttpStatusCode.BadRequest, oldLaunch.StatusCode);
            Assert.Contains("reopen the launcher", (await oldLaunch.Content.ReadFromJsonAsync<ApiError>())!.Error);
            var launchResponse = await http.PostAsJsonAsync("api/launch", new LaunchRequest(tunnel.GatewayPort, BidirectionalDoors.ProtocolVersion));
            launchResponse.EnsureSuccessStatusCode(); var launch = (await launchResponse.Content.ReadFromJsonAsync<GameLaunch>())!;
            Assert.False(zone.DoorSwingClientReady!(alice.AccountId));
            using var wrongReady = await http.PostAsJsonAsync("api/client/doors-ready", new DoorClientReadyRequest("wrong-ticket", 1));
            Assert.Equal(HttpStatusCode.BadRequest, wrongReady.StatusCode);
            Assert.False(zone.DoorSwingClientReady(alice.AccountId));
            using var ready = await http.PostAsJsonAsync("api/client/doors-ready", new DoorClientReadyRequest(launch.Ticket, 1));
            ready.EnsureSuccessStatusCode();
            Assert.True(zone.DoorSwingClientReady(alice.AccountId));
            using var gameLogin = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var gameGateway = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await gameLogin.SendAsync("hello login"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, tunnel.LoginPort), timeout.Token);
            var received = await loginUdp.ReceiveAsync(timeout.Token);
            Assert.Equal("hello login"u8.ToArray(), received.Buffer);
            Assert.True(accounts.TryResolveConnection(launch.Ticket, received.RemoteEndPoint, out string actor, out string? destination));
            Assert.Equal(alice.AccountId, actor); Assert.Equal("127.0.0.1:" + tunnel.GatewayPort, destination);
            Assert.False(accounts.TryResolveConnection("development-ticket", received.RemoteEndPoint, out _, out _));
            await loginUdp.SendAsync("reply login"u8.ToArray(), received.RemoteEndPoint, timeout.Token);
            Assert.Equal("reply login"u8.ToArray(), (await gameLogin.ReceiveAsync(timeout.Token)).Buffer);
            await gameGateway.SendAsync("hello gateway"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, tunnel.GatewayPort), timeout.Token);
            received = await gatewayUdp.ReceiveAsync(timeout.Token);
            Assert.Equal("hello gateway"u8.ToArray(), received.Buffer);
            await gatewayUdp.SendAsync("reply gateway"u8.ToArray(), received.RemoteEndPoint, timeout.Token);
            Assert.Equal("reply gateway"u8.ToArray(), (await gameGateway.ReceiveAsync(timeout.Token)).Buffer);
            using var returningLogin = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            byte[] newSession = [0, 1, 0, 0, 0, 3, 1, 2, 3, 4, 0, 0, 2, 0, (byte)'T', 0];
            await returningLogin.SendAsync(newSession, new IPEndPoint(IPAddress.Loopback, tunnel.LoginPort), timeout.Token);
            received = await loginUdp.ReceiveAsync(timeout.Token);
            Assert.Equal(newSession, received.Buffer);
            // Old queued application bytes must not reach the replacement game socket.
            await loginUdp.SendAsync("old session data"u8.ToArray(), received.RemoteEndPoint, timeout.Token);
            byte[] sessionReply = [0, 2, 1, 2, 3, 4];
            await loginUdp.SendAsync(sessionReply, received.RemoteEndPoint, timeout.Token);
            await loginUdp.SendAsync("return to character select"u8.ToArray(), received.RemoteEndPoint, timeout.Token);
            Assert.Equal(sessionReply, (await returningLogin.ReceiveAsync(timeout.Token)).Buffer);
            Assert.Equal("return to character select"u8.ToArray(), (await returningLogin.ReceiveAsync(timeout.Token)).Buffer);
            using var missingAuth = LauncherConnection.CreateHttp(settings);
            Assert.Equal(HttpStatusCode.Unauthorized, (await missingAuth.PostAsJsonAsync("api/launch", new LaunchRequest(12345))).StatusCode);
            await using var unauthorizedTunnel = new GameTunnel();
            await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => unauthorizedTunnel.Connect(settings, new string('0', 64)));
        }
        finally { Directory.Delete(root, true); }
    }
}
