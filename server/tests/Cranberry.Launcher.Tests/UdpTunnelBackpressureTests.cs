using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using Cranberry.Launcher.Service;
using Cranberry.Login;
using Cranberry.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Cranberry.Launcher.Tests;

public sealed class UdpTunnelBackpressureTests
{
    private sealed class SilentService : ISoeService, ITransportLog
    {
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection connection) { }
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) { }
        public void OnMessage(SoeConnection connection, Span<byte> message) { }
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }

    private sealed class SocketFeature(WebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(socket);
    }

    private sealed class PausedSocket : WebSocket
    {
        public readonly Channel<byte[]> Incoming = Channel.CreateUnbounded<byte[]>();
        public readonly TaskCompletionSource SendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<byte[]> Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => _state;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            SendStarted.TrySetResult();
            await Resume.Task.WaitAsync(ct);
            Sent.TrySetResult(buffer.ToArray());
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            byte[] frame = await Incoming.Reader.ReadAsync(ct);
            frame.CopyTo(buffer.AsSpan());
            return new(frame.Length, WebSocketMessageType.Binary, true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BlockedWriteCanRecoverAfterSixSecondsOrCancelPromptly(bool recover)
    {
        var service = new SilentService();
        using var login = new SoeListener(new(IPAddress.Loopback, 0), service, service);
        using var gateway = new SoeListener(new(IPAddress.Loopback, 0), service, service);
        login.Start(); gateway.Start();
        using var socket = new PausedSocket();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        context.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));
        var accounts = new LocalAccountDirectory("owner");
        Task run = UdpTunnelEndpoint.Run(context, "owner", accounts, login.LocalEndPoint.Port, gateway.LocalEndPoint.Port,
            loginListener: login, gatewayListener: gateway);
        socket.Incoming.Writer.TryWrite([1, 0, 1, 0, 0, 0, 3, 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 2, 0, (byte)'T', 0]);
        try
        {
            await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (recover)
            {
                Task pause = Task.Delay(TimeSpan.FromSeconds(6), cancellation.Token);
                Assert.Same(pause, await Task.WhenAny(run, pause));
                socket.Resume.TrySetResult();
                byte[] frame = await socket.Sent.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(1, frame[0]);
                Assert.Equal((byte)SoeOpcode.SessionReply, frame[2]);
                Assert.False(run.IsCompleted);
            }
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        Assert.Equal(WebSocketState.Aborted, socket.State);
        Assert.Equal(0, login.ConnectionCount);
        Assert.Equal(0, gateway.ConnectionCount);
    }
}
