using System.Numerics;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchDrop;

/// <summary>
/// Where a match drops (docs/48 §5). The three properties the brief asks for are one test each —
/// determinism, in-bounds, and distribution across matches — and the rest pin the failure ladder,
/// which is the part a play-test will never exercise.
/// </summary>
public sealed class DropPlannerTests
{
    /// <summary>Matches simulated by <see cref="TheDropIsSpreadOverTheWholeMap"/>. Seeds 1..N, so the result is fixed, not sampled.</summary>
    private const int SimulatedMatches = 10_000;

    private static GasCircle FirstCircle(ulong matchSeed, GasSettings? settings = null) =>
        GasSchedule.Create(settings ?? new GasSettings(), MatchSeeds.For(matchSeed, MatchSeeds.GasSalt))
            .Phase(1)
            .Target;

    private static DropPlan Plan(ulong matchSeed, DropOptions? options = null) =>
        Assert.IsType<DropPlan>(DropPlanner.Plan(
            DropFixture.Places,
            FirstCircle(matchSeed),
            options ?? DropOptions.Default,
            matchSeed));

    // ---------------------------------------------------------------- determinism

    [Fact]
    public void TheSameSeedPlansTheSameDropTwice()
    {
        for (ulong seed = 1; seed <= 25; seed++)
        {
            Assert.Equal(Plan(seed), Plan(seed));
        }
    }

    [Fact]
    public void TheDropSeedIsASaltedSubStreamOfTheMatchSeed()
    {
        DropPlan plan = Plan(7);

        Assert.Equal(7UL, plan.MatchSeed);
        Assert.Equal(MatchSeeds.For(7, MatchSeeds.DropSalt), plan.DropSeed);
        Assert.NotEqual(MatchSeeds.For(7, MatchSeeds.GasSalt), plan.DropSeed);
    }

