using System.Buffers;
using System.Diagnostics;

namespace Cranberry.Transport;

/// <summary>
/// The receiving half of the reliable channel: puts datagrams back in sequence order, rebuilds
/// fragmented messages, and hands each complete message to the connection exactly once.
/// Sequence numbers are 16-bit and wrap; "ahead" and "behind" are decided by signed distance.
/// </summary>
internal sealed class InboundChannel
{
    public const int MaxHeldAhead = 4096;

    private readonly Dictionary<ushort, HeldDatagram> _ahead = new();
    private readonly ReassemblyBuffer _assembly = new();
    private readonly Action<byte[], int> _deliver;
    private ushort _next;
    internal SoeDiagnosticCollector? Diagnostics { get; set; }
    private long _assemblyStarted;
    public long HeldBytes { get; private set; }
    public double OldestHeldMs => _ahead.Count == 0 ? 0 : _ahead.Values.Max(h =>
        h.QueuedAt == 0 ? 0 : Stopwatch.GetElapsedTime(h.QueuedAt).TotalMilliseconds);

    public InboundChannel(Action<byte[], int> deliver)
    {
        _deliver = deliver;
    }

    /// <summary>Highest sequence delivered in order so far; what our cumulative Ack carries.</summary>
    public ushort LastInOrder => (ushort)(_next - 1);

    /// <summary>Set when an Ack should go out: in-order progress was made, or a duplicate arrived.</summary>
    public bool AckPending { get; private set; }

    /// <summary>Set when a datagram arrived ahead of the expected one; the value to report as OutOfOrder.</summary>
    public ushort? OutOfOrderPending { get; private set; }

    public int HeldCount => _ahead.Count;

    public bool ReassemblyInProgress => _assembly.InProgress;

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
            Diagnostics?.Record(SoeDiagnosticCounter.ReliableInOrder);
            Process(payload, isFragment);
            _next++;
            AckPending = true;
            DrainHeld();
            // Several datagrams may share one receive pass. A repaired gap must not solicit
            // redundant retransmissions immediately before its cumulative acknowledgement.
            if (OutOfOrderPending is ushort ahead && (short)(ahead - _next) < 0)
                OutOfOrderPending = null;
            return;
        }

        if (distance > 0)
        {
            Diagnostics?.Record(SoeDiagnosticCounter.ReliableAhead);
            if (!_ahead.ContainsKey(sequence))
            {
                // A repeated admitted packet consumes no additional capacity. In particular,
                // loss recovery may duplicate a packet while the reorder buffer is full.
                if (_ahead.Count >= MaxHeldAhead)
                    throw new SoeProtocolException($"More than {MaxHeldAhead} datagrams held ahead of sequence {_next}.");
                byte[] copy = ArrayPool<byte>.Shared.Rent(payload.Length);
                payload.CopyTo(copy);
                _ahead[sequence] = new HeldDatagram(copy, payload.Length, isFragment,
                    Diagnostics is null ? 0 : Stopwatch.GetTimestamp());
                HeldBytes += payload.Length;
            }
            else Diagnostics?.Record(SoeDiagnosticCounter.ReliableAheadDuplicate);

            OutOfOrderPending = sequence;
            return;
        }

        // Behind: a resend of something already delivered. Re-acknowledge so the peer stops.
        Diagnostics?.Record(SoeDiagnosticCounter.ReliableBehind);
        AckPending = true;
    }

    private void DrainHeld()
    {
        while (_ahead.Remove(_next, out HeldDatagram held))
        {
            HeldBytes -= held.Length;
            if (held.QueuedAt != 0) Diagnostics?.ReliableHeldWait.RecordTicks(Stopwatch.GetTimestamp() - held.QueuedAt);
            try
            {
                Process(held.Buffer.AsSpan(0, held.Length), held.IsFragment);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(held.Buffer);
            }

            _next++;
        }
    }

    private void Process(ReadOnlySpan<byte> payload, bool isFragment)
    {
        if (isFragment)
        {
            if (!_assembly.InProgress && Diagnostics is not null) _assemblyStarted = Stopwatch.GetTimestamp();
            if (_assembly.Add(payload, out byte[] message, out int length))
            {
                if (_assemblyStarted != 0) Diagnostics?.ReliableAssemblyWait.RecordTicks(Stopwatch.GetTimestamp() - _assemblyStarted);
                _assemblyStarted = 0;
                try
                {
                    _deliver(message, length);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(message);
                }
            }

            return;
        }

        if (_assembly.InProgress)
        {
            _assembly.Reset();
            throw new SoeProtocolException("Whole datagram arrived in the middle of a fragment group.");
        }

        byte[] copy = ArrayPool<byte>.Shared.Rent(payload.Length);
        try
        {
            payload.CopyTo(copy);
            _deliver(copy, payload.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copy);
        }
    }

    /// <summary>Returns every pooled buffer still owned by an interrupted channel.</summary>
    public void Close()
    {
        foreach (HeldDatagram held in _ahead.Values)
        {
            ArrayPool<byte>.Shared.Return(held.Buffer);
        }

        _ahead.Clear();
        HeldBytes = 0;
        _assemblyStarted = 0;
        _assembly.Reset();
    }

    private readonly record struct HeldDatagram(byte[] Buffer, int Length, bool IsFragment, long QueuedAt);
}
