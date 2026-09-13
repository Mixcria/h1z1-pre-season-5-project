using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

/// <summary>
/// <b>The damage model itself</b> (docs/117 §2): the client's own flip and resistance numbers, the
/// owner's condition ladder, the burst rule that stops a ramp of reports killing anybody, and the
/// wreck.
///
/// <para>
/// Nothing here touches the wire — <c>VehicleFleet</c> decides and never sends, which is the whole
/// reason it can be tested without a connection. The orchestration is pinned separately in
/// <c>VehicleDamageIntegrationTests</c>.
/// </para>
/// </summary>
public sealed class VehicleDamageTests
{
    private static readonly VehicleRoster Roster = VehicleRoster.LoadDefault();

    private static (VehicleFleet Fleet, MatchVehicle Vehicle) Car(
        uint vehicleId = 1, VehicleFleetOptions? options = null)
    {
        var fleet = new VehicleFleet(Roster, options);
        var vehicle = new MatchVehicle(
            guid: 0xD000_0000_0000_0002,
            transientId: 4_242,
            definition: Roster.Require(vehicleId),
            position: Vector3.Zero,
            yaw: 0f,
            health: fleet.Options.MaxHealth,
            fuel: fleet.Options.MaxFuel);
        fleet.Add(vehicle);
        return (fleet, vehicle);
    }

    // ------------------------------------------------------------------ the client's numbers

    /// <summary>
    /// <c>Vehicles.txt UPSIDE_DOWN_DAMAGE_PULSE</c> — and the PickupTruck really is double. It also
    /// carries the only non-zero <c>COLLISION_RESISTANCE</c>, so the truck is the tough one in both
    /// directions.
    /// </summary>
    [Theory]
    [InlineData(1u, 5_000u, 280u, 0u)]
    [InlineData(2u, 10_000u, 600u, 100u)]
    [InlineData(3u, 5_000u, 850u, 0u)]
    [InlineData(5u, 5_000u, 100u, 0u)]
    public void TheFlipAndResistanceColumnsAreTheClientsOwn(
        uint vehicleId, uint pulse, uint undo, uint resistance)
    {
        Assert.Equal(pulse, AugustVehicleDamageFacts.UpsideDownDamagePulse(vehicleId));
        Assert.Equal(undo, AugustVehicleDamageFacts.UpsideDownUndo(vehicleId));
        Assert.Equal(resistance, AugustVehicleDamageFacts.CollisionResistance(vehicleId));
        Assert.Equal(1f, AugustVehicleDamageFacts.ImpactDamageMultiplier(vehicleId));
    }

    /// <summary>The parachute's pulse is 0, which is why an inverted canopy costs nothing.</summary>
    [Fact]
    public void TheParachuteHasNoFlipPulse()
    {
        Assert.Equal(0u, AugustVehicleDamageFacts.UpsideDownDamagePulse(13));
        Assert.Equal(0f, AugustVehicleDamageFacts.ImpactDamageMultiplier(13));
    }

    /// <summary>
    /// All sixteen <c>VEH_Damage_&lt;family&gt;_Stage0n</c> ids are real rows of the August client's
    /// own effect catalogue, with the matching names. That cross-check is what makes them client
    /// facts rather than a port.
    /// </summary>
    [Theory]
    [InlineData(1u, "OffRoader")]
    [InlineData(2u, "PickupTruck")]
    [InlineData(3u, "PoliceCar")]
    [InlineData(5u, "ATV")]
    public void EveryDamageStageEffectIsInTheAugustCatalogue(uint vehicleId, string family)
    {
        for (int stage = 1; stage <= 4; stage++)
        {
            uint id = AugustVehicleDamageFacts.DamageStageEffect(vehicleId, stage);
            AugustEffectDefinition? definition = AugustEffectCatalog.ById(id);

            Assert.NotNull(definition);
            Assert.Equal($"VEH_Damage_{family}_Stage{stage:00}", definition!.Value.Name);
        }

        Assert.Equal(0u, AugustVehicleDamageFacts.DamageStageEffect(vehicleId, 0));
        Assert.Equal(0u, AugustVehicleDamageFacts.DamageStageEffect(vehicleId, 5));
    }