    /// <summary>
    /// Ten frozen matches. This is the test that catches a silent behaviour change — a reordered
    /// draw, a different weighting, a changed anchor — that every property test above would still
    /// pass. Re-running the host with <c>CRANBERRY_MATCH_SEED</c> set to one of these must produce
    /// the same <c>match: drop</c> line.
    /// </summary>
    /// <remarks>
    /// <b>Re-baselined in wave 5 (docs/53), not relaxed.</b> Every row below was re-measured, and
    /// every row moved: the gas lane changed <c>GasSettings.PhaseCount</c> 5 -> 10 and phase 1's
    /// radius 1,810.3 -> 3,635 m, and this planner draws inside <c>schedule.Phase(1).Target</c>, so
    /// a different circle makes a different set of places eligible from the same seed. The four
    /// invariants below (all 92 reachable, in-circle, loot underneath, spread) were re-measured over
    /// 10,000 matches and are what actually guards this lane; these ten rows only catch a silent
    /// change of the draw itself.
    /// <para>
    /// <b>Re-baselined a second time by the wave-5 verify pass, again not relaxed.</b> Six of the
    /// ten rows moved. Wave 5 absorbed the widened phase-1 circle by loosening
    /// <see cref="TheDropIsSpreadOverTheWholeMap"/>'s Pleasant Valley cap 0.15 -> 0.20 and turning
    /// <see cref="ConsecutiveMatchesDropSomewhereElse"/> from a hard property into a rate tolerance.
    /// This pass reversed both by fixing production instead: <c>DropOptions.PoiWeightExponent</c>
    /// 0.5 -> 0.3, re-measured over the same 10,000 seeds (PV* 17.70 % -> 14.54 %, busiest single
    /// place 4.77 % -> 3.25 %, zero repeating three-match windows again). A different weighting is
    /// exactly the "silent behaviour change" these ten rows exist to catch, so they were re-cut, and
    /// seeds 1, 3, 6 and 7 are unchanged, which is the sanity check that only the weighting moved.
    /// </para>
    /// <para>
    /// <b>Re-baselined a third time by the wave-8 gas lane (docs/77), and again not relaxed.</b>
    /// Eight of the ten rows moved because the circle this planner draws inside moved: the play
    /// area came in to 4,400 m at (-250, 0, 100), phase 1 with it (3,635 -> 2,750 m), the centre
    /// walk was capped to bound the gas wall's leading edge, and
    /// <see cref="DropOptions.RingFactor"/> went 1.0 -> 1.5 to keep all 92 places reachable through
    /// the smaller circle. Seeds 2 and 3 are unchanged. The four invariants below still hold, and
    /// the distribution they measure is better than it has ever been: the busiest single place is
    /// 2.58 % of 10,000 matches (was 3.25 %) and PV* is 9.86 % (was 14.54 %).
    /// </para>
    /// <para>
    /// <b>Re-baselined a fourth time by the gas lane's D276-D278 (docs/118), and again not
    /// relaxed.</b> The owner ruled on 2026-09-03 that matches must be able to end anywhere on the
    /// map, so phase 1's centre is no longer drawn inside a 330 m stub around the play-area centre:
    /// it is walked toward one of the nine <c>GasWeightArea</c> volumes the client itself ships, a
    /// median of 2,406 m out. This planner draws inside <c>schedule.Phase(1).Target</c>, so nine of
    /// the ten rows moved; seed 3 is unchanged, which is the sanity check that only the circle
    /// moved. <b>The four invariants below were re-measured over the same 10,000 seeds and every
    /// one of them still holds without being touched</b>: all 92 places occur, the busiest is
    /// 3.37 % (was 2.58 %), PV* is 13.75 % against the 15 % bound (was 9.86 %), the least eligible
    /// match still offers 53 places, and zero of the first 200 three-match windows repeat.
    /// </para>
    /// <para>
    /// The last of those was the one at risk, and it was fixed in production rather than by moving
    /// the number this test's own comment says not to move: at <c>GasSettings.PoiWeightExponent</c>
    /// 1.0 exactly one window in 200 repeated, because two matches aimed at the same volume have
    /// nearly the same first circle and one volume took 40 % of all matches. The gas lane shipped
    /// 0.5 instead (docs/118 §3.4), which is still the client's own footprints driving the draw.
    /// </para>
    /// <para>
    /// <b>Re-baselined a fifth time by the gas lane's D285 (docs/118), and again not relaxed.</b>
    /// D285 adopts the owner's own Z1 radius ladder, so phase 1's radius comes in from the geometric
    /// 2,750 m to Z1's 2,635 m and its centre walks a little further (the walk step is proportional
    /// to the phase's radius drop, which grew). This planner draws inside
    /// <c>schedule.Phase(1).Target</c>, so seeds 2, 4 and 10 moved to a different eligible place;
    /// the other seven are unchanged, which is the sanity check that only the circle moved.
    /// </para>
    /// <para>
    /// Re-measured for the September 6 retail gas correction: the opening boundary is 8,000 m
    /// and the first safe target is 2,000 m. Both the eligible places and the POI-directed circle
    /// centres change. The drop weighting exponent is now 0.25 to preserve the distribution
    /// guards below: all 92 places occur, the busiest takes 4.49 %, Pleasant Valley takes 5.98 %,
    /// the least eligible match offers 21 places, and no first-200 three-match window repeats.
    /// The drop envelope remains 1.5 times the first target radius. Seed 3 remains unchanged.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(1UL, "BumjickFarms", -2565.60f, -10.08f, -178.35f)]
    [InlineData(2UL, "DiamondsServiceYard", 3271.21f, -10.75f, -1557.29f)]
    [InlineData(3UL, "TheVillas", 521.94f, 4.25f, 1142.40f)]
    [InlineData(4UL, "RanchitoResidential", 1987.66f, 15.50f, 1661.77f)]
    [InlineData(5UL, "SchadeWoodsCamp", 2281.60f, 73.88f, -3186.99f)]
    [InlineData(6UL, "FlockOfTheShepherdChurch", -209.23f, 71.23f, 3275.53f)]
    [InlineData(7UL, "CranberryResidential", -2259.46f, -30.75f, -213.00f)]
    [InlineData(8UL, "ValleyViewMall", 1083.95f, 8.14f, 410.75f)]
    [InlineData(9UL, "SchadeWoodsLoggingTrail", 2585.49f, 14.92f, -2693.34f)]
    [InlineData(10UL, "EverlivingHopeChapel", -658.06f, 44.90f, -1914.38f)]
    public void FrozenMatches(ulong seed, string area, float x, float y, float z)
    {
        DropPlan plan = Plan(seed);

        Assert.Equal(area, plan.Poi.Area);
        Assert.Equal(x, plan.Position.X, 0.01f);
        Assert.Equal(y, plan.Position.Y, 0.01f);
        Assert.Equal(z, plan.Position.Z, 0.01f);
        Assert.Equal(850f, plan.AirPosition.Y);
        Assert.Equal(DropSelection.InsideFirstCircle, plan.Selection);
    }

