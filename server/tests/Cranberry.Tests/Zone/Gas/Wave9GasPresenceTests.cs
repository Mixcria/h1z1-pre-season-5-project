using System.Numerics;
using System.Text;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Movement;

namespace Cranberry.Tests.Zone.Gas;

/// <summary>
/// Wave 9, lane GAS (docs/87). The owner played a whole 3:29 match on the wave-8 tree and saw no
/// gas at all. Two halves, and only the second is a defect:
/// <list type="number">
/// <item><b>Scheduling — not a bug.</b> <c>PreMoveRing = None</c> is his own server
/// (<c>RetailGasHiddenUntilMove</c> + <c>NoRingBeforeMove</c>), so nothing is drawn until 4:30 and
/// he left at 3:29. These tests re-assert that the ladder is UNCHANGED.</item>
/// <item><b>Durability — the defect.</b> The <c>ce 0f</c> countdown is the only gas feedback in the
/// first 4:30, it was sent once, and his client reported rebuilding <c>HudGameModeWindow</c> 2.6 s
/// later. So the widget was almost certainly blank for 97 % of the window. The fix is a 1 Hz heal
/// plus the owner's own TextAlert banners.</item>
/// </list>
/// <b>D29:</b> nothing here is client-verified. These are unit assertions over the server's own
/// model; the only live evidence in play is his session log, and that is evidence of a failure.
/// </summary>
public sealed class Wave9GasPresenceTests
{
    private static GasSettings Settings() => new();

    private static GasSchedule Schedule() => GasSchedule.Create(Settings(), 1);

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    /// <summary>
    /// <b>The widget can never be blank.</b> <c>FUN_140bb67b0</c> gates the whole countdown on
    /// <c>labelId &gt; 0</c>, so a zero label is the one value that would hide the thing this wave
    /// exists to keep on screen. Walked at every whole second of the full schedule.
    /// </summary>
    [Fact]
    public void TheCountdownIsNeverBlankAtAnySecondOfTheMatch()
    {
        GasSettings settings = Settings();
        GasSchedule schedule = Schedule();
        uint[] allowed =
        [
            GameModeHud.RevealingSafeZoneLabelId,
            GameModeHud.GasAdvancesInLabelId,
            GameModeHud.GasIsSpreadingLabelId,
        ];

        for (long clock = 0; clock <= schedule.FinishedAtMs + 60_000; clock += 1000)
        {
            (uint label, uint _) = GasHud.Countdown(settings, schedule, clock, airborne: false);
            Assert.Contains(label, allowed);
            Assert.True(label > 0, $"label 0 at {clock} ms would blank the widget");
        }

        // Before the schedule exists (the drop burst runs a few ms ahead of GasSchedule.Create).
        Assert.Equal(GameModeHud.RevealingSafeZoneLabelId, GasHud.Countdown(settings, null, 0, false).LabelId);
    }

    /// <summary>The three labels flip on the two edges the owner's own file describes in prose.</summary>
    [Fact]
    public void TheCountdownFlipsAtTheRevealAndFirstMovement()
    {
        GasSettings settings = Settings();
        GasSchedule schedule = Schedule();

        foreach (long before in (long[])[0, 60_000, 119_000])
        {
            Assert.Equal(
                GameModeHud.RevealingSafeZoneLabelId,
                GasHud.Countdown(settings, schedule, before, false).LabelId);
        }

        foreach (long held in (long[])[120_000, 200_000, 369_000])
        {
            Assert.Equal(
                GameModeHud.GasAdvancesInLabelId,
                GasHud.Countdown(settings, schedule, held, false).LabelId);
        }

        foreach (long moving in (long[])[370_000, 400_000])
        {
            Assert.Equal(
                GameModeHud.GasIsSpreadingLabelId,
                GasHud.Countdown(settings, schedule, moving, false).LabelId);
        }

        (uint parkedLabel, uint parkedMs) = GasHud.Countdown(settings, schedule, schedule.FinishedAtMs + 5_000, false);
        Assert.Equal(GameModeHud.GasIsSpreadingLabelId, parkedLabel);
        Assert.Equal(0u, parkedMs);

        // Z1's airborne rule: under the canopy the label is the reveal, whatever else is true.
        Assert.Equal(
            GameModeHud.RevealingSafeZoneLabelId,
            GasHud.Countdown(settings, schedule, 200_000, airborne: true).LabelId);
    }

