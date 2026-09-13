using Cranberry.Zone.Crafting;
using Cranberry.Zone.Descent;
using Cranberry.Zone.Lighting;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Movement;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone;

// Lane 2C (docs/101). The owner's rulings moved out of C# literals and into rulings/*.json, from
// which tools/pipeline/generators/gen-rulings.py emits Generated/Rulings.g.cs. That move was
// supposed to change WHERE a number is declared and nothing else.
//
// Every number below is written out AS A LITERAL, twice over: once against the generated constant
// and once against the value class that now reads it. The literals are the ones the value classes
// held before lane 2C ran, transcribed from the pre-move files. So:
//
//   * if the generated file and the value class ever disagree, the second half of a row fails;
//   * if somebody edits rulings/*.json, the FIRST half fails and the edit is visible in a diff
//     rather than silently becoming the new truth.
//
// That second property is the point. A ruling is allowed to change - the owner changes his mind,
// and D59 already records two of his rulings in direct conflict - but it may never change by
// accident, and it may never change without this file saying so.
public sealed class RulingsTests
{
    // ---------------------------------------------------------------- movement (D54/D55/D56/D59)

    [Fact]
    public void MovementMatchesTheRecordedRulingsAndSeptemberShiftRollPolicy()
    {
        MovementProfile profile = MovementProfile.Default;

        Assert.Equal(4.10f, Rulings.Movement.MaxMovementSpeed);
        Assert.Equal(4.10f, profile.MaxMovementSpeed);
        Assert.Equal(1.40f, Rulings.Movement.SprintSpeedModifier);
        Assert.Equal(1.40f, profile.SprintSpeedModifier);
        Assert.Equal(0.30f, Rulings.Movement.WalkSpeedModifier);
        Assert.Equal(0.30f, profile.WalkSpeedModifier);
        Assert.Equal(0.70f, Rulings.Movement.CrouchSpeedModifier);
        Assert.Equal(0.70f, profile.CrouchSpeedModifier);
        Assert.Equal(0.75f, Rulings.Movement.BackpedalSpeedModifier);
        Assert.Equal(0.75f, profile.BackpedalSpeedModifier);
        Assert.Equal(0.75f, Rulings.Movement.StrafeSpeedModifier);
        Assert.Equal(0.75f, profile.StrafeSpeedModifier);
        Assert.Equal(0.40f, Rulings.Movement.ProneSpeedModifier);
        Assert.Equal(0.40f, profile.ProneSpeedModifier);
        // User 2026-09-06: native lateral Shift-roll must be faster than crawling.
        // Roll applies after the prone scalar: 4.10 x 0.40 x 2.50 = 4.10 m/s.
        Assert.Equal(2.50f, Rulings.Movement.ProneRollSpeedModifier);
        Assert.Equal(2.50f, profile.ProneRollSpeedModifier);
        Assert.Equal(0.55f, Rulings.Movement.SwimSpeedModifier);
        Assert.Equal(0.55f, profile.SwimSpeedModifier);
        Assert.Equal(0.80f, Rulings.Movement.WaterSpeedModifier);
        Assert.Equal(0.80f, profile.WaterSpeedModifier);

        // September 8: 350 ms startup; the existing zero deceleration stays unchanged.
        Assert.Equal(0.35f, Rulings.Movement.SprintAccelerationTime);
        Assert.Equal(0.35f, profile.SprintAccelerationTime);
        Assert.Equal(0f, Rulings.Movement.SprintDecelerationTime);
        Assert.Equal(0f, profile.SprintDecelerationTime);
        Assert.Equal(0.35f, Rulings.Movement.ForwardAccelerationTime);
        Assert.Equal(0.35f, profile.ForwardAccelerationTime);
        Assert.Equal(0f, Rulings.Movement.ForwardDecelerationTime);
        Assert.Equal(0f, profile.ForwardDecelerationTime);
        Assert.Equal(0.35f, Rulings.Movement.BackAccelerationTime);
        Assert.Equal(0.35f, profile.BackAccelerationTime);
        Assert.Equal(0f, Rulings.Movement.BackDecelerationTime);
        Assert.Equal(0f, profile.BackDecelerationTime);
        Assert.Equal(0.35f, Rulings.Movement.StrafeAccelerationTime);
        Assert.Equal(0.35f, profile.StrafeAccelerationTime);
        Assert.Equal(0f, Rulings.Movement.StrafeDecelerationTime);
        Assert.Equal(0f, profile.StrafeDecelerationTime);

        // and the derived speeds the owner actually feels, so a ratio change cannot hide
        Assert.Equal(5.74f, profile.SprintSpeed, 3);
        Assert.Equal(3.075f, profile.StrafeSpeed, 3);
    }

    // --------------------------------------------------------------------- gas (D43/D62/D63/D65)

