namespace Cranberry.Zone.World;

/// <summary>
/// One viewer's replicated view: what this client has been told about, and the transient id it knows
/// each entity by. Hysteresis (enter at <c>Enter</c>, leave at <c>Leave</c>) stops an entity sitting
/// on the boundary from being spawned and despawned every tick, which is the failure mode that
/// saturates 512-byte datagrams (docs/22 §4.7).
/// </summary>
public sealed class ObserverView
{
    /// <summary>
    /// Player replication reaches two kilometres, as requested for the public August server.
    /// The client's graphics RenderDistance must also allow that range.
    /// </summary>
    public const float PlayerEnterMetres = 2000f;

    /// <summary>
    /// Keep known players for another 200 metres to avoid repeated spawns at the boundary.
    /// </summary>
    public const float PlayerLeaveMetres = 2200f;
    public const float VehicleEnterMetres = 320f;
    public const float VehicleLeaveMetres = 360f;

    /// <summary>The <c>[F]</c> band; the prompt itself is about 4 m (docs/13 §8).</summary>
    public const float ItemEnterMetres = 80f;
    public const float ItemLeaveMetres = 96f;

    private readonly List<EntityId> _known = [];
    private readonly Dictionary<EntityId, int> _index = [];

    public ObserverView(int spawnBudgetPerTick = 8, int despawnBudgetPerTick = 16)
    {
        SpawnBudgetPerTick = spawnBudgetPerTick;
        DespawnBudgetPerTick = despawnBudgetPerTick;
    }

    // Menu sessions know no world entities. Allocate the maps as peers actually arrive.
    public TransientIdTable Transients { get; } = new(capacity: 0);

    /// <summary>
    /// What stops a drop from queueing 50 lightweight records into one tick and blowing the resend
    /// window at 512 B per datagram. Deferred work is not lost — it lands on the next tick.
    /// </summary>
    public int SpawnBudgetPerTick { get; }

    public int DespawnBudgetPerTick { get; }

    public int KnownCount => _known.Count;

    /// <summary>
    /// Round-robin cursor over known subjects, so a crowded landing zone degrades to a lower
    /// per-peer update rate for everyone instead of freezing the far half of the view (docs/22 §6.1).
    /// </summary>
    public int RelayCursor { get; set; }

    /// <summary>Tick of the last relay pass for this viewer; the staleness key a subject is tested
    /// against, so an unmoved peer costs nothing.</summary>
    public long LastRelayTick { get; set; } = -1;

    public bool Knows(EntityId id) => _index.ContainsKey(id);

    /// <summary>Enumeration order is insertion order with swap-removal; the relay cursor walks it.</summary>
    public EntityId KnownAt(int index) => _known[index];

    public IReadOnlyList<EntityId> Known => _known;

    public void MarkKnown(EntityId id)
    {
        if (_index.ContainsKey(id))
        {
            return;
        }

        _index[id] = _known.Count;
        _known.Add(id);
    }

    public void MarkForgotten(EntityId id)
    {
        if (!_index.Remove(id, out int index))
        {
            return;
        }

        int last = _known.Count - 1;
        if (index != last)
        {
            EntityId moved = _known[last];
            _known[index] = moved;
            _index[moved] = index;
        }

        _known.RemoveAt(last);
        if (RelayCursor >= _known.Count)
        {
            // Valid indices are 0.._known.Count-1 after the removal, so Count itself is already out
            // of range. RelaySystem re-mods by the live count, but a caller that indexes with the
            // raw cursor must not be handed a stale one.
            RelayCursor = 0;
        }
    }

    public void Clear()
    {
        _known.Clear();
        _index.Clear();
        Transients.Clear();
        RelayCursor = 0;
        LastRelayTick = -1;
    }
}
