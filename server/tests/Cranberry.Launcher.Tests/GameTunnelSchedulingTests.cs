using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Cranberry.Launcher.Core;
using Cranberry.Launcher.Service;
using Cranberry.Login;
using Cranberry.Transport;
using Cranberry.Zone;

namespace Cranberry.Launcher.Tests;

public sealed class GameTunnelSchedulingTests
{
    // A caller-owned UI queue. Draining establishes the connection; withholding
    // further dispatch then models a busy window without stalling the test runner.
    private sealed class UiQueue : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
        public int Pending => _pending.Count;
        public override void Post(SendOrPostCallback callback, object? state) => _pending.Enqueue((callback, state));

        public Task Invoke(Func<Task> action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return action(); }
            finally { SetSynchronizationContext(previous); }
        }

        public async Task DrainUntil(Task operation)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!operation.IsCompleted)
            {
                while (_pending.TryDequeue(out var work))
                {
                    var previous = Current;
                    SetSynchronizationContext(this);
                    try { work.Callback(work.State); }
                    finally { SetSynchronizationContext(previous); }
                }
                if (!operation.IsCompleted) await Task.Delay(1, deadline.Token);
            }
            await operation;
        }
    }

    private sealed class Echo : ISoeService, ITransportLog, IPacketRecorder
    {
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection connection) { }
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) { }
        public void OnMessage(SoeConnection connection, Span<byte> message) => connection.Send(message);
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) { }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-tunnel-scheduling-" + Guid.NewGuid().ToString("N"));
        private readonly Echo _echo = new();
        private readonly SoeListener _login;
        private readonly SoeListener _gateway;
        private LauncherHost? _host;
        public LauncherSettings Settings = new();
        public string Token = "";

        public Fixture()
        {
            _login = new(new(IPAddress.Loopback, 0), _echo, _echo);
            _gateway = new(new(IPAddress.Loopback, 0), _echo, _echo);
        }

        public async Task Start()
        {
            Directory.CreateDirectory(Path.Combine(_root, "launcher-release"));
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var options = new LauncherHostOptions { Port = port };
            File.WriteAllText(Path.Combine(_root, "launcher-host.json"), JsonSerializer.Serialize(options));
            File.WriteAllText(Path.Combine(_root, "launcher-release", "manifest.json"), JsonSerializer.Serialize(
                new GameManifest(1, "scheduling-test", "0.0.118.208059", [new("H1Z1.exe", 1, new('0', 64))])));
            _login.Start(); _gateway.Start();
            var zone = new ZoneService(_echo, _echo, new GatewayTicketRegistry(), new ZoneOptions()) { Post = action => action() };
            _host = new(_root, new LocalAccountDirectory("owner"), zone, _login.LocalEndPoint.Port,
                _gateway.LocalEndPoint.Port, loginListener: _login, gatewayListener: _gateway);
            await _host.StartAsync();
            Settings = new() { ServerUrl = $"https://127.0.0.1:{port}/", CertificateSha256 = _host.CertificateSha256 };
            using var http = LauncherConnection.CreateHttp(Settings);
            using var registration = await http.PostAsJsonAsync("api/register", new Credentials("Alice", "integration-password", options.JoinCode));
            registration.EnsureSuccessStatusCode();
            Token = (await registration.Content.ReadFromJsonAsync<AuthSession>())!.Token;
        }

        public async ValueTask DisposeAsync()
        {
            if (_host is not null) await _host.DisposeAsync();
            _login.Dispose(); _gateway.Dispose();
            // This fixture alone created this unique directory under the OS temp root.
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    [Fact]
    public async Task EstablishedTunnelForwardsBothChannelsWhileCallerUiIsNotDispatching()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        var ui = new UiQueue();
        var tunnel = new GameTunnel();
        try
        {
            await ui.DrainUntil(ui.Invoke(() => tunnel.Connect(fixture.Settings, fixture.Token)));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            foreach (int destination in new[] { tunnel.LoginPort, tunnel.GatewayPort })
            {
                using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                game.Connect(IPAddress.Loopback, destination);
                byte[] request = [0, 1, 0, 0, 0, 3, 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 2, 0, (byte)'T', 0];
                await game.SendAsync(request, deadline.Token);
                Assert.Equal((byte)SoeOpcode.SessionReply, (await game.ReceiveAsync(deadline.Token)).Buffer[1]);
                for (int i = 0; i < 3; i++)
                {
                    await game.SendAsync(new byte[] { 0, (byte)SoeOpcode.Ping }, deadline.Token);
                    Assert.Equal(new byte[] { 0, (byte)SoeOpcode.Ping }, (await game.ReceiveAsync(deadline.Token)).Buffer);
                }
            }
            Assert.Equal(0, ui.Pending);
        }
        finally { await ui.DrainUntil(tunnel.DisposeAsync().AsTask()); }
    }

    [Fact]
    public async Task TunnelCanCloseWithoutPostingItsCleanupToCallerUi()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        var ui = new UiQueue();
        var tunnel = new GameTunnel();
        Task? closing = null;
        try
        {
            await ui.DrainUntil(ui.Invoke(() => tunnel.Connect(fixture.Settings, fixture.Token)));
            closing = ui.Invoke(() => tunnel.DisposeAsync().AsTask());
            await closing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, ui.Pending);
        }
        finally { await ui.DrainUntil(closing ?? tunnel.DisposeAsync().AsTask()); }
    }

    [Fact]
    public async Task DiagnosticWindowsFlushWithoutCallerUiDispatch()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cranberry-tunnel-metrics-" + Guid.NewGuid().ToString("N"));
        var ui = new UiQueue();
        var metrics = new GameTunnelMetrics();
        using var stop = new CancellationTokenSource();
        Task writer = ui.Invoke(() => metrics.WriteWindows(stop.Token, directory));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (metrics.LogPath is null || new FileInfo(metrics.LogPath).Length == 0)
            {
                if (writer.IsCompleted) await writer;
                await Task.Delay(20, deadline.Token);
            }
            using var file = new FileStream(metrics.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(file);
            using var document = JsonDocument.Parse((await reader.ReadLineAsync())!);
            Assert.True(document.RootElement.GetProperty("windowSeconds").GetDouble() > 0);
            Assert.Null(metrics.WriterError);
            Assert.Equal(0, ui.Pending);
        }
        finally
        {
            stop.Cancel();
            await ui.DrainUntil(writer);
            // The test created this unique temp directory; it contains only its own log.
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
