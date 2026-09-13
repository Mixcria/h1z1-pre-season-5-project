using System.Buffers;
using System.Diagnostics;

namespace Cranberry.Transport;

/// <summary>
/// The sending half of the reliable channel: numbers datagrams, cuts long messages into
/// fragments, keeps every datagram until the peer acknowledges it, and resends on a timer.
/// Encryption happens before fragmentation, so a resend is the cached bytes verbatim — the
/// keystream has moved on and must never be re-applied.
/// </summary>
/// <remarks>
/// <para>
/// <b>Flow control (2026-09-03, docs/108).</b> This channel used to hand every datagram straight
/// to the socket the moment <see cref="Send"/> cut it, with no window and no backpressure — the
/// shape <c>ZoneService.ArmGroundLoot</c> already had to work around by slicing its own burst. The
/// zoning burst cannot be sliced that way: its 1.27 MB <c>DynamicAppearanceDefinitions</c> is ONE
/// reliable message, ~2,500 datagrams emitted back to back, and the August client is rebuilding
/// its actor and loading Z2 at exactly that moment. Its socket buffer overran, the resends came
/// off the front of the queue, the head datagram exhausted 25 × 300 ms of patience and the server
/// closed a session the client was still alive inside — the client's own "G10".
/// </para>
/// <para>
/// Two things changed. (1) <see cref="SendWindow"/> datagrams may be in flight; the rest wait in
/// the same FIFO and go out as acknowledgements free the window, so the peer's receive buffer is
/// never handed more than it can hold and the ordinary loss/resend path is never entered at all.
/// (2) A peer that goes quiet is given <see cref="PeerSilenceLimitMs"/> of real time rather than
/// 25 fixed 300 ms rounds, and each resend backs off towards
/// <see cref="MaxResendIntervalMs"/> — a client stalled inside a synchronous world load is busy,
/// not gone, and flooding it with the same 2,000 datagrams every 300 ms was making the stall
/// worse.
/// </para>
/// </remarks>
internal sealed class OutboundChannel
{
    private bool _closed;
    private long _queueRevision;
    private readonly SessionSettings _settings;
    private readonly Action<ReadOnlyMemory<byte>> _transmit;
    private readonly Queue<Pending> _pending = new();
    private ushort _nextSequence;
    private int _inFlight;
    private long _resendBudgetAt = long.MinValue;
    private int _resendBudget;
    private byte[]? _bundle;
    private int _bundleLength;
    private int _bundleMessages;
    private readonly RoundTripEstimator _rtt = new();
    internal SoeDiagnosticCollector? Diagnostics { get; set; }
    private long _bundleQueuedAt;
    public double OldestUnsentMs => _pending.FirstOrDefault(p => !p.Sent) is { } p
        ? Math.Max(0, Environment.TickCount64 - p.QueuedAt) : 0;
    public double OldestUnackedMs => _pending.TryPeek(out var p) && p.Sent
        ? Math.Max(0, Environment.TickCount64 - p.FirstSent) : 0;

    // Stay below half the 16-bit sequence space and bound a stalled peer's memory.
    public int MaxPendingDatagrams { get; init; } = 16_384;
    public int MaxPendingBytes { get; init; } = 8 * 1024 * 1024;
    public long PendingBytes { get; private set; }

    public bool CanQueue(int messageLength, bool encrypted)
    {
        long length = (long)messageLength + (encrypted ? 1 : 0);
        int room = _settings.MaxReliablePayload;
        long packets = length <= room ? 1 : (length + 4 + room - 1) / room;
        long bytes = length + (packets > 1 ? 4 : 0)
            + packets * (_settings.ReliableHeaderLength + _settings.CrcLength);
        long bufferedBytes = _bundleLength == 0 ? 0 : _bundleLength
            + _settings.ReliableHeaderLength + _settings.CrcLength;
        return messageLength >= 0 && _pending.Count + packets + (_bundleLength == 0 ? 0 : 1)
                <= Math.Min(32_767, MaxPendingDatagrams)
            && PendingBytes + bufferedBytes + bytes <= MaxPendingBytes;
    }

    public OutboundChannel(SessionSettings settings, Action<ReadOnlyMemory<byte>> transmit)
    {
        _settings = settings;
        _transmit = transmit;
    }