    [Fact]
    public void GasDefaultsMatchTheDocumentedSizingAudit()
    {
        var gas = new GasSettings();

        Assert.Equal(10, Rulings.Gas.PhaseCount);
        Assert.Equal(10, gas.PhaseCount);
        Assert.Equal(new System.Numerics.Vector3(-250f, 0f, 100f), Rulings.Gas.PlayAreaCentre);
        Assert.Equal(new System.Numerics.Vector3(-250f, 0f, 100f), gas.PlayAreaCentre);
        Assert.Equal(8000f, Rulings.Gas.InitialRadius);
        Assert.Equal(8000f, gas.InitialRadius);
        Assert.Equal(40f, Rulings.Gas.FinalRadius);
        Assert.Equal(40f, gas.FinalRadius);

        // D285: the owner's own Z1 radius ladder. RadiusForPhase reads it, so the first ring is
        // 2635 m (Z1's) rather than the geometric 2750 m the audit measured.
        float[] ladder = [2000f, 1400f, 900f, 625f, 300f, 137.5f, 75f, 60f, 45f, 40f];
        Assert.Equal(ladder, Rulings.Gas.RadiusLadder);
        Assert.Equal(ladder, gas.RadiusLadder);
        Assert.Equal(2000f, gas.RadiusForPhase(1));
        Assert.Equal(GasPacing.PhaseTable, gas.Pacing);
        uint[] holds = [250000, 120000, 90000, 90000, 60000, 60000, 50000, 50000, 50000, 50000];
        uint[] advances = [300000, 60000, 60000, 60000, 60000, 60000, 60000, 60000, 60000, 60000];
        Assert.Equal(holds, Rulings.Gas.HoldDurationsMs);
        Assert.Equal(holds, gas.HoldDurationsMs);
        Assert.Equal(advances, Rulings.Gas.AdvanceDurationsMs);
        Assert.Equal(advances, gas.AdvanceDurationsMs);
        Assert.Equal(120_000u, Rulings.Gas.FirstRevealDelayMs);
        Assert.Equal(120_000u, gas.FirstRevealDelayMs);
        Assert.Equal(370_000u, Rulings.Gas.FirstMoveDelayMs);
        Assert.Equal(370_000u, gas.FirstMoveDelayMs);
        Assert.Equal(16_000u, Rulings.Gas.InterPhaseHoldMs);
        Assert.Equal(16_000u, gas.InterPhaseHoldMs);
        Assert.Equal(3.866f, Rulings.Gas.ShrinkSpeedMetresPerSecond);
        Assert.Equal(3.866f, gas.ShrinkSpeedMetresPerSecond);
        Assert.Equal(1_490_000u, Rulings.Gas.TargetMatchLengthMs);
        Assert.Equal(1_490_000u, gas.TargetMatchLengthMs);
        Assert.Equal(0.96f, Rulings.Gas.CentreDriftFraction);
        Assert.Equal(0.96f, gas.CentreDriftFraction);
        Assert.Equal(120f, Rulings.Gas.DriftConeDegrees);
        Assert.Equal(120f, gas.DriftConeDegrees);
        Assert.Equal(40.00f, Rulings.Gas.MaxEdgeSpeedMetresPerSecond);
        Assert.Equal(40.00f, gas.MaxEdgeSpeedMetresPerSecond);
        Assert.Equal(100u, Rulings.Gas.AdvanceRoundingMs);
        Assert.Equal(100u, gas.AdvanceRoundingMs);
        Assert.Equal(5f, Rulings.Gas.RadiusRoundingMetres);
        Assert.Equal(5f, gas.RadiusRoundingMetres);
        Assert.Equal(180_000u, Rulings.Gas.FirstPhaseWindowMs);
        Assert.Equal(180_000u, gas.FirstPhaseWindowMs);
        Assert.Equal(60_000u, Rulings.Gas.PhaseWindowShorteningMs);
        Assert.Equal(60_000u, gas.PhaseWindowShorteningMs);
        Assert.Equal(90_000u, Rulings.Gas.MinimumPhaseWindowMs);
        Assert.Equal(90_000u, gas.MinimumPhaseWindowMs);
        Assert.Equal(30_000u, Rulings.Gas.ShrinkWarningMs);
        Assert.Equal(30_000u, gas.ShrinkWarningMs);
        Assert.Equal(1000u, Rulings.Gas.TickPeriodMs);
        Assert.Equal(1000u, gas.TickPeriodMs);
        Assert.Equal(250, Rulings.Gas.HostTickIntervalMs);
        Assert.Equal(250, gas.HostTickIntervalMs);
        Assert.Equal(500u, Rulings.Gas.SafeZoneUpdateIntervalMs);
        Assert.Equal(500u, gas.SafeZoneUpdateIntervalMs);
        Assert.Equal(1000u, Rulings.Gas.HudHealIntervalMs);
        Assert.Equal(1000u, gas.HudHealIntervalMs);
        Assert.Equal(15_000u, Rulings.Gas.SafeZoneHealIntervalMs);
        Assert.Equal(15_000u, gas.SafeZoneHealIntervalMs);
        Assert.Equal(90u, Rulings.Gas.FirstPhaseDamage);
        Assert.Equal(90u, gas.FirstPhaseDamage);
        Assert.Equal(45u, Rulings.Gas.DamageIncreasePerPhase);
        Assert.Equal(45u, gas.DamageIncreasePerPhase);
        Assert.Equal(10_000u, Rulings.Gas.MaxHitpoints);
        Assert.Equal(10_000u, gas.MaxHitpoints);
        Assert.Equal(0x5A32_4741_5320_0001UL, Rulings.Gas.Seed);
        Assert.Equal(0x5A32_4741_5320_0001UL, gas.Seed);

        uint[] damage = [90, 100, 120, 150, 200, 270, 400, 600, 600, 600];
        Assert.Equal(damage, Rulings.Gas.RetailDamagePerPhase);
        Assert.Equal(damage, gas.DamagePerPhase);

        // D65's pre-move ring is an ENUM, so the ruling row carries the member NAME and the
        // property keeps its typed literal. This is what stops the two drifting apart.
        Assert.Equal("None", Rulings.Gas.PreMoveRing);
        Assert.Equal(GasPreMoveRing.None, gas.PreMoveRing);
        Assert.Equal(Rulings.Gas.PreMoveRing, gas.PreMoveRing.ToString());

        // D277/D278 (docs/118): where the circles go. Two more enums on the same rule as D65's -
        // the row carries the member NAME, the property keeps its typed literal.
        Assert.Equal("PoiDestination", Rulings.Gas.CentrePlan);
        Assert.Equal(GasCentrePlan.PoiDestination, gas.CentrePlan);
        Assert.Equal(Rulings.Gas.CentrePlan, gas.CentrePlan.ToString());
        Assert.Equal("SendPeriod", Rulings.Gas.RingBlendMode);
        Assert.Equal(GasRingBlendMode.SendPeriod, gas.RingBlendMode);
        Assert.Equal(Rulings.Gas.RingBlendMode, gas.RingBlendMode.ToString());
        Assert.Equal(0.5f, Rulings.Gas.PoiWeightExponent);
        Assert.Equal(0.5f, gas.PoiWeightExponent);
        Assert.Equal(1_200f, Rulings.Gas.PlayAreaLeadMetres);
        Assert.Equal(1_200f, gas.PlayAreaLeadMetres);

        // D279 (docs/118 s4): the toxicity meter. Five of these six are the CLIENT's own
        // Resources.txt:145 columns; only the drain is a ruling, because BURN_PER_MSEC is 0 there.
        Assert.True(Rulings.Gas.SendToxicity);
        Assert.True(gas.SendToxicity);
        Assert.Equal(611u, Rulings.Gas.ToxicityResourceId);
        Assert.Equal(611u, gas.ToxicityResourceId);
        Assert.Equal(75u, Rulings.Gas.ToxicityResourceType);
        Assert.Equal(75u, gas.ToxicityResourceType);
        Assert.Equal(180_000u, Rulings.Gas.ToxicityMaxValue);
        Assert.Equal(180_000u, gas.ToxicityMaxValue);
        Assert.Equal(1.0f, Rulings.Gas.ToxicityRegenPerMs);
        Assert.Equal(1.0f, gas.ToxicityRegenPerMs);
        Assert.Equal(1_000u, Rulings.Gas.ToxicityRegenTickMs);
        Assert.Equal(1_000u, gas.ToxicityRegenTickMs);
        Assert.Equal(1_000u, Rulings.Gas.ToxicityDrainPerSecond);
        Assert.Equal(1_000u, gas.ToxicityDrainPerSecond);
    }

