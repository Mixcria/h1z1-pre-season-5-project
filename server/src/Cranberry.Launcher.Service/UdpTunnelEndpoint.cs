using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Diagnostics;
using Cranberry.Login;
using Cranberry.Transport;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;

namespace Cranberry.Launcher.Service;

internal static class UdpTunnelEndpoint
{
    public static Task Run(HttpContext context, string account, LocalAccountDirectory accounts, int loginPort, int gatewayPort,
        Action<byte, string, ReadOnlyMemory<byte>>? observer = null,
        SoeListener? loginListener = null, SoeListener? gatewayListener = null, LauncherDiagnostics? diagnostics = null)
        => diagnostics is null
            ? RunCore(context, account, accounts, loginPort, gatewayPort, observer, loginListener, gatewayListener, null)
            : RunMeasured(context, account, accounts, loginPort, gatewayPort, observer, loginListener, gatewayListener, diagnostics);

    private static async Task RunMeasured(HttpContext context, string account, LocalAccountDirectory accounts,
        int loginPort, int gatewayPort, Action<byte, string, ReadOnlyMemory<byte>>? observer,
        SoeListener? loginListener, SoeListener? gatewayListener, LauncherDiagnostics diagnostics)
    {
        diagnostics.TunnelStarted();
        Exception? failure = null;
        try { await RunCore(context, account, accounts, loginPort, gatewayPort, observer, loginListener, gatewayListener, diagnostics); }
        catch (Exception ex) { failure = ex; throw; }
        finally { diagnostics.TunnelClosed(failure); }
    }

    private static async Task RunCore(HttpContext context, string account, LocalAccountDirectory accounts, int loginPort, int gatewayPort,
        Action<byte, string, ReadOnlyMemory<byte>>? observer,
        SoeListener? loginListener, SoeListener? gatewayListener, LauncherDiagnostics? diagnostics)
    {
        // Reserve one source identity for both listeners until both local routes are retired.
        // The socket carries no traffic on the in-process path; the legacy UDP bridge remains
        // available when the launcher service runs separately from the game listeners.
        using var reservation = loginListener is null ? null : new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using IGamePort login = loginListener is null ? new UdpGamePort(loginPort)
            : new LocalGamePort(loginListener.OpenLocalPeer((IPEndPoint)reservation!.Client.LocalEndPoint!));
        await using IGamePort gateway = gatewayListener is null ? new UdpGamePort(gatewayPort)
            : new LocalGamePort(gatewayListener.OpenLocalPeer((IPEndPoint)reservation!.Client.LocalEndPoint!));
        observer?.Invoke(1, "gateway-port-" + gateway.SourcePort, ReadOnlyMemory<byte>.Empty);
        int sourcePort = login.SourcePort;
        accounts.BindTunnel(sourcePort, account);
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            diagnostics?.TunnelAccepted();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            stop.CancelAfter(TimeSpan.FromHours(12));
            using var send = new SemaphoreSlim(1);
            IGamePort[] udp = [login, gateway];
            async Task FromGame(byte channel)
            {
                byte[] frame = [];
                while (!stop.IsCancellationRequested)
                {
                    var response = await udp[channel].ReceiveAsync(stop.Token);
                    observer?.Invoke(channel, loginListener is null ? "udp-receive" : "local-receive", response);
                    // This pump awaits every write before reusing its frame. Retain the
                    // largest datagram buffer instead of allocating one for every relay.
                    if (frame.Length < response.Length + 1) frame = new byte[response.Length + 1];
                    frame[0] = channel;
                    response.CopyTo(frame, 1);
                    long waitStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                    await send.WaitAsync(stop.Token);
                    try
                    {
                        if (diagnostics is not null)
                            diagnostics.WssSendWait.RecordTicks(Stopwatch.GetTimestamp() - waitStarted);
                        long sendStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                        // Allow a brief WAN stall to recover; a peer that stops reading
                        // still releases its bounded game channels before SOE's 45s limit.
                        // Completed writes avoid a timer allocation inside WaitAsync.
                        await socket.SendAsync(frame.AsMemory(0, response.Length + 1), WebSocketMessageType.Binary, true, stop.Token)
                            .AsTask().WaitAsync(TimeSpan.FromSeconds(30), stop.Token);
                        if (diagnostics is not null)
                            diagnostics.FrameSent(response.Length + 1, Stopwatch.GetTimestamp() - sendStarted);
                        observer?.Invoke(channel, "ws-send", response);
                    }
                    finally { send.Release(); }
                }
            }
            async Task FromLauncher()
            {
                byte[] frame = new byte[65508];
                while (!stop.IsCancellationRequested)
                {
                    int length = 0;
                    ValueWebSocketReceiveResult received;
                    do
                    {
                        if (length == frame.Length) throw new InvalidDataException("Tunnel frame too large.");
                        received = await socket.ReceiveAsync(frame.AsMemory(length), stop.Token);
                        if (received.MessageType == WebSocketMessageType.Close) return;
                        if (received.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("Invalid tunnel message.");
                        length += received.Count;
                    } while (!received.EndOfMessage);
                    long frameCompleted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                    diagnostics?.FrameReceived(length);
                    if (length < 2 || frame[0] > 1) throw new InvalidDataException("Invalid tunnel channel.");
                    observer?.Invoke(frame[0], "ws-receive", frame.AsMemory(1, length - 1));
                    await udp[frame[0]].SendAsync(frame.AsMemory(1, length - 1), stop.Token);
                    if (diagnostics is not null)
                        diagnostics.FrameEnqueued(Stopwatch.GetTimestamp() - frameCompleted);
                    observer?.Invoke(frame[0], loginListener is null ? "udp-send" : "local-send", frame.AsMemory(1, length - 1));
                }
            }
            Task[] tasks = [FromGame(0), FromGame(1), FromLauncher()];
            try { await await Task.WhenAny(tasks); }
            finally
            {
                stop.Cancel();
                socket.Abort();
                try { await Task.WhenAll(tasks); }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or SocketException
                    or IOException or ChannelClosedException or ObjectDisposedException or TimeoutException) { }
            }
        }
        finally { accounts.UnbindTunnel(sourcePort); }
    }

    private interface IGamePort : IAsyncDisposable
    {
        int SourcePort { get; }
        ValueTask<byte[]> ReceiveAsync(CancellationToken ct);
        ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);
    }

    private sealed class LocalGamePort(SoeLocalPeer peer) : IGamePort
    {
        public int SourcePort => peer.RemoteEndPoint.Port;
        public ValueTask<byte[]> ReceiveAsync(CancellationToken ct) => peer.ReceiveAsync(ct);
        public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); peer.Send(bytes.Span); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => peer.DisposeAsync();
    }

    private sealed class UdpGamePort : IGamePort
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        public UdpGamePort(int port) => _udp.Connect(IPAddress.Loopback, port);
        public int SourcePort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        public async ValueTask<byte[]> ReceiveAsync(CancellationToken ct) => (await _udp.ReceiveAsync(ct)).Buffer;
        public async ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) => await _udp.SendAsync(bytes, ct);
        public ValueTask DisposeAsync() { _udp.Dispose(); return ValueTask.CompletedTask; }
    }
}
