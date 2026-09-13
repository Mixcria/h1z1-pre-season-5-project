using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Threading.Channels;

namespace Cranberry.Transport;

/// <summary>
/// A bounded datagram bridge for a TLS endpoint in this process. The caller reserves the
/// loopback source port until DisposeAsync completes. All SOE state still belongs to the listener.
/// </summary>
public sealed class SoeLocalPeer : IAsyncDisposable
{
    private const int MaxDatagrams = 512, MaxPeerBytes = 256 << 10;
    private readonly SoeListener _listener;
    private readonly record struct ReliableQueueKey(SoeConnection Connection, SoeOpcode Opcode, ushort Sequence);
    private readonly record struct QueuedDatagram(byte[] Bytes, long QueuedAt, ReliableQueueKey? ReliableKey = null);
    private readonly ConcurrentQueue<QueuedDatagram> _incoming = new();
    private readonly Channel<QueuedDatagram> _outgoing = Channel.CreateBounded<QueuedDatagram>(new BoundedChannelOptions(MaxDatagrams)
    { SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly object _gate = new();
    private readonly Dictionary<ReliableQueueKey, byte[]> _queuedReliable = new();
    private int _incomingBytes, _outgoingBytes, _scheduled;
    private volatile bool _closed;
    private readonly TaskCompletionSource _retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IPEndPoint RemoteEndPoint { get; }
    public bool IsClosed => _closed;
    internal SoeLocalPeer(SoeListener listener, IPEndPoint remote) { _listener = listener; RemoteEndPoint = remote; }

    /// <summary>Copies a complete datagram. Exceeding the bounded backlog closes only this peer.</summary>
    public void Send(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length is < 2 or > 65507)
        {
            _listener.DiagnosticCollector?.Record(SoeDiagnosticCounter.InvalidDatagramSize);
            throw new InvalidDataException("Invalid SOE datagram size.");
        }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_incoming.Count >= MaxDatagrams || _incomingBytes + datagram.Length > MaxPeerBytes
                || !_listener.ReserveLocalBytes(datagram.Length))
            {
                _listener.DiagnosticCollector?.Record(SoeDiagnosticCounter.LocalInputBacklogRejected);
                BeginClose(new IOException("Local game input backlog exceeded."));
                throw new IOException("Local game input backlog exceeded.");
            }
            _incoming.Enqueue(new(datagram.ToArray(), _listener.DiagnosticCollector is null ? 0 : Stopwatch.GetTimestamp()));
            _incomingBytes += datagram.Length;
            Schedule();
        }
    }

    public async ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        QueuedDatagram queued = await _outgoing.Reader.ReadAsync(cancellationToken);
        byte[] bytes = queued.Bytes;
        lock (_gate) ReleaseOutput(queued);
        if (_listener.DiagnosticCollector is { } diagnostics)
            diagnostics.LocalOutputQueueWait.RecordTicks(Stopwatch.GetTimestamp() - queued.QueuedAt);
        return bytes;
    }

    internal bool TryRead(out byte[] bytes)
    {
        lock (_gate)
        {
            if (_incoming.TryDequeue(out QueuedDatagram queued))
            {
                bytes = queued.Bytes;
                _incomingBytes -= bytes.Length;
                _listener.ReleaseLocalBytes(bytes.Length);
                if (_listener.DiagnosticCollector is { } diagnostics)
                    diagnostics.LocalInputQueueWait.RecordTicks(Stopwatch.GetTimestamp() - queued.QueuedAt);
                return true;
            }
            bytes = null!;
            return false;
        }
    }

    internal bool TryWriteResponse(SoeConnection connection, ReadOnlySpan<byte> datagram)
    {
        lock (_gate)
        {
            if (_closed) return false;
            ReliableQueueKey? reliableKey = null;
            if (datagram.Length >= connection.Settings.ReliableHeaderLength && datagram[0] == 0
                && (SoeOpcode)datagram[1] is SoeOpcode.Data or SoeOpcode.DataFragment)
            {
                int sequenceOffset = connection.Settings.ReliableHeaderLength - 2;
                reliableKey = new(connection, (SoeOpcode)datagram[1], BinaryPrimitives.ReadUInt16BigEndian(datagram[sequenceOffset..]));
                // A retry cannot repair loss until its first copy leaves this queue.
                // Keep one identical copy during tunnel backpressure, without changing
                // wire bytes, FIFO order, or retries after the response pump dequeues it.
                if (_queuedReliable.TryGetValue(reliableKey.Value, out byte[]? pending) && datagram.SequenceEqual(pending))
                {
                    _listener.DiagnosticCollector?.Record(SoeDiagnosticCounter.LocalOutputRetriesCoalesced);
                    return true;
                }
            }
            if (_outgoingBytes + datagram.Length > MaxPeerBytes || !_listener.ReserveLocalBytes(datagram.Length))
            {
                _listener.DiagnosticCollector?.Record(SoeDiagnosticCounter.LocalOutputBacklogRejected);
                BeginClose(new IOException("Local game output backlog exceeded.")); return false;
            }
            byte[] bytes = datagram.ToArray();
            if (!_outgoing.Writer.TryWrite(new(bytes, _listener.DiagnosticCollector is null ? 0 : Stopwatch.GetTimestamp(), reliableKey)))
            {
                _listener.DiagnosticCollector?.Record(SoeDiagnosticCounter.LocalOutputBacklogRejected);
                _listener.ReleaseLocalBytes(datagram.Length);
                BeginClose(new IOException("Local game output backlog exceeded.")); return false;
            }
            _outgoingBytes += datagram.Length;
            if (reliableKey is { } key) _queuedReliable[key] = bytes;
            return true;
        }
    }

    // Called under _gate, including when retirement races a response already dequeued.
    private void ReleaseOutput(QueuedDatagram queued)
    {
        _outgoingBytes -= queued.Bytes.Length;
        _listener.ReleaseLocalBytes(queued.Bytes.Length);
        if (queued.ReliableKey is { } key && _queuedReliable.TryGetValue(key, out byte[]? current)
            && ReferenceEquals(current, queued.Bytes))
            _queuedReliable.Remove(key);
    }

    private void Schedule()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 0) _listener.ScheduleLocal(this);
    }

    internal void Reschedule()
    {
        lock (_gate)
        {
            Volatile.Write(ref _scheduled, 0);
            if (!_closed && !_incoming.IsEmpty) Schedule();
        }
    }

    private void BeginClose(Exception? error = null)
    {
        if (_closed) return;
        _closed = true;
        _outgoing.Writer.TryComplete(error);
        _listener.RetireLocal(this);
    }

    internal void CompleteRetirement()
    {
        lock (_gate)
        {
            _closed = true;
            _outgoing.Writer.TryComplete();
            while (_incoming.TryDequeue(out QueuedDatagram queued))
            {
                _listener.ReleaseLocalBytes(queued.Bytes.Length);
                _listener.DiagnosticCollector?.Record(SoeDiagnosticCounter.LocalDiscardedDatagrams);
                _listener.DiagnosticCollector?.Record(SoeDiagnosticCounter.LocalDiscardedBytes, queued.Bytes.Length);
            }
            _incomingBytes = 0;
            // Stop may overlap the response pump, so the channel supports concurrent readers.
            while (_outgoing.Reader.TryRead(out QueuedDatagram queued))
            {
                ReleaseOutput(queued);
                _listener.DiagnosticCollector?.Record(SoeDiagnosticCounter.LocalDiscardedDatagrams);
                _listener.DiagnosticCollector?.Record(SoeDiagnosticCounter.LocalDiscardedBytes, queued.Bytes.Length);
            }
        }
        _retired.TrySetResult();
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) BeginClose();
        return new(_retired.Task);
    }
}

