using System.Numerics;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

/// <summary>
/// docs/61 §3 — <c>VehicleFleet.BurnFuel</c> had no caller, so a tank never moved (docs/59 §2.0).
/// These tests pin the caller, and in particular the three ways a naive one goes wrong: the
/// first-tick sentinel, a stalled listener thread billing minutes in one go, and 101 bytes of gauge
/// per car per tick.
/// </summary>
public sealed class VehicleFuelPumpTests
{
    private static readonly Lazy<VehicleRoster> SharedRoster = new(VehicleRoster.LoadDefault);

    private const ulong Driver = 4001;
    private const ulong VehicleGuid = 0x7000;

    private static VehicleFleet FleetWith(out MatchVehicle vehicle, float fuel = 10_000f)
    {
        VehicleRoster roster = SharedRoster.Value;
        var fleet = new VehicleFleet(roster);
        vehicle = new MatchVehicle(
            VehicleGuid, 7_777u, roster.Require(1), new Vector3(5, 30, 5), 0f,
            fleet.Options.MaxHealth, fuel);
        fleet.Add(vehicle);
        return fleet;
    }

    private static VehicleFuelOptions Burning(uint step = 100) =>
        new() { Enabled = true, SendGauge = true, GaugeStepUnits = step };

    // --- the client's own numbers ---------------------------------------------------------------

    /// <summary>
    /// Fuel is <c>Resources.txt</c> row 50 and nothing about how fast it goes down is in the client:
    /// <c>BURN_PER_MSEC</c> and <c>BURN_TICK_MSEC</c> are both 0, and
    /// <c>VehicleResourceMappings.txt</c> is header-only. Pinning that here is what stops a later
    /// lane presenting the burn rate as derived.
    /// </summary>
    [Fact]
    public void TheFuelResourceFactsMatchTheClientSheetAndTheBurnRateIsNotAmongThem()
    {
        Assert.Equal(50u, AugustFuelFacts.ResourceId);
        Assert.Equal(50u, AugustFuelFacts.ResourceType);
        Assert.Equal(1_000u, AugustFuelFacts.InitialValue);
        Assert.Equal(10_000u, AugustFuelFacts.MaxValue);
        Assert.Equal(30f, AugustFuelFacts.BroadcastRangeMetres);
        Assert.Equal(0f, AugustFuelFacts.BurnPerMillisecondInClient);
        Assert.Equal(20_000, AugustFuelFacts.RegenDamageInterruptMs);

        Assert.True(AugustFuelFacts.IsRefuelItem(73));
        Assert.True(AugustFuelFacts.IsRefuelItem(1_384));
        Assert.False(AugustFuelFacts.IsRefuelItem(3_460));
    }

    // --- the three defect guards ----------------------------------------------------------------

    /// <summary>
    /// The very first call must bill nothing. The sentinel is <see cref="long.MinValue"/> and
    /// <c>nowMs - long.MinValue</c> overflows and wraps negative — the same trap
    /// <c>VehicleFleet.IsCoolingDown</c> documents, where it made an untouched car read as
    /// permanently on cooldown.
    /// </summary>
    [Fact]
    public void TheFirstTickOnlyStampsAndBillsNothing()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        vehicle.EngineOn = true;
        var pump = new VehicleFuelPump();

