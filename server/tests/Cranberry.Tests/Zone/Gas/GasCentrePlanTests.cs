using System.Numerics;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Generated;
using Xunit.Abstractions;

namespace Cranberry.Tests.Zone.Gas;

/// <summary>
/// Centre-plan containment, reach across the client's nine GasWeightArea volumes,
/// and deterministic replay. These are implementation guarantees, not proof of retail centre selection.
/// </summary>
public sealed class GasCentrePlanTests(ITestOutputHelper output)
{
    private const int HistogramSeeds = 20_000;

    private static GasSettings Settings() => new();

    private static ulong SeedOf(int index) => unchecked((ulong)index * 0x9E37_79B9_7F4A_7C15UL);

    private static float DistanceFrom(Vector3 centre, Vector3 from)
    {
        float dx = centre.X - from.X;
        float dz = centre.Z - from.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    // ------------------------------------------------------------------ 1. the rail

    /// <summary>
    /// The observed 20 m/s first radial advance remains compatible with the configured
    /// 0.96 centre-drift cap and 40 m/s edge ceiling.
    /// </summary>
    [Fact]
    public void TheRailAdmitsTheObservedTwentyMetreRadialAdvance()
    {
        GasSettings settings = Settings();

        Assert.Equal(40.0f, settings.MaxEdgeSpeedMetresPerSecond);
        Assert.Equal(0.96f, settings.CentreDriftFraction);

        double ceiling = settings.LeadingEdgeSpeedCeiling();
        Assert.Equal(39.2d, ceiling, 3);

        // The budget the whole lane spends: f x (InitialRadius - FinalRadius).
        double budget = settings.CentreDriftFraction * (settings.InitialRadius - (double)settings.FinalRadius);
        Assert.InRange(budget, 7_641d, 7_642d);

        // FootRail preserves the historical slower speed pacing and smaller drift cap.
        GasSettings d62 = GasTuning.FootRail;
        d62.Validate();
        Assert.InRange(d62.LeadingEdgeSpeedCeiling(), 4.6d, 4.7d);

        output.WriteLine($"rail {settings.MaxEdgeSpeedMetresPerSecond:0.00} m/s, drift {settings.CentreDriftFraction:0.000}, "
            + $"edge {ceiling:0.000} m/s, budget {budget:0.0} m (D62: {d62.LeadingEdgeSpeedCeiling():0.000} m/s, "
            + $"{d62.CentreDriftFraction * (d62.InitialRadius - (double)d62.FinalRadius):0.0} m)");
    }

    /// <summary>
    /// A centre-drift fraction above one violates containment and is rejected at validation.
    /// </summary>
    [Fact]
    public void ADriftFractionAboveTheRailIsRefused()
    {
        GasSettings tooFar = Settings() with { CentreDriftFraction = 1.01f };
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(tooFar.Validate);
        Assert.Contains(nameof(GasSettings.CentreDriftFraction), error.Message, StringComparison.Ordinal);
    }

    /// <summary>Every shipped preset builds a schedule the rail accepts.</summary>
    [Theory]
    [InlineData("Aug2017Retail")]
    [InlineData("Sprint")]
    [InlineData("Wave4Legacy")]
    [InlineData("FootRail")]
    public void EveryPresetValidates(string name)
    {
        GasSettings settings = GasTuning.FromNameOrDefault(name);
        settings.Validate();
        Assert.NotNull(GasSchedule.Create(settings, 12345));
    }

    // ------------------------------------------------------------------ 2. containment

    /// <summary>
    /// A drift fraction no greater than one keeps each target inside its predecessor.
    /// This checks geometry independently of the time available to reach the next target.
    /// </summary>
    [Fact]
    public void EveryCircleIsStillContainedInItsPredecessor()
    {
        GasSettings settings = Settings();
        for (int index = 1; index <= 500; index++)
        {
            GasSchedule schedule = GasSchedule.Create(settings, SeedOf(index));
            GasCircle previous = schedule.InitialCircle;
            foreach (GasPhase phase in schedule.Phases)
            {
                Assert.True(
                    previous.Contains(phase.Target),
                    $"seed {index}: phase {phase.Index} r={phase.Target.Radius} is not inside "
                    + $"r={previous.Radius} (gap "
                    + $"{previous.HorizontalDistanceTo(phase.Target.Centre) + phase.Target.Radius - previous.Radius:0.###} m)");
                previous = phase.Target;
            }
        }
    }

    /// <summary>
    /// The leading edge is bounded for every phase of every match, not on average — the wave-8
    /// property, re-measured on the walk D277 replaced the cone draw with. Sampled on the wire
    /// cadence rather than analytically, so a mistake in <c>GasPhase.CircleAt</c> would show.
    /// </summary>
    [Fact]
    public void TheDrawnLeadingEdgeNeverExceedsTheRail()
    {
        GasSettings settings = Settings();
        double worst = 0d;
        for (int index = 1; index <= 200; index++)
        {
            GasSchedule schedule = GasSchedule.Create(settings, SeedOf(index));
            foreach (GasPhase phase in schedule.Phases)
            {
                for (long clock = phase.ShrinkStartAtMs; clock < phase.ClosedAtMs; clock += 100)
                {
                    GasCircle now = phase.CircleAt(clock);
                    GasCircle then = phase.CircleAt(Math.Min(clock + 100, phase.ClosedAtMs));
                    double edge = (now.Radius - then.Radius + now.HorizontalDistanceTo(then.Centre))
                        / (Math.Min(clock + 100, phase.ClosedAtMs) - clock) * 1000d;
                    worst = Math.Max(worst, edge);
                }
            }
        }

        output.WriteLine($"worst sampled leading edge over 200 matches: {worst:0.000} m/s");
        Assert.True(worst <= settings.MaxEdgeSpeedMetresPerSecond + 0.01d, $"{worst:0.000} m/s");
    }

    // ------------------------------------------------------------------ 3. the reach histogram

    /// <summary>
    /// <b>The owner's ruling, as a test:</b> <i>"the endgame must be able to land in any of
    /// them"</i>. Over <see cref="HistogramSeeds"/> fixed seeds every one of the client's nine
    /// <c>GasWeightArea</c> volumes is both <i>chosen</i> and actually <i>landed in</i> — the final
    /// circle's centre inside that volume's own box.
    /// <para>
    /// The histogram is written to the test output in the shape the audit used, so a future change
    /// to the weighting can be diffed against it rather than argued about.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryClientVolumeCanHostTheEndgame()
    {
        GasSettings settings = Settings();
        int[] chosen = new int[AugustGasWeightAreas.Count];
        int[] landed = new int[AugustGasWeightAreas.Count];
        var firstRing = new List<float>(HistogramSeeds);
        var finalRing = new List<float>(HistogramSeeds);

        for (int index = 1; index <= HistogramSeeds; index++)
        {
            GasSchedule schedule = GasSchedule.Create(settings, SeedOf(index));
            int area = schedule.DestinationAreaIndex;
            Assert.InRange(area, 0, AugustGasWeightAreas.Count - 1);
            chosen[area]++;

            Vector3 last = schedule.FinalCircle.Centre;
            if (AugustGasWeightAreas.All[area].Contains(last.X, last.Z))
            {
                landed[area]++;
            }

            firstRing.Add(DistanceFrom(schedule.Phase(1).Target.Centre, settings.PlayAreaCentre));
            finalRing.Add(DistanceFrom(last, settings.PlayAreaCentre));
        }

        output.WriteLine($"reach histogram over {HistogramSeeds:N0} seeds "
            + $"(rail {settings.MaxEdgeSpeedMetresPerSecond:0.00} m/s, drift {settings.CentreDriftFraction:0.00}, "
            + $"lead {settings.PlayAreaLeadMetres:0} m, weight ^{settings.PoiWeightExponent:0.##})");
        double totalWeight = AugustGasWeightAreas.All
            .Sum(area => GasWeightAreas.WeightOf(in area, settings.PoiWeightExponent));
        output.WriteLine($"{"volume",-34}{"footprint",12}{"draw",9}{"chosen",9}{"landed in",11}{"land %",9}");
        for (int area = 0; area < AugustGasWeightAreas.Count; area++)
        {
            ref readonly AugustGasWeightArea box = ref AugustGasWeightAreas.All[area];
            output.WriteLine(
                $"{box.Name.Replace("GasWeightArea.", string.Empty, StringComparison.Ordinal),-34}"
                + $"{box.FootprintArea,11:N0}m"
                + $"{100d * GasWeightAreas.WeightOf(in box, settings.PoiWeightExponent) / totalWeight,8:0.00}%"
                + $"{chosen[area],9:N0}{landed[area],11:N0}{100d * landed[area] / Math.Max(1, chosen[area]),8:0.0}%");

            Assert.True(chosen[area] > 0, $"{box.Name} was never chosen in {HistogramSeeds} matches");
            Assert.True(landed[area] > 0, $"{box.Name} never hosted an endgame in {HistogramSeeds} matches");
        }

        firstRing.Sort();
        finalRing.Sort();
        output.WriteLine($"phase-1 centre offset from the play-area centre: min {firstRing[0]:0} p10 "
            + $"{firstRing[HistogramSeeds / 10]:0} median {firstRing[HistogramSeeds / 2]:0} p90 "
            + $"{firstRing[HistogramSeeds * 9 / 10]:0} max {firstRing[^1]:0} m");
        output.WriteLine($"final circle offset: min {finalRing[0]:0} p10 {finalRing[HistogramSeeds / 10]:0} "
            + $"median {finalRing[HistogramSeeds / 2]:0} p90 {finalRing[HistogramSeeds * 9 / 10]:0} "
            + $"max {finalRing[^1]:0} m");

        // G-b, as a bound: D62 held every first ring inside 330 m of one point. The audit's own
        // number for the median was 233 m; anything of that order means the ruling did not land.
        Assert.True(firstRing[HistogramSeeds / 2] > 1_000f,
            $"the median first ring is still only {firstRing[HistogramSeeds / 2]:0} m out");
        Assert.True(finalRing[HistogramSeeds / 2] > 3_000f,
            $"the median endgame is still only {finalRing[HistogramSeeds / 2]:0} m out");
    }

    /// <summary>
    /// The first target has the measured 2,000 m radius for every seed;
    /// the centre plan changes its location.
    /// </summary>
    [Fact]
    public void TheFirstRingRadiusMatchesTheMeasuredPublicValue()
    {
        GasSettings settings = Settings();
        for (int index = 1; index <= 200; index++)
        {
            Assert.Equal(2000f, GasSchedule.Create(settings, SeedOf(index)).Phase(1).Target.Radius);
        }
    }

    /// <summary>
    /// With a reduced opening radius, the play area moves only when the destination
    /// lies beyond the available centre-drift budget.
    /// </summary>
    [Fact]
    public void ThePlayAreaOnlyLeadsWhenContainmentCannotReach()
    {
        GasSettings settings = Settings() with { InitialRadius = 4200f };
        double budget = settings.CentreDriftFraction * (settings.InitialRadius - (double)settings.FinalRadius);
        int led = 0;
        int pinned = 0;

        for (int index = 1; index <= 2_000; index++)
        {
            GasSchedule schedule = GasSchedule.Create(settings, SeedOf(index));
            float lead = DistanceFrom(schedule.InitialCircle.Centre, settings.PlayAreaCentre);
            Assert.InRange(lead, 0f, settings.PlayAreaLeadMetres + 0.01f);

            float reach = MathF.Sqrt(
                ((schedule.Destination.X - settings.PlayAreaCentre.X) * (schedule.Destination.X - settings.PlayAreaCentre.X))
                + ((schedule.Destination.Y - settings.PlayAreaCentre.Z) * (schedule.Destination.Y - settings.PlayAreaCentre.Z)));

            if (reach <= budget)
            {
                Assert.Equal(0f, lead, 3);
                pinned++;
            }
            else
            {
                Assert.True(lead > 0f, $"seed {index}: destination {reach:0} m out but the play area did not move");
                led++;
            }
        }

        output.WriteLine($"of 2,000 matches: {pinned:N0} kept the shipped play area, {led:N0} led toward "
            + $"a destination outside the {budget:0} m containment budget");
        Assert.True(pinned > 0 && led > 0);
    }

    // ------------------------------------------------------------------ 4. determinism

    /// <summary>
    /// <b>Lane 3C's contract.</b> <c>SharedMatchGas</c> gives the second player the first player's
    /// <c>(seed, matchClockMs)</c> and nothing else, on the documented grounds that
    /// <c>GasSchedule.Create</c> is a pure function of (settings, seed) and <c>GasController</c> a
    /// pure function of (schedule, start, now). D277 draws three more random numbers per match, so
    /// this is re-asserted rather than assumed: two controllers seeded alike must produce identical
    /// centres, radii and timings — sampled every second of a whole match.
    /// </summary>
    [Fact]
    public void TwoControllersSeededAlikePlayTheIdenticalMatch()
    {
        GasSettings settings = Settings();
        const ulong seed = 0x1234_5678_9ABC_DEF0UL;
        const long startedAtMs = 4_242_000L;

        var first = new GasController(settings);
        var second = new GasController(settings);
        GasSchedule a = first.Start(startedAtMs, seed);
        GasSchedule b = second.Start(startedAtMs, seed);

        Assert.Equal(a.InitialCircle, b.InitialCircle);
        Assert.Equal(a.DestinationAreaIndex, b.DestinationAreaIndex);
        Assert.Equal(a.Destination, b.Destination);
        Assert.Equal(a.RingLiveFromMs, b.RingLiveFromMs);
        Assert.Equal(a.FinishedAtMs, b.FinishedAtMs);
        Assert.Equal(a.Phases.Count, b.Phases.Count);

        for (int index = 0; index < a.Phases.Count; index++)
        {
            Assert.Equal(a.Phases[index], b.Phases[index]);
        }

        for (long now = startedAtMs; now <= startedAtMs + a.FinishedAtMs + 5_000; now += 1_000)
        {
            Assert.Equal(first.ActiveCircleAt(now), second.ActiveCircleAt(now));
        }
    }

    /// <summary>
    /// The other half of the same contract: two <i>different</i> seeds must actually differ, or
    /// "matches differ" is a claim about nothing. Measured as distinct destinations rather than
    /// distinct floats, because that is the thing the owner will see.
    /// </summary>
    [Fact]
    public void DifferentSeedsPlayDifferentMatches()
    {
        GasSettings settings = Settings();
        var destinations = new HashSet<(float X, float Z)>();
        var volumes = new HashSet<int>();
        for (int index = 1; index <= 500; index++)
        {
            GasSchedule schedule = GasSchedule.Create(settings, SeedOf(index));
            destinations.Add((schedule.FinalCircle.Centre.X, schedule.FinalCircle.Centre.Z));
            volumes.Add(schedule.DestinationAreaIndex);
        }

        Assert.Equal(500, destinations.Count);
        Assert.Equal(AugustGasWeightAreas.Count, volumes.Count);
    }

    /// <summary>
    /// The A/B partner still works: <c>CRANBERRY_GAS_CENTRE_PLAN=Drift</c> with D62's rail and cap
    /// reproduces the pre-D276 behaviour the audit measured — every first ring inside 330 m of one
    /// fixed point, and a play area that never moves.
    /// </summary>
    [Fact]
    public void TheFootRailPresetReproducesTheOldCentres()
    {
        GasSettings settings = GasTuning.FromNameOrDefault("FootRail");
        Assert.Equal(GasCentrePlan.Drift, settings.CentrePlan);

        float worstFirst = 0f;
        for (int index = 1; index <= 2_000; index++)
        {
            GasSchedule schedule = GasSchedule.Create(settings, SeedOf(index));
            Assert.Equal(settings.PlayAreaCentre, schedule.InitialCircle.Centre);
            Assert.Equal(-1, schedule.DestinationAreaIndex);
            worstFirst = MathF.Max(worstFirst, DistanceFrom(schedule.Phase(1).Target.Centre, settings.PlayAreaCentre));
        }

        output.WriteLine($"FootRail: worst first-ring offset over 2,000 matches {worstFirst:0} m");
        Assert.InRange(worstFirst, 300f, 331f);
    }

    // ------------------------------------------------------------------ 5. the client's own table

    /// <summary>
    /// The generated table is the client's, and the nine names are the nine the audit found. A
    /// re-extraction that lost or renamed a volume would silently change where every match ends.
    /// </summary>
    [Fact]
    public void TheNineClientVolumesAreTheOnesTheClientShips()
    {
        Assert.Equal(9, AugustGasWeightAreas.Count);
        Assert.Equal(
            [
                "GasWeightArea.ChangsWildCampgrounds.01",
                "GasWeightArea.DeSotosServiceStop.01",
                "GasWeightArea.DoubleHFarms.05",
                "GasWeightArea.FlockOfTheShepherdChurch.01",
                "GasWeightArea.JayWildernessCamp.01",
                "GasWeightArea.JayWildernessCamp.02",
                "GasWeightArea.SchadeWoodsLoggingTrail.01",
                "GasWeightArea.ScottsField.01",
                "GasWeightArea.WestPeaksRanch",
            ],
            AugustGasWeightAreas.All.Select(area => area.Name).ToArray());

        foreach (AugustGasWeightArea area in AugustGasWeightAreas.All)
        {
            Assert.True(area.Width > 0f && area.Depth > 0f, area.Name);
            Assert.True(area.Contains(area.Centre.X, area.Centre.Y), area.Name);
        }

        // The weighting rule, and the one that decides how often each is drawn.
        Assert.Equal(
            AugustGasWeightAreas.TotalFootprintArea,
            AugustGasWeightAreas.All.Sum(area => area.FootprintArea),
            1);
        Assert.Equal(
            137_900f,
            AugustGasWeightAreas.SchadeWoodsLoggingTrail01.FootprintArea,
            0);

        // D277 ships weight = sqrt(footprint), so the biggest volume is 4.5x the smallest rather
        // than 20x. The ratio is what the histogram's "chosen" column measures.
        Assert.Equal(
            4.5d,
            Math.Sqrt(AugustGasWeightAreas.SchadeWoodsLoggingTrail01.FootprintArea)
                / Math.Sqrt(AugustGasWeightAreas.ChangsWildCampgrounds01.FootprintArea),
            0.05d);
    }

    /// <summary>
    /// <see cref="GasSettings.PoiWeightExponent"/> = 0 is the flat draw, and it is the switch that
    /// says the weighting is a ruling rather than a fact.
    /// </summary>
    [Fact]
    public void AZeroExponentMakesTheNineEquallyLikely()
    {
        GasSettings flat = Settings() with { PoiWeightExponent = 0f };
        int[] chosen = new int[AugustGasWeightAreas.Count];
        for (int index = 1; index <= 9_000; index++)
        {
            chosen[GasSchedule.Create(flat, SeedOf(index)).DestinationAreaIndex]++;
        }

        foreach (int count in chosen)
        {
            Assert.InRange(count, 850, 1_150);
        }
    }
}