    // ---------------------------------------------------------------- in bounds

    /// <summary>
    /// The defect this lane exists to fix, stated as an invariant. In
    /// <c>logs/host-20260829-220829.log</c> the fixed drop put the player 4,316 m from a first
    /// circle of radius 1,810.3 — outside the first safe zone before the match had started.
    /// </summary>
    [Fact]
    public void EveryDropIsInsideTheFirstSafeCircleAndOnTheMap()
    {
        float half = DropFixture.Places.MapHalfExtentMetres;

        for (ulong seed = 1; seed <= 2_000; seed++)
        {
            GasCircle first = FirstCircle(seed);
            DropPlan plan = Assert.IsType<DropPlan>(
                DropPlanner.Plan(DropFixture.Places, first, DropOptions.Default, seed));

            Assert.Equal(DropSelection.InsideFirstCircle, plan.Selection);
            Assert.Equal(0, plan.WidenSteps);
            Assert.True(
                first.HorizontalDistanceTo(plan.Poi.Anchor) <= first.Radius * DropOptions.Default.RingFactor,
                $"seed {seed}: {plan.Poi.Area} is outside the first circle");

            Assert.True(MathF.Abs(plan.Position.X) <= half, $"seed {seed}: X {plan.Position.X}");
            Assert.True(MathF.Abs(plan.Position.Z) <= half, $"seed {seed}: Z {plan.Position.Z}");
            Assert.True(plan.Poi.EdgeMetres >= DropOptions.Default.MinimumEdgeMetres, plan.Poi.Area);
            Assert.InRange(plan.EligibleRank, 1, plan.EligibleCount);
        }
    }

    /// <summary>
    /// Landing with something to pick up is the point of choosing anchors at all: the jitter draw
    /// is only accepted when the marker it lands on clears <see cref="DropOptions.MinimumMarkers"/>,
    /// and the anchor fallback always does.
    /// </summary>
    [Fact]
    public void EveryDropHasLootUnderIt()
    {
        for (ulong seed = 1; seed <= 2_000; seed++)
        {
            DropPlan plan = Plan(seed);

            Assert.True(
                plan.MarkersWithinLootRadius >= DropOptions.Default.MinimumMarkers,
                $"seed {seed}: {plan.Poi.Area} has {plan.MarkersWithinLootRadius} markers under it");
            Assert.Equal(
                plan.MarkersWithinLootRadius,
                DropFixture.Places.CountWithin(plan.Position, DropOptions.Default.LootRadius));
        }
    }

    /// <summary>
    /// The air spawn is the client's own <c>KotK.SkySpawn</c> slab centre — an absolute 850 m, not
    /// an offset above the ground (docs/48 §2a) — so the fall is 850 m minus wherever the player
    /// lands, and the 400 m clearance floor never binds on Z2. §2a quotes 608–892 m for the 92
    /// *anchors*; the drawn point is any of the place's markers, whose heights spread a little
    /// wider (a rooftop marker at 261 m gives 589 m), so the assertion here is the rule, not the
    /// anchor range. Every drop is still far shorter than the unsourced 1,500 m it replaces.
    /// </summary>
    [Fact]
    public void TheAirSpawnIsTheClientsOwnSkySpawnAltitude()
    {
        for (ulong seed = 1; seed <= 500; seed++)
        {
            DropPlan plan = Plan(seed);

            Assert.Equal(plan.Position.X, plan.AirPosition.X);
            Assert.Equal(plan.Position.Z, plan.AirPosition.Z);
            Assert.Equal(DropOptions.Default.SkySpawnAltitude, plan.AirPosition.Y);
            Assert.Equal(
                MathF.Max(
                    DropOptions.Default.SkySpawnAltitude - plan.Position.Y,
                    DropOptions.Default.MinimumClearanceMetres),
                plan.DescentMetres,
                0.01f);
            Assert.InRange(plan.DescentMetres, DropOptions.Default.MinimumClearanceMetres, 900f);
            Assert.True(plan.DescentMetres < 1_500f, $"seed {seed}: {plan.DescentMetres} m");
        }
    }

