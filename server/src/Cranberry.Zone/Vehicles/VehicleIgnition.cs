namespace Cranberry.Zone.Vehicles;

/// <summary>
/// What the August client data actually says about starting a car, re-read from the sheets this pass
/// (docs/61 §4.1). Every constant here is <b>[P-data]</b>.
/// </summary>
public static class AugustIgnitionFacts
{
    /// <summary>
    /// The two "Hotwire" items. Both <c>ITEM_CLASS</c> <see cref="HotwireItemClass"/>,
    /// <c>NAME_ID</c> 14166, <c>MAX_STACK_SIZE</c> 1, <c>PARAM2</c> 2.
    /// </summary>
    public static ReadOnlySpan<uint> HotwireItemIds => [3458, 3459];

    /// <summary><c>ItemClasses</c> 25075, <c>NAME_ID</c> 14166 = "Hotwire".</summary>
    public const uint HotwireItemClass = 25075;

    /// <summary>
    /// <c>ItemIdUseOptionGroupId</c>: both hotwire items map to option group 55, whose five members
    /// are the four <c>StartVehicle</c> options and one <c>DragAndDropItem</c>.
    /// </summary>
    public const uint HotwireUseOptionGroup = 55;

    /// <summary>
    /// The four <c>StartVehicle</c> options of group 55 — one per drivable family, separated only by
    /// <see cref="StartVehicleTargetRequirementSets"/>. All four carry <c>TARGET_TYPE</c> 2 (a world
    /// target), <c>INPUT_ACTION_KEY</c> <c>enter-gamepad_A</c>, <c>INTERACTION_ANIMATION_ID</c> 2 and
    /// the same busy timer.
    /// </summary>
    public static ReadOnlySpan<uint> StartVehicleOptionIds => [62, 70, 71, 72];

    /// <summary><c>TARGET_REQ_SET_ID</c> of options 62 / 70 / 71 / 72, in that order.</summary>
    public static ReadOnlySpan<uint> StartVehicleTargetRequirementSets => [4726, 4728, 4727, 4725];

    /// <summary>
    /// <c>BUSY_MSEC</c> on all four <c>StartVehicle</c> options: <b>7,000</b>. This is the client own
    /// interaction timer — the server arms it and answers its completion; it never measures a held
    /// key, because the whole <c>GroundVehicle</c> action set is client-side (docs/59 §2.1).
    /// </summary>
    public const int HotwireBusyMs = 7_000;

    /// <summary><c>StringHashToValue</c>: <c>Vehicle.KeyItemId</c> 3460.</summary>
    public const uint KeyItemId = 3_460;

    /// <summary><c>StringHashToValue</c>: <c>Vehicle.KeyClassId</c> 25076.</summary>
    public const uint KeyItemClass = 25_076;

    /// <summary>The second item of class 25076, same <c>NAME_ID</c> 14018 ("Vehicle Key").</summary>
    public const uint SecondKeyItemId = 3_717;

    /// <summary>
    /// <b>Correction to docs/59 §2.5.2.</b> That section says the key is an "instant start" through
    /// <c>ItemUseOptionGroup</c> 63. Group 63 contains <b>no <c>StartVehicle</c> option at all</b>:
    /// its five members are 4 <c>DropItem</c>, 12 <c>RemoveItem</c>, 59 <c>LootItem</c>,
    /// 60 <c>EquipItem</c>, 61 <c>MoveItem</c> and 88 <c>SkinItem</c>, every one of them with
    /// <c>BUSY_MSEC</c> 0. The other key item, 3717, is on group 8 and has not even
    /// <c>EquipItem</c>. So the key does not <i>start</i> a car; the key is <b>equipped into</b> one,
    /// which is what <c>EquipmentSlotDefinitions</c> 67-72 and FTE 47 (<i>"You can quickly remove the
    /// Vehicle Key and Biofuel"</i>, removal 0 ms) describe. A car that has its key <i>already
    /// installed</i> needs no hotwire at all; a car that does not, needs the 7,000 ms
    /// <c>StartVehicle</c> interaction and a Hotwire item.
    /// <para>Only the <c>StartVehicle</c> option is a start. This constant records the corrected
    /// verb so the next lane does not re-derive the wrong one.</para>
    /// </summary>
    public const uint KeyUseOptionGroup = 63;

