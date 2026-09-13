using System.Numerics;
using Cranberry.Tests.Zone.MatchDrop;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Match;
using Cranberry.Zone.Movement;

namespace Cranberry.Tests.Zone.Gas;

// Geometry, phase transitions and legacy pacing regressions. The default's observed rows come
// from public gameplay on build 0.0.118.208059; the unobserved tail is explicitly provisional.
public sealed class GasScheduleTests
{
    private const ulong Seed = 0x1234_5678_9ABC_DEF0UL;

    /// <summary>The configured end time includes a provisional continuation beyond the observed footage.</summary>
    private const long FinishedAtMs = 1_830_000;

    private static GasSchedule Default() => GasSchedule.Create(new GasSettings(), Seed);

    [Fact]
    public void DefaultSettingsProduceTheConfiguredPhaseTable()
    {
        GasSchedule schedule = Default();
        Assert.Equal(10, schedule.Phases.Count);
        Assert.Equal(Enumerable.Range(1, 10), schedule.Phases.Select(p => p.Index));
        Assert.Equal(GasPacing.PhaseTable, schedule.Settings.Pacing);
    }

    // First seven holds and first six advances are observed. Tail continuation is provisional.
    [Theory]
    [InlineData(1, 120_000, 370_000, 670_000, 2000f, 90u)]
    [InlineData(2, 670_000, 790_000, 850_000, 1400f, 100u)]
    [InlineData(3, 850_000, 940_000, 1_000_000, 900f, 120u)]
    [InlineData(4, 1_000_000, 1_090_000, 1_150_000, 625f, 150u)]
    [InlineData(5, 1_150_000, 1_210_000, 1_270_000, 300f, 200u)]
    [InlineData(6, 1_270_000, 1_330_000, 1_390_000, 137.5f, 270u)]
    [InlineData(7, 1_390_000, 1_440_000, 1_500_000, 75f, 400u)]
    [InlineData(8, 1_500_000, 1_550_000, 1_610_000, 60f, 600u)]
    [InlineData(9, 1_610_000, 1_660_000, 1_720_000, 45f, 600u)]
    [InlineData(10, 1_720_000, 1_770_000, 1_830_000, 40f, 600u)]
    public void TheDefaultScheduleUsesMeasuredRowsAndExplicitContinuation(
        int index,
        long revealAtMs,
        long shrinkStartAtMs,
        long closedAtMs,
        float radius,
        uint damagePerTick)
    {
        GasPhase phase = Default().Phase(index);

        Assert.Equal(revealAtMs, phase.RevealAtMs);
        Assert.Equal(shrinkStartAtMs, phase.ShrinkStartAtMs);
        Assert.Equal(closedAtMs, phase.ClosedAtMs);
        Assert.Equal(radius, phase.Target.Radius, 0.001f);
        Assert.Equal(damagePerTick, phase.DamagePerTick);
    }

    [Fact]
    public void PhaseOneHasTheExactBuildFourMinuteTenSecondHold()
    {
        // Exact-build footage shows a 250-second first hold. The configured reveal remains at 2:00,
        // placing movement at 6:10 on the server match clock.
        var settings = new GasSettings();
        GasPhase phase = Default().Phase(1);

        Assert.Equal(120_000, phase.RevealAtMs);
        Assert.Equal(370_000, phase.ShrinkStartAtMs);
        Assert.Equal(250_000, phase.ShrinkStartAtMs - phase.RevealAtMs);
        Assert.Equal(settings.FirstRevealDelayMs, (uint)phase.RevealAtMs);
        Assert.Equal(settings.FirstMoveDelayMs, (uint)phase.ShrinkStartAtMs);
    }

    [Fact]
    public void TheConfiguredContinuationEndsAtThirtyThirty()
    {
        // This is the configured ten-phase endpoint; the recording ends before the final phases.
        GasSchedule schedule = Default();
        Assert.Equal(FinishedAtMs, schedule.FinishedAtMs);
        Assert.Equal(1_830_000, schedule.FinishedAtMs);
    }