    // -------------------------------------------------------------- descent (D125/D127, docs/56)

    [Fact]
    public void DescentIsUnchangedByTheMoveIntoRulings()
    {
        Assert.Equal(40.4f, Rulings.Descent.PlannedDescentMetresPerSecond);
        Assert.Equal(40.4f, DescentSettings.Default.PlannedDescentMetresPerSecond);
        Assert.Equal(9.8f, Rulings.Descent.MeasuredHandsOffMetresPerSecond);
        Assert.Equal(9.8f, DescentSettings.MeasuredHandsOffMetresPerSecond);
        Assert.Equal(45.8f, Rulings.Descent.MeasuredFlownMetresPerSecond);
        Assert.Equal(45.8f, DescentSettings.MeasuredFlownMetresPerSecond);
        Assert.Equal(0f, Rulings.Descent.DefaultTargetDescentSeconds);
        Assert.Equal(0f, DescentSettings.Default.TargetDescentSeconds);

        Assert.Equal(10f, Rulings.Descent.MinimumRate);
        Assert.Equal(10f, DescentTuning.MinimumRate);
        Assert.Equal(56f, Rulings.Descent.MaximumRate);
        Assert.Equal(56f, DescentTuning.MaximumRate);
        Assert.Equal(120f, Rulings.Descent.MaximumSeconds);
        Assert.Equal(120f, DescentTuning.MaximumSeconds);

        Assert.Equal(36f, Rulings.Descent.Legacy36Seconds);
        Assert.Equal(36f, DescentTuning.Legacy36.TargetDescentSeconds);
        // D238: the shipped release is the client's own 850 m slab, so the seconds knob is OFF.
        Assert.Equal(0f, DescentTuning.ShippedDefault.TargetDescentSeconds);
        Assert.Equal(30f, Rulings.Descent.Owner30Seconds);
        Assert.Equal(30f, DescentTuning.Owner30.TargetDescentSeconds);

        Assert.Equal(2d, Rulings.Descent.ExpectedRideMultiple);
        Assert.Equal(2d, DescentDeadline.ExpectedRideMultiple);
        Assert.Equal(15d, Rulings.Descent.DeadlineSlackSeconds);
        Assert.Equal(15d, DescentDeadline.SlackSeconds);

        // D239's rebuilt deadline.
        Assert.Equal(1.5d, Rulings.Descent.HandsOffRideMultiple);
        Assert.Equal(1.5d, DescentDeadline.HandsOffRideMultiple);
        Assert.Equal(30d, Rulings.Descent.HandsOffSlackSeconds);
        Assert.Equal(30d, DescentDeadline.HandsOffSlackSeconds);
        Assert.Equal(40f, Rulings.Descent.GroundProximityMetres);
        Assert.Equal(40f, DescentDeadline.GroundProximityMetres);
        Assert.Equal(20d, Rulings.Descent.StreamSilenceSeconds);
        Assert.Equal(20d, DescentDeadline.StreamSilenceSeconds);
        Assert.Equal(2d, Rulings.Descent.StuckClientMultiple);
        Assert.Equal(2d, DescentDeadline.StuckClientMultiple);
    }

    // ------------------------------------------------------------------- sky (D16/D19/D98/D99)

    [Fact]
    public void SkyIsUnchangedByTheMoveIntoRulings()
    {
        Assert.Equal("Aug2017KotkClear", Rulings.Sky.DefaultPresetName);
        Assert.Equal("Aug2017KotkClear", EnvironmentPresets.Default.Name);

        // D98: five words of the 152-byte struct at zero, every other float still the map
        // author's own 12:00 row.
        SkySettings sky = EnvironmentPresets.Default.Sky;
        Assert.Equal(0f, Rulings.Sky.KotkClearCloudWeight0);
        Assert.Equal(0f, sky.CloudWeight0);
        Assert.Equal(0f, Rulings.Sky.KotkClearCloudWeight1);
        Assert.Equal(0f, sky.CloudWeight1);
        Assert.Equal(0f, Rulings.Sky.KotkClearCloudWeight2);
        Assert.Equal(0f, sky.CloudWeight2);
        Assert.Equal(0f, Rulings.Sky.KotkClearCloudWeight3);
        Assert.Equal(0f, sky.CloudWeight3);
        Assert.Equal(0f, Rulings.Sky.KotkClearCloudShadows);
        Assert.Equal(0f, sky.CloudShadows);
        Assert.Equal(0f, Rulings.Sky.GlobalPrecipitation);
        Assert.Equal(0f, sky.GlobalPrecipitation);

        // and the four the ruling did NOT touch, so a future edit to rulings/sky.json cannot
        // quietly become an edit to the client's own transcription (docs/101 §4).
        Assert.Equal(1.0e-4f, sky.FogDensity);
        Assert.Equal(38f, sky.SunAxisX);
        Assert.Equal(0.25f, sky.SkyClarity);
        Assert.Equal(1.30f, sky.CumulusCloudTiling);

        Assert.Equal(12, Rulings.Sky.FrozenHourUtc);
        Assert.Equal(12, FrozenSkyClock.SolarNoon.HourUtc);
        Assert.Equal(14, Rulings.Sky.LegacyAfternoonHourUtc);
        Assert.Equal(14, FrozenSkyClock.LegacyD18Afternoon.HourUtc);
        Assert.True(Rulings.Sky.FreezeClock);
        Assert.True(FrozenSkyClock.SolarNoon.Freeze);
        Assert.Equal("2017-08-15", Rulings.Sky.FrozenDateUtc);
        Assert.Equal(new DateOnly(2017, 8, 15), FrozenSkyClock.SolarNoon.DateUtc);

        Assert.Equal("Lighting_Z2.txt", Rulings.Sky.LightingFile);
        Assert.Equal(LightingTable.Z2, EnvironmentPresets.Default.LightingFile);
    }