    /// <summary>
    /// <b>The owner's original label complaint, pinned.</b> "Gas advances in" counts to the moment
    /// the ring starts MOVING, not to the next reveal: at 2:00 that is 250 000 ms, before the
    /// phase's own close.
    /// </summary>
    [Fact]
    public void TheAdvancesInTimerCountsToTheShrinkNotToTheNextReveal()
    {
        GasSchedule schedule = Schedule();
        (uint label, uint ms) = GasHud.Countdown(Settings(), schedule, 120_000, false);

        Assert.Equal(GameModeHud.GasAdvancesInLabelId, label);
        Assert.Equal(250_000u, ms);
        Assert.Equal(370_000L, schedule.Phase(1).ShrinkStartAtMs);
        Assert.NotEqual((uint)(schedule.Phase(1).ClosedAtMs - 120_000L), ms);
        Assert.Equal(250u, GasAlerts.SecondsOf(ms));
    }

    /// <summary>
    /// <c>ClientUpdate.TextAlert</c> on the wire: <c>11 31 00</c>, a <c>u32</c> UTF-8 byte count,
    /// then the text, and nothing else. Byte-identical registration in 1087 and 1148 (base 17 /
    /// sub 49), and the 1148 dispatcher's <c>case 0x31</c> installs exactly one string.
    /// </summary>
    [Fact]
    public void ATextAlertIsElevenThirtyOnePlusOneString()
    {
        const string message = GasAlerts.MatchBegun;
        byte[] bytes = Bytes(w => GasAlerts.Write(w, message));

        Assert.Equal(0x11, bytes[0]);
        Assert.Equal(0x31, bytes[1]);
        Assert.Equal(0x00, bytes[2]);
        Assert.Equal((uint)Encoding.UTF8.GetByteCount(message), BitConverter.ToUInt32(bytes, 3));
        Assert.Equal(message, Encoding.UTF8.GetString(bytes, 7, bytes.Length - 7));
        Assert.Equal(GasAlerts.LengthOf(message), bytes.Length);
        Assert.Equal(7 + message.Length, bytes.Length);
        Assert.Equal(ZoneOpcodes.ClientUpdateBase, GasAlerts.Family);
        Assert.Equal(0x0031, GasAlerts.SubOpcode);
    }

    /// <summary>
    /// The four sentences are the August client's own locale text, verbatim, and the slot in
    /// <c>BR.SafeZoneAnnounce</c> is expanded server-side because TextAlert carries a string and
    /// not a locale id.
    /// </summary>
    [Fact]
    public void TheBannersAreTheAugustClientsOwnSentences()
    {
        Assert.Equal("The Match has begun!", GasAlerts.MatchBegun);
        Assert.Equal("Proceed to the safe area marked on your map.", GasAlerts.Proceed);
        Assert.Equal("Releasing the toxic gas.", GasAlerts.ReleasingGas);
        Assert.Equal(
            "The safe zone has been marked on your map. Toxic gas will be released in 270 seconds.",
            GasAlerts.SafeZoneMarked(270));
        Assert.Equal(
            "The safe zone has been marked on your map. Toxic gas will be released in 150 seconds.",
            GasAlerts.SafeZoneMarked(150));

        // The drop banner announces the FIRST MOVE, the moment gas exists.
        Assert.Equal(370u, GasAlerts.SecondsOf(Settings().FirstMoveDelayMs));
        Assert.Equal(0u, GasAlerts.SecondsOf(0));
        Assert.Equal(1u, GasAlerts.SecondsOf(1));
        Assert.Equal(150u, GasAlerts.SecondsOf(149_880));
    }

