using System.Numerics;
using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Gas;

/// <summary>
/// Gas geometry and timing for the August 4, 2017 client. The default phase tables follow
/// public gameplay on build 0.0.118.208059; unobserved endgame phases remain explicit fallbacks.
/// See docs/gas-ring-sizing-20260906.md for measurements and limits of the reconstruction.
/// Runtime tuning and compressed test schedules are available through <see cref="GasTuning"/>.
/// </summary>
public sealed record GasSettings
{
    /// <summary>Use a smaller shared spawn/gas plan for rosters below 100 players.</summary>
    public bool PopulationAdaptive { get; init; } = true;

    /// <summary>
    /// docs/53 §5.4's damage column, in health units per <see cref="TickPeriodMs"/> off a
    /// <see cref="MaxHitpoints"/> bar: 0.9 %/s rising to 6.0 %/s. Shared as one instance so that
    /// two default <see cref="GasSettings"/> compare equal (a record compares this list by
    /// reference), which is what <see cref="GasTuning.NameOf"/> needs to recognise a preset.
    /// </summary>
    internal static readonly uint[] RetailDamagePerPhase = Rulings.Gas.RetailDamagePerPhase;

    /// <summary>
    /// Number of revealed targets. Ten is the retained project continuation; the exact-build
    /// recording reaches seven targets before the match ends, so it does not establish the total.
    /// <para>Override: <c>CRANBERRY_GAS_PHASES</c>.</para>
    /// </summary>
    public int PhaseCount { get; init; } = Rulings.Gas.PhaseCount;

    /// <summary>
    /// Default <see cref="GasPacing.PhaseTable"/> keeps measured holds and movement durations
    /// independent of radius changes. Speed-paced and legacy fixed windows remain available.
    /// <para>Override: <c>CRANBERRY_GAS_PACING</c>.</para>
    /// </summary>
    public GasPacing Pacing { get; init; } = GasPacing.PhaseTable;

    /// <summary>
    /// Exact reveal-to-movement hold for every phase in <see cref="GasPacing.PhaseTable"/>.
    /// There must be one entry per phase, including phase 1. Zero means movement begins at the
    /// reveal. Other pacing models do not read this table.
    /// </summary>
    public IReadOnlyList<uint> HoldDurationsMs { get; init; } = Rulings.Gas.HoldDurationsMs;

    /// <summary>
    /// Exact movement duration for every phase in <see cref="GasPacing.PhaseTable"/>.
    /// There must be one positive entry per phase. Radii and wall-speed settings do not rescale
    /// these durations; <see cref="ScaledBy"/> scales them with the rest of the match clock.
    /// Other pacing models do not read this table.
    /// </summary>
    public IReadOnlyList<uint> AdvanceDurationsMs { get; init; } = Rulings.Gas.AdvanceDurationsMs;

    /// <summary>
    /// Centre of the very first (pre-phase-1) circle, in world coordinates. Circles are horizontal:
    /// only X and Z are compared, Y is carried through so the value can be logged and written into
    /// the <c>f32×4</c> centre of <c>ce 01</c>/<c>ce 02</c> unchanged.
    /// <para>
    /// <b>(−250, 0, 100) since wave 8</b> (docs/66 §3, docs/77 §4.2); wave 5 shipped
    /// <see cref="Vector3.Zero"/>. Z2's content is not centred on the world origin: the minimum
    /// enclosing circle of docs/48 §4.4's 92 named drop anchors sits at (−314.1, 23.6) with
    /// r = 4 330.6 m, and a 50 m grid search over the 166 781 <c>ItemSpawner_*</c> markers puts 98 %
    /// of the loot mass inside 4 311 m at (−150, 250). (−250, 100) lies between the two optima
    /// and costs 22 m of radius against the bare minimum enclosing circle. Moving the centre 315 m
    /// removes 220 m of <see cref="InitialRadius"/>, and that is what buys the drift budget
    /// <see cref="CentreDriftFraction"/> spends.
    /// </para>
    /// <para>Override: <c>CRANBERRY_GAS_CENTRE_X</c> / <c>CRANBERRY_GAS_CENTRE_Z</c>.</para>
    /// </summary>
    public Vector3 PlayAreaCentre { get; init; } = Rulings.Gas.PlayAreaCentre;

    /// <summary>
    /// Opening gas boundary radius, distinct from the first revealed safe target. Approximately
    /// 8000 m is inferred from the exact-build recording: 20 m/s radial movement for 300 seconds
    /// to a 2000 m target. The opening boundary is outside the playable map and dormant until movement.
    /// <para>Override: <c>CRANBERRY_GAS_INITIAL_RADIUS_M</c>.</para>
    /// </summary>
    public float InitialRadius { get; init; } = Rulings.Gas.InitialRadius;

    /// <summary>
    /// Terminal target radius. The retained 40 m fallback is not established by the available
    /// retail recording, which ends during phase seven.
    /// <para>Override: <c>CRANBERRY_GAS_FINAL_RADIUS_M</c>.</para>
    /// </summary>
    public float FinalRadius { get; init; } = Rulings.Gas.FinalRadius;

    /// <summary>
    /// Match clock at the first safe-zone reveal. The 120-second opening countdown is retained.
    /// <see cref="ZoneOptions.SafeZoneRevealMs"/> must agree so the drop HUD and gas share one clock.
    /// <para>Override: <c>CRANBERRY_GAS_FIRST_REVEAL_MS</c>.</para>
    /// </summary>
    public uint FirstRevealDelayMs { get; init; } = Rulings.Gas.FirstRevealDelayMs;

    /// <summary>
    /// Absolute first movement time used by <see cref="GasPacing.SpeedPaced"/>. The default
    /// phase table expresses the same time as first reveal plus its first hold. Environment overrides
    /// translate this absolute value into that first hold when using the phase table.
    /// <para>Override: <c>CRANBERRY_GAS_FIRST_MOVE_MS</c>.</para>
    /// </summary>
    public uint FirstMoveDelayMs { get; init; } = Rulings.Gas.FirstMoveDelayMs;

    /// <summary>
    /// Uniform phase 2..N hold for <see cref="GasPacing.SpeedPaced"/>. The default phase table
    /// has distinct measured holds. An environment override replaces all its later holds.
    /// <para>Override: <c>CRANBERRY_GAS_HOLD_MS</c>.</para>
    /// </summary>
    public uint InterPhaseHoldMs { get; init; } = Rulings.Gas.InterPhaseHoldMs;

    /// <summary>
    /// Radius decrease per second under <see cref="GasPacing.SpeedPaced"/>. The retained scalar
    /// is a legacy tuning value; default phase-table movement has a different rate in every phase.
    /// <para>Override: <c>CRANBERRY_GAS_WALL_SPEED</c> selects speed pacing.</para>
    /// </summary>
    public float ShrinkSpeedMetresPerSecond { get; init; } = Rulings.Gas.ShrinkSpeedMetresPerSecond;

    /// <summary>
    /// Historical speed-pacing budget, retained for <see cref="SolvedShrinkSpeedMetresPerSecond"/>.
    /// The default phase table does not derive timings from this budget; its finish is the sum of
    /// its configured holds and advances.
    /// </summary>
    public uint TargetMatchLengthMs { get; init; } = Rulings.Gas.TargetMatchLengthMs;

