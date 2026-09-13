using System.Net;

namespace Cranberry.Transport;

public enum ConnectionState
{
    Open,
    Closing,
    Closed,
}

/// <summary>
/// One client session: the settings we dictated, the cipher for each direction, and the two
/// halves of the reliable channel. Every method runs on the listener thread.
/// </summary>
public sealed partial class SoeConnection
{
    private readonly ISoeService _service;
    private readonly ITransportLog _log;
    private readonly Action<SoeConnection, ReadOnlyMemory<byte>> _transmit;
    private readonly InboundChannel _inbound;
    private readonly OutboundChannel _outbound;
    private readonly Action<SoeConnection>? _bufferedReady;
    private Rc4Cipher? _inboundCipher;
    private Rc4Cipher? _outboundCipher;
    private readonly byte[] _control = new byte[64];
    private int _multiDepth;

    internal SoeConnection(
        IPEndPoint remote,
        in SessionRequest request,
        SessionSettings settings,
        SessionDecision decision,
        ISoeService service,
        ITransportLog log,
        Action<SoeConnection, ReadOnlyMemory<byte>> transmit,
        long now,
        int sendWindow = 32,
        Action<SoeConnection>? bufferedReady = null)
    {
        RemoteEndPoint = remote;
        SendAddress = remote.Serialize();
        SessionId = request.SessionId;
        ProtocolName = request.ProtocolName;
        ClientUdpLength = request.UdpLength;
        Settings = settings;
        CreatedAt = DateTimeOffset.Now;
        LastActivity = now;
        _service = service;
        _log = log;
        _transmit = transmit;
        _bufferedReady = bufferedReady;

        if (decision.Key is not null)
        {
            _inboundCipher = new Rc4Cipher(decision.Key);
            _outboundCipher = new Rc4Cipher(decision.Key);
            EncryptionEnabled = decision.EncryptFromStart;
        }

        _inbound = new InboundChannel(Deliver);
        _outbound = new OutboundChannel(settings, datagram => _transmit(this, datagram)) { SendWindow = sendWindow };
    }

    public IPEndPoint RemoteEndPoint { get; }
    internal SocketAddress SendAddress { get; }

    /// <summary>Chosen by the client and echoed back; never generated here.</summary>
    public uint SessionId { get; }

    public string ProtocolName { get; }

    /// <summary>The largest datagram the client said it would send.</summary>
    public uint ClientUdpLength { get; }