public sealed partial class SoeListener
{
    private const long MaxLocalQueuedBytes = 64L << 20;
    private readonly ConcurrentDictionary<IPEndPoint, SoeLocalPeer> _localPeers = new();
    private readonly ConcurrentQueue<SoeLocalPeer> _localReady = new();
    private readonly object _localGate = new();
    private long _localQueuedBytes;

    public SoeLocalPeer OpenLocalPeer(IPEndPoint reservedSource)
    {
        if (!IPAddress.IsLoopback(reservedSource.Address) || reservedSource.Port == 0)
            throw new ArgumentException("A reserved loopback source port is required.", nameof(reservedSource));
        lock (_localGate)
        {
            if (!_running) throw new InvalidOperationException("The SOE listener is not running.");
            if (_localPeers.Count >= _options.MaxConnections)
            {
                _diagnostic?.Record(SoeDiagnosticCounter.ConnectionCapacityRejected);
                throw new IOException("Game tunnel capacity reached.");
            }
            var peer = new SoeLocalPeer(this, new IPEndPoint(reservedSource.Address, reservedSource.Port));
            if (!_localPeers.TryAdd(peer.RemoteEndPoint, peer)) throw new InvalidOperationException("Source port already has a local route.");
            return peer;
        }
    }

    internal bool ReserveLocalBytes(int bytes)
    {
        if (Interlocked.Add(ref _localQueuedBytes, bytes) <= MaxLocalQueuedBytes) return true;
        _diagnostic?.Record(SoeDiagnosticCounter.LocalByteBudgetRejected);
        Interlocked.Add(ref _localQueuedBytes, -bytes); return false;
    }
    internal void ReleaseLocalBytes(int bytes) => Interlocked.Add(ref _localQueuedBytes, -bytes);
    internal void ScheduleLocal(SoeLocalPeer peer)
    {
        _localReady.Enqueue(peer);
        WakeListener();
    }