    /// <summary>Is this item a Hotwire.</summary>
    public static bool IsHotwireItem(uint itemId) => itemId is 3458 or 3459;

    /// <summary>Is this item a Vehicle Key.</summary>
    public static bool IsKeyItem(uint itemId) => itemId is KeyItemId or SecondKeyItemId;
}

/// <summary>Why a start attempt did or did not happen.</summary>
public enum VehicleIgnitionResult
{
    /// <summary>The engine is running now — a car whose key is installed, or ignition is not required.</summary>
    Started,

    /// <summary>A 7,000 ms hotwire is running. The caller re-asks at <c>ReadyAtMs</c>.</summary>
    Armed,

    /// <summary>The hotwire finished and the engine is running.</summary>
    Completed,

    /// <summary>Already running; nothing to do and nothing to send.</summary>
    AlreadyRunning,

    /// <summary>A hotwire is already running on this vehicle for this character.</summary>
    AlreadyArmed,

    /// <summary>No key installed and no Hotwire item carried.</summary>
    NoIgnitionItem,

    /// <summary>Only the driver may start the engine.</summary>
    NotDriver,

    /// <summary>Empty tank. Turning the key does nothing.</summary>
    NoFuel,

    MissingComponents,

    /// <summary>The car is a wreck (condition 0 — FTE 1 <i>"will no longer be operable"</i>).</summary>
    Wrecked,

    /// <summary>The hotwire timer has not elapsed yet.</summary>
    StillBusy,

    /// <summary>There is no hotwire in progress to complete.</summary>
    NotArmed,
}

/// <summary>The answer to one <see cref="VehicleIgnition"/> call.</summary>
/// <param name="Result">What happened.</param>
/// <param name="Vehicle">The car, when one was resolved.</param>
/// <param name="BusyMs">The client busy timer to arm, 0 when there is none.</param>
/// <param name="ReadyAtMs">When <see cref="VehicleIgnition.TryComplete"/> will accept, for an <see cref="VehicleIgnitionResult.Armed"/>.</param>
/// <param name="EngineOn">Whether the caller owes a <c>88 1b Vehicle.Engine</c> and with what value.</param>
public readonly record struct VehicleIgnitionOutcome(
    VehicleIgnitionResult Result,
    MatchVehicle? Vehicle,
    int BusyMs,
    long ReadyAtMs,
    bool EngineOn)
{
    /// <summary>Does the caller owe the client an engine packet.</summary>
    public bool SendsEngine => Result is VehicleIgnitionResult.Started or VehicleIgnitionResult.Completed;
}

/// <summary>
/// Options for the ignition rule. All <b>[DESIGN]</b> — the client carries the timers and the item
/// ids, never the policy.
/// </summary>
public sealed class VehicleIgnitionOptions
{
    public static VehicleIgnitionOptions Default { get; } = new();

    /// <summary>
    /// Must a car be started before it drives.
    ///
    /// <para><b>Default off, and this one is not timidity.</b> Neither Hotwire item (3458/3459) nor
    /// either Vehicle Key (3460/3717) is in <c>z2-loot-tables.json</c> today — docs/59 §2.5.2 says so
    /// and this pass re-checked it. Turning this on before those items spawn would make
    /// <see cref="KeyedFraction"/> the <i>only</i> way to ever drive, i.e. it would take a working
    /// feature away from a play-test the owner is not present for. It goes on in the wave that adds
    /// the loot rows, and <c>CRANBERRY_VEHICLE_IGNITION=1</c> reaches it before then.</para>
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Fraction of parked cars that spawn with their key already installed, so they start on mount
    /// with no hotwire. Seeded from the match seed and the anchor, so the same match parks the same
    /// keyed cars — the same rule the fuel level and the model already follow.
    ///
    /// <para>0.35 makes a keyed car the pleasant exception rather than the rule, which is what hint
    /// 15221 (<i>"No key? Hotwire a car to get it started."</i>) describes as the normal case.</para>
    /// </summary>
    public float KeyedFraction { get; init; } = 0.35f;

    /// <summary>The hotwire timer. Defaults to the client own <c>BUSY_MSEC</c>; a server that armed a
    /// different one would finish out of step with the client progress bar.</summary>
    public int HotwireBusyMs { get; init; } = AugustIgnitionFacts.HotwireBusyMs;
}

