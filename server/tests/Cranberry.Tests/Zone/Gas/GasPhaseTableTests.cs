using Cranberry.Zone.Gas;

namespace Cranberry.Tests.Zone.Gas;

public sealed class GasPhaseTableTests
{
    // Synthetic, deliberately unequal phase durations verify the pacing mechanism independently
    // of the historical default table. These values are not retail evidence.
    private static GasSettings Table() => new()
    {
        Pacing = GasPacing.PhaseTable,
        PhaseCount = 3,
        InitialRadius = 1000f,
        FinalRadius = 100f,
        FirstRevealDelayMs = 120_000,
        HoldDurationsMs = [240_000, 90_000, 0],
        AdvanceDurationsMs = [180_000, 120_000, 60_000],
    };

    [Fact]
    public void EachPhaseUsesItsExactHoldAndMovementDuration()
    {
        GasSettings settings = Table();
        GasSchedule schedule = GasSchedule.Create(settings, 1);

        Assert.Equal(new[] { (120_000L, 360_000L, 540_000L), (540_000L, 630_000L, 750_000L),
            (750_000L, 750_000L, 810_000L) }, Times(schedule));
        Assert.Equal(360_000, schedule.RingLiveFromMs);
        Assert.Equal(810_000, schedule.FinishedAtMs);
        Assert.False(schedule.IsLethalAt(359_999));
        Assert.True(schedule.IsLethalAt(360_000));
        Assert.Equal(240_000u, settings.HoldMsForPhase(0));
        Assert.Equal(60_000u, settings.AdvanceMsForPhase(99));
    }

    [Fact]
    public void ChangingRadiiDoesNotChangeTheTableTimetable()
    {
        GasSettings original = Table();
        GasSettings smaller = original with { InitialRadius = 500f, FinalRadius = 50f };
        GasSchedule before = GasSchedule.Create(original, 2);
        GasSchedule after = GasSchedule.Create(smaller, 2);

        Assert.Equal(Times(before), Times(after));
        Assert.True(after.Phase(1).Target.Radius < before.Phase(1).Target.Radius);
        Assert.True(smaller.LeadingEdgeSpeedCeiling() < original.LeadingEdgeSpeedCeiling());
    }

    [Fact]
    public void ScalarValuesFromTheOtherPacingModelsDoNotChangeTableTimes()
    {
        GasSettings original = Table();
        GasSettings changed = original with
        {
            FirstMoveDelayMs = 0,
            InterPhaseHoldMs = 999_999,
            ShrinkSpeedMetresPerSecond = float.NaN,
            AdvanceRoundingMs = 0,
            FirstPhaseWindowMs = 0,
            MinimumPhaseWindowMs = 0,
            ShrinkWarningMs = 0,
            PhaseWindowShorteningMs = uint.MaxValue,
        };

        Assert.Equal(Times(GasSchedule.Create(original, 3)), Times(GasSchedule.Create(changed, 3)));
    }

    [Fact]
    public void ScalingTheClockScalesEveryTableEntryAndPreservesZeroHolds()
    {
        GasSettings full = Table();
        GasSettings fast = full.ScaledBy(0.2f);
        Assert.Equal(new uint[] { 48_000, 18_000, 0 }, fast.HoldDurationsMs);
        Assert.Equal(new uint[] { 36_000, 24_000, 12_000 }, fast.AdvanceDurationsMs);
        Assert.Equal(new uint[] { 240_000, 90_000, 0 }, full.HoldDurationsMs);
        Assert.Equal(new uint[] { 180_000, 120_000, 60_000 }, full.AdvanceDurationsMs);

        GasSchedule original = GasSchedule.Create(full, 4);
        GasSchedule compressed = GasSchedule.Create(fast, 4);
        foreach (GasPhase phase in original.Phases)
        {
            GasPhase scaled = compressed.Phase(phase.Index);
            Assert.Equal(phase.RevealAtMs / 5, scaled.RevealAtMs);
            Assert.Equal(phase.ShrinkStartAtMs / 5, scaled.ShrinkStartAtMs);
            Assert.Equal(phase.ClosedAtMs / 5, scaled.ClosedAtMs);
            Assert.Equal(phase.Origin, scaled.Origin);
            Assert.Equal(phase.Target, scaled.Target);
        }

        Assert.Equal(162_000, compressed.FinishedAtMs);
    }

    [Fact]
    public void ShortScaledAdvancesRemainPositiveWithoutRepairingInvalidZeroEntries()
    {
        GasSettings fast = Table().ScaledBy(0.000001f);
        Assert.All(fast.AdvanceDurationsMs, duration => Assert.Equal(1u, duration));
        fast.Validate();

        GasSettings invalid = Table() with { AdvanceDurationsMs = [180_000, 0, 60_000] };
        Assert.Throws<ArgumentException>(() => invalid.ScaledBy(0.2f).Validate());
    }

    [Fact]
    public void OtherModesIgnoreTheUnusedPhaseTables()
    {
        foreach (GasSettings settings in new[] { new GasSettings { Pacing = GasPacing.SpeedPaced }, GasTuning.Wave4Legacy })
        {
            var withUnusedTable = settings with { HoldDurationsMs = [7], AdvanceDurationsMs = [0] };
            Assert.Equal(Times(GasSchedule.Create(settings, 5)),
                Times(GasSchedule.Create(withUnusedTable, 5)));
        }
    }

    [Fact]
    public void ATableCannotExceedTheConfiguredLeadingEdgeSpeedLimit()
    {
        GasSettings settings = Table() with { AdvanceDurationsMs = [10_000, 120_000, 60_000] };
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(settings.Validate);
        Assert.Equal(nameof(GasSettings.CentreDriftFraction), error.ParamName);
        Assert.Contains(nameof(GasSettings.AdvanceDurationsMs), error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryPhaseRequiresExactlyOneHoldAndAdvance(bool invalidHold)
    {
        GasSettings settings = invalidHold
            ? Table() with { HoldDurationsMs = [240_000, 90_000] }
            : Table() with { AdvanceDurationsMs = [180_000, 120_000] };
        ArgumentException error = Assert.Throws<ArgumentException>(settings.Validate);
        Assert.Equal(invalidHold ? nameof(GasSettings.HoldDurationsMs) : nameof(GasSettings.AdvanceDurationsMs),
            error.ParamName);
    }

    [Fact]
    public void ZeroAdvanceAndOverflowingWindowAreRejected()
    {
        var zero = Table() with { AdvanceDurationsMs = [180_000, 0, 60_000] };
        Assert.Throws<ArgumentException>(zero.Validate);

        var overflow = Table() with { HoldDurationsMs = [uint.MaxValue, 90_000, 0] };
        Assert.Throws<ArgumentException>(overflow.Validate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NullTablesAreRejectedWithTheRelevantPropertyName(bool invalidHold)
    {
        GasSettings settings = invalidHold
            ? Table() with { HoldDurationsMs = null! }
            : Table() with { AdvanceDurationsMs = null! };
        ArgumentException error = Assert.Throws<ArgumentException>(settings.Validate);
        Assert.Equal(invalidHold ? nameof(GasSettings.HoldDurationsMs) : nameof(GasSettings.AdvanceDurationsMs),
            error.ParamName);
    }

    [Fact]
    public void UnknownPacingIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => (Table() with { Pacing = (GasPacing)99 }).Validate());
    }

    private static (long Reveal, long Move, long Close)[] Times(GasSchedule schedule) =>
        schedule.Phases.Select(phase => (phase.RevealAtMs, phase.ShrinkStartAtMs, phase.ClosedAtMs)).ToArray();
}
