using Cranberry.Zone;
using System.Numerics;
using Cranberry.Zone.Descent;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Match;
using Cranberry.Zone.World;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone;

/// <summary>
/// The wave-5 Integrate pass. Five lanes built five mechanisms and each one needed a call site in a
/// file only this pass may edit — <c>ZoneService.cs</c>, <c>ZoneOptions.cs</c>, <c>Program.cs</c>.
/// A mechanism that is built, tested and <b>never called</b> is exactly what the owner experiences
/// as "nothing changed", so these cases pin the wiring rather than the mechanisms.
/// <para>
/// <b>Send-side and structural assertions only (D29).</b> Nothing here is LIVE-VERIFIED, and none of
/// it claims to be.
/// </para>
/// <para>
/// <b>2026-09-02 (overhaul lane 0A).</b> The nine cases that asserted a call site by locating
/// <c>Cranberry.slnx</c> and grepping <c>ZoneService.cs</c> / <c>Program.cs</c> were deleted, with
/// their <c>Between</c> / <c>Code</c> / <c>RepositoryRoot</c> helpers: they asserted source text
/// rather than behaviour and failed whenever the assemblies were built out of tree (S1 §4.2,
/// OVERHAUL-PLAN §5.2). What remains are the option and schedule cases, which assert values.
/// </para>
/// </summary>
[Collection(Cranberry.Tests.Zone.Appearance.AppearanceStaticsCollection.Name)]
public sealed class Wave5IntegrationTests
{
    // -------------------------------------------------------------------------------------------
    // 1. Loot streaming (docs/52) — the P0. The owner found every building away from the landing
    //    point empty, because wave 4 spawned the map's loot once and never again.
    // -------------------------------------------------------------------------------------------

    /// <summary>The streamer is on by default: a battle royale with one building of loot is not one.</summary>
    [Fact]
    public void TheLootStreamerIsOnByDefaultAndReachesTheZoneOptions()
    {
        var options = new ZoneOptions();

        Assert.True(options.LootStream.Enabled);
        Assert.Equal(60f, options.LootStream.StreamRadiusMetres);

        // WAVE 8 (docs/78 §3): the cap is now a guard rail above the working set rather than a
        // budget inside it, the tick is 500 ms rather than 3 s, and the panel is the owner's own
        // 2 m ball. These four are the shipped shape of the streamer and are asserted together so a
        // partial revert is loud.
        Assert.Equal(704, options.LootStream.MaxLive);
        Assert.Equal(500, options.LootStream.RestreamIntervalMs);
        Assert.Equal(2f, options.LootStream.PanelRadiusMetres);
        Assert.Equal(4f, options.LootStream.PickupReachMetres);
    }

    /// <summary>
    /// <b>The pump ends only when BOTH arms are done.</b> One arm being switched off must not take
    /// the other one down with it — and <see cref="WorldStreamStep.Stop"/> ends the timer chain for
    /// the rest of the match, which is the wave-4 defect this whole shape exists to avoid.
    /// </summary>
    [Theory]
    // doors on, loot off: still a live chain.
    [InlineData(true, false)]
    // doors off, loot on: still a live chain. This is the case a door-only gate would have broken.
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EitherArmAloneKeepsTheWorldPumpAlive(bool doors, bool loot)
    {
        DoorPumpStep doorStep = MatchDoors.NextPumpStep(
            inMatch: true,
            sendDoors: doors,
            restreamIntervalMs: 3000,
            doors: null,
            centre: null,
            radius: 60f);

        WorldStreamStep lootStep = MatchLoot.NextPumpStep(
            inMatch: true,
            sendLoot: loot,
            loot: null,
            centre: null,
            options: LootStreamOptions.Default);

        Assert.False(doorStep == DoorPumpStep.Stop && lootStep == WorldStreamStep.Stop);
    }

    /// <summary>Leaving the match stops both arms, which is the only thing that may end the chain.</summary>
    [Fact]
    public void LeavingTheMatchStopsBothArms()
    {
        Assert.Equal(
            DoorPumpStep.Stop,
            MatchDoors.NextPumpStep(false, true, 3000, null, null, 60f));
        Assert.Equal(
            WorldStreamStep.Stop,
            MatchLoot.NextPumpStep(false, true, null, null, LootStreamOptions.Default));
    }

