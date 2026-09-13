using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Cranberry.Zone;

/// <summary>
/// One lootable object lying in the world: the world-object guid the client names back in
/// <see cref="InteractRequest"/>, the client-side ids needed to render it
/// (<see cref="GroundModelId"/> from <c>Models.txt</c>, <see cref="NameId"/> for the prompt text)
/// and the item identity granted on pickup (<see cref="ItemDefinitionId"/> from
/// <c>ClientItemDefinitions.txt</c>). docs/13 §2 proves the definition→ground-model linkage; the
/// item definition id itself never rides in the spawn packet.
/// </summary>
public sealed record GroundLootItem(
    ulong WorldGuid,
    uint TransientId,
    uint ItemDefinitionId,
    uint GroundModelId,
    uint NameId,
    uint Count,
    Vector3 Position)
{
    /// <summary>Cosmetic captured when dropped; gameplay identity stays ItemDefinitionId.</summary>
    public uint SkinRewardItemId { get; init; }
    /// <summary>Retained firearm rounds on a player drop; null keeps fresh-spawn policy.</summary>
    public int? MagazineRounds { get; init; }
}

/// <summary>
/// Server-owned registry of the ground loot in one session's world: <c>worldGuid →
/// (itemDefinitionId, count, position)</c> plus the ids the spawn packet needs (docs/13 §9 step 1).
/// <para>
/// Claiming is destructive and single-shot, which is exactly the idempotency the August client
/// forces on the server: one F press emits both <c>Command.PlayerSelect</c> and
/// <c>Command.InteractRequest</c> for the same object (docs/13 §8), so the first request to reach
/// <see cref="TryClaim"/> wins and the second finds nothing to grant.
/// </para>
/// </summary>
public sealed class LootWorld
{
    private readonly Dictionary<ulong, GroundLootItem> _items = [];

    /// <summary>
    /// Guids this session streamed back out again, so the pickup path can tell "already claimed by
    /// the other half of the same [F] press" apart from "this object was streamed out before you
    /// pressed" (docs/52 §5d). Both look identical to <see cref="IsLootGuid"/>, and logging the
    /// wrong one is exactly the log-versus-truth divergence D29 exists to stop. Bounded by the number
    /// of items one session streams — about 2,000 in a 20-minute match, i.e. 16 KB.
    /// </summary>
    private readonly HashSet<ulong> _evicted = [];

    private ulong _nextWorldGuid;
    private uint _nextTransientId;
    private ulong _nextItemGuid;

    public LootWorld(
        ulong worldGuidBase = DefaultWorldGuidBase,
        uint transientIdBase = DefaultTransientIdBase,
        ulong itemGuidBase = DefaultItemGuidBase,
        uint transientIdCeiling = DefaultTransientIdCeiling)
    {
        _worldGuidBase = worldGuidBase;
        _nextWorldGuid = worldGuidBase;
        _nextTransientId = transientIdBase;
        _nextItemGuid = itemGuidBase;
        _transientIdCeiling = transientIdCeiling;
    }

    private readonly uint _transientIdCeiling;

    private readonly ulong _worldGuidBase;

    /// <summary>
    /// Guid range for world objects. A range of its own keeps ground loot from colliding with the
    /// character guids the login host issues or with the parachute's <c>guid + 0x1000</c>
    /// (docs/12); the specific bases follow the owner's earlier server (lead, not load-bearing —
    /// the client only ever echoes the guid back).
    /// </summary>
    public const ulong DefaultWorldGuidBase = 0x2000_0000_0000_0001;

    /// <summary>Guid range for the carried item instances named by <see cref="ItemAdd"/>.</summary>
    public const ulong DefaultItemGuidBase = 0x3100_0000_0000_0001;

    /// <summary>
    /// Transient-id range. The parachute uses 2 (<c>ZoneOptions.ParachuteTransientId</c>), so
    /// ground loot starts well above it; the client varint widens automatically.
    /// </summary>
    public const uint DefaultTransientIdBase = 1000;

