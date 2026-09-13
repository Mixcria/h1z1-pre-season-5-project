using System.Diagnostics;

namespace Cranberry.Transport;

internal enum SoeDiagnosticCounter
{
    ReceivedDatagrams, ReceivedBytes, SentDatagrams, SentBytes,
    ApplicationMessagesReceived, UnreliableApplicationMessagesReceived, ApplicationBytesReceived,
    ApplicationMessagesSent, ApplicationBytesSent, ReliableDatagramsResent, FastRetransmits,
    LatestMessagesReplaced, LatestMessagesCommitted,
    InvalidDatagramSize, RouteMismatch, UnknownSession, MalformedSessionRequest,
    ConnectionCapacityRejected, SessionRejected, ProtocolErrors, UnknownOpcodes,
    LocalInputBacklogRejected, LocalOutputBacklogRejected, LocalByteBudgetRejected,
    LocalDiscardedDatagrams, LocalDiscardedBytes, ReliableBacklogClosures,
    ReceiveErrors, SendErrors, ServiceErrors, ConnectionsOpened, ConnectionsRetired,
    IdleTimeoutClosures, ReliableTimeoutClosures,
    ReliableInOrder, ReliableAhead, ReliableAheadDuplicate, ReliableBehind,
    AcksReceived, AcksIgnored, AcksAdvanced, AckedDatagrams, RoundTripSamplesTaken,
    RoundTripSamplesExcluded, ServiceFailureClosures, LocalOutputRetriesCoalesced,
    Count
}

/// <summary>Bounded listener totals. Only queue/output hooks run outside the listener.</summary>
internal sealed class SoeDiagnosticCollector
{
    private readonly long[] _counters = new long[(int)SoeDiagnosticCounter.Count];
    public readonly DiagnosticTiming ListenerWork = new(), ReceiveDispatch = new(), LocalDispatch = new(),
        SignalWork = new(), PostedWork = new(), PostedQueueWait = new(), TransportTickWork = new(),
        TransportTickInterval = new(), LocalInputQueueWait = new(), LocalOutputQueueWait = new(),
        LatestFirstOfferToCommit = new(), LatestNewestOfferToCommit = new(),
        ReliableHeldWait = new(), ReliableAssemblyWait = new(), ReliableBufferWait = new(),
        ReliableFirstTransmitWait = new(), EncryptWork = new(), DecryptWork = new(),
        ApplicationDispatchWork = new(), UdpSendWork = new(), LocalSendEnqueueWork = new();
    public readonly DiagnosticHistogram ReceivedDatagramBytes = new(64, 128, 256, 512, 1024, 4096, 16384, 65536),
        SentDatagramBytes = new(64, 128, 256, 512, 1024, 4096, 16384, 65536),
        ReceivedApplicationBytes = new(64, 128, 256, 512, 1024, 4096, 16384, 65536, 1048576),
        SentApplicationBytes = new(64, 128, 256, 512, 1024, 4096, 16384, 65536, 1048576),
        BufferedMessagesPerDatagram = new(1, 2, 4, 8, 16, 32, 64, 128, 256),
        FragmentsPerMessage = new(1, 2, 4, 8, 16, 32, 128, 512, 2048),
        UdpDatagramsPerPass = new(1, 2, 4, 8, 16, 32, 64, 128, 256),
        LocalDatagramsPerPass = new(1, 2, 4, 8, 16, 32, 64, 128, 256);

    public void ReceivedDatagram(int bytes)
    {
        Record(SoeDiagnosticCounter.ReceivedDatagrams);
        Record(SoeDiagnosticCounter.ReceivedBytes, bytes);
        ReceivedDatagramBytes.Record(bytes);
    }
    public void Record(SoeDiagnosticCounter counter, long count = 1) => Interlocked.Add(ref _counters[(int)counter], count);
    public Dictionary<string, long> TakeCounters()
    {
        var result = new Dictionary<string, long>(_counters.Length);
        for (int i = 0; i < _counters.Length; i++)
            result.Add(((SoeDiagnosticCounter)i).ToString(), Interlocked.Exchange(ref _counters[i], 0));
        return result;
    }
}

public sealed partial class SoeListener
{
    private readonly SoeDiagnosticCollector? _diagnostic;
    private long _diagnosticWindowStart = Stopwatch.GetTimestamp(), _lastDiagnosticTickStart;
    private long _nextDiagnosticSessionId;
    private int _diagnosticPostedDepth, _diagnosticPostedHighWater, _diagnosticSessionOffset;
    private readonly record struct PostedAction(Action Work, long QueuedAt);

    internal SoeDiagnosticCollector? DiagnosticCollector => _diagnostic;

