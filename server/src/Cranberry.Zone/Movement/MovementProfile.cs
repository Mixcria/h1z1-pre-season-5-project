using System.Globalization;
using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Movement;

/// <summary>
/// The mutually exclusive posture branches of the client's speed-scalar chain
/// <c>FUN_1411acfa0</c>, in the order that <c>else if</c> chain tests them. Crouch, prone and walk
/// each <b>suppress the sprint blend entirely</b> — only the fall-through branch (standing, dry, not
/// walking) applies it (docs/40 §2.1).
/// <para>
/// Every one of these is decided <b>client-side</b> from the local player's own key binds; the
/// server never sets a stance and cannot read one off the wire (docs/40 §5, docs/21 §4). The server
/// supplies the numbers each stance multiplies by, and this enum exists so server-side estimates
/// and tests can name the branches.
/// </para>
/// </summary>
public enum MovementStance
{
    /// <summary>Standing, dry, not walking — the branch that applies the sprint blend.</summary>
    Standing = 0,

    /// <summary>Standing with the sprint toggle on (<c>+0x37e8</c> bit <c>0x80</c>).</summary>
    Sprinting = 1,

    /// <summary>Walk toggle (<c>+0x37ed</c> bit <c>0x01</c>) — a real, key-bound stance in 1148.</summary>
    Walking = 2,

    /// <summary>Crouched (<c>+0x37e7</c> bit <c>0x08</c>).</summary>
    Crouching = 3,

    /// <summary>Prone (<c>+0x37e7</c> bit <c>0x04</c>) — engine-supported, not reachable in KOTK (docs/40 §4.4).</summary>
    Prone = 4,

    /// <summary>Swimming (<c>+0x37e3</c> bit <c>0x20</c>).</summary>
    Swimming = 5,

    /// <summary>Wading in water (<c>+0x37e3</c> bit <c>0x40</c>); the client's own fallback here is 0.5.</summary>
    Wading = 6,
}

/// <summary>
/// Which axis scalar the client multiplies in: <c>speed = base × movementScalar × axisScalar</c>
/// (docs/40 §2).
/// </summary>
public enum MovementAxis
{
    /// <summary>Moving forward — no axis penalty (see <see cref="MovementProfile.AxisScalar"/>).</summary>
    Forward = 0,

    /// <summary>Moving backwards — <c>FUN_1411acb10</c> applies <c>BackpedalSpeedModifier</c>.</summary>
    Backward = 1,

    /// <summary>Strafing — <c>FUN_1411ad740</c> applies <c>StrafeSpeedModifier</c>, unless sprinting.</summary>
    Strafe = 2,

    /// <summary>
    /// Backwards <b>and</b> sideways at once. <c>FUN_141593e80</c> takes the <b>smaller</b> of the
    /// two axis scalars here — <c>if (strafe &lt;= backpedal) use strafe;</c> then one multiply
    /// (docs/49 §4.1). [P]
    /// </summary>
    BackwardStrafe = 3,
}

