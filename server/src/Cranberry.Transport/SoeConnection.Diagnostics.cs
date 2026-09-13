namespace Cranberry.Transport;

/// <summary>Anonymous per-window detail; no endpoint, account, token, payload, or client session ID.</summary>
public sealed record SoeConnectionDiagnosticSnapshot(long DiagnosticSessionId, long ReceivedDatagrams,
    long ReceivedBytes, long SentDatagrams, long SentBytes, long ApplicationMessagesReceived,
    long UnreliableApplicationMessagesReceived, long ApplicationBytesReceived,
    long ApplicationMessagesSent, long ApplicationBytesSent, long ReliableDatagramsResent,
    long FastRetransmits, long LatestMessagesReplaced, long LatestMessagesCommitted,
    int ReliablePendingDatagrams, long ReliablePendingBytes, int ReliableInFlightDatagrams,
    int ReliableUnsentDatagrams, int PendingLatestMessages, int RetainedLatestMessages,
    double SmoothedRoundTripMs, long RoundTripSamples, int ResendTimeoutMs)
{
    public int InboundHeldDatagrams { get; init; }
    public long InboundHeldBytes { get; init; }
    public double OldestInboundHeldMs { get; init; }
    public double OldestUnsentMs { get; init; }
    public double OldestUnackedMs { get; init; }
    public int SendWindow { get; init; }
}

public sealed partial class SoeConnection
{
    private SoeDiagnosticCollector? _diagnostic;
    private long _diagnosticId, _diagnosticReceived, _diagnosticReceivedBytes, _diagnosticSent,
        _diagnosticSentBytes, _diagnosticAppReceived, _diagnosticRawReceived, _diagnosticAppReceivedBytes,
        _diagnosticAppSent, _diagnosticAppSentBytes;
    private long _diagnosticLastResent, _diagnosticLastFastResent, _diagnosticLastReplaced, _diagnosticLastCommitted;
    internal int DiagnosticInFlight => _outbound.InFlightCount;
    internal int DiagnosticQueued => _outbound.QueuedCount;

    internal void EnableDiagnostics(SoeDiagnosticCollector diagnostics, long id)
    {
        _diagnostic = diagnostics; _diagnosticId = id;
        _inbound.Diagnostics = diagnostics; _outbound.Diagnostics = diagnostics;
    }

    internal void RecordDiagnosticReceive(int bytes)
    {
        if (_diagnostic is null) return;
        _diagnosticReceived++;
        _diagnosticReceivedBytes += bytes;
    }

    internal void RecordDiagnosticSend(int bytes)
    {
        if (_diagnostic is null) return;
        _diagnosticSent++;
        _diagnosticSentBytes += bytes;
    }

    private void RecordDiagnosticApplicationReceived(int bytes, bool unreliable)
    {
        if (_diagnostic is null) return;
        _diagnosticAppReceived++;
        _diagnosticAppReceivedBytes += bytes;
        _diagnostic.Record(SoeDiagnosticCounter.ApplicationMessagesReceived);
        _diagnostic.Record(SoeDiagnosticCounter.ApplicationBytesReceived, bytes);
        _diagnostic.ReceivedApplicationBytes.Record(bytes);
        if (unreliable)
        {
            _diagnosticRawReceived++;
            _diagnostic.Record(SoeDiagnosticCounter.UnreliableApplicationMessagesReceived);
        }
    }

    private void RecordDiagnosticApplicationSent(int bytes)
    {
        if (_diagnostic is null) return;
        _diagnosticAppSent++;
        _diagnosticAppSentBytes += bytes;
        _diagnostic.Record(SoeDiagnosticCounter.ApplicationMessagesSent);
        _diagnostic.Record(SoeDiagnosticCounter.ApplicationBytesSent, bytes);
        _diagnostic.SentApplicationBytes.Record(bytes);
    }

    internal SoeConnectionDiagnosticSnapshot? CaptureDiagnostics(bool include)
    {
        if (_diagnostic is null) return null;
        long resent = DatagramsResent - _diagnosticLastResent;
        long fastResent = _outbound.FastRetransmits - _diagnosticLastFastResent;
        long replaced = LatestMessagesReplaced - _diagnosticLastReplaced;
        long committed = LatestMessagesCommitted - _diagnosticLastCommitted;
        _diagnostic.Record(SoeDiagnosticCounter.ReliableDatagramsResent, resent);
        _diagnostic.Record(SoeDiagnosticCounter.FastRetransmits, fastResent);
        _diagnostic.Record(SoeDiagnosticCounter.LatestMessagesReplaced, replaced);
        _diagnostic.Record(SoeDiagnosticCounter.LatestMessagesCommitted, committed);
        SoeConnectionDiagnosticSnapshot? snapshot = include ? new(_diagnosticId,
            _diagnosticReceived, _diagnosticReceivedBytes, _diagnosticSent, _diagnosticSentBytes,
            _diagnosticAppReceived, _diagnosticRawReceived, _diagnosticAppReceivedBytes,
            _diagnosticAppSent, _diagnosticAppSentBytes, resent, fastResent, replaced, committed,
            PendingDatagrams, PendingBytes, _outbound.InFlightCount, _outbound.QueuedCount,
            PendingLatestMessages, RetainedLatestMessages, _outbound.SmoothedRoundTripMs,
            _outbound.RoundTripSamples, _outbound.CurrentResendIntervalMs)
        {
            InboundHeldDatagrams = _inbound.HeldCount, InboundHeldBytes = _inbound.HeldBytes,
            OldestInboundHeldMs = _inbound.OldestHeldMs, OldestUnsentMs = _outbound.OldestUnsentMs,
            OldestUnackedMs = _outbound.OldestUnackedMs, SendWindow = _outbound.SendWindow
        } : null;
        _diagnosticReceived = _diagnosticReceivedBytes = _diagnosticSent = _diagnosticSentBytes = 0;
        _diagnosticAppReceived = _diagnosticRawReceived = _diagnosticAppReceivedBytes = 0;
        _diagnosticAppSent = _diagnosticAppSentBytes = 0;
        _diagnosticLastResent = DatagramsResent;
        _diagnosticLastFastResent = _outbound.FastRetransmits;
        _diagnosticLastReplaced = LatestMessagesReplaced;
        _diagnosticLastCommitted = LatestMessagesCommitted;
        return snapshot;
    }
}
