using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Movement;

namespace Cranberry.Tests.Zone.Gas;

/// <summary>
/// Wave 8, lane GAS (docs/77). Three things, all of them owner-facing:
/// <list type="number">
/// <item>the wall a player actually has to outrun is the ring's LEADING EDGE — radius rate plus
/// centre rate — which docs/74 probe S3 measured at 7.48 and 10.91 m/s on the wire against a
/// 6.60 m/s sprint;</item>
/// <item>the HUD says "Gas is spreading!" while the gas is spreading, which is the owner's own
/// report, and the client has had all three label ids all along;</item>
/// <item>nothing gas-shaped is on the map, and nothing burns, before the ring first moves.</item>
/// </list>
/// Nothing here is client-verified (D29): these are unit assertions over the server's own model,
/// and the model's own validation is the two live harness measurements reproduced below.
/// </summary>
public sealed class Wave8GasEdgeTests
{
    /// <summary>
    /// <b>The arithmetic docs/74 probe S3 measured, reproduced from the model.</b> The drawn ring
    /// interpolates its centre and its radius on one <c>t</c>, so its leading edge closes at
    /// <c>v · (1 + d/Δr)</c>. The two matches S3 recorded drew <c>d/Δr</c> = 0.966 and 0.351 at the
    /// pre-wave-8 defaults (v = 5.539, Δr = 2 365 m over a 427 s advance), and the harness measured
    /// centre rates of 5.37 and 1.94 m/s and edges of 10.91 and 7.48 m/s. If this ever stops
    /// matching, the model no longer describes what the server puts on the wire.
    /// </summary>
    [Theory]
    [InlineData(0.966d, 5.352d, 10.891d)]
    [InlineData(0.351d, 1.944d, 7.483d)]
    public void TheLeadingEdgeModelReproducesTheHarnessMeasurement(
        double driftRatio,
        double expectedCentreRate,
        double expectedEdge)
    {
        // Preserve the speed-paced geometric settings wave 5 shipped and probe S3 ran against:
        // a 6 000 m play area and a 3 635 m first safe radius.
        var shipped = new GasSettings
        {
            Pacing = GasPacing.SpeedPaced,
            PlayAreaCentre = Vector3.Zero,
            InitialRadius = 6000f,
            ShrinkSpeedMetresPerSecond = 5.539f,
            CentreDriftFraction = 1f,
            MaxEdgeSpeedMetresPerSecond = 12f,
            RadiusLadder = [],
        };

        double radiusDrop = shipped.InitialRadius - shipped.RadiusForPhase(1);
        double advanceSeconds = shipped.AdvanceMsForPhase(1) / 1000d;
        double radiusRate = radiusDrop / advanceSeconds;
        double centreRate = radiusDrop * driftRatio / advanceSeconds;

        Assert.Equal(2_365d, radiusDrop, 0.5d);
        Assert.Equal(expectedCentreRate, centreRate, 0.02d);
        Assert.Equal(expectedEdge, radiusRate + centreRate, 0.02d);

        // Against a 6.60 m/s sprint, one of those two matches was survivable and the other was not,
        // and which one you got was an invisible dice roll. That is the defect.
        Assert.True(radiusRate + centreRate > MovementProfile.Default.SprintSpeed);
    }

    /// <summary>
    /// Preserve the wave-8 comparison: its foot preset limits the same two draws to at most
    /// 1.20 times the radial speed, below that preset's rail.
    /// </summary>
    [Fact]
    public void TheCapWouldHavePreventedBothMeasuredMatches()
    {
        GasSettings settings = GasTuning.FootRail;
        double radiusDrop = settings.InitialRadius - settings.RadiusForPhase(1);
        double advanceSeconds = settings.AdvanceMsForPhase(1) / 1000d;

        foreach (double driftRatio in new[] { 0.966d, 0.351d })
        {
            double capped = Math.Min(driftRatio, settings.CentreDriftFraction);
            double edge = radiusDrop * (1d + capped) / advanceSeconds;

            Assert.True(
                edge <= settings.MaxEdgeSpeedMetresPerSecond,
                $"drift {driftRatio}: edge {edge:F2} m/s");
        }
    }

    /// <summary>
    /// Not the analytic bound but the measured one, over 500 whole matches: every phase of every
    /// match, both the edge speed (you started moving when the ring did) and the required traverse
    /// (you started when it was revealed) stay under the configured rail. The preserved FootRail
    /// preset additionally stays below a sprint with headroom.
    /// </summary>
    [Fact]
    public void FiveHundredMatchesRespectTheirRailsAndFootRailRemainsOutrunnable()
    {
        var settings = new GasSettings();
        double sprint = MovementProfile.Default.SprintSpeed;
        double worstEdge = 0d;
        double worstTraverse = 0d;

        for (ulong seed = 0; seed < 500; seed++)
        {
            foreach (GasPhase phase in GasSchedule.Create(settings, seed).Phases)
            {
                float travel =
                    Vector3.Distance(phase.Origin.Centre, phase.Target.Centre)
                    + phase.Origin.Radius
                    - phase.Target.Radius;

                worstEdge = Math.Max(worstEdge, travel / (phase.ShrinkDurationMs / 1000d));
                worstTraverse = Math.Max(worstTraverse, travel / ((phase.ClosedAtMs - phase.RevealAtMs) / 1000d));
            }
        }

        Assert.True(worstEdge <= settings.LeadingEdgeSpeedCeiling() + 0.001d, $"edge {worstEdge:F3} m/s");

        // The retail timing reconstruction is checked against its configured leading-edge rail.
        // Keep the historical sprint bound separately on FootRail so its comparison cannot rot.
        Assert.True(
            worstEdge < settings.MaxEdgeSpeedMetresPerSecond,
            $"edge {worstEdge:F3} m/s against the {settings.MaxEdgeSpeedMetresPerSecond:F2} m/s rail");
        Assert.True(
            worstTraverse < settings.MaxEdgeSpeedMetresPerSecond,
            $"traverse {worstTraverse:F3} m/s");

        double footEdge = 0d;
        double footTraverse = 0d;
        for (ulong seed = 0; seed < 500; seed++)
        {
            foreach (GasPhase phase in GasSchedule.Create(GasTuning.FootRail, seed).Phases)
            {
                float travel =
                    Vector3.Distance(phase.Origin.Centre, phase.Target.Centre)
                    + phase.Origin.Radius
                    - phase.Target.Radius;

                footEdge = Math.Max(footEdge, travel / (phase.ShrinkDurationMs / 1000d));
                footTraverse = Math.Max(footTraverse, travel / ((phase.ClosedAtMs - phase.RevealAtMs) / 1000d));
            }
        }

        Assert.True(footEdge < sprint - 0.5d, $"FootRail edge {footEdge:F3} m/s against a {sprint:F2} m/s sprint");
        Assert.True(footTraverse < sprint - 0.5d, $"FootRail traverse {footTraverse:F3} m/s");

        // And it is not a static circle: some match somewhere still walks its ring nearly the whole
        // budget. (The floor matters more than the ceiling here - see TheCircleWalksOneWayAcrossTheMap.)
        Assert.True(worstEdge > settings.ShrinkSpeedMetresPerSecond * 1.15d, "the cap has gone slack");
    }

