using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Loot;

/// <summary>
/// Tuning for the ground-loot streamer (docs/52 §3, §4). Every value here is Cranberry's own choice
/// <b>[design]</b>, but none of them is arbitrary: each one is the arithmetic in docs/52 §4 applied
/// to bytes measured on this server's own wire, and each doc comment records that measurement so a
/// later lane can re-derive the number rather than re-guess it.
/// <para>
/// <b>The problem these numbers solve.</b> Before wave 5 the whole match's ground loot was one burst
/// at the parachute touchdown: <c>state.RealGroundLootArmed</c> was a one-way latch, so the other
/// 39,708 items on the Z2 floor were never mentioned again and every building the player entered
/// after the first was empty by construction (docs/52 §1, and the owner's own play-test
/// <c>logs/host-20260830-090725.log</c> — one loot burst against twenty door re-streams in the same
/// match).
/// </para>
/// </summary>
public sealed class LootStreamOptions
{
    /// <summary>The shipped defaults, which are the values docs/52 §4 argues for.</summary>
    public static LootStreamOptions Default { get; } = new();

    /// <summary>Is the streamer on at all. Off leaves wave 4's single landing burst exactly as it was.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Radius of the streamed disc, in metres.
    /// <para>
    /// 60 m is a <b>strict subset</b> of the untouched 80 m landing burst
    /// (<c>ZoneOptions.GroundLootRadius</c>), which is what makes the streamer purely additive: the
    /// first re-stream tick after a touchdown can spawn nothing the landing has not already sent, so
    /// there is no double-spawn and no wasted bytes on the one loot shape this client has accepted
    /// live. It is also exactly <c>ZoneOptions.DoorRadius</c>, so one 30 m movement threshold and one
    /// distance computation serve both arms of the shared pump (docs/52 §3b).
    /// </para>
    /// </summary>
    public float StreamRadiusMetres { get; init; } = Rulings.LootGates.StreamRadiusMetres;

    /// <summary>
    /// Beyond this distance a streamed object is destroyed on the client with a 13-byte
    /// <c>0f 01 RemovePlayer</c> (docs/52 §5a: the entity-destroy call sits <i>outside</i> the
    /// <c>effectFlag == 1</c> branch, so <c>effectFlag = 0</c> is a full, silent destroy).
    /// <para>
    /// 90 m = 1.5 × <see cref="StreamRadiusMetres"/>, and the hysteresis is <b>mandatory</b>: with
    /// the despawn radius equal to the stream radius a player standing on the boundary would spawn
    /// and destroy the same item every tick, forever, at up to 640 B a go. The 30 m dead band is exactly
    /// the re-stream threshold, so an item can never be spawned and evicted by the same movement
    /// that triggered the burst (docs/52 §3e).
    /// </para>
    /// </summary>
    public float DespawnRadiusMetres { get; init; } = Rulings.LootGates.DespawnRadiusMetres;

    /// <summary>
    /// <see cref="DespawnRadiusMetres"/> floored at <see cref="StreamRadiusMetres"/>. A host that
    /// configures the despawn radius <i>inside</i> the stream radius would otherwise get the
    /// spawn/evict oscillation described above on every tick; clamping degrades that to "no
    /// hysteresis" instead of "a permanent packet loop".
    /// </summary>
    public float EffectiveDespawnRadiusMetres =>
        MathF.Max(DespawnRadiusMetres, StreamRadiusMetres);