    /// <summary>
    /// Where ground loot's transient ids must stop: <c>MatchDoors.DefaultTransientIdBase</c>. The two
    /// spaces are disjoint by construction and streaming does not change that — 32 spawns per 3 s for
    /// twenty minutes is 12,800 ids against 999,000 available, 78× headroom (docs/52 §4f) — but a
    /// collision would be severe rather than untidy: the <c>0xda</c> applier <c>FUN_140b02060</c>
    /// matches its record to an object <b>by transient id, not by guid</b>, so an id shared with a
    /// door would apply a loot record to that door. Observable through
    /// <see cref="TransientIdHeadroom"/> rather than enforced by a throw, because the only place it
    /// could throw is a burst action on the listener thread.
    /// </summary>
    public const uint DefaultTransientIdCeiling = 1_000_000;

    public int Count => _items.Count;

    public IReadOnlyCollection<GroundLootItem> Items => _items.Values;

    /// <summary>
    /// How many more transient ids this world may mint before it reaches
    /// <see cref="DefaultTransientIdCeiling"/>. A streamer passes this to
    /// <c>MatchLoot.PlanRestream</c>'s spawn budget so that exhaustion degrades into "no more loot
    /// streams" rather than into loot records landing on doors.
    /// </summary>
    public int TransientIdHeadroom => _nextTransientId >= _transientIdCeiling
        ? 0
        : (int)Math.Min(int.MaxValue, _transientIdCeiling - _nextTransientId);

    /// <summary>Allocates ids and registers one lootable object at <paramref name="position"/>.</summary>
    public GroundLootItem Spawn(
        uint itemDefinitionId,
        uint groundModelId,
        Vector3 position,
        uint count = 1,
        uint nameId = 0,
        uint skinRewardItemId = 0,
        int? magazineRounds = null)
    {
        var item = new GroundLootItem(
            WorldGuid: _nextWorldGuid++,
            TransientId: _nextTransientId++,
            ItemDefinitionId: itemDefinitionId,
            GroundModelId: groundModelId,
            NameId: nameId,
            Count: count,
            Position: position) { SkinRewardItemId = skinRewardItemId, MagazineRounds = magazineRounds };
        _items.Add(item.WorldGuid, item);
        return item;
    }

    public bool TryGet(ulong worldGuid, [NotNullWhen(true)] out GroundLootItem? item) =>
        _items.TryGetValue(worldGuid, out item);

    /// <summary>Retain the existing world identity after a partial stack pickup.</summary>
    public GroundLootItem ReduceCount(ulong worldGuid, uint remaining)
    {
        GroundLootItem current = _items[worldGuid];
        if (remaining == 0 || remaining > current.Count)
            throw new ArgumentOutOfRangeException(nameof(remaining));
        return _items[worldGuid] = current with { Count = remaining };
    }

    /// <summary>
    /// True when this guid was ever minted by this <see cref="LootWorld"/> — including one that has
    /// since been claimed and removed by <see cref="TryClaim"/>.
    /// <para>
    /// <b>Why a range test rather than <see cref="TryGet"/>.</b> One <c>[F]</c> press produces
    /// <b>two</b> c2s packets naming the same guid (<c>09 15 PlayerSelect</c> then
    /// <c>09 07 InteractRequest</c> about 2 ms later, live-captured). The first one claims the item
    /// destructively, so by the time the second arrives <see cref="TryGet"/> says "not loot" and any
    /// caller that used it to decide "this is not mine" — the door resolver's positional fallback —
    /// would then act on the second packet. Loot, doors and vehicles mint from disjoint guid bases,
    /// so "at or above my base and below the next id I will issue" identifies a loot guid exactly and
    /// in O(1), without a set that grows for the life of the match.
    /// </para>
    /// <para>
    /// Deliberately <b>not</b> reset by <see cref="Clear"/>: the id allocator is not rewound either,
    /// so a guid from a previous world in this session stays a loot guid and still must not be
    /// mistaken for a door.
    /// </para>
    /// </summary>
    public bool IsLootGuid(ulong worldGuid) => worldGuid >= _worldGuidBase && worldGuid < _nextWorldGuid;

