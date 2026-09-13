using Cranberry.Zone.Movement;

namespace Cranberry.Tests.Zone.Movement;

/// <summary>
/// The wave-8 port of the owner's own Z1 movement (docs/76), pinned in one place and by name.
/// <para>
/// <b>Why a whole file for fifteen numbers.</b> Every other movement test asserts an <i>arithmetic
/// consequence</i> — a plateau, a ladder, a wire byte — so an accidental edit to a modifier surfaces
/// as "expected 2.87, got 3.03" somewhere three files away, with nothing naming what was actually
/// broken. These assertions name the source row instead: each one says which line of the owner's
/// <c>ZoneSendSelf.BuildStats3()</c> it reproduces, so the failure message points at the decision
/// rather than at the symptom.
/// </para>
/// <para>
/// <b>Grade: BUILT + TESTED, never LIVE-VERIFIED (project law D29).</b> Nothing here is evidence
/// that the client moves at these speeds. It is evidence that the server sends them. The one thing
/// that closes that gap is the owner's own capture or his eyes — docs/76 §7.8 is the acceptance
/// check, and its headline predictions are a 4.1 m/s jog plateau and a 5.7 m/s sprint plateau.
/// </para>
/// </summary>
public sealed class Wave8Z1MovementPortTests
{
    [Fact]
    public void DefaultWireStatsHave350MillisecondStartupAndImmediateStops()
    {
        uint[] blendIds = [21, 22, 24, 25, 26, 27, 28, 29];
        var stats = MovementTuning.FromEnvironment(_ => null, out _).ToStats()
            .Where(stat => blendIds.Contains(stat.StatId)).ToArray();
        Assert.Equal(8, stats.Length);
        Assert.All(stats, stat => Assert.Equal(
            stat.StatId is 21 or 24 or 25 or 26 ? 0.35f : 0f, stat.EffectiveValue));
    }

    /// <summary>
    /// The fifteen initialisers docs/76 §7.1 moved, asserted against the owner's own shipping stat
    /// array row by row. If one of these fails, someone re-tuned a value that is his, not ours.
    /// </summary>
    [Fact]
    public void Aug2017DefaultMatchesTheZ1Port()
    {
        MovementProfile p = MovementProfile.Default;

        // The base is the only row Cranberry does NOT copy from his stat array: he sends id 2 as the
        // integer 1 because at 1087 the base is client-intrinsic, so there is nothing to copy. 4.10
        // is his measured jog, ZoneMovement.RetailJogSpeed (docs/76 §2.2, §2.3).
        Assert.Equal(4.10f, p.MaxMovementSpeed, 5);

        // The stance and axis modifiers — BuildStats3() rows 5, 86, 4, 3, 67, 85.
        Assert.Equal(1.40f, p.SprintSpeedModifier, 5);
        Assert.Equal(0.30f, p.WalkSpeedModifier, 5);
        Assert.Equal(0.70f, p.CrouchSpeedModifier, 5);
        Assert.Equal(0.75f, p.BackpedalSpeedModifier, 5);
        Assert.Equal(0.40f, p.ProneSpeedModifier, 5);
        Assert.Equal(0.80f, p.WaterSpeedModifier, 5);

        // The eight blend times — rows 21, 22, 24, 25, 26, 27, 28, 29. This is the half of his
        // movement docs/67 §5 missed entirely. The owner now requests 350 ms startup
        // while all four decelerations remain zero for immediate stops.
        Assert.Equal(0.35f, p.SprintAccelerationTime, 5);
        Assert.Equal(0f, p.SprintDecelerationTime, 5);
        Assert.Equal(0.35f, p.ForwardAccelerationTime, 5);
        Assert.Equal(0.35f, p.BackAccelerationTime, 5);
        Assert.Equal(0.35f, p.StrafeAccelerationTime, 5);
        Assert.Equal(0f, p.ForwardDecelerationTime, 5);
        Assert.Equal(0f, p.BackDecelerationTime, 5);
        Assert.Equal(0f, p.StrafeDecelerationTime, 5);

        // Zero blend times are legal on the wire.
        p.Validate();
        Assert.Equal(MovementProfile.StatCount, p.ToStats().Count);
    }

