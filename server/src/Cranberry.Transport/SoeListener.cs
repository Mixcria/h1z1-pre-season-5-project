using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Cranberry.Transport;

public delegate void SoeDatagramObserver(IPEndPoint peer, bool incoming, ReadOnlySpan<byte> bytes);

public sealed record SoeListenerOptions
{
    /// <summary>
    /// A session that sends nothing at all for this long is dropped. Generous, because a client
    /// waiting on a login reply sends nothing at all — not even pings — for tens of seconds.
    /// </summary>
    public int IdleTimeoutMs { get; init; } = 120_000;

    /// <summary>How often resend timers and idle checks run.</summary>
    public int TickIntervalMs { get; init; } = 20;
    public int MaxDatagramsPerPass { get; init; } = 256;
    public int MaxReceiveWorkMs { get; init; } = 5;
    public int MaxPostedActionsPerPass { get; init; } = 64;
    public int MaxPostedWorkMs { get; init; } = 5;
    public int MaxConnections { get; init; } = 8192;
    /// <summary>Opt-in bounded production timing/counters; does not change transport behavior.</summary>
    public bool EnableDiagnostics { get; init; }
    public SoeDatagramObserver? ObserveDatagram { get; init; }

    public SessionSettings Settings { get; init; } = SessionSettings.WithSeed(RandomNumberGenerator.GetInt32(1, int.MaxValue) switch { var v => (uint)v });
}

/// <summary>
/// One UDP socket, one thread. Datagrams are dispatched to their session by remote endpoint;
/// the tick runs resends and idle checks on the same thread, so sessions need no locks.
/// </summary>
public sealed partial class SoeListener : IDisposable
{
    private const int UdpConnectionReset = -1744830452; // SIO_UDP_CONNRESET

    private readonly IPEndPoint _bind;
    private readonly ISoeService _service;
    private readonly ITransportLog _log;
    private readonly SoeListenerOptions _options;
    private readonly Dictionary<IPEndPoint, SoeConnection> _connections = new();
    private readonly ConcurrentQueue<PostedAction> _posted = new();
    private readonly List<SoeConnection> _closing = new();
    private readonly HashSet<SoeConnection> _signals = new();
    private HashSet<SoeConnection> _bufferedReady = new(), _bufferedFlushing = new();
    private Socket? _socket;
    private SoeListenerWakeSignal? _wakeSignal;
    private Thread? _thread;
    private volatile bool _running;
    private long _received, _sent, _ticks, _tickWork, _longestTick;
    private int _largestReceiveBatch;

    public object Diagnostics => new
    {
        receivedDatagrams = Interlocked.Read(ref _received), sentDatagrams = Interlocked.Read(ref _sent),
        ticks = Interlocked.Read(ref _ticks), tickWorkMs = Interlocked.Read(ref _tickWork) * 1000d / Stopwatch.Frequency,
        longestTickMs = Interlocked.Read(ref _longestTick) * 1000d / Stopwatch.Frequency,
        largestReceiveBatch = Volatile.Read(ref _largestReceiveBatch), postedActions = _posted.Count,
        localPeers = _localPeers.Count, localQueuedBytes = Interlocked.Read(ref _localQueuedBytes),
    };

