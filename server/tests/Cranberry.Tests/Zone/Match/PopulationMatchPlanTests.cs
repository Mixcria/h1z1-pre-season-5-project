using System.Numerics;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Match;
using Xunit.Abstractions;

namespace Cranberry.Tests.Zone.MatchDrop;

public sealed class PopulationMatchPlanTests(ITestOutputHelper output)
{
    private static DropParticipant[] Roster(int count, int size) => Enumerable.Range(1, count)
        .Select(i => new DropParticipant((ulong)i, (ulong)((i - 1) / size + 1))).ToArray();

    private static PopulationMatchPlan Plan(int count, int size, ulong seed, GasSettings? gas = null, bool enabled = true) =>
        PopulationMatchPlan.Create(DropFixture.Places, DropOptions.Default, gas ?? new(), enabled, seed, Roster(count, size), size);

    [Theory]
    [InlineData(1, 1, 300f)] [InlineData(2, 1, 300f)] [InlineData(10, 1, 625f)]
    [InlineData(20, 1, 900f)] [InlineData(50, 1, 1400f)] [InlineData(99, 1, 2000f)]
    [InlineData(10, 2, 625f)] [InlineData(10, 5, 625f)]
    public void CompactSpawnsStayInsideTheVisibleOpeningAndNestedGas(int count, int size, float radius)
    {
        var locations = new HashSet<(bool, bool)>();
        int reduced = 0;
        for (ulong seed = 1; seed <= 64; seed++)
        {
            var plan = Plan(count, size, seed);
            var schedule = plan.Schedule;
            Assert.True(plan.Dynamic);
            Assert.Equal(radius, schedule.Phase(1).Target.Radius);
            Assert.Equal(radius * 1.6f, schedule.InitialCircle.Radius);
            Assert.Equal(15000, schedule.Phase(1).RevealAtMs);
            Assert.Equal(135000, schedule.Phase(1).ShrinkStartAtMs);
            Assert.Equal(225000, schedule.Phase(1).ClosedAtMs);
            Assert.Equal(0, schedule.RingLiveFromMs);
            Assert.Equal(count, plan.Spawns.Players.Values.Select(p => p.Position).Distinct().Count());
            reduced += plan.Spawns.ReducedSpacingPlacements;
            Assert.All(plan.Spawns.Players.Values, p =>
            {
                Assert.True(schedule.InitialCircle.HorizontalDistanceTo(p.Position) + 75 <= schedule.InitialCircle.Radius + 0.1f);
                Assert.Equal(850f, p.AirPosition.Y);
                Assert.True(p.MarkersWithinLootRadius > 0);
            });
            if (count == 10 && size == 1)
                Assert.All(plan.Spawns.Players.Values, p => Assert.InRange(plan.Spawns.Players.Values
                    .Where(q => q != p).Min(q => MathF.Sqrt(PopulationDropPlanner.DistanceSquared(p.Position, q.Position))), 260f, 1000f));
            GasCircle previous = schedule.InitialCircle;
            foreach (GasPhase phase in schedule.Phases)
            {
                Assert.True(previous.HorizontalDistanceTo(phase.Target.Centre) + phase.Target.Radius <= previous.Radius + 0.01f);
                Assert.True(phase.Target.Radius < previous.Radius);
                Assert.True(phase.ClosedAtMs > phase.ShrinkStartAtMs);
                previous = phase.Target;
            }
            locations.Add((schedule.InitialCircle.Centre.X > 0, schedule.InitialCircle.Centre.Z > 0));
        }
        output.WriteLine($"{count}/{size}: reduced spacing {reduced}; map quadrants {locations.Count}");
        Assert.Equal(0, reduced);
        Assert.Equal(4, locations.Count);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(5)]
    public void FullRosterUsesTheCentralSkySpawnFootprintAndNormalGas(int size)
    {
        for (ulong seed = 1; seed <= 16; seed++)
        {
            var plan = Plan(150, size, seed);
            Assert.False(plan.Dynamic);
            Assert.Equal(GasSchedule.Create(new(), MatchSeeds.For(seed, MatchSeeds.GasSalt)).Phases, plan.Schedule.Phases);
            var points = plan.Spawns.Players.Values.Select(p => p.Position).ToArray();
            Assert.Equal(150, points.Distinct().Count());
            Assert.All(points, p => { Assert.InRange(p.X, -2000, 2000); Assert.InRange(p.Z, -2000, 2000); });
            Assert.True(points.Max(p => p.X) - points.Min(p => p.X) > 3000);
            Assert.True(points.Max(p => p.Z) - points.Min(p => p.Z) > 3000);
            Assert.Equal(4, points.Select(p => (p.X > 0, p.Z > 0)).Distinct().Count());
            Assert.Equal(0, plan.Spawns.ReducedSpacingPlacements);
        }
    }