    // -------------------------------------------------------------------------------------------
    // 2. Gas timings (docs/53) — the other P0. "Gas is spreading way too quickly."
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The retail reconstruction uses ten waves with explicit hold and movement durations:
    /// reveal at 2:00, first movement at 6:10, and the final circle closes at 30:30. Every movement
    /// has a positive duration and stays within the configured leading-edge rail.
    /// </summary>
    [Fact]
    public void TheShippedGasScheduleUsesTenTimedWavesAndRespectsItsSpeedRail()
    {
        var settings = new GasSettings();

        Assert.Equal(10, settings.PhaseCount);
        Assert.Equal(120_000u, settings.FirstRevealDelayMs);
        Assert.Equal(GasPacing.PhaseTable, settings.Pacing);

        GasSchedule schedule = GasSchedule.Create(settings, 0);
        Assert.Equal(370_000, schedule.Phase(1).ShrinkStartAtMs);
        Assert.Equal(1_830_000, schedule.FinishedAtMs);
        Assert.Equal(20d, (schedule.Phase(1).Origin.Radius - schedule.Phase(1).Target.Radius)
            / (schedule.Phase(1).ShrinkDurationMs / 1000d));

        // Every wall, not just the first: an endgame ring that teleports is the same bug.
        for (int phase = 1; phase <= settings.PhaseCount; phase++)
        {
            GasPhase wave = schedule.Phase(phase);
            float travelled = Vector3.Distance(wave.Origin.Centre, wave.Target.Centre)
                + wave.Origin.Radius - wave.Target.Radius;
            double seconds = wave.ShrinkDurationMs / 1000.0;
            Assert.True(seconds > 0d);
            Assert.True(travelled > 0f);
            Assert.InRange(travelled / seconds, 0d, settings.MaxEdgeSpeedMetresPerSecond);
        }
    }

    /// <summary>
    /// A scaled run shows all ten reveals sooner, and it must
    /// keep the shape: same radii, same damage, same ordering — only the clock moves.
    /// </summary>
    [Fact]
    public void TheSprintPresetPlaysTheSameLadderOnAFasterClock()
    {
        GasSchedule full = GasSchedule.Create(GasTuning.Aug2017Retail, 0);
        GasSchedule sprint = GasSchedule.Create(GasTuning.Sprint, 0);

        Assert.Equal(GasTuning.Aug2017Retail.PhaseCount, GasTuning.Sprint.PhaseCount);
        Assert.True(sprint.FinishedAtMs < full.FinishedAtMs / 4);
        for (int phase = 1; phase <= GasTuning.Aug2017Retail.PhaseCount; phase++)
        {
            Assert.Equal(full.Phase(phase).Target.Radius, sprint.Phase(phase).Target.Radius, 3);
        }
    }

    // -------------------------------------------------------------------------------------------
    // 3. Item tints and worn visuals (docs/54).
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Regression guard 1 is still not asked to move</b> — restated for wave 8 (docs/80 edit 3).
    /// <para>
    /// This used to assert a process-wide bool, <c>AugustWornVisuals.SendShaderParameterGroup</c>,
    /// was false. That bool is gone: the colour gate is now a per-group predicate passed into
    /// <c>Dress</c>, which is both narrower and un-flaky (it was the reason
    /// <c>AppearanceStaticsCollection</c> had to exist). What this guard actually cares about is
    /// unchanged and is asserted directly: the appearance FILTER is untouched, so the
    /// <c>ReferenceData</c> payload does not move, and docs/54 §I4's +27,721-byte experiment
    /// remains a separate one.
    /// </para>
    /// </summary>
    [Fact]
    public void TheWornColourExperimentStaysOff()
    {
        // The filter's own switch, which is the thing that moves the payload.
        Assert.False(AugustDynamicAppearanceTable.ApplyAppearanceRowOverrides);

        // And the wire proof: a dress with no predicate carries group 0 for everything, exactly as
        // it did before wave 8.
        List<CharacterEquipmentAttachment> dress =
        [
            .. CharacterVisuals.FromSelection(
                CharacterVisuals.Male, headId: 1, hairId: 1, skinToneId: 665, profileId: 0)
                .StarterOutfit,
        ];
        Cranberry.Zone.Appearance.AugustWornVisuals.Dress(
            dress,
            [new Cranberry.Zone.Appearance.AugustWornItem(10, 2112, string.Empty)],
            CharacterVisuals.Male,
            appearanceRowsFor: null);
        Assert.Equal(
            0u,
            Assert.Single(dress, attachment => attachment.SlotId == 10).ShaderParameterGroupId);
    }

    // -------------------------------------------------------------------------------------------
    // 5. Fists and descent (docs/56).
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The owner's "maybe I drop down too fast" is answered by altitude, because the ~40 m/s fall is
    /// the client's own simulation and no packet Cranberry sends carries a rate. <c>Owner30</c> must
    /// therefore actually raise the plan's air spawn.
    /// </summary>
    [Fact]
    public void TheOwnerThirtySecondPresetAsksForMoreAirThanTheDerivedSkySpawn()
    {
        DropOptions raised = new ZoneOptions().Drop.WithDescent(DescentTuning.Owner30);

        Assert.True(raised.MinimumClearanceMetres > new ZoneOptions().Drop.MinimumClearanceMetres);
        Assert.InRange(raised.MinimumClearanceMetres, 1_100f, 1_400f);
    }
}
