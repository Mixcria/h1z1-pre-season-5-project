using Cranberry.Zone.Gas;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchDrop;

/// <summary>
/// The single call the match-start path makes, and the two ways it is allowed to say no: the
/// feature is switched off, or the shipped placement file could not be read. Neither may throw on
/// the listener thread.
/// </summary>
public sealed class MatchDropChooserTests
{
    private static MatchDropChooser Chooser(DropOptions? options = null) =>
        new(options ?? DropOptions.Default, () => DropFixture.Spawns);

    private static GasSchedule Schedule(ulong matchSeed) =>
        GasSchedule.Create(new GasSettings(), MatchSeeds.For(matchSeed, MatchSeeds.GasSalt));

    [Fact]
    public void ItPlansAgainstPhaseOneOfTheSchedule()
    {
        MatchDropChooser chooser = Chooser();

        Assert.True(chooser.TryPlan(Schedule(1), 1, out DropPlan plan, out string? reason));
        Assert.Null(reason);

        // DropOptions.RingFactor, not a bare radius: wave 8 took it 1.0 -> 1.5 because the gas
        // lane's play area came in to 4,400 m and its centre walk is capped, which left 41 of the
        // 92 named places permanently undroppable at 1.0 (docs/77 §4.6, DropOptions.RingFactor).
        GasCircle first = Schedule(1).Phase(1).Target;
        Assert.True(
            first.HorizontalDistanceTo(plan.Poi.Anchor) <= first.Radius * DropOptions.Default.RingFactor);
        Assert.Equal(plan.RingFactorUsed, DropOptions.Default.RingFactor, 0.001f);
        // The measured 2,000 m first target and 8,000 m opening boundary change the
        // POI-directed circle and its eligible places; see DropPlannerTests.FrozenMatches.
        Assert.Equal("BumjickFarms", plan.Poi.Area);
    }

    [Fact]
    public void WithoutAScheduleItStillPlans()
    {
        Assert.True(Chooser().TryPlan(schedule: null, 1, out DropPlan plan, out _));
        Assert.Equal(DropSelection.WholeMap, plan.Selection);
    }

    [Fact]
    public void DisabledMeansTheCallerKeepsItsFixedSpawn()
    {
        MatchDropChooser chooser = Chooser(DropOptions.Default with { Enabled = false });

        Assert.False(chooser.TryPlan(Schedule(1), 1, out DropPlan plan, out string? reason));
        Assert.Null(plan);
        Assert.Null(reason);
    }

    /// <summary>
    /// A missing or hand-edited placement file is the likeliest failure of all — the loot data is
    /// shipped as loose content — and it must cost the match a warning, not a crash.
    /// </summary>
    [Fact]
    public void AnUnreadableDatasetDegradesWithAReason()
    {
        var chooser = new MatchDropChooser(
            DropOptions.Default,
            () => throw new FileNotFoundException("z2-loot-spawns.bin is missing"));

        Assert.False(chooser.TryPlan(Schedule(1), 1, out DropPlan plan, out string? reason));
        Assert.Null(plan);
        Assert.NotNull(reason);
        Assert.Contains("FileNotFoundException", reason, StringComparison.Ordinal);

        // Tried once, then remembered: a broken file must not be reopened every match.
        Assert.Null(chooser.Places(out string? again));
        Assert.Equal(reason, again);
    }

    [Fact]
    public void ThePlaceListIsBuiltOnceAndShared()
    {
        MatchDropChooser chooser = Chooser();

        Z2DropPois? first = chooser.Places(out _);
        Assert.NotNull(first);
        Assert.Same(first, chooser.Places(out _));
        Assert.Same(DropFixture.Places, first);
    }

    [Fact]
    public void ThePlanIsReplayableFromItsLoggedSeed()
    {
        MatchDropChooser chooser = Chooser();

        Assert.True(chooser.TryPlan(Schedule(9), 9, out DropPlan first, out _));
        Assert.True(MatchSeeds.TryParse(MatchSeeds.Format(first.MatchSeed), out ulong replayed));
        Assert.True(chooser.TryPlan(Schedule(replayed), replayed, out DropPlan second, out _));

        Assert.Equal(first, second);
    }

    [Fact]
    public void BadArgumentsAreRefusedAtConstruction()
    {
        Assert.Throws<ArgumentNullException>(() => new MatchDropChooser(DropOptions.Default, null!));
        Assert.Throws<ArgumentNullException>(() => new MatchDropChooser(null!, () => DropFixture.Spawns));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MatchDropChooser(DropOptions.Default with { LootRadius = 0f }, () => DropFixture.Spawns));
    }

    [Fact]
    public void TheDefaultChooserReadsTheShippedFile()
    {
        MatchDropChooser chooser = MatchDropChooser.Default();
        Z2LootSpawns spawns = DropFixture.Spawns;

        Assert.True(chooser.TryPlan(Schedule(2), 2, out DropPlan plan, out _));
        // Same literal fixture as seed 2 in DropPlannerTests, after the measured gas correction.
        Assert.Equal("DiamondsServiceYard", plan.Poi.Area);
        Assert.Equal(spawns.Count, chooser.Places(out _)!.Spawns.Count);
    }
}