    /// <summary>
    /// Leading-edge speed includes both radial shrink and centre movement.
    /// FootRail retains its historical sprint margin; the default uses its configured edge ceiling.
    /// </summary>
    [Fact]
    public void NoRingEdgeEverTravelsFasterThanAPlayerCanSprint()
    {
        // CRANBERRY_GAS_PRESET=FootRail preserves the historical sprint-bound schedule.
        double sprint = MovementProfile.Default.SprintSpeed;
        foreach (GasPhase phase in GasSchedule.Create(GasTuning.FootRail, Seed).Phases)
        {
            double seconds = phase.ShrinkDurationMs / 1000d;
            double edge =
                (Vector3.Distance(phase.Origin.Centre, phase.Target.Centre)
                    + phase.Origin.Radius - phase.Target.Radius)
                / seconds;

            Assert.InRange(edge, 0.1d, sprint - 0.5d);
        }

        // Check the default separately against its configured ceiling.
        var shipped = new GasSettings();
        foreach (GasPhase phase in Default().Phases)
        {
            double seconds = phase.ShrinkDurationMs / 1000d;
            double edge =
                (Vector3.Distance(phase.Origin.Centre, phase.Target.Centre)
                    + phase.Origin.Radius - phase.Target.Radius)
                / seconds;

            Assert.True(edge > 0d && edge <= shipped.MaxEdgeSpeedMetresPerSecond);
        }
    }

    /// <summary>
    /// The reveal-to-close window must cover the radial drop plus the centre offset
    /// within each preset's configured traversal bound.
    /// </summary>
    [Fact]
    public void TheWorstPlacedPlayerCanWalkToEveryCircle()
    {
        // Preserve FootRail's sprint margin and check the default against its edge ceiling.
        double sprint = MovementProfile.Default.SprintSpeed;
        var shipped = new GasSettings();
        for (ulong seed = 0; seed < 32; seed++)
        {
            foreach (GasPhase phase in GasSchedule.Create(GasTuning.FootRail, seed).Phases)
            {
                double seconds = (phase.ClosedAtMs - phase.RevealAtMs) / 1000d;
                double traverse =
                    (Vector3.Distance(phase.Origin.Centre, phase.Target.Centre)
                        + phase.Origin.Radius - phase.Target.Radius)
                    / seconds;

                Assert.True(
                    traverse < sprint - 0.5d,
                    $"seed {seed} phase {phase.Index}: {traverse:F2} m/s required against a {sprint:F2} m/s sprint");
            }

            foreach (GasPhase phase in GasSchedule.Create(shipped, seed).Phases)
            {
                double seconds = (phase.ClosedAtMs - phase.RevealAtMs) / 1000d;
                double traverse =
                    (Vector3.Distance(phase.Origin.Centre, phase.Target.Centre)
                        + phase.Origin.Radius - phase.Target.Radius)
                    / seconds;

                Assert.True(
                    traverse < shipped.MaxEdgeSpeedMetresPerSecond,
                    $"seed {seed} phase {phase.Index}: {traverse:F2} m/s required against the "
                    + $"{shipped.MaxEdgeSpeedMetresPerSecond:F2} m/s rail");
            }
        }
    }