    /// <summary>
    /// The most ground objects the streamer will keep alive on the client at once — items and their
    /// ammunition boxes together.
    /// <para>
    /// <b>D281: 576, and it is a guard rail rather than a budget.</b> D69 sized it 384 against a
    /// measured peak working set of 341 on the wave-8 floor; D270 doubled that floor and the same
    /// sweep measures <b>497</b>, so 384 had started truncating. 576 restores D69's own margin.
    /// Measured with the cap lifted to 4,096 the peak stays 497, so it binds nowhere.
    /// <para>The paragraph below is wave 8's and its numbers are the wave-8 floor's.</para> The owner's own server has
    /// <b>no live cap at all</b> — its items carry a 150 m render distance, its sweeps move up to 50
    /// entities / 22 KB every 100 ms, and <c>DespawnOutOfRange = false</c> means nothing is ever
    /// un-sent (docs/78 §3.1). Cranberry keeps a ceiling, because one paced world pump on a link
    /// with no send window is a different machine from his, but the ceiling is now set <i>above</i>
    /// the working set instead of inside it: at his density the whole 60 m disc is <b>181 median /
    /// 261 p90 / 307 max</b> objects over 1,200 sampled POI positions, so 384 never binds anywhere
    /// that was sampled.
    /// </para>
    /// <para>
    /// <b>This is the "some buildings had no loot" fix.</b> 128 was spent nearest-first inside
    /// <b>24-31 m</b> in the four densest POIs, so the far half of the disc was never reached:
    /// measured building coverage inside 60 m was <b>57 % median / 36 % p10</b>. The owner's density
    /// alone lifts that to 95 % / 68 %; lifting the cap as well takes it to <b>100 % / 96 %</b>
    /// (docs/78 §3.2).
    /// </para>
    /// <para>
    /// <b>What 128 used to be, and why that reason expired.</b> It was the working set the client
    /// already carried after the landing burst, chosen because the binding cost was never the spawns
    /// but the <c>f8 01 ProximateItems</c> republish — <c>7 + 74 × N</c> bytes, <b>9,552 B</b> at the
    /// 129 rows measured live (<c>logs/host-20260830-090725.log</c> 09:12:49.975), with a
    /// client-side sink that also recomputes every crafting recipe. That coupling is gone:
    /// <see cref="PanelRadiusMetres"/> now filters the panel to the owner's own 2 m ball, so the
    /// republish is <b>155 B p90</b> and no longer scales with this number at all.
    /// </para>
    /// </summary>
    public int MaxLive { get; init; } = Rulings.LootGates.MaxLive;

    /// <summary>
    /// Hard ceiling on how many ground objects one re-stream tick may spawn. The tick is normally
    /// stopped earlier, by <see cref="RestreamByteBudget"/>; this is the count that bounds it when
    /// a host sets the byte budget to zero, and the term that keeps the per-tick peak a
    /// <b>constant</b> rather than a function of how dense the POI is or how fast the player runs.
    /// <para>
    /// <b>WAVE 8: 32 → 64.</b> The owner's sweep budget is 50 entities <i>or</i> 22,000 B every
    /// 100 ms; Cranberry's is one paced tick every 500 ms, so the count ceiling is raised to keep the
    /// byte budget — not the count — the thing that actually decides a tick (docs/78 §3.4).
    /// </para>
    /// </summary>
    public int MaxPerRestream { get; init; } = Rulings.LootGates.MaxPerRestream;

    /// <summary>
    /// A sweep stops taking spawns once its planned bytes reach this budget. Each object reserves
    /// <see cref="SpawnBytes"/>, including the ItemAdd needed for native proximity clicks.
    /// At 22,000 B the planner can take at most 38 objects, including the final indivisible
    /// firearm-and-ammunition set; <see cref="WorstCaseTickBytes"/> also includes evictions and panel.
    /// <para>
    /// Bytes rather than count because a mixed sweep's cost is not proportional to its count — a
    /// spawn reserves 627 B and an eviction 13 — and because the loot arm will eventually share its burst
    /// with vehicles and doors, whose objects cost different amounts again. Non-positive disables the
    /// byte budget and leaves <see cref="MaxPerRestream"/> as the only ceiling.
    /// </para>
    /// </summary>
    public int RestreamByteBudget { get; init; } = Rulings.LootGates.RestreamByteBudget;

    /// <summary>
    /// The most objects one tick may destroy, so a straight run out of a POI cannot put an unbounded
    /// eviction list into a single burst.
    /// <para>
    /// <b>The old objection to capping this has expired, and it is worth saying why rather than
    /// silently reversing it.</b> Capping evictions was rejected at wave 5 because a deferred
    /// eviction keeps <see cref="MaxLive"/> full, which starves the same tick's spawns — a player
    /// sprinting into a new POI would find it empty for the four ticks it took to drain, which is
    /// the owner's own P0. That argument depends entirely on the cap <i>binding</i>. It no longer
    /// does: at the owner's density the 60 m disc is 261 p90 objects against a 384 ceiling
    /// (docs/78 §3.2), so a deferred eviction leaves ~120 slots of headroom and cannot starve
    /// anything. What it buys is a bounded tick: 38 spawns + 64 evictions = 102 loot actions, which
    /// is 7 <c>DrainBurst</c> slices ≈ 280 ms inside a 500 ms tick.
    /// </para>
    /// <para>Non-positive means "no cap", which is the pre-wave-8 behaviour exactly.</para>
    /// </summary>
    public int MaxEvictionsPerRestream { get; init; } = Rulings.LootGates.MaxEvictionsPerRestream;

