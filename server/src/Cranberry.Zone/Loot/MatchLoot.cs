using System.Buffers;
using System.Numerics;
using Cranberry.Zone.World;

namespace Cranberry.Zone.Loot;

/// <summary>Which of the two ground-object identities a <see cref="LootStreamKey"/> names.</summary>
public enum LootStreamKeyKind
{
    /// <summary>An index into <see cref="Z2LootSpawns.Points"/> — one rolled marker.</summary>
    Marker,

    /// <summary>One ammunition box of a gun's pair, identified by <see cref="LootClusterItem.Key"/>.</summary>
    Box,

    /// <summary>
    /// An object the <b>player</b> put on the floor with a <c>RequestUseItem</c> drop, identified by
    /// its own world guid. It has no marker and no cluster behind it, so it is never a spawn
    /// candidate - the only thing this kind buys is that the streamer can <i>evict</i> it and that it
    /// counts against <see cref="LootStreamOptions.MaxLive"/> like every other ground object.
    /// </summary>
    Dropped,
}

/// <summary>
/// The identity of one streamable ground object, and the reason it carries an explicit
/// discriminator rather than living in a single <c>ulong</c> key space.
/// <para>
/// <b>The collision this avoids.</b> A marker is identified by its <i>index</i> (&lt; 168,322) and a
/// box by <c>(GunInstanceId &lt;&lt; 32) | BoxIndex</c>. <see cref="Z2LootLayout.ClusterFor"/>'s own
/// doc comment records why a box has no instance id of its own: <i>"Z2's marker ids are unique but
/// occupy the whole 32-bit range, so there is no spare bit to steal."</i> So a box key's high 32 bits
/// are an arbitrary <c>InstanceId</c> that <b>may be 0</b>, and a naive shared key space would make
/// the box <c>(instance 0, index 1)</c> and marker index 1 the same key — a picked-up box would then
/// permanently suppress an unrelated marker. docs/52 §3c calls for two separate sets;
/// <see cref="Kind"/> is that separation made explicit, and it is what lets one dictionary hold both
/// kinds with their positions. The taken sets below stay genuinely separate, as the doc asks.
/// </para>
/// </summary>
/// <param name="Kind">Marker or box.</param>
/// <param name="MarkerIndex">The marker index, for <see cref="LootStreamKeyKind.Marker"/>; 0 otherwise.</param>
/// <param name="BoxKey"><see cref="LootClusterItem.Key"/>, for <see cref="LootStreamKeyKind.Box"/>; 0 otherwise.</param>
public readonly record struct LootStreamKey(LootStreamKeyKind Kind, int MarkerIndex, ulong BoxKey)
{
    /// <summary>The identity of the rolled item standing on marker <paramref name="markerIndex"/>.</summary>
    public static LootStreamKey ForMarker(int markerIndex) =>
        new(LootStreamKeyKind.Marker, markerIndex, 0);

    /// <summary>The identity of one ammunition box, by its gun's instance id and its index in the pair.</summary>
    public static LootStreamKey ForBox(ulong boxKey) =>
        new(LootStreamKeyKind.Box, 0, boxKey);

    /// <summary>The identity of one ammunition box (<see cref="LootClusterItem.Key"/>).</summary>
    public static LootStreamKey ForBox(in LootClusterItem box) => ForBox(box.Key);

    /// <summary>
    /// The identity of an object a player dropped, by the world guid <c>LootWorld.Spawn</c> minted
    /// for it. <see cref="Kind"/> keeps it out of the box key space, so it can never collide with a
    /// <see cref="LootClusterItem.Key"/> whose high 32 bits happen to be 0.
    /// </summary>
    public static LootStreamKey ForDropped(ulong worldGuid) =>
        new(LootStreamKeyKind.Dropped, 0, worldGuid);

    public override string ToString() => Kind switch
    {
        LootStreamKeyKind.Marker => $"marker#{MarkerIndex}",
        LootStreamKeyKind.Dropped => $"dropped#{BoxKey}",
        _ => $"box#{BoxKey >> 32}/{(uint)BoxKey}",
    };
}

/// <summary>
/// One ground object a re-stream tick has decided to put on the wire. It carries everything
/// <c>ZoneService.SpawnGroundLoot</c> needs and nothing it does not — deliberately not a
/// <see cref="GroundLootItem"/>, because the world guid and transient id do not exist yet: they are
/// minted by <c>LootWorld.Spawn</c> when the burst action actually runs, and the caller hands them
/// back with <see cref="MatchLoot.NoteSpawned"/>.
/// </summary>
public readonly record struct LootStreamSpawn(
    LootStreamKey Key,
    uint ItemDefinitionId,
    uint GroundModelId,
    uint NameId,
    uint Count,
    Vector3 Position,
    bool IsAmmunitionBox);

