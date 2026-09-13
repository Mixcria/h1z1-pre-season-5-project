using Cranberry.Harness.Wire;

namespace Cranberry.Harness.Soe;

/// <summary>
/// The client's receiving half of the reliable channel: reorders datagrams, rebuilds fragmented
/// messages, and hands each complete reliable payload out exactly once and in order. Sequence
/// numbers are 16-bit and wrap, so "ahead" and "behind" are signed distances.
///
/// Independent of <c>Cranberry.Transport.InboundChannel</c> for the reason given on
/// <see cref="ClientSendChannel"/>. In particular the reassembly rule — the first fragment of a
/// group carries a big-endian u32 total length, later fragments are raw — is re-derived here so
/// that a mistake in the server's fragmenter shows up as a harness parse failure rather than as
/// a matching mistake in the reassembler.
/// </summary>
internal sealed class ClientReceiveChannel(Action<byte[]> deliver)
{
    /// <summary>Enough to hold a whole 1.2 MB ReferenceData blob's worth of 512-byte datagrams.</summary>
    public const int MaxHeldAhead = 8192;

    /// <summary>The largest application message the harness will rebuild.</summary>
    public const int MaxMessageLength = 8 << 20;

    private readonly Dictionary<ushort, Held> _ahead = new();
    private byte[]? _assembly;
    private int _assemblyExpected;
    private int _assemblyFilled;
    private ushort _next;

    /// <summary>Highest sequence delivered in order; the value a cumulative Ack carries.</summary>
    public ushort LastInOrder => (ushort)(_next - 1);

    /// <summary>Set when an Ack is owed: in-order progress was made, or a duplicate arrived.</summary>
    public bool AckPending { get; private set; }

    /// <summary>Set when a datagram arrived ahead of the expected one.</summary>
    public ushort? OutOfOrderPending { get; private set; }

    public int HeldCount => _ahead.Count;

    public bool ReassemblyInProgress => _assembly is not null;

    public long MessagesDelivered { get; private set; }

    public void ClearSignals()
    {
        AckPending = false;
        OutOfOrderPending = null;
    }

    public void Accept(ushort sequence, ReadOnlySpan<byte> payload, bool isFragment)
    {
        int distance = (short)(sequence - _next);
        if (distance == 0)
        {
            Process(payload, isFragment);
            _next++;
            AckPending = true;
            DrainHeld();
            return;
        }

        if (distance > 0)
        {
            if (_ahead.Count >= MaxHeldAhead)
            {
                throw new WireFormatException(
                    $"More than {MaxHeldAhead} datagrams held ahead of sequence {_next}; the harness lost the stream.");
            }

            if (!_ahead.ContainsKey(sequence))
            {
                _ahead[sequence] = new Held(payload.ToArray(), isFragment);
            }

            OutOfOrderPending = sequence;
            return;
        }

        // Behind: a resend of something already delivered. Re-acknowledge so the server stops.
        AckPending = true;
    }

    public void Reset()
    {
        _ahead.Clear();
        _assembly = null;
        _assemblyExpected = 0;
        _assemblyFilled = 0;
    }

    private void DrainHeld()
    {
        while (_ahead.Remove(_next, out Held held))
        {
            Process(held.Payload, held.IsFragment);
            _next++;
        }
    }

    private void Process(ReadOnlySpan<byte> payload, bool isFragment)
    {
        if (!isFragment)
        {
            if (_assembly is not null)
            {
                Reset();
                throw new WireFormatException("A whole datagram arrived in the middle of a fragment group.");
            }

            MessagesDelivered++;
            deliver(payload.ToArray());
            return;
        }

        if (_assembly is null)
        {
            if (payload.Length < 4)
            {
                throw new WireFormatException("First fragment is shorter than its 4-byte length prefix.");
            }

            uint declared = (uint)((payload[0] << 24) | (payload[1] << 16) | (payload[2] << 8) | payload[3]);
            if (declared == 0 || declared > MaxMessageLength)
            {
                throw new WireFormatException($"Fragment group declares {declared} byte(s).");
            }

            _assembly = new byte[declared];
            _assemblyExpected = (int)declared;
            _assemblyFilled = 0;
            payload = payload[4..];
        }

        if (_assemblyFilled + payload.Length > _assemblyExpected)
        {
            int overflow = _assemblyFilled + payload.Length;
            Reset();
            throw new WireFormatException(
                $"Fragment group overflows its declared length ({overflow} > {_assemblyExpected}).");
        }

        payload.CopyTo(_assembly.AsSpan(_assemblyFilled));
        _assemblyFilled += payload.Length;
        if (_assemblyFilled != _assemblyExpected)
        {
            return;
        }

        byte[] message = _assembly;
        _assembly = null;
        _assemblyExpected = 0;
        _assemblyFilled = 0;
        MessagesDelivered++;
        deliver(message);
    }

    private readonly record struct Held(byte[] Payload, bool IsFragment);
}