    /// <summary>
    /// Maximum centre displacement as a fraction of the radius reduction. A value at most one
    /// keeps each target inside its predecessor. The retained 0.96 cap is a project placement rule;
    /// the recordings establish target sizes, not the original server randomization algorithm.
    /// <para>Override: <c>CRANBERRY_GAS_DRIFT</c>.</para>
    /// </summary>
    public float CentreDriftFraction { get; init; } = Rulings.Gas.CentreDriftFraction;

    /// <summary>
    /// How the match decides <b>where its circles go</b> — see <see cref="GasCentrePlan"/>.
    /// <b>Default <see cref="GasCentrePlan.PoiDestination"/></b> (D277): the endgame is aimed at one
    /// of the nine <c>GasWeightArea</c> volumes the August client itself ships, weighted by
    /// <see cref="PoiWeightExponent"/>, and every phase centre walks the straight line toward it.
    /// <para>Override: <c>CRANBERRY_GAS_CENTRE_PLAN</c> (an enum name).</para>
    /// </summary>
    public GasCentrePlan CentrePlan { get; init; } = GasCentrePlan.PoiDestination;   // Rulings.Gas.CentrePlan (D277)

    /// <summary>
    /// The exponent the nine client volumes are weighted by: <c>weight = footprint ^ exponent</c>.
    /// <b>0.5</b> (D277) weights by the box's own <i>linear extent</i>, so
    /// <c>SchadeWoodsLoggingTrail</c> (137 900 m²) is drawn 4.5× as often as
    /// <c>ChangsWildCampgrounds</c> (6 802 m²); 1.0 weights strictly by area (20×) and <b>0</b>
    /// makes the nine equally likely. The footprints are the client's; the rule is Cranberry's
    /// (docs/118 §3).
    /// <para>
    /// <b>Why 0.5 and not 1.0.</b> At 1.0 one volume takes 40 % of every match, and the drop lane's
    /// own signed-off property — three consecutive matches are never the same place
    /// (<c>DropPlannerTests.ConsecutiveMatchesDropSomewhereElse</c>) — fails at exactly one window
    /// in 200, because two matches aimed at the same volume have nearly the same first circle. At
    /// 0.5 it holds, with the client's own volumes still driving the draw. Measured, docs/118 §3.4.
    /// </para>
    /// <para>Override: <c>CRANBERRY_GAS_POI_EXPONENT</c>.</para>
    /// </summary>
    public float PoiWeightExponent { get; init; } = Rulings.Gas.PoiWeightExponent;

    /// <summary>
    /// Maximum opening-centre movement toward a destination that the remaining containment
    /// budget cannot reach. The default 8000 m boundary reaches all shipped POI destinations
    /// without spending this allowance; custom smaller boundaries may need it.
    /// This is a retained project placement rule, not a recovered retail randomization rule.
    /// <para>Override: <c>CRANBERRY_GAS_PLAY_AREA_LEAD_M</c>.</para>
    /// </summary>
    public float PlayAreaLeadMetres { get; init; } = Rulings.Gas.PlayAreaLeadMetres;

    /// <summary>
    /// Width, in degrees, of the cone the per-match drift heading is jittered inside — <b>120°</b>
    /// (±60°). 360 is the old uniform-angle draw.
    /// <para>
    /// A uniform angle turns the walk into a random walk, so most of a capped drift budget cancels
    /// itself out: at <see cref="CentreDriftFraction"/> = 0.20 the final circle's centre lands a
    /// median of 265 m from <see cref="PlayAreaCentre"/> (p10 108 m) under a uniform angle, and
    /// <b>510 m (p10 378 m)</b> inside a 120° cone about one heading drawn per match — the same
    /// per-phase offsets, so every bound above is untouched, but the gas pushes consistently one way
    /// across the map instead of jittering back and forth, and the "the circle barely moved" match
    /// stops happening. docs/77 §4.4.
    /// </para>
    /// <para>Override: <c>CRANBERRY_GAS_DRIFT_CONE</c>.</para>
    /// </summary>
    public float DriftConeDegrees { get; init; } = Rulings.Gas.DriftConeDegrees;

    /// <summary>
    /// Validation ceiling for radial speed plus centre movement, in metres per second.
    /// The 40 m/s default admits the observed 20 m/s first radial advance with the retained
    /// 0.96 centre-drift cap. This is a project bound, not a measured retail leading-edge constant.
    /// <para>Override: <c>CRANBERRY_GAS_MAX_EDGE</c>.</para>
    /// </summary>
    public float MaxEdgeSpeedMetresPerSecond { get; init; } = Rulings.Gas.MaxEdgeSpeedMetresPerSecond;

    /// <summary>
    /// What the client is shown, and what the gas may burn, <b>before phase 1's ring first
    /// moves</b>. <see cref="GasPreMoveRing.None"/> by default — the owner's own click-test ruling.
    /// <para>Override: <c>CRANBERRY_GAS_PRE_MOVE_RING</c> (an enum name).</para>
    /// </summary>
    public GasPreMoveRing PreMoveRing { get; init; } = GasPreMoveRing.None;   // Rulings.Gas.PreMoveRing (D65)

    /// <summary>
    /// <see cref="GasPacing.SpeedPaced"/> only. Advances are rounded to this many milliseconds so
    /// the timetable reads as tenths of a second rather than as float noise. 1 disables rounding.
    /// </summary>
    public uint AdvanceRoundingMs { get; init; } = Rulings.Gas.AdvanceRoundingMs;

    /// <summary>
    /// Phase radii are rounded to this many metres so the ladder is legible (3 635 / 2 205 / 1 335
    /// … rather than 3 635.34 / 2 202.61 / 1 334.53). 0 disables rounding.
    /// <para>
    /// Rounding is skipped for any phase whose raw gap to either neighbour is not wider than this
    /// step: a rounded value moves by at most half a step, so a gap wider than a full step cannot
    /// be closed by rounding both ends, and the ladder stays strictly decreasing for <i>any</i>
    /// <see cref="PhaseCount"/> and <see cref="InitialRadius"/> — including the compressed circles
    /// a bring-up run asks for.
    /// </para>
    /// </summary>
    public float RadiusRoundingMetres { get; init; } = Rulings.Gas.RadiusRoundingMetres;

    /// <summary>
    /// An exact list of target radii, one per phase including <see cref="FinalRadius"/>.
    /// A non-empty list must be finite, positive and strictly decreasing from
    /// <see cref="InitialRadius"/>; <see cref="Validate"/> rejects an incompatible list.
    /// <para>
    /// Without an explicit assignment, this exposes the configured default table when its phase
    /// count matches. <see cref="RadiusForPhase"/> rescales that table between the requested
    /// initial and final radii and applies <see cref="RadiusRoundingMetres"/> without flattening
    /// adjacent phases. The unchanged default geometry preserves every table value exactly,
    /// including fractional radii. A different phase count selects the geometric calculation.
    /// Timing changes do not change the selected radii.
    /// </para>
    /// <para>
    /// An explicit non-empty assignment retains its exact values when other settings change.
    /// An explicit empty list always selects the geometric calculation, including through record
    /// copies. <c>CRANBERRY_GAS_RADIUS_LADDER=0</c> selects that mode.
    /// </para>
    /// </summary>
    public IReadOnlyList<float> RadiusLadder
    {
        get => _radiusLadder ?? (PhaseCount == Rulings.Gas.RadiusLadder.Length
            ? Rulings.Gas.RadiusLadder
            : Array.Empty<float>());
        init => _radiusLadder = value ?? throw new ArgumentNullException(nameof(RadiusLadder));
    }