    /// <summary>
    /// How far, as a fraction of <see cref="StreamRadiusMetres"/>, the player must move before a
    /// burst is due.
    /// <para>
    /// <b>WAVE 8: 0.5 → 0.167, a 10 m threshold at 60 m — his <c>RescanDistance</c>.</b> It used to
    /// be 30 m, matched to <c>MatchDoorOptions.RestreamFraction</c> so both arms of the shared pump
    /// fired together; each arm has carried its own rate limit since (<c>WorldPumpArm.IsDue</c>), so
    /// they no longer have to agree. 30 m of walking at 128 live objects is what let a player cross
    /// a whole POI between bursts.
    /// </para>
    /// Non-positive is the explicit "stream once per match" setting: wave 4's ground-loot behaviour,
    /// kept reachable so a regression can be A/B'd against it. The backfill arm
    /// (<see cref="MatchLoot.ShouldRestream"/>) is disabled with it, so the whole streamer collapses
    /// to the wave-4 latch rather than to a half-behaviour.
    /// </summary>
    public float RestreamFraction { get; init; } = Rulings.LootGates.RestreamFraction;

    /// <summary>
    /// Pump period in milliseconds.
    /// <para>
    /// <b>WAVE 8: 3,000 → 500.</b> His streamer sweeps every <b>100 ms</b> and moves up to 500
    /// entities a second; ours moved 32 objects every 3 s = <b>10.7/s</b>, forty-seven times slower,
    /// and that rate is the other half of the empty-buildings complaint. At 500 ms a cold 60 m disc
    /// now fills in approximately <c>261 / 36</c>, or <b>8 ticks = 4.0 s</b>. 500 rather than his 100
    /// because Cranberry keeps <i>one</i> paced world pump and one <c>DrainBurst</c> slicer; 280 ms
    /// of slices inside a 500 ms tick still leaves the tick a margin (docs/78 §3.4).
    /// </para>
    /// <para>
    /// The shared pump ticks at the shortest arm's interval and each arm then re-checks its own
    /// deadline (<c>WorldPumpArm.IsDue</c>), so shortening this one cannot speed the door or vehicle
    /// arm up — that guard is what makes the change safe to make alone.
    /// </para>
    /// </summary>
    public int RestreamIntervalMs { get; init; } = Rulings.LootGates.RestreamIntervalMs;

    /// <summary>
    /// Whether a streamed firearm brings its two ammunition boxes (docs/39 §5). Mirrors
    /// <c>ZoneOptions.SendLootClusters</c> so the streamed floor and the landing floor read the same;
    /// a gun without its pair is an inert prop.
    /// </summary>
    public bool SendClusters { get; init; } = true;

    /// <summary>
    /// Largest nearest-marker window one planning pass may look at, mirroring
    /// <c>ZoneService.GroundLootQueryWindow</c>. Bigger than <see cref="MaxPerRestream"/> so that
    /// markers already spawned, already taken, or whose category rolls nothing are stepped over
    /// rather than costing the tick a spawn.
    /// </summary>
    public int QueryWindow { get; init; } = Rulings.LootGates.QueryWindow;

