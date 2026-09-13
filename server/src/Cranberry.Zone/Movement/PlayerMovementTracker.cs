namespace Cranberry.Zone.Movement;

/// <summary>
/// The bits of the 64-bit CharacterState word at <c>character+0x18d8</c> — written by
/// <c>UpdateCharacterState</c> (<c>0f 0a</c>) and <c>UpdateCharacterStateDelta</c> (<c>0f 3f</c>)
/// through <c>FUN_140c81fe0</c> — that this lane proved (docs/40 §8.2).
/// <para>
/// This is the <b>only</b> server-side stance lever in 1148. The server can never <i>set</i> a
/// stance: every writer of the local posture block at <c>entity+0x37e0</c> is client-local
/// (docs/21 §4, re-confirmed in docs/40 §5). It can only forbid sprinting.
/// </para>
/// </summary>
public static class CharacterStateMovementBits
{
    /// <summary>
    /// Bit 37 (<c>0x25</c>): when set, <b>sprinting is hard-disabled</b> on that character.
    /// <c>FUN_14158ef20</c> line 509 gates the whole sprint-request path on
    /// <c>((state &gt;&gt; 0x25 &amp; 1) == 0) &amp;&amp; (*(int*)(char + 0x8f0) == 0)</c>; when the
    /// gate fails the sprint flag is forced to 0 and <c>FUN_140c76340(char, 0)</c>
    /// (<c>SetSprinting(false)</c>) runs. [P]
    /// <para>
    /// The bit is <b>outside</b> the applier's immediate mask <c>0x0201010000000000</c>, so it flows
    /// through the timed queue and honours the packet's <c>time</c> field.
    /// </para>
    /// </summary>
    public const int SprintDisabledBit = 37;

    /// <summary><see cref="SprintDisabledBit"/> as a mask.</summary>
    public const ulong SprintDisabledMask = 1UL << SprintDisabledBit;

    /// <summary>
    /// Bit 24 (<c>0x18</c>): appears twice in <c>FUN_14158ef20</c> as
    /// <c>sprint = requested &amp;&amp; (bit24 || forwardInput &gt; 0)</c>, i.e. it permits sprinting
    /// without forward input (auto-run / sprint in any direction). <b>[lead]</b> — not confirmed,
    /// and deliberately not used by anything here.
    /// </summary>
    public const int SprintWithoutForwardInputBit = 24;

    /// <summary>True when <paramref name="state"/> forbids sprinting.</summary>
    public static bool IsSprintDisabled(ulong state) => (state & SprintDisabledMask) != 0;

    /// <summary>Returns <paramref name="state"/> with the sprint-disable bit set or cleared.</summary>
    public static ulong WithSprintDisabled(ulong state, bool disabled) =>
        disabled ? state | SprintDisabledMask : state & ~SprintDisabledMask;
}

/// <summary>
/// The server's own view of one player's movement: which profile they were sent, whether that burst
/// has actually gone out, the last speed the server observed, and a best-effort stance estimate that
/// other systems (audio, AI, HUD, anti-cheat counters) can query.
/// <para>
/// <b>The stance is readable, and that is new in wave 4.</b> docs/40 §5 and docs/21 §4 are right
/// that the server can never <i>set</i> a stance. They were wrong that it cannot <i>know</i> one:
/// the client's channel-2 record carries a <c>Posture</c> bitfield whose crouch, sprint,
/// backwards and grounded bits are proven against the client's own reported speed (docs/49 §3,
/// <see cref="MovementPosture"/>). Feed it through <see cref="SetReportedPosture(uint)"/> and
/// <see cref="ReportedStance"/> is a <b>read</b>. <see cref="EstimatedStance"/>'s nearest-speed
/// guess survives only as the fallback for before the first posture record arrives — crouch
/// (3.03 m/s) and backpedalling (2.75 m/s) are close enough in the default profile that the guess
/// must never be used as evidence of what a player did.
/// </para>
/// <para>
/// <b>And the speeds are checkable.</b> <see cref="Observe(float)"/> compares each settled plateau
/// the client reports against what the profile predicts for the stance the client also reported,
/// and queues one line per stance for the host log (<see cref="TakeSpeedCheckLine"/>). That is the
/// whole of the wave-4 acceptance test for this lane, and it replaces "ask the owner how it feels"
/// with a grep (docs/49 §I4).
/// </para>
/// <para>
/// The one bound that is sound with or without a posture is <see cref="SpeedLimit"/>, because
/// <see cref="MovementProfile.MaxLegalSpeed"/> is the fastest any legal input can produce
/// regardless of stance.
/// </para>
/// </summary>
public sealed class PlayerMovementTracker
{
    /// <summary>
    /// Slack allowed over <see cref="MovementProfile.MaxLegalSpeed"/> before a sample counts as a
    /// violation. Cranberry's own number, not a client fact: a sample is a distance divided by a
    /// client-supplied time, so honest play overshoots on jitter, on a jump, and on a slope.
    /// </summary>
    public const float SpeedTolerance = 1.25f;