    /// <summary>
    /// The two rows Cranberry deliberately <b>refuses</b> from Z1, and the one it already agreed on.
    /// docs/76 §2.7 and §2.4 — the value crosses only where the behaviour is the owner's intent.
    /// </summary>
    [Fact]
    public void TheRefusedRowsStayCranberrysAndStrafeWasAlreadyRight()
    {
        MovementProfile p = MovementProfile.Default;

        // Row 6: he ships SwimSpeedModifier as the integer 0. Harmless on 1087, where the swim
        // branch is unused; at 1148 swim is a live stance branch and 0 is a swimmer who cannot move.
        Assert.NotEqual(0f, p.SwimSpeedModifier);
        Assert.Equal(0.55f, p.SwimSpeedModifier, 5);
        Assert.True(p.SpeedFor(MovementStance.Swimming) > 1f, "A swimmer must be able to swim.");

        // The 2026-09-06 Shift-roll request supersedes the old unobserved 0.50 default.
        // Native roll multiplies the prone scalar on its lateral axis.
        Assert.Equal(p.RunSpeed, p.SpeedFor(MovementStance.Prone) * p.ProneRollSpeedModifier, 5);
        Assert.True(p.ProneRollSpeedModifier > p.StrafeSpeedModifier);
        Assert.Equal(2.50f, p.ToStats().Single(row => row.StatId == CharacterStatId.ProneRollSpeedModifier).EffectiveValue);

        // Row 7: strafe. Triple-anchored — his stat array, the August client's own
        // Movement.SprintStrafeMultiplier, and MoveInfo.txt row 4001's MAX_STRAFE 75 — so Cranberry
        // and Z1 already agreed here and nothing moved.
        Assert.Equal(0.75f, p.StrafeSpeedModifier, 5);
        Assert.Equal(MovementProfile.SprintStrafeMultiplier, p.StrafeSpeedModifier, 5);
    }

    /// <summary>
    /// <b>The number every other lane needs: sprint is 5.74 m/s.</b> Anything sized against "what a
    /// player can outrun" — the gas wall above all — must use this, and it fell 13 % in wave 8.
    /// </summary>
    [Fact]
    public void SprintIsFivePointSevenFourAndIsTheCeilingOnEveryLegalInput()
    {
        MovementProfile p = MovementProfile.Default;
        Assert.Equal(5.74f, p.SprintSpeed, 0.001f);
        Assert.Equal(5.74f, p.MaxLegalSpeed, 0.001f);

        // No stance/axis combination the client can reach beats it, so it really is the ceiling.
        foreach (MovementStance stance in Enum.GetValues<MovementStance>())
        {
            foreach (MovementAxis axis in Enum.GetValues<MovementAxis>())
            {
                Assert.True(
                    p.SpeedFor(stance, axis) <= p.MaxLegalSpeed + 0.001f,
                    $"{stance}/{axis} is faster than the sprint ceiling.");
            }
        }

        // It is a real drop, not a rounding change: wave 5 sprinted at 6.60 m/s.
        Assert.True(p.SprintSpeed < MovementTuning.Wave5Legacy.SprintSpeed - 0.8f);
    }

    /// <summary>
    /// docs/76 §7.8's acceptance table, computed rather than transcribed: what the owner's next
    /// capture should report for each posture word, to the 0.1 m/s the wire carries.
    /// </summary>
    [Theory]
    [InlineData(0x0401u, MovementAxis.Forward, 4.1f)]    // forward run
    [InlineData(0x0401u, MovementAxis.Strafe, 3.1f)]     // pure strafe (same posture word, §3)
    [InlineData(0x0405u, MovementAxis.Forward, 5.7f)]    // sprint
    [InlineData(0x8401u, MovementAxis.Backward, 3.1f)]   // backpedal
    [InlineData(0x0403u, MovementAxis.Forward, 2.9f)]    // crouch forward
    [InlineData(0x0403u, MovementAxis.Strafe, 2.2f)]     // crouch strafe
    [InlineData(0x8403u, MovementAxis.Backward, 2.2f)]   // crouch backpedal
    public void TheAcceptanceTableIsWhatTheProfileActuallyPredicts(uint postureWord, MovementAxis axis, float expected)
    {
        var posture = new MovementPosture(postureWord);
        float predicted = MovementProfile.Default.SpeedFor(posture.Stance, axis);

        // The wire reports one decimal, so the check is at that resolution.
        Assert.Equal(expected, MathF.Round(predicted, 1), 3);
    }

