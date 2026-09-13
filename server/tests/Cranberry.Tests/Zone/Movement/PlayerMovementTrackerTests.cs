using Cranberry.Zone.Movement;

namespace Cranberry.Tests.Zone.Movement;

// The server-side speed/stance view other systems query. Its limits are part of its contract: the
// stance is client-local and never on the wire (docs/40 §5, docs/21 §4), so the estimate is an
// inference and only the speed ceiling is sound.
public sealed class PlayerMovementTrackerTests
{
    [Fact]
    public void StatsAreNotDeliveredUntilTheBurstIsSent()
    {
        // A 0x0f sub against a guid the client does not know is silently dropped (docs/21 §1b), so
        // the integrator sends the burst after the character record and marks it only then.
        var tracker = new PlayerMovementTracker();
        Assert.False(tracker.StatsDelivered);

        tracker.MarkStatsDelivered();
        Assert.True(tracker.StatsDelivered);

        tracker.SetProfile(MovementProfile.Default with { MaxMovementSpeed = 6f });
        Assert.False(tracker.StatsDelivered);
        Assert.Equal(6f, tracker.Profile.MaxMovementSpeed, 4);
    }

    [Fact]
    public void TheBurstItBuildsIsTheOneItsProfileDescribes()
    {
        var tracker = new PlayerMovementTracker(MovementProfile.Default with { SprintSpeedModifier = 1.6f });
        CharacterStatPackets.UpdateStat burst = tracker.BuildStatBurst(0x99UL);

        Assert.Equal(0x99UL, burst.CharacterGuid);
        Assert.Equal(MovementProfile.StatCount, burst.Stats.Count);
        Assert.Equal(248, burst.Length);
        Assert.Equal(
            1.6f,
            burst.Stats.Single(s => s.StatId == CharacterStatId.SprintSpeedModifier).EffectiveValue,
            4);
        Assert.Equal(
            tracker.Profile.MaxMovementSpeed,
            tracker.BuildBaseSpeedUpdate().Stats.Single().EffectiveValue,
            4);
    }

    [Fact]
    public void SpeedAboveTheSprintCeilingCountsAsAViolation()
    {
        var tracker = new PlayerMovementTracker();
        Assert.True(tracker.Observe(tracker.Profile.SprintSpeed));
        Assert.Equal(0, tracker.SpeedViolations);

        Assert.False(tracker.Observe(tracker.SpeedLimit + 1f));
        Assert.Equal(1, tracker.SpeedViolations);
        Assert.Equal(2, tracker.Samples);
        Assert.Equal(tracker.SpeedLimit + 1f, tracker.PeakHorizontalSpeed, 3);
    }

    [Fact]
    public void ANonFiniteSampleIsIgnoredEntirely()
    {
        var tracker = new PlayerMovementTracker();
        Assert.False(tracker.Observe(float.NaN));
        Assert.False(tracker.Observe(float.PositiveInfinity));
        Assert.Equal(0, tracker.Samples);
        Assert.Equal(0, tracker.SpeedViolations);
    }

    [Theory]
    // The wave-8 ladder: walk 1.23, crouch 2.87, run 4.10, sprint 5.74 (docs/76 §2.5).
    [InlineData(0f, MovementStance.Standing)]
    [InlineData(1.2f, MovementStance.Walking)]
    [InlineData(2.9f, MovementStance.Crouching)]
    [InlineData(4.1f, MovementStance.Standing)]
    [InlineData(5.7f, MovementStance.Sprinting)]
    [InlineData(9.9f, MovementStance.Sprinting)]
    public void TheStanceEstimateSnapsToTheNearestModeSpeed(float speed, MovementStance expected)
    {
        var tracker = new PlayerMovementTracker();
        tracker.Observe(speed);
        Assert.Equal(expected, tracker.EstimatedStance);
    }

    [Fact]
    public void TheStanceEstimateFoldsInTheClientsStaminaPenalty()
    {
        // At 30 % stamina the client scales everything by 0.75, so a running player reports
        // 4.13 m/s — without the penalty that reads as "strafing or crouched".
        var tracker = new PlayerMovementTracker();
        tracker.SetStamina(30f);
        tracker.Observe(tracker.Profile.RunSpeed * 0.75f);
        Assert.Equal(MovementStance.Standing, tracker.EstimatedStance);
        Assert.Equal(30f, tracker.StaminaPercent, 3);

        tracker.SetStamina(500f);
        Assert.Equal(100f, tracker.StaminaPercent, 3);
    }

