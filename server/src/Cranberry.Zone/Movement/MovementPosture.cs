namespace Cranberry.Zone.Movement;

/// <summary>
/// The bits of the <c>Posture</c> word the client sends back on channel 2
/// (<c>MovementFieldMask.Posture = 0x0001</c>, decoded by <c>ClientMovementUpdate.Parse</c>).
/// <para>
/// <b>This closes docs/17 §2.1's [BLOCKED].</b> That pass read four bits out of the binary and
/// recorded the rest as unrecoverable, noting specifically that no bit had been tied to
/// crouch/prone/sprint/walk/run because <c>MotionRecord</c> is a pure replication buffer that never
/// branches on them. Correlating each bit against the <i>client's own reported horizontal speed</i>
/// sidesteps that: over the 745 speed samples decoded out of
/// <c>captures\wire-20260829-220829.txt</c>, each named bit below lands on a speed plateau that is
/// exactly <c>MaxMovementSpeed × the modifier Cranberry sent for it</c> (docs/49 §2.2, §3). [P]
/// </para>
/// <para>
/// <b>Consequence.</b> docs/40 §5 and docs/21 §4 are right that the server can never <i>set</i> a
/// stance. They are wrong as stated that it cannot <i>know</i> one: crouch, sprint, backwards and
/// grounded arrive on every posture change. <see cref="PlayerMovementTracker.ReportedStance"/> is a
/// read of this word, not the nearest-speed guess that
/// <see cref="PlayerMovementTracker.EstimatedStance"/> still performs as a fallback.
/// </para>
/// </summary>
public static class MovementPostureBits
{
    /// <summary>Bit 0 — set on 745/745 posture records; record-present / alive. [P for the observation]</summary>
    public const int Present = 0;

    /// <summary>
    /// Bit 1 — <b>crouching</b>. Its plateau is 3.0 m/s = <c>5.5 × 0.55</c>, and with
    /// <see cref="MovingBackwards"/> 2.0 m/s = <c>5.5 × 0.55 × 0.65</c>. [P]
    /// <para>
    /// The arithmetic here is against the <b>wave-3</b> stat burst (<c>MovementTuning.Wave3Legacy</c>
    /// — crouch 0.55, backpedal 0.65, sprint 1.45), which is what the 745 samples of
    /// <c>captures\wire-20260829-220829.txt</c> were measured against. Do not read it against the
    /// live <see cref="MovementProfile"/>: docs/49 changed backpedal to 0.50 and sprint to 1.20, so
    /// the symbol names would contradict the numbers. The bit IDENTIFICATION is what is proven; the
    /// modifiers are the wave-3 ones that made it readable.
    /// </para>
    /// </summary>
    public const int Crouching = 1;

    /// <summary>
    /// Bit 2 — <b>sprinting</b>. Plateau 8.0 m/s = <c>5.5 × 1.45</c>, n = 57 — the wave-3 sprint
    /// modifier, not the live one (see <see cref="Crouching"/>). [P]
    /// </summary>
    public const int Sprinting = 2;

    /// <summary>Bit 3 — unidentified; 4 samples, all airborne, all 5.8 m/s. [BLOCKED] (docs/49 §8 q5)</summary>
    public const int Unidentified3 = 3;

    /// <summary>
    /// Bit 4 — <b>no speed effect</b>: <c>0x0411</c> peaks at the same 5.5 as <c>0x0401</c> and
    /// <c>0x0415</c> at the same 8.0 as <c>0x0405</c>, while 97 % of its samples are moving.
    /// ADS / weapon-ready is the candidate. <b>[lead]</b> — do not branch on it.
    /// </summary>
    public const int WeaponReadyCandidate = 4;

    /// <summary>
    /// Bit 5 — <b>airborne</b>. Strictly complementary to <see cref="OnGround"/> in 745/745 samples,
    /// and every free-fall sample (15.7–18.9 m/s) carries it. [P]
    /// </summary>
    public const int Airborne = 5;

    /// <summary>
    /// Bit 6 — the <b>stop flag</b>. Set on <b>209/209</b> zero-speed samples and <b>0/536</b>
    /// moving ones: independent wire confirmation of docs/17 §2.1's <c>FUN_1423391e0</c>
    /// "STOP FLAG". [P]
    /// </summary>
    public const int Stopped = 6;

    /// <summary>Bit 10 — <b>on ground</b>; see <see cref="Airborne"/>. [P]</summary>
    public const int OnGround = 10;