/// <summary>
/// What one re-stream tick must send: the evictions <b>first</b>, then the spawns (docs/52 §3f).
/// The order is load-bearing — evicting first keeps the peak client-side object count at
/// <see cref="LootStreamOptions.MaxLive"/> rather than <c>MaxLive + delta</c>, and it front-loads the
/// 13-byte packets so the working set is back under the cap before the 477-byte ones land.
/// </summary>
public sealed class LootStreamPlan
{
    internal LootStreamPlan(
        IReadOnlyList<ulong> evictions,
        IReadOnlyList<LootStreamSpawn> spawns,
        int reapedReservations,
        int liveMarkersInRadius,
        int liveAfter,
        bool discExhausted,
        string? areaName,
        float nearestMetres,
        float farthestMetres,
        int panelRowCap = int.MaxValue,
        int deferredEvictions = 0,
        int expiredDrops = 0)
    {
        Evictions = evictions;
        Spawns = spawns;
        ReapedReservations = reapedReservations;
        LiveMarkersInRadius = liveMarkersInRadius;
        LiveAfter = liveAfter;
        DiscExhausted = discExhausted;
        AreaName = areaName;
        NearestMetres = nearestMetres;
        FarthestMetres = farthestMetres;
        PanelRowCap = panelRowCap;
        DeferredEvictions = deferredEvictions;
        ExpiredDrops = expiredDrops;
    }

    /// <summary>
    /// The most <c>f8 01</c> rows the republish that follows this plan can carry
    /// (<c>LootStreamOptions.MaxPanelRows</c>). Since wave 8 the panel is a 2 m ball rather than the
    /// whole working set, so the plan's cost is bounded by this and not by <see cref="LiveAfter"/>.
    /// </summary>
    public int PanelRowCap { get; }

    /// <summary>
    /// Objects this tick wanted to destroy but deferred to the next one, because
    /// <c>LootStreamOptions.MaxEvictionsPerRestream</c> bounded the tick. They stay in the working
    /// set and are re-offered to the next plan's eviction pass; at the shipped 384 ceiling against a
    /// 261 p90 disc a deferral cannot starve the same tick's spawns (docs/78 §3.4).
    /// </summary>
    public int DeferredEvictions { get; }

    /// <summary>
    /// Player-dropped objects destroyed because they outlived
    /// <c>LootStreamOptions.DroppedItemLifetimeMs</c>, counted separately from distance evictions
    /// for the log line. Always 0 at the shipped default, which is "never expire".
    /// </summary>
    public int ExpiredDrops { get; }

    /// <summary>World guids to destroy with <c>0f 01 RemovePlayer</c>, <c>effectFlag = 0</c>.</summary>
    public IReadOnlyList<ulong> Evictions { get; }

    /// <summary>Objects to spawn, nearest first, guns immediately followed by their own boxes.</summary>
    public IReadOnlyList<LootStreamSpawn> Spawns { get; }

    /// <summary>
    /// Reservations from an earlier tick that were dropped without a packet because they were never
    /// actually put on the wire and are now out of range. Non-zero means a burst was abandoned;
    /// worth a log line, never an error.
    /// </summary>
    public int ReapedReservations { get; }

    /// <summary>How many live markers the streamed disc holds in total, for the log line.</summary>
    public int LiveMarkersInRadius { get; }

    /// <summary>The working-set size once this plan has been executed. Never exceeds <c>MaxLive</c>.</summary>
    public int LiveAfter { get; }

    /// <summary>
    /// True when planning walked every candidate in the disc without ever being stopped by a budget.
    /// That is what switches the backfill arm off, so a player standing in a fully-streamed building
    /// costs one predicate per tick and no spatial query (docs/52 §3d).
    /// </summary>
    public bool DiscExhausted { get; }

    /// <summary>The first named POI a spawned marker sits in, for the log line. May be null.</summary>
    public string? AreaName { get; }

    /// <summary>Horizontal distance to the nearest spawned object, or 0 when nothing was spawned.</summary>
    public float NearestMetres { get; }

    /// <summary>Horizontal distance to the farthest spawned object, or 0 when nothing was spawned.</summary>
    public float FarthestMetres { get; }

    /// <summary>Did this tick change the ground world — i.e. must <c>f8 01</c> be republished.</summary>
    public bool ChangedTheWorld => Evictions.Count > 0 || Spawns.Count > 0;

    /// <summary>
    /// What this plan costs on the link, by <see cref="LootStreamOptions.EstimateTickBytes"/>. A plan
    /// that changed nothing costs nothing, republish included.
    /// </summary>
    public int EstimatedBytes => LootStreamOptions.EstimateTickBytes(
        Spawns.Count,
        Evictions.Count,
        ChangedTheWorld ? Math.Min(PanelRowCap, LiveAfter) : -1);

    /// <summary>The empty plan a waiting tick produces.</summary>
    public static LootStreamPlan Empty { get; } =
        new([], [], 0, 0, 0, discExhausted: false, null, 0f, 0f);
}