    // ------------------------------------------------ loot gates (D68 THIRD_PARTY_SHAPED, D69/D70)

    [Fact]
    public void LootGatesAreUnchangedByTheMoveIntoRulings()
    {
        LootDensityOptions density0 = LootDensityOptions.Default;
        // D270/D271 (2026-09-03) raised the three gates that sat below the ruled 0.27 density to
        // it, which retired three of the six THIRD_PARTY_SHAPED rows this file used to carry. The
        // two that remain are the two his own tree already put ABOVE the ruled density, and the
        // flag on those has to survive.
        Assert.Equal(0.2700, Rulings.LootGates.Weapons01SpawnChance);
        Assert.Equal(0.2700, LootDensityOptions.Weapons01SpawnChance);
        Assert.Equal(0.2700, Rulings.LootGates.Gear01SpawnChance);
        Assert.Equal(0.2700, LootDensityOptions.Gear01SpawnChance);
        Assert.Equal(0.2700, Rulings.LootGates.Backpack01SpawnChance);
        Assert.Equal(0.2700, LootDensityOptions.Backpack01SpawnChance);
        Assert.Equal(0.3000, Rulings.LootGates.FirstAidKit01SpawnChance);
        Assert.Equal(0.3000, LootDensityOptions.FirstAidKit01SpawnChance);
        Assert.Equal(0.6000, Rulings.LootGates.Ammo01SpawnChance);
        Assert.Equal(0.6000, LootDensityOptions.Ammo01SpawnChance);
        Assert.Equal(0.2721, Rulings.LootGates.WeightedSpawnChance);
        Assert.Equal(0.2721, LootDensityOptions.WeightedSpawnChance);

        // D286 (reverses D272) and D275. Every gun on the floor carries TWO boxes again.
        Assert.Equal(2, Rulings.LootGates.BoxesPerGun);
        Assert.Equal(2, LootTables.LoadDefault().BoxesPerGun);
        Assert.Equal(2271u, Rulings.LootGates.LaminatedArmourItemDefinitionId);
        Assert.Equal(2271u, LootDensityOptions.LaminatedArmourItemDefinitionId);
        Assert.Equal(0.05, Rulings.LootGates.LaminatedArmourWorldChance);
        Assert.Equal(0.05, density0.LaminatedArmourWorldChance);
        Assert.Equal(250f, Rulings.LootGates.LaminatedArmourSpacingMetres);
        Assert.Equal(250f, density0.LaminatedArmourSpacingMetres);
        Assert.Equal(3, Rulings.LootGates.LaminatedArmourMaxPerSquare);
        Assert.Equal(3, density0.LaminatedArmourMaxPerSquare);
        Assert.Equal(1024f, Rulings.LootGates.LaminatedArmourMapSquareMetres);
        Assert.Equal(1024f, density0.LaminatedArmourMapSquareMetres);
        Assert.True(density0.LaminatedArmourRules);

        // D274's airdrop channel.
        AirdropOptions airdrop = AirdropOptions.Default;
        Assert.Equal(300_000L, Rulings.LootGates.FirstDropAtMs);
        Assert.Equal(300_000L, airdrop.FirstDropAtMs);
        Assert.Equal(240_000L, Rulings.LootGates.DropIntervalMs);
        Assert.Equal(240_000L, airdrop.DropIntervalMs);
        Assert.Equal(4, Rulings.LootGates.MaxDropsPerMatch);
        Assert.Equal(4, airdrop.MaxDropsPerMatch);
        Assert.Equal(20, Rulings.LootGates.StopAtPlayersAlive);
        Assert.Equal(20, airdrop.StopAtPlayersAlive);
        Assert.Equal(1, Rulings.LootGates.MinPlayersAlive);
        Assert.Equal(1, airdrop.MinPlayersAlive);
        Assert.Equal(20_000L, Rulings.LootGates.DescentMs);
        Assert.Equal(20_000L, airdrop.DescentMs);
        Assert.Equal(8_000L, Rulings.LootGates.UnlockMs);
        Assert.Equal(8_000L, airdrop.UnlockMs);
        Assert.Equal(0.8, Rulings.LootGates.SafeZoneFraction);
        Assert.Equal(0.8, airdrop.SafeZoneFraction);
        Assert.Equal(0.5, Rulings.LootGates.RifleChance);
        Assert.Equal(0.5, airdrop.RifleChance);
        Assert.Equal(3, Rulings.LootGates.PoolDraws);
        Assert.Equal(3, airdrop.PoolDraws);
        Assert.Equal(1.5f, Rulings.LootGates.SpillRadiusMetres);
        Assert.Equal(1.5f, airdrop.SpillRadiusMetres);
        Assert.Equal(5038u, Rulings.LootGates.LandingEffectId);
        Assert.Equal(5038u, airdrop.LandingEffectId);
        Assert.Equal(9215u, Rulings.LootGates.PlaneModelId);
        Assert.Equal(9215u, airdrop.PlaneModelId);
        Assert.Equal("A military crate is inbound.", Rulings.LootGates.InboundText);
        Assert.Equal(Rulings.LootGates.InboundText, airdrop.InboundText);
        Assert.Equal("The military crate has landed.", Rulings.LootGates.LandedText);
        Assert.Equal(Rulings.LootGates.LandedText, airdrop.LandedText);

        // the one gate with no third-party ancestry - and since D270 it is also the shipped
        // density for three of the five families, kept as its own constant because it is the FLAT
        // all-families override, which is a different statement from "no family sits below 0.27"
        Assert.Equal(0.27, Rulings.LootGates.PreWave8SpawnChance);
        Assert.Equal(0.27, LootDensityOptions.PreWave8SpawnChance);

        LootDensityOptions density = density0;
        Assert.Equal(4.0f, Rulings.LootGates.RoomRadiusMetres);
        Assert.Equal(4.0f, density.RoomRadiusMetres);
        Assert.Equal(2.0f, Rulings.LootGates.RoomHeightMetres);
        Assert.Equal(2.0f, density.RoomHeightMetres);
        Assert.Equal(6, Rulings.LootGates.MaxItemsPerRoom);
        Assert.Equal(6, density.MaxItemsPerRoom);
        Assert.Equal(2, Rulings.LootGates.MaxWeaponsPerRoom);
        Assert.Equal(2, density.MaxWeaponsPerRoom);
        Assert.Equal(["Backpack", "BodyArmor", "Helmet"], Rulings.LootGates.SingletonKindNames);
        Assert.True(density.SingletonKinds.Contains(LootItemKind.Backpack));
        Assert.True(density.SingletonKinds.Contains(LootItemKind.BodyArmor));
        Assert.True(density.SingletonKinds.Contains(LootItemKind.Helmet));

        LootStreamOptions stream = LootStreamOptions.Default;
        Assert.Equal(60f, Rulings.LootGates.StreamRadiusMetres);
        Assert.Equal(60f, stream.StreamRadiusMetres);
        Assert.Equal(90f, Rulings.LootGates.DespawnRadiusMetres);
        Assert.Equal(90f, stream.DespawnRadiusMetres);
        // D281: 384 -> 576 at D270's density; D286's two boxes per gun raised the peak to 612, so
        // 576 -> 704 by the same headroom rule.
        Assert.Equal(704, Rulings.LootGates.MaxLive);
        Assert.Equal(704, stream.MaxLive);
        Assert.Equal(64, Rulings.LootGates.MaxPerRestream);
        Assert.Equal(64, stream.MaxPerRestream);
        Assert.Equal(22_000, Rulings.LootGates.RestreamByteBudget);
        Assert.Equal(22_000, stream.RestreamByteBudget);
        Assert.Equal(64, Rulings.LootGates.MaxEvictionsPerRestream);
        Assert.Equal(64, stream.MaxEvictionsPerRestream);
        Assert.Equal(0.167f, Rulings.LootGates.RestreamFraction);
        Assert.Equal(0.167f, stream.RestreamFraction);
        Assert.Equal(500, Rulings.LootGates.RestreamIntervalMs);
        Assert.Equal(500, stream.RestreamIntervalMs);
        Assert.Equal(512, Rulings.LootGates.QueryWindow);
        Assert.Equal(512, stream.QueryWindow);
        Assert.Equal(2f, Rulings.LootGates.PanelRadiusMetres);
        Assert.Equal(2f, stream.PanelRadiusMetres);
        Assert.Equal(1f, Rulings.LootGates.PanelHeightMetres);
        Assert.Equal(1f, stream.PanelHeightMetres);
        Assert.Equal(32, Rulings.LootGates.PanelMaxRows);
        Assert.Equal(32, stream.PanelMaxRows);
        Assert.Equal(4f, Rulings.LootGates.PickupReachMetres);
        Assert.Equal(4f, stream.PickupReachMetres);
    }