    /// <summary>
    /// <b>The <c>f8 01 ProximateItems</c> panel is a 2 m ball, and that is his number.</b>
    /// <c>ZoneProximity.ProximityRadius = 2f</c> — the horizontal radius inside which a ground item
    /// is listed in the client's nearby-pickable panel. Cranberry published the <i>whole</i>
    /// registry with no distance test at all, which was survivable only while
    /// <see cref="MaxLive"/> was 128 and is what pinned it there: <c>7 + 74 × N</c> bytes, measured
    /// at <b>9,552 B</b> per republish.
    /// <para>
    /// Measured rows inside 2 m / ±1 m over 1,200 sampled POI positions at the owner's density:
    /// median <b>0</b>, p90 <b>3</b>, p99 6, max 9 — a <b>155 B p90</b> packet. This one filter is
    /// what makes <see cref="MaxLive"/> = 384 free (docs/78 §3.3).
    /// </para>
    /// <para>
    /// <b>It cannot break pickup.</b> <c>f8 01</c> is the QuickLoot panel list, not the <c>[F]</c>
    /// binding — the prompt is bound by <c>ea 04 CreateComponent</c> (docs/36 §2 rules 2-3,
    /// docs/19 §4b) — and the client's reader frees its whole collection before parsing, so a full
    /// replace carrying fewer rows is exactly what the packet is for. Non-positive publishes the
    /// whole registry, which is the pre-wave-8 behaviour.
    /// </para>
    /// <para>
    /// It is also coupled to the cluster geometry: the 0.5 m ammunition-box offset was chosen by the
    /// owner so a gun and both its boxes fall inside this same ball together, so widening one
    /// without the other stops a gun's boxes being listed with it (docs/78 §2.4).
    /// </para>
    /// </summary>
    public float PanelRadiusMetres { get; init; } = Rulings.LootGates.PanelRadiusMetres;

    /// <summary>
    /// Vertical half-height of the panel ball, his <c>ZoneProximity.ProximityYDistance</c>. ±1 m is
    /// what keeps a first floor's loot out of a ground floor's panel. Non-positive means "any
    /// height".
    /// </summary>
    public float PanelHeightMetres { get; init; } = Rulings.LootGates.PanelHeightMetres;

    /// <summary>
    /// Most rows one <c>f8 01</c> may carry, his <c>ZoneProximity.MaximumEntries</c>. It never binds
    /// at his density (max 9 measured); it is kept because it bounds the packet <i>by
    /// construction</i> rather than by the world's statistics, which is the property that lets a
    /// later denser table ship without re-deriving this packet's cost. Non-positive means "no cap".
    /// </summary>
    public int PanelMaxRows { get; init; } = Rulings.LootGates.PanelMaxRows;

    /// <summary>
    /// <b>The server's own answer gate for a ground pickup, in metres.</b> His
    /// <c>ZoneProximity.InteractReach = 4f</c>: the radius inside which his server answers
    /// <c>Command.InteractionString</c> and therefore the radius inside which the client is willing
    /// to send <c>Command.InteractRequest</c> at all. Cranberry has had <b>no</b> server-side reach
    /// check on any pickup path.
    /// <para>
    /// <b>4 m and not his 2.2 m, deliberately.</b> His round-23 build gated the panel and the
    /// context menu at <c>PickupReach = 2.2</c> and left the interact key ungated; his round 24
    /// replaced that split with one 4 m gate on every path, because the 18:05 session logged the two
    /// gates disagreeing about the same item in the same instant (panel REFUSED at 2.30 m &gt; 2.2 m
    /// while interact granted it). Round 24 is the later ruling and the one adopted here. 4 m also
    /// independently matches docs/13 §8's ~4 m reach measured from the <i>August</i> binary, so two
    /// builds agree on it.
    /// </para>
    /// <para>
    /// <b>Fail-open.</b> The gate is skipped entirely when this session has no player position yet,
    /// so it can only ever refuse a press whose distance the server actually knows. Non-positive
    /// disables it and restores the pre-wave-8 behaviour of granting any claimable guid.
    /// </para>
    /// </summary>
    public float PickupReachMetres { get; init; } = Rulings.LootGates.PickupReachMetres;