/// <summary>
/// The six movement-mode speeds Cranberry sends, plus the eight animation blend times, as one
/// record. Every value is commented with its provenance.
/// <para>
/// <b>These are the owner's own numbers now — docs/76, wave 8.</b> They were <c>[DESIGN]</c>
/// through wave 4, because the August client ships <i>no</i> numeric value for any of them
/// (<c>CharacterStatDefinitions.txt</c> is ids and flags with no value column, and
/// <c>MoveInfo.txt</c> is 100 % vehicle rows — docs/40 §4.1). D53 opened the owner's own Z1 server
/// to reading, and every speed and blend time below is now his: the base is his stopwatch
/// calibration <c>ZoneMovement.RetailJogSpeed = 4.10</c> m/s, taken against a door leaf whose width
/// Cranberry re-derived <i>independently</i> from its own client's meshes at the same 1.327 m
/// (docs/55), and the modifiers and blend times are the shipping rows of his
/// <c>ZoneSendSelf.BuildStats3()</c>. Per-field provenance is docs/76 §2.1 and §2.4; the two rows
/// Cranberry deliberately <b>refuses</b> to take from Z1 are <see cref="SwimSpeedModifier"/> and
/// <see cref="ProneRollSpeedModifier"/> (docs/76 §2.7).
/// </para>
/// <para>
/// The client-mechanism comments on each field (the <c>FUN_*</c> / <c>DAT_*</c> citations) are
/// Cranberry's own reverse engineering and are unaffected by any of that — they say what the client
/// <i>does</i> with each number, which is still the most valuable content in this file.
/// </para>
/// <para>
/// Units: metres per second for <see cref="MaxMovementSpeed"/> (the world is in metres —
/// <c>Movement.JumpHeight = 1.3</c>, <c>Movement.StepHeight = .3</c>), a dimensionless multiplier
/// for every <c>*Modifier</c>, and <b>seconds</b> for every <c>*Time</c> (the client feeds them
/// <c>frameMs × DAT_1430ef084</c> where that constant is <c>1e-3</c>) — docs/40 §2.4, §4.2.
/// </para>
/// </summary>
public sealed record MovementProfile
{
    /// <summary>
    /// <c>Movement.SprintStrafeMultiplier</c> = <b>0.75</b>, read out of the client's own
    /// <c>StringHashToValue</c> table by the inline hash <c>0x489772F6</c> in <c>FUN_140c4c1f0</c>.
    /// <para>
    /// <b>Corrected in docs/49 §4.1.</b> docs/40 §2.3 / §8.3 read this as "while sprinting the
    /// strafe axis is forced to a flat 0.75". That is now <b>doubtful [lead]</b>: the only path in
    /// <c>FUN_141593e80</c> that multiplies <i>speed</i> by the strafe scalar requires
    /// <c>fwd == 0 &amp;&amp; !sprinting</c>, and sprinting requires <c>fwd &gt; 0</c>, so the
    /// sprint-strafe branch is unreachable. What 0.75 demonstrably weights is the
    /// <i>pre-normalisation direction vector</i> (<c>latRamp·0.75·right + fwdRamp·forward</c>,
    /// normalised by <c>divps</c>), where it skews the diagonal <b>heading</b> and not the
    /// magnitude. The value itself is real and client-owned; what it does to speed is not settled
    /// (docs/49 §8 q3). It is also the anchor for <see cref="StrafeSpeedModifier"/>.
    /// </para>
    /// </summary>
    public static readonly float SprintStrafeMultiplier = LookupClientValue("Movement.SprintStrafeMultiplier");

    /// <summary>
    /// The client's own tiered-stamina fallbacks, recovered from the <c>.rdata</c> operands
    /// <c>FUN_1411acfa0</c> passes to <c>GetStat</c>: ≥50 % no penalty, 25-50 % ×0.75, 5-25 % ×0.5,
    /// &lt;5 % ×0.1 (docs/40 §4.3). These are <b>real client values</b>, so Cranberry deliberately
    /// sends none of the six stamina stats and lets the client use them. [P]
    /// </summary>
    public static class ClientStaminaFallback
    {
        /// <summary><c>DAT_14312af50</c> = 50.0 — <c>NominalStaminaPercent</c>.</summary>
        public const float NominalPercent = 50f;

        /// <summary><c>DAT_14315f1d4</c> = 25.0 — <c>LowStaminaPercent</c>.</summary>
        public const float LowPercent = 25f;

        /// <summary><c>DAT_1430efbd4</c> = 5.0 — <c>CriticalStaminaPercent</c>.</summary>
        public const float CriticalPercent = 5f;

        /// <summary><c>DAT_14311d6a8</c> = 0.75 — <c>NominalStaminaSpeedModifier</c>.</summary>
        public const float NominalModifier = 0.75f;

        /// <summary><c>DAT_1430efbd0</c> = 0.5 — <c>LowStaminaSpeedModifier</c>.</summary>
        public const float LowModifier = 0.5f;

        /// <summary><c>DAT_1430f0a38</c> = 0.1 — <c>CriticalStaminaSpeedModifier</c>.</summary>
        public const float CriticalModifier = 0.1f;
    }