    /// <summary>
    /// The observed 20 m/s first radial advance and 0.96 drift cap imply a 39.2 m/s
    /// analytic edge ceiling. Construction rejects settings that exceed their configured bound.
    /// </summary>
    [Fact]
    public void TheLeadingEdgeCeilingIsAnalyticAndEnforced()
    {
        var settings = new GasSettings();
        Assert.Equal(0.96f, settings.CentreDriftFraction);
        Assert.Equal(39.2d, settings.LeadingEdgeSpeedCeiling(), 2);

        // FootRail retains the historical 0.20 drift cap and its slower speed pacing.
        Assert.Equal(0.20f, GasTuning.FootRail.CentreDriftFraction);
        Assert.Equal(4.645d, GasTuning.FootRail.LeadingEdgeSpeedCeiling(), 3);
        Assert.True(settings.LeadingEdgeSpeedCeiling() <= settings.MaxEdgeSpeedMetresPerSecond);

        // Every drawn centre honours the cap, seed after seed.
        for (ulong seed = 0; seed < 64; seed++)
        {
            GasSchedule schedule = GasSchedule.Create(settings, seed);
            foreach (GasPhase phase in schedule.Phases)
            {
                float drop = phase.Origin.Radius - phase.Target.Radius;
                Assert.True(
                    Vector3.Distance(phase.Origin.Centre, phase.Target.Centre)
                        <= (drop * settings.CentreDriftFraction) + 0.01f,
                    $"seed {seed} phase {phase.Index} drifted past the cap");
            }
        }

        // Full centre drift exceeds an explicitly tightened 39 m/s ceiling.
        ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(
            () => GasSchedule.Create(new GasSettings { CentreDriftFraction = 1f, MaxEdgeSpeedMetresPerSecond = 39f }, Seed));
        Assert.Contains("leading edge", refused.Message, StringComparison.Ordinal);

        // Historical speed-paced regression: a 5.539 m/s radial speed already exceeds a 5 m/s bound.
        var shipped = new GasSettings { Pacing = GasPacing.SpeedPaced, InitialRadius = 6000f, ShrinkSpeedMetresPerSecond = 5.539f };
        Assert.True(shipped.MaxCentreDriftFractionFor(5.0d) < 0d);
    }

    /// <summary>
    /// One heading per match inside a 120 degree cone, so a capped budget is spent on going
    /// somewhere rather than on a random walk (docs/77 §4.4). Measured over 400 seeds: the final
    /// circle sits a median ~510 m from the play-area centre and never nearer than ~100 m.
    /// </summary>
    [Fact]
    public void TheCircleWalksOneWayAcrossTheMap()
    {
        // D277 (docs/118 §3) replaced the cone walk with an aimed one on the DEFAULT, and kept the
        // cone as GasCentrePlan.Drift. This test measures the cone, so it measures the preset that
        // still uses it; GasCentrePlanTests owns the shipped plan's own distribution.
        GasSettings settings = GasTuning.FootRail;
        var distances = new List<float>();
        for (ulong seed = 0; seed < 400; seed++)
        {
            GasSchedule schedule = GasSchedule.Create(settings, seed);
            distances.Add(Vector3.Distance(schedule.FinalCircle.Centre, settings.PlayAreaCentre));
        }

        distances.Sort();
        float median = distances[distances.Count / 2];
        Assert.InRange(median, 400f, 620f);
        Assert.True(distances[0] > 80f, $"the least-moved match only walked {distances[0]:F0} m");

        // A uniform angle spends the same budget on a random walk and lands half as far out. This
        // is the measurement the cone is here for, pinned as a comparison rather than as a rule.
        var uniform = settings with { DriftConeDegrees = 360f };
        var uniformDistances = new List<float>();
        for (ulong seed = 0; seed < 400; seed++)
        {
            uniformDistances.Add(Vector3.Distance(
                GasSchedule.Create(uniform, seed).FinalCircle.Centre, settings.PlayAreaCentre));
        }

        uniformDistances.Sort();
        Assert.True(
            uniformDistances[uniformDistances.Count / 2] < median,
            "the cone must move the circle further than a uniform angle does");
    }