    /// <summary>
    /// The condition bar is the client's own <c>Resources.txt</c> row 561 (type 1
    /// <c>ResourceTypeHealth</c>, MAX 100,000), and the shipped default matches it. This replaced an
    /// unruled 25,000.
    /// </summary>
    [Fact]
    public void TheConditionBarIsTheClientsOwnHundredThousand()
    {
        Assert.Equal(100_000u, AugustVehicleDamageFacts.ConditionMaxValue);
        Assert.Equal(561u, AugustVehicleDamageFacts.ConditionResourceId);
        Assert.Equal(1u, AugustVehicleDamageFacts.ConditionResourceType);
        Assert.Equal(100_000u, new VehicleFleetOptions().MaxHealth);
        Assert.Equal(100_000u, Rulings.VehiclesPlan.MaxHealth);
    }

    // ------------------------------------------------------------------------- the ladder

    /// <summary>
    /// The owner's own 50 / 35 / 20 / 10 % ladder on a 100,000 bar, adopted under D53 — 50,000,
    /// 35,000, 20,000 and 10,000. It replaced <c>DamageLevelInfo</c>'s 75/50/25, which are the
    /// hit-indicator levels rather than the degradation ones.
    /// </summary>
    [Fact]
    public void TheConditionLadderIsFiftyThirtyFiveTwentyTen()
    {
        var options = new VehicleFleetOptions();
        Assert.Equal(0.5f, options.DamagedFraction);
        Assert.Equal(0.35f, options.BadlyDamagedFraction);
        Assert.Equal(0.2f, options.CrippledFraction);
        Assert.Equal(0.1f, options.CriticalFraction);

        (VehicleFleet fleet, MatchVehicle car) = Car();

        Assert.Equal(VehicleCondition.Intact, car.ConditionUnder(options));
        Assert.Equal(0, car.DamageStageUnder(options));

        Assert.Equal(VehicleCondition.Damaged, fleet.ApplyDamage(car, 50_000));      // 50,000
        Assert.Equal(1, car.DamageStageUnder(options));

        Assert.Equal(VehicleCondition.BadlyDamaged, fleet.ApplyDamage(car, 15_000)); // 35,000
        Assert.Equal(2, car.DamageStageUnder(options));

        Assert.Equal(VehicleCondition.Crippled, fleet.ApplyDamage(car, 15_000));     // 20,000
        Assert.Equal(3, car.DamageStageUnder(options));

        Assert.Equal(VehicleCondition.Crippled, fleet.ApplyDamage(car, 10_000));     // 10,000
        Assert.Equal(4, car.DamageStageUnder(options));

        Assert.Equal(VehicleCondition.Destroyed, fleet.ApplyDamage(car, 10_000));    // 0
    }

    /// <summary>
    /// The four bands are reported as a stage effect exactly once each, on the crossing — a car that
    /// is shot twice inside one band must not play its smoke twice.
    /// </summary>
    [Fact]
    public void AStageEffectIsPlayedOnTheCrossingAndNotOnEveryHit()
    {
        (VehicleFleet fleet, MatchVehicle car) = Car();

        Assert.Equal(0u, fleet.Damage(car, 10_000).StageEffectId);      // 90,000, still intact
        Assert.Equal(182u, fleet.Damage(car, 45_000).StageEffectId);    // 45,000, stage 1
        Assert.Equal(0u, fleet.Damage(car, 5_000).StageEffectId);       // 40,000, still stage 1
        Assert.Equal(181u, fleet.Damage(car, 10_000).StageEffectId);    // 30,000, stage 2
    }