    [Fact]
    public void TheClearanceFloorBindsOnlyWhenTheGroundIsHigherThanTheSlab()
    {
        var options = DropOptions.Default with { MinimumClearanceMetres = 2_000f };
        DropPlan plan = Plan(1, options);

        Assert.Equal(plan.Position.Y + 2_000f, plan.AirPosition.Y);
    }

    // ---------------------------------------------------------------- distribution

    /// <summary>
    /// The owner's complaint, measured. Before D39 one place took 100 % of matches; over
    /// <see cref="SimulatedMatches"/> deterministic matches every one of the 92 places occurs and no
    /// single place takes more than 5 %.
    /// <para>
    /// <b>Wave 5 moved the Pleasant Valley bound, and that is a real change worth reading (docs/53
    /// §Integration).</b> The gas lane widened phase 1 from 1,810 m to 3,635 m inside the same
    /// 6,000 m play area, so the first circle now covers 60 % of the play radius instead of 30 %.
    /// A circle that large always contains the middle of the map and only sometimes contains the
    /// edges: measured over 10,000 matches, the six PV* places went from <b>9.05 % to 17.70 %</b>
    /// while the busiest single place barely moved (3.62 % -> 4.77 %) and the eligible set got far
    /// healthier (4 -> 45 places at its worst). PV* is 6 of the 92 names but a large, dense town, so
    /// a centre-weighted first circle favouring it is a property of the new schedule rather than a
    /// defect in the draw. The cap is set at 20 % to hold the measurement, and re-tuning
    /// <see cref="DropOptions.PoiWeightExponent"/> against the wider circle is the drop lane's call,
    /// not the gas lane's.
    /// </para>
    /// </summary>
    [Fact]
    public void TheDropIsSpreadOverTheWholeMap()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int leastEligible = int.MaxValue;

        for (ulong seed = 1; seed <= SimulatedMatches; seed++)
        {
            DropPlan plan = Plan(seed);
            counts[plan.Poi.Area] = counts.GetValueOrDefault(plan.Poi.Area) + 1;
            leastEligible = Math.Min(leastEligible, plan.EligibleCount);
        }

        Assert.Equal(DropFixture.Places.Count, counts.Count);

        int most = counts.Values.Max();
        int pleasantValley = counts
            .Where(pair => pair.Key.StartsWith("PV", StringComparison.Ordinal))
            .Sum(pair => pair.Value);

        Assert.True(most <= SimulatedMatches * 0.05, $"the most frequent place took {most} of {SimulatedMatches}");

        // BACK TO 0.15 (wave-5 verify pass). Wave 5 raised this to 0.20 to absorb a cross-lane
        // consequence of the gas change: widening phase 1 from 1,810 m to 3,635 m took PV* from
        // 9.05 % to 17.70 %, and the guard was moved instead of the draw. Re-tuning
        // DropOptions.PoiWeightExponent 0.5 -> 0.3 against the new circle puts it at 14.54 %, so the
        // original bound holds again — and the busiest single place falls 4.77 % -> 3.25 %, which
        // more than doubles the headroom on the 5 % assertion above.
        Assert.True(
            pleasantValley <= SimulatedMatches * 0.15,
            $"Pleasant Valley took {pleasantValley} of {SimulatedMatches}");