    /// <summary>
    /// FootRail derives its radial speed from its legacy match-length budget.
    /// The explicit phase table used by the default is tested separately.
    /// </summary>
    [Fact]
    public void LegacySpeedPacingStillSolvesItsMatchLengthBudget()
    {
        GasSettings settings = GasTuning.FootRail;

        Assert.Equal(3.866d, settings.SolvedShrinkSpeedMetresPerSecond(), 3);
        Assert.Equal(settings.SolvedShrinkSpeedMetresPerSecond(), settings.ShrinkSpeedMetresPerSecond, 3);

        // Travel, the opening delay and nine 16-second holds consume the legacy budget.
        double travelSeconds = (settings.InitialRadius - settings.FinalRadius) / settings.ShrinkSpeedMetresPerSecond;
        double total = travelSeconds
            + (settings.FirstMoveDelayMs / 1000d)
            + ((settings.PhaseCount - 1) * settings.InterPhaseHoldMs / 1000d);
        Assert.Equal(settings.TargetMatchLengthMs / 1000d, total, 1);
    }

    /// <summary>
    /// The 8,000 m opening boundary contains every fixture drop location.
    /// The outer wall stays hidden and nonlethal until the first advance.
    /// </summary>
    [Fact]
    public void OpeningBoundaryCoversDropLocationsAndStaysDormantUntilMovement()
    {
        var settings = new GasSettings();
        Assert.Equal(8000f, settings.InitialRadius);
        Assert.Equal(new Vector3(-250f, 0f, 100f), settings.PlayAreaCentre);

        var area = new GasCircle(settings.PlayAreaCentre, settings.InitialRadius);
        float farthest = 0f;
        string farthestName = string.Empty;
        foreach (DropPoi place in DropFixture.Places.Places)
        {
            float distance = area.HorizontalDistanceTo(place.Anchor);
            if (distance > farthest)
            {
                farthest = distance;
                farthestName = place.Area;
            }
        }

        Assert.True(farthest < settings.InitialRadius, farthestName);
        var schedule = GasSchedule.Create(settings);
        Assert.False(schedule.IsLethalAt(settings.FirstMoveDelayMs - 1));
        Assert.False(schedule.IsRingVisibleAt(settings.FirstMoveDelayMs - 1));
        Assert.True(schedule.IsLethalAt(settings.FirstMoveDelayMs));
    }

    [Fact]
    public void PhasesChainBackToBackWithTheDefaultZeroHold()
    {
        GasSchedule schedule = Default();
        for (int index = 1; index < schedule.Phases.Count; index++)
        {
            Assert.Equal(schedule.Phase(index).ClosedAtMs, schedule.Phase(index + 1).RevealAtMs);
        }

        Assert.Equal(FinishedAtMs, schedule.FinishedAtMs);
    }

    [Fact]
    public void APhaseHoldPushesEveryLaterPhaseOut()
    {
        GasSchedule schedule = GasSchedule.Create(new GasSettings { PhaseHoldMs = 10_000 }, Seed);
        Assert.Equal(670_000, schedule.Phase(1).ClosedAtMs);
        Assert.Equal(680_000, schedule.Phase(2).RevealAtMs);
        Assert.Equal(FinishedAtMs + (9 * 10_000), schedule.FinishedAtMs);
    }

    [Fact]
    public void EveryCircleIsWhollyContainedInItsPredecessor()
    {
        // Each target must lie wholly inside its predecessor; traversal timing is tested separately.
        for (ulong seed = 0; seed < 64; seed++)
        {
            GasSchedule schedule = GasSchedule.Create(new GasSettings(), seed);
            GasCircle previous = schedule.InitialCircle;
            foreach (GasPhase phase in schedule.Phases)
            {
                Assert.Equal(previous, phase.Origin);
                Assert.True(
                    previous.Contains(phase.Target),
                    $"seed {seed} phase {phase.Index}: {phase.Target} escapes {previous}");
                previous = phase.Target;
            }
        }
    }

