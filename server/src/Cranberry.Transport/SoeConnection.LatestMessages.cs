using System.Diagnostics;

namespace Cranberry.Transport;

public sealed partial class SoeConnection
{
    // Only complete replaceable state belongs here. Events, sparse deltas and encrypted
    // datagrams must retain their normal reliable order. All access stays on the listener.
    private readonly Dictionary<ulong, LatestMessage> _latestMessages = [];
    private readonly LinkedList<LatestMessage> _latestPending = [];
    private const int LatestMessageLimit = 512;
    private const int LatestMessageMaximumBytes = 256;
    private const int LatestMessageReliableLimit = 16;

    public int PendingLatestMessages => _latestPending.Count;
    public int RetainedLatestMessages => _latestMessages.Count;
    public long LatestMessagesReplaced { get; private set; }
    public long LatestMessagesCommitted { get; private set; }

    /// <summary>Retain the newest complete state for a key before consuming any RC4 bytes.
    /// At most 512 keys of 256 bytes are retained per connection. Forget a key before entity ID reuse.</summary>
    public void SendLatest(ulong key, ReadOnlySpan<byte> message, Action<ReadOnlyMemory<byte>>? committed = null)
    {
        if (State != ConnectionState.Open || message.IsEmpty) return;
        if (message.Length > LatestMessageMaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(message), "Latest-state messages are limited to 256 bytes.");
        if (!_latestMessages.TryGetValue(key, out LatestMessage? slot))
        {
            if (_latestMessages.Count >= LatestMessageLimit)
                throw new InvalidOperationException("Latest-state entity limit exceeded.");
            slot = new LatestMessage();
            _latestMessages.Add(key, slot);
        }
        if (_diagnostic is not null)
        {
            slot.NewestQueuedAt = Stopwatch.GetTimestamp();
            if (slot.Node.List is null) slot.FirstQueuedAt = slot.NewestQueuedAt;
        }
        if (slot.Node.List is not null) LatestMessagesReplaced++;
        else _latestPending.AddLast(slot.Node);
        if (slot.Buffer.Length < message.Length) slot.Buffer = new byte[message.Length];
        message.CopyTo(slot.Buffer);
        slot.Length = message.Length;
        slot.Committed = committed;
    }

    public void ForgetLatest(ulong key)
    {
        if (!_latestMessages.Remove(key, out LatestMessage? slot)) return;
        if (slot.Node.List is not null) _latestPending.Remove(slot.Node);
    }

    public void ForgetAllLatest()
    {
        _latestPending.Clear();
        _latestMessages.Clear();
    }

    /// <summary>A non-replaceable update for the same entity is an ordering barrier.</summary>
    public void FlushLatest(ulong key)
    {
        if (!_latestMessages.TryGetValue(key, out LatestMessage? slot) || slot.Node.List is null) return;
        CommitLatest(slot, Environment.TickCount64);
    }

    private void FlushLatestMessages(long now)
    {
        // In-flight datagrams are already travelling to the client. Counting them as
        // backlog stalled movement on healthy high-RTT links while waiting for ACKs.
        while (State == ConnectionState.Open && _outbound.QueuedCount < LatestMessageReliableLimit
            && _latestPending.First is { } first)
            CommitLatest(first.Value, now);
    }

    private void CommitLatest(LatestMessage slot, long now)
    {
        _latestPending.Remove(slot.Node);
        ReadOnlyMemory<byte> message = slot.Buffer.AsMemory(0, slot.Length);
        slot.Committed?.Invoke(message);
        try { _outbound.SendBuffered(message.Span, EncryptionEnabled ? _outboundCipher : null, now); }
        catch (SoeProtocolException)
        {
            _diagnostic?.Record(SoeDiagnosticCounter.ReliableBacklogClosures);
            _log.Warn($"{this}: latest-state barrier exceeded the reliable backlog; closing slow session");
            Disconnect(DisconnectReason.Application);
            return;
        }
        MessagesSent++;
        RecordDiagnosticApplicationSent(message.Length);
        LatestMessagesCommitted++;
        _bufferedReady?.Invoke(this);
        if (_diagnostic is not null)
        {
            long committedAt = Stopwatch.GetTimestamp();
            _diagnostic.LatestFirstOfferToCommit.RecordTicks(committedAt - slot.FirstQueuedAt);
            _diagnostic.LatestNewestOfferToCommit.RecordTicks(committedAt - slot.NewestQueuedAt);
        }
    }

    private sealed class LatestMessage
    {
        public byte[] Buffer = [];
        public int Length;
        public long FirstQueuedAt, NewestQueuedAt;
        public Action<ReadOnlyMemory<byte>>? Committed;
        public readonly LinkedListNode<LatestMessage> Node;
        public LatestMessage() => Node = new(this);
    }
}