    /// <summary>
    /// <c>COLLISION_RESISTANCE</c> comes off a CRASH and nothing else: a bullet that ignored it
    /// would make the PickupTruck bullet-proof by 100 units a shot.
    /// </summary>
    [Fact]
    public void CollisionResistanceAppliesToACrashOnly()
    {
        (VehicleFleet truck, MatchVehicle car) = Car(vehicleId: 2);

        Assert.Equal(900u, truck.Damage(car, 1_000, collision: true).Charged);
        Assert.Equal(1_000u, truck.Damage(car, 1_000).Charged);

        (VehicleFleet offRoader, MatchVehicle other) = Car(vehicleId: 1);
        Assert.Equal(1_000u, offRoader.Damage(other, 1_000, collision: true).Charged);
    }

    /// <summary>A crash under the truck's resistance costs it nothing at all.</summary>
    [Fact]
    public void ACrashUnderTheTrucksResistanceCostsNothing()
    {
        (VehicleFleet fleet, MatchVehicle car) = Car(vehicleId: 2);
        VehicleDamageOutcome outcome = fleet.Damage(car, 60, collision: true);

        Assert.Equal(0u, outcome.Charged);
        Assert.False(outcome.Moved);
        Assert.Equal(fleet.Options.MaxHealth, car.Health);
    }

    // -------------------------------------------------------------------------- the wreck

    [Fact]
    public void AWreckThrowsItsOccupantsOutAndKeepsTheirGuids()
    {
        (VehicleFleet fleet, MatchVehicle car) = Car();
        fleet.TryEnter(car.Guid, 0x1001, 0, nowMs: 0, out _, out _);
        // Past the client's own 1,000 ms interaction cooldown, which the fleet enforces on entry.
        fleet.TryEnter(car.Guid, 0x1002, 1, nowMs: 5_000, out _, out _);

        VehicleDamageOutcome outcome = fleet.Damage(car, uint.MaxValue);

        Assert.True(outcome.Destroyed);
        Assert.Equal(VehicleCondition.Destroyed, outcome.Condition);
        Assert.Equal(2, outcome.EvictedOccupants.Count);
        Assert.Contains(0x1001ul, outcome.EvictedOccupants);
        Assert.Contains(0x1002ul, outcome.EvictedOccupants);
        Assert.True(car.IsEmpty);
        Assert.Equal(0ul, car.OwnerGuid);
        Assert.False(car.EngineOn);
        Assert.False(fleet.TryGetForOccupant(0x1001, out _));

        // The wreck stays: the fleet still holds it, and it is still findable by guid.
        Assert.True(fleet.TryGet(car.Guid, out _));
        Assert.Equal(1, fleet.Count);
    }

    /// <summary>A wreck cannot be climbed back into.</summary>
    [Fact]
    public void AWreckRefusesAMount()
    {
        (VehicleFleet fleet, MatchVehicle car) = Car();
        fleet.Damage(car, uint.MaxValue);

        Assert.Equal(
            VehicleActionResult.Destroyed,
            fleet.TryEnter(car.Guid, 0x1001, 0, nowMs: 10_000, out _, out _));
    }

    // ---------------------------------------------------------------------- the burst rule

    /// <summary>
    /// <b>The rule the recovered samples demand.</b> The client reports one continuing impact as a
    /// ramp with a running total — 7, 14, 22, 30 — so inside the window each report costs only what
    /// it adds to the peak. Charging all four would bill 73 for an impact worth 30.
    /// </summary>
    [Fact]
    public void ARampOfReportsCostsItsPeakOnce()
    {
        CollisionBurst burst = CollisionBurst.Fresh;

        Assert.Equal(7u, burst.Charge(7, 1_000, 500));
        Assert.Equal(7u, burst.Charge(14, 1_100, 500));
        Assert.Equal(8u, burst.Charge(22, 1_200, 500));
        Assert.Equal(8u, burst.Charge(30, 1_300, 500));
        Assert.Equal(30u, burst.Peak);
    }

