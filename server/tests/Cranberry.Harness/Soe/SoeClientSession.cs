using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Cranberry.Harness.Runtime;
using Cranberry.Harness.Wire;

namespace Cranberry.Harness.Soe;

/// <summary>How a link ended.</summary>
public enum LinkCloseCause
{
    /// <summary>Still open.</summary>
    None,

    /// <summary>The harness sent a Disconnect.</summary>
    LocalRequest,

    /// <summary>The server sent a Disconnect.</summary>
    ServerRequest,

    /// <summary>The server stopped acknowledging: MaxResends exhausted.</summary>
    ServerLost,

    /// <summary>A malformed datagram; <see cref="SoeClientSession.Fault"/> has the detail.</summary>
    ProtocolError,

    /// <summary>The harness disposed the link.</summary>
    Disposed,
}

/// <summary>One application message the server delivered to the harness.</summary>
public sealed record InboundMessage(TimeSpan At, byte[] Bytes, bool WasEncrypted, long KeystreamPosition);

public sealed record SoeClientOptions
{
    /// <summary>"LoginUdp_14" or "ExternalGatewayApi_3".</summary>
    public required string ProtocolName { get; init; }

    /// <summary>Short label used in the journal and failure messages.</summary>
    public required string LinkName { get; init; }

    /// <summary>What the client asks for. The August client asks for 3 and accepts whatever the reply says.</summary>
    public uint CrcLength { get; init; } = 3;

    public uint UdpLength { get; init; } = 512;

    /// <summary>Null picks a random id, as the client does.</summary>
    public uint? SessionId { get; init; }

    /// <summary>RC4 key installed at open. The login link has one; the gateway link starts clear.</summary>
    public byte[]? Key { get; init; }

    /// <summary>How often resend timers run.</summary>
    public int TickIntervalMs { get; init; } = 10;
    public int IdleTickIntervalMs { get; init; } = 10;

    /// <summary>How long <see cref="SoeClientSession.OpenAsync"/> waits for the SessionReply.</summary>
    public TimeSpan SessionReplyTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Receive buffer. The Z2 bootstrap arrives as ~2,500 datagrams in one burst.</summary>
    public int ReceiveBufferBytes { get; init; } = 16 << 20;
}