    // ------------------------------------------------------------------------- loot roster (D20)

    [Fact]
    public void LootRosterShapeIsUnchangedByTheMoveIntoRulings()
    {
        // These are documentation constants: the roster itself ships in z2-loot-tables.json and
        // gen-rulings.py cross-checks every one of them against that file at generate time. What
        // this test adds is the pinned literal, so a silent edit to rulings/loot-roster.json
        // that ALSO edited the table would still be visible here.
        // D273 added the Crossbow: 10 -> 11 entries, 285 -> 305 weight.
        Assert.Equal(11, Rulings.LootRoster.Weapons01EntryCount);
        Assert.Equal(305, Rulings.LootRoster.Weapons01TotalWeight);
        // September 8 play-test: remove bandages from both natural medical pools.
        Assert.Equal(52, Rulings.LootRoster.Gear01EntryCount);
        Assert.Equal(498, Rulings.LootRoster.Gear01TotalWeight);
        Assert.Equal(7, Rulings.LootRoster.Backpack01EntryCount);
        Assert.Equal(100, Rulings.LootRoster.Backpack01TotalWeight);
        Assert.Equal(1, Rulings.LootRoster.FirstAidKit01EntryCount);
        Assert.Equal(40, Rulings.LootRoster.FirstAidKit01TotalWeight);
        Assert.Equal(7, Rulings.LootRoster.Ammo01EntryCount);
        Assert.Equal(120, Rulings.LootRoster.Ammo01TotalWeight);
        Assert.Equal(71, Rulings.LootRoster.TableItemIdCount);

        Assert.Equal(0.5f, Rulings.LootRoster.ClusterOffsetMetres);
        Assert.Equal(0.35f, Rulings.LootRoster.ClusterSecondBoxYawRadians);
        Assert.Equal(9, Rulings.LootRoster.ClusterCount);
        Assert.Equal([30, 30, 15, 7, 7, 6, 6, 5, 5], Rulings.LootRoster.ClusterRoundsPerBox);
        Assert.Equal(64f, Rulings.LootRoster.GridCellMetres);
        Assert.Equal(128, Rulings.LootRoster.GridDimension);

        // D70's coupling, stated as a test rather than as a comment: the cluster offset was
        // chosen so a gun and its box (both of them, before D272) fall inside the proximity ball.
        Assert.True(Rulings.LootRoster.ClusterOffsetMetres < Rulings.LootGates.PanelRadiusMetres);
    }

    // ----------------------------------------------------------------- vehicles plan (D35/D50)

