namespace Cranberry.Zone.Vehicles;

/// <summary>
/// The August build own fuel facts, re-read from the client data this pass rather than taken from
/// docs/59 (docs/61 §3.1). Nothing here is a Cranberry choice; the choices live in
/// <see cref="VehicleFuelOptions"/> and are labelled there.
/// </summary>
public static class AugustFuelFacts
{
    /// <summary>
    /// <c>Resources.txt</c> row 50: <c>RESOURCE_TYPE</c> 50, <c>TYPE_NAME</c> <c>ResourceTypeFuel</c>,
    /// factory <c>Generic</c>. This is the id the <c>8d ResourceEvent</c> family carries.
    /// </summary>
    public const uint ResourceId = 50;

    /// <summary>Same row, <c>RESOURCE_TYPE</c> column.</summary>
    public const uint ResourceType = 50;

    /// <summary><c>INITIAL_VALUE</c> — 1,000, i.e. a tenth of a tank.</summary>
    public const uint InitialValue = 1_000;

    /// <summary><c>MAX_VALUE</c> — the tank.</summary>
    public const uint MaxValue = 10_000;

    /// <summary>
    /// <c>PACKET_BROADCAST_RANGE</c> — 30 m. Fuel is a near-field resource, which is one more reason
    /// the gauge is sent to the occupants and not to the disc.
    /// </summary>
    public const float BroadcastRangeMetres = 30f;

    /// <summary>
    /// <b>The burn rate is not in the client.</b> <c>BURN_PER_MSEC</c> and <c>BURN_TICK_MSEC</c> are
    /// both 0 on row 50, and <c>VehicleResourceMappings.txt</c> is header-only, so there is no
    /// per-vehicle tank and no per-vehicle consumption anywhere in the build. Everything about how
    /// fast fuel goes down is <see cref="VehicleFuelOptions"/>, i.e. Cranberry design.
    /// </summary>
    public const float BurnPerMillisecondInClient = 0f;

    /// <summary>
    /// docs/59 §2.5.1 reads the 20,000 on row 50 as <c>REGEN_TICK_MSEC</c>. It is column 20,
    /// <c>REGEN_DAMAGE_INTERRUPT_MS</c>; <c>REGEN_TICK_MSEC</c> (column 21) is 0. Both readings agree
    /// that fuel never regenerates on its own, so nothing downstream changes — but the column name
    /// is recorded here so the next lane does not re-derive the wrong one (docs/61 §3.1).
    /// </summary>
    public const int RegenDamageInterruptMs = 20_000;

    /// <summary>Biofuel, <c>ITEM_CLASS</c> 25011, <c>ConsumeItem</c> with a 1,000 ms busy timer.</summary>
    public const uint BiofuelItemId = 73;

    /// <summary>Ethanol, <c>ITEM_CLASS</c> 16053, same verb.</summary>
    public const uint EthanolItemId = 1_384;

    /// <summary>Is this item one of the two refuel items.</summary>
    public static bool IsRefuelItem(uint itemId) =>
        itemId is BiofuelItemId or EthanolItemId;
}

/// <summary>
/// How fast fuel goes down and how often the gauge is told about it. <b>Every value is
/// [DESIGN]</b> — see <see cref="AugustFuelFacts.BurnPerMillisecondInClient"/> for why there is no
/// alternative.
/// </summary>
public sealed class VehicleFuelOptions
{
    public static VehicleFuelOptions Default { get; } = new();

    /// <summary>
    /// Is fuel burned at all. <b>ON since docs/117 §3.4</b>; it was off through docs/61 because an
    /// engine that cuts out mid-drive had never been play-tested, and that was the right call while
    /// nothing else read the tank.
    ///
    /// <para>The boost changes the trade. The client's own <c>AbilityEx</c> <c>VehicleTurbo</c> rows
    /// carry <c>RESOURCE_TYPE = 50</c>, which is <c>ResourceTypeFuel</c> — <b>the boost meter in
    /// this build IS the fuel tank</b>, and there is no separate boost resource anywhere in
    /// <c>Resources.txt</c>. With the burn off there is nothing for a boost to spend and nothing to
    /// refuse it on, so the boost would be free and infinite.</para>
    ///
    /// <para>A full tank is still 20 min 50 s of cruising against a 24:50 gas ladder (docs/53), so
    /// fuel never ends a first drive; the 25-75 % spawn band is what makes a found car a decision.
    /// <c>CRANBERRY_VEHICLE_FUEL=0</c> is the one-word revert.</para>
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The gauge send. Independent of <see cref="Enabled"/> so the tank can be shown without being
    /// spent, which is the safe half of this feature and the one worth having on first.
    /// </summary>
    public bool SendGauge { get; init; } = true;