    private readonly IReadOnlyList<float>? _radiusLadder;

    /// <summary>
    /// <see cref="GasPacing.FixedWindows"/> only. Phase 1's reveal → fully-closed window (wave 4:
    /// 3 min). Not read under the default <see cref="GasPacing.SpeedPaced"/>.
    /// <para>
    /// No environment override: <c>CRANBERRY_GAS_PHASE_WINDOW_MS</c> was deleted in lane 0D
    /// because it was inert under the shipped pacing and only ever produced a rejection note.
    /// </para>
    /// </summary>
    public uint FirstPhaseWindowMs { get; init; } = Rulings.Gas.FirstPhaseWindowMs;

    /// <summary>
    /// <see cref="GasPacing.FixedWindows"/> only. Each later phase's window is this much shorter
    /// than the previous one (wave 4: ~60 s).
    /// </summary>
    public uint PhaseWindowShorteningMs { get; init; } = Rulings.Gas.PhaseWindowShorteningMs;

    /// <summary>
    /// <see cref="GasPacing.FixedWindows"/> only. Floor under
    /// <see cref="PhaseWindowShorteningMs"/> (wave 4: 90 s).
    /// </summary>
    public uint MinimumPhaseWindowMs { get; init; } = Rulings.Gas.MinimumPhaseWindowMs;

    /// <summary>
    /// <see cref="GasPacing.FixedWindows"/> only. Head of each window during which the circle is
    /// revealed but has not started closing yet — the client's own animation is a
    /// <c>(startTime, duration)</c> linear interpolation (docs/15 §3b, <c>FUN_140bbe3e0</c>), so the
    /// server's closing window is <c>window − warning</c>. This field <b>is</b> the same quantity
    /// <see cref="FirstMoveDelayMs"/> and <see cref="InterPhaseHoldMs"/> express under
    /// <see cref="GasPacing.SpeedPaced"/> (docs/53 §Integration 1); the two models differ only in
    /// that this one is a single constant for every phase.
    /// </summary>
    public uint ShrinkWarningMs { get; init; } = Rulings.Gas.ShrinkWarningMs;

    /// <summary>
    /// Pause between one phase closing and the next being revealed. Zero (the default) chains the
    /// phases back to back, which is what both models assume: a phase 2..N is revealed the instant
    /// its predecessor finishes closing, and then holds.
    /// </summary>
    public uint PhaseHoldMs { get; init; }

    /// <summary>Period of the damage tick applied to players outside the closing circle (D23: 1 s).</summary>
    public uint TickPeriodMs { get; init; } = Rulings.Gas.TickPeriodMs;

    /// <summary>
    /// How often the host pumps <see cref="GasController.Tick"/>. It only bounds the latency of a
    /// reveal or a damage tick — the controller rate-limits both against its own clock, so a
    /// 100 ms and a 250 ms pump play the identical match (<c>GasControllerTests</c>).
    /// </summary>
    public int HostTickIntervalMs { get; init; } = Rulings.Gas.HostTickIntervalMs;

    /// <summary>
    /// Minimum spacing between two <c>ce 01</c> re-sends of the travelling ring. The client
    /// interpolates the ring itself every frame, so this only has to keep the HUD/minimap circle
    /// honest; 500 ms is two updates per second per player. A ten-wave match sends the same 2 Hz
    /// stream a five-wave one did — only for longer (docs/53 §6).
    /// </summary>
    public uint SafeZoneUpdateIntervalMs { get; init; } = Rulings.Gas.SafeZoneUpdateIntervalMs;

    /// <summary>
    /// How often the <c>ce 0f</c> countdown widget is re-sent while a match is running — the
    /// <b>heal</b>. <b>1 000 ms</b>, and it is the whole of wave 9's fix (docs/87 §3.2, §4.3).
    /// <para>
    /// Wave 8 sent the widget on three edges in a 24:50 match. The owner's 2026-08-30 20:36 session
    /// shows his client tearing down and rebuilding <c>HudGameModeWindow</c> 2.6 s after the only
    /// <c>ce 0f</c> of the first two minutes, and the class hides its own countdown in
    /// <c>enter()</c> — which a window open runs — with nothing to un-hide it until the next
    /// packet, 117.6 s later. So the one piece of gas feedback the first 4:30 has was almost
    /// certainly blank for 97 % of it. The client stores <c>now + ms</c> as an absolute deadline and
    /// counts down itself, so re-sending changes nothing it displays; it only makes the widget
    /// impossible to lose. 19 bytes a second.
    /// </para>
    /// <para>
    /// This is the owner's own cadence: his sweep sends the countdown every tick
    /// (<c>C:\Z1\Server\Zone\ZoneMatch.cs:1205</c>). Cranberry heals at 1 Hz rather than on every
    /// 250 ms pump because the widget's resolution is one second.
    /// </para>
    /// <para>
    /// <b>0 turns the heal off</b> and restores wave 8's transition-only behaviour — an A/B without
    /// a rebuild, not an opt-in. Override: <c>CRANBERRY_GAS_HUD_HEAL_MS</c>.
    /// </para>
    /// </summary>
    public uint HudHealIntervalMs { get; init; } = Rulings.Gas.HudHealIntervalMs;

    /// <summary>
    /// How often the green <c>ce 02</c> next-safe-zone circle is re-sent while one is revealed —
    /// <b>15 000 ms</b>. Cranberry sent it once per phase, which is the same single-point-of-failure
    /// shape as the countdown: one lost copy and the map is empty until the next reveal, minutes
    /// away. 23 bytes per 15 s.
    /// <para>
    /// <b>DESIGN, not a port</b> (docs/87 §4.4). The owner's server re-sends on the same beat
    /// (<c>ZoneMatch.cs:1806</c>, 15 ticks), but his own constant carries a
    /// <c>SOURCED. kotkgasmanager.ts:198</c> provenance — the number originates in the excluded
    /// third-party server — so the cadence here is Cranberry's own durability choice and the
    /// agreement is noted rather than relied on.
    /// </para>
    /// <para><b>0 turns it off.</b> Override: <c>CRANBERRY_GAS_SAFEZONE_HEAL_MS</c>.</para>
    /// </summary>
    public uint SafeZoneHealIntervalMs { get; init; } = Rulings.Gas.SafeZoneHealIntervalMs;

    /// <summary>
    /// Send the August client's own BR banners on <c>ClientUpdate.TextAlert</c> (<c>11 31</c>) —
    /// three at the drop, one at every reveal, one at every advance. <b>On by default</b>
    /// (<see cref="GasAlerts"/>, docs/87 §4.1-4.2): Cranberry had never sent a TextAlert of any
    /// kind, which is why its first 4:30 carried two gas sends where the owner's carries nine
    /// visible events. The registration is byte-identical in 1087 and 1148 and every sentence is
    /// read verbatim out of the August client's own <c>en_us_data.dat</c>.
    /// <para>Override: <c>CRANBERRY_GAS_BANNERS</c> (0 to silence them).</para>
    /// </summary>
    public bool SendBanners { get; init; } = true;