/// <summary>
/// The start-a-car rule: key installed = instant, otherwise a Hotwire item and the client own
/// 7,000 ms <c>StartVehicle</c> interaction (docs/61 §4).
///
/// <para><b>What the server owns here is only arming and answering.</b> The 7,000 ms belongs to the
/// client — it is <c>BUSY_MSEC</c> on the four <c>StartVehicle</c> options, and the client draws its
/// own progress bar from it. The server holds the deadline so that a completion that arrives too
/// early is refused, and it holds nothing else. It does <b>not</b> measure a held key: docs/59 §2.1
/// proves the whole <c>GroundVehicle</c> action set is client-side, so the owner recollection of
/// "hold forward for six or seven seconds" is the interact key on the car, not the throttle.</para>
///
/// <para>One instance per session. It reads no clock; the caller passes the instant in.</para>
/// </summary>
public sealed class VehicleIgnition
{
    private readonly Dictionary<ulong, ulong> _keyed = [];
    private ulong _armedVehicle;
    private ulong _armedCharacter;
    private long _armedReadyAtMs;

    /// <summary>The car a hotwire is currently running on, or 0.</summary>
    public ulong ArmedVehicleGuid => _armedVehicle;

    /// <summary>When that hotwire completes.</summary>
    public long ArmedReadyAtMs => _armedReadyAtMs;

    /// <summary>Hotwires that ran to completion this session.</summary>
    public int HotwiresCompleted { get; private set; }

    /// <summary>Starts that needed no hotwire.</summary>
    public int KeyedStarts { get; private set; }

    /// <summary>
    /// Does this car have its key installed. Seeded, so it is stable for the whole match and the same
    /// for any observer that asks.
    /// </summary>
    public bool IsKeyed(MatchVehicle vehicle, ulong matchSeed, VehicleIgnitionOptions options)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(options);

        if (options.KeyedFraction >= 1f)
        {
            return true;
        }

        if (options.KeyedFraction <= 0f)
        {
            return false;
        }

        if (_keyed.TryGetValue(vehicle.Guid, out ulong cached))
        {
            return cached != 0;
        }

