using System.Diagnostics;
using System.Numerics;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Match;
using Xunit.Abstractions;

namespace Cranberry.Tests.Zone.MatchDrop;

public sealed class PopulationDropPlannerTests(ITestOutputHelper output)
{
    private static DropParticipant[] Roster(int count, int size) => Enumerable.Range(0, count)
        .Select(i => new DropParticipant((ulong)i + 1, (ulong)(i / size) + 1)).ToArray();

    private static PopulationDropPlanner Plan(int count, int size, ulong seed, bool gas = true)
    {
        var circle = gas ? GasSchedule.Create(new GasSettings(), MatchSeeds.For(seed, MatchSeeds.GasSalt)).Phase(1).Target : (GasCircle?)null;
        var planner = new PopulationDropPlanner(DropFixture.Places, DropOptions.Default, circle, seed);
        planner.Assign(Roster(count, size), size);
        return planner;
    }

    [Theory]
    [InlineData(2, 1)] [InlineData(10, 1)] [InlineData(50, 1)] [InlineData(150, 1)]
    [InlineData(4, 2)] [InlineData(150, 2)] [InlineData(10, 5)] [InlineData(150, 5)]
    public void ShippedMapKeepsOpponentsReachableAndSquadsSeparate(int count, int size)
    {
        var timer = Stopwatch.StartNew();
        float minimum = float.PositiveInfinity, maximumNearest = 0;
        int expanded = 0, reduced = 0;
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var planner = Plan(count, size, seed);
            expanded += planner.ExpandedPlacements;
            reduced += planner.ReducedSpacingPlacements;
            var roster = Roster(count, size);
            foreach (var player in roster)
            {
                var drop = planner.Players[player.Player];
                Assert.Equal(850f, drop.AirPosition.Y);
                Assert.InRange(MathF.Abs(drop.Position.X), 0, 3896);
                Assert.InRange(MathF.Abs(drop.Position.Z), 0, 3896);
                Assert.True(drop.MarkersWithinLootRadius > 0);
                float nearest = roster.Where(p => p.Team != player.Team)
                    .Min(p => MathF.Sqrt(PopulationDropPlanner.DistanceSquared(drop.Position, planner.Players[p.Player].Position)));
                minimum = Math.Min(minimum, nearest);
                maximumNearest = Math.Max(maximumNearest, nearest);
                float fullness = Math.Clamp((count - 20) / 130f, 0f, 1f);
                float floor = size == 1 ? 260f - 60f * fullness : size == 2 ? 292f - 40f * fullness : 372f;
                Assert.InRange(nearest, floor - 0.1f, 1050f);
                foreach (var mate in roster.Where(p => p.Team == player.Team && p.Player != player.Player))
                {
                    float distance = MathF.Sqrt(PopulationDropPlanner.DistanceSquared(drop.Position, planner.Players[mate.Player].Position));
                    Assert.InRange(distance, 25f, 49f);
                }
            }
        }
        output.WriteLine($"{count}/{size}: min opponent {minimum:F1}m, max nearest {maximumNearest:F1}m, expanded {expanded}, reduced {reduced}, {timer.ElapsedMilliseconds}ms / 12 matches");
        Assert.Equal(0, reduced);
    }

    [Fact]
    public void AFullPopulationCanExpandBeyondTheSmallSafeRingWithoutLosingStablePlacements()
    {
        const ulong seed = 1;
        GasCircle circle = GasSchedule.Create(new GasSettings(), MatchSeeds.For(seed, MatchSeeds.GasSalt))
            .Phase(1).Target;
        Assert.Equal(2000f, circle.Radius);
        var planner = Plan(150, 1, seed);
        var assigned = planner.Players.ToDictionary();

        Assert.Contains(assigned.Values, p => circle.HorizontalDistanceTo(p.Position)
            > circle.Radius * DropOptions.Default.RingFactor);
        Assert.Equal(0, planner.ReducedSpacingPlacements);
        Assert.All(assigned.Values, p =>
        {
            Assert.True(circle.HorizontalDistanceTo(p.Position) <= circle.Radius + 3000f);
            Assert.InRange(MathF.Abs(p.Position.X), 0f, 3896f);
            Assert.InRange(MathF.Abs(p.Position.Z), 0f, 3896f);
            Assert.True(p.MarkersWithinLootRadius >= DropOptions.Default.MinimumMarkers);
        });

        var replay = Plan(150, 1, seed);
        Assert.All(assigned, p => Assert.Equal(p.Value, replay.Players[p.Key]));
        planner.Assign(Roster(150, 1).Reverse(), 1);
        planner.Assign(Roster(150, 1).Skip(10), 1);
        planner.Assign(Roster(151, 1), 1);
        Assert.All(assigned, p => Assert.Equal(p.Value, planner.Players[p.Key]));
        Assert.All(assigned.Values, p => Assert.True(
            PopulationDropPlanner.DistanceSquared(p.Position, planner.Players[151].Position) >= 200f * 200f));
        Assert.Equal(0, planner.ReducedSpacingPlacements);
    }

    [Fact]
    public void FrozenAssignmentsSurviveReorderingDisconnectsAndLateTeammates()
    {
        var planner = Plan(9, 5, 19);
        var before = planner.Players.ToDictionary();
        planner.Assign(Roster(10, 5).Reverse(), 5);
        planner.Assign(Roster(10, 5).Skip(2), 5);
        foreach (var pair in before) Assert.Equal(pair.Value, planner.Players[pair.Key]);
        var replay = Plan(9, 5, 19);
        foreach (var pair in before) Assert.Equal(pair.Value, replay.Players[pair.Key]);
        Assert.InRange(MathF.Sqrt(PopulationDropPlanner.DistanceSquared(planner.Players[10].Position, planner.Players[6].Position)), 25, 49);
        // A departed squad member's seat may be filled without overfilling the canopy slots.
        planner.Assign(Roster(10, 5).Where(p => p.Player != 6).Append(new(11, 2)), 5);
        Assert.Equal(planner.Players[6].Position, planner.Players[11].Position);
        Assert.All(before, pair => Assert.Equal(pair.Value, planner.Players[pair.Key]));
    }

    [Fact]
    public void SmallMatchesRotateAcrossAllMapQuadrantsWithAndWithoutGas()
    {
        foreach (bool gas in new[] { true, false })
        {
            var quadrants = new HashSet<(bool, bool)>();
            var places = new HashSet<string>();
            for (ulong seed = 1; seed <= 60; seed++)
            {
                var planner = Plan(2, 1, seed, gas);
                var a = planner.Players[1].Position;
                var b = planner.Players[2].Position;
                quadrants.Add((a.X > 0, a.Z > 0));
                places.Add(planner.Players[1].Poi.Area);
                Assert.InRange(MathF.Sqrt(PopulationDropPlanner.DistanceSquared(a, b)), 250, 1000);
            }
            Assert.Equal(4, quadrants.Count);
            Assert.True(places.Count >= 20, $"Only {places.Count} locations");
        }
    }

    [Fact]
    public void PopulationBroadensActualFootprint()
    {
        double small = 0, full = 0;
        for (ulong seed = 1; seed <= 12; seed++)
        {
            static float Extent(PopulationDropPlanner p) => p.Players.Values.Max(a => p.Players.Values.Max(b => PopulationDropPlanner.DistanceSquared(a.Position, b.Position)));
            small += Math.Sqrt(Extent(Plan(2, 1, seed)));
            full += Math.Sqrt(Extent(Plan(150, 1, seed)));
        }
        output.WriteLine($"Mean footprint diameter: 2 players {small / 12:F0}m, 150 players {full / 12:F0}m");
        Assert.True(full > small * 4);
    }

    [Fact]
    public void LobbySupports150DistinctMarksAndAvoidsPreviousSpawn()
    {
        var occupied = new List<Vector4>();
        for (ulong i = 1; i <= 150; i++)
        {
            var point = LobbySpawnPlanner.Choose(i, occupied);
            Assert.InRange(point.Y, 505, 508);
            Assert.All(occupied, p => Assert.True(Vector4.Distance(p, point) >= 2.5f));
            occupied.Add(point);
        }
        Assert.Equal(150, occupied.Distinct().Count());
        var previous = occupied[0];
        for (ulong i = 0; i < 100; i++)
        {
            var next = LobbySpawnPlanner.Choose(i, [], previous);
            Assert.NotEqual(previous, next);
            previous = next;
        }
    }
}
