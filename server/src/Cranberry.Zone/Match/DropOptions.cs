namespace Cranberry.Zone.Match;

/// <summary>
/// Every knob the random drop has, in one place (docs/48 §5.6). Defaults reproduce the design that
/// was simulated over 100,000 matches in docs/48 §6.
/// <para>
/// This is a lane-local record on purpose: <c>ZoneOptions</c> belongs to the Integrate lane, so the
/// drop ships its own settings object and the host wires one field. Nothing here is per-session.
/// </para>
/// </summary>
public sealed record DropOptions
{
    /// <summary>The shipped design.</summary>
    public static readonly DropOptions Default = new();

    /// <summary>
    /// False restores the pre-wave-4 behaviour exactly: the caller keeps using its own fixed
    /// <c>ZoneOptions.MatchDropSpawn</c> and never asks for a plan.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Absolute world Y of the air spawn, and <b>since D238 the release altitude the server
    /// actually ships</b>. <b>DERIVED from the client's own data</b>: <c>Z2Areas.xml</c> carries
    /// exactly one sky-spawn volume,
    /// <c>&lt;AreaDefinition id="429116895" name="KotK.SkySpawn" shape="box" x1="-2000" y1="845"
    /// z1="-2000" x2="2000" y2="855" z2="2000" rotY="-3.141593"/&gt;</c> — a bare 10 m slab with no
    /// <c>&lt;Property&gt;</c> child — so the retail sky spawn is an absolute <b>850 m</b> and not an
    /// offset above the terrain (docs/48 §2a, AUDIT-parachute R1).
    /// <para>
    /// <b>The owner's ruling of 2026-09-03</b> — "do whatever retail is; our current dropzone height
    /// is a guess" — resolves here. Retail's own release altitude is <b>unknown</b>: not published,
    /// not on disk, never measured (AUDIT-parachute R3). 850 m is the only altitude the client
    /// itself states, so it is the value that ships; wave 9's 1,454 m
    /// (<c>DescentTuning.Legacy36</c>) was a design number reverse-engineered from the friend's
    /// emulator's 1,500 m and is now the one-word revert.
    /// </para>
    /// <para>
    /// Honest grading: the client never <em>reads</em> the name — <c>exeview.py findstr</c> over
    /// <c>H1Z1.exe</c> finds no <c>SkySpawn</c> string — so 850 is level-designer metadata a retail
    /// server consumed. It is the best available design input, never citable as client behaviour.
    /// </para>
    /// </summary>
    public float SkySpawnAltitude { get; init; } = 850f;

    /// <summary>
    /// Floor on the fall: the air spawn is never closer than this to the ground under it. Designed,
    /// and on Z2 it never binds — the highest of the 92 anchors is 241.9 m, which still leaves
    /// 608 m of clearance under an 850 m slab. It exists so that pointing the server at a map whose
    /// ground is above ~450 m cannot spawn a player underneath the world.
    /// </summary>
    public float MinimumClearanceMetres { get; init; } = 400f;

    /// <summary>
    /// Preferred drop reach as a multiple of the first safe radius. At the measured 2000 m
    /// first target, 1.5 allows a 3000 m preferred drop region. This is a spawn placement
    /// allowance, not a gas radius; players can land outside the first safe zone. Full
    /// matches can expand their placement area before reducing opponent separation.
    /// </summary>
    public float RingFactor { get; init; } = 1.5f;

    /// <summary>
    /// Exponent on a place's marker count when drawing a drop location. With the corrected
    /// 2000 m first safe target, 0.25 keeps all 92 named places reachable over 10,000 seeded
    /// matches, the busiest place at 4.49%, and Pleasant Valley at 5.98%. The previous 0.3
    /// gave one place 5.14%, exceeding the existing 5% concentration guard. This remains
    /// project spawn weighting rather than a recovered retail formula.
    /// </summary>
    public float PoiWeightExponent { get; init; } = 0.25f;

    /// <summary>
    /// A place whose anchor is closer than this to the ±4,096 m terrain edge is not droppable.
    /// <b>Measured, not invented</b>: the closest of the 92 anchors (ChangsWildCampgrounds) is
    /// 202.8 m from the edge, so this excludes nothing today and simply pins the invariant against
    /// a future data change (docs/48 §4.4).
    /// </summary>
    public float MinimumEdgeMetres { get; init; } = 200f;

    /// <summary>Half-extent of the playable terrain, for <see cref="MinimumEdgeMetres"/>. Z2 is ±4,096 m (docs/29 §2.1).</summary>
    public float MapHalfExtentMetres { get; init; } = 4096f;

    /// <summary>
    /// How many of the chosen place's own markers to try before settling for its anchor. Retail
    /// scattered players <i>within</i> the spawn area, so the exact point must move between matches
    /// even when the place repeats; the attempts bound how fussy that draw is allowed to be.
    /// </summary>
    public int JitterAttempts { get; init; } = 4;

    /// <summary>
    /// A drawn marker is only accepted if at least this many markers lie within
    /// <see cref="LootRadius"/> of it. The anchor fallback always satisfies it: the worst anchor in
    /// the game (JayWildernessCamp) still has 122 markers at 80 m (docs/48 §4.3).
    /// <para>
    /// <b>That is a floor on MARKERS, not on items</b> (verify wave 4; the earlier claim that it
    /// makes docs/44 blocker 4 "NOTHING SPAWNED" <i>unreachable</i> was too strong). The count comes
    /// from <c>Z2LootSpawns</c> with <c>categoryMask = uint.MaxValue</c> and no live-set filter,
    /// while <c>ZoneService.SpawnRealGroundLoot</c> spawns from the density-rolled <b>live</b> set —
    /// so 64 markers is 64 candidate positions, and how many of them survive the roll is
    /// <c>LootDensityOptions</c>' business, not this one's. What this floor does guarantee is that
    /// the drop never lands somewhere the map itself has nothing to offer.
    /// </para>
    /// </summary>
    public int MinimumMarkers { get; init; } = 64;