    /// <summary>
    /// Units per second of engine-on time. <see cref="AugustFuelFacts.MaxValue"/> / 8 = 1,250 s, so a
    /// full tank is <b>20 min 50 s</b> of continuous driving against a 24:50 gas ladder (docs/53),
    /// and the 25-75 % spawn range of <c>VehicleFleetOptions</c> is 5 to 15 minutes. That is long
    /// enough that fuel is never the reason a first drive ends, and short enough that a car is not a
    /// free ride for the whole match.
    /// </summary>
    public float BurnPerSecond { get; init; } = 8f;

    /// <summary>
    /// The largest elapsed time one call may bill, in seconds.
    ///
    /// <para><b>This is a real defect guard, not slop.</b> The pump is driven from the same
    /// <c>Later</c> chain as the gas and world pumps, and that chain is a wall-clock delay on a
    /// thread pool: a host stopped in a debugger, a machine that slept, or a listener thread starved
    /// behind a 300-object drain all produce a single tick with a gap of minutes. Billing it would
    /// empty a full tank in one call and stall every running engine in the match at once. Five
    /// seconds is one and a bit pump periods.</para>
    /// </summary>
    public float MaxBilledSeconds { get; init; } = 5f;

    /// <summary>
    /// How far the tank must move before the client is told again, in resource units.
    ///
    /// <para>One <c>8d ResourceEvent</c> type 3 is <c>CharacterResourceUpdate.WireLength</c> = 101 B.
    /// At <see cref="BurnPerSecond"/> 8 a 100-unit step (1 % of the tank) is one packet every
    /// 12.5 s — about 8 B/s per driven car, against the streamer own ~1 KB/s. Sending on every pump
    /// tick instead would be 101 B every 3 s for no visible difference, because 1 % is finer than the
    /// gauge can draw.</para>
    ///
    /// <para>A stall always sends, whatever this is set to: reaching zero is the one fuel event the
    /// player must see.</para>
    /// </summary>
    public uint GaugeStepUnits { get; init; } = 100;

    /// <summary>What a Biofuel or Ethanol use puts in the tank. Locale 884649093 says a Biofuel is
    /// <i>"enough biofuel to fill up a quarter of a tank"</i>, which is the only number the client
    /// offers and the reason this is 2,500 rather than a round 2,000. [DESIGN, client-hinted]</summary>
    public float RefuelAmount { get; init; } = 2_500f;
}

/// <summary>One gauge row this tick owes the client.</summary>
/// <param name="Vehicle">The car whose tank changed.</param>
/// <param name="Value">The new value, clamped into <c>[0, MAX_VALUE]</c>.</param>
/// <param name="PreviousValue">What the client was last told, so the update is a real delta.</param>
public readonly record struct VehicleFuelGauge(MatchVehicle Vehicle, uint Value, uint PreviousValue);

/// <summary>The outcome of one <see cref="VehicleFuelPump.Step"/>.</summary>
public sealed class VehicleFuelTick
{
    internal VehicleFuelTick(
        float billedSeconds,
        IReadOnlyList<MatchVehicle> stalled,
        IReadOnlyList<VehicleFuelGauge> gauges)
    {
        BilledSeconds = billedSeconds;
        Stalled = stalled;
        Gauges = gauges;
    }

    /// <summary>Seconds actually charged, after the <see cref="VehicleFuelOptions.MaxBilledSeconds"/> clamp.</summary>
    public float BilledSeconds { get; }