    /// <summary>
    /// docs/53 §5.1's per-wave damage table, in health units per <see cref="TickPeriodMs"/>:
    /// 90 / 100 / 120 / 150 / 200 / 270 / 400 / 600 / 600 / 600 off a <see cref="MaxHitpoints"/>
    /// bar, i.e. 0.9 %/s rising to 6.0 %/s. The owner's Z1 curve; Cranberry's own clean-sourced
    /// research originally stopped at wave 6; the identical ten-row table was subsequently
    /// found in a February 9, 2017 GamerSky translation. This establishes pre-August publication,
    /// not August server parity (docs/gas-retail-audit-20260906.md). Waves past the table's end
    /// repeat its last entry.
    /// <para>
    /// Set this to an <b>empty</b> list to fall back to the wave-4 linear curve
    /// <see cref="FirstPhaseDamage"/> + <see cref="DamageIncreasePerPhase"/>.
    /// </para>
    /// <para>Override: <c>CRANBERRY_GAS_DAMAGE_SCALE</c> (multiplies every entry).</para>
    /// </summary>
    public IReadOnlyList<uint> DamagePerPhase { get; init; } = RetailDamagePerPhase;

    /// <summary>
    /// Damage at a full toxicity meter, in the same health units per tick as
    /// <see cref="DamagePerPhase"/>. The numerical table is a reconstruction from the same
    /// community source as the base table, also published in a February 9, 2017 GamerSky
    /// translation. The date improves its chronology, but the guide's prose conflicts with its
    /// table and no August server capture verifies it; see docs/gas-retail-audit-20260906.md.
    /// The client and official producer letter independently
    /// establish that full toxicity increases damage. Empty disables the increase for legacy
    /// presets. A custom base damage is never reduced by reaching full toxicity.
    /// </summary>
    public IReadOnlyList<uint> DamageAtFullToxicityPerPhase { get; init; } =
        Rulings.Gas.RetailDamageAtFullToxicityPerPhase;

    /// <summary>
    /// Damage applied per <see cref="TickPeriodMs"/> during phase 1 when
    /// <see cref="DamagePerPhase"/> is empty. 90 of <see cref="MaxHitpoints"/> = 0.9 %/s, which is
    /// also the table's first entry, so a default match ticks 90 either way.
    /// </summary>
    public uint FirstPhaseDamage { get; init; } = Rulings.Gas.FirstPhaseDamage;

    /// <summary>
    /// Added to the damage per tick for every phase after the first, when
    /// <see cref="DamagePerPhase"/> is empty. The wave-4 curve: 90 / 135 / 180 / 225 / 270.
    /// </summary>
    public uint DamageIncreasePerPhase { get; init; } = Rulings.Gas.DamageIncreasePerPhase;

    /// <summary>
    /// The health bar the damage curve is expressed against: <c>CharacterResource.Starter</c>
    /// ships resource 1 (health) as 10000/10000, and the code audit confirms nothing else moves
    /// it yet. Used for the <c>ClientUpdate.Hitpoints (11 01)</c> maximum and the death threshold.
    /// </summary>
    public uint MaxHitpoints { get; init; } = Rulings.Gas.MaxHitpoints;

    /// <summary>
    /// Default seed for <see cref="GasSchedule.Create(GasSettings, ulong)"/> when the caller does
    /// not supply one. A fixed seed makes every match identical, which is what the tests and a
    /// repeatable live run want; the host passes a per-match value for real play.
    /// </summary>
    public ulong Seed { get; init; } = Rulings.Gas.Seed;

    /// <summary>
    /// <c>ce 01</c>'s blend time in milliseconds (gas object <c>+0x54</c>). G-07 closed this field
    /// in docs/18 §1: the client's per-frame smoother <c>FUN_140bbed00</c> uses it as the time
    /// constant of an exponential ease toward the received centre and radius
    /// (<c>alpha = min(frameDeltaMs, 1000) / blendMs</c>), **not** as a start time or a deadline.
    /// It is a divisor with no zero guard, so <see cref="Validate"/> refuses 0; 1000 reproduces the
    /// client's own pre-parse default. Larger values make the ring slide in more lazily.
    /// <para>Override: <c>CRANBERRY_GAS_BLEND_MS</c>.</para>
    /// </summary>
    public uint RingBlendMs { get; init; } = GasPackets.RingBlendMsDefault;

    /// <summary>
    /// What <see cref="RingBlendMs"/> actually means on the wire — see <see cref="GasRingBlendMode"/>.
    /// <b>Default <see cref="GasRingBlendMode.SendPeriod"/></b> (D280, the owner's ruling (e)): the
    /// blend constant a moving <c>ce 01</c> carries is the update interval. This shortens the
    /// exponential lag; it does not make an ease finish exactly at the next packet. At a steady
    /// frame rate, roughly 37 percent of a step remains after one time constant.
    /// <para>Override: <c>CRANBERRY_GAS_BLEND_MODE</c> (an enum name).</para>
    /// </summary>
    public GasRingBlendMode RingBlendMode { get; init; } = GasRingBlendMode.SendPeriod;   // Rulings.Gas.RingBlendMode (D280)

    /// <summary>
    /// Arm and drive the August client's own <b>toxicity meter</b> while a match runs — resource
    /// <see cref="ToxicityResourceId"/>, the <c>m_toxicity</c> <c>ResourceBar</c> that
    /// <c>HudPlayerResourcesWindow.gfx</c> already draws beside health and fuel. <b>On</b> (D279,
    /// the owner's ruling (d)). Off suppresses only the HUD sends; the authoritative meter and
    /// its damage effect continue to run in <see cref="GasController"/>.
    /// <para>Override: <c>CRANBERRY_GAS_TOXICITY</c> (0 to silence it).</para>
    /// </summary>
    public bool SendToxicity { get; init; } = Rulings.Gas.SendToxicity;

    /// <summary>
    /// The client's own toxicity resource id — <b>611</b>, <c>Resources.txt:145</c> column
    /// <c>*ID</c>. [CLIENT]
    /// </summary>
    public uint ToxicityResourceId { get; init; } = Rulings.Gas.ToxicityResourceId;

    /// <summary>
    /// The client's own toxicity resource <i>type</i> — <b>75</b>, <c>Resources.txt:145</c> column
    /// <c>RESOURCE_TYPE</c>. [CLIENT]
    /// <para>
    /// Unlike health (1/1) and stamina (6/6) the id and the type differ on this row, and the
    /// <c>8d</c> ResourceEvent carries both — swapping them is a silent no-op on the client's own
    /// lookup rather than an error.
    /// </para>
    /// </summary>
    public uint ToxicityResourceType { get; init; } = Rulings.Gas.ToxicityResourceType;

    /// <summary>
    /// The full meter — <b>180 000</b>, <c>Resources.txt:145</c> column <c>MAX_VALUE</c>. [CLIENT]
    /// It contradicts Z1's <c>ToxicityMax = 100</c> (<c>ZoneGasRetail.cs:348</c>) and the client
    /// wins: it is the client's own bar and the client's own percentage calc. <c>INITIAL_VALUE</c>
    /// is 0 and the row carries no <c>VALUE_MARKER</c> tiers, so the meter is a plain 0 → 180 000
    /// fill with no thresholds.
    /// </summary>
    public uint ToxicityMaxValue { get; init; } = Rulings.Gas.ToxicityMaxValue;