    /// <summary>
    /// The two heal beats, driven across 60 s of real 250 ms pumps: 60 countdowns and 4 circles.
    /// A zero period is off — that is <c>CRANBERRY_GAS_HUD_HEAL_MS=0</c>, the A/B against wave 8's
    /// transition-only behaviour.
    /// </summary>
    [Fact]
    public void TheHealBeatsOncePerSecondAndTheSafeZoneEveryFifteen()
    {
        GasSettings settings = Settings();
        long hud = 0;
        long safeZone = 0;
        long off = 0;
        int hudFires = 0;
        int safeZoneFires = 0;
        int offFires = 0;

        const long Base = 1_000_000L;
        for (long tick = 0; tick < 60_000; tick += settings.HostTickIntervalMs)
        {
            if (GasHud.DueAt(Base + tick, ref hud, settings.HudHealIntervalMs))
            {
                hudFires++;
            }

            if (GasHud.DueAt(Base + tick, ref safeZone, settings.SafeZoneHealIntervalMs))
            {
                safeZoneFires++;
            }

            if (GasHud.DueAt(Base + tick, ref off, 0))
            {
                offFires++;
            }
        }

        Assert.Equal(60, hudFires);
        Assert.Equal(4, safeZoneFires);
        Assert.Equal(0, offFires);
        Assert.Equal(1000u, settings.HudHealIntervalMs);
        Assert.Equal(15_000u, settings.SafeZoneHealIntervalMs);
        Assert.True(settings.SendBanners, "the banners must be on by default — a switch is for reverting");
    }

    /// <summary>
    /// <b>The escape margin at the movement lane's 5.74 m/s sprint</b> (docs/87 §6). The drawn ring
    /// interpolates centre and radius on one <c>t</c>, so its leading edge closes at
    /// <c>Δr·(1 + f)/advance</c>. Worst case is wave 9 at 4.884 m/s — a margin of 0.856 m/s, 85.1 %
    /// of sprint — and the shipped guard <c>edge ≤ sprint − 0.5</c> passes with 0.356 to spare.
    /// Round 35's "no stamina" ruling means the margin holds for the whole 407 s of wave 1.
    /// </summary>
    [Fact]
    public void NoRingEdgeOutrunsTheMovementLanesSprint()
    {
        double sprint = MovementProfile.Default.SprintSpeed;
        Assert.InRange(sprint, 5.73d, 5.75d);

        // D276 (docs/118 §2) MOVED THIS. The owner's ruling (a) is that retail's whole-map rings
        // were survivable by driving, not by sprinting, and D62's 5.00 m/s foot rail was the single
        // constraint keeping every circle in the middle of the map. So the wave-9 measurement now
        // lives on the FootRail preset - which is D62 byte for byte - and the shipped ladder is
        // asserted against its configured rail instead. THE CONSEQUENCE IS REAL AND DELIBERATE: a
        // player on foot can no longer outrun the shipped wall (docs/118 §2.3).
        GasSchedule footRail = GasSchedule.Create(GasTuning.FootRail, 1);
        double worst = 0d;
        foreach (GasPhase phase in footRail.Phases)
        {
            double seconds = phase.ShrinkDurationMs / 1000d;
            double edge =
                (Vector3.Distance(phase.Origin.Centre, phase.Target.Centre)
                    + phase.Origin.Radius - phase.Target.Radius)
                / seconds;
            worst = Math.Max(worst, edge);
            Assert.InRange(edge, 0.1d, sprint - 0.5d);
        }

        Assert.InRange(worst, 4.50d, 4.70d);
        Assert.True(sprint - worst >= 0.5d, $"escape margin {sprint - worst:F3} m/s is too thin");
        Assert.Equal(0.20f, GasTuning.FootRail.CentreDriftFraction);
        Assert.Equal(5.00f, GasTuning.FootRail.MaxEdgeSpeedMetresPerSecond);

        // The shipped ladder: bounded by the rail, and the cone is untouched.
        GasSettings settings = Settings();
        double shippedWorst = 0d;
        foreach (GasPhase phase in Schedule().Phases)
        {
            double seconds = phase.ShrinkDurationMs / 1000d;
            shippedWorst = Math.Max(
                shippedWorst,
                (Vector3.Distance(phase.Origin.Centre, phase.Target.Centre)
                    + phase.Origin.Radius - phase.Target.Radius)
                / seconds);
        }

        Assert.InRange(shippedWorst, 0.1d, settings.MaxEdgeSpeedMetresPerSecond);
        Assert.Equal(0.96f, settings.CentreDriftFraction);
        Assert.Equal(40.00f, settings.MaxEdgeSpeedMetresPerSecond);
        Assert.Equal(120f, settings.DriftConeDegrees);
        Assert.Equal(8000f, settings.InitialRadius);
        Assert.Equal(GasPacing.PhaseTable, settings.Pacing);
        Assert.Equal(300_000u, settings.AdvanceMsForPhase(1));
    }