    /// <summary>A report that dips below the peak inside the window costs nothing.</summary>
    [Fact]
    public void ADipInsideTheWindowCostsNothing()
    {
        CollisionBurst burst = CollisionBurst.Fresh;

        Assert.Equal(100u, burst.Charge(100, 1_000, 500));
        Assert.Equal(0u, burst.Charge(60, 1_100, 500));
        Assert.Equal(0u, burst.Charge(100, 1_200, 500));
        Assert.Equal(5u, burst.Charge(105, 1_300, 500));
    }

    /// <summary>Past the window the next impact is a new one and costs its whole value.</summary>
    [Fact]
    public void PastTheWindowTheNextImpactStartsAgain()
    {
        CollisionBurst burst = CollisionBurst.Fresh;

        Assert.Equal(100u, burst.Charge(100, 1_000, 500));
        Assert.Equal(40u, burst.Charge(40, 1_600, 500));
        Assert.Equal(40u, burst.Peak);
    }

    /// <summary>
    /// The "never happened" sentinel is compared and never subtracted: <c>now - long.MinValue</c>
    /// overflows negative, which would make the very first report of a match read as mid-burst.
    /// </summary>
    [Fact]
    public void TheFirstReportOfAMatchIsNotInsideAWindow()
    {
        CollisionBurst burst = CollisionBurst.Fresh;

        Assert.Equal(CollisionBurst.Never, burst.LastMs);
        Assert.Equal(12_345u, burst.Charge(12_345, nowMs: 5, windowMs: 500));
    }

    // ------------------------------------------------------------------- the flip detector

    /// <summary>
    /// Upright is +1, on its side is 0, on its roof is −1 — and the shipped −0.25 threshold sits
    /// well past vertical, so a car on a steep bank is not "flipped".
    /// </summary>
    [Fact]
    public void TheFlipDetectorReadsTheClientsOwnRotation()
    {
        float threshold = new VehicleDamageOptions().UpsideDownDotThreshold;

        Assert.Equal(1f, VehicleFlipDetector.UpDot(Quaternion.Identity), 4);
        Assert.False(VehicleFlipDetector.IsUpsideDown(Quaternion.Identity, threshold));

        Quaternion onItsSide = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        Assert.Equal(0f, VehicleFlipDetector.UpDot(onItsSide), 4);
        Assert.False(VehicleFlipDetector.IsUpsideDown(onItsSide, threshold));

        Quaternion onItsRoof = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);
        Assert.Equal(-1f, VehicleFlipDetector.UpDot(onItsRoof), 4);
        Assert.True(VehicleFlipDetector.IsUpsideDown(onItsRoof, threshold));