    [Fact]
    public void RadiiFallMonotonicallyAndLandExactlyOnTheFinalRadius()
    {
        GasSchedule schedule = Default();
        float previous = schedule.InitialCircle.Radius;
        foreach (GasPhase phase in schedule.Phases)
        {
            Assert.True(phase.Target.Radius < previous, $"phase {phase.Index} did not shrink");
            previous = phase.Target.Radius;
        }

        Assert.Equal(new GasSettings().FinalRadius, schedule.FinalCircle.Radius);
    }

    [Fact]
    public void TheRadiiUseThePublicMatchMeasurementsAndRetainGeometricTuning()
    {
        // The default uses measured public-match targets followed by a provisional continuation.
        // An empty ladder still enables geometric tuning at the explicitly configured opening radius.
        Assert.Equal(
            new[] { 2000f, 1400f, 900f, 625f, 300f, 137.5f, 75f, 60f, 45f, 40f },
            Default().Phases.Select(p => p.Target.Radius).ToArray());

        Assert.Equal(
            new[] { 2_750f, 1_720f, 1_075f, 670f, 420f, 260f, 165f, 100f, 65f, 40f },
            GasSchedule.Create(new GasSettings { InitialRadius = 4400f, RadiusLadder = [] }, Seed)
                .Phases.Select(p => p.Target.Radius).ToArray());
    }

    /// <summary>
    /// Rounding radii to 5 m is cosmetic and must never be able to flatten two rungs of the ladder
    /// into one - including for the compressed circles a bring-up run asks for
    /// (<c>CRANBERRY_GAS_INITIAL_RADIUS_M=600</c>) and for phase counts nobody has tried.
    /// </summary>
    [Theory]
    [InlineData(4400f, 40f, 10)]
    [InlineData(6000f, 40f, 10)]
    [InlineData(6000f, 15f, 5)]
    [InlineData(600f, 40f, 10)]
    [InlineData(120f, 40f, 10)]
    [InlineData(60f, 40f, 20)]
    [InlineData(6000f, 40f, 20)]
    public void RadiusRoundingNeverFlattensTheLadder(float initial, float final, int phases)
    {
        var settings = new GasSettings { InitialRadius = initial, FinalRadius = final, PhaseCount = phases };

        float previous = settings.InitialRadius;
        for (int phase = 1; phase <= phases; phase++)
        {
            float radius = settings.RadiusForPhase(phase);
            Assert.True(radius < previous, $"phase {phase}: {radius} did not shrink below {previous}");
            previous = radius;
        }

        Assert.Equal(final, previous);
    }

    [Fact]
    public void ANonDefaultPhaseCountStillEndsOnTheFinalRadius()
    {
        GasSchedule schedule = GasSchedule.Create(new GasSettings { Pacing = GasPacing.SpeedPaced, PhaseCount = 8 }, Seed);
        Assert.Equal(8, schedule.Phases.Count);
        Assert.Equal(new GasSettings().FinalRadius, schedule.FinalCircle.Radius);
    }

    [Fact]
    public void TheSameSeedReplaysTheSameCircles()
    {
        GasSchedule first = GasSchedule.Create(new GasSettings(), Seed);
        GasSchedule second = GasSchedule.Create(new GasSettings(), Seed);
        Assert.Equal(
            first.Phases.Select(p => p.Target).ToArray(),
            second.Phases.Select(p => p.Target).ToArray());
    }

    [Fact]
    public void ADifferentSeedMovesTheCircles()
    {
        GasSchedule first = GasSchedule.Create(new GasSettings(), Seed);
        GasSchedule second = GasSchedule.Create(new GasSettings(), Seed + 1);
        Assert.NotEqual(first.Phase(1).Target.Centre, second.Phase(1).Target.Centre);
    }

    [Fact]
    public void TheSettingsSeedIsUsedWhenNoneIsGiven()
    {
        var settings = new GasSettings { Seed = 42 };
        Assert.Equal(
            GasSchedule.Create(settings, 42).Phase(1).Target,
            GasSchedule.Create(settings).Phase(1).Target);
    }

