using System.Numerics;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

/// <summary>
/// docs/61 §4 — starting a car.
///
/// <para>The half that is <b>derived</b> is the item data, and this pass re-read it from the sheets
/// rather than taking docs/59 on trust — which turned up a correction: <b>the Vehicle Key has no
/// <c>StartVehicle</c> option at all</b>. Only the two Hotwire items do, and their busy timer is the
/// client own 7,000 ms. The half that is <b>designed</b> is the policy, and it is off by default
/// because neither item is in the loot tables yet.</para>
/// </summary>
public sealed class VehicleIgnitionTests
{
    private static readonly Lazy<VehicleRoster> SharedRoster = new(VehicleRoster.LoadDefault);

    private const ulong Driver = 5001;
    private const ulong Passenger = 5002;
    private const ulong VehicleGuid = 0x8000;
    private const ulong Seed = 0xC0FFEE;

    private static VehicleFleet FleetWith(out MatchVehicle vehicle, float fuel = 10_000f)
    {
        VehicleRoster roster = SharedRoster.Value;
        var fleet = new VehicleFleet(roster);
        vehicle = new MatchVehicle(
            VehicleGuid, 8_888u, roster.Require(1), new Vector3(0, 30, 0), 0f,
            fleet.Options.MaxHealth, fuel);
        fleet.Add(vehicle);
        return fleet;
    }

    private static VehicleIgnitionOptions Required(float keyed = 0f) =>
        new() { Required = true, KeyedFraction = keyed };

    // --- the client's own data ------------------------------------------------------------------

    /// <summary>
    /// <c>ItemIdUseOptionGroupId</c> maps 3458 and 3459 to group 55; group 55 is four
    /// <c>StartVehicle</c> options (62/70/71/72) with <c>TARGET_TYPE</c> 2 and <c>BUSY_MSEC</c> 7000,
    /// plus one <c>DragAndDropItem</c>.
    /// </summary>
    [Fact]
    public void TheHotwireFactsMatchTheClientSheets()
    {
        Assert.Equal(new uint[] { 3458, 3459 }, AugustIgnitionFacts.HotwireItemIds.ToArray());
        Assert.Equal(25_075u, AugustIgnitionFacts.HotwireItemClass);
        Assert.Equal(55u, AugustIgnitionFacts.HotwireUseOptionGroup);
        Assert.Equal(new uint[] { 62, 70, 71, 72 }, AugustIgnitionFacts.StartVehicleOptionIds.ToArray());
        Assert.Equal(
            new uint[] { 4726, 4728, 4727, 4725 },
            AugustIgnitionFacts.StartVehicleTargetRequirementSets.ToArray());
        Assert.Equal(7_000, AugustIgnitionFacts.HotwireBusyMs);

        Assert.True(AugustIgnitionFacts.IsHotwireItem(3458));
        Assert.True(AugustIgnitionFacts.IsHotwireItem(3459));
        Assert.False(AugustIgnitionFacts.IsHotwireItem(3460));
    }

    /// <summary>
    /// <b>The correction to docs/59 §2.5.2.</b> That section calls the Vehicle Key an "instant start"
    /// via <c>ItemUseOptionGroup</c> 63. Group 63 has no <c>StartVehicle</c> option: its members are
    /// 4 <c>DropItem</c>, 12 <c>RemoveItem</c>, 59 <c>LootItem</c>, 60 <c>EquipItem</c>,
    /// 61 <c>MoveItem</c>, 88 <c>SkinItem</c>. The key is <i>equipped into</i> a car, which is what
    /// <c>EquipmentSlotDefinitions</c> 67-72 and FTE 47 describe. The constants still agree with
    /// <c>StringHashToValue</c>: <c>Vehicle.KeyItemId</c> 3460, <c>Vehicle.KeyClassId</c> 25076.
    /// </summary>
    [Fact]
    public void TheVehicleKeyIsAnInstalledPartAndNotAStartVehicleOption()
    {
        Assert.Equal(3_460u, AugustIgnitionFacts.KeyItemId);
        Assert.Equal(25_076u, AugustIgnitionFacts.KeyItemClass);
        Assert.Equal(3_717u, AugustIgnitionFacts.SecondKeyItemId);
        Assert.Equal(63u, AugustIgnitionFacts.KeyUseOptionGroup);

        Assert.True(AugustIgnitionFacts.IsKeyItem(3_460));
        Assert.True(AugustIgnitionFacts.IsKeyItem(3_717));
        Assert.False(AugustIgnitionFacts.IsKeyItem(3_458));

        // The key group is not among the four StartVehicle-bearing groups.
        Assert.NotEqual(AugustIgnitionFacts.HotwireUseOptionGroup, AugustIgnitionFacts.KeyUseOptionGroup);
    }

    // --- the default, which must not take driving away ------------------------------------------