    /// <summary>
    /// The client's own fill rate — <b>1</b> per millisecond, <c>Resources.txt:145</c> column
    /// <c>REGEN_PER_MS</c>, charged on a <see cref="ToxicityRegenTickMs"/> beat. [CLIENT]
    /// 1 000 units a second is <b>180 s from empty to full</b>, which is the fill the owner's
    /// ruling (d) names. <c>FLAG_INIT_WITH_DISABLED_REGEN = 1</c> on the same row is why it does not
    /// run until a server arms it.
    /// </summary>
    public float ToxicityRegenPerMs { get; init; } = Rulings.Gas.ToxicityRegenPerMs;

    /// <summary>
    /// The beat <see cref="ToxicityRegenPerMs"/> is charged on — <b>1 000 ms</b>,
    /// <c>Resources.txt:145</c> column <c>REGEN_TICK_MSEC</c>. [CLIENT] Equal to
    /// <see cref="TickPeriodMs"/>, which is why the server's accumulator moves in whole gas ticks
    /// and stays in step with a client that ticks the resource itself.
    /// </summary>
    public uint ToxicityRegenTickMs { get; init; } = Rulings.Gas.ToxicityRegenTickMs;

    /// <summary>
    /// How fast the meter drains outside the gas, in units per second — <b>1 000</b> (D279), the
    /// one toxicity number the client does <b>not</b> supply.
    /// <para>
    /// <c>Resources.txt:145</c>'s <c>BURN_PER_MSEC</c> is <b>0</b> and
    /// <c>FLAG_INIT_WITH_DISABLED_BURN</c> is 1, so a client-side drain off the row's own columns
    /// would drain nothing at all: the drain is a server number or there is no drain. 1 000 a second
    /// mirrors the client's own fill exactly — 180 s out, 180 s back — which is the only choice that
    /// needs no second story about why the two differ. 0 makes the meter one-way.
    /// </para>
    /// <para>Override: <c>CRANBERRY_GAS_TOXICITY_DRAIN</c>.</para>
    /// </summary>
    public uint ToxicityDrainPerSecond { get; init; } = Rulings.Gas.ToxicityDrainPerSecond;

    /// <summary>
    /// How many toxicity units a second in the gas is worth, derived from the client's rate:
    /// <c>ToxicityRegenPerMs × 1000 ms/s</c> = 1 × 1000 =
    /// <b>1 000</b>, i.e. <see cref="ToxicityMaxValue"/> / 1 000 = 180 s from empty to full. It is a
    /// derivation and not a constant on purpose — the two numbers it multiplies are the client's,
    /// and a hand-typed 1 000 beside them would be a third copy free to drift.
    /// </summary>
    public uint ToxicityFillPerSecond =>
        (uint)Math.Clamp(
            Math.Round((double)ToxicityRegenPerMs * 1000d, MidpointRounding.AwayFromZero),
            0d,
            uint.MaxValue);

    /// <summary>
    /// Send <c>ClientUpdate.DamageInfo (11 1e)</c> for each gas tick. **Off by default: the field
    /// semantics are unverified.** docs/15 §4 / docs/16 §4e prove the reader's field order and
    /// width and prove the packet drives the local player's take-damage path
    /// (<c>vtable+0x9d8</c>), but which of fields 5–10 is the damage amount is BLOCKED
    /// (research-gaps G-08 is exactly the live experiment that closes it). Until then the server
    /// moves health with <see cref="Hitpoints"/> (<c>11 01</c>), whose current/max pair is
    /// anchored by the client's own <c>(current*100)/max</c> calc (docs/16 §4d).
    /// </summary>
    public bool SendDamageInfo { get; init; }

    /// <summary>
    /// Unverified layout variant for <see cref="SendDamageInfo"/>. False (the default) reads the
    /// parser's leading <c>u8; u16</c> as the packet's own <c>11 1e 00</c> header, exactly as
    /// <c>ce 02</c>'s self-framing reader <c>FUN_140baff10</c> does and as
    /// <c>ClientUpdate.Hitpoints</c>'s 11-byte total requires. True repeats them as payload after
    /// the header, which is how docs/15 §4 tabulated the same reader (a ≈36-byte packet). One live
    /// tick decides it.
    /// </summary>
    public bool DamageInfoRepeatsPrefix { get; init; }

    /// <summary>
    /// The wall speed the <see cref="TargetMatchLengthMs"/> budget implies for the current radii,
    /// phase count and holds — the identity <see cref="ShrinkSpeedMetresPerSecond"/> was solved
    /// from (docs/53 §5.3). 0 when the budget leaves no time to travel in.
    /// </summary>
    public double SolvedShrinkSpeedMetresPerSecond()
    {
        double travelMs = (double)TargetMatchLengthMs
            - FirstMoveDelayMs
            - ((double)Math.Max(PhaseCount - 1, 0) * InterPhaseHoldMs);
        return travelMs <= 0d ? 0d : (InitialRadius - FinalRadius) / (travelMs / 1000d);
    }

    /// <summary>
    /// The worst-case speed of the drawn ring's <b>leading edge</b> — the number a player has to
    /// outrun, and the one <see cref="ShrinkSpeedMetresPerSecond"/> is <i>not</i>. It walks the
    /// ladder and returns the largest <c>(Δr · (1 + CentreDriftFraction)) / advance</c> of any phase,
    /// because the drawn ring's centre and radius are interpolated on one <c>t</c> and a player on
    /// the far side meets the sum of the two rates (docs/77 §3–§4).
    /// <para>
    /// It is the exact bound rather than the analytic <c>v · (1 + f)</c>: the per-phase advance is
    /// rounded to <see cref="AdvanceRoundingMs"/>, which moves the true ceiling by a few thousandths,
    /// and this form is also correct under <see cref="GasPacing.FixedWindows"/>, where
    /// <see cref="ShrinkSpeedMetresPerSecond"/> is not read at all.
    /// </para>
    /// </summary>
    public double LeadingEdgeSpeedCeiling()
    {
        double drift = 1d + Math.Max(0f, CentreDriftFraction);
        double worst = 0d;
        for (int phase = 1; phase <= Math.Max(PhaseCount, 1); phase++)
        {
            double metres = RadiusForPhase(phase - 1) - (double)RadiusForPhase(phase);
            double seconds = AdvanceMsForPhase(phase) / 1000d;
            if (metres > 0d && seconds > 0d)
            {
                worst = Math.Max(worst, metres * drift / seconds);
            }
        }

        return worst;
    }

    /// <summary>
    /// The largest <see cref="CentreDriftFraction"/> this ladder can carry without its leading edge
    /// exceeding <paramref name="maxEdgeMetresPerSecond"/>; negative when even a perfectly concentric
    /// shrink is already too fast, which is the state the 6 000 m play area was in (docs/77 §4.2).
    /// Used by <see cref="Validate"/> so an operator is told the fix and not just the failure.
    /// </summary>
    public double MaxCentreDriftFractionFor(double maxEdgeMetresPerSecond)
    {
        double concentric = LeadingEdgeSpeedCeiling() / (1d + Math.Max(0f, CentreDriftFraction));
        return concentric <= 0d ? 0d : (maxEdgeMetresPerSecond / concentric) - 1d;
    }

    /// <summary>
    /// Damage per tick for a 1-based phase index, clamped into <c>1..PhaseCount</c> and then into
    /// <see cref="DamagePerPhase"/> (a match longer than the table repeats its last entry).
    /// </summary>
    public uint DamageForPhase(int phaseIndex)
    {
        int clamped = Math.Clamp(phaseIndex, 1, Math.Max(PhaseCount, 1));
        IReadOnlyList<uint> table = DamagePerPhase;
        if (table is { Count: > 0 })
        {
            return table[Math.Min(clamped, table.Count) - 1];
        }

        return FirstPhaseDamage + ((uint)(clamped - 1) * DamageIncreasePerPhase);
    }