    /// <summary>
    /// One predicate gates the draw and the burn, in all three positions. If they could come apart,
    /// a player outside the play area would be burned by a circle their client is not drawing —
    /// which is the failure mode the owner's own Z1 file warns about in so many words.
    /// </summary>
    [Theory]
    [InlineData(GasPreMoveRing.None, false)]
    [InlineData(GasPreMoveRing.ZeroRadius, false)]
    [InlineData(GasPreMoveRing.Boundary, true)]
    public void TheDrawGateAndTheDamageGateAreOnePredicate(GasPreMoveRing position, bool liveFromTheStart)
    {
        GasSchedule schedule = GasSchedule.Create(new GasSettings { PreMoveRing = position }, 11);
        long firstMovement = schedule.Phase(1).ShrinkStartAtMs;

        Assert.Equal(liveFromTheStart ? 0L : firstMovement, schedule.RingLiveFromMs);

        foreach (long clock in new[] { 0L, 1_000L, 119_999L, 120_000L, firstMovement - 1, firstMovement, 1_600_000L })
        {
            Assert.Equal(schedule.IsLethalAt(clock), schedule.IsRingVisibleAt(clock));
            Assert.Equal(liveFromTheStart || clock >= firstMovement, schedule.IsLethalAt(clock));
        }
    }

    /// <summary>
    /// The three ids are the client's own, they are consecutive, and the countdown writer puts the
    /// one it is handed on the wire. The owner's report was that the HUD said "Revealing safe zone
    /// in" while the gas was spreading; 14152 is the packet that fixes it.
    /// </summary>
    [Fact]
    public void TheThreeGasLabelsAreTheClientsOwnConsecutiveIds()
    {
        Assert.Equal(14_151u, GameModeHud.GasAdvancesInLabelId);
        Assert.Equal(14_152u, GameModeHud.GasIsSpreadingLabelId);
        Assert.Equal(14_153u, GameModeHud.RevealingSafeZoneLabelId);

        using var writer = new PacketWriter();
        GameModeHud.WriteCountdown(writer, 61_000, GameModeHud.GasIsSpreadingLabelId);
        byte[] bytes = writer.Written.ToArray();

        Assert.Equal(
            Convert.FromHexString("CE0F00" + "00000000" + "48EE0000" + "48370000" + "00000000"),
            bytes);
    }

    /// <summary>
    /// The whole match is still a pure function of (settings, seed) — the per-match drift heading is
    /// drawn from the same stream, so a logged seed still replays a match exactly, and two different
    /// seeds still give two different sets of circles.
    /// </summary>
    [Fact]
    public void AMatchIsStillAPureFunctionOfItsSeed()
    {
        var settings = new GasSettings();
        GasSchedule first = GasSchedule.Create(settings, 0xC0FFEE);
        GasSchedule again = GasSchedule.Create(settings, 0xC0FFEE);
        GasSchedule other = GasSchedule.Create(settings, 0xC0FFEF);

        Assert.Equal(
            first.Phases.Select(p => p.Target).ToArray(),
            again.Phases.Select(p => p.Target).ToArray());
        Assert.NotEqual(first.FinalCircle.Centre, other.FinalCircle.Centre);
    }

    /// <summary>
    /// The rail is enforced where an operator can reach it: <c>CRANBERRY_GAS_*</c> can ask for a
    /// combination whose edge is unoutrunnable, and <see cref="GasTuning.FromEnvironment"/> must
    /// report it and fall back to the preset rather than serving it or taking the host down.
    /// </summary>
    [Fact]
    public void AnUnoutrunnableTuningIsRefusedAndReported()
    {
        static string? Read(string variable) => variable switch
        {
            "CRANBERRY_GAS_DRIFT" => "1.0",
            "CRANBERRY_GAS_WALL_SPEED" => "25.0",
            _ => null,
        };

        GasSettings settings = GasTuning.FromEnvironment(Read, out string? note);

        Assert.Equal(new GasSettings().CentreDriftFraction, settings.CentreDriftFraction);
        Assert.NotNull(note);
        Assert.Contains("IGNORED", note, StringComparison.Ordinal);
        Assert.Contains("leading edge", note, StringComparison.Ordinal);
    }
}
