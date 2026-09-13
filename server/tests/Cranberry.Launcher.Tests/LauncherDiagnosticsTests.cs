using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Cranberry.Launcher.Core;
using Cranberry.Launcher.Service;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;

namespace Cranberry.Launcher.Tests;

public sealed class LauncherDiagnosticsTests
{
    private static JsonElement Snapshot(object value) => JsonSerializer.SerializeToElement(value);
    private static long Value(JsonElement value, params string[] path)
    {
        foreach (string part in path) value = value.GetProperty(part);
        return value.GetInt64();
    }

    [Fact]
    public void CapturingResetsEventWindowsWithoutResettingActiveRequestsOrConnectionGauges()
    {
        var diagnostics = new LauncherDiagnostics();
        diagnostics.HttpStarted();
        diagnostics.HttpStarted();
        diagnostics.HttpCompleted(200, faulted: false);
        diagnostics.TunnelStarted();
        diagnostics.TunnelAccepted();
        diagnostics.FrameReceived(64);
        diagnostics.FrameEnqueued(Stopwatch.Frequency / 1000);
        diagnostics.WssSendWait.RecordTicks(Stopwatch.Frequency / 500);
        diagnostics.FrameSent(80, Stopwatch.Frequency / 100);
        diagnostics.GateWait.RecordTicks(Stopwatch.Frequency / 1000);
        diagnostics.GateHold.RecordTicks(Stopwatch.Frequency / 100);
        var first = Snapshot(diagnostics.Capture(1, 2));
        Assert.Equal(2, Value(first, "Http", "Started"));
        Assert.Equal(1, Value(first, "Http", "Completed"));
        Assert.Equal(1, Value(first, "Http", "ActiveRequests"));
        Assert.Equal(1, Value(first, "Http", "Status2xx"));
        Assert.Equal(64, Value(first, "Tunnel", "InboundPayloadBytes"));
        Assert.Equal(80, Value(first, "Tunnel", "OutboundPayloadBytes"));
        Assert.Equal(1, Value(first, "Tunnel", "SendCompleted", "Count"));
        Assert.Equal(1, Value(first, "Tunnel", "CompleteFrameToGameEnqueue", "Count"));
        Assert.InRange(first.GetProperty("Tunnel").GetProperty("SendCompleted").GetProperty("AverageMs").GetDouble(), 9.9, 10.1);

        var reset = Snapshot(diagnostics.Capture(1, 2));
        Assert.Equal(1, Value(reset, "ActiveTunnelRoutes"));
        Assert.Equal(2, Value(reset, "ActiveVoiceConnections"));
        Assert.Equal(1, Value(reset, "Http", "ActiveRequests"));
        Assert.Equal(0, Value(reset, "Http", "Started"));
        Assert.Equal(0, Value(reset, "Http", "Completed"));
        Assert.Equal(0, Value(reset, "GateWait", "Count"));
        Assert.Equal(0, Value(reset, "GateHold", "Count"));
        Assert.Equal(0, Value(reset, "Tunnel", "InboundFrames"));
        Assert.Equal(0, Value(reset, "Tunnel", "OutboundFrames"));
        Assert.Equal(0, Value(reset, "Tunnel", "SendSemaphoreWait", "Count"));
        Assert.Equal(0, Value(reset, "Tunnel", "SendCompleted", "Count"));
        Assert.Equal(0, Value(reset, "Tunnel", "CompleteFrameToGameEnqueue", "Count"));
        diagnostics.HttpCompleted(401, faulted: false);
        var final = Snapshot(diagnostics.Capture(0, 0));
        Assert.Equal(0, Value(final, "Http", "ActiveRequests"));
        Assert.Equal(1, Value(final, "Http", "Unauthorized401"));
        Assert.Equal(1, Value(final, "Http", "Status4xx"));
    }