    /// <summary>
    /// Call only on this listener's owning thread (normally via Post). Resets counters/timings
    /// for all connections, including unsampled connections, and starts a new window. Gauges
    /// describe snapshot time. Session rows rotate, are anonymous, and are capped at 64.
    /// The transport timer runs resends/idle checks; it is NOT a world simulation tick.
    /// Output queue counters/timings cross threads, so adjacent fields have small boundary skew.
    /// </summary>
    public object CaptureDiagnostics(int maxSessions = 16)
    {
        if (_thread is null || Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
            throw new InvalidOperationException("CaptureDiagnostics must run on the owning SOE listener thread via Post.");
        ArgumentOutOfRangeException.ThrowIfNegative(maxSessions);
        if (_diagnostic is not { } diagnostics) return new { enabled = false };

        long now = Stopwatch.GetTimestamp();
        double windowSeconds = (now - _diagnosticWindowStart) / (double)Stopwatch.Frequency;
        _diagnosticWindowStart = now;
        int limit = Math.Min(64, maxSessions), total = _connections.Count;
        var sessions = new List<SoeConnectionDiagnosticSnapshot>(Math.Min(limit, total));
        int start = total == 0 ? 0 : _diagnosticSessionOffset % total;
        int index = 0, pending = 0, inFlight = 0, queued = 0, latestPending = 0;
        long pendingBytes = 0;
        foreach (SoeConnection connection in _connections.Values)
        {
            pending += connection.PendingDatagrams;
            pendingBytes += connection.PendingBytes;
            inFlight += connection.DiagnosticInFlight;
            queued += connection.DiagnosticQueued;
            latestPending += connection.PendingLatestMessages;
            bool include = limit > 0 && (index - start + total) % total < limit;
            SoeConnectionDiagnosticSnapshot? snapshot = connection.CaptureDiagnostics(include);
            if (snapshot is not null) sessions.Add(snapshot);
            index++;
        }
        _diagnosticSessionOffset = total == 0 ? 0 : (start + Math.Min(limit, total)) % total;
        int postedDepth = Volatile.Read(ref _diagnosticPostedDepth);
        // The peak resets to the current gauge, so backlog spanning a window remains visible.
        int postedHighWater = Interlocked.Exchange(ref _diagnosticPostedHighWater, postedDepth);
        double oldestPostedMs = _posted.TryPeek(out PostedAction oldest)
            ? Math.Max(0, (now - oldest.QueuedAt) * 1000d / Stopwatch.Frequency) : 0;
        return new
        {
            enabled = true, windowSeconds, transportTimerTargetMs = _options.TickIntervalMs,
            configuration = new { _options.MaxDatagramsPerPass, _options.MaxReceiveWorkMs,
                _options.MaxPostedActionsPerPass, _options.MaxPostedWorkMs, _options.MaxConnections,
                _options.IdleTimeoutMs, _options.Settings.UdpLength, _options.Settings.CrcLength,
                _options.Settings.Compression },
            connections = total, localPeers = _localPeers.Count,
            counters = diagnostics.TakeCounters(),
            queues = new { postedDepth, postedHighWater = Math.Max(postedDepth, postedHighWater), oldestPostedMs,
                localQueuedBytes = Interlocked.Read(ref _localQueuedBytes),
                reliablePendingDatagrams = pending, reliablePendingBytes = pendingBytes,
                reliableInFlightDatagrams = inFlight, reliableUnsentDatagrams = queued, latestPending },
            timings = new
            {
                listenerWork = diagnostics.ListenerWork.TakeSnapshot(),
                receiveDispatch = diagnostics.ReceiveDispatch.TakeSnapshot(),
                localDispatch = diagnostics.LocalDispatch.TakeSnapshot(),
                signalWork = diagnostics.SignalWork.TakeSnapshot(),
                postedWork = diagnostics.PostedWork.TakeSnapshot(),
                postedQueueWait = diagnostics.PostedQueueWait.TakeSnapshot(),
                transportTickWork = diagnostics.TransportTickWork.TakeSnapshot(),
                transportTickInterval = diagnostics.TransportTickInterval.TakeSnapshot(),
                localInputQueueWait = diagnostics.LocalInputQueueWait.TakeSnapshot(),
                localOutputQueueWait = diagnostics.LocalOutputQueueWait.TakeSnapshot(),
                latestFirstOfferToCommit = diagnostics.LatestFirstOfferToCommit.TakeSnapshot(),
                latestNewestOfferToCommit = diagnostics.LatestNewestOfferToCommit.TakeSnapshot(),
                reliableHeldWait = diagnostics.ReliableHeldWait.TakeSnapshot(),
                reliableAssemblyWait = diagnostics.ReliableAssemblyWait.TakeSnapshot(),
                reliableBufferWait = diagnostics.ReliableBufferWait.TakeSnapshot(),
                reliableFirstTransmitWait = diagnostics.ReliableFirstTransmitWait.TakeSnapshot(),
                encryptWork = diagnostics.EncryptWork.TakeSnapshot(),
                decryptWork = diagnostics.DecryptWork.TakeSnapshot(),
                applicationDispatchWork = diagnostics.ApplicationDispatchWork.TakeSnapshot(),
                udpSendWork = diagnostics.UdpSendWork.TakeSnapshot(),
                localSendEnqueueWork = diagnostics.LocalSendEnqueueWork.TakeSnapshot(),
            },
            distributions = new
            {
                receivedDatagramBytes = diagnostics.ReceivedDatagramBytes.TakeSnapshot(),
                sentDatagramBytes = diagnostics.SentDatagramBytes.TakeSnapshot(),
                receivedApplicationBytes = diagnostics.ReceivedApplicationBytes.TakeSnapshot(),
                sentApplicationBytes = diagnostics.SentApplicationBytes.TakeSnapshot(),
                bufferedMessagesPerDatagram = diagnostics.BufferedMessagesPerDatagram.TakeSnapshot(),
                fragmentsPerMessage = diagnostics.FragmentsPerMessage.TakeSnapshot(),
                udpDatagramsPerPass = diagnostics.UdpDatagramsPerPass.TakeSnapshot(),
                localDatagramsPerPass = diagnostics.LocalDatagramsPerPass.TakeSnapshot(),
            },
            sampledSessions = sessions, omittedSessions = total - sessions.Count,
        };
    }

    private void DiagnosticPostedEnqueued()
    {
        int depth = Interlocked.Increment(ref _diagnosticPostedDepth);
        int high = Volatile.Read(ref _diagnosticPostedHighWater);
        while (depth > high)
        {
            int previous = Interlocked.CompareExchange(ref _diagnosticPostedHighWater, depth, high);
            if (previous == high) break;
            high = previous;
        }
    }
}