    /// <summary>
    /// Cars whose engine just stopped because the tank hit zero. The caller owes each one a
    /// <c>88 1b Vehicle.Engine(character, vehicle, engineOn: false)</c>.
    /// </summary>
    public IReadOnlyList<MatchVehicle> Stalled { get; }

    /// <summary>Gauge rows to send as <c>8d</c> ResourceEvent type 3.</summary>
    public IReadOnlyList<VehicleFuelGauge> Gauges { get; }

    /// <summary>Did this tick owe the wire anything.</summary>
    public bool HasWork => Stalled.Count > 0 || Gauges.Count > 0;

    /// <summary>A tick that did nothing.</summary>
    public static VehicleFuelTick Empty { get; } = new(0f, [], []);
}

/// <summary>
/// The caller <c>VehicleFleet.BurnFuel</c> never had (docs/59 §2.0: <i>"Fuel is never burned.
/// <c>VehicleFleet.BurnFuel</c> exists at <c>VehicleState.cs:694</c>; no caller"</i>).
///
/// <para>It is a class rather than a free function because it owns two pieces of per-session state
/// that must not be recomputed from the fleet: the instant it last billed, and what the client was
/// last <i>told</i> each tank was. Neither can be derived from <c>MatchVehicle.Fuel</c> — the first
/// because the fleet has no clock, the second because the whole point of
/// <see cref="VehicleFuelOptions.GaugeStepUnits"/> is that the two differ.</para>
///
/// <para>It reads no clock itself: the caller passes the instant in, exactly as
/// <c>WorldPumpArm.IsDue</c> does, so the whole thing is pinned by tests rather than by a live
/// match (docs/22 §9.4).</para>
/// </summary>
public sealed class VehicleFuelPump
{
    /// <summary>"No tick has ever run", tested explicitly and never subtracted.</summary>
    private const long Never = long.MinValue;

    private readonly Dictionary<ulong, uint> _lastGaugeSent = [];
    private long _lastBilledMs = Never;

    /// <summary>The instant of the last billed tick, or null before the first one.</summary>
    public long? LastBilledMs => _lastBilledMs == Never ? null : _lastBilledMs;

    /// <summary>Total seconds of engine time billed this session, for the log line.</summary>
    public double BilledSecondsTotal { get; private set; }

    /// <summary>
    /// Units charged to <i>each</i> running engine this session — the per-vehicle total, not the
    /// fleet sum, because that is the number that maps onto one tank.
    /// </summary>
    public double BurnedPerVehicleTotal { get; private set; }

    /// <summary>Gauge packets this pump has asked for.</summary>
    public int GaugeSends { get; private set; }

    /// <summary>
    /// Bills one tick.
    ///
    /// <para><b>The first call bills nothing.</b> <c>_lastBilledMs</c> starts at
    /// <see cref="long.MinValue"/>, and <c>nowMs - long.MinValue</c> overflows and wraps
    /// <i>negative</i> — the same trap <c>VehicleFleet.IsCoolingDown</c> documents, where it made a
    /// car nobody had ever touched read as permanently on cooldown. Here it would instead bill a
    /// nonsense interval on the very first tick of every match. So the sentinel is compared, never
    /// subtracted, and the first call only stamps.</para>
    ///
    /// <para>A <paramref name="nowMs"/> that goes backwards bills nothing and re-stamps, so a
    /// monotonic clock that wrapped cannot refund fuel.</para>
    /// </summary>
    /// <param name="fleet">The match car park.</param>
    /// <param name="nowMs">The caller monotonic instant, usually <c>Environment.TickCount64</c>.</param>
    /// <param name="options">Rates and gauge policy.</param>
    public VehicleFuelTick Step(VehicleFleet fleet, long nowMs, VehicleFuelOptions options, ulong? driverGuid = null)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(options);

        if (_lastBilledMs == Never)
        {
            _lastBilledMs = nowMs;
            return VehicleFuelTick.Empty;
        }

        long deltaMs = nowMs - _lastBilledMs;
        _lastBilledMs = nowMs;
        if (deltaMs <= 0)
        {
            return VehicleFuelTick.Empty;
        }