    /// <summary>How long a datagram waits for an Ack before it is sent again.</summary>
    public int ResendIntervalMs { get; init; } = 300;
    public int MinResendIntervalMs { get; init; } = 100;
    public int CurrentResendIntervalMs => _rtt.TimeoutMs(ResendIntervalMs, MinResendIntervalMs, MaxResendIntervalMs);
    public long RoundTripSamples => _rtt.Samples;
    public double SmoothedRoundTripMs => _rtt.SmoothedMs;
    public long FastRetransmits { get; private set; }

    /// <summary>
    /// The ceiling the resend interval doubles towards while a peer stays quiet, so a busy client
    /// is not flooded with its whole backlog every 300 ms.
    /// </summary>
    public int MaxResendIntervalMs { get; init; } = 2400;

    /// <summary>Resends after which one datagram stops being sent again (the flood guard).</summary>
    public int MaxResends { get; init; } = 25;

    /// <summary>Datagrams resent per tick at most, so a stalled peer does not get a flood.</summary>
    public int MaxResendsPerTick { get; init; } = 32;

    /// <summary>
    /// Datagrams allowed on the wire unacknowledged. Keep bursts small across a crowded match:
    /// 32 × 512 B per connection, replenished by cumulative ACKs. Queued bytes retain their order.
    /// </summary>
    public int SendWindow { get; init; } = 32;

    /// <summary>
    /// How long the oldest unacknowledged datagram may go unacknowledged before the peer is
    /// declared gone. The August client stops servicing its socket for the whole of a synchronous
    /// world load; 45 s is longer than any load this project has measured and still an order of
    /// magnitude under a human's patience.
    /// </summary>
    public int PeerSilenceLimitMs { get; init; } = 45_000;

    public int PendingCount => _pending.Count;

    /// <summary>Datagrams sent once and not yet acknowledged.</summary>
    public int InFlightCount => _inFlight;

    /// <summary>Datagrams cut but still waiting for room in the window.</summary>
    public int QueuedCount => _pending.Count - _inFlight;

    public ushort NextSequence => _nextSequence;
    public long DatagramsSent { get; private set; }
    public long DatagramsResent { get; private set; }

    /// <summary>True once the peer has been silent for <see cref="PeerSilenceLimitMs"/>.</summary>
    public bool PeerLost { get; private set; }