    /// <summary>
    /// Takes the object out of the world for the caller. Returns false when it was already claimed
    /// — the duplicate request of one F press, or a second player racing for the same pile.
    /// </summary>
    public bool TryClaim(ulong worldGuid, [NotNullWhen(true)] out GroundLootItem? item)
    {
        if (!_items.Remove(worldGuid, out item))
        {
            item = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Takes a streamed object out of the world because the player has walked away from it, not
    /// because anyone picked it up (docs/52 §3a EVICT). Identical bookkeeping to
    /// <see cref="TryClaim"/> and deliberately a separate method: the two differ in what the caller
    /// must do next (an eviction sends <c>0f 01 RemovePlayer</c> and nothing else — no grant, no
    /// <c>ItemAdd</c>), in what they mean for the streamer's key sets (an evicted marker may be
    /// streamed again, a claimed one never), and in what an honest log line says about them.
    /// <para>
    /// <b>The allocator is not rewound.</b> <see cref="IsLootGuid"/> is a range test over every guid
    /// this world ever minted, and it is what stops a loot press from swinging a door; an evicted
    /// guid has to stay "a loot guid" forever. A marker that is streamed in again gets a
    /// <b>new</b> guid and a new transient id, which is what makes despawn-then-respawn inside one
    /// burst structurally safe (docs/52 §6c).
    /// </para>
    /// </summary>
    public bool TryEvict(ulong worldGuid, [NotNullWhen(true)] out GroundLootItem? item)
    {
        if (!_items.Remove(worldGuid, out item))
        {
            item = null;
            return false;
        }

        _evicted.Add(worldGuid);
        return true;
    }

    /// <summary>
    /// Was this guid streamed out rather than claimed? The pickup path uses it to keep its log
    /// honest: before streaming, a <c>09 07</c>/<c>09 15</c> naming a guid that
    /// <see cref="IsLootGuid"/> accepts but <see cref="TryGet"/> does not could only be the second
    /// packet of one <c>[F]</c> press, and the log said so. After streaming it can equally be an
    /// object the player walked away from, and saying "already claimed by the other half of the same
    /// press — expected" about that would be a lie (docs/52 §5d).
    /// </summary>
    public bool WasEvicted(ulong worldGuid) => _evicted.Contains(worldGuid);

    /// <summary>
    /// The registered items within <paramref name="radius"/> metres of <paramref name="centre"/>,
    /// measured in the horizontal plane — the same plane the streamer's own radii use, because an
    /// item is reachable from the floor it is on.
    /// <para>
    /// <b>Wave 8 took the narrowing this comment predicted.</b> It used to say that publishing the
    /// whole registry was correct only because the working set was bounded to
    /// <c>LootStreamOptions.MaxLive</c> = 128, and that this was the filter available if that bound
    /// were ever raised. It was raised, and <see cref="SelectPanelRows"/> is the filter. This method
    /// remains for callers that want a plain radius query.
    /// </para>
    /// </summary>
    public IEnumerable<GroundLootItem> ItemsWithin(Vector3 centre, float radius)
    {
        if (!float.IsFinite(radius) || radius <= 0f)
        {
            yield break;
        }

        float radiusSquared = radius * radius;
        foreach (GroundLootItem item in _items.Values)
        {
            float dx = item.Position.X - centre.X;
            float dz = item.Position.Z - centre.Z;
            if ((dx * dx) + (dz * dz) <= radiusSquared)
            {
                yield return item;
            }
        }
    }

    /// <summary>
    /// <b>The rows the <c>f8 01 ProximateItems</c> panel should carry</b> — the owner's own 2 m ball
    /// (<c>ZoneProximity.ProximityRadius</c> 2 m, <c>ProximityYDistance</c> ±1 m,
    /// <c>MaximumEntries</c> 32), nearest first, appended to <paramref name="into"/>.
    /// <para>
    /// <b>Why the panel is a ball and not the registry.</b> The packet is <c>7 + 74 × N</c> bytes and
    /// its client-side sink also recomputes every crafting recipe, so N was the streamer's dominant
    /// cost and the only thing that ever pinned <c>LootStreamOptions.MaxLive</c> at 128 — a measured
    /// <b>9,552 B</b> per republish at 129 rows. Inside 2 m / ±1 m at the owner's density the
    /// measured distribution is median 0, p90 3, p99 6, max 9 rows: <b>155 B p90</b> (docs/78 §3.3).
    /// </para>
    /// <para>
    /// <b>Allocation-free in the steady state.</b> One pass over the registry with no LINQ, no
    /// iterator and no intermediate collection; the only allocation is the caller's own reused list
    /// growing. Selection past <paramref name="maxRows"/> replaces the current farthest row rather
    /// than sorting the whole set, which is an O(N·k) worst case that never runs at the shipped
    /// numbers because k = 32 is never reached.
    /// </para>
    /// <para>
    /// A non-positive <paramref name="radius"/> means "no filter" — the whole registry, which is the
    /// pre-wave-8 behaviour and is kept reachable for an A/B. A non-positive
    /// <paramref name="heightMetres"/> means "any height"; a non-positive
    /// <paramref name="maxRows"/> means "no cap".
    /// </para>
    /// </summary>
    /// <returns>How many rows were appended.</returns>
    public int SelectPanelRows(
        in Vector3 centre,
        float radius,
        float heightMetres,
        int maxRows,
        List<GroundLootItem> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        int before = into.Count;
        bool byRadius = float.IsFinite(radius) && radius > 0f;
        bool byHeight = float.IsFinite(heightMetres) && heightMetres > 0f;
        bool capped = maxRows > 0;
        float radiusSquared = byRadius ? radius * radius : 0f;

        // Distance of the current farthest ACCEPTED row, so the cap can evict it for a nearer one.
        float farthestSquared = -1f;
        int farthestAt = -1;

        foreach (GroundLootItem item in _items.Values)
        {
            float dx = item.Position.X - centre.X;
            float dz = item.Position.Z - centre.Z;
            float distanceSquared = (dx * dx) + (dz * dz);
            if (byRadius && distanceSquared > radiusSquared)
            {
                continue;
            }

            if (byHeight && MathF.Abs(item.Position.Y - centre.Y) > heightMetres)
            {
                continue;
            }

            if (!capped || into.Count - before < maxRows)
            {
                into.Add(item);
                if (distanceSquared > farthestSquared)
                {
                    farthestSquared = distanceSquared;
                    farthestAt = into.Count - 1;
                }

                continue;
            }

            if (distanceSquared >= farthestSquared)
            {
                // The panel is full and this row is no nearer than the worst one already in it.
                continue;
            }

            into[farthestAt] = item;
            farthestSquared = -1f;
            farthestAt = -1;
            for (int i = before; i < into.Count; i++)
            {
                Vector3 position = into[i].Position;
                float ex = position.X - centre.X;
                float ez = position.Z - centre.Z;
                float squared = (ex * ex) + (ez * ez);
                if (squared > farthestSquared)
                {
                    farthestSquared = squared;
                    farthestAt = i;
                }
            }
        }

        return into.Count - before;
    }

    /// <summary>Allocates the instance guid that identifies the granted item inside the bag.</summary>
    internal Func<ulong>? ItemGuidAllocator { get; set; }

    public ulong NextItemGuid() => ItemGuidAllocator?.Invoke() ?? _nextItemGuid++;

    /// <summary>
    /// Drops every registration; used when a session zones into a different world. The id allocators
    /// are deliberately not rewound (see <see cref="IsLootGuid"/>), and neither is the evicted set:
    /// a guid from a previous world is still a loot guid and still was not claimed.
    /// </summary>
    public void Clear() => _items.Clear();
}
