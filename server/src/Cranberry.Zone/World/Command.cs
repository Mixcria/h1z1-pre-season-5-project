namespace Cranberry.Zone.World;

/// <summary>What a transport thread hands the simulation. One kind per c2s message the zone acts on.</summary>
public enum CommandKind : byte
{
    /// <summary>Gateway channel 2: the local player's own 20 Hz movement record.</summary>
    Movement,

    /// <summary>Gateway channel 3: a pose for an object the client owns (a mount).</summary>
    ManagedMovement,

    Interact,
    PlayerSelect,
    Mount,
    Dismount,
    AutoMountEcho,
    Fire,
    Reload,
    UseItem,
    DropItem,
    Ready,
    Join,
    Leave,
}

/// <summary>
/// One queued client action. Payload bytes live in the queue's own arena — the command carries an
/// offset and a length, never a <c>byte[]</c>, so a 20 Hz stream from 50 players allocates nothing
/// after warm-up (docs/22 §4.6).
/// </summary>
public readonly record struct Command(
    CommandKind Kind,
    int Slot,
    EntityId Target,
    uint Value,
    int PayloadOffset,
    int PayloadLength);

/// <summary>
/// Bounded ring of commands plus a byte arena for their payloads.
/// <para>
/// Enqueue takes a short monitor so a future dedicated match thread (migration step 9) is safe
/// today; under step 1 the producer and the consumer are the same listener thread, so the lock is
/// never contended. Both the ring and the arena are fixed: an overflow increments
/// <see cref="Dropped"/> rather than growing, because unbounded inbound growth under a stall is how
/// a tick loop dies.
/// </para>
/// </summary>
public sealed class CommandQueue
{
    private readonly Lock _gate = new();
    private readonly Command[] _ring;
    private readonly byte[] _arena;
    private int _head;
    private int _tail;
    private int _count;
    private int _arenaUsed;

    public CommandQueue(int capacity = 1024, int arenaBytes = 64 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(arenaBytes);

        _ring = new Command[capacity];
        _arena = new byte[arenaBytes];
    }

    public int Capacity => _ring.Length;

    public int ArenaBytes => _arena.Length;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Commands refused because the ring or the arena was full.</summary>
    public long Dropped { get; private set; }

    /// <summary>Copies the payload into the arena and enqueues one command.</summary>
    public bool TryEnqueue(in Command command, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (_count == _ring.Length || _arenaUsed + payload.Length > _arena.Length)
            {
                Dropped++;
                return false;
            }

            int offset = _arenaUsed;
            if (!payload.IsEmpty)
            {
                payload.CopyTo(_arena.AsSpan(offset));
                _arenaUsed += payload.Length;
            }

            _ring[_tail] = command with { PayloadOffset = offset, PayloadLength = payload.Length };
            _tail = (_tail + 1) % _ring.Length;
            _count++;
            return true;
        }
    }

    public bool TryDequeue(out Command command)
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                command = default;
                return false;
            }

            command = _ring[_head];
            _head = (_head + 1) % _ring.Length;
            _count--;
            return true;
        }
    }

    /// <summary>The bytes this command carries. Valid until the arena is reset.</summary>
    public ReadOnlySpan<byte> Payload(in Command command) =>
        _arena.AsSpan(command.PayloadOffset, command.PayloadLength);

    /// <summary>
    /// Frees the arena. Only legal once the ring is empty: a bounded drain that stopped on the
    /// inbound budget leaves commands whose payloads still point into it, so resetting then would
    /// hand the next tick another command's bytes. Returns false when the reset was refused.
    /// </summary>
    public bool ResetArena()
    {
        lock (_gate)
        {
            if (_count != 0)
            {
                return false;
            }

            _arenaUsed = 0;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _head = 0;
            _tail = 0;
            _count = 0;
            _arenaUsed = 0;
        }
    }
}