    [Fact]
    public void VehiclePlanUsesTheOwnersZ1PopulationPolicy()
    {
        var plan = new VehicleSpawnPlanOptions();
        Assert.Equal(150, Rulings.VehiclesPlan.Count);
        Assert.Equal(150, plan.Count);
        Assert.Equal([1u, 2u, 5u, 3u], Rulings.VehiclesPlan.MixVehicleIds);
        Assert.Equal([40, 25, 20, 15], Rulings.VehiclesPlan.MixWeights);
        Assert.Equal([1u, 2u, 5u, 3u], plan.Mix.Select(share => share.VehicleId));
        Assert.Equal([40, 25, 20, 15], plan.Mix.Select(share => share.Weight));
        Assert.Equal(50f, Rulings.VehiclesPlan.MinimumSeparationMetres);
        Assert.Equal(50f, plan.MinimumSeparationMetres);
        Assert.Equal(12, Rulings.VehiclesPlan.MaxPerArea);
        Assert.Equal(12, plan.MaxPerArea);
        Assert.Equal(0f, Rulings.VehiclesPlan.GroundClearanceMetres);
        Assert.Equal(0f, plan.GroundClearanceMetres);

        VehicleStreamOptions stream = VehicleStreamOptions.Default;
        Assert.Equal(1700f, Rulings.VehiclesPlan.StreamRadiusMetres);
        Assert.Equal(1700f, stream.StreamRadiusMetres);
        Assert.Equal(2000f, Rulings.VehiclesPlan.DespawnRadiusMetres);
        Assert.Equal(2000f, stream.DespawnRadiusMetres);
        Assert.Equal(192, Rulings.VehiclesPlan.MaxLive);
        Assert.Equal(192, stream.MaxLive);
        Assert.Equal(32, Rulings.VehiclesPlan.MaxPerRestream);
        Assert.Equal(32, stream.MaxPerRestream);
        Assert.Equal(0.02f, Rulings.VehiclesPlan.RestreamFraction);
        Assert.Equal(0.02f, stream.RestreamFraction);
        Assert.Equal(1000, Rulings.VehiclesPlan.RestreamIntervalMs);
        Assert.Equal(1000, stream.RestreamIntervalMs);

        var fleet = new VehicleFleetOptions();
        // docs/117 §2.2 (D270): 100,000, and it is the CLIENT's - Resources.txt row 561
        // ResourceTypeHealth, INITIAL_VALUE and MAX_VALUE both 100,000. It replaced an unruled
        // 25,000, and the owner's Z1 gives every vehicle the same figure.
        Assert.Equal(100_000u, Rulings.VehiclesPlan.MaxHealth);
        Assert.Equal(100_000u, fleet.MaxHealth);
        Assert.Equal(10_000f, Rulings.VehiclesPlan.MaxFuel);
        Assert.Equal(10_000f, fleet.MaxFuel);
        Assert.Equal(0.75f, Rulings.VehiclesPlan.MinimumSpawnFuelFraction);
        Assert.Equal(0.75f, fleet.MinimumSpawnFuelFraction);
        Assert.Equal(0.75f, Rulings.VehiclesPlan.MaximumSpawnFuelFraction);
        Assert.Equal(0.75f, fleet.MaximumSpawnFuelFraction);
        Assert.Equal(8f, Rulings.VehiclesPlan.FuelBurnPerSecond);
        Assert.Equal(8f, fleet.FuelBurnPerSecond);
        Assert.Equal(2_500f, Rulings.VehiclesPlan.RefuelAmount);
        Assert.Equal(2_500f, fleet.RefuelAmount);
        Assert.Equal(60f, Rulings.VehiclesPlan.MaxPoseSpeedMetresPerSecond);
        Assert.Equal(60f, fleet.MaxPoseSpeedMetresPerSecond);
        // docs/117 §2.2 (D270): the owner's own 50 / 35 / 20 / 10 % ladder under D53, replacing
        // DamageLevelInfo's 75/50/25 - those are real client numbers, but they are the
        // HIT-INDICATOR levels, and taking his 100,000 bar without his ladder would have changed a
        // car's behaviour twice.
        Assert.Equal(0.50f, Rulings.VehiclesPlan.DamagedFraction);
        Assert.Equal(0.50f, fleet.DamagedFraction);
        Assert.Equal(0.35f, Rulings.VehiclesPlan.BadlyDamagedFraction);
        Assert.Equal(0.35f, fleet.BadlyDamagedFraction);
        Assert.Equal(0.20f, Rulings.VehiclesPlan.CrippledFraction);
        Assert.Equal(0.20f, fleet.CrippledFraction);
        Assert.Equal(0.10f, Rulings.VehiclesPlan.CriticalFraction);
        Assert.Equal(0.10f, fleet.CriticalFraction);

        // The knobs docs/117 added. The two Z1-graded ones are the owner's own gates; the rest are
        // Cranberry design, and the wreck damage is zero because this build ships no explosion
        // damage at all.
        var damage = new VehicleDamageOptions();
        Assert.Equal(500, Rulings.VehiclesPlan.CollisionBurstWindowMs);
        Assert.Equal(500, damage.CollisionBurstWindowMs);
        Assert.Equal(4_000, Rulings.VehiclesPlan.PostArrivalGraceMs);
        Assert.Equal(4_000, damage.PostArrivalGraceMs);
        Assert.Equal(3_000, Rulings.VehiclesPlan.FlipPulseIntervalMs);
        Assert.Equal(3_000, damage.FlipPulseIntervalMs);
        Assert.Equal(-0.25f, Rulings.VehiclesPlan.UpsideDownDotThreshold);
        Assert.Equal(-0.25f, damage.UpsideDownDotThreshold);
        Assert.Equal(0u, Rulings.VehiclesPlan.WreckOccupantDamage);
        Assert.Equal(0u, damage.WreckOccupantDamage);

        var boost = new VehicleBoostOptions();
        Assert.Equal(4f, Rulings.VehiclesPlan.FuelMultiplier);
        Assert.Equal(4f, boost.FuelMultiplier);
        Assert.Equal(0, Rulings.VehiclesPlan.TurboOnValue);
        Assert.Equal(0, boost.TurboOnValue);
    }

    // ------------------------------------ crafting (D47/D48/D112/D116, D256-D260)