    /// <summary>
    /// <c>StatId.MaxMovementSpeed</c> (2), <b>m/s</b> — the single absolute speed; every other row is
    /// relative to it. <b>4.10 is the owner's own calibrated jog</b>, <c>ZoneMovement.RetailJogSpeed</c>
    /// (docs/76 §2.3), well under the 12 m/s <c>Vehicle.DefaultMaxDismountSpeed</c> ceiling.
    /// <para>
    /// <b>Wave 8: 5.50 → 4.10 (docs/76 §7.1).</b> The delivery was never wrong — the client reported
    /// a forward plateau of exactly 5.5 m/s against the sent 5.50 in every session of
    /// <c>captures\wire-20260829-220829.txt</c>, and only after that session's <c>0f 40</c> burst
    /// (docs/49 §2.2, §2.3), which is what proves this stat <i>is</i> the absolute base at 1148 and
    /// lands as sent. 5.50 was simply 34 % faster than the owner's server. Note his server sends this
    /// stat as the <b>integer 1</b>: on 1087 the base is client-intrinsic (he measured 4.24 u/s
    /// unaided) and stat 2 is a no-op declaration, so Cranberry has to send explicitly what Z1 gets
    /// for free (docs/76 §2.2). It is still the one knob to reach for if the owner says
    /// <i>everything</i> is the wrong speed, because it scales every mode at once —
    /// <c>CRANBERRY_MOVE_BASE</c>, and <c>=4.00</c> reproduces his client's reported plateaus to the
    /// decimal (docs/76 §2.5).
    /// </para>
    /// </summary>
    public float MaxMovementSpeed { get; init; } = Rulings.Movement.MaxMovementSpeed;

    /// <summary>
    /// <c>StatId.SprintSpeedModifier</c> (5) — the target the blend at <c>char+0x43f8</c> ramps to.
    /// The owner's shipping value, <c>ZoneSendSelf.BuildStats3()</c> row 5. 1.40 ⇒ <b>5.74 m/s</b>
    /// sprint, the fastest legal on-foot speed and the number anything that has to be outrun (the gas
    /// wall) must be sized against.
    /// <para>
    /// <b>Wave 8: 1.20 → 1.40 (docs/76 §2.1).</b> The ratio goes <i>up</i> and the sprint still gets
    /// <i>slower</i>, because the base fell further: 4.10 × 1.40 = 5.74 against wave 4's
    /// 5.50 × 1.20 = 6.60. Do not take the 1.30 in his <c>ZoneSpeed.RetailSprintMultiplier</c>
    /// instead — that constant never reaches his wire (<c>ClientOwnsSpeed</c> short-circuits the
    /// builder) and its own author documents it as "UNMEASURED … not a calibrated figure"
    /// (docs/76 §2.4).
    /// </para>
    /// <para>
    /// Changing the modifier does <b>not</b> change how long the ramp takes: the client's blend is
    /// normalised and reaches its target in <see cref="SprintAccelerationTime"/> seconds whatever the
    /// target is (docs/49 §2.4). Retune live with <c>CRANBERRY_MOVE_SPRINT</c>.
    /// </para>
    /// </summary>
    public float SprintSpeedModifier { get; init; } = Rulings.Movement.SprintSpeedModifier;

    /// <summary>
    /// <c>StatId.WalkSpeedModifier</c> (86) — the walk toggle's branch. The owner's row 86.
    /// <b>Wave 8: 0.45 → 0.30</b> ⇒ 1.23 m/s: a deliberate stroll, less than half of a jog and well
    /// under a crouch, not the "brisk walk" wave 4 had (docs/76 §2.1).
    /// </summary>
    public float WalkSpeedModifier { get; init; } = Rulings.Movement.WalkSpeedModifier;

    /// <summary>
    /// <c>StatId.CrouchSpeedModifier</c> (4). The owner's row 4. <b>Wave 8: 0.55 → 0.70</b>
    /// ⇒ 2.87 m/s — slower in absolute terms than wave 4's 3.03 despite the higher ratio, and now
    /// clearly above walk (1.23) rather than beside it (docs/76 §2.1).
    /// </summary>
    public float CrouchSpeedModifier { get; init; } = Rulings.Movement.CrouchSpeedModifier;