    /// <summary>
    /// Walk really is a stroll now (docs/76 §2.1: row 86 went 0.45 → 0.30), and the modes a player
    /// can toggle between are separated by enough to be felt rather than measured.
    /// </summary>
    [Fact]
    public void TheOwnersWalkIsHalfWhatCranberryUsedToSend()
    {
        MovementProfile p = MovementProfile.Default;
        Assert.Equal(1.23f, p.WalkSpeed, 0.001f);
        Assert.True(p.WalkSpeed < MovementTuning.Wave5Legacy.WalkSpeed / 2f);

        // Walk, crouch and run are the three the owner toggles between on foot.
        Assert.True(p.CrouchSpeed - p.WalkSpeed > 1.5f);
        Assert.True(p.RunSpeed - p.CrouchSpeed > 1.2f);
    }

    /// <summary>
    /// The backpedal conflict of docs/76 §2.6, pinned so nobody has to rediscover it: the owner
    /// ships 0.75, but in wave 4 he called a measured 3.58 m/s backpedal too fast. 0.75 is taken
    /// because the base drop more than pays for the ratio rise — and the escape hatch is asserted
    /// too, because that is what a play-test will reach for if he says it is still wrong.
    /// </summary>
    [Fact]
    public void TheBackpedalIsSlowerThanTheOneHeComplainedAboutAndHasAKnob()
    {
        MovementProfile p = MovementProfile.Default;
        Assert.Equal(0.75f, p.BackpedalSpeedModifier, 5);
        Assert.Equal(3.075f, p.BackpedalSpeed, 0.001f);
        Assert.True(p.BackpedalSpeed < 3.58f, "The wave-4 complaint was a measured 3.58 m/s backpedal.");

        // CRANBERRY_MOVE_BACK=0.67 restores the 2.75 m/s absolute he accepted, at the new base.
        MovementProfile restored = MovementTuning.FromEnvironment(
            v => v == "CRANBERRY_MOVE_BACK" ? "0.67" : null,
            out string? note);
        Assert.Equal(2.747f, restored.BackpedalSpeed, 0.01f);
        Assert.NotNull(note);
        Assert.DoesNotContain("IGNORED", note);
    }

    /// <summary>
    /// The three ramp knobs wave 8 added (docs/76 §7.3 item 6). The port tripled three acceleration
    /// times; "too sluggish" has to be answerable with a restart, not a rebuild.
    /// </summary>
    [Theory]
    [InlineData("CRANBERRY_MOVE_FWD_ACCEL")]
    [InlineData("CRANBERRY_MOVE_BACK_ACCEL")]
    [InlineData("CRANBERRY_MOVE_STRAFE_ACCEL")]
    public void EachNewRampKnobRetunesItsOwnBlendTimeAndAcceptsZero(string variable)
    {
        MovementTuning.MovementKnob knob = MovementTuning.Knobs.Single(k => k.Variable == variable);
        Assert.Equal(0f, knob.Minimum);

        MovementProfile tuned = MovementTuning.FromEnvironment(
            v => v == variable ? "0.25" : null,
            out string? note);
        Assert.Equal(0.25f, knob.Read(tuned), 5);
        Assert.NotNull(note);
        Assert.DoesNotContain("IGNORED", note);

        // Zero is the client's own default and means "snap": legal, and what the owner ships for
        // three of the four decelerations.
        MovementProfile snapped = MovementTuning.FromEnvironment(v => v == variable ? "0" : null, out _);
        Assert.Equal(0f, knob.Read(snapped), 5);
        snapped.Validate();

        // Nothing else moved.
        foreach (MovementTuning.MovementKnob other in MovementTuning.Knobs.Where(k => k.Variable != variable))
        {
            Assert.Equal(other.Read(MovementProfile.Default), other.Read(tuned), 5);
        }
    }