    public SoeListener(IPEndPoint bind, ISoeService service, ITransportLog log, SoeListenerOptions? options = null)
    {
        _bind = bind;
        _service = service;
        _log = log;
        _options = options ?? new SoeListenerOptions();
        _diagnostic = _options.EnableDiagnostics ? new SoeDiagnosticCollector() : null;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.TickIntervalMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxDatagramsPerPass);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxReceiveWorkMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxPostedActionsPerPass);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxPostedWorkMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxConnections);
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)(_socket?.LocalEndPoint ?? _bind);

    public int ConnectionCount => _connections.Count;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _socket = new Socket(_bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveTimeout = _options.TickIntervalMs,
            // All tunneled clients share this socket. Zoning produces synchronized ACK and
            // client-readiness bursts while the listener is constructing the world bootstrap.
            ReceiveBufferSize = 16 << 20,
            SendBufferSize = 4 << 20,
        };

        if (OperatingSystem.IsWindows())
        {
            // Otherwise an ICMP "port unreachable" from any peer surfaces as a receive error.
            _socket.IOControl(UdpConnectionReset, [0, 0, 0, 0], null);
        }

        try
        {
            _socket.Bind(_bind);
            _wakeSignal = new SoeListenerWakeSignal();
        }
        catch
        {
            _socket.Dispose();
            _socket = null;
            throw;
        }
        if (_diagnostic is not null)
        {
            _diagnosticWindowStart = Stopwatch.GetTimestamp();
            _lastDiagnosticTickStart = 0;
        }
        _running = true;
        _thread = new Thread(Loop) { Name = $"soe-{_bind.Port}", IsBackground = true };
        _thread.Start();
        _log.Info($"listening on {_socket.LocalEndPoint}");
    }

    public void Stop()
    {
        _running = false;
        WakeListener();
        _thread?.Join();
        _thread = null;
        _socket?.Dispose();
        _socket = null;
        _wakeSignal?.Dispose();
        _wakeSignal = null;
    }

    /// <summary>Runs <paramref name="work"/> on the listener thread, where touching connections is safe.</summary>
    public void Post(Action work)
    {
        long queuedAt = 0;
        if (_diagnostic is not null)
        {
            queuedAt = Stopwatch.GetTimestamp();
            DiagnosticPostedEnqueued();
        }
        _posted.Enqueue(new(work, queuedAt));
        WakeListener();
    }

    private void WakeListener()
    {
        // Listener-owned work is already running and checks queues before sleeping.
        if (Thread.CurrentThread != _thread) _wakeSignal?.Signal();
    }

    private void Loop()
    {
        Socket socket = _socket!;
        SoeListenerWakeSignal wake = _wakeSignal!;
        var readableSockets = new List<Socket>(2);
        byte[] buffer = new byte[65536];
        EndPoint sender = new IPEndPoint(_bind.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        long lastTick = Environment.TickCount64;

        while (_running)
        {
            long iterationStart = _diagnostic is null ? 0 : Stopwatch.GetTimestamp();
            long socketWait = 0;
            try
            {
                int handled = 0;
                // Both UDP arrival and queued tunnel/posted work can wake this wait.
                // Use the remaining timer deadline so frequent wakes do not postpone
                // maintenance or add another full interval before flushing buffers.
                long remainingMs = Math.Max(0, _options.TickIntervalMs - (Environment.TickCount64 - lastTick));
                int waitUs = _running && _localReady.IsEmpty && _posted.IsEmpty && _bufferedReady.Count == 0
                    ? (int)Math.Min(remainingMs * 1000, int.MaxValue) : 0;
                readableSockets.Clear();
                readableSockets.Add(socket);
                readableSockets.Add(wake.Reader);
                long pollStart = _diagnostic is null ? 0 : Stopwatch.GetTimestamp();
                Socket.Select(readableSockets, null, null, waitUs);
                if (_diagnostic is not null) socketWait = Stopwatch.GetTimestamp() - pollStart;
                if (readableSockets.Contains(wake.Reader)) wake.Drain();
                bool readable = readableSockets.Contains(socket);
                long receiveStart = readable ? Stopwatch.GetTimestamp() : 0;
                if (readable) do
                {
                    int received = socket.ReceiveFrom(buffer, ref sender);
                    _diagnostic?.ReceivedDatagram(received);
                    if (received > 0)
                    {
                        _received++;
                        OnDatagram(buffer.AsSpan(0, received), (IPEndPoint)sender, Environment.TickCount64);
                    }
                    else _diagnostic?.Record(SoeDiagnosticCounter.InvalidDatagramSize);
                    handled++;
                }
                while (_running && handled < _options.MaxDatagramsPerPass
                    && Stopwatch.GetElapsedTime(receiveStart).TotalMilliseconds < _options.MaxReceiveWorkMs
                    && socket.Poll(0, SelectMode.SelectRead));
                _largestReceiveBatch = Math.Max(_largestReceiveBatch, handled);
                if (handled > 0) _diagnostic?.UdpDatagramsPerPass.Record(handled);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
            {
                // Quiet interval; fall through to the tick.
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // A peer that went away; nothing to do.
            }
            catch (Exception ex)
            {
                _diagnostic?.Record(SoeDiagnosticCounter.ReceiveErrors);
                _log.Error($"receive loop: {ex}");
            }

            if (_diagnostic is not null)
                _diagnostic.ReceiveDispatch.RecordTicks(Math.Max(0, Stopwatch.GetTimestamp() - iterationStart - socketWait));
            long localStart = _diagnostic is null ? 0 : Stopwatch.GetTimestamp();
            DrainLocalDatagrams();
            if (_diagnostic is not null) _diagnostic.LocalDispatch.RecordTicks(Stopwatch.GetTimestamp() - localStart);

            // ACKs are cumulative: send the final progress/gap for each peer in this bounded
            // receive pass. Replying to every reordered packet amplifies a dropped burst into
            // many duplicate retransmission requests before its first repair can arrive.
            long signalStart = _diagnostic is null ? 0 : Stopwatch.GetTimestamp();
            foreach (SoeConnection connection in _signals) connection.FlushSignals();
            _signals.Clear();
            if (_diagnostic is not null) _diagnostic.SignalWork.RecordTicks(Stopwatch.GetTimestamp() - signalStart);

            long postedStart = Stopwatch.GetTimestamp();
            int postedBudget = _options.MaxPostedActionsPerPass;
            while (postedBudget-- > 0 && _posted.TryDequeue(out PostedAction work))
            {
                long workStart = 0;
                if (_diagnostic is not null)
                {
                    Interlocked.Decrement(ref _diagnosticPostedDepth);
                    workStart = Stopwatch.GetTimestamp();
                    _diagnostic.PostedQueueWait.RecordTicks(workStart - work.QueuedAt);
                }
                RunSafely(work.Work);
                if (_diagnostic is not null) _diagnostic.PostedWork.RecordTicks(Stopwatch.GetTimestamp() - workStart);
                if (Stopwatch.GetElapsedTime(postedStart).TotalMilliseconds >= _options.MaxPostedWorkMs) break;
            }

            long now = Environment.TickCount64;
            // Keep batching within this bounded receive/post pass, but do not hold fresh
            // gameplay events until maintenance. Retry/window policy remains in Tick/ACK.
            // Swap sets so a send triggered while flushing belongs to the following pass.
            (_bufferedReady, _bufferedFlushing) = (_bufferedFlushing, _bufferedReady);
            foreach (SoeConnection connection in _bufferedFlushing)
                RunSafely(() => connection.FlushBufferedOutput(now));
            _bufferedFlushing.Clear();
            if (now - lastTick >= _options.TickIntervalMs)
            {
                long tickStart = Stopwatch.GetTimestamp();
                if (_diagnostic is not null)
                {
                    if (_lastDiagnosticTickStart != 0)
                        _diagnostic.TransportTickInterval.RecordTicks(tickStart - _lastDiagnosticTickStart);
                    _lastDiagnosticTickStart = tickStart;
                }
                Tick(now);
                long elapsed = Stopwatch.GetTimestamp() - tickStart;
                _diagnostic?.TransportTickWork.RecordTicks(elapsed);
                _ticks++; _tickWork += elapsed; _longestTick = Math.Max(_longestTick, elapsed);
                // A crowded send/resend pass can overrun the interval. Give incoming ACKs
                // another interval to drain before repeating it; otherwise the loop receives
                // one datagram between whole-population ticks and creates a retry avalanche.
                lastTick = Environment.TickCount64;
            }
            if (_diagnostic is not null)
                _diagnostic.ListenerWork.RecordTicks(Math.Max(0, Stopwatch.GetTimestamp() - iterationStart - socketWait));
        }

        foreach (SoeConnection connection in _connections.Values.ToArray())
        {
            connection.Disconnect(DisconnectReason.Application);
            Retire(connection);
        }
        CloseLocalPeers();
        _bufferedReady.Clear();
        _bufferedFlushing.Clear();
    }

    private void OnDatagram(Span<byte> datagram, IPEndPoint remote, long now, SoeLocalPeer? local = null)
    {
        // A registered in-process route has a reserved loopback source port. Only its owning
        // channel may supply packets; raw UDP cannot inject into that authenticated tunnel.
        if (_localPeers.TryGetValue(remote, out SoeLocalPeer? owner)
            && (!ReferenceEquals(owner, local) || owner.IsClosed))
        { _diagnostic?.Record(SoeDiagnosticCounter.RouteMismatch); return; }
        if (local is not null && !ReferenceEquals(owner, local))
        { _diagnostic?.Record(SoeDiagnosticCounter.RouteMismatch); return; }
        _options.ObserveDatagram?.Invoke(remote, true, datagram);
        if (datagram.Length < 2)
        {
            _diagnostic?.Record(SoeDiagnosticCounter.InvalidDatagramSize);
            return;
        }

        var opcode = (SoeOpcode)((datagram[0] << 8) | datagram[1]);
        if (opcode == SoeOpcode.SessionRequest)
        {
            OpenSession(datagram.Slice(2), remote, now);
            return;
        }

        if (!_connections.TryGetValue(remote, out SoeConnection? connection))
        {
            _diagnostic?.Record(SoeDiagnosticCounter.UnknownSession);
            if (_log.IsEnabled(TransportLogLevel.Debug))
            {
                _log.Debug($"{remote} opcode 0x{(ushort)opcode:x4} without a session, ignored");
            }

            return;
        }

        connection.RecordDiagnosticReceive(datagram.Length);
        connection.Touch(now);
        try
        {
            connection.HandleDatagram(datagram, now, flushSignals: false);
            if (connection.HasReceiveSignals) _signals.Add(connection);
        }
        catch (SoeProtocolException ex)
        {
            _diagnostic?.Record(SoeDiagnosticCounter.ProtocolErrors);
            // Application captures omit the outer Multi/Data framing. Preserve that evidence
            // on a protocol failure, bounded to one normal datagram even for oversized input.
            int prefixLength = Math.Min(datagram.Length, 512);
            _log.Warn($"{connection}: {ex.Message}; dropping the session; "
                + $"udpBytes={datagram.Length} udpPrefixBytes={prefixLength} "
                + $"udpPrefix={Convert.ToHexString(datagram[..prefixLength])}");
            connection.Disconnect(DisconnectReason.ProtocolError);
            connection.Close(DisconnectCause.ProtocolError);
        }
        catch (Exception ex)
        {
            _diagnostic?.Record(SoeDiagnosticCounter.ServiceErrors);
            _log.Error($"{connection}: service threw {ex}");
            // Dispatch may already have consumed RC4 bytes or applied part of an action,
            // while reliable sequence admission has not completed. A retry on this session
            // cannot safely replay that action or decrypt from the old stream position.
            _diagnostic?.Record(SoeDiagnosticCounter.ServiceFailureClosures);
            connection.Disconnect(DisconnectReason.Application);
        }

        if (connection.State == ConnectionState.Closed)
        {
            Retire(connection);
        }
    }

    private void OpenSession(ReadOnlySpan<byte> body, IPEndPoint remote, long now)
    {
        SessionRequest request;
        try
        {
            request = SessionRequest.Parse(body);
        }
        catch (SoeProtocolException ex)
        {
            _diagnostic?.Record(SoeDiagnosticCounter.MalformedSessionRequest);
            _log.Warn($"{remote} malformed SessionRequest: {ex.Message}");
            return;
        }

        if (_connections.TryGetValue(remote, out var existing)
            && existing.State == ConnectionState.Open && existing.SessionId == request.SessionId
            && existing.ProtocolName == request.ProtocolName)
        {
            // A lost SessionReply causes a retry, not a new cipher or application session.
            existing.RecordDiagnosticReceive(body.Length + 2);
            Span<byte> retry = stackalloc byte[SessionReply.Length];
            SessionReply.Write(retry, request.SessionId, _options.Settings);
            Transmit(existing, retry.ToArray());
            return;
        }

        if (_connections.Remove(remote, out SoeConnection? previous))
        {
            _log.Info($"{previous} replaced by a new SessionRequest");
            previous.Close(DisconnectCause.Replaced);
            Retire(previous);
        }

        if (_connections.Count >= _options.MaxConnections)
        { _diagnostic?.Record(SoeDiagnosticCounter.ConnectionCapacityRejected); return; }
        SessionDecision decision = _service.OnSessionRequest(remote, in request);
        if (!decision.Accept)
        {
            _diagnostic?.Record(SoeDiagnosticCounter.SessionRejected);
            _log.Info($"{remote} SessionRequest for '{request.ProtocolName}' refused");
            return;
        }

        // Authenticated launcher traffic crosses TLS and a final loopback UDP hop.
        // 128 x 512 B permits reference-data loading over a WAN without waiting an
        // entire round trip for each 16 KiB. Direct UDP keeps its smaller window.
        var connection = new SoeConnection(remote, in request, _options.Settings, decision, _service, _log, Transmit, now,
            sendWindow: _localPeers.ContainsKey(remote) ? 128 : 32,
            bufferedReady: connection => _bufferedReady.Add(connection));
        if (_diagnostic is not null)
        {
            connection.EnableDiagnostics(_diagnostic, ++_nextDiagnosticSessionId);
            connection.RecordDiagnosticReceive(body.Length + 2);
            _diagnostic.Record(SoeDiagnosticCounter.ConnectionsOpened);
        }
        _connections[remote] = connection;

        Span<byte> reply = stackalloc byte[SessionReply.Length];
        SessionReply.Write(reply, request.SessionId, _options.Settings);
        Transmit(connection, reply.ToArray());

        _log.Info($"{connection} opened (client asked crc={request.CrcLength} udp={request.UdpLength}; we set crc={_options.Settings.CrcLength} udp={_options.Settings.UdpLength} encryption={(decision.Key is null ? "none" : decision.EncryptFromStart ? "on" : "later")})");
        RunSafely(() => _service.OnConnected(connection));
    }

    private void Tick(long now)
    {
        foreach (SoeConnection connection in _connections.Values)
        {
            if (now - connection.LastActivity > _options.IdleTimeoutMs)
            {
                _diagnostic?.Record(SoeDiagnosticCounter.IdleTimeoutClosures);
                _log.Info($"{connection} idle for {_options.IdleTimeoutMs} ms, dropping");
                connection.Close(DisconnectCause.Timeout);
            }
            else
            {
                connection.Tick(now);
            }

            if (connection.State == ConnectionState.Closed)
            {
                _closing.Add(connection);
            }
        }

        foreach (SoeConnection connection in _closing)
        {
            Retire(connection);
        }

        _closing.Clear();
    }

    private void Retire(SoeConnection connection)
    {
        connection.CaptureDiagnostics(include: false);
        _diagnostic?.Record(SoeDiagnosticCounter.ConnectionsRetired);
        _connections.Remove(connection.RemoteEndPoint);
        DisconnectCause cause = connection.PendingClose ?? DisconnectCause.ServerRequested;
        RunSafely(() => _service.OnDisconnected(connection, cause));
    }

    private void Transmit(SoeConnection connection, ReadOnlyMemory<byte> datagram)
    {
        try
        {
            if (_localPeers.TryGetValue(connection.RemoteEndPoint, out SoeLocalPeer? local))
            {
                bool accepted;
                long started = _diagnostic is null ? 0 : Stopwatch.GetTimestamp();
                try { accepted = local.TryWriteResponse(connection, datagram.Span); }
                finally
                {
                    if (_diagnostic is not null)
                        _diagnostic.LocalSendEnqueueWork.RecordTicks(Stopwatch.GetTimestamp() - started);
                }
                if (!accepted)
                {
                    connection.Close(DisconnectCause.Timeout);
                    return;
                }
            }
            else
            {
                long started = _diagnostic is null ? 0 : Stopwatch.GetTimestamp();
                try { _socket?.SendTo(datagram.Span, SocketFlags.None, connection.SendAddress); }
                finally
                {
                    if (_diagnostic is not null)
                        _diagnostic.UdpSendWork.RecordTicks(Stopwatch.GetTimestamp() - started);
                }
            }
            _options.ObserveDatagram?.Invoke(connection.RemoteEndPoint, false, datagram.Span);
            _sent++;
            connection.RecordDiagnosticSend(datagram.Length);
            _diagnostic?.Record(SoeDiagnosticCounter.SentDatagrams);
            _diagnostic?.Record(SoeDiagnosticCounter.SentBytes, datagram.Length);
            _diagnostic?.SentDatagramBytes.Record(datagram.Length);
        }
        catch (SocketException ex)
        {
            _diagnostic?.Record(SoeDiagnosticCounter.SendErrors);
            _log.Warn($"{connection} send failed: {ex.SocketErrorCode}");
        }
    }

    private void RunSafely(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            _diagnostic?.Record(SoeDiagnosticCounter.ServiceErrors);
            _log.Error($"service threw: {ex}");
        }
    }

    public void Dispose() => Stop();
}