    /// <summary>
    /// <c>StatId.BackpedalSpeedModifier</c> (3) — an <b>axis</b> scalar, applied on top of whatever
    /// stance scalar is in force. The owner's row 3. <b>Wave 8: 0.50 → 0.75</b> ⇒ <b>3.08 m/s</b>
    /// backing away from a standing run.
    /// <para>
    /// <b>The one row where the owner's two rulings disagree, so read this before "fixing" it</b>
    /// (docs/76 §2.6). In wave 4 he called a <i>measured</i> 3.58 m/s backpedal too fast and this
    /// dropped to 0.50 ⇒ 2.75 m/s; his own server ships 0.75. 0.75 is taken because this wave's
    /// instruction is the later one and because the ratio rise is more than paid for by the base
    /// drop — 3.08 m/s is below the 3.58 he actually complained about. If he says it is still too
    /// fast, <c>CRANBERRY_MOVE_BACK=0.67</c> restores his accepted 2.75 m/s absolute at the new base.
    /// </para>
    /// <para>
    /// Second-order, and worth knowing when reading a capture: with backpedal and
    /// <see cref="StrafeSpeedModifier"/> both 0.75, <see cref="AxisScalar"/>'s
    /// <c>BackwardStrafe ⇒ min(strafe, backpedal)</c> is a no-op and a backpedal-strafe can no longer
    /// be told from a pure strafe by speed alone.
    /// </para>
    /// <para>
    /// The mechanism behind those wave-4 measurements still holds and is what makes this arithmetic
    /// trustworthy: 5.50 × 0.65 = 3.575 reported as 3.6, and 5.50 × 0.55 × 0.65 = 1.966 reported as
    /// 2.0 for a crouch-backpedal — one stance multiply and one axis multiply, nothing applied twice
    /// (docs/49 §2.2).
    /// </para>
    /// </summary>
    public float BackpedalSpeedModifier { get; init; } = Rulings.Movement.BackpedalSpeedModifier;

    /// <summary>
    /// <c>StatId.StrafeSpeedModifier</c> (7) — an <b>axis</b> scalar, applied only on a pure lateral
    /// input (<c>fwd == 0</c>): a diagonal run takes no axis penalty at all (docs/49 §4.1).
    /// <b>Client-anchored</b>, not free design — 0.75 is <see cref="SprintStrafeMultiplier"/>, the
    /// sideways penalty this build's own designers wrote into <c>StringHashToValue</c>, so the
    /// sideways cost is the same 25 % in every mode. ⇒ <b>4.13 m/s</b> strafing at a run.
    /// <para>
    /// <b>Wave 8: no change, and it is the best-anchored number in the file</b> — the August
    /// client's own <c>Movement.SprintStrafeMultiplier = .75</c>, <c>MoveInfo.txt</c> row 4001's
    /// <c>MAX_STRAFE 75</c>, and the owner's own <c>BuildStats3()</c> row 7 all say 0.75, so Z1 and
    /// Cranberry already agreed here (docs/76 §2.4). ⇒ 3.08 m/s at the new base.
    /// </para>
    /// <para>
    /// <b>Wave 4: 0.80 → 0.75.</b> The client reported a 4.4 m/s strafe plateau against a sent
    /// 5.50 × 0.80 = 4.40 (docs/49 §2.2). Note there is exactly <b>one</b> strafe number and it
    /// cannot be made asymmetric: left and right share this stat and share
    /// <see cref="StrafeAccelerationTime"/> / <see cref="StrafeDecelerationTime"/>, so "left differs
    /// from right" is an animation or camera problem, not a stat (docs/49 §6). Retune live with
    /// <c>CRANBERRY_MOVE_STRAFE</c>.
    /// </para>
    /// </summary>
    public float StrafeSpeedModifier { get; init; } = Rulings.Movement.StrafeSpeedModifier;

    /// <summary>
    /// <c>StatId.ProneSpeedModifier</c> (67). The owner's Z1 row 67: ?0.40, or 1.64 m/s.
    /// The current client reaches prone normally; the earlier unreachable annotation was wrong.
    /// </summary>
    public float ProneSpeedModifier { get; init; } = Rulings.Movement.ProneSpeedModifier;