    [Fact]
    public void PhaseIndexWalksZeroThroughPhaseCount()
    {
        GasSchedule schedule = Default();
        Assert.Equal(0, schedule.PhaseIndexAt(0));
        Assert.Equal(0, schedule.PhaseIndexAt(119_999));
        Assert.Equal(1, schedule.PhaseIndexAt(120_000));
        Assert.Equal(1, schedule.PhaseIndexAt(669_999));
        Assert.Equal(2, schedule.PhaseIndexAt(670_000));
        Assert.Equal(10, schedule.PhaseIndexAt(FinishedAtMs));
        Assert.Equal(10, schedule.PhaseIndexAt(10_000_000));
        Assert.Null(schedule.PhaseAt(0));
    }

    [Fact]
    public void TheActiveCircleHoldsThroughTheWarningHeadThenInterpolates()
    {
        GasSchedule schedule = Default();
        GasPhase phase = schedule.Phase(1);

        Assert.Equal(schedule.InitialCircle, schedule.ActiveCircleAt(0));
        Assert.Equal(schedule.InitialCircle, schedule.ActiveCircleAt(phase.ShrinkStartAtMs));
        Assert.False(schedule.IsClosingAt(phase.ShrinkStartAtMs - 1));
        Assert.True(schedule.IsClosingAt(phase.ShrinkStartAtMs));

        // Halfway through phase 1's observed 300-second advance.
        const long Middle = (370_000 + 670_000) / 2;
        GasCircle middle = schedule.ActiveCircleAt(Middle);
        Assert.True(schedule.IsClosingAt(Middle));
        Assert.Equal((phase.Origin.Radius + phase.Target.Radius) / 2f, middle.Radius, 0.01f);
        Assert.Equal((phase.Origin.Centre.X + phase.Target.Centre.X) / 2f, middle.Centre.X, 0.01f);
    }

    [Fact]
    public void TheActiveCircleSettlesOnTheTargetAndStaysOnTheFinalOne()
    {
        GasSchedule schedule = Default();
        Assert.Equal(schedule.Phase(1).Target, schedule.ActiveCircleAt(670_000));
        Assert.Equal(schedule.FinalCircle, schedule.ActiveCircleAt(schedule.FinishedAtMs));
        Assert.Equal(schedule.FinalCircle, schedule.ActiveCircleAt(schedule.FinishedAtMs + 600_000));
        Assert.False(schedule.IsClosingAt(schedule.FinishedAtMs));
    }

    [Fact]
    public void TheRevealedCircleIsTheCurrentPhaseTarget()
    {
        GasSchedule schedule = Default();
        Assert.Equal(schedule.InitialCircle, schedule.RevealedCircleAt(0));
        Assert.Equal(schedule.Phase(1).Target, schedule.RevealedCircleAt(120_000));
        Assert.Equal(schedule.Phase(2).Target, schedule.RevealedCircleAt(670_000));
    }

    [Fact]
    public void NextEventWalksRevealShrinkAndCloseThenStops()
    {
        GasSchedule schedule = Default();
        Assert.Equal(120_000, schedule.NextEventAtMs(0));
        Assert.Equal(370_000, schedule.NextEventAtMs(120_000));
        Assert.Equal(670_000, schedule.NextEventAtMs(370_000));
        Assert.Equal(790_000, schedule.NextEventAtMs(670_000));
        Assert.Equal(long.MaxValue, schedule.NextEventAtMs(schedule.FinishedAtMs));
    }

    [Fact]
    public void DamagePerTickWalksTheRetailCurve()
    {
        GasSchedule schedule = Default();
        Assert.Equal(
            new[] { 90u, 100u, 120u, 150u, 200u, 270u, 400u, 600u, 600u, 600u },
            schedule.Phases.Select(p => p.DamagePerTick).ToArray());

        // 0.9 %/s rising to 6.0 %/s of the 10,000 bar.
        Assert.Equal(10_000u, schedule.Settings.MaxHitpoints);

        // The damage lookup supplies phase 1's rate before reveal; lethality has its own gate.
        Assert.Equal(90u, schedule.DamagePerTickAt(0));
        Assert.Equal(600u, schedule.DamagePerTickAt(schedule.FinishedAtMs));
    }

