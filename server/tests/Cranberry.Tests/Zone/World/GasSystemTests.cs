using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Gas;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// Which packet draws the gas, and who is standing in it. GasController owns the schedule and has
// its own tests; this file pins the two things only the system can get wrong - the ce 01 / ce 02
// split of docs/18 §1b/§2, and who is sampled as damageable.
public sealed class GasSystemTests
{
    private const byte GameModeOpcode = GameModeHud.Opcode;   // 0xce
    private const ushort RingSub = GasPackets.RingSubOpcode;  // ce 01 - the drawn wall
    private const ushort SafeZoneSub = 0x0002;                // ce 02 - the next zone

    /// <summary>
    /// A compressed match: reveal at 1 s, a 1 s warning head and a 4 s close, so a whole phase runs
    /// inside a few hundred ticks. Every number is a GasSettings knob; none of them is a client fact.
    /// </summary>
    private static MatchSettings Fast(GasPreMoveRing preMoveRing = GasPreMoveRing.Boundary) =>
        FastSettings(preMoveRing);

    private static MatchSettings FastSettings(GasPreMoveRing preMoveRing) => MatchSettings.Default with
    {
        MinPlayersToStart = 1,
        LobbyCountdownMs = 200,
        DropDurationMs = 2_000,
        GasEnabled = true,
        Gas = new GasSettings
        {
            // Wave 5 (docs/53) made GasPacing.SpeedPaced the default, which derives every window
            // from the wall speed and the radii — a 4,000 -> 500 m phase would then take ten
            // minutes. This file is about which packet is sent to whom, not about the ladder, so it
            // asks for the literal-window model and writes its own compressed windows.
            Pacing = GasPacing.FixedWindows,
            PhaseCount = 1,
            InitialRadius = 4_000f,
            FinalRadius = 500f,
            FirstRevealDelayMs = 1_000,
            FirstPhaseWindowMs = 5_000,
            MinimumPhaseWindowMs = 5_000,
            ShrinkWarningMs = 1_000,
            SafeZoneUpdateIntervalMs = 500,

            // Wave 8 (docs/77 §6) made GasPreMoveRing.None the shipped default: nothing gas-shaped
            // goes on the map, and nothing is lethal, until the ring first moves. Most of this file
            // is about the ce 01 / ce 02 split during a phase, so it asks for the pre-wave-8
            // Boundary position and the two tests that are ABOUT the new rule name it.
            PreMoveRing = preMoveRing,
        },
    };

    private static float RingRadius(RecordedPacket packet) => BitConverter.ToSingle(packet.Body, 19);

    [Fact]
    public void TheOpeningRingDrawsThePlayAreaBeforeAnyPhaseIsRevealed()
    {
        // GasPreMoveRing.Boundary, the pre-wave-8 rule: the play-area circle is lethal from the
        // first damage tick, so it is drawn from the first tick too. ce 02 is withheld either way:
        // revealing phase 1 early is what FirstRevealDelayMs prevents.
        var harness = new MatchHarness(Fast(GasPreMoveRing.Boundary));
        MatchPlayer player = harness.AddPlayer();
        harness.StepSeconds(0.5);

        Assert.True(harness.Match.Gas.Running);
        RecordedPacket opening = Assert.Single(MatchHarness.SinkOf(player).With(GameModeOpcode, RingSub));
        Assert.Equal(4_000f, RingRadius(opening));
        Assert.Empty(MatchHarness.SinkOf(player).With(GameModeOpcode, SafeZoneSub));
    }

    /// <summary>
    /// The shipped rule, and the owner's own click-test report: <i>"The gas is already on the map —
    /// it shouldn't be on the map until it starts moving and closing in on the circle."</i> No
    /// <c>ce 01</c> at all until the ring moves; the reveal still sends the green <c>ce 02</c> on
    /// time, so the player is shown where to go without a wall being drawn around them (docs/77 §6).
    /// </summary>
    [Fact]
    public void NoRingIsDrawnUntilTheGasStartsMoving()
    {
        var harness = new MatchHarness(Fast(GasPreMoveRing.None));
        MatchPlayer player = harness.AddPlayer();

        harness.StepSeconds(0.5);
        Assert.Empty(MatchHarness.SinkOf(player).With(GameModeOpcode, RingSub));

        // Revealed at 1 s: the next safe zone appears, the wall does not.
        harness.StepSeconds(0.8);
        Assert.Equal(1, harness.Match.Gas.PhaseIndex);
        Assert.Single(MatchHarness.SinkOf(player).With(GameModeOpcode, SafeZoneSub));
        Assert.Empty(MatchHarness.SinkOf(player).With(GameModeOpcode, RingSub));

        // It starts moving at 2 s, and that is when the wall is drawn — at the circle still in
        // force, the whole play area.
        harness.StepSeconds(3.0);
        List<RecordedPacket> rings = [.. MatchHarness.SinkOf(player).With(GameModeOpcode, RingSub)];
        Assert.NotEmpty(rings);

        // The first wall the client ever sees is the circle in force — the whole play area, a few
        // ticks into its travel — and never the 500 m destination.
        Assert.InRange(RingRadius(rings[0]), 3_500f, 4_000f);
    }

