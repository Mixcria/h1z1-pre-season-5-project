using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Channels;
using Cranberry.Transport;

namespace Cranberry.Launcher.Service;

// All labels are fixed. No account, address, request path, token or payload enters a snapshot.
internal sealed class LauncherDiagnostics
{
    private readonly object _snapshotGate = new();
    private long _windowStart = Stopwatch.GetTimestamp();
    private long _httpStarted, _httpCompleted, _httpFaulted, _activeHttp, _unauthorized, _rateLimited;
    private readonly long[] _httpStatusClasses = new long[6];
    private long _tunnelStarted, _tunnelAccepted, _inFrames, _inBytes, _enqueuedFrames, _outFrames, _outBytes;
    private readonly long[] _closures = new long[Enum.GetValues<TunnelClosure>().Length];

    internal DiagnosticTiming GateWait { get; } = new();
    internal DiagnosticTiming GateHold { get; } = new();
    internal DiagnosticTiming WssSendWait { get; } = new();
    internal DiagnosticTiming WssSendCompleted { get; } = new();
    internal DiagnosticTiming FrameToGameEnqueue { get; } = new();

    internal void HttpStarted()
    {
        Interlocked.Increment(ref _httpStarted);
        Interlocked.Increment(ref _activeHttp);
    }

    internal void HttpCompleted(int status, bool faulted)
    {
        Interlocked.Increment(ref _httpCompleted);
        Interlocked.Decrement(ref _activeHttp);
        if (faulted) Interlocked.Increment(ref _httpFaulted);
        int category = status is >= 100 and < 600 ? status / 100 : 0;
        Interlocked.Increment(ref _httpStatusClasses[category]);
        if (status == 401) Interlocked.Increment(ref _unauthorized);
        if (status == 429) Interlocked.Increment(ref _rateLimited);
    }

    internal void TunnelStarted() => Interlocked.Increment(ref _tunnelStarted);
    internal void TunnelAccepted() => Interlocked.Increment(ref _tunnelAccepted);
    internal void TunnelClosed(Exception? failure)
    {
        TunnelClosure reason = failure switch
        {
            null => TunnelClosure.Completed,
            OperationCanceledException => TunnelClosure.Cancelled,
            InvalidDataException => TunnelClosure.InvalidFrame,
            TimeoutException => TunnelClosure.Timeout,
            WebSocketException => TunnelClosure.WebSocket,
            SocketException => TunnelClosure.Socket,
            ChannelClosedException => TunnelClosure.QueueClosed,
            ObjectDisposedException => TunnelClosure.Disposed,
            IOException => TunnelClosure.Io,
            _ => TunnelClosure.Other
        };
        Interlocked.Increment(ref _closures[(int)reason]);
    }

    internal void FrameReceived(int bytes)
    {
        Interlocked.Increment(ref _inFrames);
        Interlocked.Add(ref _inBytes, bytes);
    }

    internal void FrameEnqueued(long elapsedTicks)
    {
        Interlocked.Increment(ref _enqueuedFrames);
        FrameToGameEnqueue.RecordTicks(elapsedTicks);
    }

    internal void FrameSent(int bytes, long elapsedTicks)
    {
        Interlocked.Increment(ref _outFrames);
        Interlocked.Add(ref _outBytes, bytes);
        WssSendCompleted.RecordTicks(elapsedTicks);
    }

    internal object Capture(int activeTunnelRoutes, int activeVoiceConnections)
    {
        lock (_snapshotGate)
        {
            long now = Stopwatch.GetTimestamp();
            double windowMs = Stopwatch.GetElapsedTime(_windowStart, now).TotalMilliseconds;
            _windowStart = now;
            var statuses = new long[_httpStatusClasses.Length];
            for (int i = 0; i < statuses.Length; i++) statuses[i] = Interlocked.Exchange(ref _httpStatusClasses[i], 0);
            var closures = new Dictionary<string, long>();
            foreach (TunnelClosure reason in Enum.GetValues<TunnelClosure>())
                closures.Add(reason.ToString(), Interlocked.Exchange(ref _closures[(int)reason], 0));
            return new
            {
                Enabled = true,
                WindowMs = windowMs,
                ActiveTunnelRoutes = activeTunnelRoutes,
                ActiveVoiceConnections = activeVoiceConnections,
                Http = new
                {
                    ActiveRequests = Interlocked.Read(ref _activeHttp),
                    Started = Interlocked.Exchange(ref _httpStarted, 0),
                    Completed = Interlocked.Exchange(ref _httpCompleted, 0),
                    Faulted = Interlocked.Exchange(ref _httpFaulted, 0),
                    Status1xx = statuses[1], Status2xx = statuses[2], Status3xx = statuses[3],
                    Status4xx = statuses[4], Status5xx = statuses[5], StatusOther = statuses[0],
                    Unauthorized401 = Interlocked.Exchange(ref _unauthorized, 0),
                    RateLimited429 = Interlocked.Exchange(ref _rateLimited, 0)
                },
                GateWait = GateWait.TakeSnapshot(),
                GateHold = GateHold.TakeSnapshot(),
                Tunnel = new
                {
                    Started = Interlocked.Exchange(ref _tunnelStarted, 0),
                    Accepted = Interlocked.Exchange(ref _tunnelAccepted, 0),
                    InboundFrames = Interlocked.Exchange(ref _inFrames, 0),
                    InboundPayloadBytes = Interlocked.Exchange(ref _inBytes, 0),
                    EnqueuedFrames = Interlocked.Exchange(ref _enqueuedFrames, 0),
                    OutboundFrames = Interlocked.Exchange(ref _outFrames, 0),
                    OutboundPayloadBytes = Interlocked.Exchange(ref _outBytes, 0),
                    Closures = closures,
                    SendSemaphoreWait = WssSendWait.TakeSnapshot(),
                    SendCompleted = WssSendCompleted.TakeSnapshot(),
                    CompleteFrameToGameEnqueue = FrameToGameEnqueue.TakeSnapshot()
                }
            };
        }
    }

    private enum TunnelClosure { Completed, Cancelled, InvalidFrame, Timeout, WebSocket, Socket, QueueClosed, Disposed, Io, Other }
}