    /// <summary>Below this, in m/s, a sample is treated as standing still rather than a stance.</summary>
    public const float IdleSpeed = 0.25f;

    /// <summary>
    /// How close two consecutive samples must be, in m/s, for the second to count as a <i>settled</i>
    /// plateau rather than a point on a ramp. The wire carries speed as a packed signed integer over
    /// 10, so 0.1 m/s is the resolution and anything tighter than that is noise (docs/49 §2.1).
    /// </summary>
    public const float PlateauTolerance = 0.15f;

    /// <summary>
    /// How far a settled plateau may sit from the profile's prediction and still be called a MATCH.
    /// One wire quantum plus rounding: the measured plateaus in docs/49 §2.2 all landed inside it
    /// (8.0 observed against 7.975 sent, 3.6 against 3.575, 2.0 against 1.966).
    /// </summary>
    public const float SpeedCheckTolerance = 0.15f;

    private readonly Dictionary<(MovementStance Stance, MovementAxis Axis), float> _settledPeaks = [];
    private readonly HashSet<(MovementStance Stance, MovementAxis Axis)> _checked = [];
    private readonly Queue<string> _pendingCheckLines = new();
    private MovementStance _estimatedStance = MovementStance.Standing;
    private float _previousSample = float.NaN;

    public PlayerMovementTracker(MovementProfile? profile = null)
    {
        Profile = profile ?? MovementProfile.Default;
        Profile.Validate();
    }

    /// <summary>The stat set this player has been (or is about to be) sent.</summary>
    public MovementProfile Profile { get; private set; }

    /// <summary>
    /// True once the <c>0f 40</c> burst has actually been written for this player. A <c>0x0f</c> sub
    /// against a guid the client does not know is <b>silently dropped</b> (docs/21 §1b), so the
    /// integrator sets this only after sending, and only after the character record.
    /// </summary>
    public bool StatsDelivered { get; private set; }

    /// <summary>Last horizontal speed the server observed, m/s.</summary>
    public float LastHorizontalSpeed { get; private set; }

    /// <summary>Fastest horizontal speed observed since the last <see cref="Reset"/>, m/s.</summary>
    public float PeakHorizontalSpeed { get; private set; }

    /// <summary>Samples that exceeded <see cref="SpeedLimit"/>.</summary>
    public long SpeedViolations { get; private set; }

    /// <summary>Samples observed since the last <see cref="Reset"/>.</summary>
    public long Samples { get; private set; }

    /// <summary>
    /// The player's stamina, 0-100. Cranberry sends no stamina stats, so the client applies its own
    /// tiered fallbacks (docs/40 §4.3) — this field only lets the server's estimate agree with them.
    /// </summary>
    public float StaminaPercent { get; private set; } = 100f;

    /// <summary>Whether CharacterState bit 37 is currently asserted for this player.</summary>
    public bool SprintDisabled { get; private set; }

    /// <summary>True while the last sample was above <see cref="IdleSpeed"/>.</summary>
    public bool IsMoving => LastHorizontalSpeed > IdleSpeed;

    /// <summary>
    /// The last <c>Posture</c> word the client sent on channel 2, or null before the first one
    /// arrives (and after a <see cref="Reset"/>). The client carries posture forward between
    /// records, which is what its own delta encoding intends, so this stays valid between updates.
    /// </summary>
    public MovementPosture? ReportedPosture { get; private set; }

    /// <summary>
    /// The stance the <b>client itself reported</b>, falling back to <see cref="EstimateStance"/>'s
    /// guess until the first posture record arrives. Prefer this to
    /// <see cref="EstimatedStance"/> everywhere.
    /// <para>
    /// Its limits are the posture word's limits: the walk toggle has no bit, so a walking player
    /// reads as <see cref="MovementStance.Standing"/>, and neither swim nor wade is readable
    /// (docs/49 §3).
    /// </para>
    /// </summary>
    public MovementStance ReportedStance => ReportedPosture?.Stance ?? _estimatedStance;