    [Fact]
    public void TheSprintLockIsCharacterStateBitThirtySeven()
    {
        // FUN_14158ef20 line 509: with this bit set the sprint request is forced off and
        // SetSprinting(false) runs (docs/40 §8.2). It is the only server-side stance lever.
        Assert.Equal(0x0000_0020_0000_0000UL, CharacterStateMovementBits.SprintDisabledMask);
        Assert.Equal(37, CharacterStateMovementBits.SprintDisabledBit);
        Assert.True(CharacterStateMovementBits.IsSprintDisabled(CharacterStateMovementBits.SprintDisabledMask));
        Assert.False(CharacterStateMovementBits.IsSprintDisabled(0));

        // Setting and clearing must leave every other bit of the state word alone.
        const ulong Other = 0x0201_0100_0000_00FFUL;
        Assert.Equal(
            Other | CharacterStateMovementBits.SprintDisabledMask,
            CharacterStateMovementBits.WithSprintDisabled(Other, disabled: true));
        Assert.Equal(
            Other,
            CharacterStateMovementBits.WithSprintDisabled(
                Other | CharacterStateMovementBits.SprintDisabledMask,
                disabled: false));
    }

    [Fact]
    public void SettingTheSprintLockReportsOnlyRealChanges()
    {
        var tracker = new PlayerMovementTracker();
        Assert.False(tracker.SprintDisabled);
        Assert.Equal(0UL, tracker.ApplySprintLock(0UL));

        Assert.True(tracker.SetSprintDisabled(true));
        Assert.False(tracker.SetSprintDisabled(true));
        Assert.True(tracker.SprintDisabled);
        Assert.Equal(CharacterStateMovementBits.SprintDisabledMask, tracker.ApplySprintLock(0UL));

        Assert.True(tracker.SetSprintDisabled(false));
        Assert.Equal(0UL, tracker.ApplySprintLock(0UL));
    }

    /// <summary>
    /// Reset is the world-change hook — <c>EnterMatch</c>'s zoning reset and <c>AbandonMatch</c> are
    /// its only callers — so it clears the observation window <b>and re-arms the stat burst</b>,
    /// while the profile and the sprint lock survive.
    /// <para>
    /// This assertion was inverted, and that inversion was the bug: <c>SendMovementStats</c> returns
    /// early on <see cref="PlayerMovementTracker.StatsDelivered"/>, and the only other writer that
    /// clears it (<c>SetProfile</c>) is skipped because <c>ZoneOptions.Movement</c> is the same
    /// <c>MovementProfile.Default</c> reference the tracker was built with. With the flag surviving,
    /// the <c>0f 40</c> burst was sent once per <i>session</i>: match one got the six speeds and
    /// every match after it on the same gateway link ran at the client's built-ins, because
    /// <c>ClientBeginZoning</c> rebuilds the entity and empties its stat block.
    /// </para>
    /// </summary>
    [Fact]
    public void ResetClearsTheWindowAndReArmsTheBurstButKeepsTheProfileAndTheLock()
    {
        var tracker = new PlayerMovementTracker();
        tracker.SetSprintDisabled(true);
        tracker.MarkStatsDelivered();
        tracker.Observe(100f);

        tracker.Reset();

        Assert.Equal(0, tracker.Samples);
        Assert.Equal(0, tracker.SpeedViolations);
        Assert.Equal(0f, tracker.PeakHorizontalSpeed, 3);
        Assert.Equal(MovementStance.Standing, tracker.EstimatedStance);
        Assert.True(tracker.SprintDisabled);
        Assert.Same(MovementProfile.Default, tracker.Profile);
        Assert.False(tracker.StatsDelivered);

        // And the burst really can be sent again for the new world.
        tracker.MarkStatsDelivered();
        Assert.True(tracker.StatsDelivered);
    }