    [Fact]
    public void AMatchWithMorePhasesThanTheDamageTableRepeatsItsLastEntry()
    {
        var settings = new GasSettings { PhaseCount = 13 };
        Assert.Equal(600u, settings.DamageForPhase(11));
        Assert.Equal(600u, settings.DamageForPhase(13));
        Assert.Equal(600u, settings.DamageForPhase(999));
    }

    [Fact]
    public void AnEmptyDamageTableFallsBackToTheWave4LinearCurve()
    {
        var settings = new GasSettings { PhaseCount = 5, DamagePerPhase = [] };
        Assert.Equal(
            new[] { 90u, 135u, 180u, 225u, 270u },
            Enumerable.Range(1, 5).Select(settings.DamageForPhase).ToArray());
    }

    /// <summary>
    /// <c>CRANBERRY_GAS_PRESET=Wave4Legacy</c> has to reproduce what the owner already play-tested,
    /// to the millisecond, or the A/B it exists for proves nothing.
    /// </summary>
    [Fact]
    public void TheWave4LegacyPresetReplaysTheShippedWave4Schedule()
    {
        GasSchedule schedule = GasSchedule.Create(GasTuning.Wave4Legacy, Seed);

        Assert.Equal(5, schedule.Phases.Count);
        Assert.Equal(
            new[] { 180_000L, 120_000L, 90_000L, 90_000L, 90_000L },
            schedule.Phases.Select(p => p.WindowMs).ToArray());
        Assert.Equal(690_000, schedule.FinishedAtMs);
        Assert.Equal(150_000, schedule.Phase(1).ShrinkStartAtMs);
        Assert.Equal(
            new[] { 90u, 135u, 180u, 225u, 270u },
            schedule.Phases.Select(p => p.DamagePerTick).ToArray());
        Assert.Equal(15f, schedule.FinalCircle.Radius);

        // And the defect it documents: the phase-1 wall at five times a sprint.
        GasPhase first = schedule.Phase(1);
        double speed = (first.Origin.Radius - first.Target.Radius) / (first.ShrinkDurationMs / 1000d);
        Assert.InRange(speed, 27d, 29d);
    }

    /// <summary>
    /// <c>CRANBERRY_GAS_SCALE</c> shortens the configured schedule for manual testing
    /// while preserving circle geometry and damage values.
    /// </summary>
    [Fact]
    public void ScalingTheClockMovesEveryTimeAndNothingElse()
    {
        var full = new GasSettings();
        GasSettings fifth = full.ScaledBy(0.2f);
        GasSchedule fullSchedule = GasSchedule.Create(full, Seed);
        GasSchedule fifthSchedule = GasSchedule.Create(fifth, Seed);

        Assert.Equal(24_000u, fifth.FirstRevealDelayMs);
        Assert.Equal(74_000u, fifth.FirstMoveDelayMs);
        Assert.Equal(3_200u, fifth.InterPhaseHoldMs);
        Assert.Equal(366_000, fifthSchedule.FinishedAtMs);
        Assert.Equal(fullSchedule.FinishedAtMs / 5d, fifthSchedule.FinishedAtMs, 200d);

        // Same circles, same damage: only the clock moved.
        Assert.Equal(
            fullSchedule.Phases.Select(p => p.Target).ToArray(),
            fifthSchedule.Phases.Select(p => p.Target).ToArray());
        Assert.Equal(
            fullSchedule.Phases.Select(p => p.DamagePerTick).ToArray(),
            fifthSchedule.Phases.Select(p => p.DamagePerTick).ToArray());

        // A factor of 1 (or a nonsense one) is a no-op, and returns the same instance.
        Assert.Same(full, full.ScaledBy(1f));
        Assert.Same(full, full.ScaledBy(0f));
        Assert.Same(full, full.ScaledBy(float.NaN));
    }