    /// <summary>
    /// Bit 15 — <b>moving backwards</b>. Plateau 3.6 m/s = <c>5.5 × 0.65</c> — again the wave-3
    /// backpedal modifier, not the live one (see <see cref="Crouching"/>); with
    /// <see cref="Crouching"/>, 2.0 m/s. [P]
    /// <para>
    /// There is <b>no lateral bit</b>: strafing reports the same posture as running forward
    /// (<c>0x0401</c>) and only the reported speed separates them, so a strafe can never be read off
    /// this word.
    /// </para>
    /// </summary>
    public const int MovingBackwards = 15;

    /// <summary>
    /// Bit 16 — set on 98.7 % of moving samples and 44 % of stopped ones; "has movement input",
    /// leading the velocity. <b>[lead]</b>
    /// </summary>
    public const int HasMovementInput = 16;

    /// <summary>Turns a bit index into its mask.</summary>
    public static uint Mask(int bit) => 1u << bit;
}

/// <summary>
/// One client-reported posture word, read through <see cref="MovementPostureBits"/>.
/// <para>
/// Only what §3 of docs/49 proved is exposed as a named property. Bits 3, 17, 18 and 22 vary with no
/// speed correlation and are deliberately unreadable here; bit 4 is a <b>[lead]</b> and is exposed
/// only under a name that says so.
/// </para>
/// </summary>
/// <param name="Value">The raw 32-bit word off the wire.</param>
public readonly record struct MovementPosture(uint Value)
{
    private bool Bit(int index) => (Value & MovementPostureBits.Mask(index)) != 0;

    /// <summary>Bit 0 — the record-present bit that every observed sample carries.</summary>
    public bool IsPresent => Bit(MovementPostureBits.Present);

    /// <summary>Bit 1. [P]</summary>
    public bool IsCrouching => Bit(MovementPostureBits.Crouching);

    /// <summary>Bit 2. [P]</summary>
    public bool IsSprinting => Bit(MovementPostureBits.Sprinting);

    /// <summary>Bit 5. [P]</summary>
    public bool IsAirborne => Bit(MovementPostureBits.Airborne);

    /// <summary>Bit 6 — the client says it is stopped (209/209 zero-speed samples). [P]</summary>
    public bool IsStopped => Bit(MovementPostureBits.Stopped);

    /// <summary>Bit 10. [P]</summary>
    public bool IsOnGround => Bit(MovementPostureBits.OnGround);

    /// <summary>Bit 15. [P]</summary>
    public bool IsMovingBackwards => Bit(MovementPostureBits.MovingBackwards);

    /// <summary>Bit 16 — <b>[lead]</b>, "has movement input".</summary>
    public bool HasMovementInput => Bit(MovementPostureBits.HasMovementInput);

    /// <summary>Bit 4 — <b>[lead]</b>, candidate ADS / weapon-ready. Never branch on it.</summary>
    public bool IsWeaponReadyCandidate => Bit(MovementPostureBits.WeaponReadyCandidate);

    /// <summary>
    /// The stance this word states. Crouch wins over sprint because the client's own scalar chain
    /// (<c>FUN_1411acfa0</c>) is an exclusive <c>else if</c> that tests crouch first and so can never
    /// apply the sprint blend to a crouching character (docs/40 §2.1).
    /// <para>
    /// <see cref="MovementStance.Walking"/> is <b>not</b> readable — the walk toggle has no posture
    /// bit — so a walking player reads as <see cref="MovementStance.Standing"/>. Neither is
    /// swim/wade. Those are the honest limits of this word.
    /// </para>
    /// </summary>
    public MovementStance Stance =>
        IsCrouching ? MovementStance.Crouching
        : IsSprinting ? MovementStance.Sprinting
        : MovementStance.Standing;

    /// <summary>
    /// The axis this word states. Only backwards is distinguishable: there is no lateral bit, so a
    /// strafe reports <see cref="MovementAxis.Forward"/> and
    /// <see cref="MovementAxis.BackwardStrafe"/> can never be read off the wire.
    /// </summary>
    public MovementAxis Axis => IsMovingBackwards ? MovementAxis.Backward : MovementAxis.Forward;

    /// <summary>The mode this posture names, for a log line: e.g. "Sprinting", "Crouching backwards".</summary>
    public string Describe() => Axis == MovementAxis.Backward
        ? $"{Stance} backwards"
        : Stance.ToString();

    /// <summary>The raw word as the capture decoders print it.</summary>
    public override string ToString() => $"0x{Value:x8}";
}