    /// <summary>
    /// The middle position: one <c>ce 01</c> at radius 0, which is the client's own recognised "no
    /// gas" value (<c>FUN_140bbba50</c> clears the volume renderer's enable byte, docs/15 §2,
    /// docs/18 §1). It exists because a radius-0 ring correlated with a white bloom artefact on the
    /// owner's own server; if sending nothing upsets the 1148 HUD, this is the position to try.
    /// </summary>
    [Fact]
    public void TheZeroRadiusPositionOpensWithTheClientsOwnNoGasValue()
    {
        var harness = new MatchHarness(Fast(GasPreMoveRing.ZeroRadius));
        MatchPlayer player = harness.AddPlayer();
        harness.StepSeconds(0.5);

        RecordedPacket opening = Assert.Single(MatchHarness.SinkOf(player).With(GameModeOpcode, RingSub));
        Assert.Equal(GasPackets.RingTerminalRadius, RingRadius(opening));
    }

    [Fact]
    public void TheRevealDrawsTheBoundaryStillInForceAndNamesTheTargetAsTheNextZone()
    {
        // docs/18 §1b: ce 01 is the only sub the gas-volume renderer and the minimap ring read, and
        // its trailing u32 is a smoothing constant, not a deadline. docs/18 §2: ce 02 has no
        // renderer path at all. So the reveal draws the origin and promises the target - never the
        // other way round, which would put a lethal-looking wall around a player who is not being
        // damaged for another four seconds.
        var harness = new MatchHarness(Fast());
        MatchPlayer player = harness.AddPlayer();
        harness.StepSeconds(0.5);
        harness.ClearSinks();

        harness.StepSeconds(0.8);

        Assert.Equal(1, harness.Match.Gas.PhaseIndex);
        RecordedPacket ring = MatchHarness.SinkOf(player).With(GameModeOpcode, RingSub).First();
        RecordedPacket next = Assert.Single(MatchHarness.SinkOf(player).With(GameModeOpcode, SafeZoneSub));
        Assert.Equal(4_000f, RingRadius(ring));    // the origin: still the whole play area
        Assert.Equal(500f, RingRadius(next));      // the destination the phase closes onto
    }

    [Fact]
    public void TheDrawnRingTracksTheServersOwnClosingCircle()
    {
        // The bug this pins: sending ce 01 once per phase with the target and then re-sending only
        // ce 02 left the drawn wall parked on the final radius for the whole shrink, while the
        // server damaged against a circle that was still kilometres wider.
        var harness = new MatchHarness(Fast());
        MatchPlayer player = harness.AddPlayer();
        harness.StepSeconds(2.5);
        harness.ClearSinks();

        harness.StepSeconds(1.0);

        List<RecordedPacket> rings = [.. MatchHarness.SinkOf(player).With(GameModeOpcode, RingSub)];
        Assert.NotEmpty(rings);

        // The second half of the shrink: every re-send is between the two radii and monotonically
        // smaller, and it agrees with the circle the controller is damaging against.
        float[] radii = [.. rings.Select(RingRadius)];
        Assert.All(radii, radius => Assert.InRange(radius, 500f, 4_000f));
        for (int i = 1; i < radii.Length; i++)
        {
            Assert.True(radii[i] < radii[i - 1], $"ce 01 radius did not shrink: {radii[i - 1]} -> {radii[i]}");
        }

        // ce 02 does not move during the phase: the next zone is fixed once it is revealed.
        Assert.Empty(MatchHarness.SinkOf(player).With(GameModeOpcode, SafeZoneSub));
    }