    /// <summary>
    /// <c>StatId.ProneRollSpeedModifier</c> (84): the lateral scalar when native
    /// <c>+0x37e7 &amp; 5 == 5</c>. Hold Sprint sets the qualifier in <c>14158ffb0</c>.
    /// ?2.50 makes the default roll 4.10 m/s, faster than crawling; this is server policy.
    /// The old ?0.50 made the roll slower than ordinary prone strafing. See
    /// <c>docs/movement-input-20260906.md</c> for the recovered input branch.
    /// </summary>
    public float ProneRollSpeedModifier { get; init; } = Rulings.Movement.ProneRollSpeedModifier;

    /// <summary>
    /// <c>StatId.SwimSpeedModifier</c> (6). [DESIGN] 0.55 ⇒ 2.26 m/s.
    /// <para>
    /// <b>REFUSED from Z1, on purpose — do not "fix" this to 0 (docs/76 §2.7).</b> The owner's
    /// server ships row 6 as the integer <b>0</b>. On 1087 that is harmless because the swim branch
    /// is unused there; at 1148 swim is a live stance branch (<c>FUN_1411acfa0</c>,
    /// <c>+0x37e3 &amp; 0x20</c>) and a 0 here is a swimmer who cannot move. The value crosses only
    /// where the behaviour is the owner's intent, and a frozen swimmer is not.
    /// </para>
    /// </summary>
    public float SwimSpeedModifier { get; init; } = Rulings.Movement.SwimSpeedModifier;

    /// <summary>
    /// <c>StatId.WaterSpeedModifier</c> (85) — wading. The owner's row 85.
    /// <b>Wave 8: 0.75 → 0.80</b> ⇒ 3.28 m/s, still deliberately overriding the client's own 0.5
    /// fallback (<c>DAT_1430efbd0</c>) so wading is not punishing.
    /// </summary>
    public float WaterSpeedModifier { get; init; } = Rulings.Movement.WaterSpeedModifier;

    /// <summary>Input blend time in seconds. Zero snaps immediately to the target, per the
    /// 2026-09-06 responsiveness request. Existing CRANBERRY_MOVE_* overrides still apply.</summary>
    public float SprintAccelerationTime { get; init; } = Rulings.Movement.SprintAccelerationTime;

    /// <summary>Input blend time in seconds. Zero snaps immediately to the target, per the
    /// 2026-09-06 responsiveness request. Existing CRANBERRY_MOVE_* overrides still apply.</summary>
    public float SprintDecelerationTime { get; init; } = Rulings.Movement.SprintDecelerationTime;

    /// <summary>Input blend time in seconds. Zero snaps immediately to the target, per the
    /// 2026-09-06 responsiveness request. Existing CRANBERRY_MOVE_* overrides still apply.</summary>
    public float ForwardAccelerationTime { get; init; } = Rulings.Movement.ForwardAccelerationTime;

    /// <summary>Input blend time in seconds. Zero snaps immediately to the target, per the
    /// 2026-09-06 responsiveness request. Existing CRANBERRY_MOVE_* overrides still apply.</summary>
    public float ForwardDecelerationTime { get; init; } = Rulings.Movement.ForwardDecelerationTime;

    /// <summary>Input blend time in seconds. Zero snaps immediately to the target, per the
    /// 2026-09-06 responsiveness request. Existing CRANBERRY_MOVE_* overrides still apply.</summary>
    public float BackAccelerationTime { get; init; } = Rulings.Movement.BackAccelerationTime;

    /// <summary>Input blend time in seconds. Zero snaps immediately to the target, per the
    /// 2026-09-06 responsiveness request. Existing CRANBERRY_MOVE_* overrides still apply.</summary>
    public float BackDecelerationTime { get; init; } = Rulings.Movement.BackDecelerationTime;

    /// <summary>Input blend time in seconds. Zero snaps immediately to the target, per the
    /// 2026-09-06 responsiveness request. Existing CRANBERRY_MOVE_* overrides still apply.</summary>
    public float StrafeAccelerationTime { get; init; } = Rulings.Movement.StrafeAccelerationTime;