        // A 30-degree bank is not a flip.
        Quaternion banked = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6f);
        Assert.False(VehicleFlipDetector.IsUpsideDown(banked, threshold));
    }

    /// <summary>Yaw alone never flips a car, however far it spins.</summary>
    [Fact]
    public void YawIsNotAFlip()
    {
        float threshold = new VehicleDamageOptions().UpsideDownDotThreshold;
        for (int degrees = 0; degrees < 360; degrees += 15)
        {
            Quaternion spun = Quaternion.CreateFromAxisAngle(
                Vector3.UnitY, degrees * MathF.PI / 180f);
            Assert.False(VehicleFlipDetector.IsUpsideDown(spun, threshold));
        }
    }

    // --------------------------------------------------------------------- the flip pulses

    [Fact]
    public void AnUprightCarIsNeverPulsed()
    {
        (VehicleFleet fleet, MatchVehicle car) = Car();
        var options = new VehicleDamageOptions();

        Assert.Empty(fleet.PulseUpsideDown(1_000_000, options));
        Assert.Equal(fleet.Options.MaxHealth, car.Health);
    }

    /// <summary>
    /// One pulse per period, and the amount is the client's own. An OffRoader on its roof takes
    /// 5,000 a pulse, so it is a wreck after twenty of them.
    /// </summary>
    [Fact]
    public void AFlippedCarIsPulsedOncePerPeriodAtTheClientsOwnAmount()
    {
        (VehicleFleet fleet, MatchVehicle car) = Car();
        var options = new VehicleDamageOptions { FlipPulseIntervalMs = 3_000, FlipInitialGraceMs = 6000, FlipDamageMultiplier = 1 };
        Quaternion onItsRoof = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);

        Assert.True(fleet.NoteAttitude(car, onItsRoof, options.UpsideDownDotThreshold));
        Assert.True(car.UpsideDown);

        // The flip itself starts the clock, so nothing is owed immediately.
        long start = car.LastFlipPulseMs;
        Assert.Empty(fleet.PulseUpsideDown(start + 5_999, options));
        Assert.Equal(100_000u, car.Health);

        VehicleDamageOutcome first = Assert.Single(fleet.PulseUpsideDown(start + 6_000, options));
        Assert.Equal(5_000u, first.Charged);
        Assert.Equal(95_000u, car.Health);

        Assert.Empty(fleet.PulseUpsideDown(start + 7_000, options));
        Assert.Single(fleet.PulseUpsideDown(start + 9_000, options));
        Assert.Equal(90_000u, car.Health);
    }

    /// <summary>The PickupTruck's pulse is double, so it wrecks in half the time.</summary>
    [Fact]
    public void ThePickupTruckPulsesTwiceAsHard()
    {
        (VehicleFleet fleet, MatchVehicle truck) = Car(vehicleId: 2);
        var options = new VehicleDamageOptions();
        Quaternion onItsRoof = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);

        fleet.NoteAttitude(truck, onItsRoof, options.UpsideDownDotThreshold);
        VehicleDamageOutcome pulse = Assert.Single(
            fleet.PulseUpsideDown(truck.LastFlipPulseMs + options.FlipInitialGraceMs, options));

        Assert.Equal(1_000u, pulse.Charged);
    }

    /// <summary>Righting the car stops the pulses; there is no debt for the time it spent over.</summary>
    [Fact]
    public void RightingTheCarStopsThePulses()
    {
        (VehicleFleet fleet, MatchVehicle car) = Car();
        var options = new VehicleDamageOptions();
        Quaternion onItsRoof = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);

        fleet.NoteAttitude(car, onItsRoof, options.UpsideDownDotThreshold);
        long start = car.LastFlipPulseMs;
        Assert.Single(fleet.PulseUpsideDown(start + options.FlipInitialGraceMs, options));

        Assert.True(fleet.NoteAttitude(car, Quaternion.Identity, options.UpsideDownDotThreshold));
        Assert.False(car.UpsideDown);
        Assert.Empty(fleet.PulseUpsideDown(start + 60_000, options));
        Assert.Equal(99_500u, car.Health);
    }

    /// <summary>An attitude that has not changed reports no change, so the log stays quiet.</summary>
    [Fact]
    public void AnUnchangedAttitudeIsNotAnEvent()
    {
        (VehicleFleet fleet, MatchVehicle car) = Car();
        float threshold = new VehicleDamageOptions().UpsideDownDotThreshold;

        Assert.False(fleet.NoteAttitude(car, Quaternion.Identity, threshold));

        Quaternion onItsRoof = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);
        Assert.True(fleet.NoteAttitude(car, onItsRoof, threshold));
        Assert.False(fleet.NoteAttitude(car, onItsRoof, threshold));
    }

    /// <summary>The flip switch off is a complete revert: the attitude is still tracked, nothing is charged.</summary>
    [Fact]
    public void TheFlipSwitchOffPulsesNothing()
    {
        (VehicleFleet fleet, MatchVehicle car) = Car();
        var off = new VehicleDamageOptions { Flip = false };
        Quaternion onItsRoof = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);

        fleet.NoteAttitude(car, onItsRoof, off.UpsideDownDotThreshold);
        Assert.Empty(fleet.PulseUpsideDown(car.LastFlipPulseMs + 60_000, off));
        Assert.Equal(100_000u, car.Health);
    }

    // ------------------------------------------------------------------- the dismount guard

    /// <summary>
    /// The speed the dismount guard reads is measured from two poses the owner authored, not
    /// assumed. Fifty metres in one second is well over the client's own
    /// <c>Vehicle.DefaultMaxDismountSpeed</c> of 12.
    /// </summary>
    [Fact]
    public void TheOwnersPoseStreamMeasuresSpeedWithoutBlockingFullSpeedDismount()
    {
        (VehicleFleet fleet, MatchVehicle car) = Car();
        fleet.TryEnter(car.Guid, 0x1001, 0, nowMs: 0, out _, out _);

        Assert.True(fleet.TryApplyOwnerPose(
            car.TransientId, 0x1001, Vector3.Zero, 0f, nowMs: 1_000, out _));
        Assert.True(fleet.TryApplyOwnerPose(
            car.TransientId, 0x1001, new Vector3(50f, 0f, 0f), 0f, nowMs: 2_000, out _));

        Assert.Equal(50f, car.LastSpeed, 2);
        Assert.Equal(12f, car.Definition.MaxDismountSpeed);
        Assert.Equal(
            VehicleActionResult.Ok,
            fleet.TryExit(0x1001, nowMs: 4_000, car.LastSpeed, out _, out _));

        Assert.True(fleet.TryApplyOwnerPose(
            car.TransientId, 0x1001, new Vector3(51f, 0f, 0f), 0f, nowMs: 3_000, out _));
        Assert.Equal(1f, car.LastSpeed, 2);
        Assert.Equal(
            VehicleActionResult.NotMounted,
            fleet.TryExit(0x1001, nowMs: 5_000, car.LastSpeed, out _, out _));
    }

    // ---------------------------------------------------------------------- the switch rows

    /// <summary>
    /// The shipped defaults, restated so a change to one of them is a deliberate edit to this file
    /// rather than a silent behaviour change: damage ON (before this lane a car could not be hurt at
    /// all), the burst window and the arrival grace at the owner's own values, and the wreck damage
    /// at zero because this build ships no explosion damage.
    /// </summary>
    [Fact]
    public void TheShippedDamageSwitches()
    {
        var options = new VehicleDamageOptions();

        Assert.True(options.Enabled);
        Assert.True(options.Bullets);
        Assert.True(options.Flip);
        Assert.True(options.SuppressFallDamageUnderCanopy);
        Assert.Equal(500, options.CollisionBurstWindowMs);
        Assert.Equal(4_000, options.PostArrivalGraceMs);
        Assert.Equal(3_000, options.FlipPulseIntervalMs);
        Assert.Equal(-0.25f, options.UpsideDownDotThreshold);
        Assert.Equal(0u, options.WreckOccupantDamage);
    }

    /// <summary>
    /// <c>09 1d Command.PlayDialogEffect</c>, the carrier for an effect that hangs on an entity:
    /// 15 bytes, and the <c>0x09</c> family's subs are <c>u16</c> exactly as
    /// <c>09 2d InteractionString</c> already writes them.
    /// </summary>
    [Fact]
    public void PlayDialogEffectIsFifteenBytes()
    {
        using var writer = new PacketWriter();
        new PlayDialogEffect(0xD000_0000_0000_0002, 182).WriteTo(writer);
        byte[] bytes = writer.Written.ToArray();

        Assert.Equal(PlayDialogEffect.Length, bytes.Length);
        Assert.Equal(15, PlayDialogEffect.Length);
        Assert.Equal(
            Convert.FromHexString("09" + "1D00" + "02000000000000D0" + "B6000000"),
            bytes);
        Assert.Equal(ZoneOpcodes.CommandBase, PlayDialogEffect.Opcode);
    }
}