    /// <summary>
    /// The axis the client reported: backwards (posture bit 15) or forward. <b>There is no lateral
    /// bit</b> — a strafe reports the same posture as a forward run and only the speed separates
    /// them — so this never returns <see cref="MovementAxis.Strafe"/> (docs/49 §3).
    /// </summary>
    public MovementAxis ReportedAxis => ReportedPosture?.Axis ?? MovementAxis.Forward;

    /// <summary>
    /// The stance the last observed speed is closest to, <b>or the reported one when the client has
    /// sent a posture</b> — a read beats a guess (docs/49 §3). See the class remarks: without a
    /// posture this is an inference and never evidence. <see cref="MovementStance.Standing"/> is
    /// also what a stationary player reports.
    /// </summary>
    public MovementStance EstimatedStance => ReportedPosture?.Stance ?? _estimatedStance;

    /// <summary>
    /// The speed above which a sample is counted as a violation: the profile's fastest legal speed
    /// with <see cref="SpeedTolerance"/> applied. Sprint is the ceiling even when
    /// <see cref="SprintDisabled"/> is set, because the client applies that bit itself and a lagged
    /// sample can still straddle the change.
    /// <para>
    /// <b>This is an on-foot ceiling only</b>, and wave 8's port made that matter: dropping the base
    /// to 4.10 m/s took it from 8.25 to <b>7.18</b> m/s, which is below a parachute descent — the
    /// chute's own <c>MoveInfo.txt</c> row 4001 <c>MAX_FORWARD</c> is 18 m/s. See
    /// <see cref="Observe"/> for the exclusion that keeps <see cref="SpeedViolations"/> meaningful.
    /// </para>
    /// </summary>
    public float SpeedLimit => Profile.MaxLegalSpeed * SpeedTolerance;

    /// <summary>
    /// Swaps the profile in and clears <see cref="StatsDelivered"/>, so the integrator's "has this
    /// player got its stats?" check re-fires and the new numbers go out.
    /// </summary>
    public void SetProfile(MovementProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        Profile = profile;
        StatsDelivered = false;
    }

    /// <summary>Records that the <c>0f 40</c> burst has been written for this player.</summary>
    public void MarkStatsDelivered() => StatsDelivered = true;

    /// <summary>The burst to send: docs/40 §8.1 step 1, 18 entries against this player's character guid.</summary>
    public CharacterStatPackets.UpdateStat BuildStatBurst(ulong characterGuid) =>
        CharacterStatPackets.UpdateStat.ForProfile(characterGuid, Profile);

    /// <summary>The owning-client companion: docs/40 §8.1 step 2, the single <c>MaxMovementSpeed</c> entry.</summary>
    public CharacterStatPackets.ClientUpdateStat BuildBaseSpeedUpdate() =>
        CharacterStatPackets.ClientUpdateStat.BaseSpeed(Profile);

    /// <summary>
    /// Feeds the <c>Posture</c> word off a channel-2 <c>ClientMovementUpdate</c>. Half of docs/49
    /// §I4's two-line hook; <see cref="Observe(float)"/> is the other half, and the order matters —
    /// posture first, so the speed that follows is checked against the stance it was measured in.
    /// </summary>
    public void SetReportedPosture(uint posture)
    {
        var next = new MovementPosture(posture);
        // Verify wave 4: a mode change BREAKS the plateau. RecordSpeedCheck calls two consecutive
        // samples within PlateauTolerance "settled" for whatever mode the LATEST record names, so
        // without this the tail of the previous mode's ramp is attributed to the new one — release
        // sprint and the 6.60 -> 5.50 decay (SprintDecelerationTime 0.25 s, ~38 ms between channel-2
        // records in host-20260829-220829.log) lands two ~5.85 samples in (Standing, Forward), which
        // is +0.35 over the 5.50 prediction: a MISMATCH line, _checked.Add(mode), and the real
        // plateau — the one measurement this lane exists to take — never reported for the rest of
        // the match. Requiring two samples in the SAME mode costs one comparison per record.
        if (ReportedPosture is MovementPosture current
            && (current.Stance != next.Stance || current.Axis != next.Axis))
        {
            _previousSample = float.NaN;
        }

        ReportedPosture = next;
    }

    /// <summary>
    /// Pops the next queued speed-check line, or null when there is none. The caller logs it:
    /// <code>
    /// if (tracker.TakeSpeedCheckLine() is string line) _log.Info($"{connection} {line}");
    /// </code>
    /// The tracker writes no log of its own because this lane does not own the zone's logger.
    /// </summary>
    public string? TakeSpeedCheckLine() => _pendingCheckLines.Count > 0 ? _pendingCheckLines.Dequeue() : null;