    /// <summary>
    /// The wave-3 second-match regression, pinned as a scenario rather than as a field read: two
    /// matches on one gateway link must produce <b>two</b> bursts.
    /// <para>
    /// The bug was that <c>SendMovementStats</c> returns early on
    /// <see cref="PlayerMovementTracker.StatsDelivered"/> while its other clearing path
    /// (<c>SetProfile</c>) is skipped, because <c>ZoneOptions.Movement</c> is the very
    /// <c>MovementProfile.Default</c> instance the tracker was built with — deliberately so, see
    /// <c>MovementTuningTests.TheDefaultPresetIsTheProfileDefaultInstanceItself</c>. With the flag
    /// surviving a world change, match one got the six speeds and every match after it on the same
    /// link ran at the client's 1.0 built-ins, because <c>ClientBeginZoning</c> rebuilds the entity
    /// and empties its stat block. <see cref="PlayerMovementTracker.Reset"/> clearing the flag is
    /// the whole of the fix, and it must not be undone.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryMatchOnOneLinkGetsItsOwnBurst()
    {
        var tracker = new PlayerMovementTracker();
        int bursts = 0;

        for (int match = 0; match < 3; match++)
        {
            tracker.Reset();                       // ClientBeginZoning: a new world, an empty stat map

            // The integrator's guard, verbatim: the profile is the same instance every time, so
            // SetProfile is never called and only Reset can re-arm the burst.
            if (!ReferenceEquals(tracker.Profile, MovementProfile.Default))
            {
                tracker.SetProfile(MovementProfile.Default);
            }

            if (!tracker.StatsDelivered)
            {
                bursts++;
                tracker.MarkStatsDelivered();
            }

            // A mid-match resync must not send a second burst.
            Assert.True(tracker.StatsDelivered);
        }

        Assert.Equal(3, bursts);
    }

    [Fact]
    public void ThePostureTheClientSendsIsReadNotGuessed()
    {
        // docs/49 §3: crouch, sprint, backwards and grounded are on the wire. Before the first
        // posture record the nearest-speed estimate is all there is; after it, it is a read.
        var tracker = new PlayerMovementTracker();
        Assert.Null(tracker.ReportedPosture);
        tracker.Observe(3.0f);
        Assert.Equal(MovementStance.Crouching, tracker.ReportedStance);   // the guess: 3.03 is crouch

        // 0x8401 — on ground, moving backwards, no stance bit. The speed says "crouch" and the
        // client says "standing, backwards"; the client wins.
        tracker.SetReportedPosture(0x8401u);
        Assert.Equal(MovementStance.Standing, tracker.ReportedStance);
        Assert.Equal(MovementStance.Standing, tracker.EstimatedStance);
        Assert.Equal(MovementAxis.Backward, tracker.ReportedAxis);
        Assert.True(tracker.ReportedPosture!.Value.IsOnGround);

        tracker.Reset();
        Assert.Null(tracker.ReportedPosture);
        Assert.Equal(MovementStance.Standing, tracker.ReportedStance);
    }

    [Theory]
    // The four modes docs/49 §I4's acceptance check asks for, at the speeds docs/76 §7.8 predicts
    // after the Z1 port.
    [InlineData(0x0401u, 4.10f, "Standing")]
    [InlineData(0x0405u, 5.74f, "Sprinting")]
    [InlineData(0x0403u, 2.87f, "Crouching")]
    [InlineData(0x8401u, 3.08f, "Standing backwards")]
    public void ASettledPlateauAtThePredictedSpeedLogsAMatch(uint posture, float speed, string described)
    {
        var tracker = new PlayerMovementTracker();
        tracker.SetReportedPosture(posture);

        Assert.Null(tracker.TakeSpeedCheckLine());  // one sample is not a plateau
        tracker.Observe(speed);
        Assert.Null(tracker.TakeSpeedCheckLine());

        tracker.Observe(speed);                     // two consecutive: settled
        string? line = tracker.TakeSpeedCheckLine();

        Assert.NotNull(line);
        Assert.Contains("movement: observed", line);
        Assert.Contains(described, line);
        Assert.Contains("MATCH", line);
        Assert.DoesNotContain("MISMATCH", line);

        // Once per mode per world, not once per sample.
        tracker.Observe(speed);
        Assert.Null(tracker.TakeSpeedCheckLine());
    }