    /// <summary>The per-player amount after applying the full-toxicity threshold.</summary>
    public uint DamageForPhase(int phaseIndex, bool fullToxicity)
    {
        uint normal = DamageForPhase(phaseIndex);
        if (!fullToxicity || DamageAtFullToxicityPerPhase is not { Count: > 0 } table)
        {
            return normal;
        }

        int clamped = Math.Clamp(phaseIndex, 1, Math.Max(PhaseCount, 1));
        return Math.Max(normal, table[Math.Min(clamped, table.Count) - 1]);
    }

    /// <summary>
    /// How long a 1-based phase stays revealed-but-still before its ring starts moving.
    /// <see cref="GasPacing.PhaseTable"/>: the corresponding <see cref="HoldDurationsMs"/> entry.
    /// <see cref="GasPacing.SpeedPaced"/>: <c>FirstMoveDelayMs − FirstRevealDelayMs</c> for phase 1,
    /// <see cref="InterPhaseHoldMs"/> after that. <see cref="GasPacing.FixedWindows"/>:
    /// <see cref="ShrinkWarningMs"/>, capped at the window so a short window cannot invert.
    /// </summary>
    public uint HoldMsForPhase(int phaseIndex)
    {
        int clamped = Math.Clamp(phaseIndex, 1, Math.Max(PhaseCount, 1));
        if (Pacing == GasPacing.PhaseTable)
        {
            return HoldDurationsMs[clamped - 1];
        }

        if (Pacing == GasPacing.FixedWindows)
        {
            return Math.Min(ShrinkWarningMs, LegacyWindowForPhase(clamped));
        }

        if (clamped > 1)
        {
            return InterPhaseHoldMs;
        }

        return FirstMoveDelayMs > FirstRevealDelayMs ? FirstMoveDelayMs - FirstRevealDelayMs : 0u;
    }

    /// <summary>
    /// How long a 1-based phase's ring spends travelling.
    /// <see cref="GasPacing.PhaseTable"/>: the corresponding <see cref="AdvanceDurationsMs"/> entry.
    /// <see cref="GasPacing.SpeedPaced"/>: <c>(radius(i−1) − radius(i)) / speed</c>, rounded to
    /// <see cref="AdvanceRoundingMs"/>. <see cref="GasPacing.FixedWindows"/>: the window minus its
    /// warning head.
    /// </summary>
    public uint AdvanceMsForPhase(int phaseIndex)
    {
        int clamped = Math.Clamp(phaseIndex, 1, Math.Max(PhaseCount, 1));
        if (Pacing == GasPacing.PhaseTable)
        {
            return AdvanceDurationsMs[clamped - 1];
        }

        if (Pacing == GasPacing.FixedWindows)
        {
            uint window = LegacyWindowForPhase(clamped);
            return window - Math.Min(ShrinkWarningMs, window);
        }

        double metres = RadiusForPhase(clamped - 1) - (double)RadiusForPhase(clamped);
        if (!(metres > 0d) || !(ShrinkSpeedMetresPerSecond > 0f))
        {
            return 0u;
        }

        double ms = metres / ShrinkSpeedMetresPerSecond * 1000d;
        ms = AdvanceRoundingMs > 1
            ? Math.Round(ms / AdvanceRoundingMs, MidpointRounding.AwayFromZero) * AdvanceRoundingMs
            : Math.Round(ms, MidpointRounding.AwayFromZero);

        return (uint)Math.Clamp(ms, 1d, int.MaxValue);
    }

    /// <summary>
    /// Reveal → fully-closed window for a 1-based phase index — <see cref="HoldMsForPhase"/> plus
    /// <see cref="AdvanceMsForPhase"/>. Under <see cref="GasPacing.FixedWindows"/> this is exactly
    /// the wave-4 shorten-with-a-floor formula, unchanged.
    /// </summary>
    public uint WindowForPhase(int phaseIndex) =>
        Pacing == GasPacing.FixedWindows
            ? LegacyWindowForPhase(Math.Clamp(phaseIndex, 1, Math.Max(PhaseCount, 1)))
            : HoldMsForPhase(phaseIndex) + AdvanceMsForPhase(phaseIndex);

    /// <summary>
    /// Target radius of a 1-based phase index. Explicit tables supply exact values; an inherited
    /// table preserves the default shape between the requested initial and final radii. Without
    /// a table, phases remove equal fractions geometrically. Rescaled and geometric values are
    /// rounded to <see cref="RadiusRoundingMetres"/> wherever rounding cannot disturb the ladder.
    /// Phase 0 is <see cref="InitialRadius"/> and the last phase is <see cref="FinalRadius"/>.
    /// </summary>
    public float RadiusForPhase(int phaseIndex)
    {
        if (phaseIndex <= 0)
        {
            return InitialRadius;
        }

        if (phaseIndex >= PhaseCount)
        {
            return FinalRadius;
        }

        // Explicit tables and unchanged inherited geometry retain the exact source values,
        // including fractions. Endpoint/rounding overrides remap the inherited table below.
        if (RadiusLadder is { Count: > 0 } ladder
            && (_radiusLadder is not null
                || (InitialRadius == Rulings.Gas.InitialRadius
                    && FinalRadius == Rulings.Gas.FinalRadius
                    && RadiusRoundingMetres == Rulings.Gas.RadiusRoundingMetres)))
        {
            return ladder[phaseIndex - 1];
        }

        double raw = RawRadiusForPhase(phaseIndex);
        float step = RadiusRoundingMetres;
        if (!float.IsFinite(step) || step <= 0f)
        {
            return (float)raw;
        }

        // Rounding moves a radius by at most step/2, so two raw radii more than a full step apart
        // stay strictly ordered afterwards. Anything tighter than that keeps its raw value.
        if (RawRadiusForPhase(phaseIndex - 1) - raw <= step
            || raw - RawRadiusForPhase(phaseIndex + 1) <= step)
        {
            return (float)raw;
        }

        return (float)(Math.Round(raw / step, MidpointRounding.AwayFromZero) * step);
    }