    public void Send(ReadOnlySpan<byte> message, Rc4Cipher? cipher, long now)
    {
        if (_closed) return;
        FlushBuffered(now);
        if (message.IsEmpty)
        {
            return;
        }

        // Reject the whole message BEFORE consuming RC4 or allocating any fragments.
        if (!CanQueue(message.Length, cipher is not null))
            throw new SoeProtocolException("Reliable outbound backlog limit exceeded.");

        byte[] scratch = ArrayPool<byte>.Shared.Rent(
            checked(message.Length + (cipher is null ? 0 : 1)));
        try
        {
            Span<byte> body = scratch.AsSpan(0, message.Length);
            message.CopyTo(body);
            if (cipher is not null)
            {
                long encryptStart = Diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                cipher.Transform(body);
                if (Diagnostics is not null) Diagnostics.EncryptWork.RecordTicks(Stopwatch.GetTimestamp() - encryptStart);
                if (body[0] == 0)
                {
                    // The receiver reserves 00 19 for a clear bundle envelope. Prefix one
                    // unencrypted zero so leading-zero ciphertext is carried as 00 00 instead.
                    body.CopyTo(scratch.AsSpan(1, body.Length));
                    scratch[0] = 0;
                    body = scratch.AsSpan(0, message.Length + 1);
                }
            }

            int room = _settings.MaxReliablePayload;
            if (body.Length <= room)
            {
                Emit(SoeOpcode.Data, body, null, now);
                return;
            }

            int firstChunk = room - 4;
            Diagnostics?.FragmentsPerMessage.Record(((long)body.Length + 4 + room - 1) / room);
            Emit(SoeOpcode.DataFragment, body.Slice(0, firstChunk), (uint)body.Length, now);
            for (int offset = firstChunk; offset < body.Length; offset += room)
            {
                int take = Math.Min(room, body.Length - offset);
                Emit(SoeOpcode.DataFragment, body.Slice(offset, take), null, now);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>Pack small messages into an MTU-sized reliable bundle, flushed by the owner.
    /// A normal send flushes first, preserving application and RC4 order.</summary>
    public void SendBuffered(ReadOnlySpan<byte> message, Rc4Cipher? cipher, long now)
    {
        if (_closed || message.IsEmpty) return;
        int maximum = checked(message.Length + (cipher is null ? 0 : 1));
        int room = _settings.MaxReliablePayload;
        int needed = SoeVarInt.EncodedSize(maximum) + maximum;
        if (needed + 2 > room)
        {
            Send(message, cipher, now);
            return;
        }
        if (_bundleLength + needed > room) FlushBuffered(now);
        // This extends the current bundle, rather than queuing a second copy of it.
        // Reserve the worst-case escaped size before consuming the continuing cipher.
        int bufferedBytes = Math.Max(2, _bundleLength) + needed
            + _settings.ReliableHeaderLength + _settings.CrcLength;
        if (_pending.Count + 1 > Math.Min(32_767, MaxPendingDatagrams)
            || PendingBytes + bufferedBytes > MaxPendingBytes)
            throw new SoeProtocolException("Reliable outbound backlog limit exceeded.");
        _bundle ??= new byte[room];
        if (_bundleLength == 0)
        {
            _bundleQueuedAt = Diagnostics is null ? 0 : Stopwatch.GetTimestamp();
            _bundle[0] = 0;
            _bundle[1] = (byte)SoeOpcode.Bundle;
            _bundleLength = 2;
        }
        // Encrypt into scratch so the actual escaped length determines the prefix width.
        Span<byte> scratch = stackalloc byte[room];
        message.CopyTo(scratch);
        int length = message.Length;
        if (cipher is not null)
        {
            long encryptStart = Diagnostics is null ? 0 : Stopwatch.GetTimestamp();
            cipher.Transform(scratch[..length]);
            if (Diagnostics is not null) Diagnostics.EncryptWork.RecordTicks(Stopwatch.GetTimestamp() - encryptStart);
            if (scratch[0] == 0)
            {
                scratch[..length].CopyTo(scratch[1..]);
                scratch[0] = 0;
                length++;
            }
        }
        _bundleLength += SoeVarInt.Write(_bundle.AsSpan(_bundleLength), length);
        scratch[..length].CopyTo(_bundle.AsSpan(_bundleLength));
        _bundleLength += length;
        _bundleMessages++;
    }

    public void FlushBuffered(long now)
    {
        if (_bundleLength == 0) return;
        if (_bundleQueuedAt != 0) Diagnostics?.ReliableBufferWait.RecordTicks(Stopwatch.GetTimestamp() - _bundleQueuedAt);
        _bundleQueuedAt = 0;
        Diagnostics?.BufferedMessagesPerDatagram.Record(_bundleMessages);
        _bundleMessages = 0;
        Emit(SoeOpcode.Data, _bundle.AsSpan(0, _bundleLength), null, now);
        _bundleLength = 0;
    }

    private void Emit(SoeOpcode opcode, ReadOnlySpan<byte> chunk, uint? totalLength, long now)
    {
        if (_closed) return;
        int length = _settings.ReliableHeaderLength + (totalLength.HasValue ? 4 : 0) + chunk.Length + _settings.CrcLength;
        byte[] datagram = ArrayPool<byte>.Shared.Rent(length);
        var writer = new SpanWriter(datagram.AsSpan(0, length));
        writer.WriteUInt16((ushort)opcode);
        if (_settings.Compression != 0)
        {
            writer.WriteByte(0);
        }

        writer.WriteUInt16(_nextSequence);
        if (totalLength.HasValue)
        {
            writer.WriteUInt32(totalLength.Value);
        }

        writer.WriteBytes(chunk);

        var pending = new Pending(_nextSequence, datagram, length, now, 0);
        pending.DiagnosticQueuedAt = Diagnostics is null ? 0 : Stopwatch.GetTimestamp();
        _pending.Enqueue(pending);
        _queueRevision++;
        PendingBytes += length;
        _nextSequence++;

        // Ordering is the queue's, not the socket's: nothing behind a datagram still waiting for
        // window room may overtake it, so the pump only ever transmits from the front.
        if (QueuedCount == 1 && _inFlight < SendWindow)
        {
            TransmitFirst(pending, now);
        }
    }

    /// <summary>Hands as many queued datagrams to the socket as the window has room for.</summary>
    private void Pump(long now)
    {
        long revision = _queueRevision;
        foreach (Pending p in _pending)
        {
            if (p.Sent)
            {
                continue;
            }

            if (_inFlight >= SendWindow)
            {
                return;
            }

            TransmitFirst(p, now);
            if (_queueRevision != revision) return;
        }
    }

    private void TransmitFirst(Pending p, long now)
    {
        if (p.DiagnosticQueuedAt != 0) Diagnostics?.ReliableFirstTransmitWait.RecordTicks(Stopwatch.GetTimestamp() - p.DiagnosticQueuedAt);
        p.Sent = true;
        p.FirstSent = now;
        p.LastSent = now;
        _inFlight++;
        DatagramsSent++;
        _transmit(p.Datagram.AsMemory(0, p.Length));
    }

    /// <summary>Cumulative acknowledgement: everything up to and including <paramref name="sequence"/> is done.</summary>
    public void Acknowledge(ushort sequence) => Acknowledge(sequence, Environment.TickCount64);

    /// <summary>Cumulative acknowledgement, with the clock the caller is already holding.</summary>
    public void Acknowledge(ushort sequence, long now)
    {
        Diagnostics?.Record(SoeDiagnosticCounter.AcksReceived);
        // An old or future cumulative Ack is not evidence that queued data arrived. Locate the
        // exact sequence in the contiguous pending run before releasing anything. A datagram still
        // waiting for window room has never been on the wire and cannot have been seen.
        int release = 0;
        bool found = false;
        bool ambiguous = false;
        long sampleSent = 0;
        foreach (Pending pending in _pending)
        {
            if (!pending.Sent)
            {
                break;
            }

            release++;
            ambiguous |= pending.Resends != 0;
            sampleSent = pending.FirstSent;
            if (pending.Sequence == sequence)
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            Diagnostics?.Record(SoeDiagnosticCounter.AcksIgnored);
            return;
        }
        Diagnostics?.Record(SoeDiagnosticCounter.AcksAdvanced);
        Diagnostics?.Record(SoeDiagnosticCounter.AckedDatagrams, release);

        // One sample per advancing ACK. A cumulative ACK spanning any retransmission is
        // ambiguous: the newest original packet might have waited behind the repaired gap.
        if (!ambiguous)
        {
            long samples = _rtt.Samples;
            _rtt.Observe(now - sampleSent);
            Diagnostics?.Record(SoeDiagnosticCounter.RoundTripSamplesTaken, _rtt.Samples - samples);
        }
        else Diagnostics?.Record(SoeDiagnosticCounter.RoundTripSamplesExcluded);

        while (release-- > 0)
        {
            Pending acknowledged = _pending.Dequeue();
            _queueRevision++;
            if (acknowledged.Sent)
            {
                _inFlight--;
            }

            ArrayPool<byte>.Shared.Return(acknowledged.Datagram);
            PendingBytes -= acknowledged.Length;
        }

        // The window just opened: the acknowledgement is the clock this channel sends on.
        Pump(now);
    }

    /// <summary>The peer saw <paramref name="sequence"/> before something earlier; resend what precedes it now.</summary>
    public void ResendBefore(ushort sequence, long now)
    {
        if (_inFlight == 0 || (ushort)(sequence - _pending.Peek().Sequence) >= _inFlight)
            return; // A future/stale signal cannot request a retransmission.
        Pending? received = null;
        foreach (Pending p in _pending)
        {
            if (!p.Sent) break;
            if (p.Sequence == sequence) { received = p; break; }
        }
        if (received is null || received.ReportedReceived) return;
        // Advisory receipt, NOT an ACK: retain the encrypted bytes and sequence until a
        // cumulative ACK releases them. This avoids resending the whole window for one loss.
        received.ReportedReceived = true;
        RefreshResendBudget(now);
        long revision = _queueRevision;
        foreach (Pending p in _pending)
        {
            if (!p.Sent || p == received)
            {
                break;
            }
            if (p.ReportedReceived) continue;
            if (p.GapReports == 0) p.FirstGapReportAt = now;
            p.GapReports = Math.Min(3, p.GapReports + 1);
            TryFastResend(p, now);
            if (_queueRevision != revision) return;
        }
    }

    private bool TryFastResend(Pending p, long now)
    {
        // Three distinct later receipts and a reorder grace period. Repeated copies of one
        // report add no evidence and cannot reset backoff or multiply the shared send budget.
        // Start the grace period when the gap is reported. Starting it at the
        // original send made it expire during a normal WAN round trip, triggering
        // needless repairs of a window that was only briefly out of order.
        int grace = (int)Math.Clamp(2 * _rtt.VariationMs, 25, 100);
        if (p.GapReports < 3 || p.ReportedReceived || p.Resends != 0
            || _resendBudget <= 0 || now - p.FirstGapReportAt < grace) return false;
        _resendBudget--;
        FastRetransmits++;
        Resend(p, now);
        return true;
    }

    public void Tick(long now)
    {
        if (_closed) return;
        FlushBuffered(now);
        RefreshResendBudget(now);
        long revision = _queueRevision;
        foreach (Pending p in _pending)
        {
            if (!p.Sent)
            {
                break;
            }

            if (now - p.FirstSent >= PeerSilenceLimitMs)
            {
                PeerLost = true;
                return;
            }

            // A reported packet remains reliable but need not be resent. Probe the oldest
            // anyway: if the cumulative ACK was lost this solicits a fresh ACK and progress.
            if (p.ReportedReceived && p != _pending.Peek()) continue;
            if (TryFastResend(p, now))
            {
                if (_queueRevision != revision) return;
                continue;
            }
            if (p.Resends >= MaxResends || now - p.LastSent < ResendInterval(p.Resends))
            {
                continue;
            }

            if (_resendBudget <= 0) break;
            Resend(p, now);
            if (_queueRevision != revision) return;
            if (--_resendBudget == 0)
            {
                break;
            }
        }

        Pump(now);
    }

    private void RefreshResendBudget(long now)
    {
        if (_resendBudgetAt == long.MinValue || now - _resendBudgetAt >= 20)
        {
            _resendBudgetAt = now;
            _resendBudget = MaxResendsPerTick;
        }
    }

    /// <summary>Measured retry timeout doubling towards <see cref="MaxResendIntervalMs"/>.</summary>
    private int ResendInterval(int resends)
    {
        long interval = CurrentResendIntervalMs;
        for (int i = 0; i < resends && interval < MaxResendIntervalMs; i++)
        {
            interval *= 2;
        }

        return (int)Math.Min(interval, MaxResendIntervalMs);
    }

    private void Resend(Pending p, long now)
    {
        p.LastSent = now;
        p.Resends++;
        p.GapReports = 0;
        DatagramsResent++;
        _transmit(p.Datagram.AsMemory(0, p.Length));
    }

    public void Close()
    {
        // The transmit callback may synchronously close a blocked TLS peer. Invalidate
        // active resend/window walks before releasing their pooled datagrams.
        _closed = true;
        _queueRevision++;
        while (_pending.TryDequeue(out Pending? p))
        {
            ArrayPool<byte>.Shared.Return(p.Datagram);
        }

        _inFlight = 0;
        PendingBytes = 0;
        _bundleLength = 0;
        _bundleMessages = 0;
        _bundleQueuedAt = 0;
    }

    private sealed class Pending(ushort sequence, byte[] datagram, int length, long lastSent, int resends)
    {
        public ushort Sequence { get; } = sequence;
        public byte[] Datagram { get; } = datagram;
        public int Length { get; } = length;
        public long LastSent { get; set; } = lastSent;
        public long QueuedAt { get; } = lastSent;
        public long DiagnosticQueuedAt { get; set; }
        public int Resends { get; set; } = resends;
        public bool ReportedReceived { get; set; }
        public int GapReports { get; set; }
        public long FirstGapReportAt { get; set; }

        /// <summary>False while the datagram is cut but still waiting for window room.</summary>
        public bool Sent { get; set; }

        /// <summary>When the datagram first reached the socket; the peer-silence clock starts here.</summary>
        public long FirstSent { get; set; }
    }
}
