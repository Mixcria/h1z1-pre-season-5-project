using System.Globalization;
using Cranberry.Zone.Gas;

namespace Cranberry.Tests.Zone.Gas;

// Environment overrides must affect the actual schedule, preserve the selected pacing contract,
// and report incompatible requests without stopping the host.
public sealed class GasTuningTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }

    [Fact]
    public void AQuietBootTakesTheShippedLadderAndLogsNothing()
    {
        GasSettings settings = GasTuning.FromEnvironment(Env(), out string? note);

        Assert.Null(note);
        Assert.Equal(new GasSettings(), settings);
        Assert.Same(GasTuning.Aug2017Retail, settings);
        Assert.Equal("Aug2017Retail", GasTuning.NameOf(settings));
    }

    [Fact]
    public void EveryKnobIsNamedCranberryGasAndNamedOnlyOnce()
    {
        // The owner retunes from these names, and docs/53 lists them; a duplicate or a stray prefix
        // would make one of them silently unreachable.
        string[] names =
        [
            GasTuning.PresetVariable,
            GasTuning.PacingVariable,
            GasTuning.ScaleVariable,
            GasTuning.DamageScaleVariable,
            .. GasTuning.Knobs.Select(k => k.Variable),
        ];

        Assert.All(names, name => Assert.StartsWith("CRANBERRY_GAS_", name, StringComparison.Ordinal));
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(GasTuning.Knobs, knob => Assert.True(knob.Minimum <= knob.Maximum, knob.Variable));
    }

    [Fact]
    public void TheWave4LegacyPresetIsTheScheduleTheOwnerAlreadyPlayTested()
    {
        GasSettings settings = GasTuning.FromEnvironment(
            Env((GasTuning.PresetVariable, "wave4legacy")), out string? note);

        Assert.Equal(GasPacing.FixedWindows, settings.Pacing);
        Assert.Equal(5, settings.PhaseCount);
        Assert.Equal(15f, settings.FinalRadius);
        Assert.Equal(90u, settings.DamageForPhase(1));
        Assert.Equal(270u, settings.DamageForPhase(5));
        Assert.Equal(690_000, GasSchedule.Create(settings, 1).FinishedAtMs);

        // The GEOMETRY, not just the clock. RadiusRoundingMetres = 5 is new this wave, and until the
        // preset overrode it back to 0 the "exact wave-4 schedule" shipped 1810/545/165/50/15 —
        // rounded radii, and therefore, through DrawContainedCentre's (previous.Radius - radius)
        // slack, displaced circle centres as well. Wave 4's own raw ladder, to 3 decimal places:
        Assert.Equal(0f, settings.RadiusRoundingMetres);
        float[] expected = [1810.253f, 546.169f, 164.784f, 49.717f, 15f];
        for (int phase = 1; phase <= 5; phase++)
        {
            Assert.Equal(expected[phase - 1], settings.RadiusForPhase(phase), 3);
        }
        Assert.NotNull(note);
        Assert.Contains("Wave4Legacy", note, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSprintPresetIsExactlyTheLadderAtOneFifthSpeed()
    {
        GasSettings preset = GasTuning.FromEnvironment(Env((GasTuning.PresetVariable, "Sprint")), out _);
        GasSettings scaled = GasTuning.FromEnvironment(Env((GasTuning.ScaleVariable, "0.2")), out _);

        Assert.Equal(preset, scaled);
        Assert.Equal("Sprint", GasTuning.NameOf(scaled));
        Assert.Equal(GasSchedule.Create(GasTuning.Aug2017Retail, 1).FinishedAtMs / 5,
            GasSchedule.Create(preset, 1).FinishedAtMs);
    }

    [Fact]
    public void ScalingTheClockLeavesTheRadiiAndTheDamageAlone()
    {
        GasSettings settings = GasTuning.FromEnvironment(Env((GasTuning.ScaleVariable, "0.2")), out string? note);
        GasSchedule fast = GasSchedule.Create(settings, 7);
        GasSchedule full = GasSchedule.Create(new GasSettings(), 7);

        Assert.Equal(full.Phases.Count, fast.Phases.Count);
        Assert.Equal(
            full.Phases.Select(p => p.Target).ToArray(),
            fast.Phases.Select(p => p.Target).ToArray());
        Assert.Equal(
            full.Phases.Select(p => p.DamagePerTick).ToArray(),
            fast.Phases.Select(p => p.DamagePerTick).ToArray());
        Assert.Equal(full.FinishedAtMs / 5, fast.FinishedAtMs);
        Assert.NotNull(note);
        Assert.Contains("CRANBERRY_GAS_SCALE=0.2", note, StringComparison.Ordinal);
    }

    [Fact]
    public void EachKnobWritesItsOwnValue()
    {
        GasSettings settings = GasTuning.FromEnvironment(
            Env(
                ("CRANBERRY_GAS_PHASES", "6"),
                ("CRANBERRY_GAS_FIRST_REVEAL_MS", "45000"),
                ("CRANBERRY_GAS_FIRST_MOVE_MS", "90000"),
                ("CRANBERRY_GAS_HOLD_MS", "8000"),
                ("CRANBERRY_GAS_WALL_SPEED", "4.25"),
                ("CRANBERRY_GAS_FINAL_RADIUS_M", "60"),
                ("CRANBERRY_GAS_RADIUS_ROUND_M", "0"),
                ("CRANBERRY_GAS_TICK_MS", "500"),
                ("CRANBERRY_GAS_UPDATE_MS", "250"),
                ("CRANBERRY_GAS_BLEND_MS", "2000"),

                // The wave-8 knobs (docs/77 §8). MAX_EDGE is set because the rest of this line
                // asks for a 6-wave ladder on a 4.25 m/s wall, whose leading edge is 5.10 m/s —
                // above the shipped 5.00 rail, which GasSettings.Validate would refuse outright.
                // That refusal is the guard working; raising the rail here is the operator saying
                // "I know, this is a bring-up run".
                ("CRANBERRY_GAS_MAX_EDGE", "8"),
                ("CRANBERRY_GAS_INITIAL_RADIUS_M", "3000"),
                ("CRANBERRY_GAS_CENTRE_X", "-100"),
                ("CRANBERRY_GAS_CENTRE_Z", "250"),
                ("CRANBERRY_GAS_DRIFT", "0.4"),
                ("CRANBERRY_GAS_DRIFT_CONE", "90"),
                (GasTuning.PreMoveRingVariable, "ZeroRadius")),
            out string? note);

        Assert.Equal(6, settings.PhaseCount);
        Assert.Equal(45_000u, settings.FirstRevealDelayMs);
        Assert.Equal(90_000u, settings.FirstMoveDelayMs);
        Assert.Equal(8_000u, settings.InterPhaseHoldMs);
        Assert.Equal(4.25f, settings.ShrinkSpeedMetresPerSecond);
        Assert.Equal(60f, settings.FinalRadius);
        Assert.Equal(0f, settings.RadiusRoundingMetres);
        Assert.Equal(500u, settings.TickPeriodMs);
        Assert.Equal(250u, settings.SafeZoneUpdateIntervalMs);
        Assert.Equal(2_000u, settings.RingBlendMs);
        Assert.Equal(8f, settings.MaxEdgeSpeedMetresPerSecond);
        Assert.Equal(3_000f, settings.InitialRadius);
        Assert.Equal(-100f, settings.PlayAreaCentre.X);
        Assert.Equal(250f, settings.PlayAreaCentre.Z);
        Assert.Equal(0.4f, settings.CentreDriftFraction);
        Assert.Equal(90f, settings.DriftConeDegrees);
        Assert.Equal(GasPreMoveRing.ZeroRadius, settings.PreMoveRing);
        Assert.NotNull(note);
        Assert.DoesNotContain("IGNORED", note, StringComparison.Ordinal);

        // And it still builds a legal match.
        GasSchedule schedule = GasSchedule.Create(settings, 3);
        Assert.Equal(6, schedule.Phases.Count);
        Assert.Equal(90_000, schedule.Phase(1).ShrinkStartAtMs);
    }

    [Fact]
    public void TheDamageScaleMultipliesEveryEntryOfTheTable()
    {
        GasSettings settings = GasTuning.FromEnvironment(
            Env((GasTuning.DamageScaleVariable, "0.5")), out _);

        Assert.Equal(45u, settings.DamageForPhase(1));
        Assert.Equal(300u, settings.DamageForPhase(10));
    }

    [Fact]
    public void ThePacingKnobSwitchesTheModel()
    {
        GasSettings fixedWindows = GasTuning.FromEnvironment(
            Env((GasTuning.PacingVariable, "fixedwindows")), out string? note);

        Assert.Equal(GasPacing.FixedWindows, fixedWindows.Pacing);
        Assert.Equal(180_000u, fixedWindows.WindowForPhase(1));
        Assert.NotNull(note);
        Assert.Contains("FixedWindows", note, StringComparison.Ordinal);
    }

    [Fact]
    public void AGarbledValueIsReportedAndSkippedRatherThanThrown()
    {
        GasSettings settings = GasTuning.FromEnvironment(
            Env(
                (GasTuning.PresetVariable, "NoSuchPreset"),
                (GasTuning.PacingVariable, "sideways"),
                (GasTuning.ScaleVariable, "quickly"),
                ("CRANBERRY_GAS_WALL_SPEED", "-4"),
                ("CRANBERRY_GAS_PHASES", "500")),
            out string? note);

        Assert.Equal(new GasSettings(), settings);
        Assert.NotNull(note);
        Assert.Contains("IGNORED", note, StringComparison.Ordinal);
        Assert.Contains("NoSuchPreset", note, StringComparison.Ordinal);
        Assert.Contains("sideways", note, StringComparison.Ordinal);
        Assert.Contains("CRANBERRY_GAS_WALL_SPEED", note, StringComparison.Ordinal);
        Assert.Contains("CRANBERRY_GAS_PHASES", note, StringComparison.Ordinal);
    }

    [Fact]
    public void AnImpossibleCombinationFallsBackToThePresetInsteadOfStoppingTheHost()
    {
        // Each knob is inside its own rail, but a ring that starts closing before it is drawn is
        // still an impossible match. GasSettings.Validate refuses it; the host must still boot.
        GasSettings settings = GasTuning.FromEnvironment(
            Env(("CRANBERRY_GAS_FIRST_MOVE_MS", "30000")), out string? note);

        Assert.Equal(new GasSettings(), settings);
        Assert.NotNull(note);
        Assert.Contains("refused", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lane 0D deleted <c>CRANBERRY_GAS_PHASE_WINDOW_MS</c>: it was inert under the shipped
    /// SpeedPaced pacing and, under FixedWindows, it moved a value the ladder no longer reads from
    /// the environment. Setting it now changes nothing at all — no schedule move, no note.
    /// </summary>
    [Fact]
    public void TheDeletedPhaseWindowKnobIsIgnoredEntirely()
    {
        GasSettings speedPaced = GasTuning.FromEnvironment(
            Env(("CRANBERRY_GAS_PHASE_WINDOW_MS", "20000")), out string? note);

        Assert.Equal(new GasSettings(), speedPaced);
        Assert.Null(note);

        GasSettings windows = GasTuning.FromEnvironment(
            Env(
                (GasTuning.PacingVariable, "FixedWindows"),
                ("CRANBERRY_GAS_PHASE_WINDOW_MS", "20000")),
            out _);

        // The FixedWindows fields keep their ruling values; only the knob is gone.
        Assert.Equal(new GasSettings().FirstPhaseWindowMs, windows.FirstPhaseWindowMs);
        Assert.Equal(
            new GasSettings { Pacing = GasPacing.FixedWindows }.WindowForPhase(1),
            windows.WindowForPhase(1));
    }

    [Fact]
    public void TheScheduleSummaryNamesWhatTheOwnerWillFeel()
    {
        var settings = new GasSettings();
        GasSchedule schedule = GasSchedule.Create(settings, 1);
        string line = GasTuning.DescribeSchedule(settings);

        Assert.Contains($"{settings.PhaseCount} waves", line, StringComparison.Ordinal);
        Assert.Contains($"reveal {GasTuning.Clock(schedule.Phase(1).RevealAtMs)}", line, StringComparison.Ordinal);
        Assert.Contains($"first move {GasTuning.Clock(schedule.Phase(1).ShrinkStartAtMs)}", line, StringComparison.Ordinal);
        Assert.Contains($"match {GasTuning.Clock(schedule.FinishedAtMs)}", line, StringComparison.Ordinal);
        Assert.Contains("damage 90", line, StringComparison.Ordinal);
        float first = schedule.Phase(1).Target.Radius;
        Assert.Contains(FormattableString.Invariant($"first safe radius {first:0.###} m (diameter {first * 2f:0.###} m)"),
            line, StringComparison.Ordinal);
        string radii = string.Join(", ", schedule.Phases.Select(p => p.Target.Radius.ToString("0.###", CultureInfo.InvariantCulture)));
        Assert.Contains($"target radii [{radii}] m (table)", line, StringComparison.Ordinal);

        // The two the owner is actually going to feel, and neither was in this line before wave 8:
        // what a player has to outrun, and whether there is gas on the map before it moves.
        Assert.Contains(FormattableString.Invariant($"leading edge <= {settings.LeadingEdgeSpeedCeiling():0.00} m/s"),
            line, StringComparison.Ordinal);

        // D276-D280 (docs/118): the boot line now also says where the circles are allowed to go,
        // what ce 01's blend constant means and whether the toxicity meter is armed - the three
        // things a click-test is looking for.
        Assert.Contains("centres PoiDestination", line, StringComparison.Ordinal);
        Assert.Contains("play-area lead 1200 m", line, StringComparison.Ordinal);
        Assert.Contains("ce 01 blend send period", line, StringComparison.Ordinal);
        Assert.Contains("toxicity on", line, StringComparison.Ordinal);
        Assert.Contains("pre-move ring None", line, StringComparison.Ordinal);

        // And the schedule it is replacing, for the A/B.
        string legacy = GasTuning.DescribeSchedule(GasTuning.Wave4Legacy);
        Assert.Contains("5 waves", legacy, StringComparison.Ordinal);
        Assert.Contains("match 11:30", legacy, StringComparison.Ordinal);
        Assert.Contains("wall 27.93 m/s", legacy, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullReaderIsRefusedAtTheCall()
    {
        Assert.Throws<ArgumentNullException>(() => GasTuning.FromEnvironment(null!, out _));
        Assert.Throws<ArgumentNullException>(() => GasTuning.DescribeSchedule(null!));
        Assert.Throws<ArgumentNullException>(() => GasTuning.NameOf(null!));
    }

    [Fact]
    public void FirstRevealAndMovementOverridesUpdateTheFirstTableHoldTogether()
    {
        GasSettings settings = GasTuning.FromEnvironment(Env(
            ("CRANBERRY_GAS_FIRST_REVEAL_MS", "45000"),
            ("CRANBERRY_GAS_FIRST_MOVE_MS", "90000")), out string? note);

        Assert.Equal(GasPacing.PhaseTable, settings.Pacing);
        Assert.Equal(45_000u, settings.HoldDurationsMs[0]);
        Assert.Equal(90_000, GasSchedule.Create(settings, 1).Phase(1).ShrinkStartAtMs);
        Assert.Equal(GasTuning.Aug2017Retail.AdvanceDurationsMs, settings.AdvanceDurationsMs);
        Assert.DoesNotContain("refused", note ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingOnlyTheRevealKeepsTheAbsoluteFirstMovementTime()
    {
        GasSettings settings = GasTuning.FromEnvironment(
            Env(("CRANBERRY_GAS_FIRST_REVEAL_MS", "60000")), out _);
        Assert.Equal(GasTuning.Aug2017Retail.FirstMoveDelayMs - 60_000u, settings.HoldDurationsMs[0]);
        Assert.Equal((long)GasTuning.Aug2017Retail.FirstMoveDelayMs,
            GasSchedule.Create(settings, 1).Phase(1).ShrinkStartAtMs);
    }

    [Fact]
    public void LaterHoldOverrideLeavesTheFirstHoldAndAllAdvancesAlone()
    {
        GasSettings settings = GasTuning.FromEnvironment(
            Env(("CRANBERRY_GAS_HOLD_MS", "17000")), out _);
        Assert.Equal(GasPacing.PhaseTable, settings.Pacing);
        Assert.Equal(GasTuning.Aug2017Retail.HoldDurationsMs[0], settings.HoldDurationsMs[0]);
        Assert.All(settings.HoldDurationsMs.Skip(1), duration => Assert.Equal(17_000u, duration));
        Assert.Equal(GasTuning.Aug2017Retail.AdvanceDurationsMs, settings.AdvanceDurationsMs);
        Assert.NotEqual(settings.HoldDurationsMs, GasTuning.Aug2017Retail.HoldDurationsMs);
    }

    [Fact]
    public void InvalidClockTextDoesNotRecomputeTheTableHold()
    {
        GasSettings settings = GasTuning.FromEnvironment(Env(
            ("CRANBERRY_GAS_FIRST_REVEAL_MS", "later"),
            ("CRANBERRY_GAS_FIRST_MOVE_MS", "-1")), out string? note);
        Assert.Same(GasTuning.Aug2017Retail, settings);
        Assert.Contains("IGNORED", note ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatingTheScalarDefaultsDoesNotFlattenTheDefaultHoldTable()
    {
        GasSettings defaults = GasTuning.Aug2017Retail;
        GasSettings settings = GasTuning.FromEnvironment(Env(
            (GasTuning.PacingVariable, "PhaseTable"),
            ("CRANBERRY_GAS_HOLD_MS", defaults.InterPhaseHoldMs.ToString(CultureInfo.InvariantCulture)),
            ("CRANBERRY_GAS_WALL_SPEED", defaults.ShrinkSpeedMetresPerSecond.ToString(CultureInfo.InvariantCulture))),
            out string? note);
        Assert.Same(defaults, settings);
        Assert.DoesNotContain("IGNORED", note ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void WallSpeedOverrideSelectsTheSpeedPacedModelAndReportsIt()
    {
        GasSettings settings = GasTuning.FromEnvironment(
            Env(("CRANBERRY_GAS_WALL_SPEED", "3.5")), out string? note);
        Assert.Equal(GasPacing.SpeedPaced, settings.Pacing);
        Assert.Equal(3.5f, settings.ShrinkSpeedMetresPerSecond);
        Assert.Contains("Pacing=SpeedPaced", note ?? "", StringComparison.Ordinal);
        Assert.NotEqual(GasTuning.Aug2017Retail.AdvanceMsForPhase(1), settings.AdvanceMsForPhase(1));
    }

    [Fact]
    public void AChangedPhaseCountSelectsDerivedTimingAndGeometricRadii()
    {
        int phases = GasTuning.Aug2017Retail.PhaseCount - 1;
        GasSettings settings = GasTuning.FromEnvironment(
            Env(("CRANBERRY_GAS_PHASES", phases.ToString(CultureInfo.InvariantCulture))), out string? note);
        Assert.Equal(phases, settings.PhaseCount);
        Assert.Equal(GasPacing.SpeedPaced, settings.Pacing);
        Assert.Empty(settings.RadiusLadder);
        Assert.Equal(phases, GasSchedule.Create(settings, 1).Phases.Count);
        Assert.Contains("Pacing=SpeedPaced", note ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void SelectingGeometricRadiiAlsoSelectsDerivedTiming()
    {
        GasSettings settings = GasTuning.FromEnvironment(
            Env(("CRANBERRY_GAS_RADIUS_LADDER", "0")), out string? note);
        Assert.Empty(settings.RadiusLadder);
        Assert.Equal(GasPacing.SpeedPaced, settings.Pacing);
        Assert.Contains("Pacing=SpeedPaced", note ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("refused", note ?? "", StringComparison.Ordinal);
        Assert.Equal(settings.PhaseCount, GasSchedule.Create(settings, 1).Phases.Count);
    }

    [Theory]
    [InlineData("CRANBERRY_GAS_WALL_SPEED", "3.5")]
    [InlineData("CRANBERRY_GAS_PHASES", "3")]
    [InlineData("CRANBERRY_GAS_RADIUS_LADDER", "0")]
    public void ExplicitTablePacingRejectsConflictingKnobsInsteadOfChangingModes(string knob, string value)
    {
        GasSettings settings = GasTuning.FromEnvironment(Env(
            (GasTuning.PacingVariable, "PhaseTable"), (knob, value)), out string? note);
        Assert.Equal(GasPacing.PhaseTable, settings.Pacing);
        Assert.Same(GasTuning.Aug2017Retail, settings);
        Assert.Contains("conflicts with explicit PhaseTable", note ?? "", StringComparison.Ordinal);
        Assert.Contains(knob, note ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void TableHoldOverridesApplyAfterClockScaling()
    {
        GasSettings settings = GasTuning.FromEnvironment(Env(
            (GasTuning.ScaleVariable, "0.2"),
            ("CRANBERRY_GAS_FIRST_REVEAL_MS", "10000"),
            ("CRANBERRY_GAS_FIRST_MOVE_MS", "30000"),
            ("CRANBERRY_GAS_HOLD_MS", "5000")), out _);
        Assert.Equal(20_000u, settings.HoldDurationsMs[0]);
        Assert.All(settings.HoldDurationsMs.Skip(1), duration => Assert.Equal(5000u, duration));
        Assert.Equal(GasTuning.Sprint.AdvanceDurationsMs, settings.AdvanceDurationsMs);
        Assert.Equal(30_000, GasSchedule.Create(settings, 1).Phase(1).ShrinkStartAtMs);
    }

    [Fact]
    public void FootRailRetainsItsPreviousGeometryAndSpeedPacedClock()
    {
        GasSettings settings = GasTuning.FootRail;
        Assert.Equal(GasPacing.SpeedPaced, settings.Pacing);
        Assert.Equal(4200f, settings.InitialRadius);
        Assert.Equal(40f, settings.FinalRadius);
        Assert.Equal(10, settings.PhaseCount);
        Assert.Equal(270_000u, settings.FirstMoveDelayMs);
        Assert.Equal(16_000u, settings.InterPhaseHoldMs);
        Assert.Equal(3.866f, settings.ShrinkSpeedMetresPerSecond);
        Assert.Equal(2635f, settings.RadiusForPhase(1));
        Assert.Equal(1_490_200, GasSchedule.Create(settings, 1).FinishedAtMs);
    }
}