    [Fact]
    public void CraftingIsUnchangedByTheMoveIntoRulings()
    {
        Assert.Equal(2423u, Rulings.Crafting.FieldBandageItemId);
        Assert.Equal(2423u, CraftingCatalog.FieldBandage);
        Assert.Equal(3375u, Rulings.Crafting.ProcoagulantItemId);
        Assert.Equal(3375u, CraftingCatalog.Procoagulant);
        Assert.Equal(3378u, Rulings.Crafting.MakeshiftArmorItemId);
        Assert.Equal(3378u, CraftingCatalog.MakeshiftArmor);
        Assert.Equal(93u, Rulings.Crafting.CraftedBackpackItemId);
        Assert.Equal(93u, CraftingCatalog.CraftedBackpack);
        Assert.Equal(23u, Rulings.Crafting.ScrapOfClothItemId);
        Assert.Equal(23u, CraftingCatalog.ScrapOfCloth);
        Assert.Equal(134u, Rulings.Crafting.DuctTapeItemId);
        Assert.Equal(134u, CraftingCatalog.DuctTape);
        Assert.Equal(3499u, Rulings.Crafting.ArmorScrapItemId);
        Assert.Equal(3499u, CraftingCatalog.ArmorScrap);
        Assert.Equal(3500u, Rulings.Crafting.CompositeFabricItemId);
        Assert.Equal(3500u, CraftingCatalog.CompositeFabric);

        // D256 - the six retail item ids
        Assert.Equal(2125u, Rulings.Crafting.SatchelItemId);
        Assert.Equal(2125u, CraftingCatalog.Satchel);
        Assert.Equal(138u, Rulings.Crafting.ExplosiveArrowItemId);
        Assert.Equal(138u, CraftingCatalog.ExplosiveArrow);
        Assert.Equal(1434u, Rulings.Crafting.FlamingArrowItemId);
        Assert.Equal(1434u, CraftingCatalog.FlamingArrow);
        Assert.Equal(14u, CraftingCatalog.MolotovCocktail);
        Assert.Equal(65u, CraftingCatalog.FragGrenade);
        Assert.Equal(112u, CraftingCatalog.WoodenArrow);
        Assert.Equal(1511u, CraftingCatalog.BuckshotShell);
        Assert.Equal(2424u, CraftingCatalog.FirstAidKit);
        Assert.Equal(11u, CraftingCatalog.Gunpowder);
        Assert.Equal(111u, CraftingCatalog.WoodStick);

        // the six recipes, in the order the friend server writes them onto the wire
        Assert.Equal(
            [2125u, 1434u, 3378u, 138u, 3375u, 2423u],
            Rulings.Crafting.RetailRecipeWireOrder);
        Assert.Equal(
            Rulings.Crafting.RetailRecipeWireOrder,
            CraftingCatalog.Recipes.Select(r => r.RecipeId));
        Assert.Equal([6u, 5u, 2u, 4u, 3u, 1u], Rulings.Crafting.RetailRecipeSortOrdinals);
        Assert.Equal(
            Rulings.Crafting.RetailRecipeSortOrdinals,
            CraftingCatalog.Recipes.Select(r => r.SortOrdinal));
        Assert.Equal([1u, 0u, 1u, 5u, 1u, 1u], Rulings.Crafting.RetailRecipeBundleCounts);
        Assert.Equal(
            Rulings.Crafting.RetailRecipeBundleCounts,
            CraftingCatalog.Recipes.Select(r => r.BundleCount));
        Assert.Equal([1u, 5u, 1u, 5u, 1u, 1u], Rulings.Crafting.RetailRecipeOutputCounts);
        Assert.Equal(
            Rulings.Crafting.RetailRecipeOutputCounts,
            CraftingCatalog.Recipes.Select(r => r.OutputCount));
        Assert.Equal(1u, Rulings.Crafting.RetailRecipeReserved);
        Assert.All(CraftingCatalog.Recipes, r => Assert.Equal(1u, r.Reserved));
        Assert.Equal(0xFFFFFFFFu, Rulings.Crafting.RetailComponentRecipeType);

        AssertIngredients(CraftingCatalog.FieldBandage, Rulings.Crafting.FieldBandageIngredients, [23u, 2u]);
        AssertIngredients(
            CraftingCatalog.Procoagulant, Rulings.Crafting.ProcoagulantIngredients, [2423u, 10u, 2424u, 1u]);
        AssertIngredients(
            CraftingCatalog.MakeshiftArmor,
            Rulings.Crafting.MakeshiftArmorIngredients,
            [134u, 1u, 3499u, 2u, 3500u, 4u]);
        AssertIngredients(CraftingCatalog.Satchel, Rulings.Crafting.SatchelIngredients, [23u, 6u]);
        AssertIngredients(
            CraftingCatalog.ExplosiveArrow,
            Rulings.Crafting.ExplosiveArrowIngredients,
            [65u, 1u, 112u, 5u, 134u, 1u, 1511u, 12u]);
        AssertIngredients(
            CraftingCatalog.FlamingArrow,
            Rulings.Crafting.FlamingArrowIngredients,
            [14u, 1u, 112u, 5u, 134u, 1u, 1511u, 5u]);

        // D257 - the craft cast bar's two numbers, neither of which the client carries
        Assert.Equal(1000, Rulings.Crafting.CraftMillisecondsPerUnit);
        Assert.All(CraftingCatalog.RetailRecipes, r => Assert.Equal(1000, r.BusyMilliseconds));
        Assert.Equal(10u, Rulings.Crafting.CraftInteractionAnimationId);

        // D258 - retail sends cf 03 twice at every completion
        Assert.Equal(2, Rulings.Crafting.InteractionStopRepeat);

        // the legacy four survive as the revert, with their wave-6 design numbers
        Assert.Equal([2423u, 3375u, 3378u, 93u], Rulings.Crafting.LegacyRecipeSortOrder);
        Assert.Equal(
            [2423u, 3375u, 3378u, 93u],
            CraftingCatalog.LegacyRecipes.OrderBy(r => r.SortOrdinal).Select(r => r.RecipeId));
        Assert.Equal(1u, Rulings.Crafting.LegacyRecipeOutputCount);
        Assert.All(CraftingCatalog.LegacyRecipes, r => Assert.Equal(1u, r.OutputCount));
        Assert.Equal(2000, Rulings.Crafting.FieldBandageBusyMs);
        Assert.Equal(3000, Rulings.Crafting.ProcoagulantBusyMs);
        Assert.Equal(5000, Rulings.Crafting.MakeshiftArmorBusyMs);
        Assert.Equal(5000, Rulings.Crafting.CraftedBackpackBusyMs);
        AssertLegacy(CraftingCatalog.FieldBandage, Rulings.Crafting.LegacyFieldBandageIngredients, [23u, 1u], 2000);
        AssertLegacy(
            CraftingCatalog.Procoagulant,
            Rulings.Crafting.LegacyProcoagulantIngredients,
            [2423u, 2u, 134u, 1u],
            3000);
        AssertLegacy(
            CraftingCatalog.MakeshiftArmor,
            Rulings.Crafting.LegacyMakeshiftArmorIngredients,
            [3499u, 2u, 3500u, 3u, 134u, 1u],
            5000);
        AssertLegacy(
            CraftingCatalog.CraftedBackpack,
            Rulings.Crafting.LegacyCraftedBackpackIngredients,
            [23u, 4u, 134u, 1u],
            5000);

        // the shred table: the busy window is the client's (D116), the class list is the client's
        // own join (D260), and only the 25008/25040 quantities are proven
        Assert.Equal(1000, Rulings.Crafting.ShredBusyMilliseconds);
        Assert.Equal(1000, ShredTable.BusyMilliseconds);
        uint[] classes = [25002, 25003, 25040, 25008, 25013, 25004, 25000, 25005, 25010, 16050, 16053];
        Assert.Equal(classes, Rulings.Crafting.ShredYieldItemClasses);
        Assert.Equal(
            [23u, 23u, 23u, 23u, 23u, 3500u, 3499u, 3500u, 23u, 23u, 11u],
            Rulings.Crafting.ShredYieldOutputItemIds);
        Assert.Equal(
            [4u, 4u, 1u, 1u, 1u, 2u, 2u, 2u, 1u, 1u, 1u],
            Rulings.Crafting.ShredYieldQuantities);
        for (int i = 0; i < classes.Length; i++)
        {
            Assert.Equal(Rulings.Crafting.ShredYieldOutputItemIds[i], ShredTable.Yields[classes[i]].ItemDefinitionId);
            Assert.Equal(Rulings.Crafting.ShredYieldQuantities[i], ShredTable.Yields[classes[i]].Quantity);
        }

        Assert.Equal(classes.Length, ShredTable.Yields.Count);
        Assert.Equal(7, ShredTable.LegacyYields.Count);

        Assert.Equal([1694u], Rulings.Crafting.ShredItemOverrideItemIds);
        Assert.Equal([111u], Rulings.Crafting.ShredItemOverrideOutputItemIds);
        Assert.Equal([1u], Rulings.Crafting.ShredItemOverrideQuantities);
        Assert.Equal(111u, ShredTable.ItemOverrides[1694].ItemDefinitionId);
        Assert.Equal(1u, ShredTable.ItemOverrides[1694].Quantity);

        // the fifteen display rows are the client's own ClientItemDefinitions columns
        Assert.Equal(15, Rulings.Crafting.RecipeDisplayItemIds.Length);
        Assert.Equal(new RecipeItemDisplay(22u, 1027u, 15859u), RecipeDisplayFacts.For(23u));
        Assert.Equal(new RecipeItemDisplay(21u, 12299u, 15863u), RecipeDisplayFacts.For(2423u));
        Assert.Equal(default, RecipeDisplayFacts.For(93u));

        CraftingOptions options = CraftingOptions.Default;
        Assert.True(Rulings.Crafting.SendRecipeList);
        Assert.True(options.SendRecipeList);
        // D289: stage 2 defaults ON — it is the only delivery that populates the crafting window.
        Assert.True(Rulings.Crafting.SendRecipesInSelfRecord);
        Assert.True(options.SendRecipesInSelfRecord);
        Assert.True(Rulings.Crafting.AllowCrafting);
        Assert.True(options.AllowCrafting);
        Assert.False(Rulings.Crafting.SendComponentCounts);
        Assert.False(options.SendComponentCounts);
        Assert.False(Rulings.Crafting.SentinelFields);
        Assert.False(options.SentinelFields);
        Assert.False(Rulings.Crafting.SeedIngredients);
        Assert.False(options.SeedIngredients);
        Assert.True(Rulings.Crafting.RetailRecipes);
        Assert.True(options.RetailRecipes);
        Assert.True(Rulings.Crafting.CraftCastBar);
        Assert.True(options.CraftCastBar);
        Assert.True(Rulings.Crafting.SendInteractionStop);
        Assert.True(options.SendInteractionStop);

        static void AssertIngredients(uint recipeId, uint[] ruling, uint[] expected)
        {
            Assert.Equal(expected, ruling);
            Assert.Equal(
                expected,
                CraftingCatalog.RetailById[recipeId].Ingredients
                    .SelectMany(i => new[] { i.ItemDefinitionId, i.Quantity }));
        }

        static void AssertLegacy(uint recipeId, uint[] ruling, uint[] expected, int busyMs)
        {
            Assert.Equal(expected, ruling);
            RecipeDefinition recipe = CraftingCatalog.LegacyById[recipeId];
            Assert.Equal(
                expected,
                recipe.Ingredients.SelectMany(i => new[] { i.ItemDefinitionId, i.Quantity }));
            Assert.Equal(busyMs, recipe.BusyMilliseconds);
        }
    }

