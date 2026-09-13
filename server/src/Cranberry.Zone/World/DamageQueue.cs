namespace Cranberry.Zone.World;

/// <summary>
/// Why a player lost health. Maps onto the client's own death-cause byte
/// (<c>GasPackets.DeathCause</c>, <c>ce 04</c>) where one exists.
/// </summary>
public enum DamageCause : byte
{
    Unknown,
    ToxicGas,
    Bullet,
    Melee,
    Falling,
    Vehicle,
    Explosion,
    Fire,
    Disconnected,
    /// <summary>Supply-plane bomber ordnance; maps to the client's dedicated bombing-run results cause.</summary>
    BombingRun,
}

/// <summary>
/// Queued by any producer; drained by exactly one resolver (docs/22 §6.3).
/// <para>
/// <see cref="Bleeds"/> and <see cref="Headshot"/> are carried rather than decided at the resolver
/// because the hit rule is the only thing that knows them - a helmet that absorbed a headshot opens
/// no wound, and gas never does - and <c>Match.ResolveDamage</c> is the only place health moves.
/// Still a readonly record struct: the queue allocates nothing per hit.
/// </para>
/// </summary>
public readonly record struct PendingDamage(
    int Slot,
    int Amount,
    DamageCause Cause,
    EntityId Attacker,
    bool Bleeds = false,
    bool Headshot = false);

/// <summary>
/// The tick's damage, in production order. Nothing writes health directly: gas and a bullet landing
/// on the same tick must produce one death, one <c>ce 04</c> and one decrement of the alive count,
/// which only a single resolver over a single queue can guarantee.
/// </summary>
public sealed class DamageQueue
{
    private PendingDamage[] _items;
    private int _count;

    public DamageQueue(int capacity = 64) => _items = new PendingDamage[Math.Max(4, capacity)];

    public int Count => _count;

    public ref readonly PendingDamage this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return ref _items[index];
        }
    }

    public void Add(in PendingDamage damage)
    {
        if (_count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }

        _items[_count++] = damage;
    }

    /// <summary>
    /// Drops the first <paramref name="count"/> entries, keeping anything queued while they were
    /// being resolved (a death that queues follow-up damage) for the next pass.
    /// </summary>
    public void RemoveFirst(int count)
    {
        if (count <= 0)
        {
            return;
        }

        if (count >= _count)
        {
            _count = 0;
            return;
        }

        Array.Copy(_items, count, _items, 0, _count - count);
        _count -= count;
    }

    public void Clear() => _count = 0;
}
