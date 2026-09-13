using Cranberry.Protocol;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Generated;

namespace Cranberry.Tests.Zone.Gas;

public sealed class GasRadiusOverrideTests
{
    [Fact]
    public void ReducingTheInitialRadiusAlsoReducesTheRevealedCircles()
    {
        GasSettings settings = GasTuning.FromEnvironment(
            key => key == "CRANBERRY_GAS_INITIAL_RADIUS_M" ? "3000" : null, out string? note);

        Assert.Equal(3000f, settings.InitialRadius);
        AssertRevealedRadiiShrinkWithInitialRadius(settings, new GasSettings());
        Assert.Equal(settings.FinalRadius, settings.RadiusForPhase(settings.PhaseCount));
        Assert.DoesNotContain("refused", note ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RaisingTheFinalRadiusNeverCreatesAnExpandingLastPhase()
    {
        GasSettings settings = GasTuning.FromEnvironment(
            key => key == "CRANBERRY_GAS_FINAL_RADIUS_M" ? "100" : null, out _);

        Assert.Equal(100f, settings.FinalRadius);
        float previous = settings.InitialRadius;
        foreach (float radius in Radii(settings))
        {
            Assert.True(radius < previous);
            previous = radius;
        }

        Assert.Equal(100f, previous);
    }

    [Fact]
    public void AutomaticLadderTracksAllOfItsGeometryInputsThroughRecordCopies()
    {
        var defaults = new GasSettings();
        Assert.Equal(Rulings.Gas.RadiusLadder, defaults.RadiusLadder);
        Assert.Equal(Rulings.Gas.RadiusLadder, Radii(defaults));

        GasSettings smaller = defaults with { InitialRadius = 3000f };
        AssertRevealedRadiiShrinkWithInitialRadius(smaller, defaults);
        Assert.Equal(Radii(defaults), Radii(smaller with { InitialRadius = defaults.InitialRadius }));
        Assert.Equal(Radii(defaults), Radii(defaults.ScaledBy(0.2f)));

        GasSettings differentCount = defaults with { PhaseCount = 8 };
        Assert.Empty(differentCount.RadiusLadder);
        Assert.Equal(Radii(differentCount with { RadiusLadder = [] }), Radii(differentCount));

        GasSettings unrounded = smaller with { RadiusRoundingMetres = 0f };
        Assert.NotEqual(Radii(smaller), Radii(unrounded));
        foreach (float radius in Radii(unrounded))
        {
            Assert.True(float.IsFinite(radius));
        }
    }

    [Fact]
    public void AnExplicitLadderRetainsItsExactRadiiWhenOtherGeometryChanges()
    {
        float[] requested = [1601f, 1001f, 651f, 426f, 301f, 226f, 161f, 121f, 81f, 40f];
        var settings = new GasSettings
        {
            InitialRadius = 3000f,
            RadiusRoundingMetres = 100f,
            RadiusLadder = requested,
        };

        settings.Validate();
        Assert.Equal(requested, Radii(settings));

        // Explicitly requesting even a copy of the preset is different from inheriting it.
        var copiedPreset = new GasSettings
        {
            InitialRadius = 3000f,
            RadiusLadder = Rulings.Gas.RadiusLadder.ToArray(),
        };
        copiedPreset.Validate();
        Assert.Equal(Rulings.Gas.RadiusLadder[0], copiedPreset.RadiusForPhase(1));
    }

    [Fact]
    public void EmptyLadderRemainsAnExplicitGeometricSelection()
    {
        var geometric = new GasSettings { RadiusLadder = [] };
        Assert.Empty((geometric with { InitialRadius = 3000f }).RadiusLadder);
        Assert.Empty((geometric with { InitialRadius = Rulings.Gas.InitialRadius }).RadiusLadder);
    }

    [Fact]
    public void ReducingTheOpeningBoundaryToFourThousandCannotEnlargeTheFirstSafeZone()
    {
        var defaults = new GasSettings();
        var settings = defaults with { InitialRadius = 4000f };
        AssertRevealedRadiiShrinkWithInitialRadius(settings, defaults);
    }

    [Fact]
    public void HalvingTheDistanceBetweenTheBoundsHalvesEachRungsDistanceFromTheFinalRadius()
    {
        var defaults = new GasSettings();
        var settings = defaults with
        {
            InitialRadius = (defaults.InitialRadius + defaults.FinalRadius) / 2f,
            RadiusRoundingMetres = 0f,
        };

        settings.Validate();
        for (int phase = 1; phase <= settings.PhaseCount; phase++)
        {
            Assert.Equal((defaults.RadiusLadder[phase - 1] + defaults.FinalRadius) / 2f,
                settings.RadiusForPhase(phase), 3);
        }
    }

    [Fact]
    public void AnUnchangedRadiusMatchRemainsValid()
    {
        var settings = new GasSettings { InitialRadius = 40f, FinalRadius = 40f };
        settings.Validate();
        Assert.All(Radii(settings), radius => Assert.Equal(40f, radius));
    }

    public static IEnumerable<object[]> InvalidLadders()
    {
        yield return [new[] { 2000f, 40f }];
        yield return [new[] { Rulings.Gas.InitialRadius, 1655f, 1040f, 655f, 410f, 255f, 160f, 100f, 65f, 40f }];
        yield return [new[] { 2635f, 2635f, 1040f, 655f, 410f, 255f, 160f, 100f, 65f, 40f }];
        yield return [new[] { 2635f, 3000f, 1040f, 655f, 410f, 255f, 160f, 100f, 65f, 40f }];
        yield return [new[] { 2635f, float.NaN, 1040f, 655f, 410f, 255f, 160f, 100f, 65f, 40f }];
        yield return [new[] { 2635f, float.PositiveInfinity, 1040f, 655f, 410f, 255f, 160f, 100f, 65f, 40f }];
        yield return [new[] { 2635f, 0f, 1040f, 655f, 410f, 255f, 160f, 100f, 65f, 40f }];
        yield return [new[] { 2635f, -1f, 1040f, 655f, 410f, 255f, 160f, 100f, 65f, 40f }];
        yield return [new[] { 2635f, 1655f, 1040f, 655f, 410f, 255f, 160f, 100f, 65f, 30f }];
    }

    [Theory]
    [MemberData(nameof(InvalidLadders))]
    public void InvalidExplicitLaddersAreRefusedInsteadOfPartiallyApplied(float[] ladder)
    {
        ArgumentException error = Assert.ThrowsAny<ArgumentException>(() =>
            GasSchedule.Create(new GasSettings { RadiusLadder = ladder }, 1));
        Assert.Equal(nameof(GasSettings.RadiusLadder), error.ParamName);
    }

    [Fact]
    public void NullIsNotAcceptedAsAnExplicitLadder()
    {
        Assert.Throws<ArgumentNullException>(() => new GasSettings { RadiusLadder = null! });
    }

    [Theory]
    [InlineData(3000f, 40f, false)]
    [InlineData(4200f, 100f, false)]
    [InlineData(3000f, 100f, false)]
    [InlineData(4000f, 40f, false)]
    [InlineData(3000f, 40f, true)]
    public void ResizedCirclesRemainContainedAndUseTheSameRadiusOnBothPackets(
        float initial, float final, bool explicitLadder)
    {
        var settings = new GasSettings { InitialRadius = initial, FinalRadius = final };
        if (explicitLadder)
        {
            settings = settings with
            {
                RadiusLadder = [1600f, 1000f, 650f, 425f, 300f, 225f, 160f, 120f, 80f, 40f],
            };
        }

        for (ulong seed = 1; seed <= 50; seed++)
        {
            GasSchedule schedule = GasSchedule.Create(settings, seed);
            foreach (GasPhase phase in schedule.Phases)
            {
                Assert.True(phase.Target.Radius < phase.Origin.Radius);
                Assert.True(phase.Origin.Contains(phase.Target));
                Assert.True(phase.ShrinkDurationMs > 0);
                Assert.Equal(settings.RadiusForPhase(phase.Index), phase.Target.Radius);

                using var safeZone = new PacketWriter();
                GasPackets.WriteSafeZone(safeZone, phase.Target);
                Assert.Equal(phase.Target.Radius, BitConverter.ToSingle(safeZone.Written[19..]));

                for (int sample = 0; sample <= 4; sample++)
                {
                    GasCircle active = phase.CircleAt(
                        phase.ShrinkStartAtMs + phase.ShrinkDurationMs * sample / 4);
                    Assert.True(phase.Origin.Contains(active));
                    Assert.True(active.Contains(phase.Target));
                    using var ring = new PacketWriter();
                    GasPackets.WriteRing(ring, settings, active, advancing: true);
                    Assert.Equal(active.Radius, BitConverter.ToSingle(ring.Written[19..]));
                }
            }

            Assert.Equal(final, schedule.FinalCircle.Radius);
        }
    }

    private static float[] Radii(GasSettings settings) =>
        Enumerable.Range(1, settings.PhaseCount).Select(settings.RadiusForPhase).ToArray();

    private static void AssertRevealedRadiiShrinkWithInitialRadius(GasSettings smaller, GasSettings larger)
    {
        Assert.True(smaller.RadiusForPhase(1) < larger.RadiusForPhase(1));
        for (int phase = 1; phase < smaller.PhaseCount; phase++)
        {
            Assert.True(smaller.RadiusForPhase(phase) <= larger.RadiusForPhase(phase),
                $"Phase {phase} grew to {smaller.RadiusForPhase(phase)} m after reducing the initial radius.");
        }
    }
}