    public SessionSettings Settings { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Monotonic milliseconds at the last datagram of any kind from this endpoint.</summary>
    public long LastActivity { get; private set; }

    public ConnectionState State { get; private set; } = ConnectionState.Open;

    public bool EncryptionEnabled { get; private set; }

    /// <summary>Free slot for the service's per-connection state.</summary>
    public object? Tag { get; set; }

    public long MessagesReceived { get; private set; }
    public long MessagesSent { get; private set; }
    public long DatagramsReceived { get; private set; }
    public int PendingDatagrams => _outbound.PendingCount;
    public long PendingBytes => _outbound.PendingBytes;
    public long DatagramsResent => _outbound.DatagramsResent;

    /// <summary>
    /// Turns encryption on for both directions from the next message. One-way: nothing in the
    /// protocol turns it off again. The keystreams start at position zero because nothing
    /// consumed them while the link was in the clear.
    /// </summary>
    public void EnableEncryption()
    {
        if (State != ConnectionState.Open)
        {
            throw new InvalidOperationException("Cannot enable encryption on a closed session.");
        }

        if (_inboundCipher is null)
        {
            throw new InvalidOperationException("This session was accepted without a key.");
        }

        EncryptionEnabled = true;
    }

    /// <summary>
    /// Installs a key learned by the application protocol and enables encryption for both
    /// directions. Use this after sending the final clear reply on a clear-start session. The
    /// ciphers begin at position zero; re-keying an enabled or pre-provisioned session is refused
    /// because it would silently desynchronise the peer.
    /// </summary>
    public void EnableEncryption(ReadOnlySpan<byte> key)
    {
        if (State != ConnectionState.Open)
        {
            throw new InvalidOperationException("Cannot enable encryption on a closed session.");
        }

        if (EncryptionEnabled || _inboundCipher is not null || _outboundCipher is not null)
        {
            throw new InvalidOperationException(
                "This session already has an encryption key; use the parameterless switch for a pre-provisioned key.");
        }

        // Construct both before changing connection state, so an invalid key leaves the clear
        // session untouched and a caller may correct it.
        var inbound = new Rc4Cipher(key);
        var outbound = new Rc4Cipher(key);
        _inboundCipher = inbound;
        _outboundCipher = outbound;
        EncryptionEnabled = true;
    }

    /// <summary>Queues one application message on the reliable channel.</summary>
    public void Send(ReadOnlySpan<byte> message)
    {
        if (State != ConnectionState.Open)
        {
            return;
        }

        if (!_outbound.CanQueue(message.Length, EncryptionEnabled))
        {
            _diagnostic?.Record(SoeDiagnosticCounter.ReliableBacklogClosures);
            _log.Warn($"{this}: reliable send backlog exceeded ({PendingDatagrams} packets, {PendingBytes} bytes); closing slow session");
            Disconnect(DisconnectReason.Application);
            return;
        }
        _outbound.Send(message, EncryptionEnabled ? _outboundCipher : null, Environment.TickCount64);
        MessagesSent++;
        RecordDiagnosticApplicationSent(message.Length);
    }

    /// <summary>Reliable small-message batching, flushed after the listener's bounded work pass.</summary>
    public void SendBuffered(ReadOnlySpan<byte> message)
    {
        if (State != ConnectionState.Open) return;
        try
        {
            _outbound.SendBuffered(message, EncryptionEnabled ? _outboundCipher : null, Environment.TickCount64);
            MessagesSent++;
            RecordDiagnosticApplicationSent(message.Length);
            _bufferedReady?.Invoke(this);
        }
        catch (SoeProtocolException)
        {
            _diagnostic?.Record(SoeDiagnosticCounter.ReliableBacklogClosures);
            _log.Warn($"{this}: buffered send backlog exceeded; closing slow session");
            Disconnect(DisconnectReason.Application);
        }
    }

    internal void FlushBufferedOutput(long now)
    {
        if (State == ConnectionState.Open) _outbound.FlushBuffered(now);
    }

    /// <summary>Tells the client the session is over and closes it locally.</summary>
    public void Disconnect(ushort reason = DisconnectReason.Application)
    {
        if (State == ConnectionState.Closed)
        {
            return;
        }

        int n = new DisconnectPacket(SessionId, reason).Write(_control);
        _transmit(this, _control.AsMemory(0, n));
        Close(DisconnectCause.ServerRequested);
    }

    internal DisconnectCause? PendingClose { get; private set; }

    internal void Close(DisconnectCause cause)
    {
        if (State == ConnectionState.Closed)
        {
            return;
        }

        State = ConnectionState.Closed;
        PendingClose = cause;
        _inbound.Close();
        _outbound.Close();
        _latestMessages.Clear();
        _latestPending.Clear();
    }

    internal void Touch(long now) => LastActivity = now;

    internal void HandleDatagram(Span<byte> datagram, long now, bool flushSignals = true)
    {
        if (State != ConnectionState.Open) return;
        DatagramsReceived++;
        HandlePacket(datagram, isSubPacket: false, now);
        if (flushSignals) FlushSignals();
    }

    private void HandlePacket(Span<byte> packet, bool isSubPacket, long now)
    {
        if (packet.Length < 2)
        {
            throw new SoeProtocolException("Datagram shorter than an opcode.");
        }

        // SOE control opcodes are 00 xx. A datagram whose first byte is non-zero is a raw
        // application message sent on the unreliable path: the August client's CryptoBaseApi::Send
        // (FUN_1420d4bd0) neither encrypts nor sequences it. The gateway tunnel byte0 = (channel<<5)|6
        // is the first byte (e.g. 06 8C = the 5-second Synchronization request), so hand it to the
        // service as-is.
        // Multi may aggregate this raw form as one of its length-prefixed sub-packets. It is still
        // an application message there; only 00 xx is an SOE control opcode at either level.
        if (packet[0] != 0)
        {
            RecordDiagnosticApplicationReceived(packet.Length, unreliable: true);
            DispatchApplication(packet);
            return;
        }

        var opcode = (SoeOpcode)((packet[0] << 8) | packet[1]);
        Span<byte> body = packet.Slice(2);

        switch (opcode)
        {
            case SoeOpcode.Multi:
                HandleMulti(body, now);
                break;

            case SoeOpcode.Data:
            case SoeOpcode.DataFragment:
                HandleReliable(body, opcode == SoeOpcode.DataFragment, isSubPacket);
                break;

            case SoeOpcode.Ack:
                _outbound.Acknowledge(ReadSequence(body), now);
                break;

            case SoeOpcode.OutOfOrder:
                _outbound.ResendBefore(ReadSequence(body), now);
                break;

            case SoeOpcode.Ping:
                _control[0] = 0;
                _control[1] = (byte)SoeOpcode.Ping;
                _transmit(this, _control.AsMemory(0, 2));
                break;

            case SoeOpcode.Disconnect:
                var disconnect = DisconnectPacket.Parse(body);
                _log.Info($"{RemoteEndPoint} session {SessionId:x8} disconnect, reason {disconnect.Reason}");
                Close(DisconnectCause.PeerRequested);
                break;

            case SoeOpcode.NetStatusRequest:
                HandleNetStatus(body);
                break;

            case SoeOpcode.SessionRequest:
                // Handled by the listener (a repeat means the client restarted its session).
                break;

            default:
                _diagnostic?.Record(SoeDiagnosticCounter.UnknownOpcodes);
                _log.Warn($"{RemoteEndPoint} unhandled transport opcode 0x{(ushort)opcode:x4} ({packet.Length} bytes)");
                break;
        }
    }

    private void HandleMulti(Span<byte> body, long now)
    {
        if (_multiDepth >= 8) throw new SoeProtocolException("Multi-packet nesting limit exceeded.");
        _multiDepth++;
        try
        {
            int offset = 0;
            // Application dispatch can retire the session while this outer packet is being
            // walked. Remaining children must not perform actions on that retired session.
            while (offset < body.Length && State == ConnectionState.Open)
            {
                int length = SoeVarInt.Read(body, ref offset);
                if (length < 2 || length > body.Length - offset)
                {
                    throw new SoeProtocolException($"Sub-packet of {length} byte(s) at {offset} does not fit.");
                }

                HandlePacket(body.Slice(offset, length), isSubPacket: true, now);
                offset += length;
            }
        }
        finally { _multiDepth--; }
    }

    private void HandleReliable(Span<byte> body, bool isFragment, bool isSubPacket)
    {
        var reader = new SpanReader(body);
        if (Settings.Compression != 0 && !isSubPacket)
        {
            _ = reader.ReadByte();
        }

        ushort sequence = reader.ReadUInt16();
        int payloadStart = 2 + reader.Position;
        int payloadEnd = body.Length + 2 - (isSubPacket ? 0 : Settings.CrcLength);
        if (payloadEnd < payloadStart)
        {
            throw new SoeProtocolException("Reliable datagram shorter than its header.");
        }

        // body starts 2 bytes into the packet; rebase the slice onto body.
        Span<byte> payload = body.Slice(payloadStart - 2, payloadEnd - payloadStart);
        _inbound.Accept(sequence, payload, isFragment);
    }

    private void HandleNetStatus(Span<byte> body)
    {
        // Layout confirmed only as far as the leading client tick count; we answer with our tick
        // and zeroed counters, which the client accepts as "server alive".
        var reader = new SpanReader(body);
        ushort clientTick = reader.ReadUInt16();

        // Opcode + echoed client tick + server tick + four counters + trailing status.
        Span<byte> reply = stackalloc byte[42];
        var writer = new SpanWriter(reply);
        writer.WriteUInt16((ushort)SoeOpcode.NetStatusReply);
        writer.WriteUInt16(clientTick);
        writer.WriteUInt32((uint)Environment.TickCount64);
        writer.WriteUInt64(0);
        writer.WriteUInt64(0);
        writer.WriteUInt64(0);
        writer.WriteUInt64(0);
        writer.WriteUInt16(0);
        reply.Slice(0, writer.Written).CopyTo(_control);
        _transmit(this, _control.AsMemory(0, writer.Written));
    }

    private static ushort ReadSequence(ReadOnlySpan<byte> body)
    {
        var reader = new SpanReader(body);
        return reader.ReadUInt16();
    }

    /// <summary>Diagnostic: raw reliable payload (ciphertext when encryption is on) and the inbound
    /// keystream position at which it was decrypted. Null unless a diagnostic sets it.</summary>
    public RawInboundHandler? RawInboundSink { get; set; }

    /// <summary>
    /// One complete reliable payload. Either a single application message, or a bundle: the
    /// marker <see cref="SoeOpcode.Bundle"/> followed by length-prefixed chunks. The marker and
    /// the length prefixes travel in the clear; only the chunk bodies are ciphertext, and the
    /// keystream runs on from one body to the next (observed 2026-08-27: the August client's
    /// ServerListRequest + CharacterSelectInfoRequest arrive as one such bundle).
    /// </summary>
    private void Deliver(byte[] buffer, int length)
    {
        Span<byte> payload = buffer.AsSpan(0, length);
        if (!IsBundle(payload))
        {
            DeliverOne(payload);
            return;
        }

        ValidateBundle(payload);

        int offset = 2;
        while (offset < payload.Length && State == ConnectionState.Open)
        {
            int chunk = SoeVarInt.Read(payload, ref offset);
            DeliverOne(payload.Slice(offset, chunk));
            offset += chunk;
        }
    }

    /// <summary>
    /// Checks the complete clear envelope before any child consumes RC4 or reaches the service.
    /// A malformed later child must not leave the otherwise continuous keystream half-advanced.
    /// </summary>
    private static void ValidateBundle(ReadOnlySpan<byte> payload)
    {
        int offset = 2;
        while (offset < payload.Length)
        {
            int chunk = SoeVarInt.Read(payload, ref offset);
            if (chunk < 1 || chunk > payload.Length - offset)
            {
                throw new SoeProtocolException(
                    $"Bundled message of {chunk} byte(s) at {offset} does not fit in {payload.Length}.");
            }

            offset += chunk;
        }
    }

    private static bool IsBundle(ReadOnlySpan<byte> payload) =>
        payload.Length >= 2 && payload[0] == 0 && payload[1] == (byte)SoeOpcode.Bundle;

    private void DeliverOne(Span<byte> message)
    {
        if (EncryptionEnabled)
        {
            // A ciphertext beginning with zero is escaped by one clear zero. That produces
            // 00 00, distinct from the 00 19 bundle marker; the pad does not consume RC4.
            if (message.Length > 1 && message[0] == 0 && message[1] == 0)
            {
                message = message.Slice(1);
            }

            RawInboundSink?.Invoke(this, message, _inboundCipher!.Position);
            long decryptStart = _diagnostic is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            _inboundCipher!.Transform(message);
            if (_diagnostic is not null) _diagnostic.DecryptWork.RecordTicks(System.Diagnostics.Stopwatch.GetTimestamp() - decryptStart);
        }

        if (message.IsEmpty)
        {
            return;
        }

        MessagesReceived++;
        RecordDiagnosticApplicationReceived(message.Length, unreliable: false);
        DispatchApplication(message);
    }

    private void DispatchApplication(Span<byte> message)
    {
        long started = _diagnostic is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        try { _service.OnMessage(this, message); }
        finally
        {
            if (_diagnostic is not null)
                _diagnostic.ApplicationDispatchWork.RecordTicks(System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }
    }

    internal bool HasReceiveSignals => _inbound.AckPending || _inbound.OutOfOrderPending is not null;

    internal void FlushSignals()
    {
        if (State != ConnectionState.Open) return;
        if (_inbound.OutOfOrderPending is ushort ahead)
        {
            int n = SequencePacket.Write(_control, SoeOpcode.OutOfOrder, ahead);
            _transmit(this, _control.AsMemory(0, n));
        }

        if (_inbound.AckPending)
        {
            int n = SequencePacket.Write(_control, SoeOpcode.Ack, _inbound.LastInOrder);
            _transmit(this, _control.AsMemory(0, n));
        }

        _inbound.ClearSignals();
    }

    internal void Tick(long now)
    {
        if (State != ConnectionState.Open)
        {
            return;
        }

        FlushLatestMessages(now);
        _outbound.Tick(now);
        if (_outbound.PeerLost)
        {
            _diagnostic?.Record(SoeDiagnosticCounter.ReliableTimeoutClosures);
            _log.Warn($"{RemoteEndPoint} session {SessionId:x8}: {_outbound.PendingCount} datagram(s) never "
                + $"acknowledged ({_outbound.InFlightCount} on the wire, {_outbound.QueuedCount} still queued) — "
                + $"the peer answered nothing for {_outbound.PeerSilenceLimitMs} ms");
            Close(DisconnectCause.Timeout);
        }
    }

    public override string ToString() => $"{RemoteEndPoint} [{ProtocolName} {SessionId:x8}]";
}

/// <summary>
/// Observes a reliable payload before decryption, with the inbound keystream position. The span is
/// only valid for the duration of the call: the connection decrypts the same bytes in place next.
/// </summary>
public delegate void RawInboundHandler(SoeConnection connection, ReadOnlySpan<byte> ciphertext, long keystreamPosition);