    [Fact]
    public void ContainmentUsesTheHorizontalPlaneOnly()
    {
        var circle = new GasCircle(new Vector3(0f, 500f, 0f), 100f);
        Assert.True(circle.Contains(new Vector3(0f, -9_000f, 0f)));
        Assert.True(circle.Contains(new Vector3(60f, 500f, 60f)));
        Assert.False(circle.Contains(new Vector3(80f, 500f, 80f)));
        Assert.Equal(100f, circle.HorizontalDistanceTo(new Vector3(100f, 12f, 0f)), 0.001f);
    }

    [Fact]
    public void AnUnresolvedPositionCountsAsInside()
    {
        // Sparse movement records can leave a NaN behind; the gas must not punish our own gap.
        var circle = new GasCircle(Vector3.Zero, 10f);
        Assert.True(circle.Contains(new Vector3(float.NaN, 0f, 0f)));
        Assert.True(circle.Contains(new Vector3(0f, 0f, float.PositiveInfinity)));
    }

    [Fact]
    public void IsInsideSafeZoneTracksTheClosingCircle()
    {
        // D278: the play area may LEAD toward the match's destination, so the initial circle's
        // centre is a per-match value and the edge point has to be built from the schedule rather
        // than from the setting.
        GasSchedule schedule = GasSchedule.Create(new GasSettings { PlayAreaCentre = Vector3.Zero }, Seed);
        var edge = new Vector3(
            schedule.InitialCircle.Centre.X + schedule.InitialCircle.Radius - 1f,
            500f,
            schedule.InitialCircle.Centre.Z);
        Assert.True(schedule.IsInsideSafeZone(0, edge));
        Assert.False(schedule.IsInsideSafeZone(schedule.FinishedAtMs, edge));
    }

    [Fact]
    public void ImpossibleSettingsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GasSchedule.Create(new GasSettings { PhaseCount = 0 }, Seed));
        Assert.Throws<ArgumentOutOfRangeException>(() => GasSchedule.Create(new GasSettings { FinalRadius = 99_999f }, Seed));
        Assert.Throws<ArgumentOutOfRangeException>(() => GasSchedule.Create(new GasSettings { TickPeriodMs = 0 }, Seed));
        Assert.Throws<ArgumentOutOfRangeException>(() => GasSchedule.Create(new GasSettings { SafeZoneUpdateIntervalMs = 0 }, Seed));
        Assert.Throws<ArgumentOutOfRangeException>(() => GasSchedule.Create(new GasSettings { MaxHitpoints = 0 }, Seed));
        Assert.Throws<ArgumentOutOfRangeException>(() => GasSchedule.Create(new GasSettings { HostTickIntervalMs = 0 }, Seed));

        // Wave-5 additions. A ring that starts closing before it has been drawn is the one that
        // would actually kill a player who was never shown a circle.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GasSchedule.Create(new GasSettings { Pacing = GasPacing.SpeedPaced, FirstMoveDelayMs = 60_000 }, Seed));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GasSchedule.Create(new GasSettings { Pacing = GasPacing.SpeedPaced, ShrinkSpeedMetresPerSecond = 0f }, Seed));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GasSchedule.Create(new GasSettings { Pacing = GasPacing.SpeedPaced, AdvanceRoundingMs = 0 }, Seed));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GasSchedule.Create(new GasSettings { RadiusRoundingMetres = -1f }, Seed));

        // The same three are irrelevant under FixedWindows and must not be validated there.
        GasSchedule.Create(
            GasTuning.Wave4Legacy with { ShrinkSpeedMetresPerSecond = 0f, FirstMoveDelayMs = 0, AdvanceRoundingMs = 0 },
            Seed);
    }
}