        // Same shape as VehicleFleet.SpawnFuel: a splitmix step over (seed, anchor) so the roll is
        // reproducible and independent of the order cars are asked about.
        ulong hash = Mix(matchSeed ^ (vehicle.AnchorInstanceId * 0x9E37_79B9_7F4A_7C15UL) ^ vehicle.Guid);
        float roll = (hash >> 11) * (1.0f / (1UL << 53));
        bool keyed = roll < options.KeyedFraction;
        _keyed[vehicle.Guid] = keyed ? 1UL : 0UL;
        return keyed;
    }

    /// <summary>
    /// Asks to start <paramref name="vehicle"/>. Called at the moment a rider takes the wheel, and
    /// again on a <c>StartVehicle</c> interaction.
    /// </summary>
    /// <param name="vehicle">The car.</param>
    /// <param name="characterGuid">Who is asking.</param>
    /// <param name="carriesHotwire">Does the asker have a Hotwire item (3458/3459) in inventory.</param>
    /// <param name="nowMs">The caller monotonic instant.</param>
    /// <param name="matchSeed">This match seed, for <see cref="IsKeyed"/>.</param>
    /// <param name="fleetOptions">Used only for the wreck test.</param>
    /// <param name="options">The ignition policy.</param>
    public VehicleIgnitionOutcome TryStart(
        MatchVehicle vehicle,
        ulong characterGuid,
        bool carriesHotwire,
        long nowMs,
        ulong matchSeed,
        VehicleFleetOptions fleetOptions,
        VehicleIgnitionOptions options)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(fleetOptions);
        ArgumentNullException.ThrowIfNull(options);

        if (vehicle.ConditionUnder(fleetOptions) == VehicleCondition.Destroyed)
        {
            return new VehicleIgnitionOutcome(VehicleIgnitionResult.Wrecked, vehicle, 0, 0, false);
        }

        if (vehicle.OwnerGuid != characterGuid)
        {
            return new VehicleIgnitionOutcome(VehicleIgnitionResult.NotDriver, vehicle, 0, 0, false);
        }

        if (vehicle.EngineOn)
        {
            return new VehicleIgnitionOutcome(VehicleIgnitionResult.AlreadyRunning, vehicle, 0, 0, true);
        }

        if (!vehicle.Inventory.HasEngineParts)
            return new VehicleIgnitionOutcome(VehicleIgnitionResult.MissingComponents, vehicle, 0, 0, false);

        if (vehicle.Fuel <= 0f)
        {
            return new VehicleIgnitionOutcome(VehicleIgnitionResult.NoFuel, vehicle, 0, 0, false);
        }

        if (vehicle.Definition.VehicleId == 5 || !options.Required || IsKeyed(vehicle, matchSeed, options))
        {
            KeyedStarts++;
            vehicle.EngineOn = true;
            Disarm();
            return new VehicleIgnitionOutcome(VehicleIgnitionResult.Started, vehicle, 0, 0, true);
        }

        if (!carriesHotwire)
        {
            return new VehicleIgnitionOutcome(VehicleIgnitionResult.NoIgnitionItem, vehicle, 0, 0, false);
        }

        if (_armedVehicle == vehicle.Guid && _armedCharacter == characterGuid)
        {
            return new VehicleIgnitionOutcome(
                VehicleIgnitionResult.AlreadyArmed, vehicle, options.HotwireBusyMs, _armedReadyAtMs, false);
        }

        _armedVehicle = vehicle.Guid;
        _armedCharacter = characterGuid;
        _armedReadyAtMs = nowMs + Math.Max(0, options.HotwireBusyMs);
        return new VehicleIgnitionOutcome(
            VehicleIgnitionResult.Armed, vehicle, options.HotwireBusyMs, _armedReadyAtMs, false);
    }

    /// <summary>
    /// Completes an armed hotwire. Refuses early — the client draws the 7,000 ms bar itself, so a
    /// completion before the deadline is a client that lied or a duplicate interaction, never a
    /// legitimate finish.
    /// </summary>
    public VehicleIgnitionOutcome TryComplete(
        MatchVehicle vehicle,
        ulong characterGuid,
        long nowMs,
        VehicleFleetOptions fleetOptions)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(fleetOptions);

        if (_armedVehicle != vehicle.Guid || _armedCharacter != characterGuid)
        {
            return new VehicleIgnitionOutcome(VehicleIgnitionResult.NotArmed, vehicle, 0, 0, false);
        }

        if (nowMs < _armedReadyAtMs)
        {
            return new VehicleIgnitionOutcome(
                VehicleIgnitionResult.StillBusy, vehicle, 0, _armedReadyAtMs, false);
        }

        Disarm();

        if (vehicle.ConditionUnder(fleetOptions) == VehicleCondition.Destroyed)
        {
            return new VehicleIgnitionOutcome(VehicleIgnitionResult.Wrecked, vehicle, 0, 0, false);
        }

        if (vehicle.OwnerGuid != characterGuid)
        {
            return new VehicleIgnitionOutcome(VehicleIgnitionResult.NotDriver, vehicle, 0, 0, false);
        }

        if (!vehicle.Inventory.HasEngineParts)
            return new VehicleIgnitionOutcome(VehicleIgnitionResult.MissingComponents, vehicle, 0, 0, false);

        if (vehicle.Fuel <= 0f)
        {
            return new VehicleIgnitionOutcome(VehicleIgnitionResult.NoFuel, vehicle, 0, 0, false);
        }

        HotwiresCompleted++;
        vehicle.EngineOn = true;
        return new VehicleIgnitionOutcome(VehicleIgnitionResult.Completed, vehicle, 0, 0, true);
    }

    /// <summary>
    /// Cancels an armed hotwire — the player walked away, got out, or died. Returns whether one was
    /// running, so the caller can log it.
    /// </summary>
    public bool Cancel(ulong characterGuid)
    {
        if (_armedVehicle == 0 || (_armedCharacter != characterGuid && characterGuid != 0))
        {
            return false;
        }

        Disarm();
        return true;
    }

    /// <summary>Drops the whole ignition memory — a match reset.</summary>
    public void Clear()
    {
        _keyed.Clear();
        Disarm();
    }

    private void Disarm()
    {
        _armedVehicle = 0;
        _armedCharacter = 0;
        _armedReadyAtMs = 0;
    }

    private static ulong Mix(ulong value)
    {
        value += 0x9E37_79B9_7F4A_7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return value ^ (value >> 31);
    }
}