    /// <summary>
    /// <b>D240 (AUDIT-parachute G5): the jitter floor is relative to the place's own anchor.</b> A
    /// jittered marker is accepted only when it has at least
    /// <c>max(MinimumMarkers, JitterFloorFraction x anchorNeighbours)</c> markers within
    /// <see cref="LootRadius"/>.
    /// <para>
    /// <b>The measurement that forced it.</b> On 2026-09-03 at 17:59 the drop chose
    /// PalaminoTrailsCampground — a place whose anchor has <b>472</b> markers within 80 m
    /// (docs/48 §4.5 row 57) — and then accepted a jittered marker with <b>74</b>. A 6.4x density
    /// loss, bought for nothing by an absolute floor of 64 that a rich place clears almost anywhere
    /// inside its own footprint. The anchor work exists precisely so that a chosen place delivers
    /// what its anchor promises; an absolute floor throws that away the moment the place is good.
    /// </para>
    /// <para>
    /// <b>Why a fraction and not a bigger absolute.</b> Raising <see cref="MinimumMarkers"/> would
    /// make poor places undroppable — the worst anchor in the game (JayWildernessCamp) has 122 — and
    /// retail scattered players over an <em>area</em>, so a place must stay able to use its own
    /// footprint. Half the anchor is scale-free: a 472-anchor place must find 236, a 122-anchor place
    /// still only needs 64. The anchor fallback is unaffected, because an anchor trivially satisfies
    /// half of itself.
    /// </para>
    /// <para>
    /// <c>0</c> restores the pre-D240 absolute-only floor exactly
    /// (<c>CRANBERRY_DROP_JITTER_FLOOR=0</c>).
    /// </para>
    /// </summary>
    public float JitterFloorFraction { get; init; } = 0.5f;

    /// <summary>
    /// The floor a jittered marker inside <paramref name="anchorNeighbours"/>-dense place must
    /// clear — <see cref="MinimumMarkers"/> raised by <see cref="JitterFloorFraction"/> of the
    /// place's own anchor. Written out so a test can read the rule without a planner.
    /// </summary>
    public int JitterFloorFor(int anchorNeighbours) =>
        Math.Max(MinimumMarkers, (int)MathF.Ceiling(JitterFloorFraction * MathF.Max(0, anchorNeighbours)));

    /// <summary>
    /// The radius the landing burst actually uses — keep it equal to
    /// <c>ZoneOptions.GroundLootRadius</c>. Anchors are chosen for this radius, so changing one
    /// without the other silently degrades the anchor table.
    /// </summary>
    public float LootRadius { get; init; } = 80f;

    /// <summary>
    /// How much to widen <see cref="RingFactor"/> per rung when no place is inside the first circle.
    /// Under the shipped gas settings this never runs (minimum 4 eligible places over 100,000
    /// matches, docs/48 §6); it exists for changed gas settings.
    /// </summary>
    public float WidenFactor { get; init; } = 1.5f;

    /// <summary>How many widening rungs before falling back to the place nearest the ring centre.</summary>
    public int WidenSteps { get; init; } = 4;

    /// <summary>Throws when a knob is outside the range the planner can honour.</summary>
    public void Validate()
    {
        Require(float.IsFinite(SkySpawnAltitude), nameof(SkySpawnAltitude), "must be finite");
        Require(MinimumClearanceMetres >= 0f && float.IsFinite(MinimumClearanceMetres), nameof(MinimumClearanceMetres), "must be finite and non-negative");
        Require(RingFactor > 0f && float.IsFinite(RingFactor), nameof(RingFactor), "must be positive");
        Require(PoiWeightExponent >= 0f && float.IsFinite(PoiWeightExponent), nameof(PoiWeightExponent), "must be finite and non-negative");
        Require(MinimumEdgeMetres >= 0f && float.IsFinite(MinimumEdgeMetres), nameof(MinimumEdgeMetres), "must be finite and non-negative");
        Require(MapHalfExtentMetres > 0f && float.IsFinite(MapHalfExtentMetres), nameof(MapHalfExtentMetres), "must be positive");
        Require(JitterAttempts >= 0, nameof(JitterAttempts), "must be non-negative");
        Require(MinimumMarkers >= 0, nameof(MinimumMarkers), "must be non-negative");
        Require(
            JitterFloorFraction >= 0f && JitterFloorFraction <= 1f && float.IsFinite(JitterFloorFraction),
            nameof(JitterFloorFraction),
            "must be finite and within 0 .. 1");
        Require(LootRadius > 0f && float.IsFinite(LootRadius), nameof(LootRadius), "must be positive");
        Require(WidenFactor >= 1f && float.IsFinite(WidenFactor), nameof(WidenFactor), "must be at least 1");
        Require(WidenSteps >= 0, nameof(WidenSteps), "must be non-negative");
    }

    private static void Require(bool condition, string name, string requirement)
    {
        if (!condition)
        {
            throw new ArgumentOutOfRangeException(name, $"DropOptions.{name} {requirement}.");
        }
    }
}