    /// <summary>
    /// How long an object a <b>player</b> dropped survives before the streamer destroys it, in
    /// milliseconds. <b>0 (the default) means never</b>, which is the pre-wave-8 behaviour.
    /// <para>
    /// His server expires player drops at 10 minutes (<c>ZoneLootSpawner</c>), and the shape is
    /// worth having — a 25-minute match otherwise ends with hundreds of abandoned entities holding
    /// streamer slots and 74 B of panel each. The <i>number</i> is not adopted: its only provenance
    /// is third-party config, which D53 leaves closed, and the owner has never asked for it. So the
    /// mechanism ships and the dial ships at off (docs/78 §5.1).
    /// </para>
    /// <para>
    /// Only <see cref="LootStreamKeyKind.Dropped"/> entries are eligible. A marker item must never
    /// expire on a timer: its marker is retired for the match when it is taken and by nothing else,
    /// so an expired one would leave a hole no later sweep could fill.
    /// </para>
    /// </summary>
    public int DroppedItemLifetimeMs { get; init; }

    /// <summary>
    /// <b>Read from the owner's server and deliberately NOT adopted</b>, with the reasons on the
    /// record so this is not re-litigated (docs/78 §5, §7 E19):
    /// <list type="bullet">
    /// <item><b>Mid-match spawner respawn.</b> His <c>ZoneLootSpawner</c> refills spent spawners on
    /// a player-count ladder — 75+ 10 min, 50+ 15, 25+ 20, 1+ 25 — with a 40-minute spawner-item
    /// despawn beside it. That is 2016-survival behaviour: a KOTK match is shorter than the ladder's
    /// <i>second</i> rung, the owner has never asked for loot to come back mid-match, and
    /// Cranberry's "a taken marker never returns" is the right model for a 20-25 minute round.</item>
    /// <item><b>The 150 m per-item render distance.</b> At his density a 150 m disc is 527 median /
    /// 930 p90 objects — 250-440 KB to fill, and a minute of Cranberry's slicer — for coverage that
    /// <see cref="StreamRadiusMetres"/> = 60 already delivers at 100 % / 96 %.</item>
    /// <item><b><c>Ammo01</c> pair-gating.</b> docs/65 §4 C4 proposed gating the 36 client-placed
    /// ammunition pairs jointly, as a substitute for a spawn chance it had to reject. With 0.60
    /// adoptable the substitute has no job, and his server rolls each of the 72 markers
    /// independently.</item>
    /// <item><b>A per-building loot minimum.</b> docs/65 §4 C5. No version of his server has one,
    /// and at 1.4 % bare doorways the map does not need one — the empty buildings were this
    /// streamer, not the world generation.</item>
    /// <item><b>His unseeded refill RNG.</b> He uses <c>new Random()</c> on purpose, so two servers
    /// with the same uptime do not hold identical loot. Cranberry's standing rule is seeded
    /// determinism (<c>Z2LootSpawns.SeedFor</c>); if <see cref="DroppedItemLifetimeMs"/> ever grows
    /// a random component it seeds from the match seed and the tick index, never
    /// <c>Random.Shared</c>.</item>
    /// </list>
    /// </summary>
    public static string NotAdoptedFromZ1 =>
        "spawner respawn ladder; 150 m render distance; Ammo01 pair-gating; per-building minimum; "
        + "unseeded refill RNG";

    /// <summary>Conservative per-spawn wire budget: <c>d6</c> 202 + <c>da</c> 211 +
    /// NPC <c>ea 04</c> 129 + interact <c>ea 04</c> 63 + remote-owner <c>ItemAdd</c> at most
    /// 149 application bytes, plus five
    /// 1-byte gateway tunnel envelopes. ItemAdd registers the item in the world's native inventory
    /// so proximity clicks can resolve it. Generic items use 78 B; the budget reserves the largest
    /// weapon tail, verified across all item facts by <c>LootStreamBudgetTests</c>.
    /// These reserve the widest loot transient id (999,999, a three-byte varint); the earlier
    /// measured 201/210 B spawn/promotion lengths used two-byte ids. No individual packet
    /// exceeds 508 B.</summary>
    public const int SpawnBytes = 759;

    /// <summary>Bytes one eviction costs: <c>RemovePlayer.Length</c> 12 + the tunnel envelope.</summary>
    public const int DespawnBytes = 13;

    /// <summary>
    /// <c>ProximateItems</c> fixed cost: the packet's own 6-byte header (<c>u8</c> opcode,
    /// <c>u8</c> sub, <c>i32</c> count) plus the tunnel envelope. docs/52 §4a wrote this as 3 B; the
    /// packet writer says 6, and 6 + 74 × 129 = 9,552 is the number actually measured on the wire,
    /// so 6 is right and the doc's 3 was a slip.
    /// </summary>
    public const int ProximateItemsHeaderBytes = 7;