    /// <summary>
    /// The fastest settled plateau seen per (stance, axis) since the last <see cref="Reset"/> — the
    /// evidence behind the check lines, exposed so a match summary can print what was never
    /// exercised. Silence for a mode means the player never reached its full speed in that mode, not
    /// that the stat failed.
    /// </summary>
    public IReadOnlyDictionary<(MovementStance Stance, MovementAxis Axis), float> SettledPeaks => _settledPeaks;

    /// <summary>Sets the stamina the server believes this player has, clamped to 0-100.</summary>
    public void SetStamina(float percent) =>
        StaminaPercent = float.IsFinite(percent) ? Math.Clamp(percent, 0f, 100f) : StaminaPercent;

    /// <summary>
    /// Asserts or clears CharacterState bit 37. Returns true when the value changed, which is the
    /// integrator's cue to send an <c>0f 0a</c>/<c>0f 3f</c> carrying
    /// <see cref="CharacterStateMovementBits.WithSprintDisabled"/>; the tracker itself writes no
    /// packet, because this lane does not own the character-state writer.
    /// </summary>
    public bool SetSprintDisabled(bool disabled)
    {
        if (SprintDisabled == disabled)
        {
            return false;
        }

        SprintDisabled = disabled;
        return true;
    }

    /// <summary>This player's sprint-disable bit folded into an existing CharacterState word.</summary>
    public ulong ApplySprintLock(ulong characterState) =>
        CharacterStateMovementBits.WithSprintDisabled(characterState, SprintDisabled);