    /// <summary>
    /// <b>NO STAMINA (the owner's round-35 ruling) survives the port untouched.</b> His server
    /// satisfies it with <c>ZoneStamina.NoStamina2017 = true</c> and a resource pinned at 10000;
    /// Cranberry satisfies it by pinning resource 6 at 100 % and sending none of stats 52-57, so the
    /// client's own tiers never fire. Nothing in wave 8 sends a stamina stat, and this is the guard.
    /// </summary>
    [Fact]
    public void NoStaminaStatIsSentAndTheTiersNeverBiteAtFullStamina()
    {
        Assert.Equal(1f, MovementProfile.StaminaScalar(100f), 5);
        Assert.Equal(
            MovementProfile.Default.RunSpeed,
            MovementProfile.Default.SpeedFor(MovementStance.Standing, MovementAxis.Forward, 100f),
            4);

        // The six stamina stats (52-57) are not in the burst — the client falls back to its own
        // .rdata tiers, which is docs/76 §4.2's deliberate choice.
        uint[] staminaStatIds = [52u, 53u, 54u, 55u, 56u, 57u];
        Assert.All(
            MovementProfile.Default.ToStats(),
            s => Assert.DoesNotContain(s.StatId, staminaStatIds));
        Assert.Equal(18, MovementProfile.StatCount);
    }

    /// <summary>
    /// The parachute consequence of the lower base (docs/76 §7.4): <c>SpeedLimit</c> fell from 8.25
    /// to 7.18 m/s, which is under a chute's 18 m/s descent, so an airborne sample must no longer
    /// count as a speed violation — otherwise the counter measures the drop and nothing else.
    /// </summary>
    [Fact]
    public void ADescendingPlayerNoLongerCountsAsASpeedViolation()
    {
        var tracker = new PlayerMovementTracker();
        Assert.Equal(7.175f, tracker.SpeedLimit, 0.001f);

        // Airborne, at parachute speed: refused as a sample, but not counted.
        tracker.SetReportedPosture(0x0021u);
        Assert.False(tracker.Observe(18f));
        Assert.Equal(0, tracker.SpeedViolations);
        Assert.Equal(1, tracker.Samples);

        // On the ground at the same speed it is a violation, because on foot nothing explains it.
        tracker.SetReportedPosture(0x0401u);
        Assert.False(tracker.Observe(18f));
        Assert.Equal(1, tracker.SpeedViolations);

        // And with no posture at all it still counts — an unreporting client gets no free pass.
        var blind = new PlayerMovementTracker();
        Assert.False(blind.Observe(18f));
        Assert.Equal(1, blind.SpeedViolations);
    }

    /// <summary>
    /// The historical presets are stated absolutely and no longer inherit from the live default —
    /// docs/76 §7.3 calls this the single largest correctness risk in the port, because a preset
    /// defined as <c>Default with { … }</c> silently changes meaning every time the default moves.
    /// </summary>
    [Fact]
    public void TheHistoricalPresetsDidNotMoveWhenTheDefaultDid()
    {
        Assert.Equal(5.50f, MovementTuning.Wave3Legacy.MaxMovementSpeed, 5);
        Assert.Equal(5.50f, MovementTuning.Wave5Legacy.MaxMovementSpeed, 5);
        Assert.Equal(7.975f, MovementTuning.Wave3Legacy.SprintSpeed, 0.001f);
        Assert.Equal(6.600f, MovementTuning.Wave5Legacy.SprintSpeed, 0.001f);

        // Not one blend time of either is the owner's.
        Assert.Equal(0.35f, MovementTuning.Wave3Legacy.SprintAccelerationTime);
        Assert.NotEqual(MovementProfile.Default.StrafeDecelerationTime, MovementTuning.Wave5Legacy.StrafeDecelerationTime);

        // Aug2017Default is still the SAME INSTANCE as MovementProfile.Default: ZoneService's stat
        // burst short-circuits on ReferenceEquals, and a distinct-but-equal default re-arms the
        // burst on every resync (docs/49 §I3).
        Assert.Same(MovementProfile.Default, MovementTuning.Aug2017Default);
    }
}