    [Fact]
    public void ClosureCategoriesAreBoundedAndNeverIncludeExceptionMessages()
    {
        const string privateText = "do-not-export-this-account-or-token";
        var diagnostics = new LauncherDiagnostics();
        Exception?[] failures = [null, new OperationCanceledException(privateText), new InvalidDataException(privateText),
            new TimeoutException(privateText), new WebSocketException(privateText), new SocketException(10054),
            new ChannelClosedException(privateText), new ObjectDisposedException(privateText),
            new IOException(privateText), new InvalidOperationException(privateText)];
        foreach (var failure in failures) diagnostics.TunnelClosed(failure);
        diagnostics.HttpStarted();
        diagnostics.HttpCompleted(429, faulted: false);
        var captured = Snapshot(diagnostics.Capture(0, 0));
        var closures = captured.GetProperty("Tunnel").GetProperty("Closures");
        Assert.Equal(10, closures.EnumerateObject().Count());
        Assert.All(closures.EnumerateObject(), item => Assert.Equal(1, item.Value.GetInt64()));
        Assert.DoesNotContain(privateText, captured.GetRawText());
        Assert.Equal(1, Value(captured, "Http", "RateLimited429"));
    }

    [Fact]
    public void ConcurrentCapturesAndProducersDoNotLoseCounterOrTimingSamples()
    {
        const int count = 2000;
        var diagnostics = new LauncherDiagnostics();
        var windows = new ConcurrentBag<JsonElement>();
        void Produce()
        {
            for (int i = 0; i < count; i++)
            {
                diagnostics.FrameReceived(17);
                diagnostics.FrameEnqueued(1);
                diagnostics.FrameSent(19, 1);
            }
        }
        void Capture()
        {
            for (int i = 0; i < 20; i++) windows.Add(Snapshot(diagnostics.Capture(0, 0)));
        }
        Parallel.Invoke(Produce, Produce, Capture, Capture);
        windows.Add(Snapshot(diagnostics.Capture(0, 0)));
        Assert.Equal(2 * count, windows.Sum(w => Value(w, "Tunnel", "InboundFrames")));
        Assert.Equal(34 * count, windows.Sum(w => Value(w, "Tunnel", "InboundPayloadBytes")));
        Assert.Equal(2 * count, windows.Sum(w => Value(w, "Tunnel", "EnqueuedFrames")));
        Assert.Equal(2 * count, windows.Sum(w => Value(w, "Tunnel", "OutboundFrames")));
        Assert.Equal(38 * count, windows.Sum(w => Value(w, "Tunnel", "OutboundPayloadBytes")));
        Assert.Equal(2 * count, windows.Sum(w => Value(w, "Tunnel", "SendCompleted", "Count")));
        Assert.Equal(2 * count, windows.Sum(w => Value(w, "Tunnel", "CompleteFrameToGameEnqueue", "Count")));
    }