    /// <summary>
    /// Feeds one observed horizontal speed in m/s. Returns false when it exceeded
    /// <see cref="SpeedLimit"/> (the sample is still recorded, and the counter incremented — what to
    /// do about it belongs to the movement system, not here). A non-finite sample is ignored
    /// entirely and returns false without counting a violation.
    /// </summary>
    public bool Observe(float horizontalSpeedMetresPerSecond)
    {
        if (!float.IsFinite(horizontalSpeedMetresPerSecond) || horizontalSpeedMetresPerSecond < 0f)
        {
            return false;
        }

        Samples++;
        float previous = _previousSample;
        _previousSample = horizontalSpeedMetresPerSecond;
        LastHorizontalSpeed = horizontalSpeedMetresPerSecond;
        PeakHorizontalSpeed = MathF.Max(PeakHorizontalSpeed, horizontalSpeedMetresPerSecond);
        _estimatedStance = EstimateStance(horizontalSpeedMetresPerSecond);
        RecordSpeedCheck(previous, horizontalSpeedMetresPerSecond);

        if (horizontalSpeedMetresPerSecond > SpeedLimit)
        {
            // Airborne samples are not on-foot samples, and SpeedLimit is an on-foot ceiling: a
            // parachute descends at up to 18 m/s (MoveInfo.txt row 4001 MAX_FORWARD) against a
            // 7.18 m/s limit, so before this gate every drop logged a few hundred "violations" and
            // the counter meant nothing (docs/76 §7.4). Only a posture that positively says
            // "airborne" is excused — an absent posture still counts, because a client that never
            // reports one must not get a free pass. The return value is unchanged: the sample is
            // still refused, and the caller still discards that.
            if (ReportedPosture is not { IsOnGround: false })
            {
                SpeedViolations++;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// docs/49 §I4: compare what the client says it is doing against what this profile predicts, and
    /// queue one line the first time each mode settles.
    /// <para>
    /// <b>Settled</b> means two consecutive samples within <see cref="PlateauTolerance"/> of each
    /// other, on the ground, moving, with a posture to name the mode — everything else is a ramp
    /// (§2.4), a free-fall (§2.3) or an unlabelled sample. Only the <b>peak</b> plateau per mode is
    /// judged, because every confound the server cannot see pushes the reported speed <i>down</i>
    /// and never up: a strafe reports the same posture as a forward run (§3), an analogue stick at
    /// partial deflection scales by its magnitude, a slope costs <c>cos θ</c>, and the client's own
    /// stamina tiers cost 25-90 % (§2.5). A plateau <b>above</b> the prediction has no such excuse
    /// and is reported as a MISMATCH immediately.
    /// </para>
    /// <para>
    /// <b>Two confounds do push it up, and neither is invisible</b> (verify wave 4). A posture
    /// transition carries the old mode's ramp into the new mode — handled in
    /// <see cref="SetReportedPosture(uint)"/>, which breaks the plateau on a mode change. Riding a
    /// vehicle reports on-ground postures at car speeds — handled by the caller, which must not feed
    /// this tracker while the player is seated (<c>ZoneService.HandlePlayerMovement</c>). Anything
    /// else that can raise a reported speed has to be excluded the same way, at the caller.
    /// </para>
    /// </summary>
    private void RecordSpeedCheck(float previous, float current)
    {
        if (ReportedPosture is not MovementPosture posture
            || !posture.IsOnGround
            || posture.IsStopped
            || current <= IdleSpeed
            || !float.IsFinite(previous)
            || MathF.Abs(current - previous) > PlateauTolerance)
        {
            return;
        }

        (MovementStance Stance, MovementAxis Axis) mode = (posture.Stance, posture.Axis);
        if (!_settledPeaks.TryGetValue(mode, out float peak) || current > peak)
        {
            peak = current;
            _settledPeaks[mode] = current;
        }

        if (_checked.Contains(mode))
        {
            return;
        }

        float predicted = Profile.SpeedFor(mode.Stance, mode.Axis, StaminaPercent);
        float delta = peak - predicted;
        if (MathF.Abs(delta) <= SpeedCheckTolerance)
        {
            _checked.Add(mode);
            _pendingCheckLines.Enqueue(
                $"movement: observed {peak:F1} m/s in stance {posture.Describe()} (posture {posture}) "
                + $"— profile predicts {predicted:F2} — MATCH");
            return;
        }

        if (delta > SpeedCheckTolerance)
        {
            _checked.Add(mode);
            _pendingCheckLines.Enqueue(
                $"movement: observed {peak:F1} m/s in stance {posture.Describe()} (posture {posture}) "
                + $"— profile predicts {predicted:F2} — MISMATCH, {delta:F2} m/s faster than any stat "
                + "this server sent (docs/49 §I4)");
        }
    }

    /// <summary>
    /// Nearest-match of a forward speed against the profile's four reachable stances, with the
    /// client's stamina penalty folded in so a winded player is not mistaken for a walker. Speeds
    /// below <see cref="IdleSpeed"/> report <see cref="MovementStance.Standing"/>.
    /// </summary>
    public MovementStance EstimateStance(float horizontalSpeedMetresPerSecond)
    {
        if (!float.IsFinite(horizontalSpeedMetresPerSecond) || horizontalSpeedMetresPerSecond <= IdleSpeed)
        {
            return MovementStance.Standing;
        }

        float stamina = MovementProfile.StaminaScalar(StaminaPercent);
        MovementStance best = MovementStance.Standing;
        float bestDistance = float.MaxValue;
        foreach (MovementStance stance in ReachableStances)
        {
            float expected = Profile.SpeedFor(stance, MovementAxis.Forward) * stamina;
            float distance = MathF.Abs(expected - horizontalSpeedMetresPerSecond);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = stance;
            }
        }

        return best;
    }

    /// <summary>
    /// Clears the observation window <b>and re-arms the stat burst</b>; the profile and the sprint
    /// lock survive.
    /// <para>
    /// <b>Why <see cref="StatsDelivered"/> is cleared here.</b> Every caller of this method is a
    /// world change — <c>EnterMatch</c>'s zoning reset and <c>AbandonMatch</c> — and
    /// <c>ClientBeginZoning</c> destroys and rebuilds the character entity, so the stat block at
    /// <c>entity+0x3dd0</c> starts empty again and every stance falls back to <c>GetStat</c>'s 1.0
    /// default. Keeping the flag set made the <c>0f 40</c> burst a once-per-<i>session</i> event:
    /// the first match got the six speeds and every match after it on the same gateway link ran at
    /// the client's built-ins. The flag records what this client has been told about the world it is
    /// in, so a new world clears it, exactly as the sibling state (<c>Inventory</c>, <c>Doors</c>,
    /// <c>Fleet</c>) is cleared beside the call.
    /// </para>
    /// </summary>
    public void Reset()
    {
        LastHorizontalSpeed = 0f;
        PeakHorizontalSpeed = 0f;
        SpeedViolations = 0;
        Samples = 0;
        _estimatedStance = MovementStance.Standing;
        _previousSample = float.NaN;
        ReportedPosture = null;
        _settledPeaks.Clear();
        _checked.Clear();
        _pendingCheckLines.Clear();
        StatsDelivered = false;
    }

    /// <summary>
    /// The stances a KOTK player can actually be in on land: prone is unreachable (docs/40 §4.4) and
    /// swim/wade need a water test the server does not have, so neither is a candidate for a
    /// speed-only inference.
    /// </summary>
    private static readonly MovementStance[] ReachableStances =
    [
        MovementStance.Walking,
        MovementStance.Crouching,
        MovementStance.Standing,
        MovementStance.Sprinting,
    ];
}