    /// <summary>
    /// Every value that measures time, multiplied by <paramref name="factor"/> — the whole schedule
    /// played faster or slower with its shape, its radii and its damage table untouched. This is
    /// what <c>CRANBERRY_GAS_SCALE</c> applies: a 25-minute match is impractical to click-test, and
    /// <c>0.2</c> plays all ten waves in five minutes. The wall speed is <b>divided</b> by the
    /// factor, because under <see cref="GasPacing.SpeedPaced"/> every advance is
    /// <c>metres / speed</c> and the metres must not move.
    /// </summary>
    public GasSettings ScaledBy(float factor)
    {
        if (!float.IsFinite(factor) || factor <= 0f || factor == 1f)
        {
            return this;
        }

        static uint Scale(uint value, float by, uint floor) =>
            (uint)Math.Max(floor, Math.Min(Math.Round(value * (double)by, MidpointRounding.AwayFromZero), uint.MaxValue));

        static IReadOnlyList<uint> ScaleTable(IReadOnlyList<uint> table, float by, uint floor) =>
            table.Count == 0 ? table : table.Select(value => value == 0 ? 0u : Scale(value, by, floor)).ToArray();

        return this with
        {
            HoldDurationsMs = ScaleTable(HoldDurationsMs, factor, 0),
            AdvanceDurationsMs = ScaleTable(AdvanceDurationsMs, factor, 1),
            FirstRevealDelayMs = Scale(FirstRevealDelayMs, factor, 0),
            FirstMoveDelayMs = Scale(FirstMoveDelayMs, factor, 0),
            InterPhaseHoldMs = Scale(InterPhaseHoldMs, factor, 0),
            ShrinkWarningMs = Scale(ShrinkWarningMs, factor, 0),
            PhaseHoldMs = Scale(PhaseHoldMs, factor, 0),
            FirstPhaseWindowMs = Scale(FirstPhaseWindowMs, factor, 1),
            PhaseWindowShorteningMs = Scale(PhaseWindowShorteningMs, factor, 0),
            MinimumPhaseWindowMs = Scale(MinimumPhaseWindowMs, factor, 1),
            TargetMatchLengthMs = Scale(TargetMatchLengthMs, factor, 1),
            // The rounding STEP is a time too, so it scales with the clock. Left at 100 ms on a
            // one-fifth clock it is a 10 % quantisation of the endgame advances rather than a 1 %
            // one, which inflates the leading edge of the last waves past the (also scaled) rail.
            AdvanceRoundingMs = Scale(AdvanceRoundingMs, factor, 1),
            // The two wave-9 heal beats are wall-clock cadences, so a compressed match wants them
            // compressed too: a 15 s circle re-send inside a scale-0.2 phase whose whole hold is
            // 3 s would fire once or not at all. Zero means "off" and must stay off — Scale's floor
            // would otherwise turn a disabled heal into a 50 ms one (docs/87 §7 edit 6).
            HudHealIntervalMs = HudHealIntervalMs == 0 ? 0 : Scale(HudHealIntervalMs, factor, 50),
            SafeZoneHealIntervalMs = SafeZoneHealIntervalMs == 0 ? 0 : Scale(SafeZoneHealIntervalMs, factor, 250),
            ShrinkSpeedMetresPerSecond = ShrinkSpeedMetresPerSecond / factor,
            // The leading-edge rail is a wall-clock speed, so it has to move with the clock or a
            // compressed bring-up match fails Validate for being compressed. The drift fraction
            // itself is dimensionless and stays put: a scaled match keeps the same circles.
            MaxEdgeSpeedMetresPerSecond = MaxEdgeSpeedMetresPerSecond / factor,
        };
    }

    /// <summary>Rejects a settings record the schedule cannot build a monotonic match from.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Pacing))
        {
            throw new ArgumentOutOfRangeException(nameof(Pacing), Pacing, "Unknown gas pacing mode.");
        }