    private void DrainLocalDatagrams()
    {
        long started = Stopwatch.GetTimestamp();
        int budget = _options.MaxDatagramsPerPass;
        int handled = 0;
        while (budget > 0 && _localReady.TryDequeue(out SoeLocalPeer? peer))
        {
            // Round robin: one busy sender cannot sit in front of every other peer's ACKs.
            int peerBudget = 8;
            while (!peer.IsClosed && peerBudget > 0 && budget > 0 && peer.TryRead(out byte[] bytes))
            {
                // Charged after the read: an idle peer's empty queue must not spend the pass budget.
                peerBudget--;
                budget--;
                _received++;
                handled++;
                _diagnostic?.ReceivedDatagram(bytes.Length);
                try { OnDatagram(bytes, peer.RemoteEndPoint, Environment.TickCount64, peer); }
                catch (Exception ex)
                {
                    _diagnostic?.Record(SoeDiagnosticCounter.ReceiveErrors);
                    _log.Error($"local receive loop: {ex}");
                }
            }
            peer.Reschedule();
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= _options.MaxReceiveWorkMs) break;
        }
        if (handled > 0) _diagnostic?.LocalDatagramsPerPass.Record(handled);
    }

    internal void RetireLocal(SoeLocalPeer peer) => Post(() =>
    {
        if (_localPeers.TryGetValue(peer.RemoteEndPoint, out var current) && ReferenceEquals(current, peer))
        {
            if (_connections.TryGetValue(peer.RemoteEndPoint, out var connection))
            { connection.Close(DisconnectCause.PeerRequested); Retire(connection); }
            _localPeers.TryRemove(peer.RemoteEndPoint, out _);
        }
        peer.CompleteRetirement();
    });

    private void CloseLocalPeers()
    {
        lock (_localGate)
        {
            foreach (var peer in _localPeers.Values) peer.CompleteRetirement();
            _localPeers.Clear();
        }
        while (_localReady.TryDequeue(out _)) { }
    }
}