/// <summary>
/// <b>The per-match streamed ground loot: the fix for "loot only spawned where I landed".</b>
///
/// <para><b>What was wrong.</b> <c>ZoneService.ArmGroundLoot</c> gated its real loot arm on
/// <c>state.RealGroundLootArmed</c>, a one-way latch cleared only by zoning or a match teardown, and
/// <c>ArmGroundLoot</c> itself runs once per match — so 128 objects went out at the parachute
/// touchdown and the other 39,708 items on the Z2 floor were never mentioned again. Wave 4 freed the
/// <i>doors</i> from the identical latch by replacing it with a distance predicate plus a
/// self-re-arming timer; ground loot kept the latch. The owner's own play-test
/// (<c>logs/host-20260830-090725.log</c>, session <c>25109cb9</c>) shows exactly one loot burst
/// against twenty door re-streams over ~400 m of travel (docs/52 §1).</para>
///
/// <para><b>What this is.</b> A bounded working set of ground objects that follows the player: on
/// each tick of the shared world pump it evicts what has fallen outside
/// <see cref="LootStreamOptions.DespawnRadiusMetres"/> and spawns the nearest live markers inside
/// <see cref="LootStreamOptions.StreamRadiusMetres"/> that are neither already out nor already taken,
/// capped at <see cref="LootStreamOptions.MaxPerRestream"/> objects this tick and
/// <see cref="LootStreamOptions.MaxLive"/> in total. The caps are chosen so the peak stays at the
/// working-set size the August client has already accepted live, so the streamer churns inside a
/// proven budget rather than enlarging one (docs/52 §4c).</para>
///
/// <para><b>The rule that separates loot from doors.</b> A door is permanent and idempotent by
/// dataset index; an item is <i>destroyed by being picked up and must never come back</i>. Without
/// that, looting a house, walking 90 m away and returning finds it restocked. That is why eviction
/// and pickup are different operations here: eviction frees the key to be streamed again, a pickup
/// retires it for the match. It is also why the two areas share the tick and the pacing
/// (<see cref="WorldStream.NextStep"/>, <c>DrainBurst</c>) but not the class (docs/52 §2c).</para>
///
/// <para><b>The landing burst must be adopted.</b> <c>SpawnRealGroundLoot</c> spawns its 128 objects
/// without going through this type. Every one of them has to be handed to
/// <see cref="NoteSpawned"/> — otherwise the working set starts empty, the first re-stream tick sees
/// 128 unstreamed markers inside its own disc and spawns every one of them a second time. That
/// adoption is the single most load-bearing line of the integration, and
/// <c>LootStreamingTests.AdoptingTheLandingBurstIsWhatStopsTheFirstTickDuplicatingIt</c> pins it.</para>
///
/// <para><b>Threading.</b> Exactly like <c>MatchDoors</c>: one instance per session, touched only
/// from that session's listener thread (the pump's <c>Later</c> chain and the packet handlers all run
/// there). Nothing here is synchronised.</para>
/// </summary>
public sealed class MatchLoot
{
    /// <summary>
    /// One object this match has put on the client, or reserved a slot for.
    /// <para>
    /// <paramref name="SpawnedAtMs"/> is <c>Environment.TickCount64</c> at the moment the caller
    /// confirmed the spawn, and is read by exactly one rule:
    /// <c>LootStreamOptions.DroppedItemLifetimeMs</c>, which is off by default. It is 0 for a
    /// reservation that has not reached the wire, and for every object streamed before a clock was
    /// ever passed in — both of which the expiry pass treats as "no age known, never expire".
    /// </para>
    /// </summary>
    private readonly record struct Entry(ulong WorldGuid, Vector3 Position, long SpawnedAtMs = 0);

    private readonly Dictionary<LootStreamKey, Entry> _entries = [];
    private readonly Dictionary<ulong, LootStreamKey> _byGuid = [];

    /// <summary>
    /// Markers whose item has been picked up this match. Separate from <see cref="_takenBoxes"/>
    /// because the two key spaces are not comparable (see <see cref="LootStreamKey"/>).
    /// </summary>
    private readonly HashSet<int> _takenMarkers = [];
    public SharedLootClaims? SharedClaims { get; set; }
    private readonly Dictionary<LootStreamKey, uint> _remainingCounts = [];

    /// <summary>Read at spawn time so queued and later viewers see only the unlooted remainder.</summary>
    public uint RemainingCount(in LootStreamKey key, uint original) => SharedClaims is { } shared
        ? shared.RemainingCount(key, original) : _remainingCounts.GetValueOrDefault(key, original);

    public void NoteRemaining(in LootStreamKey key, uint remaining)
    {
        if (remaining == 0 || IsTaken(key)) throw new ArgumentOutOfRangeException(nameof(remaining));
        if (SharedClaims is { } shared) shared.NoteRemaining(key, remaining);
        else _remainingCounts[key] = remaining;
    }

    /// <summary>Ammunition boxes picked up this match, by <see cref="LootClusterItem.Key"/>.</summary>
    private readonly HashSet<ulong> _takenBoxes = [];

    /// <summary>
    /// Guids <see cref="PlanRestream"/> has evicted from the working set but whose <c>0f 01</c> has
    /// not necessarily left the server yet — the plan commits synchronously while <c>DrainBurst</c>
    /// pays the packets out over slices ~40 ms apart.
    /// <para>
    /// It exists so that <see cref="NoteTaken(ulong)"/> still works inside that window. Without it a
    /// pickup landing between the plan and its slice finds the guid already gone from
    /// <see cref="_byGuid"/>, silently fails to retire the MARKER, and the item comes back the next
    /// time the player walks past — the "a looted house restocks itself" failure the taken sets
    /// exist to prevent. At the shipped 90 m despawn radius the window is unreachable (the object is
    /// being evicted precisely because the player is 90 m from it, and <c>[F]</c> needs proximity),
    /// but that is a numeric coincidence between two independently configurable radii, not a rule.
    /// </para>
    /// <para>Holds at most one tick's evictions: it is cleared at the top of every plan.</para>
    /// </summary>
    private readonly Dictionary<ulong, LootStreamKey> _pendingEvictions = [];