        if (PhaseCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PhaseCount), PhaseCount, "A match needs at least one gas phase.");
        }

        if (!float.IsFinite(InitialRadius) || InitialRadius <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialRadius), InitialRadius, "The initial radius must be a positive, finite metre value.");
        }

        if (!float.IsFinite(FinalRadius) || FinalRadius <= 0f || FinalRadius > InitialRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(FinalRadius), FinalRadius, "The final radius must be positive and no larger than the initial radius.");
        }

        ValidateRadiusLadder();

        if (Pacing == GasPacing.PhaseTable)
        {
            ValidatePhaseDurations();
        }

        if (Pacing != GasPacing.PhaseTable && MinimumPhaseWindowMs == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumPhaseWindowMs), MinimumPhaseWindowMs, "A phase window cannot be zero-length.");
        }

        if (Pacing != GasPacing.PhaseTable && FirstPhaseWindowMs < MinimumPhaseWindowMs)
        {
            throw new ArgumentOutOfRangeException(nameof(FirstPhaseWindowMs), FirstPhaseWindowMs, "The first phase window cannot be shorter than the floor.");
        }

        if (Pacing == GasPacing.SpeedPaced)
        {
            if (!float.IsFinite(ShrinkSpeedMetresPerSecond) || ShrinkSpeedMetresPerSecond <= 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ShrinkSpeedMetresPerSecond),
                    ShrinkSpeedMetresPerSecond,
                    "A speed-paced schedule needs a positive, finite wall speed in metres per second.");
            }

            if (FirstMoveDelayMs < FirstRevealDelayMs)
            {
                // The ring would start closing before the client had ever been shown the circle.
                throw new ArgumentOutOfRangeException(
                    nameof(FirstMoveDelayMs),
                    FirstMoveDelayMs,
                    $"Phase 1 cannot start moving ({FirstMoveDelayMs} ms) before it is revealed ({FirstRevealDelayMs} ms).");
            }

            if (AdvanceRoundingMs == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(AdvanceRoundingMs), AdvanceRoundingMs, "The advance rounding step must be at least 1 ms.");
            }
        }

        if (Pacing is GasPacing.SpeedPaced or GasPacing.PhaseTable)
        {
            // The wave-8 guard, and the one this whole lane exists for: what a player outruns is the
            // leading edge, not the radius (docs/77 §3). Refused here rather than in a test, because
            // CRANBERRY_GAS_* can reach the same combination at runtime — GasTuning catches the throw
            // and falls back to the preset rather than taking the host down.
            double ceiling = LeadingEdgeSpeedCeiling();
            if (ceiling > MaxEdgeSpeedMetresPerSecond)
            {
                double allowed = MaxCentreDriftFractionFor(MaxEdgeSpeedMetresPerSecond);
                throw new ArgumentOutOfRangeException(
                    nameof(CentreDriftFraction),
                    CentreDriftFraction,
                    $"The drawn ring's leading edge would close at {ceiling:0.000} m/s, above "
                    + $"{nameof(MaxEdgeSpeedMetresPerSecond)} = {MaxEdgeSpeedMetresPerSecond:0.000}. "
                    + (Pacing == GasPacing.PhaseTable
                        ? $"Increase the affected {nameof(AdvanceDurationsMs)} entry or reduce the radius drop or centre drift."
                        : allowed > 0d
                        ? $"Lower {nameof(CentreDriftFraction)} to {allowed:0.000} or below, or slow the wall."
                        : $"No {nameof(CentreDriftFraction)} works: the concentric wall is already too "
                          + $"fast at {nameof(InitialRadius)} = {InitialRadius:0.#} m, so the play area "
                          + "has to come down or the match budget has to go up."));
            }
        }

        if (!float.IsFinite(CentreDriftFraction) || CentreDriftFraction <= 0f || CentreDriftFraction > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CentreDriftFraction),
                CentreDriftFraction,
                "The centre drift fraction must be in (0, 1]: above 1 the next circle would not be "
                + "contained in the current one, and at or below 0 the gas would never move.");
        }

        if (!float.IsFinite(DriftConeDegrees) || DriftConeDegrees <= 0f || DriftConeDegrees > 360f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DriftConeDegrees),
                DriftConeDegrees,
                "The drift cone must be in (0, 360] degrees; 360 is the uniform-angle draw.");
        }

        if (!float.IsFinite(MaxEdgeSpeedMetresPerSecond) || MaxEdgeSpeedMetresPerSecond <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxEdgeSpeedMetresPerSecond),
                MaxEdgeSpeedMetresPerSecond,
                "The leading-edge speed rail must be a positive, finite metres-per-second value.");
        }

        if (!Enum.IsDefined(PreMoveRing))
        {
            throw new ArgumentOutOfRangeException(
                nameof(PreMoveRing),
                PreMoveRing,
                "Unknown pre-move ring position.");
        }

        if (!Enum.IsDefined(CentrePlan))
        {
            throw new ArgumentOutOfRangeException(
                nameof(CentrePlan),
                CentrePlan,
                "Unknown gas centre plan.");
        }

        if (!Enum.IsDefined(RingBlendMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(RingBlendMode),
                RingBlendMode,
                "Unknown ce 01 blend mode.");
        }

        if (!float.IsFinite(PoiWeightExponent) || PoiWeightExponent < 0f || PoiWeightExponent > 4f)
        {
            // Above 4 the largest volume takes essentially every match, which is the opposite of
            // "the endgame must be able to land in any of them" (D277).
            throw new ArgumentOutOfRangeException(
                nameof(PoiWeightExponent),
                PoiWeightExponent,
                "The POI weight exponent must be a finite value in 0..4 (0 = the nine are equally likely).");
        }

        if (!float.IsFinite(PlayAreaLeadMetres) || PlayAreaLeadMetres < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PlayAreaLeadMetres),
                PlayAreaLeadMetres,
                "The play-area lead must be a finite, non-negative metre value (0 pins the play area).");
        }

        if (ToxicityMaxValue == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ToxicityMaxValue),
                ToxicityMaxValue,
                "The toxicity meter's maximum must be positive — it is the divisor of the client's own percentage calc.");
        }

        if (ToxicityRegenTickMs == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ToxicityRegenTickMs),
                ToxicityRegenTickMs,
                "The toxicity regen tick must be positive.");
        }

        if (!float.IsFinite(ToxicityRegenPerMs) || ToxicityRegenPerMs < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ToxicityRegenPerMs),
                ToxicityRegenPerMs,
                "The toxicity regen rate must be a finite, non-negative value.");
        }

        if (!float.IsFinite(RadiusRoundingMetres) || RadiusRoundingMetres < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(RadiusRoundingMetres), RadiusRoundingMetres, "The radius rounding step must be a finite, non-negative metre value.");
        }

        if (TickPeriodMs == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TickPeriodMs), TickPeriodMs, "The damage tick period must be positive.");
        }

        if (SafeZoneUpdateIntervalMs == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SafeZoneUpdateIntervalMs), SafeZoneUpdateIntervalMs, "The safe-zone update interval must be positive.");
        }

        if (MaxHitpoints == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxHitpoints), MaxHitpoints, "The health bar must be positive.");
        }

        if (HostTickIntervalMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HostTickIntervalMs), HostTickIntervalMs, "The host pump interval must be positive.");
        }

        if (RingBlendMs == 0 || RingBlendMs > int.MaxValue)
        {
            // FUN_140bbed00 divides by this field with no zero guard, and reads it as the i32 at
            // gas object +0x54 (docs/18 §1a field 6). A value above int.MaxValue therefore lands as
            // a negative divisor: the eased centre and radius run away from the target instead of
            // toward it, for the rest of the match.
            throw new ArgumentOutOfRangeException(
                nameof(RingBlendMs),
                RingBlendMs,
                "The ce 01 blend time must be between 1 and int.MaxValue milliseconds (the client reads +0x54 as i32).");
        }
    }

    private void ValidateRadiusLadder()
    {
        IReadOnlyList<float> ladder = RadiusLadder;
        if (ladder.Count == 0)
        {
            return;
        }

        if (ladder.Count != PhaseCount)
        {
            throw new ArgumentException(
                $"The radius ladder needs exactly {PhaseCount} entries, including the final radius.",
                nameof(RadiusLadder));
        }

        // An inherited table is defined on the configured default bounds. Its affine remap
        // preserves that ordering on the requested bounds, including an unchanged-radius match.
        float previous = _radiusLadder is null ? Rulings.Gas.InitialRadius : InitialRadius;
        float final = _radiusLadder is null ? Rulings.Gas.FinalRadius : FinalRadius;
        for (int index = 0; index < ladder.Count; index++)
        {
            float radius = ladder[index];
            if (!float.IsFinite(radius) || radius <= 0f || radius >= previous)
            {
                throw new ArgumentException(
                    $"Radius ladder phase {index + 1} must be positive, finite and smaller than the preceding radius ({previous} m).",
                    nameof(RadiusLadder));
            }

            previous = radius;
        }

        if (previous != final)
        {
            throw new ArgumentException(
                $"The last radius ladder entry must equal its final radius ({final} m).",
                nameof(RadiusLadder));
        }
    }

    private void ValidatePhaseDurations()
    {
        if (HoldDurationsMs is null || HoldDurationsMs.Count != PhaseCount)
        {
            throw new ArgumentException(
                $"The hold-duration table needs exactly {PhaseCount} entries, including phase 1.",
                nameof(HoldDurationsMs));
        }

        if (AdvanceDurationsMs is null || AdvanceDurationsMs.Count != PhaseCount)
        {
            throw new ArgumentException(
                $"The advance-duration table needs exactly {PhaseCount} positive entries.",
                nameof(AdvanceDurationsMs));
        }

        for (int index = 0; index < PhaseCount; index++)
        {
            if (AdvanceDurationsMs[index] == 0)
            {
                throw new ArgumentException(
                    $"Phase {index + 1} needs a positive movement duration.",
                    nameof(AdvanceDurationsMs));
            }

            if ((ulong)HoldDurationsMs[index] + AdvanceDurationsMs[index] > uint.MaxValue)
            {
                throw new ArgumentException(
                    $"Phase {index + 1}'s hold and advance exceed the supported window duration.",
                    nameof(HoldDurationsMs));
            }
        }
    }

    /// <summary>The wave-4 shorten-with-a-floor window, kept byte-identical for <see cref="GasPacing.FixedWindows"/>.</summary>
    private uint LegacyWindowForPhase(int phaseIndex)
    {
        uint shortening = (uint)(phaseIndex - 1) * PhaseWindowShorteningMs;
        return shortening >= FirstPhaseWindowMs
            ? MinimumPhaseWindowMs
            : Math.Max(MinimumPhaseWindowMs, FirstPhaseWindowMs - shortening);
    }

    /// <summary>The unrounded inherited-table or geometric radius of a 1-based phase index.</summary>
    private double RawRadiusForPhase(int phaseIndex)
    {
        if (phaseIndex <= 0)
        {
            return InitialRadius;
        }

        if (phaseIndex >= PhaseCount)
        {
            return FinalRadius;
        }

        if (_radiusLadder is null && RadiusLadder is { Count: > 0 } ladder)
        {
            // Preserve the default table's shape when either endpoint changes. Scaling each
            // radius's distance above the final radius keeps both endpoints exact and cannot
            // enlarge an intermediate circle when only the initial radius is reduced.
            double fraction = (ladder[phaseIndex - 1] - (double)Rulings.Gas.FinalRadius)
                / (Rulings.Gas.InitialRadius - (double)Rulings.Gas.FinalRadius);
            return FinalRadius + fraction * (InitialRadius - (double)FinalRadius);
        }

        double ratio = FinalRadius / (double)InitialRadius;
        return InitialRadius * Math.Pow(ratio, phaseIndex / (double)PhaseCount);
    }
}
