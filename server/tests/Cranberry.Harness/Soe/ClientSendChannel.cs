using Cranberry.Harness.Wire;

namespace Cranberry.Harness.Soe;

/// <summary>
/// The client's sending half of the reliable channel: numbers datagrams, fragments long messages,
/// keeps each datagram until the peer acknowledges it, and resends on a timer.
///
/// Written independently of <c>Cranberry.Transport.OutboundChannel</c> on purpose. The two are
/// the same protocol seen from opposite ends, and if the harness borrowed the server's code an
/// off-by-one in fragmentation or in the leading-zero escape would be applied identically at both
/// ends and no test could see it. Ordering facts encoded here, all from the captures and from
/// what the server's receiver demands:
/// <list type="bullet">
/// <item>RC4 runs over the whole application message <b>before</b> fragmentation, so a resend is
/// the cached bytes verbatim — re-encrypting would desynchronise the keystream.</item>
/// <item>Ciphertext whose first byte is zero is prefixed with one clear zero, producing
/// <c>00 00</c>, which is distinct from the <c>00 19</c> bundle marker. The pad is not encrypted
/// and does not consume keystream.</item>
/// <item>The first fragment of a group carries the total message length as a big-endian u32.</item>
/// </list>
/// </summary>
internal sealed class ClientSendChannel(SoeSessionParameters parameters, Action<byte[]> transmit)
{
    private readonly Queue<Pending> _pending = new();
    private ushort _nextSequence;

    /// <summary>Milliseconds a datagram waits for an Ack before it goes out again.</summary>
    public int ResendIntervalMs { get; init; } = 300;

    /// <summary>Resends after which the harness declares the server gone.</summary>
    public int MaxResends { get; init; } = 25;

    public int PendingCount => _pending.Count;

    public ushort NextSequence => _nextSequence;

    public long DatagramsSent { get; private set; }

    public long DatagramsResent { get; private set; }

    /// <summary>True once a datagram has exhausted its resends: the server stopped acknowledging.</summary>
    public bool PeerLost { get; private set; }

    /// <summary>Queues one complete application message.</summary>
    public void Send(ReadOnlySpan<byte> message, HarnessRc4? cipher, long nowMs)
    {
        if (message.IsEmpty)
        {
            return;
        }

        byte[] body = Envelope(message, cipher);
        int room = parameters.MaxReliablePayload;
        if (room < 8)
        {
            throw new WireFormatException(
                $"SessionReply left only {room} byte(s) per reliable datagram; the harness cannot speak that.");
        }

        if (body.Length <= room)
        {
            Emit(SoeOp.Data, body, 0, body.Length, null, nowMs);
            return;
        }

        int firstChunk = room - 4;
        Emit(SoeOp.DataFragment, body, 0, firstChunk, (uint)body.Length, nowMs);
        for (int offset = firstChunk; offset < body.Length; offset += room)
        {
            Emit(SoeOp.DataFragment, body, offset, Math.Min(room, body.Length - offset), null, nowMs);
        }
    }

    /// <summary>
    /// Wraps several application messages into one bundle payload (<c>00 19</c> then
    /// length-prefixed chunks). The marker and the prefixes travel in the clear; only the chunk
    /// bodies are ciphertext and the keystream runs on from one body to the next. This is the
    /// shape the August client used for its <c>ServerListRequest</c> + <c>CharacterSelectInfoRequest</c>
    /// pair (docs/71 §2), so the harness can reproduce it on demand.
    /// </summary>
    public void SendBundle(IReadOnlyList<byte[]> messages, HarnessRc4? cipher, long nowMs)
    {
        if (messages.Count == 0)
        {
            return;
        }

        if (messages.Count == 1)
        {
            Send(messages[0], cipher, nowMs);
            return;
        }

        var w = new WireWriter(256);
        w.BeU16(SoeOp.Bundle);
        foreach (byte[] message in messages)
        {
            byte[] chunk = Encrypt(message, cipher);
            SoeVarSize.Write(w, chunk.Length);
            w.Raw(chunk);
        }

        byte[] payload = w.ToArray();
        int room = parameters.MaxReliablePayload;
        if (payload.Length <= room)
        {
            Emit(SoeOp.Data, payload, 0, payload.Length, null, nowMs);
            return;
        }

        int firstChunk = room - 4;
        Emit(SoeOp.DataFragment, payload, 0, firstChunk, (uint)payload.Length, nowMs);
        for (int offset = firstChunk; offset < payload.Length; offset += room)
        {
            Emit(SoeOp.DataFragment, payload, offset, Math.Min(room, payload.Length - offset), null, nowMs);
        }
    }

    /// <summary>Cumulative acknowledgement: everything up to and including <paramref name="sequence"/> is done.</summary>
    public void Acknowledge(ushort sequence)
    {
        int release = 0;
        bool found = false;
        foreach (Pending p in _pending)
        {
            release++;
            if (p.Sequence == sequence)
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            return;
        }

        while (release-- > 0)
        {
            _pending.Dequeue();
        }
    }

    /// <summary>The peer saw <paramref name="sequence"/> before something earlier; resend what precedes it.</summary>
    public void ResendBefore(ushort sequence, long nowMs)
    {
        foreach (Pending p in _pending)
        {
            if ((short)(p.Sequence - sequence) >= 0)
            {
                break;
            }

            Resend(p, nowMs);
        }
    }

    public void Tick(long nowMs)
    {
        foreach (Pending p in _pending)
        {
            if (nowMs - p.LastSent < ResendIntervalMs)
            {
                continue;
            }

            if (p.Resends >= MaxResends)
            {
                PeerLost = true;
                return;
            }

            Resend(p, nowMs);
        }
    }

    public void Clear() => _pending.Clear();

    private static byte[] Encrypt(ReadOnlySpan<byte> message, HarnessRc4? cipher)
    {
        byte[] copy = message.ToArray();
        cipher?.Transform(copy);
        return copy;
    }

    private static byte[] Envelope(ReadOnlySpan<byte> message, HarnessRc4? cipher)
    {
        byte[] body = Encrypt(message, cipher);
        if (cipher is null || body.Length == 0 || body[0] != 0)
        {
            return body;
        }

        byte[] padded = new byte[body.Length + 1];
        body.CopyTo(padded.AsSpan(1));
        return padded;
    }

    private void Emit(ushort opcode, byte[] body, int offset, int count, uint? totalLength, long nowMs)
    {
        var w = new WireWriter(count + 16);
        w.BeU16(opcode);
        if (parameters.Compression != 0)
        {
            w.U8(0);
        }

        w.BeU16(_nextSequence);
        if (totalLength.HasValue)
        {
            w.BeU32(totalLength.Value);
        }

        w.Raw(body.AsSpan(offset, count));
        byte[] datagram = w.ToArray();

        _pending.Enqueue(new Pending(_nextSequence, datagram, nowMs));
        _nextSequence++;
        DatagramsSent++;
        transmit(datagram);
    }

    private void Resend(Pending p, long nowMs)
    {
        p.LastSent = nowMs;
        p.Resends++;
        DatagramsResent++;
        transmit(p.Datagram);
    }

    private sealed class Pending(ushort sequence, byte[] datagram, long lastSent)
    {
        public ushort Sequence { get; } = sequence;

        public byte[] Datagram { get; } = datagram;

        public long LastSent { get; set; } = lastSent;

        public int Resends { get; set; }
    }
}