    /// <summary>Input blend time in seconds. Zero snaps immediately to the target, per the
    /// 2026-09-06 responsiveness request. Existing CRANBERRY_MOVE_* overrides still apply.</summary>
    public float StrafeDecelerationTime { get; init; } = Rulings.Movement.StrafeDecelerationTime;

    /// <summary>
    /// Existing speed scalars with immediate input ramps, per the September 6 responsiveness request.
    /// <c>MovementTuning.Wave5Legacy</c> retains the historical wave-4/5 profile for comparison.
    /// Identical in value
    /// to <see cref="MovementTuning.Aug2017Default"/>, which returns <b>this same instance</b>: the
    /// zone's stat-burst send short-circuits on
    /// <c>ReferenceEquals(tracker.Profile, options.Movement)</c>, so a second equal-but-distinct
    /// default would re-arm the burst on every resync (docs/49 §I3).
    /// </summary>
    public static MovementProfile Default { get; } = new();

    /// <summary>
    /// Number of entries <see cref="ToStats"/> emits: docs/40 §6's ten rows (<c>MaxMovementSpeed</c>
    /// plus nine modifiers) and the eight blend times — the "one list of 18 entries" of §8.1, whose
    /// <c>0f 40</c> is <c>14 + 18×13 = 248</c> bytes.
    /// </summary>
    public const int StatCount = 18;

    /// <summary>Run speed, m/s — the base with no stance or axis scalar.</summary>
    public float RunSpeed => MaxMovementSpeed;

    /// <summary>Sprint speed, m/s, once the blend at <c>char+0x43f8</c> has fully ramped.</summary>
    public float SprintSpeed => MaxMovementSpeed * SprintSpeedModifier;

    /// <summary>Walk speed, m/s (the walk toggle suppresses the sprint blend).</summary>
    public float WalkSpeed => MaxMovementSpeed * WalkSpeedModifier;

    /// <summary>Crouch speed, m/s (crouch suppresses the sprint blend).</summary>
    public float CrouchSpeed => MaxMovementSpeed * CrouchSpeedModifier;

    /// <summary>Backpedal speed from a standing run, m/s.</summary>
    public float BackpedalSpeed => MaxMovementSpeed * BackpedalSpeedModifier;

    /// <summary>Strafe speed from a standing run, m/s (non-sprinting).</summary>
    public float StrafeSpeed => MaxMovementSpeed * StrafeSpeedModifier;

    /// <summary>
    /// Sprint-strafe speed, m/s, on docs/40 §2.3's reading (the axis forced to
    /// <see cref="SprintStrafeMultiplier"/> = 0.75). <b>Unreachable and unverifiable</b>: sprinting
    /// requires forward input and the strafe multiply requires none, so no real input produces this
    /// (docs/49 §4.1, §8 q3). Kept because the 0.75 is a real client value; do not read the number
    /// it returns as a speed the client will ever move at.
    /// </summary>
    public float SprintStrafeSpeed => MaxMovementSpeed * SprintSpeedModifier * SprintStrafeMultiplier;

    /// <summary>
    /// The fastest speed any legal on-foot input can produce: sprinting forward with full stamina
    /// and no water. This is the <b>only</b> bound a server-side speed check can rely on, because
    /// the client never reports its stance (docs/40 §5).
    /// </summary>
    public float MaxLegalSpeed => MaxMovementSpeed * MathF.Max(1f, SprintSpeedModifier);

    /// <summary>
    /// The stance scalar of <c>FUN_1411acfa0</c> — its <c>else if</c> chain, in order. The two
    /// factors ahead of it in the client's chain (<c>char+0x45f0</c> and the multiplier list at
    /// <c>char+0x4600</c>) and the held item's <c>WeaponMoveSpeedScalar</c> are not modelled here:
    /// their sources are docs/40 §9 question 4 and are outside this lane.
    /// </summary>
    public float StanceScalar(MovementStance stance) => stance switch
    {
        MovementStance.Swimming => SwimSpeedModifier,
        MovementStance.Wading => WaterSpeedModifier,
        MovementStance.Crouching => CrouchSpeedModifier,
        MovementStance.Prone => ProneSpeedModifier,
        MovementStance.Walking => WalkSpeedModifier,
        MovementStance.Sprinting => SprintSpeedModifier,
        _ => 1f,
    };