    /// <summary><c>ProximateItems.ElementLength</c> — 74 B per live ground item, every republish.</summary>
    public const int ProximateItemsRowBytes = 74;

    /// <summary>
    /// The most objects one tick can actually spawn, which is the byte budget expressed as a count
    /// and is <b>not</b> simply <c>RestreamByteBudget / SpawnBytes</c>.
    /// <para>
    /// The planner tests the budget <i>before</i> taking a candidate, and a candidate may be a whole
    /// set — a firearm plus its two ammunition boxes, which docs/39 §5 forbids splitting — so the
    /// last set taken can carry the plan up to two objects past the line. At the shipped defaults
    /// that is <c>(22,000 − 1) / 759 + 3</c> = <b>31</b>, still under the
    /// <see cref="MaxPerRestream"/> ceiling of 64. With the byte budget switched off it is
    /// <see cref="MaxPerRestream"/> exactly.
    /// </para>
    /// </summary>
    public int MaxSpawnsPerRestream
    {
        get
        {
            int byCount = Math.Max(0, MaxPerRestream);
            if (RestreamByteBudget <= 0)
            {
                return byCount;
            }

            // (B - 1) / S is the largest count whose bytes are still strictly under the budget, and
            // + 2 for the two boxes a gun set may add after that test passed (+1 for the gun).
            return Math.Min(byCount, ((RestreamByteBudget - 1) / SpawnBytes) + 3);
        }
    }

    /// <summary>How many <c>f8 01</c> rows one republish can carry, panel cap included.</summary>
    public int MaxPanelRows =>
        PanelMaxRows > 0 ? Math.Min(PanelMaxRows, Math.Max(0, MaxLive)) : Math.Max(0, MaxLive);

    /// <summary>
    /// The bytes one worst-case tick can put on the link — the bound
    /// <c>LootStreamBudgetTests</c> holds a simulated map crossing to.
    /// <para>
    /// Spawns are bounded by <see cref="MaxSpawnsPerRestream"/> (the byte budget, 31);
    /// evictions by <see cref="MaxEvictionsPerRestream"/> (64); and the panel by
    /// <see cref="MaxPanelRows"/> (32), independently of the working set's live cap.
    /// </para>
    /// <para>
    /// At the shipped defaults: <c>31 × 759 + 64 × 13 + 7 + 32 × 74</c> = <b>26,736 B</b>.
    /// This conservatively reserves a weapon ItemAdd for every spawn, including generic items.
    /// </para>
    /// <para>
    /// A tick only reaches this bound while the disc is genuinely cold. A player standing in a
    /// streamed street plans nothing and pays nothing — <c>ShouldRestream</c> is false until they
    /// have moved <see cref="RestreamFraction"/> × the radius, and a plan that changes nothing sends
    /// no <c>f8 01</c> either (docs/52 §3f rule 4).
    /// </para>
    /// </summary>
    public int WorstCaseTickBytes =>
        (MaxSpawnsPerRestream * SpawnBytes)
        + (Math.Max(0, MaxEvictionsPerRestream > 0 ? MaxEvictionsPerRestream : MaxLive) * DespawnBytes)
        + ProximateItemsHeaderBytes
        + (MaxPanelRows * ProximateItemsRowBytes);

    /// <summary>
    /// What one actual tick cost. <paramref name="panelRows"/> negative means the tick changed no
    /// ground item and therefore sent no <c>f8 01</c> at all — the door-only case, which must pay
    /// nothing for the panel (docs/52 §3f rule 4). Since wave 8 the caller passes the rows the panel
    /// will actually carry (bounded by <see cref="PanelRadiusMetres"/> and
    /// <see cref="PanelMaxRows"/>), not the size of the working set.
    /// </summary>
    public static int EstimateTickBytes(int spawns, int evictions, int panelRows) =>
        (Math.Max(0, spawns) * SpawnBytes)
        + (Math.Max(0, evictions) * DespawnBytes)
        + (panelRows < 0
            ? 0
            : ProximateItemsHeaderBytes + (panelRows * ProximateItemsRowBytes));
}