    private Vector3 _lastBurstCentre;
    private bool _hasStreamed;
    private bool _discExhausted;

    /// <summary>Ground objects this match holds a slot for — on the wire or reserved by a plan.</summary>
    public int LiveCount => _entries.Count;

    /// <summary>Of those, the ones whose spawn packets have actually been sent.</summary>
    public int OnWireCount => _byGuid.Count;

    /// <summary>Markers retired by a pickup this match. They never come back.</summary>
    public int TakenMarkerCount => _takenMarkers.Count;

    /// <summary>Ammunition boxes retired by a pickup this match.</summary>
    public int TakenBoxCount => _takenBoxes.Count;

    /// <summary>Objects this match has streamed out again, cumulative, for the log line.</summary>
    public long EvictedCount { get; private set; }

    /// <summary>Objects this match has streamed in, cumulative, for the log line.</summary>
    public long StreamedCount { get; private set; }

    /// <summary>How many re-stream bursts have run, for the log line.</summary>
    public int BurstCount { get; private set; }

    /// <summary>Whether any burst has been planned for this match yet.</summary>
    public bool HasStreamed => _hasStreamed;

    /// <summary>Centre of the most recent burst, the anchor <see cref="ShouldRestream"/> measures from.</summary>
    public Vector3 LastBurstCentre => _lastBurstCentre;

    /// <summary>
    /// True when the last plan covered its whole disc without hitting a budget, so there is nothing
    /// left to backfill until the player moves.
    /// </summary>
    public bool DiscExhausted => _discExhausted;

    /// <summary>Is this key currently on the client, or reserved by a plan that is still draining?</summary>
    public bool IsStreamed(in LootStreamKey key) => _entries.ContainsKey(key);

    /// <summary>
    /// Has this key been picked up this match? Always false for
    /// <see cref="LootStreamKeyKind.Dropped"/>: a dropped object's key is minted from a world guid
    /// that <c>LootWorld.NextWorldGuid</c> never reuses, so it is never re-offered and there is
    /// nothing to retire.
    /// </summary>
    public bool IsTaken(in LootStreamKey key) => SharedClaims?.IsTaken(key) == true || key.Kind switch
    {
        LootStreamKeyKind.Marker => _takenMarkers.Contains(key.MarkerIndex),
        LootStreamKeyKind.Dropped => false,
        _ => _takenBoxes.Contains(key.BoxKey),
    };

    /// <summary>The key behind a world guid, if this match streamed it.</summary>
    public bool TryGetKey(ulong worldGuid, out LootStreamKey key) =>
        _byGuid.TryGetValue(worldGuid, out key);

    public bool TryGetClaimKey(ulong worldGuid, out LootStreamKey key) =>
        _byGuid.TryGetValue(worldGuid, out key) || _pendingEvictions.TryGetValue(worldGuid, out key);

    /// <summary>
    /// The world guid this key is currently on the client as. False for a key that is only reserved
    /// — a plan has claimed it but its spawn packets have not gone out yet, so there is no guid.
    /// </summary>
    public bool TryGetGuid(in LootStreamKey key, out ulong worldGuid)
    {
        if (_entries.TryGetValue(key, out Entry entry) && entry.WorldGuid != 0)
        {
            worldGuid = entry.WorldGuid;
            return true;
        }

        worldGuid = 0;
        return false;
    }

    public ulong FindVisibleGuid(in LootStreamKey key)
    {
        if (TryGetGuid(key, out ulong guid)) return guid;
        foreach (var pending in _pendingEvictions)
            if (pending.Value == key) return pending.Key;
        return 0;
    }

    public bool TryReserveDrop(in LootStreamKey key, in Vector3 position, int maxLive)
    {
        if (key.Kind != LootStreamKeyKind.Dropped || IsTaken(key)
            || _entries.Count >= maxLive || _entries.ContainsKey(key)) return false;
        Reserve(key, position);
        return true;
    }

    /// <summary>
    /// Whether another ground-loot burst is due at <paramref name="centre"/>. Two arms, and both are
    /// needed:
    /// <list type="number">
    /// <item><b>Movement.</b> True before the first burst, and thereafter once the player has moved
    /// more than <see cref="LootStreamOptions.RestreamFraction"/> × the stream radius — 30 m at the
    /// shipped 60 m, horizontally only, exactly as <c>MatchDoors.ShouldRestream</c> does it. Height is
    /// ignored because an item is reachable from the floor it is on and Z2's Y range would otherwise
    /// re-stream on every step down a hillside.</item>
    /// <item><b>Backfill.</b> True while the working set is under <see cref="LootStreamOptions.MaxLive"/>
    /// and the last plan did <i>not</i> exhaust its disc. This is what repairs the landing burst,
    /// which truncates inside its own 80 m disc in every measured session — 114 to 222 live markers
    /// found against a cap of 128 including boxes, so even the building the player lands next to is
    /// only about 60 % stocked (docs/52 §1c). Without this arm a stationary player would wait for the
    /// 30 m threshold before that building filled in.</item>
    /// </list>
    /// The backfill arm asks no spatial question: <see cref="DiscExhausted"/> is set by the previous
    /// plan, so a player standing in a fully-streamed building costs one comparison per tick.
    /// </summary>
    public bool ShouldRestream(in Vector3 centre, LootStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!_hasStreamed)
        {
            return true;
        }