    /// <summary>
    /// The axis scalar of <c>FUN_1411acb10</c> / <c>FUN_1411ad740</c>.
    /// <para>
    /// <b>The "one honest caveat" is settled, and this implementation was right.</b> docs/40 §2
    /// transcribed the client's term as <c>(movingBackwards ? backpedal : strafe)</c>, which read
    /// literally would apply <see cref="StrafeSpeedModifier"/> to pure forward motion and make plain
    /// running slower than <see cref="MaxMovementSpeed"/>. <c>FUN_141593e80</c> branches
    /// <c>if (fwd &gt; 0 &amp;&amp; lat != 0) goto &lt;skip the axis scalar&gt;</c>, and the client
    /// reported a 5.5 m/s forward plateau — not 4.4 — back on the wire. Forward, <b>and
    /// diagonal-forward</b>, take no axis penalty. [P] (docs/49 §4.1)
    /// </para>
    /// <para>
    /// Backwards <i>and</i> sideways together takes <c>min(strafe, backpedal)</c>, which is
    /// <see cref="MovementAxis.BackwardStrafe"/>. [P]
    /// </para>
    /// </summary>
    public float AxisScalar(MovementAxis axis, bool sprinting) => axis switch
    {
        MovementAxis.Backward => BackpedalSpeedModifier,
        MovementAxis.BackwardStrafe => MathF.Min(StrafeSpeedModifier, BackpedalSpeedModifier),
        MovementAxis.Strafe => sprinting ? SprintStrafeMultiplier : StrafeSpeedModifier,
        _ => 1f,
    };

    /// <summary>
    /// The client's formula (docs/40 §2) as far as the server can model it:
    /// <c>MaxMovementSpeed × stanceScalar × axisScalar × staminaScalar</c>.
    /// </summary>
    /// <param name="stance">The (client-local) posture branch.</param>
    /// <param name="axis">Which axis scalar applies.</param>
    /// <param name="staminaPercent">0-100; 100 means no penalty.</param>
    public float SpeedFor(MovementStance stance, MovementAxis axis = MovementAxis.Forward, float staminaPercent = 100f) =>
        MaxMovementSpeed
        * StanceScalar(stance)
        * AxisScalar(axis, stance == MovementStance.Sprinting)
        * StaminaScalar(staminaPercent);

    /// <summary>
    /// The client's own tiered stamina penalty, applied last and on top of whichever stance branch
    /// ran (docs/40 §2.1, §4.3). Cranberry sends no stamina stats, so these are exactly the values
    /// the client will use. [P]
    /// </summary>
    public static float StaminaScalar(float staminaPercent)
    {
        if (staminaPercent < ClientStaminaFallback.CriticalPercent)
        {
            return ClientStaminaFallback.CriticalModifier;
        }

        if (staminaPercent < ClientStaminaFallback.LowPercent)
        {
            return ClientStaminaFallback.LowModifier;
        }

        if (staminaPercent < ClientStaminaFallback.NominalPercent)
        {
            return ClientStaminaFallback.NominalModifier;
        }

        return 1f;
    }