    [Fact]
    public void ReplayAndRosterChangesCannotMoveAnExistingMatch()
    {
        var first = Plan(10, 1, 42);
        var replay = PopulationMatchPlan.Create(DropFixture.Places, DropOptions.Default, new(), true, 42, Roster(10, 1).Reverse(), 1);
        Assert.Equal(first.Schedule.Phases, replay.Schedule.Phases);
        Assert.All(first.Spawns.Players, pair => Assert.Equal(pair.Value, replay.Spawns.Players[pair.Key]));
        var original = first.Spawns.Players.ToDictionary();
        first.Spawns.Assign(Roster(8, 1), 1);
        first.Spawns.Assign(Roster(11, 1), 1);
        Assert.All(original, pair => Assert.Equal(pair.Value, first.Spawns.Players[pair.Key]));
        Assert.True(first.Schedule.InitialCircle.Contains(first.Spawns.Players[11].Position));
        Assert.Equal(10, first.Population);
    }

    [Fact]
    public void DisabledGasAndExplicitGeometryKeepTheirConfiguredSchedule()
    {
        GasSettings[] settings = [new() { PopulationAdaptive = false }, new() { InitialRadius = 3000 },
            new() { FirstRevealDelayMs = 30000 }, GasTuning.FootRail];
        foreach (GasSettings gas in settings)
        {
            var plan = Plan(10, 1, 42, gas);
            Assert.False(plan.Dynamic);
            Assert.Same(gas, plan.Schedule.Settings);
        }
        Assert.False(Plan(10, 1, 42, enabled: false).Dynamic);
        GasSettings disabled = GasTuning.FromEnvironment(name => name == "CRANBERRY_GAS_POPULATION_ADAPTIVE" ? "0" : null, out _);
        Assert.False(Plan(10, 1, 42, disabled).Dynamic);
    }

    [Fact]
    public void ClockScalingAndEarlyDamageSurvivePopulationAdaptation()
    {
        var normal = Plan(10, 1, 42).Schedule;
        var scaled = Plan(10, 1, 42, new GasSettings().ScaledBy(0.2f)).Schedule;
        Assert.Equal(normal.InitialCircle, scaled.InitialCircle);
        Assert.Equal(3000, scaled.Phase(1).RevealAtMs);
        Assert.Equal(45000, scaled.Phase(1).ClosedAtMs);
        Assert.Equal(new GasSettings().DamageForPhase(1), scaled.Phase(1).DamagePerTick);
    }

    [Fact]
    public void ARevealedCompactZoneShowsTheMovementCountdownDuringDescent()
    {
        var schedule = Plan(10, 1, 42).Schedule;
        var hud = GasHud.Countdown(schedule.Settings, schedule, 20000, airborne: true);
        Assert.Equal(Cranberry.Zone.Generated.AugustStrings.HudLabels.GasAdvancesIn, hud.LabelId);
        Assert.Equal(115000u, hud.Milliseconds);
    }
}