        float seconds = MathF.Min(deltaMs / 1000f, MathF.Max(0f, options.MaxBilledSeconds));

        IReadOnlyList<MatchVehicle> stalled = [];
        if (options.Enabled && options.BurnPerSecond > 0f)
        {
            // The fleet owns the arithmetic and the engine flag; this pump owns only WHEN. Its
            // FuelBurnPerSecond is the fleet own option, so the rate is passed through as elapsed
            // time scaled by the ratio between the two — see BurnFuelSeconds below.
            stalled = fleet.BurnFuel(BurnFuelSeconds(fleet, options, seconds), driverGuid);
            BilledSecondsTotal += seconds;
            BurnedPerVehicleTotal += options.BurnPerSecond * seconds;
        }

        List<VehicleFuelGauge>? gauges = null;
        if (options.SendGauge)
        {
            foreach (MatchVehicle vehicle in fleet.Vehicles)
            {
                // Only cars somebody is in can show a gauge, and PACKET_BROADCAST_RANGE is 30 m, so
                // an empty car park costs nothing here however large the fleet is.
                if (vehicle.OccupantCount == 0)
                {
                    _lastGaugeSent.Remove(vehicle.Guid);
                    continue;
                }

                uint value = ClampToTank(vehicle.Fuel);
                bool known = _lastGaugeSent.TryGetValue(vehicle.Guid, out uint previous);
                bool stopped = value == 0 && !vehicle.EngineOn;
                uint step = Math.Max(1u, options.GaugeStepUnits);
                bool moved = !known
                    || stopped && previous != 0
                    || AbsoluteDifference(previous, value) >= step;
                if (!moved)
                {
                    continue;
                }

                _lastGaugeSent[vehicle.Guid] = value;
                GaugeSends++;
                (gauges ??= []).Add(new VehicleFuelGauge(vehicle, value, known ? previous : value));
            }
        }

        return new VehicleFuelTick(seconds, stalled, (IReadOnlyList<VehicleFuelGauge>?)gauges ?? []);
    }

    /// <summary>
    /// Adds fuel from a Biofuel or Ethanol use and returns what went in, keeping the gauge honest so
    /// the next <see cref="Step"/> reports the refuel immediately rather than waiting for the step
    /// threshold.
    /// </summary>
    public float Refuel(VehicleFleet fleet, MatchVehicle vehicle, VehicleFuelOptions options)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(options);

        float added = fleet.Refuel(vehicle, options.RefuelAmount);
        if (added > 0f)
        {
            // Forgetting the last-sent value is what makes the very next Step send a gauge row: a
            // refuel of less than GaugeStepUnits would otherwise be invisible until the tank had
            // burned back down past the threshold.
            _lastGaugeSent.Remove(vehicle.Guid);
        }

        return added;
    }

    /// <summary>Drops the gauge memory — a match reset, or the session leaving the match.</summary>
    public void Clear()
    {
        _lastGaugeSent.Clear();
        _lastBilledMs = Never;
    }

    /// <summary>
    /// <c>VehicleFleet.BurnFuel</c> multiplies by <c>VehicleFleetOptions.FuelBurnPerSecond</c>, and
    /// this pump has its own <see cref="VehicleFuelOptions.BurnPerSecond"/> so the rate can be tuned
    /// without reaching into the fleet options a different lane owns. Scaling the elapsed time by the
    /// ratio makes the two agree exactly, and makes a fleet rate of zero mean zero rather than
    /// dividing by it.
    /// </summary>
    private static float BurnFuelSeconds(VehicleFleet fleet, VehicleFuelOptions options, float seconds)
    {
        float fleetRate = fleet.Options.FuelBurnPerSecond;
        return fleetRate > 0f ? seconds * (options.BurnPerSecond / fleetRate) : 0f;
    }

    private static uint ClampToTank(float fuel) =>
        fuel <= 0f ? 0u
        : fuel >= AugustFuelFacts.MaxValue ? AugustFuelFacts.MaxValue
        : (uint)fuel;

    private static uint AbsoluteDifference(uint left, uint right) =>
        left > right ? left - right : right - left;
}
