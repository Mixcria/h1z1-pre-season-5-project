namespace Cranberry.Zone.World;

/// <summary>
/// One session's dense map between world entities and the client varint ids that session knows them
/// by. Ids 0–15 are reserved (<see cref="LocalPlayer"/> = 1, matching the self record), allocation
/// starts at <see cref="FirstAllocated"/>, and an id returns to a LIFO free list only after the
/// despawn packet for it has been queued — which is exactly the churn interest management creates.
/// A per-match monotonic allocator would never recycle and would drift past two varint bytes over a
/// long match (<c>ClientVarInt.Length</c> widens at 2^14).
/// <para>
/// Per viewer, not per zone: whether the client's transient space is per link or per zone is not
/// derived (docs/22 §12 row 6). Per link is the strictly safer superset — each client has exactly
/// one link and only ever echoes back an id this server handed <em>it</em>.
/// </para>
/// </summary>
public sealed class TransientIdTable
{
    /// <summary>The viewer's own actor; the self record fixes this one.</summary>
    public const uint LocalPlayer = 1;

    /// <summary>First id handed to a replicated entity; 0–15 stay reserved.</summary>
    public const uint FirstAllocated = 16;

    private readonly Dictionary<EntityId, uint> _byEntity;
    private readonly Dictionary<uint, EntityId> _byTransient;
    private readonly Stack<uint> _free;
    private uint _next = FirstAllocated;

    // The live peer registry maintains the reverse view at mapping changes, rather than
    // looking through every player's table for each movement record. Owner thread only.
    internal Action<EntityId, uint, bool>? MappingChanged { get; set; }
    internal Dictionary<EntityId, uint> Mappings => _byEntity;

    public TransientIdTable(int capacity = 512)
    {
        _byEntity = new Dictionary<EntityId, uint>(capacity);
        _byTransient = new Dictionary<uint, EntityId>(capacity);
        _free = new Stack<uint>(capacity);
    }

    /// <summary>How many entities this viewer currently knows an id for.</summary>
    public int LiveCount => _byEntity.Count;

    /// <summary>Highest id ever handed out; the varint width follows from this.</summary>
    public uint Peak { get; private set; }

    /// <summary>
    /// The id this viewer knows <paramref name="id"/> by, minting one on first sight. Stable while
    /// the entity stays known: calling twice returns the same id.
    /// </summary>
    public uint Acquire(EntityId id)
    {
        if (_byEntity.TryGetValue(id, out uint existing))
        {
            return existing;
        }

        uint transientId = _free.Count > 0 ? _free.Pop() : _next++;
        _byEntity[id] = transientId;
        _byTransient[transientId] = id;
        if (transientId > Peak)
        {
            Peak = transientId;
        }

        MappingChanged?.Invoke(id, transientId, true);
        return transientId;
    }

    public bool TryGet(EntityId id, out uint transientId) => _byEntity.TryGetValue(id, out transientId);

    /// <summary>Resolves an id the client named back (a mount request, a managed pose).</summary>
    public bool TryResolve(uint transientId, out EntityId id) => _byTransient.TryGetValue(transientId, out id);

    /// <summary>
    /// Frees the id. Call only after the despawn has been queued to this session, or the client can
    /// receive a spawn that reuses an id it still has bound.
    /// </summary>
    public void Release(EntityId id)
    {
        if (!_byEntity.Remove(id, out uint transientId))
        {
            return;
        }

        _byTransient.Remove(transientId);
        _free.Push(transientId);
        MappingChanged?.Invoke(id, transientId, false);
    }

    /// <summary>Drops the whole table; the viewer left the match or zoned.</summary>
    public void Clear()
    {
        if (MappingChanged is { } changed)
            foreach (var mapping in _byEntity) changed(mapping.Key, mapping.Value, false);
        _byEntity.Clear();
        _byTransient.Clear();
        _free.Clear();
        _next = FirstAllocated;
        Peak = 0;
    }
}