    [Fact]
    public void NobodyIsDamagedBeforeTheMatchGoesLive()
    {
        // The gas clock starts at ce 16 StartMatch so the client's reveal countdown lines up
        // (docs/22 §6.2, deviation in docs/25 §2), but Dropping is a parachute, not a match:
        // TimerPhaseTable already declares GasReveal/GasClose Live-only.
        var harness = new MatchHarness(Fast());
        MatchPlayer player = harness.AddPlayer("Outside");
        harness.AddPlayer("Inside");                             // keeps the match off Ending
        harness.MoveTo(player, new Vector3(9_000f, 500f, 0f));   // far outside every circle

        harness.StepSeconds(0.5);
        Assert.Equal(MatchPhase.Dropping, harness.Match.Phase);
        Assert.Equal(harness.Settings.StartingHealth, player.Health);

        harness.StepSeconds(2.0);
        Assert.Equal(MatchPhase.Live, harness.Match.Phase);
        Assert.True(player.Health < harness.Settings.StartingHealth);
    }

    [Fact]
    public void ARiderUnderACanopyIsNeverDamaged()
    {
        // ZoneService.PumpGas carries the identical gate: the on-foot pose is stale while the real
        // one is on channel 3, so a mounted player is not a gas target.
        var harness = new MatchHarness(Fast());
        MatchPlayer player = harness.AddPlayer("Rider");
        harness.AddPlayer("Inside");
        harness.MoveTo(player, new Vector3(9_000f, 500f, 0f));
        harness.StepSeconds(0.5);

        player.Mount = EntityId.Create(EntityKind.Vehicle, 1, 1);
        harness.StepSeconds(2.0);

        Assert.Equal(MatchPhase.Live, harness.Match.Phase);
        Assert.Equal(harness.Settings.StartingHealth, player.Health);

        player.Mount = EntityId.None;
        harness.StepSeconds(1.5);
        Assert.True(player.Health < harness.Settings.StartingHealth);
    }

    [Fact]
    public void RegisteredCarOccupantsTakeGasDamageAtTheVehiclesPosition()
    {
        var harness = new MatchHarness(Fast());
        MatchPlayer player = harness.AddPlayer("Driver");
        harness.AddPlayer("Inside");
        var car = harness.Match.World.SpawnVehicle(1, 1,
            new Vector3(9000, 500, 0), Quaternion.Identity, player.Id);
        player.Mount = car.Id;
        harness.StepSeconds(2.5);

        Assert.True(player.Health < harness.Settings.StartingHealth);
        Assert.True(harness.Match.Gas.Controller.ToxicityForPlayer(player.Slot) > 0);
    }

    [Fact]
    public void FullToxicityAffectsWorldHealthAndPublishesTheAuthoritativeMeter()
    {
        MatchSettings settings = Fast();
        settings = settings with { Gas = settings.Gas with { ToxicityMaxValue = 1000 } };
        var harness = new MatchHarness(settings);
        MatchPlayer player = harness.AddPlayer("Exposed");
        harness.AddPlayer("Inside");
        harness.MoveTo(player, new Vector3(9000, 500, 0));
        harness.StepSeconds(2.5);

        Assert.Equal(1000u, harness.Match.Gas.Controller.ToxicityForPlayer(player.Slot));
        Assert.Contains(MatchHarness.Sent(player), packet => packet.Opcode == 0x8d);
        int before = player.Health;
        harness.StepSeconds(1);
        Assert.Equal(before - 130, player.Health);
    }

    [Fact]
    public void SilencingWorldToxicityPacketsLeavesTheDamageEffectEnabled()
    {
        MatchSettings settings = Fast();
        settings = settings with { Gas = settings.Gas with { ToxicityMaxValue = 1000, SendToxicity = false } };
        var harness = new MatchHarness(settings);
        MatchPlayer player = harness.AddPlayer("Exposed");
        harness.AddPlayer("Inside");
        harness.MoveTo(player, new Vector3(9000, 500, 0));
        harness.StepSeconds(2.5);

        Assert.Equal(1000u, harness.Match.Gas.Controller.ToxicityForPlayer(player.Slot));
        Assert.DoesNotContain(MatchHarness.Sent(player), packet => packet.Opcode == 0x8d
            && packet.Body.Length == 101 && BitConverter.ToUInt32(packet.Body, 14) == settings.Gas.ToxicityResourceId);
        Assert.Contains(MatchHarness.Sent(player), packet => packet.Opcode == 0x8d
            && packet.Body.Length == 101 && BitConverter.ToUInt32(packet.Body, 14) == 1);
        int before = player.Health;
        harness.StepSeconds(1);
        Assert.Equal(before - 130, player.Health);
    }
}