    // ------------------------------------------------------------------------ starter (D37/D141)

    [Fact]
    public void StarterOutfitMatchesBasicAugustClothingRuling()
    {
        Assert.Equal(2324u, Rulings.Starter.OutfitGloves);
        Assert.Equal(2324u, SurvivorStarterOutfit.Gloves);
        Assert.Equal(3533u, Rulings.Starter.OutfitShirt);
        Assert.Equal(3533u, SurvivorStarterOutfit.Shirt);
        Assert.Equal(2177u, Rulings.Starter.OutfitPants);
        Assert.Equal(2177u, SurvivorStarterOutfit.Pants);
        Assert.Equal(3711u, Rulings.Starter.OutfitBoots);
        Assert.Equal(3711u, SurvivorStarterOutfit.Boots);

        Assert.Equal([2324u, 3533u, 2177u, 3711u], Rulings.Starter.OutfitOrder);
        Assert.Equal([2324u, 3533u, 2177u, 3711u], SurvivorStarterOutfit.DefaultItemDefinitionIds);
        Assert.Equal(
            [2324u, 3533u, 2177u, 3711u],
            SurvivorStarterOutfit.Pieces.Select(piece => piece.ItemDefinitionId));

        // The rest of the starter loadout (loadout 17, fists 85, binoculars 1542, 4 x 2423,
        // health 10,000, stamina 600) is declared in rulings/starter.json but LEFT AS A LITERAL:
        // PlayerInventory.cs, SurvivorSlots.cs and SelfRecord.cs belong to other lanes this wave
        // (docs/101 §5). These two pin the coupling the rulings file asserts across subsystems.
        Assert.Equal(17u, SurvivorLoadout.Id);
        Assert.Equal(Rulings.Gas.MaxHitpoints, 10_000u);
    }

    // ----------------------------------------------------------------------- the lane's own shape

    [Fact]
    public void TheCombatSubsystemsAreDeclaredNotYetRuled()
    {
        // Lane 2C covered the non-combat value classes only. Combat/*, Weapons/* and the medical
        // and armour models are another lane's rulings and have no rulings/*.json yet; the
        // generated file names them so the gap is visible from the code, and docs/101 §6 carries
        // the TODO.
        Assert.Equal(
            ["retail-damage", "medical", "armour", "weapon-definitions", "firemodes"],
            Rulings.SubsystemsNotYetRuled);
    }
}