    /// <summary>
    /// The burst of docs/40 §8.1: every value in this profile as one <c>0f 40</c> stat list, all
    /// <c>valueType = 1</c> (float) with <c>modifier = 0</c>. Order is the readable one — base
    /// speed, the stance modifiers, the axis modifiers, then the eight blend times; the client's map
    /// is keyed, so order carries no meaning on the wire.
    /// </summary>
    public IReadOnlyList<CharacterStat> ToStats()
    {
        Validate();
        return
        [
            CharacterStat.Float(CharacterStatId.MaxMovementSpeed, MaxMovementSpeed),
            CharacterStat.Float(CharacterStatId.SprintSpeedModifier, SprintSpeedModifier),
            CharacterStat.Float(CharacterStatId.WalkSpeedModifier, WalkSpeedModifier),
            CharacterStat.Float(CharacterStatId.CrouchSpeedModifier, CrouchSpeedModifier),
            CharacterStat.Float(CharacterStatId.ProneSpeedModifier, ProneSpeedModifier),
            CharacterStat.Float(CharacterStatId.ProneRollSpeedModifier, ProneRollSpeedModifier),
            CharacterStat.Float(CharacterStatId.SwimSpeedModifier, SwimSpeedModifier),
            CharacterStat.Float(CharacterStatId.WaterSpeedModifier, WaterSpeedModifier),
            CharacterStat.Float(CharacterStatId.BackpedalSpeedModifier, BackpedalSpeedModifier),
            CharacterStat.Float(CharacterStatId.StrafeSpeedModifier, StrafeSpeedModifier),
            CharacterStat.Float(CharacterStatId.SprintAccelerationTime, SprintAccelerationTime),
            CharacterStat.Float(CharacterStatId.SprintDecelerationTime, SprintDecelerationTime),
            CharacterStat.Float(CharacterStatId.ForwardAccelerationTime, ForwardAccelerationTime),
            CharacterStat.Float(CharacterStatId.ForwardDecelerationTime, ForwardDecelerationTime),
            CharacterStat.Float(CharacterStatId.BackAccelerationTime, BackAccelerationTime),
            CharacterStat.Float(CharacterStatId.BackDecelerationTime, BackDecelerationTime),
            CharacterStat.Float(CharacterStatId.StrafeAccelerationTime, StrafeAccelerationTime),
            CharacterStat.Float(CharacterStatId.StrafeDecelerationTime, StrafeDecelerationTime),
        ];
    }

    /// <summary>
    /// Refuses a profile the client would render as broken movement: a non-finite or non-positive
    /// base speed, a non-finite or negative modifier, or a negative blend time. Zero blend times are
    /// legal — they are the client's own default and mean "snap instantly".
    /// </summary>
    public void Validate()
    {
        RequirePositive(MaxMovementSpeed, nameof(MaxMovementSpeed));
        RequireModifier(SprintSpeedModifier, nameof(SprintSpeedModifier));
        RequireModifier(WalkSpeedModifier, nameof(WalkSpeedModifier));
        RequireModifier(CrouchSpeedModifier, nameof(CrouchSpeedModifier));
        RequireModifier(ProneSpeedModifier, nameof(ProneSpeedModifier));
        RequireModifier(ProneRollSpeedModifier, nameof(ProneRollSpeedModifier));
        RequireModifier(SwimSpeedModifier, nameof(SwimSpeedModifier));
        RequireModifier(WaterSpeedModifier, nameof(WaterSpeedModifier));
        RequireModifier(BackpedalSpeedModifier, nameof(BackpedalSpeedModifier));
        RequireModifier(StrafeSpeedModifier, nameof(StrafeSpeedModifier));
        RequireTime(SprintAccelerationTime, nameof(SprintAccelerationTime));
        RequireTime(SprintDecelerationTime, nameof(SprintDecelerationTime));
        RequireTime(ForwardAccelerationTime, nameof(ForwardAccelerationTime));
        RequireTime(ForwardDecelerationTime, nameof(ForwardDecelerationTime));
        RequireTime(BackAccelerationTime, nameof(BackAccelerationTime));
        RequireTime(BackDecelerationTime, nameof(BackDecelerationTime));
        RequireTime(StrafeAccelerationTime, nameof(StrafeAccelerationTime));
        RequireTime(StrafeDecelerationTime, nameof(StrafeDecelerationTime));
    }

    private static void RequirePositive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            throw new InvalidOperationException($"{name} must be a finite positive speed; got {value}.");
        }
    }

    private static void RequireModifier(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0f)
        {
            throw new InvalidOperationException($"{name} must be a finite non-negative multiplier; got {value}.");
        }
    }

    private static void RequireTime(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0f)
        {
            throw new InvalidOperationException(
                $"{name} is a blend time in seconds and must be finite and non-negative; got {value}.");
        }
    }

    private static float LookupClientValue(string name)
    {
        foreach (StringHashValue entry in StringHashValues.Entries)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal)
                && float.TryParse(entry.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                return value;
            }
        }

        throw new InvalidOperationException($"'{name}' is missing from the client's StringHashToValue table.");
    }
}