    private sealed class Recorder : ITransportLog, IPacketRecorder
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) { }
    }

    private sealed class Echo : ISoeService
    {
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection connection) { }
        public void OnMessage(SoeConnection connection, Span<byte> message) => connection.Send(message);
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostDiagnosticsPreserveHttpsAndBothTunnelRoutesWhileMeasuringRealWork(bool enabled)
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-launcher-metrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var options = new LauncherHostOptions { Port = ((IPEndPoint)reservation.LocalEndpoint).Port };
        reservation.Stop();
        File.WriteAllText(Path.Combine(root, "launcher-host.json"), JsonSerializer.Serialize(options));
        var recorder = new Recorder();
        using var login = new SoeListener(new(IPAddress.Loopback, 0), new Echo(), recorder);
        using var gateway = new SoeListener(new(IPAddress.Loopback, 0), new Echo(), recorder);
        login.Start(); gateway.Start();
        var accounts = new LocalAccountDirectory("owner");
        var zone = new ZoneService(recorder, recorder, new GatewayTicketRegistry(), new ZoneOptions()) { Post = a => a() };
        try
        {
            await using var host = new LauncherHost(root, accounts, zone, login.LocalEndPoint.Port, gateway.LocalEndPoint.Port,
                loginListener: login, gatewayListener: gateway, enableDiagnostics: enabled);
            await host.StartAsync();
            var settings = new LauncherSettings { ServerUrl = $"https://127.0.0.1:{options.Port}/", CertificateSha256 = host.CertificateSha256 };
            using var http = LauncherConnection.CreateHttp(settings);
            using var health = await http.GetAsync("health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            using var unauthenticated = await http.GetAsync("api/state");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            using var registration = await http.PostAsJsonAsync("api/register", new Credentials("MetricsPlayer", "integration-password", options.JoinCode));
            registration.EnsureSuccessStatusCode();
            var account = (await registration.Content.ReadFromJsonAsync<AuthSession>())!;
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + account.Token);
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                LauncherConnection.ValidateCertificate(certificate, errors, host.CertificateSha256);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{options.Port}/api/tunnel"), deadline.Token);
            byte[] request = [0, 1, 0, 0, 0, 3, 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 2, 0, (byte)'T', 0];
            foreach (byte channel in new byte[] { 0, 1 })
            {
                byte[] frame = new byte[request.Length + 1];
                frame[0] = channel;
                request.CopyTo(frame, 1);
                await socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, deadline.Token);
                byte[] response = new byte[512];
                int length = 0;
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(response.AsMemory(length), deadline.Token);
                    length += received.Count;
                } while (!received.EndOfMessage);
                Assert.Equal(WebSocketMessageType.Binary, received.MessageType);
                Assert.True(length >= 3);
                Assert.Equal(channel, response[0]);
                Assert.Equal((byte)SoeOpcode.SessionReply, response[2]);
            }

            var windows = new List<JsonElement>();
            long Total(params string[] path) => windows.Sum(window => Value(window, path));
            async Task CaptureUntil(Func<bool> complete)
            {
                do
                {
                    windows.Add(Snapshot(host.CaptureDiagnostics()));
                    if (complete()) return;
                    await Task.Delay(10, deadline.Token);
                } while (true);
            }
            await CaptureUntil(() => !enabled || Total("Tunnel", "OutboundFrames") >= 2 && Total("Http", "Completed") >= 3);
            Assert.Equal(enabled, windows[^1].GetProperty("Enabled").GetBoolean());
            Assert.Equal(1, Value(windows[^1], "ActiveTunnelRoutes"));
            Assert.Equal(0, Value(windows[^1], "ActiveVoiceConnections"));
            if (enabled)
            {
                Assert.Equal(2, Total("Tunnel", "InboundFrames"));
                Assert.Equal(2 * (request.Length + 1), Total("Tunnel", "InboundPayloadBytes"));
                Assert.Equal(2, Total("Tunnel", "EnqueuedFrames"));
                Assert.Equal(2, Total("Tunnel", "SendSemaphoreWait", "Count"));
                Assert.Equal(2, Total("Tunnel", "SendCompleted", "Count"));
                Assert.Equal(2, Total("Tunnel", "CompleteFrameToGameEnqueue", "Count"));
                Assert.Equal(1, Total("Http", "Unauthorized401"));
                Assert.True(Total("GateWait", "Count") >= 3);
                Assert.Equal(Total("GateWait", "Count"), Total("GateHold", "Count"));
            }
            else
            {
                Assert.False(windows[^1].TryGetProperty("Http", out _));
                Assert.False(windows[^1].TryGetProperty("Tunnel", out _));
            }

            // A malformed channel still terminates the tunnel; metrics only classify that outcome.
            await socket.SendAsync(new byte[] { 2, 0 }.AsMemory(), WebSocketMessageType.Binary, true, deadline.Token);
            await CaptureUntil(() => Value(windows[^1], "ActiveTunnelRoutes") == 0
                && (!enabled || Total("Http", "Faulted") >= 1));
            if (enabled)
            {
                Assert.Equal(1, Total("Tunnel", "Started"));
                Assert.Equal(1, Total("Tunnel", "Accepted"));
                Assert.Equal(1, Total("Tunnel", "Closures", "InvalidFrame"));
                Assert.Equal(3, Total("Tunnel", "InboundFrames"));
                Assert.Equal(2, Total("Tunnel", "EnqueuedFrames"));
                Assert.Equal(1, Total("Http", "Status1xx"));
                var reset = Snapshot(host.CaptureDiagnostics());
                Assert.Equal(0, Value(reset, "Http", "ActiveRequests"));
                Assert.Equal(0, Value(reset, "Tunnel", "InboundFrames"));
                Assert.Equal(0, Value(reset, "Tunnel", "SendCompleted", "Count"));
                Assert.Equal(0, Value(reset, "Tunnel", "CompleteFrameToGameEnqueue", "Count"));
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
