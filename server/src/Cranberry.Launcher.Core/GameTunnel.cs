using System.Net;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Cranberry.Launcher.Core;

public static class LauncherConnection
{
    public static bool ValidateCertificate(X509Certificate? certificate, SslPolicyErrors errors, string pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return errors == SslPolicyErrors.None;
        return certificate is not null && pin.Length == 64 && pin.All(char.IsAsciiHexDigit)
            && CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), Convert.FromHexString(pin));
    }

    public static HttpClient CreateHttp(LauncherSettings settings)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            ValidateCertificate(cert, errors, settings.CertificateSha256);
        return new HttpClient(handler) { BaseAddress = LauncherSettings.ValidateServer(settings.ServerUrl), Timeout = TimeSpan.FromMinutes(20) };
    }
}

/// <summary>Legacy game UDP stays on loopback; one authenticated TLS websocket carries it over the network.</summary>
public sealed class GameTunnel : IAsyncDisposable
{
    private readonly UdpClient[] _sockets = [new(new IPEndPoint(IPAddress.Loopback, 0)), new(new IPEndPoint(IPAddress.Loopback, 0))];
    private readonly TunnelSessionRoute[] _routes = [new(), new()];
    private readonly ClientWebSocket _webSocket = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _send = new(1);
    private Task? _running;
    private Task? _metricsWriter;
    internal string? MetricsDirectory { get; init; }
    public GameTunnelMetrics? Metrics { get; private set; }
    public int LoginPort => ((IPEndPoint)_sockets[0].Client.LocalEndPoint!).Port;
    public int GatewayPort => ((IPEndPoint)_sockets[1].Client.LocalEndPoint!).Port;
    public Task Completion => _running ?? Task.CompletedTask;
    public Action<byte, string, ReadOnlyMemory<byte>>? ObserveDatagram { get; set; }

    public async Task Connect(LauncherSettings settings, string token, CancellationToken ct = default)
    {
        if (settings.TransportDiagnostics) Metrics = new();
        foreach (var socket in _sockets)
        {
            socket.Client.ReceiveBufferSize = 1024 * 1024;
            socket.Client.SendBufferSize = 1024 * 1024;
        }
        Uri server = LauncherSettings.ValidateServer(settings.ServerUrl);
        if (server.Scheme != "https") throw new InvalidDataException("The game tunnel requires HTTPS.");
        _webSocket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        _webSocket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            LauncherConnection.ValidateCertificate(certificate, errors, settings.CertificateSha256);
        _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _webSocket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        var uri = new UriBuilder(new Uri(server, "api/tunnel")) { Scheme = "wss" };
        // The launcher calls from WinForms. Network progress must remain independent
        // of window painting, social polling, modal loops and other UI work.
        await _webSocket.ConnectAsync(uri.Uri, ct).ConfigureAwait(false);
        if (Metrics is not null) _metricsWriter = Metrics.WriteWindows(_stop.Token, MetricsDirectory);
        _running = Run();
    }

    private async Task Run()
    {
        Task[] loops = [PumpUdp(0), PumpUdp(1), PumpWebSocket()];
        try
        {
            Task completed = await Task.WhenAny(loops).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        finally
        {
            _stop.Cancel();
            try { await Task.WhenAll(loops).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task PumpUdp(byte channel)
    {
        while (!_stop.IsCancellationRequested)
        {
            var message = await _sockets[channel].ReceiveAsync(_stop.Token).ConfigureAwait(false);
            long udpReceived = Metrics is null ? 0 : Stopwatch.GetTimestamp();
            ObserveDatagram?.Invoke(channel, "udp-receive", message.Buffer);
            if (!_routes[channel].AcceptClient(message.RemoteEndPoint, message.Buffer)) continue;
            byte[] frame = new byte[message.Buffer.Length + 1];
            frame[0] = channel;
            message.Buffer.CopyTo(frame, 1);
            long waiting = Metrics is null ? 0 : Stopwatch.GetTimestamp();
            await _send.WaitAsync(_stop.Token).ConfigureAwait(false);
            Metrics?.SendWait(waiting);
            try
            {
                long sending = Metrics is null ? 0 : Stopwatch.GetTimestamp();
                await _webSocket.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, _stop.Token).ConfigureAwait(false);
                Metrics?.Sent(udpReceived, sending, message.Buffer.Length);
                ObserveDatagram?.Invoke(channel, "ws-send", message.Buffer);
            }
            finally { _send.Release(); }
        }
    }

    private async Task PumpWebSocket()
    {
        byte[] frame = new byte[65508];
        while (!_stop.IsCancellationRequested)
        {
            int length = 0;
            ValueWebSocketReceiveResult received;
            do
            {
                if (length == frame.Length) throw new InvalidDataException("Tunnel frame too large.");
                received = await _webSocket.ReceiveAsync(frame.AsMemory(length), _stop.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close) return;
                if (received.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("Invalid tunnel frame.");
                length += received.Count;
            } while (!received.EndOfMessage);
            if (length < 2 || frame[0] > 1) throw new InvalidDataException("Invalid game channel.");
            long wsReceived = Metrics is null ? 0 : Stopwatch.GetTimestamp();
            ObserveDatagram?.Invoke(frame[0], "ws-receive", frame.AsMemory(1, length - 1));
            if (_routes[frame[0]].ServerDestination(frame.AsSpan(1, length - 1)) is { } peer)
            {
                await _sockets[frame[0]].SendAsync(frame.AsMemory(1, length - 1), peer, _stop.Token).ConfigureAwait(false);
                Metrics?.Delivered(wsReceived, length - 1);
                ObserveDatagram?.Invoke(frame[0], "udp-send", frame.AsMemory(1, length - 1));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _webSocket.Abort();
        if (_running is not null) try { await _running.ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or SocketException or IOException) { }
        if (_metricsWriter is not null) await _metricsWriter.ConfigureAwait(false);
        foreach (var socket in _sockets) socket.Dispose();
        _webSocket.Dispose();
        _stop.Dispose();
        _send.Dispose();
    }
}