        // The widening ladder is dead code under the shipped gas settings, and this is the proof.
        Assert.True(leastEligible >= 4, $"one match had only {leastEligible} eligible places");
    }

    [Fact]
    public void ConsecutiveMatchesDropSomewhereElse()
    {
        // What the owner will actually do: play three matches back to back, and not want the same
        // place three times — the acceptance check in docs/48, as an assertion.
        //
        // BACK TO A HARD PROPERTY (wave-5 verify pass). Wave 5 converted this into a rate tolerance
        // ("at most 2 of 200 windows may repeat") to absorb the wider phase-1 circle. With
        // DropOptions.PoiWeightExponent re-tuned 0.5 -> 0.3 against that circle, ZERO of the first
        // 200 windows repeat again, so the assertion the owner signed off on is restored rather than
        // relaxed. The caveat wave 5 raised is still true and still worth keeping in view — a draw
        // that COULD not repeat would not be a random draw, and 25 of 1,000 consecutive PAIRS do
        // repeat — so this is pinned as a measured property of the shipped exponent, not as a law.
        // If a future tuning makes it 1 of 200, re-tune the exponent or take it to the owner; do not
        // move the number.
        for (ulong seed = 1; seed <= 200; seed++)
        {
            string[] places = [Plan(seed).Poi.Area, Plan(seed + 1).Poi.Area, Plan(seed + 2).Poi.Area];
            Assert.True(
                places.Distinct(StringComparer.Ordinal).Count() >= 2,
                $"seeds {seed}..{seed + 2} all dropped in {places[0]}");
        }
    }

    /// <summary>
    /// The exact point moves even when the place repeats — retail scattered players within the
    /// spawn area rather than stacking them on one pixel (docs/48 §3, §5.4).
    /// </summary>
    [Fact]
    public void TheSamePlaceIsNotTheSamePointTwice()
    {
        var visits = new Dictionary<string, int>(StringComparer.Ordinal);
        var points = new Dictionary<string, HashSet<Vector3>>(StringComparer.Ordinal);

        for (ulong seed = 1; seed <= 1_000; seed++)
        {
            DropPlan plan = Plan(seed);
            visits[plan.Poi.Area] = visits.GetValueOrDefault(plan.Poi.Area) + 1;
            if (!points.TryGetValue(plan.Poi.Area, out HashSet<Vector3>? seen))
            {
                seen = [];
                points[plan.Poi.Area] = seen;
            }

            seen.Add(plan.Position);
        }

        // Every place visited more than once must have been landed on in more than one spot.
        int repeated = 0;
        foreach ((string area, int visited) in visits)
        {
            if (visited <= 1)
            {
                continue;
            }

            repeated++;
            Assert.True(points[area].Count > 1, $"{area} was visited {visited} times, always at the same point");
        }

        Assert.True(repeated > 0, "no place repeated, so this test proved nothing");
    }

    // ---------------------------------------------------------------- the ladder

    [Fact]
    public void WithoutARingTheDrawRunsOverTheWholeMap()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (ulong seed = 1; seed <= 200; seed++)
        {
            DropPlan plan = Assert.IsType<DropPlan>(
                DropPlanner.Plan(DropFixture.Places, firstCircle: null, DropOptions.Default, seed));

            Assert.Equal(DropSelection.WholeMap, plan.Selection);
            Assert.Equal(DropFixture.Places.Count, plan.EligibleCount);
            Assert.True(float.IsNaN(plan.DistanceToCircleCentre));
            seen.Add(plan.Poi.Area);
        }

        Assert.True(seen.Count > 40, $"only {seen.Count} distinct places over 200 ringless matches");
    }

    [Fact]
    public void AnEmptyRingWidensAndThenFallsBackToTheNearestPlace()
    {
        // A circle no place can be inside: 1 m at the world origin. Four rungs of x1.5 reach 5.06 m,
        // still empty, so the nearest place to the centre is taken.
        var pinhole = new GasCircle(Vector3.Zero, 1f);
        DropPlan plan = Assert.IsType<DropPlan>(
            DropPlanner.Plan(DropFixture.Places, pinhole, DropOptions.Default, 1));

        Assert.Equal(DropSelection.NearestToCircle, plan.Selection);
        Assert.Equal(1, plan.EligibleCount);

        float nearest = DropFixture.Places.Places.Min(place => pinhole.HorizontalDistanceTo(place.Anchor));
        Assert.Equal(nearest, pinhole.HorizontalDistanceTo(plan.Poi.Anchor), 0.01f);
    }

    [Fact]
    public void AWidenedRingIsReportedAsSuch()
    {
        // Sized so that nothing is inside RingFactor x r but the closest place is inside
        // RingFactor x WidenFactor x r. That closest place is PVCentralPark, whose anchor is
        // ~191 m from the world origin. Written against the option rather than against a literal,
        // because wave 8 moved RingFactor 1.0 -> 1.5 (docs/77 §4.6).
        var origin = new GasCircle(Vector3.Zero, 0f);
        float nearest = DropFixture.Places.Places.Min(place => origin.HorizontalDistanceTo(place.Anchor));
        float radius = nearest / (DropOptions.Default.RingFactor * 1.2f);

        DropPlan plan = Assert.IsType<DropPlan>(DropPlanner.Plan(
            DropFixture.Places,
            new GasCircle(Vector3.Zero, radius),
            DropOptions.Default,
            1));

        Assert.Equal(DropSelection.WidenedCircle, plan.Selection);
        Assert.Equal(1, plan.WidenSteps);
        Assert.Equal(DropOptions.Default.RingFactor * DropOptions.Default.WidenFactor, plan.RingFactorUsed, 0.001f);
    }

    [Fact]
    public void AnImpossibleJitterThresholdFallsBackToTheAnchor()
    {
        var options = DropOptions.Default with { MinimumMarkers = 1_000_000 };
        DropPlan plan = Plan(1, options);

        Assert.True(plan.UsedAnchor);
        Assert.Equal(0, plan.JitterAttemptsUsed);
        Assert.Equal(plan.Poi.Anchor, plan.Position);
        Assert.Equal(plan.Poi.AnchorNeighbours, plan.MarkersWithinLootRadius);
    }

    [Fact]
    public void ZeroJitterAttemptsAlwaysUsesTheAnchor()
    {
        DropPlan plan = Plan(3, DropOptions.Default with { JitterAttempts = 0 });

        Assert.True(plan.UsedAnchor);
        Assert.Equal(plan.Poi.Anchor, plan.Position);
    }

    // ---------------------------------------------------------------- the off switches

    [Fact]
    public void PlanningIsSkippedWhenThereIsNothingToPlanFrom()
    {
        Assert.Null(DropPlanner.Plan(null, FirstCircle(1), DropOptions.Default, 1));
        Assert.Null(DropPlanner.Plan(DropFixture.Places, FirstCircle(1), DropOptions.Default with { Enabled = false }, 1));
    }

    [Fact]
    public void UniformWeightingFlattensTheDrawAndLinearWeightingConcentratesIt()
    {
        static int Top(float exponent)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var options = DropOptions.Default with { PoiWeightExponent = exponent };
            for (ulong seed = 1; seed <= 2_000; seed++)
            {
                DropPlan plan = Assert.IsType<DropPlan>(
                    DropPlanner.Plan(DropFixture.Places, FirstCircle(seed), options, seed));
                counts[plan.Poi.Area] = counts.GetValueOrDefault(plan.Poi.Area) + 1;
            }

            return counts.Values.Max();
        }

        int uniform = Top(0f);
        int root = Top(0.5f);
        int linear = Top(1f);

        Assert.True(uniform < root, $"uniform {uniform} vs root {root}");
        Assert.True(root < linear, $"root {root} vs linear {linear}");
    }

    [Fact]
    public void TheLogLineNamesThePlaceAndCarriesTheSeed()
    {
        DropPlan plan = Plan(1);
        string line = plan.Describe();

        Assert.Contains(plan.Poi.Area, line, StringComparison.Ordinal);
        Assert.Contains(MatchSeeds.Format(plan.MatchSeed), line, StringComparison.Ordinal);
        Assert.Contains("within 80 m", line, StringComparison.Ordinal);
        Assert.Contains("eligible", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void InvalidOptionsAreRejected(float ringFactor) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DropPlanner.Plan(DropFixture.Places, FirstCircle(1), DropOptions.Default with { RingFactor = ringFactor }, 1));
}
