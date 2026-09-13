using System.Numerics;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

/// <summary>
/// Possession and the referee rules — the part of a vehicle the server actually owns.
///
/// <para>The client simulates the car (docs/43 §0.1), so nothing here integrates a pose. What it
/// does is decide who may sit where, apply the two cooldowns and the dismount-speed limit the
/// <b>client itself enforces</b> with the build's own constants, keep the authoritative last-known
/// pose, and refuse one no legitimate drive could have produced.</para>
/// </summary>
public sealed class VehicleFleetTests
{
    private static readonly Lazy<VehicleRoster> SharedRoster = new(VehicleRoster.LoadDefault);

    private const ulong Driver = 1001;
    private const ulong Passenger = 1002;
    private const ulong VehicleGuid = 0x2000;
    private const uint TransientId = 4242;

    [Fact]
    public void CoastingOwnershipSurvivesSleepAndEndsOnReentryOrDisconnect()
    {
        var fleet = FleetWith(1, out var car);
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 1000, out _, out _));
        Assert.Equal(VehicleActionResult.Ok, fleet.TryExit(Driver, 3000, 8, out _, out _));
        Assert.False(car.CoastCanRelease(7999));
        Assert.False(car.CoastCanRelease(8000));
        Assert.False(car.CoastCanRelease(3600000));
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Passenger, 0, 4000, out _, out _));
        Assert.Equal(0ul, car.CoastingOwnerGuid);
        Assert.False(car.CoastCanRelease(20000));
        Assert.False(fleet.TryApplyOwnerPose(TransientId, Driver, car.Position, 0, 5000, out _));
        Assert.True(fleet.TryApplyOwnerPose(TransientId, Passenger, car.Position, 0, 5000, out _));
        Assert.Equal(VehicleActionResult.Ok, fleet.TryExit(Passenger, 6000, 0, out _, out _));
        fleet.Evict(Passenger); // already unseated, but still responsible for physics
        Assert.Equal(0ul, car.CoastingOwnerGuid);
    }

    [Fact]
    public void FallingCarDoesNotCountAsSettledJustBecauseItsHorizontalSpeedIsZero()
    {
        var fleet = FleetWith(1, out var car);
        fleet.TryEnter(VehicleGuid, Driver, 0, 1000, out _, out _);
        fleet.TryApplyOwnerPose(TransientId, Driver, new(10, 20, 30), 0, 2000, out _);
        fleet.TryExit(Driver, 3000, 0, out _, out _);
        Assert.True(fleet.TryApplyOwnerPose(TransientId, Driver, new(10, 15, 30), 0, 4000, out _));
        Assert.True(fleet.TryApplyOwnerPose(TransientId, Driver, new(10, 10, 30), 0, 5000, out _));
        Assert.False(car.CoastCanRelease(6000));
    }

    [Fact]
    public void DifferentPassengerTakingTheWheelCannotSpendParkedTimeOnASpawnJump()
    {
        var fleet = FleetWith(3, out var car);
        fleet.TryEnter(VehicleGuid, Driver, 0, 1000, out _, out _);
        Assert.True(fleet.TryApplyOwnerPose(TransientId, Driver, new(110, 20, 30), 0, 2000, out _));
        fleet.TryExit(Driver, 3000, 0, out _, out _);
        fleet.TryEnter(VehicleGuid, Passenger, 1, 5000, out _, out _);
        Assert.Equal(VehicleActionResult.Ok,
            fleet.TryChangeSeat(Passenger, 0, 120000, 0, out _));
        Assert.Equal(120000, car.LastPoseMs);
        Assert.False(fleet.TryApplyOwnerPose(TransientId, Passenger, new(10, 20, 30), 0, 120000, out _));
        Assert.False(fleet.TryApplyOwnerPose(TransientId, Passenger, new(10, 20, 30), 0, 120017, out _));
        Assert.True(fleet.TryApplyOwnerPose(TransientId, Passenger, new(110, 20, 30), 0, 120034, out _));
    }

    [Fact]
    public void SameSimulatorReturningToTheWheelKeepsItsPoseClock()
    {
        var fleet = FleetWith(3, out var car);
        fleet.TryEnter(VehicleGuid, Driver, 0, 1000, out _, out _);
        Assert.True(fleet.TryApplyOwnerPose(TransientId, Driver, new(110, 20, 30), 0, 2000, out _));
        Assert.Equal(VehicleActionResult.Ok, fleet.TryChangeSeat(Driver, 1, 3000, 0, out _));
        Assert.Equal(VehicleActionResult.Ok, fleet.TryChangeSeat(Driver, 0, 120000, 0, out _));
        Assert.Equal(2000, car.LastPoseMs);
    }

    private static VehicleFleet FleetWith(uint vehicleId, out MatchVehicle vehicle, VehicleFleetOptions? options = null)
    {
        VehicleRoster roster = SharedRoster.Value;
        var fleet = new VehicleFleet(roster, options);
        vehicle = new MatchVehicle(
            VehicleGuid, TransientId, roster.Require(vehicleId), new Vector3(10, 20, 30), yaw: 0f,
            health: fleet.Options.MaxHealth, fuel: fleet.Options.MaxFuel);
        fleet.Add(vehicle);
        return fleet;
    }

    // --- entering ------------------------------------------------------------------------------

    /// <summary>
    /// Taking the wheel is what makes the client simulate the car: the owner guid is the <i>only</i>
    /// thing <c>FUN_140e6e8b0</c> and <c>FUN_140b04c90</c> key on, so it must follow the driver seat
    /// and nothing else.
    /// </summary>
    [Fact]
    public void TakingTheDriverSeatMakesTheRiderTheOwnerAndAPassengerDoesNot()
    {
        VehicleFleet fleet = FleetWith(1, out MatchVehicle vehicle);

        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out int seat));
        Assert.Equal(0, seat);
        Assert.Equal(Driver, vehicle.OwnerGuid);
        Assert.Equal(Driver, vehicle.DriverGuid);
        Assert.Equal(0, vehicle.SeatOf(Driver));

        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Passenger, 3, 5_000, out _, out seat));
        Assert.Equal(3, seat);
        Assert.Equal(Driver, vehicle.OwnerGuid);      // still the driver's car
        Assert.Equal(2, vehicle.OccupantCount);

        Assert.True(fleet.TryGetForOccupant(Passenger, out MatchVehicle? found));
        Assert.Same(vehicle, found);
    }

    [Fact]
    public void ASeatIsRefusedWhenItIsTakenAndAnUnknownSeatIsRefusedOutright()
    {
        VehicleFleet fleet = FleetWith(5, out MatchVehicle vehicle);   // ATV: two seats

        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _));
        Assert.Equal(
            VehicleActionResult.SeatOccupied,
            fleet.TryEnter(VehicleGuid, Passenger, 0, 10_000, out _, out _));
        Assert.Equal(
            VehicleActionResult.NoSuchSeat,
            fleet.TryEnter(VehicleGuid, Passenger, 4, 10_000, out _, out _));
        Assert.Equal(
            VehicleActionResult.AlreadyMounted,
            fleet.TryEnter(VehicleGuid, Driver, 1, 10_000, out _, out _));
        Assert.Equal(
            VehicleActionResult.NoSuchVehicle,
            fleet.TryEnter(VehicleGuid + 1, Passenger, 0, 10_000, out _, out _));

        // A negative index means "first free seat", driver first.
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Passenger, -1, 10_000, out _, out int seat));
        Assert.Equal(1, seat);
        Assert.Equal(-1, vehicle.FirstFreeSeat());
    }

    /// <summary>
    /// <c>VehicleInteractionCooldownMs</c> is 1000 in the build's own <c>StringHashToValue.txt</c>
    /// and the client refuses with "Too early to exit vehicle" inside it — so the server must refuse
    /// on the same number, or the two disagree about whether the player is in the car.
    /// </summary>
    [Fact]
    public void TheClientsOwnInteractionCooldownIsEnforcedOnEntryAndExit()
    {
        VehicleFleet fleet = FleetWith(1, out _);
        int cooldown = SharedRoster.Value.Constants.InteractionCooldownMs;
        Assert.Equal(1000, cooldown);

        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 10_000, out _, out _));
        Assert.Equal(
            VehicleActionResult.Cooldown,
            fleet.TryExit(Driver, 10_000 + cooldown - 1, speed: 0f, out _, out _));
        Assert.Equal(
            VehicleActionResult.Ok,
            fleet.TryExit(Driver, 10_000 + cooldown, speed: 0f, out _, out _));
    }

    // --- leaving -------------------------------------------------------------------------------

    /// <summary>
    /// The driver leaving hands the car back to nobody: owner cleared and the engine stopped, which
    /// is what the release burst (<c>11 0039</c> + <c>0f 3b(0,0)</c>) tells every client. The car
    /// itself stays in the world — unlike the parachute, there is no <c>RemovePlayer</c>.
    /// </summary>
    [Fact]
    public void TheDriverLeavingReleasesOwnershipButKeepsTheCar()
    {
        VehicleFleet fleet = FleetWith(1, out MatchVehicle vehicle);
        fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _);
        vehicle.EngineOn = true;

        Assert.Equal(VehicleActionResult.Ok, fleet.TryExit(Driver, 5_000, speed: 0f, out MatchVehicle? left, out int seat));

        Assert.Same(vehicle, left);
        Assert.Equal(0, seat);
        Assert.Equal(0UL, vehicle.OwnerGuid);
        Assert.False(vehicle.EngineOn);
        Assert.True(vehicle.IsEmpty);
        Assert.Equal(1, fleet.Count);                       // the car is still there
        Assert.False(fleet.TryGetForOccupant(Driver, out _));
        Assert.Equal(VehicleActionResult.NotMounted, fleet.TryExit(Driver, 9_000, 0f, out _, out _));
    }

    /// <summary>
    /// Authored metadata stays intact, while the owner's full-speed exit policy takes precedence.
    /// </summary>
    [Fact]
    public void ExitingAtFullSpeedKeepsTheVehicleCoasting()
    {
        VehicleFleet fleet = FleetWith(3, out MatchVehicle vehicle);
        fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _);

        Assert.Equal(12f, vehicle.Definition.MaxDismountSpeed);
        Assert.Equal(VehicleActionResult.Ok, fleet.TryExit(Driver, 5_000, speed: 60f, out _, out _));
        Assert.Equal(0ul, vehicle.DriverGuid);
        Assert.Equal(Driver, vehicle.CoastingOwnerGuid);
    }

    // --- seat changes --------------------------------------------------------------------------

    /// <summary>
    /// Both of the client's own seat-change guards — the 250 ms cooldown and "You cannot switch
    /// seats while the vehicle is moving." — plus the ownership handover in both directions.
    /// </summary>
    [Fact]
    public void SeatChangesHonourTheSwapCooldownStillnessAndCarryOwnershipBothWays()
    {
        VehicleFleet fleet = FleetWith(1, out MatchVehicle vehicle);
        fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _);
        vehicle.EngineOn = true;

        Assert.Equal(250, SharedRoster.Value.Constants.SeatSwapCooldownMs);
        Assert.Equal(
            VehicleActionResult.TooFast,
            fleet.TryChangeSeat(Driver, 2, 1_000, speed: 0.5f, out _));

        // Driver → passenger: ownership is given up and the engine stops with it.
        Assert.Equal(VehicleActionResult.Ok, fleet.TryChangeSeat(Driver, 2, 1_000, speed: 0f, out _));
        Assert.Equal(2, vehicle.SeatOf(Driver));
        Assert.Equal(0UL, vehicle.OwnerGuid);
        Assert.False(vehicle.EngineOn);

        Assert.Equal(
            VehicleActionResult.Cooldown,
            fleet.TryChangeSeat(Driver, 0, 1_000 + 249, speed: 0f, out _));

        // Passenger → driver: ownership comes back.
        Assert.Equal(VehicleActionResult.Ok, fleet.TryChangeSeat(Driver, 0, 1_000 + 250, speed: 0f, out _));
        Assert.Equal(Driver, vehicle.OwnerGuid);

        fleet.TryEnter(VehicleGuid, Passenger, 1, 100_000, out _, out _);
        Assert.Equal(
            VehicleActionResult.SeatOccupied,
            fleet.TryChangeSeat(Passenger, 0, 200_000, speed: 0f, out _));
        Assert.Equal(
            VehicleActionResult.NotMounted,
            fleet.TryChangeSeat(9999, 0, 200_000, speed: 0f, out _));
    }

    // --- the referee ---------------------------------------------------------------------------

    /// <summary>
    /// The pose is the owner's, and only the owner's: a pose from a passenger, from a bystander or
    /// for an unowned parked car is refused, because the owner guid is the whole of the client-side
    /// simulation contract.
    /// </summary>
    [Fact]
    public void OnlyTheOwningClientsPoseIsAccepted()
    {
        VehicleFleet fleet = FleetWith(1, out MatchVehicle vehicle);

        // Parked and unowned: nobody may move it.
        Assert.False(fleet.TryApplyOwnerPose(TransientId, Driver, new Vector3(11, 20, 30), 0f, 100, out _));

        fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _);
        fleet.TryEnter(VehicleGuid, Passenger, 1, 5_000, out _, out _);

        Assert.False(fleet.TryApplyOwnerPose(TransientId, Passenger, new Vector3(11, 20, 30), 0f, 6_000, out _));
        Assert.False(fleet.TryApplyOwnerPose(TransientId + 1, Driver, new Vector3(11, 20, 30), 0f, 6_000, out _));

        Assert.True(fleet.TryApplyOwnerPose(TransientId, Driver, new Vector3(11, 20, 30), 1.5f, 6_000, out MatchVehicle? moved));
        Assert.Same(vehicle, moved);
        Assert.Equal(new Vector3(11, 20, 30), vehicle.Position);
        Assert.Equal(1.5f, vehicle.Yaw);
        Assert.Equal(6_000L, vehicle.LastPoseMs);
    }

    /// <summary>
    /// The client is not trusted: a delta no legitimate drive could produce is refused, the previous
    /// pose is kept rather than snapped, and the reject is counted. It never disconnects — a stall
    /// in the owner's uplink looks exactly like a small teleport.
    /// </summary>
    [Fact]
    public void AnImplausiblePoseIsRefusedWithoutLosingTheLastGoodOne()
    {
        var options = new VehicleFleetOptions { MaxPoseSpeedMetresPerSecond = 60f };
        VehicleFleet fleet = FleetWith(1, out MatchVehicle vehicle, options);
        fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _);

        Assert.True(fleet.TryApplyOwnerPose(TransientId, Driver, new Vector3(0, 20, 0), 0f, 1_000, out _));

        // 100 m in a second: over the bound.
        Assert.False(fleet.TryApplyOwnerPose(TransientId, Driver, new Vector3(100, 20, 0), 0f, 2_000, out _));
        Assert.Equal(new Vector3(0, 20, 0), vehicle.Position);
        Assert.Equal(1, vehicle.RejectedPoses);

        // 50 m in a second: inside it.
        Assert.True(fleet.TryApplyOwnerPose(TransientId, Driver, new Vector3(50, 20, 0), 0f, 2_000, out _));
        Assert.Equal(new Vector3(50, 20, 0), vehicle.Position);
        Assert.Equal(1, vehicle.RejectedPoses);
    }

    // --- fuel and condition --------------------------------------------------------------------

    /// <summary>
    /// Fuel is a server constant end to end — the client's <c>BURN_PER_MSEC</c> is 0 on every
    /// <c>ResourceTypeFuel</c> row — but the tank size is the client's own 10000.
    /// </summary>
    [Fact]
    public void FuelBurnsOnlyWithTheEngineRunningAndStallsTheCarAtEmpty()
    {
        var options = new VehicleFleetOptions { FuelBurnPerSecond = 100f, MaxFuel = 1_000f };
        VehicleFleet fleet = FleetWith(1, out MatchVehicle vehicle, options);
        vehicle.Fuel = 250f;

        Assert.Empty(fleet.BurnFuel(10f));                 // engine off: nothing burns
        Assert.Equal(250f, vehicle.Fuel);

        vehicle.EngineOn = true;
        Assert.Empty(fleet.BurnFuel(1f));
        Assert.Equal(150f, vehicle.Fuel);

        IReadOnlyList<MatchVehicle> stalled = fleet.BurnFuel(2f);
        Assert.Same(vehicle, Assert.Single(stalled));
        Assert.Equal(0f, vehicle.Fuel);
        Assert.False(vehicle.EngineOn);

        Assert.Equal(1_000f, fleet.Refuel(vehicle, 5_000f));   // clamped to the tank
        Assert.Equal(options.MaxFuel, vehicle.Fuel);
    }

    /// <summary>
    /// A parked car is found part-full, seeded on its anchor so the same match seed finds the same
    /// tank in the same driveway.
    /// </summary>
    [Fact]
    public void SpawnFuelIsSeededPerAnchorAndStaysInsideItsBand()
    {
        // A configured random band remains supported; the shipped band is fixed at 75%.
        var fleet = new VehicleFleet(VehicleRoster.LoadDefault(), new VehicleFleetOptions
        {
            MinimumSpawnFuelFraction = 0.25f,
            MaximumSpawnFuelFraction = 0.75f,
        });
        VehicleFleetOptions options = fleet.Options;

        float first = fleet.SpawnFuel(matchSeed: 7, anchorInstanceId: 12_345);
        Assert.Equal(first, fleet.SpawnFuel(7, 12_345));
        Assert.NotEqual(first, fleet.SpawnFuel(8, 12_345));

        for (uint anchor = 0; anchor < 200; anchor++)
        {
            float fuel = fleet.SpawnFuel(7, anchor);
            Assert.InRange(
                fuel,
                options.MaxFuel * options.MinimumSpawnFuelFraction,
                options.MaxFuel * options.MaximumSpawnFuelFraction);
        }
    }

    /// <summary>
    /// The bands are the owner's own 50 / 35 / 20 / 10 % ladder, adopted under D53 (docs/117 §2);
    /// <b>which drive mode each one selects is still Cranberry's decision</b> because
    /// <c>MOVE_INFO_OVERRIDE</c> is 0 on all twelve rows. Ordinal 5 is the one row whose
    /// <c>MAX_FORWARD</c> is 0, so a crippled car genuinely will not drive.
    ///
    /// <para>This used to walk 75 / 50 / 25 %, <c>DamageLevelInfo</c>'s own three levels. Those are
    /// real client numbers but they are the <i>hit-indicator</i> levels, and taking the owner's
    /// 100,000 condition bar without his ladder would have changed a car's behaviour twice.</para>
    /// </summary>
    [Fact]
    public void ConditionSelectsADegradedDriveModeAndOrdinalFiveCannotMove()
    {
        var options = new VehicleFleetOptions { MaxHealth = 1_000 };
        VehicleFleet fleet = FleetWith(1, out MatchVehicle vehicle, options);

        Assert.Equal(VehicleCondition.Intact, vehicle.ConditionUnder(options));
        Assert.Equal(0, vehicle.DriveModeOrdinalUnder(options));

        Assert.Equal(VehicleCondition.Damaged, fleet.ApplyDamage(vehicle, 500));        // 500/1000
        Assert.Equal(6, vehicle.DriveModeOrdinalUnder(options));
        Assert.Equal(11u, vehicle.Definition.DriveModes[6].MovementMode);

        Assert.Equal(VehicleCondition.BadlyDamaged, fleet.ApplyDamage(vehicle, 150));   // 350/1000
        Assert.Equal(7, vehicle.DriveModeOrdinalUnder(options));

        Assert.Equal(VehicleCondition.Crippled, fleet.ApplyDamage(vehicle, 150));       // 200/1000
        Assert.Equal(5, vehicle.DriveModeOrdinalUnder(options));
        Assert.Equal(0f, vehicle.Definition.DriveModes[5].MaxForward);
    }

    /// <summary>Destroying a car empties it: no occupants, no owner, no engine, and nobody may enter.</summary>
    [Fact]
    public void DestructionEmptiesTheCarAndRefusesFurtherEntry()
    {
        VehicleFleet fleet = FleetWith(1, out MatchVehicle vehicle);
        fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _);
        fleet.TryEnter(VehicleGuid, Passenger, 1, 5_000, out _, out _);
        vehicle.EngineOn = true;

        Assert.Equal(VehicleCondition.Destroyed, fleet.ApplyDamage(vehicle, uint.MaxValue));

        Assert.Equal(0u, vehicle.Health);
        Assert.True(vehicle.IsEmpty);
        Assert.Equal(0UL, vehicle.OwnerGuid);
        Assert.False(vehicle.EngineOn);
        Assert.False(fleet.TryGetForOccupant(Driver, out _));
        Assert.False(fleet.TryGetForOccupant(Passenger, out _));
        Assert.Equal(
            VehicleActionResult.Destroyed,
            fleet.TryEnter(VehicleGuid, Driver, 0, 100_000, out _, out _));
    }

    /// <summary>A disconnect or a death has to release the car the same way a clean exit does.</summary>
    [Fact]
    public void EvictingADriverReleasesOwnershipAndTheSeat()
    {
        VehicleFleet fleet = FleetWith(1, out MatchVehicle vehicle);
        fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _);
        vehicle.EngineOn = true;

        Assert.Same(vehicle, fleet.Evict(Driver));

        Assert.True(vehicle.IsEmpty);
        Assert.Equal(0UL, vehicle.OwnerGuid);
        Assert.False(vehicle.EngineOn);
        Assert.Null(fleet.Evict(Driver));
    }

    // --- population ----------------------------------------------------------------------------

    /// <summary>
    /// A plan becomes live vehicles with the zone's own guids and transient ids — the fleet never
    /// invents an id — and the occupant list a possession packet needs comes straight off the seats.
    /// </summary>
    [Fact]
    public void PopulateRealisesAPlanWithTheZonesOwnIdsAndBuildsOccupantLists()
    {
        VehicleRoster roster = SharedRoster.Value;
        var fleet = new VehicleFleet(roster);
        IReadOnlyList<PlannedVehicle> plan = VehicleSpawnPlanner.Plan(
            VehicleAnchorSet.LoadDefault(), roster, matchSeed: 99, new VehicleSpawnPlanOptions { SpawnChance = null, Count = 12 });

        IReadOnlyList<MatchVehicle> created = fleet.Populate(
            plan, i => 0x9000UL + (ulong)i, i => 5_000u + (uint)i, matchSeed: 99);

        Assert.Equal(12, created.Count);
        Assert.Equal(12, fleet.Count);
        for (int i = 0; i < created.Count; i++)
        {
            Assert.Equal(0x9000UL + (ulong)i, created[i].Guid);
            Assert.Equal(5_000u + (uint)i, created[i].TransientId);
            Assert.Equal(plan[i].VehicleId, created[i].Definition.VehicleId);
            Assert.Equal(plan[i].Position, created[i].Position);
            Assert.Equal(plan[i].AnchorInstanceId, created[i].AnchorInstanceId);
            Assert.Equal(0UL, created[i].OwnerGuid);       // parked: nobody simulates it
            Assert.True(created[i].IsEmpty);
            Assert.True(fleet.TryGetByTransient(created[i].TransientId, out _));
        }

        MatchVehicle car = created[0];
        fleet.TryEnter(car.Guid, Driver, 0, 0, out _, out _);
        IReadOnlyList<VehicleOccupantSlot> occupants = car.Occupants();
        VehicleOccupantSlot only = Assert.Single(occupants);
        Assert.Equal(0, only.SeatIndex);
        Assert.Equal(Driver, only.CharacterGuid);

        Assert.Throws<ArgumentException>(() => fleet.Add(car));
        fleet.Clear();
        Assert.Equal(0, fleet.Count);
    }

    /// <summary>
    /// The move-mode byte is recorded, not interpreted: the enum has no name strings in the image
    /// and the one live value (5, under a canopy) is not a <c>MOVEMENT_MODE</c> (docs/43 §4.5).
    /// </summary>
    [Fact]
    public void TheReportedMoveModeIsStoredVerbatim()
    {
        FleetWith(1, out MatchVehicle vehicle);

        Assert.Null(vehicle.ReportedMoveMode);
        vehicle.ReportedMoveMode = VehicleCurrentMoveMode.ObservedUnderCanopy;
        Assert.Equal((byte)5, vehicle.ReportedMoveMode);
    }
}