/// <summary>
/// A client-side SOE endpoint. It opens a session, negotiates CRC and UDP size, sends and receives
/// reliable and unreliable payloads, reassembles fragments, acknowledges, and arms RC4 at the
/// moment the August client arms it.
///
/// Everything below the application message is the harness's own code (see
/// <see cref="ClientSendChannel"/> for why). All mutable state lives on one pump loop; public
/// methods post work onto it, so nothing needs a lock.
/// </summary>
public sealed class SoeClientSession : IAsyncDisposable
{
    private readonly IPEndPoint _remote;
    private readonly SoeClientOptions _options;
    private readonly HarnessClock _clock;
    private readonly PacketJournal _journal;
    private readonly Func<byte[], string> _nameOf;
    private readonly Socket _socket;
    private readonly Channel<byte[]> _datagrams = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Channel<InboundMessage> _inbox = Channel.CreateUnbounded<InboundMessage>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite,
    });
    private readonly Queue<Action> _work = new();
    private readonly object _workGate = new();
    private readonly TaskCompletionSource<SoeSessionParameters> _established =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _shutdown = new();

    private ClientSendChannel? _send;
    private ClientReceiveChannel? _receive;
    private HarnessRc4? _inboundCipher;
    private HarnessRc4? _outboundCipher;
    private Task? _pump;
    private Task? _reader;
    private int _disposed;

    private SoeClientSession(
        IPEndPoint remote,
        SoeClientOptions options,
        HarnessClock clock,
        PacketJournal journal,
        Func<byte[], string> nameOf)
    {
        _remote = remote;
        _options = options;
        _clock = clock;
        _journal = journal;
        _nameOf = nameOf;
        SessionId = options.SessionId ?? (uint)Random.Shared.NextInt64(1, uint.MaxValue);

        _socket = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = options.ReceiveBufferBytes,
            SendBufferSize = 4 << 20,
        };

        if (OperatingSystem.IsWindows())
        {
            // Without this an ICMP "port unreachable" from a server that is not listening surfaces
            // as a receive error on the next call instead of simply being ignored.
            _socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        }

        _socket.Connect(remote);
    }

    public uint SessionId { get; }

    public string LinkName => _options.LinkName;

    public string ProtocolName => _options.ProtocolName;

    /// <summary>What the server dictated. Null until the SessionReply arrives.</summary>
    public SoeSessionParameters? Parameters { get; private set; }

    public bool EncryptionArmed { get; private set; }

    /// <summary>Inbound keystream position; the harness's own view of the s2c RC4 stream.</summary>
    public long InboundKeystreamPosition => _inboundCipher?.Position ?? 0;

    /// <summary>Outbound keystream position; the c2s equivalent, and the evidence for docs/71 §3 B3.</summary>
    public long OutboundKeystreamPosition => _outboundCipher?.Position ?? 0;

    /// <summary>Complete application messages from the server, in order.</summary>
    public ChannelReader<InboundMessage> Messages => _inbox.Reader;

    public LinkCloseCause CloseCause { get; private set; } = LinkCloseCause.None;

    public string? Fault { get; private set; }
    public Action<bool, ReadOnlyMemory<byte>>? ObserveDatagram { get; set; }

    /// <summary>
    /// Turn this off to reproduce docs/71 §12.3: a client that stops acknowledging. This server
    /// then declares PeerLost after MaxResends × ResendIntervalMs ≈ 8.4 s.
    /// </summary>
    public bool AckingEnabled { get; set; } = true;

    public long DatagramsSent => _send?.DatagramsSent ?? 0;

    public long DatagramsResent => _send?.DatagramsResent ?? 0;

    public long DatagramsReceived { get; private set; }

    public long MessagesReceived { get; private set; }

    public long MessagesSent { get; private set; }

    /// <summary>Opens a session and returns once the server's SessionReply has been parsed.</summary>
    public static async Task<SoeClientSession> OpenAsync(
        IPEndPoint remote,
        SoeClientOptions options,
        HarnessClock clock,
        PacketJournal journal,
        Func<byte[], string> nameOf,
        CancellationToken cancellationToken)
    {
        var session = new SoeClientSession(remote, options, clock, journal, nameOf);
        try
        {
            session.Start();
            await session.HandshakeAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Queues one application message on the reliable channel.</summary>
    public void Send(byte[] message, string? note = null)
    {
        byte[] copy = (byte[])message.Clone();
        Post(() =>
        {
            if (_send is null)
            {
                return;
            }

            Journal(PacketDirection.ToServer, copy, note);
            _send.Send(copy, EncryptionArmed ? _outboundCipher : null, _clock.NowMs);
            MessagesSent++;
        });
    }

    /// <summary>Queues several application messages as one bundle payload (docs/71 §2).</summary>
    public void SendBundle(IReadOnlyList<byte[]> messages, string? note = null)
    {
        byte[][] copies = [.. messages.Select(m => (byte[])m.Clone())];
        Post(() =>
        {
            if (_send is null)
            {
                return;
            }

            foreach (byte[] m in copies)
            {
                Journal(PacketDirection.ToServer, m, note is null ? "bundled" : note + ", bundled");
                MessagesSent++;
            }

            _send.SendBundle(copies, EncryptionArmed ? _outboundCipher : null, _clock.NowMs);
        });
    }

    /// <summary>
    /// The unreliable path: a bare datagram whose first byte is not zero, neither sequenced nor
    /// encrypted. The August client's CryptoBaseApi::Send uses it; the server accepts it as an
    /// application message directly.
    /// </summary>
    public void SendUnreliable(byte[] message, string? note = null)
    {
        if (message.Length == 0 || message[0] == 0)
        {
            throw new ArgumentException(
                "An unreliable application datagram must not start with a zero byte: 00 xx is an SOE control opcode.",
                nameof(message));
        }

        byte[] copy = (byte[])message.Clone();
        Post(() =>
        {
            Journal(PacketDirection.ToServer, copy, note is null ? "unreliable" : note + ", unreliable");
            Transmit(copy);
            MessagesSent++;
        });
    }

    /// <summary>
    /// Sends one clear message and arms RC4 immediately afterwards, in a single step on the pump.
    /// This is docs/71 §3: the August client sends the gateway LoginRequest in the clear and turns
    /// encryption on before the server's reply can arrive, with both keystreams starting at
    /// position 0 and the clear request consuming none of it. Doing this as two calls would leave
    /// a race in which the encrypted LoginReply is decrypted with no cipher installed.
    /// </summary>
    public void SendThenArmEncryption(byte[] clearMessage, byte[] key, string? note = null)
    {
        byte[] copy = (byte[])clearMessage.Clone();
        byte[] keyCopy = (byte[])key.Clone();
        Post(() =>
        {
            if (_send is null)
            {
                return;
            }

            Journal(PacketDirection.ToServer, copy, note is null ? "clear" : note + ", clear");
            _send.Send(copy, null, _clock.NowMs);
            MessagesSent++;
            InstallCiphers(keyCopy);
        });
    }

    /// <summary>Arms RC4 with no message first. Used by links that are encrypted from the first byte.</summary>
    public void ArmEncryption(byte[] key)
    {
        byte[] keyCopy = (byte[])key.Clone();
        Post(() => InstallCiphers(keyCopy));
    }

    /// <summary>Tells the server the session is over, the way the August client does at logout.</summary>
    public void Disconnect(ushort reason = SoeDisconnectReason.Application)
    {
        Post(() =>
        {
            var w = new WireWriter(8);
            w.BeU16(SoeOp.Disconnect).BeU32(SessionId).BeU16(reason);
            Transmit(w.ToArray());
            CloseLink(LinkCloseCause.LocalRequest, $"harness sent Disconnect reason {reason}");
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (CloseCause == LinkCloseCause.None)
        {
            CloseCause = LinkCloseCause.Disposed;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            _socket.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }

        foreach (Task? task in new[] { _reader, _pump })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _inbox.Writer.TryComplete();
        _established.TrySetCanceled();
        _socket.Dispose();
        _shutdown.Dispose();
    }

    private void Start()
    {
        _reader = Task.Run(() => ReadLoopAsync(_shutdown.Token));
        _pump = Task.Run(() => PumpAsync(_shutdown.Token));
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        byte[] request = SoeSessionParameters.BuildRequest(
            _options.CrcLength, SessionId, _options.UdpLength, _options.ProtocolName);
        _journal.Record(new JournalEntry(
            _clock.Now, LinkName, PacketDirection.ToServer,
            $"SOE SessionRequest crc={_options.CrcLength} id={SessionId:x8} udp={_options.UdpLength}",
            request, _options.ProtocolName));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        using var deadline = new CancellationTokenSource(_options.SessionReplyTimeout);
        using var all = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, deadline.Token);

        // The client re-sends its SessionRequest if nothing comes back; so does the harness.
        Task<SoeSessionParameters> established = _established.Task;
        while (!established.IsCompleted)
        {
            Post(() => Transmit(request));
            Task finished = await Task.WhenAny(
                established,
                Task.Delay(TimeSpan.FromMilliseconds(400), all.Token)).ConfigureAwait(false);
            if (finished == established)
            {
                break;
            }

            if (all.IsCancellationRequested)
            {
                break;
            }
        }

        if (!established.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"{LinkName}: no SOE SessionReply from {_remote} for protocol '{_options.ProtocolName}' "
                + $"within {_options.SessionReplyTimeout.TotalSeconds:F1} s. "
                + (Fault is null ? "Nothing arrived at all." : $"Last fault: {Fault}"));
        }

        Parameters = await established.ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[65536];
        while (!cancellationToken.IsCancellationRequested)
        {
            int received;
            try
            {
                received = await _socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Nothing is listening on the far side yet; keep waiting.
                continue;
            }
            catch (SocketException)
            {
                return;
            }

            if (received <= 0)
            {
                continue;
            }

            byte[] datagram = buffer.AsSpan(0, received).ToArray();
            ObserveDatagram?.Invoke(true, datagram);
            _datagrams.Writer.TryWrite(datagram);
            _wake.Writer.TryWrite(true);
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        Task<bool>? ready = null;
        Task? timer = null;
        int timerInterval = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            _wake.Reader.TryRead(out _);
            bool moreWork = DrainWork();

            int handled = 0;
            while (handled < 64 && _datagrams.Reader.TryRead(out byte[]? datagram))
            {
                handled++;
                DatagramsReceived++;
                try
                {
                    HandlePacket(datagram, isSubPacket: false);
                }
                catch (WireFormatException ex)
                {
                    CloseLink(LinkCloseCause.ProtocolError, ex.Message);
                }
            }

            FlushAcks();
            Tick();

            // Service ACKs, resends and outgoing player actions between bounded receive batches.
            // With many busy clients, draining an unbounded queue can otherwise starve them all.
            if (handled == 64 || moreWork)
            {
                await Task.Yield();
                continue;
            }

            try
            {
                // Keep one pending read across timer ticks. Cancelling/recreating it on every
                // idle tick makes thousands of menu clients contend in exception construction.
                ready ??= _wake.Reader.WaitToReadAsync(cancellationToken).AsTask();
                int interval = _send?.PendingCount == 0 ? _options.IdleTickIntervalMs : _options.TickIntervalMs;
                if (timer is null || timerInterval != interval)
                {
                    timerInterval = interval;
                    timer = Task.Delay(interval, cancellationToken);
                }
                await Task.WhenAny(ready, timer).ConfigureAwait(false);
                if (ready.IsCompleted)
                {
                    if (!await ready.ConfigureAwait(false)) return;
                    ready = null;
                }
                if (timer.IsCompleted) timer = null;
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
            catch (ChannelClosedException)
            {
                return;
            }
        }
    }

    private bool DrainWork()
    {
        for (int handled = 0; handled < 256; handled++)
        {
            Action work;
            lock (_workGate)
            {
                if (_work.Count == 0)
                {
                    return false;
                }

                work = _work.Dequeue();
            }

            work();
        }
        lock (_workGate) return _work.Count != 0;
    }

    private void Post(Action work)
    {
        lock (_workGate)
        {
            _work.Enqueue(work);
        }
        _wake.Writer.TryWrite(true);
    }

    private void Tick()
    {
        if (_send is null || CloseCause != LinkCloseCause.None)
        {
            return;
        }

        _send.Tick(_clock.NowMs);
        if (_send.PeerLost)
        {
            CloseLink(
                LinkCloseCause.ServerLost,
                $"{_send.PendingCount} datagram(s) were never acknowledged by the server");
        }
    }

    private void FlushAcks()
    {
        if (_receive is null)
        {
            return;
        }

        if (_receive.OutOfOrderPending is ushort ahead && AckingEnabled)
        {
            var w = new WireWriter(4);
            w.BeU16(SoeOp.OutOfOrder).BeU16(ahead);
            Transmit(w.ToArray());
        }

        if (_receive.AckPending && AckingEnabled)
        {
            var w = new WireWriter(4);
            w.BeU16(SoeOp.Ack).BeU16(_receive.LastInOrder);
            Transmit(w.ToArray());
        }

        _receive.ClearSignals();
    }

    private void HandlePacket(byte[] packet, bool isSubPacket)
    {
        if (packet.Length < 2)
        {
            throw new WireFormatException($"{LinkName}: datagram of {packet.Length} byte(s) is shorter than an opcode.");
        }

        // 00 xx is an SOE control opcode at any level; anything else is a raw application message
        // on the unreliable path (the gateway tunnel byte is (channel << 5) | 6, never zero).
        if (packet[0] != 0)
        {
            DeliverApplication(packet, wasEncrypted: false, keystreamPosition: -1);
            return;
        }

        ushort opcode = (ushort)((packet[0] << 8) | packet[1]);
        byte[] body = packet[2..];

        switch (opcode)
        {
            case SoeOp.SessionReply:
                HandleSessionReply(packet);
                break;

            case SoeOp.Multi:
                HandleMulti(body);
                break;

            case SoeOp.Data:
            case SoeOp.DataFragment:
                HandleReliable(body, opcode == SoeOp.DataFragment, isSubPacket);
                break;

            case SoeOp.Ack:
                _send?.Acknowledge(ReadSequence(body));
                break;

            case SoeOp.OutOfOrder:
                _send?.ResendBefore(ReadSequence(body), _clock.NowMs);
                break;

            case SoeOp.Ping:
                Transmit([0x00, 0x06]);
                break;

            case SoeOp.NetStatusReply:
                break;

            case SoeOp.Disconnect:
            {
                var r = new WireReader(body);
                uint id = r.BeU32();
                ushort reason = r.BeU16();
                CloseLink(LinkCloseCause.ServerRequest, $"server disconnected session {id:x8}, reason {reason}");
                break;
            }

            default:
                // Not a fault: the harness simply has nothing to do with it.
                _journal.Record(new JournalEntry(
                    _clock.Now, LinkName, PacketDirection.FromServer,
                    $"SOE {SoeOp.Name(opcode)}", packet, "ignored by the harness"));
                break;
        }
    }

    private void HandleSessionReply(byte[] packet)
    {
        if (packet.Length < SoeSessionParameters.ReplyLength)
        {
            throw new WireFormatException(
                $"{LinkName}: SessionReply is {packet.Length} byte(s), expected {SoeSessionParameters.ReplyLength}.");
        }

        SoeSessionParameters parameters = SoeSessionParameters.ParseReply(packet);
        _journal.Record(new JournalEntry(
            _clock.Now, LinkName, PacketDirection.FromServer,
            $"SOE SessionReply id={parameters.SessionId:x8} crcLen={parameters.CrcLength} "
            + $"compression={parameters.Compression} udp={parameters.UdpLength} v={parameters.ProtocolVersion}",
            packet));

        if (Parameters is not null)
        {
            return;
        }

        if (parameters.CrcLength != 0)
        {
            // No capture shows this server asking for a CRC trailer, so the harness has no
            // evidence for which polynomial or seed order to use. Refuse rather than invent one.
            throw new WireFormatException(
                $"{LinkName}: the server dictated CrcLength={parameters.CrcLength}. The harness only "
                + "speaks the CRC-less form the August server uses; a CRC would have to be derived first.");
        }

        Parameters = parameters;
        _receive = new ClientReceiveChannel(DeliverReliablePayload);
        _send = new ClientSendChannel(parameters, Transmit);
        if (_options.Key is not null)
        {
            InstallCiphers(_options.Key);
        }

        _established.TrySetResult(parameters);
    }

    private void HandleMulti(byte[] body)
    {
        int offset = 0;
        while (offset < body.Length)
        {
            int length = SoeVarSize.Read(body, ref offset);
            if (length < 2 || length > body.Length - offset)
            {
                throw new WireFormatException(
                    $"{LinkName}: Multi sub-packet of {length} byte(s) at {offset} does not fit.");
            }

            HandlePacket(body.AsSpan(offset, length).ToArray(), isSubPacket: true);
            offset += length;
        }
    }

    private void HandleReliable(byte[] body, bool isFragment, bool isSubPacket)
    {
        SoeSessionParameters parameters = Parameters
            ?? throw new WireFormatException($"{LinkName}: reliable data arrived before the SessionReply.");

        int cursor = 0;
        if (parameters.Compression != 0 && !isSubPacket)
        {
            cursor++;
        }

        if (body.Length < cursor + 2)
        {
            throw new WireFormatException($"{LinkName}: reliable datagram shorter than its header.");
        }

        ushort sequence = (ushort)((body[cursor] << 8) | body[cursor + 1]);
        cursor += 2;
        int end = body.Length - (isSubPacket ? 0 : parameters.CrcLength);
        if (end < cursor)
        {
            throw new WireFormatException($"{LinkName}: reliable datagram shorter than its CRC trailer.");
        }

        _receive!.Accept(sequence, body.AsSpan(cursor, end - cursor), isFragment);
    }

    /// <summary>
    /// One complete reliable payload: either a single application message, or a bundle — the
    /// marker <c>00 19</c> followed by length-prefixed chunks whose bodies are ciphertext and
    /// whose keystream runs on from one chunk to the next.
    /// </summary>
    private void DeliverReliablePayload(byte[] payload)
    {
        if (payload.Length >= 2 && payload[0] == 0 && payload[1] == (byte)SoeOp.Bundle)
        {
            int validate = 2;
            while (validate < payload.Length)
            {
                int chunk = SoeVarSize.Read(payload, ref validate);
                if (chunk < 1 || chunk > payload.Length - validate)
                {
                    throw new WireFormatException(
                        $"{LinkName}: bundled message of {chunk} byte(s) at {validate} does not fit in {payload.Length}.");
                }

                validate += chunk;
            }

            int offset = 2;
            while (offset < payload.Length)
            {
                int chunk = SoeVarSize.Read(payload, ref offset);
                DecryptAndDeliver(payload.AsSpan(offset, chunk).ToArray());
                offset += chunk;
            }

            return;
        }

        DecryptAndDeliver(payload);
    }

    private void DecryptAndDeliver(byte[] message)
    {
        if (!EncryptionArmed)
        {
            DeliverApplication(message, wasEncrypted: false, keystreamPosition: -1);
            return;
        }

        // Ciphertext beginning with zero is escaped by one clear zero, giving 00 00, which is
        // distinct from the 00 19 bundle marker. The pad consumes no keystream.
        Span<byte> body = message;
        if (body.Length > 1 && body[0] == 0 && body[1] == 0)
        {
            body = body[1..];
        }

        long position = _inboundCipher!.Position;
        byte[] plain = body.ToArray();
        _inboundCipher.Transform(plain);
        if (plain.Length == 0)
        {
            return;
        }

        DeliverApplication(plain, wasEncrypted: true, keystreamPosition: position);
    }

    private void DeliverApplication(byte[] message, bool wasEncrypted, long keystreamPosition)
    {
        MessagesReceived++;
        var inbound = new InboundMessage(_clock.Now, message, wasEncrypted, keystreamPosition);
        Journal(PacketDirection.FromServer, message, wasEncrypted ? $"rc4@{keystreamPosition}" : "clear");
        _inbox.Writer.TryWrite(inbound);
    }

    private void InstallCiphers(byte[] key)
    {
        if (EncryptionArmed)
        {
            throw new InvalidOperationException(
                $"{LinkName}: RC4 is already armed. Re-keying would desynchronise the keystream silently.");
        }

        _inboundCipher = new HarnessRc4(key);
        _outboundCipher = new HarnessRc4(key);
        EncryptionArmed = true;
        _journal.Record(new JournalEntry(
            _clock.Now, LinkName, PacketDirection.ToServer,
            "RC4 armed, both keystreams at position 0", [], $"{key.Length}-byte key"));
    }

    private void Journal(PacketDirection direction, byte[] message, string? note)
    {
        string name;
        try
        {
            name = _nameOf(message);
        }
        catch (Exception ex) when (ex is WireFormatException or ArgumentException or IndexOutOfRangeException)
        {
            name = $"unparsed ({ex.GetType().Name})";
        }

        _journal.Record(new JournalEntry(_clock.Now, LinkName, direction, name, message, note));
    }

    private void CloseLink(LinkCloseCause cause, string detail)
    {
        if (CloseCause != LinkCloseCause.None)
        {
            return;
        }

        CloseCause = cause;
        Fault = detail;
        _journal.Record(new JournalEntry(
            _clock.Now, LinkName, PacketDirection.FromServer, $"LINK CLOSED ({cause})", [], detail));
        _inbox.Writer.TryComplete();
        _established.TrySetException(new IOException($"{LinkName}: {detail}"));
    }

    private void Transmit(byte[] datagram)
    {
        try
        {
            _socket.Send(datagram);
            ObserveDatagram?.Invoke(false, datagram);
        }
        catch (SocketException)
        {
            // A send failure is not evidence about the client's behaviour; the resend timer and
            // the milestone deadlines are what report a dead link.
        }
        catch (ObjectDisposedException)
        {
            // Disposed mid-flush.
        }
    }

    private static ushort ReadSequence(byte[] body)
    {
        if (body.Length < 2)
        {
            throw new WireFormatException("Sequence control datagram is shorter than its sequence number.");
        }

        return (ushort)((body[0] << 8) | body[1]);
    }
}