    /// <summary>
    /// <b>No gas boundary is drawn before first movement.</b> His "no gas" report
    /// is explicitly not a reason to put the wall on the map earlier: <c>PreMoveRing = None</c> IS
    /// his server. The draw gate and the damage gate must also never come apart — a player burned
    /// by a circle their client is not drawing is the failure mode his own file warns about.
    /// </summary>
    [Fact]
    public void TheWholeFirstHoldStillDrawsNoGasBoundary()
    {
        GasSettings settings = Settings();
        GasSchedule schedule = Schedule();

        Assert.Equal(GasPreMoveRing.None, settings.PreMoveRing);
        Assert.Equal(120_000u, settings.FirstRevealDelayMs);
        Assert.Equal(370_000u, settings.FirstMoveDelayMs);
        Assert.Equal(10, settings.PhaseCount);
        Assert.Equal(40f, settings.FinalRadius);
        Assert.Equal<uint>([90, 100, 120, 150, 200, 270, 400, 600, 600, 600], settings.DamagePerPhase);
        Assert.Equal(370_000L, schedule.RingLiveFromMs);

        for (long clock = 0; clock < 370_000; clock += 1000)
        {
            Assert.False(schedule.IsRingVisibleAt(clock));
            Assert.Equal(schedule.IsRingVisibleAt(clock), schedule.IsLethalAt(clock));
        }

        Assert.True(schedule.IsRingVisibleAt(370_000));
        Assert.True(schedule.IsLethalAt(370_000));
    }

    /// <summary>
    /// The heal re-sends the phase's DESTINATION circle — the green one the player is being asked
    /// to run to — and never the moving wall, which is <c>ce 01</c>'s job.
    /// </summary>
    [Fact]
    public void TheSafeZoneHealRepeatsTheDestinationCircle()
    {
        GasSchedule schedule = Schedule();
        GasCircle destination = schedule.Phase(1).Target;

        Assert.Equal(destination, schedule.RevealedCircleAt(150_000));
        byte[] first = Bytes(w => GasPackets.WriteSafeZone(w, destination));
        byte[] heal = Bytes(w => GasPackets.WriteSafeZone(w, schedule.PhaseAt(200_000)!.Value.Target));
        Assert.Equal(first, heal);
        Assert.Equal(GasPackets.SafeZoneLength, heal.Length);
    }

    /// <summary>
    /// <c>CRANBERRY_GAS_SCALE</c> compresses the heals with the clock so a five-minute bring-up
    /// match keeps the same rhythm — but a heal switched OFF stays off, which the naive scaling
    /// floor would silently turn into a 50 ms beat.
    /// </summary>
    [Fact]
    public void ScalingCompressesTheHealsButNeverRevivesADisabledOne()
    {
        GasSettings scaled = Settings().ScaledBy(0.2f);
        Assert.Equal(200u, scaled.HudHealIntervalMs);
        Assert.Equal(3_000u, scaled.SafeZoneHealIntervalMs);

        GasSettings offThenScaled = (Settings() with { HudHealIntervalMs = 0, SafeZoneHealIntervalMs = 0 })
            .ScaledBy(0.2f);
        Assert.Equal(0u, offThenScaled.HudHealIntervalMs);
        Assert.Equal(0u, offThenScaled.SafeZoneHealIntervalMs);
        offThenScaled.Validate();
    }

    /// <summary>Every boot line and click log records the wave-9 cadences, so a report is diagnosable.</summary>
    [Fact]
    public void TheBootLineRecordsTheHealCadencesAndTheBanners()
    {
        string description = GasTuning.DescribeSchedule(Settings());
        Assert.Contains("hud heal 1000 ms", description, StringComparison.Ordinal);
        Assert.Contains("safe-zone heal 15000 ms", description, StringComparison.Ordinal);
        Assert.Contains("banners on", description, StringComparison.Ordinal);

        string off = GasTuning.DescribeSchedule(
            Settings() with { HudHealIntervalMs = 0, SafeZoneHealIntervalMs = 0, SendBanners = false });
        Assert.Contains("hud heal off", off, StringComparison.Ordinal);
        Assert.Contains("safe-zone heal off", off, StringComparison.Ordinal);
        Assert.Contains("banners off", off, StringComparison.Ordinal);
    }
}