        float radius = options.StreamRadiusMetres;
        float fraction = options.RestreamFraction;
        if (!float.IsFinite(radius) || radius <= 0f
            || !float.IsFinite(fraction) || fraction <= 0f)
        {
            // The explicit "stream once per match" setting — wave 4's behaviour, kept reachable so a
            // regression can be A/B'd against it. The backfill arm goes with it: half a streamer is
            // harder to reason about than none.
            return false;
        }

        float threshold = radius * fraction;
        float dx = centre.X - _lastBurstCentre.X;
        float dz = centre.Z - _lastBurstCentre.Z;
        if ((dx * dx) + (dz * dz) > threshold * threshold)
        {
            return true;
        }

        return !_discExhausted && _entries.Count < options.MaxLive;
    }

    /// <summary>
    /// The decision one tick of the shared world pump makes for the loot arm, mirroring
    /// <c>MatchDoors.NextPumpStep</c> so the two can be read side by side.
    /// <para>
    /// <paramref name="loot"/> is <b>null until the landing burst has run</b> — the per-match state
    /// is created lazily inside the burst, which rides <c>ZoneOptions.GroundLootDelayMs</c>, while the
    /// pump is armed at <see cref="LootStreamOptions.RestreamIntervalMs"/>. At the shipped defaults
    /// 3000 &gt; 2000 and the burst wins by a second, but the interval is host-overridable and the
    /// delay is not, so that case must be <see cref="WorldStreamStep.Wait"/> and never
    /// <see cref="WorldStreamStep.Stop"/>: stopping there would silently disable the streamer for the
    /// whole match, which is the exact wave-4 defect the verify pass found in the door pump.
    /// </para>
    /// <para>
    /// <b><paramref name="landingBurstDrained"/> is the guard that actually bites.</b> The
    /// <paramref name="loot"/> null test above is real for a caller that holds the state lazily, but
    /// <c>GatewaySessionState.StreamedLoot</c> is a non-null initialiser, so for the shipping caller
    /// it is a compile-time <c>true</c>. The condition that matters there is a different one: the
    /// landing burst does not go through <see cref="PlanRestream"/> and adopts itself into the
    /// working set only as <c>DrainBurst</c> pays it out, ~440 ms after <c>GroundLootDelayMs</c>. A
    /// pump tick landing inside that window sees an empty working set, re-offers markers the landing
    /// burst is about to spawn, and every such duplicate is a world object the client keeps forever —
    /// <see cref="NoteSpawned"/> can only keep one guid per key. So this too is a
    /// <see cref="WorldStreamStep.Wait"/>, for the same reason and never a
    /// <see cref="WorldStreamStep.Stop"/>.
    /// </para>
    /// </summary>
    public static WorldStreamStep NextPumpStep(
        bool inMatch,
        bool sendLoot,
        MatchLoot? loot,
        Vector3? centre,
        LootStreamOptions options,
        bool landingBurstDrained = true)
    {
        ArgumentNullException.ThrowIfNull(options);

        return WorldStream.NextStep(
            inMatch,
            sendLoot && options.Enabled,
            options.RestreamIntervalMs,
            loot is not null && landingBurstDrained,
            centre,
            options.StreamRadiusMetres,
            (point, _) => loot!.ShouldRestream(point, options));
    }

    /// <summary>
    /// Plans one re-stream tick: what to destroy, then what to spawn (docs/52 §3a).
    ///
    /// <para><b>This method commits.</b> Evicted entries are removed and spawned keys are reserved
    /// here, not when the caller sends the packets, because the caller sends them over four
    /// <c>DrainBurst</c> slices spread across ~160 ms and a second plan must not be able to re-offer
    /// what the first one already owns. The caller must therefore actually run the plan;
    /// <c>DrainBurst</c> does, even when it has no listener-thread dispatcher to pace with. A
    /// reservation that is nevertheless abandoned is self-healing: it is reaped, without a packet,
    /// by the first later tick that finds it out of range (<see cref="LootStreamPlan.ReapedReservations"/>).</para>
    ///
    /// <para><b>A gun and its boxes are one indivisible set.</b> A firearm is only taken when the
    /// tick has room for it <i>and</i> for the ammunition boxes it still needs — otherwise the budget
    /// would be spent leaving guns on the floor with no rounds beside them, which docs/39 §5 calls an
    /// inert prop, and the working set could overshoot <see cref="LootStreamOptions.MaxLive"/> by two.
    /// A set that does not fit is stepped over rather than ending the pass, so a lone bandage behind
    /// an AR-15 still gets streamed.</para>
    /// </summary>
    /// <param name="centre">Where the player is standing now.</param>
    /// <param name="layout">This match's decided floor.</param>
    /// <param name="options">Radii and caps.</param>
    /// <param name="spawnBudget">
    /// An extra ceiling on new objects, for a caller that has a reason of its own to hold back — the
    /// transient-id headroom check of docs/52 §4f (<c>LootWorld.TransientIdHeadroom</c>) is the
    /// intended one. Defaults to no extra limit.
    /// </param>
    /// <param name="nowMs">
    /// <c>Environment.TickCount64</c>, or 0 for "no clock". Only read when
    /// <see cref="LootStreamOptions.DroppedItemLifetimeMs"/> is non-zero, which it is not by
    /// default.
    /// </param>
    public LootStreamPlan PlanRestream(
        in Vector3 centre,
        Z2LootLayout layout,
        LootStreamOptions options,
        int spawnBudget = int.MaxValue,
        long nowMs = 0)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(options);

        // ---- 1. Evict, first, so the working set is under the cap before anything is added.
        float despawn = options.EffectiveDespawnRadiusMetres;
        float despawnSquared = despawn * despawn;
        // WAVE 8: the tick's eviction budget. Uncapped before, on the argument that a deferred
        // eviction keeps MaxLive full and starves the same tick's spawns. That argument depended on
        // the cap binding; at the owner's density the 60 m disc is 261 p90 objects against a 384
        // ceiling, so a deferral now leaves ~120 slots of headroom and cannot starve anything
        // (docs/78 §3.4). Non-positive restores "no cap" exactly.
        int evictionBudget = options.MaxEvictionsPerRestream > 0
            ? options.MaxEvictionsPerRestream
            : int.MaxValue;
        // The player-drop expiry (LootStreamOptions.DroppedItemLifetimeMs). Off by default, and it
        // can only ever touch LootStreamKeyKind.Dropped: a marker item must not expire on a timer,
        // because its marker is retired for the match by a pickup and by nothing else, so an expired
        // one would leave a hole no later sweep could fill.
        long dropLifetime = options.DroppedItemLifetimeMs > 0 && nowMs > 0
            ? options.DroppedItemLifetimeMs
            : 0;
        // Last tick's destroys have been drained by now (DrainBurst always runs the plan it was
        // handed, even with no dispatcher to pace with), so the window this map covers is one tick.
        _pendingEvictions.Clear();
        var evictions = new List<ulong>();
        List<LootStreamKey>? drop = null;
        int reaped = 0;
        int deferred = 0;
        int expired = 0;

        foreach ((LootStreamKey key, Entry entry) in _entries)
        {
            float ex = entry.Position.X - centre.X;
            float ez = entry.Position.Z - centre.Z;
            bool outOfRange = (ex * ex) + (ez * ez) > despawnSquared;
            bool tooOld = dropLifetime > 0
                && key.Kind == LootStreamKeyKind.Dropped
                && entry.WorldGuid != 0
                && entry.SpawnedAtMs > 0
                && nowMs - entry.SpawnedAtMs >= dropLifetime;

            if (!outOfRange && !tooOld)
            {
                continue;
            }

            if (drop is not null && drop.Count >= evictionBudget)
            {
                // Over budget: leave it in the working set and let the next tick take it. Counted so
                // the log line can say the tick was bounded rather than the world being stable.
                deferred++;
                continue;
            }

            drop ??= [];
            drop.Add(key);
            if (tooOld && !outOfRange)
            {
                expired++;
            }

            if (entry.WorldGuid != 0)
            {
                evictions.Add(entry.WorldGuid);
            }
            else
            {
                // Reserved by a plan that never reached the wire. Nothing to destroy client-side;
                // just give the marker back. The window is one burst (~160 ms) against a 90 m
                // despawn radius, so a live player cannot outrun a burst into this branch.
                reaped++;
            }
        }

        if (drop is not null)
        {
            foreach (LootStreamKey key in drop)
            {
                if (_entries.Remove(key, out Entry gone) && gone.WorldGuid != 0)
                {
                    _byGuid.Remove(gone.WorldGuid);
                    // Retirement by guid must keep working until the destroy has actually gone out.
                    _pendingEvictions[gone.WorldGuid] = key;
                    EvictedCount++;
                }
            }
        }

        // ---- 2. Spawn the nearest live markers the client does not already have.
        var spawns = new List<LootStreamSpawn>();
        // WAVE 8: the tick is budgeted in BYTES (his StreamBudgetBytesPerSweep = 22,000), with the
        // count kept only as a hard ceiling. A mixed sweep's cost is not proportional to its count —
        // a spawn is 477 B and an eviction 13 — and the loot arm will share this burst with doors
        // and vehicles, whose objects cost different amounts again. Non-positive switches the byte
        // budget off and leaves MaxPerRestream as the only bound.
        int byteBudget = options.RestreamByteBudget > 0 ? options.RestreamByteBudget : int.MaxValue;
        int perTick = Math.Max(0, Math.Min(options.MaxPerRestream, spawnBudget));
        int room = Math.Max(0, options.MaxLive - _entries.Count);
        int window = Math.Max(1, options.QueryWindow);
        string? area = null;
        float nearest = float.MaxValue;
        float farthest = 0f;
        bool budgetLimited = false;
        int matched = 0;

        if (perTick > 0 && room > 0)
        {
            int[] rented = ArrayPool<int>.Shared.Rent(window);
            try
            {
                Span<int> found = rented.AsSpan(0, window);
                // QueryLive, not QueryNearest: the nearest markers CARRYING AN ITEM this match. With
                // the density gate on, three quarters of the markers are empty, so a plain
                // nearest-query would fill the tick's budget with holes and then truncate.
                matched = layout.QueryLive(centre, options.StreamRadiusMetres, found);
                int usable = Math.Min(matched, window);
                Span<LootClusterItem> cluster = stackalloc LootClusterItem[2];

                for (int i = 0; i < usable; i++)
                {
                    if (spawns.Count >= perTick || spawns.Count >= room)
                    {
                        budgetLimited = true;
                        break;
                    }

                    // The byte gate is tested BEFORE the set is taken, never in the middle of one: a
                    // firearm and its two boxes are indivisible (docs/39 §5), so a set that starts
                    // under the budget finishes even if it ends two objects over it. That overshoot
                    // is exactly what LootStreamOptions.MaxSpawnsPerRestream accounts for.
                    if (spawns.Count * LootStreamOptions.SpawnBytes >= byteBudget)
                    {
                        budgetLimited = true;
                        break;
                    }

                    int index = found[i];
                    LootStreamKey key = LootStreamKey.ForMarker(index);
                    bool needsMarker = !_entries.ContainsKey(key) && !IsTaken(key);

                    if (!layout.TryGet(index, out LootSpawnRoll roll))
                    {
                        // Belt and braces: QueryLive already filtered these. A marker is empty when it
                        // lost the density gate, when its room was full, or when its category rolls
                        // nothing at all (FireExtinguisher — no such item in the August client).
                        continue;
                    }

                    int pair = options.SendClusters ? layout.ClusterFor(roll, cluster) : 0;
                    int wanted = needsMarker ? 1 : 0;
                    for (int box = 0; box < pair; box++)
                    {
                        LootStreamKey boxKey = LootStreamKey.ForBox(cluster[box]);
                        if (!_entries.ContainsKey(boxKey) && !IsTaken(boxKey))
                        {
                            wanted++;
                        }
                    }

                    if (wanted == 0) continue;
                    if (spawns.Count + wanted > perTick || spawns.Count + wanted > room)
                    {
                        // The whole set does not fit. Step over it rather than stopping: a smaller
                        // item further down the list still can, and the gun will be first in line
                        // next tick.
                        budgetLimited = true;
                        continue;
                    }

                    area ??= layout.Spawns.AreaNameOf(layout.Spawns[index]);

                    float dx = roll.Position.X - centre.X;
                    float dz = roll.Position.Z - centre.Z;
                    float distance = MathF.Sqrt((dx * dx) + (dz * dz));
                    nearest = MathF.Min(nearest, distance);
                    farthest = MathF.Max(farthest, distance);

                    if (needsMarker)
                    {
                        Reserve(key, roll.Position);
                        spawns.Add(new LootStreamSpawn(
                            key,
                            roll.ItemDefinitionId,
                            roll.GroundModelId,
                            roll.NameId,
                            roll.Count,
                            roll.Position,
                            IsAmmunitionBox: false));
                    }

                    for (int box = 0; box < pair; box++)
                    {
                        LootClusterItem ammunition = cluster[box];
                        LootStreamKey boxKey = LootStreamKey.ForBox(ammunition);
                        if (_entries.ContainsKey(boxKey) || IsTaken(boxKey))
                        {
                            continue;
                        }

                        Reserve(boxKey, ammunition.Position);
                        spawns.Add(new LootStreamSpawn(
                            boxKey,
                            ammunition.ItemDefinitionId,
                            ammunition.GroundModelId,
                            ammunition.NameId,
                            ammunition.Count,
                            ammunition.Position,
                            IsAmmunitionBox: true));
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
        else
        {
            // No room and no query: the set is full, which is itself a budget limit — the disc may
            // well hold more, so the backfill arm must not be switched off by it.
            budgetLimited = true;
        }

        // The disc is covered only when the pass walked every candidate in it AND the query window
        // was big enough to show them all. Either failure leaves the backfill arm armed.
        _discExhausted = !budgetLimited && matched <= window;
        StreamedCount += spawns.Count;
        NoteStreamed(centre);

        return new LootStreamPlan(
            evictions,
            spawns,
            reaped,
            matched,
            _entries.Count,
            _discExhausted,
            area,
            spawns.Count == 0 ? 0f : nearest,
            spawns.Count == 0 ? 0f : farthest,
            options.MaxPanelRows,
            deferred,
            expired);
    }

    /// <summary>
    /// Records that <paramref name="key"/> is now on the client as world object
    /// <paramref name="worldGuid"/>.
    /// <para>
    /// Called for every object of a re-stream burst <b>and for every object of the landing burst</b>.
    /// The landing burst does not go through <see cref="PlanRestream"/>, so without this call the
    /// working set would start empty and the first re-stream tick — whose 60 m disc lies inside the
    /// landing's 80 m one — would spawn all 128 of them a second time.
    /// </para>
    /// </summary>
    /// <returns>
    /// <c>false</c> when this key is <b>already</b> on the client under a different guid, i.e. the
    /// caller has just minted a duplicate world object. <b>The caller must then destroy it.</b>
    /// Returning void here is the wave-5 verify defect: the second guid was dropped on the floor
    /// while its <c>d6</c>/<c>da</c>/<c>ea 04</c> had already gone out, so the object stayed on the
    /// client, in every <c>ProximateItems</c> republish and visually doubled on top of its twin,
    /// with nothing left holding its identity that could ever evict it.
    /// </returns>
    /// <param name="nowMs">
    /// <c>Environment.TickCount64</c>, or 0 for "no clock" — the object then never expires on a
    /// timer, which is what every caller wants while
    /// <c>LootStreamOptions.DroppedItemLifetimeMs</c> is 0.
    /// </param>
    public bool NoteSpawned(in LootStreamKey key, ulong worldGuid, in Vector3 position, long nowMs = 0)
    {
        ArgumentOutOfRangeException.ThrowIfZero(worldGuid);
        if (IsTaken(key)) return false;

        if (_entries.TryGetValue(key, out Entry existing) && existing.WorldGuid != 0)
        {
            // Already on the client. Keep the first guid: a second one would leave the client holding
            // two objects in one place, and only the newer of them could ever be destroyed.
            return existing.WorldGuid == worldGuid;
        }

        _entries[key] = new Entry(worldGuid, position, nowMs);
        _byGuid[worldGuid] = key;
        return true;
    }

    /// <summary>
    /// Retires a key for the rest of the match: it has been picked up, and an item that has been
    /// picked up must never come back. This is the one rule that separates loot from doors — without
    /// it, looting a house, walking 90 m away and returning finds the house restocked.
    /// </summary>
    public bool NoteTaken(ulong worldGuid)
    {
        if (!_byGuid.Remove(worldGuid, out LootStreamKey key))
        {
            // Evicted by a plan whose destroy slice has not run yet: the guid is out of _byGuid but
            // the object is still on the client and still claimable, so the MARKER must still retire.
            if (!_pendingEvictions.Remove(worldGuid, out key))
            {
                return false;
            }

            NoteTaken(key);
            return true;
        }

        _entries.Remove(key);
        NoteTaken(key);
        return true;
    }

    /// <summary>Retires a key by identity rather than by guid.</summary>
    public void NoteTaken(in LootStreamKey key)
    {
        _remainingCounts.Remove(key);
        // A competing pickup may arrive while a spawn slice is still queued.
        if (_entries.TryGetValue(key, out Entry reserved) && reserved.WorldGuid == 0)
            _entries.Remove(key);
        if (key.Kind == LootStreamKeyKind.Marker)
        {
            _takenMarkers.Add(key.MarkerIndex);
        }
        else if (key.Kind == LootStreamKeyKind.Box)
        {
            _takenBoxes.Add(key.BoxKey);
        }

        // A Dropped key retires nothing: its "marker" is a one-off world guid that is never offered
        // again. Putting it in _takenBoxes would poison an unrelated LootClusterItem.Key.

        // A taken marker frees its slot in the working set but is never offered again, so the disc
        // may now have room the last plan did not know about.
        _discExhausted = false;
    }

    /// <summary>
    /// Drops a streamed object without retiring its key, for a caller that evicts by a rule of its
    /// own. <see cref="PlanRestream"/> has already done this for the evictions it returns, so this is
    /// idempotent and returns false for a guid the plan already accounted for.
    /// </summary>
    public bool NoteEvicted(ulong worldGuid)
    {
        if (!_byGuid.Remove(worldGuid, out LootStreamKey key))
        {
            return false;
        }

        _entries.Remove(key);
        EvictedCount++;
        _discExhausted = false;
        return true;
    }

    /// <summary>
    /// Records that a burst ran at <paramref name="centre"/>. <see cref="PlanRestream"/> does this
    /// itself — including for a burst that spawned nothing, so a player standing in an
    /// already-streamed street does not re-query the grid every tick.
    /// </summary>
    public void NoteStreamed(in Vector3 centre)
    {
        _lastBurstCentre = centre;
        _hasStreamed = true;
        BurstCount++;
    }

    /// <summary>Drops everything, including the taken sets; used when a session leaves a world.</summary>
    public void Clear()
    {
        SharedClaims = null;
        _entries.Clear();
        _byGuid.Clear();
        _takenMarkers.Clear();
        _takenBoxes.Clear();
        _remainingCounts.Clear();
        _pendingEvictions.Clear();
        _lastBurstCentre = default;
        _hasStreamed = false;
        _discExhausted = false;
        BurstCount = 0;
        EvictedCount = 0;
        StreamedCount = 0;
    }

    private void Reserve(in LootStreamKey key, in Vector3 position) =>
        _entries[key] = new Entry(0, position);
}