        Assert.Null(pump.LastBilledMs);
        VehicleFuelTick first = pump.Step(fleet, 5_000, Burning());
        Assert.Equal(0f, first.BilledSeconds);
        Assert.Equal(10_000f, vehicle.Fuel);
        Assert.Equal(5_000L, pump.LastBilledMs!.Value);
        Assert.False(first.HasWork);
    }

    /// <summary>
    /// A host stopped in a debugger, a machine that slept, or a listener thread starved behind a
    /// 300-object drain all produce one tick with a gap of minutes. Billing it would empty a full
    /// tank in one call and stall every running engine in the match at once.
    /// </summary>
    [Fact]
    public void AnEnormousGapIsClampedInsteadOfEmptyingEveryTankAtOnce()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        vehicle.EngineOn = true;
        var pump = new VehicleFuelPump();

        pump.Step(fleet, 0, Burning());
        VehicleFuelTick tick = pump.Step(fleet, 600_000, Burning());     // ten minutes

        Assert.Equal(5f, tick.BilledSeconds);                            // MaxBilledSeconds
        Assert.Equal(10_000f - (5f * 8f), vehicle.Fuel, 3);
        Assert.Empty(tick.Stalled);
        Assert.True(vehicle.EngineOn);
    }

    [Fact]
    public void AClockThatGoesBackwardsBillsNothingAndRefundsNothing()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        vehicle.EngineOn = true;
        var pump = new VehicleFuelPump();

        pump.Step(fleet, 10_000, Burning());
        pump.Step(fleet, 13_000, Burning());
        float after = vehicle.Fuel;

        VehicleFuelTick backwards = pump.Step(fleet, 9_000, Burning());
        Assert.Equal(0f, backwards.BilledSeconds);
        Assert.Equal(after, vehicle.Fuel);
    }

    // --- burning ---------------------------------------------------------------------------------

    [Fact]
    public void AnEngineOnCarBurnsAtTheConfiguredRateAndAParkedOneDoesNot()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        var pump = new VehicleFuelPump();

        pump.Step(fleet, 0, Burning());
        pump.Step(fleet, 3_000, Burning());
        Assert.Equal(10_000f, vehicle.Fuel);            // engine off: nothing burned

        vehicle.EngineOn = true;
        pump.Step(fleet, 6_000, Burning());
        Assert.Equal(10_000f - 24f, vehicle.Fuel, 3);   // 3 s at 8/s
    }

    /// <summary>
    /// The one fuel event the player must see. The caller owes each returned car a
    /// <c>88 1b Vehicle.Engine(off)</c>, and a stall always sends a gauge row whatever the step
    /// threshold is.
    /// </summary>
    [Fact]
    public void RunningDryStopsTheEngineReportsTheCarAndAlwaysSendsAZeroGauge()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle, fuel: 10f);
        var pump = new VehicleFuelPump();
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _));
        vehicle.EngineOn = true;

        var options = new VehicleFuelOptions
        {
            Enabled = true,
            SendGauge = true,
            GaugeStepUnits = 5_000,     // deliberately coarser than the whole remaining tank
        };

        pump.Step(fleet, 0, options);
        VehicleFuelTick tick = pump.Step(fleet, 3_000, options);

        Assert.Equal(0f, vehicle.Fuel);
        Assert.False(vehicle.EngineOn);
        Assert.Single(tick.Stalled);
        Assert.Same(vehicle, tick.Stalled[0]);
        Assert.Contains(tick.Gauges, gauge => gauge.Value == 0);
    }

    /// <summary>
    /// <b>Fuel burning is ON since docs/117 §3.4</b>, and this test is its inverse: it used to
    /// assert the burn was off, because an engine that cuts out mid-drive had never been
    /// play-tested. The boost changed the trade — the client's own <c>AbilityEx</c>
    /// <c>VehicleTurbo</c> rows spend <c>RESOURCE_TYPE 50</c>, so the boost meter in this build IS
    /// the fuel tank, and with the burn off a boost would be free and infinite.
    ///
    /// <para>A minute of driving costs 480 of 10,000 — a full tank is 20 min 50 s against a 24:50
    /// gas ladder, so fuel still cannot be what ends a first drive.
    /// <c>CRANBERRY_VEHICLE_FUEL=0</c> is the one-word revert.</para>
    /// </summary>
    [Fact]
    public void FuelBurningIsOnAndAFullTankStillOutlastsTheGasLadder()
    {
        Assert.True(VehicleFuelOptions.Default.Enabled);
        Assert.True(VehicleFuelOptions.Default.SendGauge);

        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        vehicle.EngineOn = true;
        var pump = new VehicleFuelPump();

        // MaxBilledSeconds caps one late tick at five seconds, so a minute is billed in ticks.
        pump.Step(fleet, 0, VehicleFuelOptions.Default);
        for (int second = 1; second <= 60; second++)
        {
            pump.Step(fleet, second * 1_000, VehicleFuelOptions.Default);
        }

        Assert.Equal(10_000f - (60f * VehicleFuelOptions.Default.BurnPerSecond), vehicle.Fuel, 1);
        Assert.True(vehicle.EngineOn);

        // Explicitly off is still explicitly off.
        var off = new VehicleFuelOptions { Enabled = false };
        var quiet = new VehicleFuelPump();
        quiet.Step(fleet, 0, off);
        quiet.Step(fleet, 60_000, off);
        Assert.Equal(10_000f - (60f * VehicleFuelOptions.Default.BurnPerSecond), vehicle.Fuel, 1);
    }

    // --- the gauge --------------------------------------------------------------------------------

    /// <summary>
    /// One <c>8d</c> ResourceEvent is 101 B. Sending one per car per 3 s tick is 34 B/s per car for a
    /// change of 24 units in a 10,000-unit tank — finer than the gauge can draw. The step threshold
    /// makes it one packet every 12.5 s instead.
    /// </summary>
    [Fact]
    public void TheGaugeIsSentOnceEveryStepRatherThanOnEveryTick()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        var pump = new VehicleFuelPump();
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _));
        vehicle.EngineOn = true;

        VehicleFuelOptions options = Burning();
        pump.Step(fleet, 0, options);

        // The very first gauge after mounting always goes, so the client has a starting value.
        VehicleFuelTick opening = pump.Step(fleet, 3_000, options);
        Assert.Single(opening.Gauges);
        Assert.Equal(10_000u - 24u, opening.Gauges[0].Value);

        // 24 units per tick; nothing is sent again until 100 have gone.
        int sends = 0;
        for (int tick = 2; tick <= 6; tick++)
        {
            sends += pump.Step(fleet, tick * 3_000, options).Gauges.Count;
        }

        Assert.Equal(1, sends);
    }

    [Fact]
    public void AnEmptyCarCostsNoGaugeAtAllHoweverLargeTheFleetIs()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        vehicle.EngineOn = true;
        var pump = new VehicleFuelPump();

        pump.Step(fleet, 0, Burning());
        VehicleFuelTick tick = pump.Step(fleet, 3_000, Burning());
        Assert.Empty(tick.Gauges);
    }

    /// <summary>
    /// A quarter-tank Biofuel is 2,500 units — 25 times the gauge step — but a refuel that happened
    /// to be smaller than the step would otherwise stay invisible until the tank burned back down
    /// past it, so a refuel forces the next gauge row.
    /// </summary>
    [Fact]
    public void RefuellingAddsTheDesignedAmountAndForcesTheNextGaugeRow()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle, fuel: 1_000f);
        var pump = new VehicleFuelPump();
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(VehicleGuid, Driver, 0, 0, out _, out _));

        VehicleFuelOptions options = Burning(step: 5_000);
        pump.Step(fleet, 0, options);
        pump.Step(fleet, 3_000, options);          // opening gauge at 1,000

        Assert.Equal(2_500f, pump.Refuel(fleet, vehicle, options));
        VehicleFuelTick after = pump.Step(fleet, 6_000, options);
        Assert.Single(after.Gauges);
        Assert.Equal(3_500u, after.Gauges[0].Value);
    }

    [Fact]
    public void RefuellingIsClampedToTheTank()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle, fuel: 9_000f);
        var pump = new VehicleFuelPump();
        Assert.Equal(1_000f, pump.Refuel(fleet, vehicle, VehicleFuelOptions.Default));
        Assert.Equal(10_000f, vehicle.Fuel);
    }

    [Fact]
    public void ClearForgetsTheStampSoAMatchResetDoesNotBillTheGapBetweenMatches()
    {
        VehicleFleet fleet = FleetWith(out MatchVehicle vehicle);
        vehicle.EngineOn = true;
        var pump = new VehicleFuelPump();

        pump.Step(fleet, 0, Burning());
        pump.Clear();
        Assert.Null(pump.LastBilledMs);

        VehicleFuelTick first = pump.Step(fleet, 3_600_000, Burning());
        Assert.Equal(0f, first.BilledSeconds);
        Assert.Equal(10_000f, vehicle.Fuel);
    }

    /// <summary>
    /// A full tank must outlast a normal drive but not the whole match: 10,000 / 8 = 1,250 s against
    /// the 24:50 gas ladder of docs/53, and the 25-75 % spawn range is 5 to 15 minutes.
    /// </summary>
    [Fact]
    public void AFullTankIsTwentyMinutesOfDrivingAgainstATwentyFiveMinuteMatch()
    {
        VehicleFuelOptions options = VehicleFuelOptions.Default;
        double fullTankSeconds = AugustFuelFacts.MaxValue / options.BurnPerSecond;

        Assert.Equal(1_250d, fullTankSeconds, 3);
        Assert.True(fullTankSeconds < 24 * 60 + 50, "a full tank must not outlast the gas ladder");
        Assert.True(fullTankSeconds * 0.25 > 5 * 60, "the poorest spawn must still be a real drive");
    }
}