    /// <summary>
    /// Neither Hotwire (3458/3459) nor either key (3460/3717) is in <c>z2-loot-tables.json</c>.
    /// Requiring ignition before those rows exist would make every car in the match unstartable, so
    /// the default is off and mounting starts the engine exactly as it does today.
    /// </summary>
    [Fact]
    public void IgnitionIsNotRequiredByDefaultSoEveryCarStillStartsOnMounting()
    {
        Assert.False(VehicleIgnitionOptions.Default.Required);

        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        var ignition = new VehicleIgnition();
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _));

        VehicleIgnitionOutcome outcome = ignition.TryStart(
            vehicle, Driver, carriesHotwire: false, 1_000, Seed,
            fleet.Options, VehicleIgnitionOptions.Default);

        Assert.Equal(VehicleIgnitionResult.Started, outcome.Result);
        Assert.True(outcome.EngineOn);
        Assert.True(vehicle.EngineOn);
        Assert.True(outcome.SendsEngine);
        Assert.Equal(0, outcome.BusyMs);
    }

    // --- the hotwire ----------------------------------------------------------------------------

    /// <summary>
    /// The 7,000 ms belongs to the client — it is <c>BUSY_MSEC</c> on the <c>StartVehicle</c> option
    /// and the client draws its own progress bar from it. The server arms the deadline and refuses a
    /// completion that arrives before it.
    /// </summary>
    [Fact]
    public void AHotwireArmsTheClientOwnSevenSecondTimerAndOnlyCompletesAfterIt()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        var ignition = new VehicleIgnition();
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _));

        VehicleIgnitionOutcome armed = ignition.TryStart(
            vehicle, Driver, carriesHotwire: true, 10_000, Seed, fleet.Options, Required());

        Assert.Equal(VehicleIgnitionResult.Armed, armed.Result);
        Assert.Equal(7_000, armed.BusyMs);
        Assert.Equal(17_000L, armed.ReadyAtMs);
        Assert.False(vehicle.EngineOn);
        Assert.False(armed.SendsEngine);

        Assert.Equal(
            VehicleIgnitionResult.StillBusy,
            ignition.TryComplete(vehicle, Driver, 16_999, fleet.Options).Result);
        Assert.False(vehicle.EngineOn);

        VehicleIgnitionOutcome done = ignition.TryComplete(vehicle, Driver, 17_000, fleet.Options);
        Assert.Equal(VehicleIgnitionResult.Completed, done.Result);
        Assert.True(vehicle.EngineOn);
        Assert.True(done.SendsEngine);
        Assert.Equal(1, ignition.HotwiresCompleted);
        Assert.Equal(0UL, ignition.ArmedVehicleGuid);
    }

    [Fact]
    public void ASecondStartOnAnAlreadyArmedCarDoesNotRestartTheTimer()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        var ignition = new VehicleIgnition();
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _));

        ignition.TryStart(vehicle, Driver, true, 0, Seed, fleet.Options, Required());
        VehicleIgnitionOutcome again = ignition.TryStart(
            vehicle, Driver, true, 3_000, Seed, fleet.Options, Required());

        Assert.Equal(VehicleIgnitionResult.AlreadyArmed, again.Result);
        Assert.Equal(7_000L, again.ReadyAtMs);      // the ORIGINAL deadline, not 10,000
    }

    [Fact]
    public void WithoutAHotwireItemAnUnkeyedCarSimplyDoesNotStart()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        var ignition = new VehicleIgnition();
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _));

        VehicleIgnitionOutcome outcome = ignition.TryStart(
            vehicle, Driver, carriesHotwire: false, 0, Seed, fleet.Options, Required());

        Assert.Equal(VehicleIgnitionResult.NoIgnitionItem, outcome.Result);
        Assert.False(vehicle.EngineOn);
    }

    [Fact]
    public void AKeyedCarNeedsNoHotwireAndTheRollIsStableForTheWholeMatch()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        var ignition = new VehicleIgnition();
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _));

        VehicleIgnitionOptions everyCarKeyed = Required(keyed: 1f);
        Assert.True(ignition.IsKeyed(vehicle, Seed, everyCarKeyed));
        Assert.True(ignition.IsKeyed(vehicle, Seed, everyCarKeyed));

        VehicleIgnitionOutcome outcome = ignition.TryStart(
            vehicle, Driver, carriesHotwire: false, 0, Seed, fleet.Options, everyCarKeyed);
        Assert.Equal(VehicleIgnitionResult.Started, outcome.Result);
        Assert.Equal(1, ignition.KeyedStarts);
    }

    /// <summary>
    /// The roll must be deterministic per (seed, car) and must actually land near the configured
    /// fraction, or "35 % of cars have keys" would be a fiction.
    /// </summary>
    [Fact]
    public void TheKeyedRollIsSeededAndLandsNearTheConfiguredFraction()
    {
        VehicleRoster roster = SharedRoster.Value;
        var fleet = new VehicleFleet(roster);
        for (int index = 0; index < 300; index++)
        {
            fleet.Add(new MatchVehicle(
                0x9000UL + (ulong)index, 9_000u + (uint)index, roster.Require(1),
                new Vector3(index, 30, 0), 0f, fleet.Options.MaxHealth, 5_000f)
            {
                AnchorInstanceId = (uint)(index * 7),
            });
        }

        VehicleIgnitionOptions options = Required(keyed: 0.35f);
        var first = new VehicleIgnition();
        var second = new VehicleIgnition();

        int keyed = 0;
        foreach (MatchVehicle vehicle in fleet.Vehicles)
        {
            bool a = first.IsKeyed(vehicle, Seed, options);
            bool b = second.IsKeyed(vehicle, Seed, options);
            Assert.Equal(a, b);                                  // same seed, same answer
            if (a)
            {
                keyed++;
            }
        }

        Assert.InRange(keyed / 300.0, 0.25, 0.45);
    }

    // --- the refusals ----------------------------------------------------------------------------

    [Fact]
    public void OnlyTheDriverStartsTheEngine()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        var ignition = new VehicleIgnition();
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _));
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Passenger, 2, 5_000, out _, out _));

        VehicleIgnitionOutcome outcome = ignition.TryStart(
            vehicle, Passenger, true, 6_000, Seed, fleet.Options, Required());

        Assert.Equal(VehicleIgnitionResult.NotDriver, outcome.Result);
        Assert.False(vehicle.EngineOn);
    }

    [Fact]
    public void AnEmptyTankDoesNotStart()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle, fuel: 0f);
        var ignition = new VehicleIgnition();
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _));

        Assert.Equal(
            VehicleIgnitionResult.NoFuel,
            ignition.TryStart(vehicle, Driver, true, 0, Seed, fleet.Options, VehicleIgnitionOptions.Default).Result);
        Assert.False(vehicle.EngineOn);
    }

    /// <summary>FTE 1: <i>"Once the condition has reached nothing the vehicle will no longer be operable."</i></summary>
    [Fact]
    public void AWreckDoesNotStart()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        var ignition = new VehicleIgnition();
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _));
        Assert.Equal(VehicleCondition.Destroyed, fleet.ApplyDamage(vehicle, fleet.Options.MaxHealth));

        Assert.Equal(
            VehicleIgnitionResult.Wrecked,
            ignition.TryStart(vehicle, Driver, true, 0, Seed, fleet.Options, VehicleIgnitionOptions.Default).Result);
    }

    [Fact]
    public void AnAlreadyRunningEngineIsNotRestartedAndOwesNoPacket()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        var ignition = new VehicleIgnition();
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _));
        vehicle.EngineOn = true;

        VehicleIgnitionOutcome outcome = ignition.TryStart(
            vehicle, Driver, true, 0, Seed, fleet.Options, VehicleIgnitionOptions.Default);
        Assert.Equal(VehicleIgnitionResult.AlreadyRunning, outcome.Result);
        Assert.False(outcome.SendsEngine);
    }

    [Fact]
    public void CompletingAHotwireNobodyArmedIsRefused()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        var ignition = new VehicleIgnition();

        Assert.Equal(
            VehicleIgnitionResult.NotArmed,
            ignition.TryComplete(vehicle, Driver, 100_000, fleet.Options).Result);
        Assert.False(vehicle.EngineOn);
    }

    [Fact]
    public void CancellingAnArmedHotwireStopsItCompleting()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        var ignition = new VehicleIgnition();
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _));

        ignition.TryStart(vehicle, Driver, true, 0, Seed, fleet.Options, Required());
        Assert.True(ignition.Cancel(Driver));
        Assert.False(ignition.Cancel(Driver));

        Assert.Equal(
            VehicleIgnitionResult.NotArmed,
            ignition.TryComplete(vehicle, Driver, 20_000, fleet.Options).Result);
    }

    /// <summary>
    /// A hotwire that finishes after its driver has been thrown out — killed, or dismounted — must
    /// not start the engine for somebody who is no longer at the wheel.
    /// </summary>
    [Fact]
    public void AHotwireThatCompletesAfterTheDriverLeftDoesNotStartTheCar()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        var ignition = new VehicleIgnition();
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _));

        ignition.TryStart(vehicle, Driver, true, 0, Seed, fleet.Options, Required());
        Assert.NotNull(fleet.Evict(Driver));

        Assert.Equal(
            VehicleIgnitionResult.NotDriver,
            ignition.TryComplete(vehicle, Driver, 8_000, fleet.Options).Result);
        Assert.False(vehicle.EngineOn);
    }
}