    [Fact]
    public void APlateauFasterThanAnythingSentIsAMismatch()
    {
        // Wave 5's own sprint replayed against the wave-8 profile: 6.60 m/s against a predicted
        // 5.74. Too fast has no innocent explanation — every confound the server cannot see (strafe,
        // partial stick deflection, slope, the client's stamina tiers) pushes the reported speed
        // down, never up (docs/49 §2.5). This is exactly the line the owner's first post-port
        // capture will print if the client somehow keeps its old stat block.
        var tracker = new PlayerMovementTracker();
        tracker.SetReportedPosture(0x0405u);
        tracker.Observe(6.60f);
        tracker.Observe(6.60f);

        string? line = tracker.TakeSpeedCheckLine();
        Assert.NotNull(line);
        Assert.Contains("MISMATCH", line);
        Assert.Contains("Sprinting", line);
        Assert.Contains("0.86 m/s faster", line);
    }

    [Fact]
    public void ARampAFreeFallAndAStandingStillSampleAreNeverJudged()
    {
        var tracker = new PlayerMovementTracker();

        // A ramp: consecutive samples further apart than the wire's own resolution (docs/49 §2.4).
        tracker.SetReportedPosture(0x0405u);
        tracker.Observe(5.3f);
        tracker.Observe(6.9f);
        Assert.Null(tracker.TakeSpeedCheckLine());

        // Free-fall during the drop: airborne, 15-19 m/s, and nothing the server sent explains it.
        tracker.SetReportedPosture(0x0021u);
        tracker.Observe(18.9f);
        tracker.Observe(18.9f);
        Assert.Null(tracker.TakeSpeedCheckLine());
        Assert.Empty(tracker.SettledPeaks);

        // The stop flag: 209/209 zero-speed samples carried it.
        tracker.SetReportedPosture(0x0441u);
        tracker.Observe(0f);
        tracker.Observe(0f);
        Assert.Null(tracker.TakeSpeedCheckLine());

        // And with no posture at all there is no mode to judge against.
        var blind = new PlayerMovementTracker();
        blind.Observe(4.10f);
        blind.Observe(4.10f);
        Assert.Null(blind.TakeSpeedCheckLine());
        Assert.Empty(blind.SettledPeaks);
    }

    [Fact]
    public void ASlowerPlateauIsRecordedButNotJudged()
    {
        // A strafe reports the same posture as a forward run (docs/49 §3), so 3.08 m/s under
        // 0x0401 is honest — the peak is what gets judged, and it only arrives when the player
        // actually runs forward.
        var tracker = new PlayerMovementTracker();
        tracker.SetReportedPosture(0x0401u);
        tracker.Observe(3.08f);
        tracker.Observe(3.08f);
        Assert.Null(tracker.TakeSpeedCheckLine());
        Assert.Equal(3.08f, tracker.SettledPeaks[(MovementStance.Standing, MovementAxis.Forward)], 2);

        tracker.Observe(4.10f);
        tracker.Observe(4.10f);
        Assert.Contains("MATCH", tracker.TakeSpeedCheckLine());
        Assert.Equal(4.10f, tracker.SettledPeaks[(MovementStance.Standing, MovementAxis.Forward)], 2);
    }

    [Fact]
    public void ResetClearsTheSpeedCheckSoTheNextMatchIsCheckedAgain()
    {
        var tracker = new PlayerMovementTracker();
        tracker.SetReportedPosture(0x0401u);
        tracker.Observe(4.10f);
        tracker.Observe(4.10f);
        Assert.NotNull(tracker.TakeSpeedCheckLine());

        tracker.Reset();
        Assert.Empty(tracker.SettledPeaks);
        Assert.Null(tracker.TakeSpeedCheckLine());

        tracker.SetReportedPosture(0x0401u);
        tracker.Observe(4.10f);
        tracker.Observe(4.10f);
        Assert.NotNull(tracker.TakeSpeedCheckLine());
    }

    [Fact]
    public void AnUnusableProfileIsRefusedAtConstruction()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new PlayerMovementTracker(MovementProfile.Default with { MaxMovementSpeed = 0f }));
    }
}
