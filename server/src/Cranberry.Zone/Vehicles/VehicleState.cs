using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Vehicles;

/// <summary>
/// The server's constants for things the August build genuinely does not carry.
///
/// <para><b>Everything here is a Cranberry decision and is labelled as such</b> (docs/00, docs/43
/// §8). The client ships a complete PhysX vehicle setup but <c>Vehicles.txt</c> MASS, HEALTH,
/// EXPLODE_*, RAM_* are 0 on all eight rows; <c>VehicleResourceMappings.txt</c> is header-only;
/// <c>ResourceTypeFuel</c>'s BURN_PER_MSEC and BURN_TICK_MSEC are 0; and
/// <c>DamageLevelInfo.MOVE_INFO_OVERRIDE</c> is 0 on all twelve rows. So max health, the burn rate,
/// the refuel amount and the damage→drive-mode map are not "not yet found" — they are absent, and
/// the server must choose them.</para>
/// </summary>
public sealed record VehicleFleetOptions
{
    /// <summary>
    /// Vehicle condition at spawn — <b>100,000</b>.
    ///
    /// <para><c>Vehicles.txt HEALTH</c> really is 0 on every row and <c>VehicleResistMappings</c>
    /// really is empty, but the number is not absent from the build after all: the condition bar
    /// rides on <c>Resources.txt</c> row <b>561</b> (<c>RESOURCE_TYPE</c> 1
    /// <c>ResourceTypeHealth</c>), whose <c>INITIAL_VALUE</c> and <c>MAX_VALUE</c> are both
    /// <b>100,000</b> with <c>PACKET_BROADCAST_RANGE</c> 30. The owner's Z1 names the same resource
    /// id (<c>C:\Z1\Server\Zone\ZoneVehicleResources.cs:132-138</c>) and gives every vehicle
    /// <c>Condition = 100_000</c> (<c>ZoneVehicleRegistry.cs:253, 277, 316</c>), so the client sheet
    /// and the owner's own tuned value agree exactly. This replaces the 25,000 Cranberry chose
    /// before either was known. [P-data + D53]</para>
    /// </summary>
    public uint MaxHealth { get; init; } = Rulings.VehiclesPlan.MaxHealth;

    /// <summary>
    /// Tank size. This one <b>is</b> the client's: <c>Resources.txt</c> id 50
    /// <c>ResourceTypeFuel</c> MAX = 10000. [P-data]
    /// </summary>
    public float MaxFuel { get; init; } = Rulings.VehiclesPlan.MaxFuel;

    /// <summary>Lowest fraction of a tank a parked car may be found with. [DESIGN]</summary>
    public float MinimumSpawnFuelFraction { get; init; } = Rulings.VehiclesPlan.MinimumSpawnFuelFraction;

    /// <summary>Highest fraction of a tank a parked car may be found with. [DESIGN]</summary>
    public float MaximumSpawnFuelFraction { get; init; } = Rulings.VehiclesPlan.MaximumSpawnFuelFraction;

    /// <summary>
    /// Fuel burnt per second with the engine running. A full tank lasts about 21 minutes, which is
    /// longer than a round — so fuel is a pressure on a car found half-empty, not a timer on the
    /// match. The client carries no burn rate at all. [DESIGN]
    /// </summary>
    public float FuelBurnPerSecond { get; init; } = Rulings.VehiclesPlan.FuelBurnPerSecond;

    /// <summary>
    /// What one Biofuel (item 73) or Ethanol (1384) adds. The client's own locale for the item reads
    /// <i>"This is enough biofuel to fill up a quarter of a tank"</i>, so a quarter of 10000 is the
    /// obvious reading — but PARAM1..3 are 0/1/0 on both rows, so the number itself is ours. [INF]
    /// </summary>
    public float RefuelAmount { get; init; } = Rulings.VehiclesPlan.RefuelAmount;

    /// <summary>
    /// The largest horizontal distance per second the server will accept from an owner's reported
    /// pose before treating it as a teleport and refusing it.
    ///
    /// <para><b>Deliberately not derived from <c>MAX_FORWARD</c>.</b> The client's sheets declare no
    /// units for it or for <c>ESTIMATED_MAX_SPEED</c> (docs/43 §2.3 note), so reading 105 as
    /// "105 m/s" would be a guess dressed as a fact. This is a plain server bound, generous enough
    /// that no legitimate drive trips it and tight enough that a warp does. [DESIGN]</para>
    /// </summary>
    public float MaxPoseSpeedMetresPerSecond { get; init; } = Rulings.VehiclesPlan.MaxPoseSpeedMetresPerSecond;

    /// <summary>
    /// Below this fraction of <see cref="MaxHealth"/> the vehicle drops to a degraded drive mode.
    ///
    /// <para><b>The ladder is the owner's, adopted under D53</b>: his own vehicles degrade at
    /// 50,000 / 35,000 / 20,000 / 10,000 of a 100,000 condition bar
    /// (<c>C:\Z1\Server\Zone\ZoneVehicleResources.cs:123-126</c>), i.e. 50 / 35 / 20 / 10 %.
    /// Cranberry used to carry <c>DamageLevelInfo</c>'s own 75 / 50 / 25 % here; those three
    /// percentages are real client numbers but they are the <i>hit-indicator</i> levels, and
    /// adopting the owner's 100,000 without his ladder would have changed a car's behaviour twice.
    /// The drive modes each band selects are still ours (docs/43 §8.3). [D53 + DESIGN]</para>
    /// </summary>
    public float DamagedFraction { get; init; } = Rulings.VehiclesPlan.DamagedFraction;

    /// <inheritdoc cref="DamagedFraction"/>
    public float BadlyDamagedFraction { get; init; } = Rulings.VehiclesPlan.BadlyDamagedFraction;

    /// <summary>At or below this the car is a wreck that will not move (the <c>MAX_FORWARD</c> 0 row).</summary>
    public float CrippledFraction { get; init; } = Rulings.VehiclesPlan.CrippledFraction;

    /// <summary>
    /// The fourth and last band of the owner's ladder — 10,000 of 100,000. It selects no new drive
    /// mode (the <c>MAX_FORWARD</c> 0 row is already taken by <see cref="CrippledFraction"/>); what
    /// it selects is the fourth <c>VEH_Damage_&lt;family&gt;_Stage04</c> composite effect, the one
    /// that is on fire. [D53]
    /// </summary>
    public float CriticalFraction { get; init; } = Rulings.VehiclesPlan.CriticalFraction;
}

/// <summary>How healthy a vehicle is, and therefore which <c>MoveInfo</c> ordinal it drives on.</summary>
public enum VehicleCondition
{
    /// <summary>Full drive modes — ordinals 0/1 (<c>MAX_FORWARD</c> 85–105).</summary>
    Intact,

    /// <summary>Below 75 % — ordinal 6, <c>MOVEMENT_MODE</c> 11, <c>MAX_FORWARD</c> 65.</summary>
    Damaged,

    /// <summary>Below 50 % — ordinal 7, <c>MOVEMENT_MODE</c> 12.</summary>
    BadlyDamaged,

    /// <summary>Below 25 % — ordinal 5, the row whose <c>MAX_FORWARD</c> is 0. It will not drive.</summary>
    Crippled,

    /// <summary>Health 0: the destroyed model, no occupants, no ownership.</summary>
    Destroyed,
}

/// <summary>Why a mount, seat change or dismount was refused.</summary>
public enum VehicleActionResult
{
    Ok,
    NoSuchVehicle,
    NoSuchSeat,
    SeatOccupied,
    AlreadyMounted,
    NotMounted,

    /// <summary>Inside <c>VehicleInteractionCooldownMs</c> / <c>VehicleSeatSwapCooldownMs</c> — the
    /// client's own "Too early to exit vehicle" / "Too early to change seats." guards.</summary>
    Cooldown,

    /// <summary>Above <c>Vehicle.DefaultMaxDismountSpeed</c> — "Vehicle is moving too fast to exit."</summary>
    TooFast,

    /// <summary>The vehicle is a wreck.</summary>
    Destroyed,
}

/// <summary>
/// One vehicle in a running match: where it is, who is in which seat, and the little state the server
/// is actually responsible for.
///
/// <para><b>There is no physics here, by design and by evidence.</b> The owning client simulates the
/// car and streams its pose back as <c>0x90</c>; the server's job is possession, the authoritative
/// last-known pose, a sanity bound on what the owner claims, fuel/condition, and re-broadcast
/// (docs/43 §0.1, §3.4). <see cref="Position"/> is therefore always <i>the last pose an owner
/// reported</i> or the spawn pose — never an integration.</para>
/// </summary>
public sealed class MatchVehicle
{
    private readonly ulong[] _seats;

    public MatchVehicle(
        ulong guid,
        uint transientId,
        VehicleDefinition definition,
        Vector3 position,
        float yaw,
        uint health,
        float fuel)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Guid = guid;
        TransientId = transientId;
        Definition = definition;
        Position = position;
        Yaw = yaw;
        Health = health;
        Fuel = fuel;
        _seats = new ulong[definition.SeatCount];
        Inventory = new VehicleInventory(guid, transientId, definition.VehicleId);
    }

    public VehicleInventory Inventory { get; }

    public ulong Guid { get; }

    public uint TransientId { get; }

    public VehicleDefinition Definition { get; }

    /// <summary>Last known pose. Promoted from the owner's stream; never integrated here.</summary>
    public Vector3 Position { get; private set; }

    public float Yaw { get; private set; }

    public Quaternion? LastRotation { get; internal set; }
    public uint LastClientTime { get; internal set; }
    public byte MovementVersion { get; internal set; }

    /// <summary>The last complete pose, including the initial authored slope, for <c>0xd7</c>.</summary>
    public Vector4 Rotation
    {
        get
        {
            if (LastRotation is { } rotation)
                return new Vector4(rotation.X, rotation.Y, rotation.Z, rotation.W);
            float half = Yaw * 0.5f;
            return new Vector4(0f, MathF.Sin(half), 0f, MathF.Cos(half));
        }
    }

    /// <summary>
    /// Current driver authority. When the driver gets out this is zero; the native simulation
    /// lease remains with <see cref="CoastingOwnerGuid"/> until a confirmed rest handoff.
    /// </summary>
    public ulong OwnerGuid { get; internal set; }

    /// <summary>The previous driver simulates this unoccupied car until its explicit rest
    /// report, stream-out or a new driver. Silence alone never freezes a moving car.</summary>
    public ulong CoastingOwnerGuid { get; internal set; }
    public ulong LastRightingPlayer { get; internal set; }
    public long LastRightingMs { get; internal set; } = long.MinValue;
    public long CoastStartedMs { get; internal set; } = long.MinValue;
    public long CoastRestSinceMs { get; internal set; } = long.MinValue;

    public void EndCoast()
    {
        CoastingOwnerGuid = 0;
        CoastStartedMs = CoastRestSinceMs = long.MinValue;
    }

    // No time-based coast cutoff. The explicit native rest report performs a synchronous
    // transform handoff and full remote baseline in ZoneService; this pump only evicts wrecks.
    public bool CoastCanRelease(long nowMs) => CoastingOwnerGuid != 0 && Health == 0;

    /// <summary>Set by <c>88 1b Vehicle.Engine</c>, which is what actually starts the engine loop.</summary>
    public bool EngineOn { get; set; }
    public bool HornOn { get; internal set; }
    public bool HeadlightsOn { get; internal set; }
    public bool SirenOn { get; internal set; }
    public uint? SkinShaderGroup { get; set; }

    public uint Health { get; internal set; }

    /// <summary>Shared match deadline; null until the first lethal damage event.</summary>
    public long? WreckExpiresAtMs { get; internal set; }

    public float Fuel { get; set; }

    /// <summary>
    /// The last <c>88 27 Vehicle.CurrentMoveMode</c> byte the owner reported, or null. <b>Recorded,
    /// not interpreted</b> — the enum has no name strings in the image and the one live value (5,
    /// under a parachute canopy) is not a <c>MOVEMENT_MODE</c> (docs/43 §4.5).
    /// </summary>
    public byte? ReportedMoveMode { get; set; }

    /// <summary>Milliseconds of the last mount/dismount, for the client's own interaction cooldown.</summary>
    public long LastInteractionMs { get; internal set; } = long.MinValue;

    /// <summary>Milliseconds of the last seat change, for the seat-swap cooldown.</summary>
    public long LastSeatChangeMs { get; internal set; } = long.MinValue;

    /// <summary>Milliseconds of the last accepted owner pose, or <see cref="long.MinValue"/>.</summary>
    public long LastPoseMs { get; internal set; } = long.MinValue;

    /// <summary>How many owner poses were refused as implausible. Diagnostics for the referee role.</summary>
    public int RejectedPoses { get; internal set; }

    /// <summary>
    /// Horizontal metres per second between the last two accepted owner poses, or 0.
    ///
    /// <para>This is what <c>TryExit</c>'s <c>speed</c> argument was always meant to be given.
    /// <c>ZoneService.TryExitVehicle</c> passed a hardcoded <c>0f</c>, so
    /// <c>Vehicle.DefaultMaxDismountSpeed = 12</c> was never enforced and
    /// <c>Vehicle.DefaultMinDismountDamageSpeed = 10</c> never applied bail damage — a player could
    /// step out of a car at full speed and take nothing (AUDIT-vehicles gap 10).</para>
    /// </summary>
    public float LastSpeed { get; internal set; }

    /// <summary>The <c>Z2.zone</c> placement this car spawned on, for the log.</summary>
    public uint AnchorInstanceId { get; init; }

    /// <summary>
    /// The burst window over the client's own <c>8e 01</c> impact ramps for THIS car, so one wall
    /// costs its peak once. See <see cref="VehicleDamageOptions.CollisionBurstWindowMs"/>.
    /// </summary>
    public CollisionBurst Collision = CollisionBurst.Fresh;

    /// <summary>
    /// Is the car on its roof, as of the last pose its owner streamed. Set only from
    /// <see cref="VehicleFlipDetector"/> against a rotation the client authored — the server never
    /// integrates a vehicle.
    /// </summary>
    public bool UpsideDown { get; internal set; }

    /// <summary>When the last <c>UPSIDE_DOWN_DAMAGE_PULSE</c> was charged, or the sentinel.</summary>
    public long LastFlipPulseMs { get; internal set; } = long.MinValue;
    public long UpsideDownSinceMs { get; internal set; } = long.MinValue;

    /// <summary>
    /// Which <c>VEH_Damage_&lt;family&gt;_Stage0n</c> band the car is in: 0 intact, 1..4 as the
    /// condition falls. Held so the stage effect is played on a CROSSING, not on every hit.
    /// </summary>
    public int DamageStage { get; internal set; }

    /// <summary>Occupant guids by seat index; 0 is empty.</summary>
    public ReadOnlySpan<ulong> Seats => _seats;

    public ulong DriverGuid => _seats.Length == 0 ? 0 : _seats[Definition.DriverSeat.Index];

    public bool IsEmpty
    {
        get
        {
            foreach (ulong occupant in _seats)
            {
                if (occupant != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public int OccupantCount
    {
        get
        {
            int count = 0;
            foreach (ulong occupant in _seats)
            {
                if (occupant != 0)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Which seat a character is in, or −1.</summary>
    public int SeatOf(ulong characterGuid)
    {
        if (characterGuid == 0)
        {
            return -1;
        }

        for (int seat = 0; seat < _seats.Length; seat++)
        {
            if (_seats[seat] == characterGuid)
            {
                return seat;
            }
        }

        return -1;
    }

    public bool IsSeatOccupied(int seat) => seat >= 0 && seat < _seats.Length && _seats[seat] != 0;

    /// <summary>The first free seat, driver first, or −1 when the car is full.</summary>
    public int FirstFreeSeat()
    {
        for (int seat = 0; seat < _seats.Length; seat++)
        {
            if (_seats[seat] == 0)
            {
                return seat;
            }
        }

        return -1;
    }

    /// <summary>
    /// The occupant list <c>88 01 Vehicle.Owner</c> and <c>88 02 Vehicle.Occupy</c> carry, in seat
    /// order.
    /// </summary>
    public IReadOnlyList<VehicleOccupantSlot> Occupants()
    {
        var occupants = new List<VehicleOccupantSlot>(OccupantCount);
        for (int seat = 0; seat < _seats.Length; seat++)
        {
            if (_seats[seat] != 0)
            {
                occupants.Add(new VehicleOccupantSlot((byte)seat, _seats[seat]));
            }
        }

        return occupants;
    }

    /// <summary>Health as a fraction of <paramref name="options"/>' maximum, clamped to 0..1.</summary>
    public float HealthFraction(VehicleFleetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.MaxHealth == 0 ? 0f : Math.Clamp(Health / (float)options.MaxHealth, 0f, 1f);
    }

    public VehicleCondition ConditionUnder(VehicleFleetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Health == 0)
        {
            return VehicleCondition.Destroyed;
        }

        float fraction = HealthFraction(options);
        return fraction <= options.CrippledFraction ? VehicleCondition.Crippled
            : fraction <= options.BadlyDamagedFraction ? VehicleCondition.BadlyDamaged
            : fraction <= options.DamagedFraction ? VehicleCondition.Damaged
            : VehicleCondition.Intact;
    }

    /// <summary>
    /// The owner's four-band damage ladder, as an ordinal 0..4 — the number that selects the
    /// <c>VEH_Damage_&lt;family&gt;_Stage0n</c> composite effect. Separate from
    /// <see cref="ConditionUnder"/> because the ladder has FOUR bands and the drive-mode enum has
    /// three: 10 % selects the burning effect but no new <c>MoveInfo</c> row.
    /// [D53, <c>C:\Z1\Server\Zone\ZoneVehicleResources.cs:123-126, 351-358</c>]
    /// </summary>
    public int DamageStageUnder(VehicleFleetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        float fraction = HealthFraction(options);
        return fraction <= options.CriticalFraction ? 4
            : fraction <= options.CrippledFraction ? 3
            : fraction <= options.BadlyDamagedFraction ? 2
            : fraction <= options.DamagedFraction ? 1
            : 0;
    }

    /// <summary>
    /// Which <c>MoveInfo</c> ordinal this vehicle's condition selects.
    ///
    /// <para><b>The mapping is Cranberry's.</b> The client's three damage levels exist and their
    /// 75/50/25 % thresholds are its own, but <c>MOVE_INFO_OVERRIDE</c> is 0 on all twelve rows, so
    /// nothing in the build says which level picks which mode. Ordinals 6 and 7 are the obvious
    /// degraded pair (<c>MOVEMENT_MODE</c> 11/12, <c>MAX_FORWARD</c> 65 and 60–70 against 85–105)
    /// and ordinal 5 is the only row whose <c>MAX_FORWARD</c> is 0 (docs/43 §2.2, §8.3).</para>
    /// </summary>
    public int DriveModeOrdinalUnder(VehicleFleetOptions options) =>
        ConditionUnder(options) switch
        {
            VehicleCondition.Intact => 0,
            VehicleCondition.Damaged => 6,
            VehicleCondition.BadlyDamaged => 7,
            _ => 5,
        };

    internal void Seat(int seat, ulong characterGuid) => _seats[seat] = characterGuid;

    internal void ClearSeats() => Array.Clear(_seats);

    internal void SetPose(Vector3 position, float yaw)
    {
        Position = position;
        Yaw = yaw;
    }

    public override string ToString() =>
        $"{Definition.Name} guid={Guid} transient={TransientId} at {Position} "
        + $"({OccupantCount}/{Definition.SeatCount} seats, {(EngineOn ? "running" : "off")})";
}

/// <summary>
/// Every vehicle in one match, and the arbitration the server owes the client: who may sit where,
/// the cooldowns the client itself enforces, and whether a reported pose is plausible.
///
/// <para>The fleet decides; it never sends. Callers turn a <see cref="VehicleActionResult.Ok"/> into
/// the packet burst (docs/43 §4.2–§4.6) — this type stays free of the wire so it can be tested
/// without a connection.</para>
/// </summary>
public sealed partial class VehicleFleet
{
    private readonly Dictionary<ulong, MatchVehicle> _byGuid = [];
    private readonly Dictionary<uint, MatchVehicle> _byTransient = [];
    private readonly Dictionary<ulong, MatchVehicle> _byOccupant = [];

    public VehicleFleet(VehicleRoster roster, VehicleFleetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(roster);
        Roster = roster;
        Options = options ?? new VehicleFleetOptions();
    }

    public VehicleRoster Roster { get; }

    public VehicleFleetOptions Options { get; }

    public int Count => _byGuid.Count;

    /// <summary>Monotonic allocation count, including wrecks already removed from the fleet.</summary>
    public int CreatedCount { get; private set; }

    public IEnumerable<MatchVehicle> Vehicles => _byGuid.Values;

    /// <summary>
    /// Realises a plan into live vehicles. <paramref name="guidFor"/> and
    /// <paramref name="transientFor"/> come from the zone's own allocators — this type never invents
    /// an id, because guid and transient-id ranges are the zone's to own.
    /// </summary>
    public IReadOnlyList<MatchVehicle> Populate(
        IReadOnlyList<PlannedVehicle> plan,
        Func<int, ulong> guidFor,
        Func<int, uint> transientFor,
        ulong matchSeed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(guidFor);
        ArgumentNullException.ThrowIfNull(transientFor);

        var created = new List<MatchVehicle>(plan.Count);
        for (int i = 0; i < plan.Count; i++)
        {
            PlannedVehicle planned = plan[i];
            var vehicle = new MatchVehicle(
                guid: guidFor(i),
                transientId: transientFor(i),
                definition: Roster.Require(planned.VehicleId),
                position: planned.Position,
                yaw: planned.Yaw,
                health: Options.MaxHealth,
                fuel: SpawnFuel(matchSeed, planned.AnchorInstanceId))
            {
                AnchorInstanceId = planned.AnchorInstanceId,
                LastRotation = planned.Pitch == 0 && planned.Roll == 0 ? null
                    : Quaternion.CreateFromYawPitchRoll(planned.Yaw, planned.Pitch, planned.Roll),
            };

            Add(vehicle);
            created.Add(vehicle);
        }

        return created;
    }

    /// <summary>
    /// How full a parked car is found. Seeded on the anchor's own instance id, exactly as loot is,
    /// so the same match seed finds the same tank in the same driveway. [DESIGN]
    /// </summary>
    public float SpawnFuel(ulong matchSeed, uint anchorInstanceId)
    {
        unchecked
        {
            var random = new SplitMix64((matchSeed * 0x9E37_79B9_7F4A_7C15UL) ^ (anchorInstanceId + 0x4655_454CUL));
            float fraction = Options.MinimumSpawnFuelFraction
                + ((Options.MaximumSpawnFuelFraction - Options.MinimumSpawnFuelFraction)
                    * (random.Next() >> 11) / (float)(1UL << 53));
            return Options.MaxFuel * fraction;
        }
    }

    public void Add(MatchVehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        if (!_byGuid.TryAdd(vehicle.Guid, vehicle))
        {
            throw new ArgumentException($"Vehicle guid {vehicle.Guid} is already in the fleet.", nameof(vehicle));
        }

        if (!_byTransient.TryAdd(vehicle.TransientId, vehicle))
        {
            _byGuid.Remove(vehicle.Guid);
            throw new ArgumentException(
                $"Vehicle transient id {vehicle.TransientId} is already in the fleet.", nameof(vehicle));
        }
        CreatedCount++;
    }

    public bool TryGet(ulong guid, [NotNullWhen(true)] out MatchVehicle? vehicle) =>
        _byGuid.TryGetValue(guid, out vehicle);

    public bool TryGetByTransient(uint transientId, [NotNullWhen(true)] out MatchVehicle? vehicle) =>
        _byTransient.TryGetValue(transientId, out vehicle);

    /// <summary>Authority for both positional reports and sparse speed/attitude updates.</summary>
    public bool TryGetForSimulator(uint transientId, ulong reporterGuid,
        [NotNullWhen(true)] out MatchVehicle? vehicle)
    {
        if (_byTransient.TryGetValue(transientId, out vehicle) && reporterGuid != 0
            && (vehicle.OwnerGuid == reporterGuid
                || (vehicle.OwnerGuid == 0 && vehicle.CoastingOwnerGuid == reporterGuid)))
            return true;
        vehicle = null;
        return false;
    }

    /// <summary>The vehicle a character is currently sitting in, or null.</summary>
    public bool TryGetForOccupant(ulong characterGuid, [NotNullWhen(true)] out MatchVehicle? vehicle) =>
        _byOccupant.TryGetValue(characterGuid, out vehicle);

    /// <summary>
    /// Seats <paramref name="characterGuid"/> in <paramref name="seatIndex"/> of
    /// <paramref name="vehicleGuid"/>, or says why not. Pass a negative
    /// <paramref name="seatIndex"/> to take the first free seat (driver first).
    ///
    /// <para>On success the caller sends, in this order (docs/43 §9 step 2):
    /// <c>0f 3b</c> grant when the rider took the driver's seat → <c>88 01</c> →
    /// <c>70 02(seat, isDriver)</c> → <c>88 02</c> → <c>88 1b(engineOn)</c>.</para>
    /// </summary>
    public VehicleActionResult TryEnter(
        ulong vehicleGuid,
        ulong characterGuid,
        int seatIndex,
        long nowMs,
        out MatchVehicle? vehicle,
        out int seat)
    {
        seat = -1;
        if (!_byGuid.TryGetValue(vehicleGuid, out vehicle))
        {
            return VehicleActionResult.NoSuchVehicle;
        }

        if (vehicle.ConditionUnder(Options) == VehicleCondition.Destroyed)
        {
            return VehicleActionResult.Destroyed;
        }

        if (_byOccupant.ContainsKey(characterGuid))
        {
            return VehicleActionResult.AlreadyMounted;
        }

        if (IsCoolingDown(vehicle.LastInteractionMs, nowMs, Roster.Constants.InteractionCooldownMs))
        {
            return VehicleActionResult.Cooldown;
        }

        int target = seatIndex < 0 ? vehicle.FirstFreeSeat() : seatIndex;
        if (target < 0 || !vehicle.Definition.TryGetSeat(target, out _))
        {
            return seatIndex < 0 ? VehicleActionResult.SeatOccupied : VehicleActionResult.NoSuchSeat;
        }

        if (vehicle.IsSeatOccupied(target))
        {
            return VehicleActionResult.SeatOccupied;
        }

        vehicle.Seat(target, characterGuid);
        _byOccupant[characterGuid] = vehicle;
        vehicle.LastInteractionMs = nowMs;

        // Simulation follows the driver. A passenger never owns the car, which is exactly what
        // FUN_140e6e8b0 and FUN_140b04c90 key on: the owner guid, and nothing else.
        if (vehicle.Definition.Seats[target].IsDriver)
        {
            // Parked time is not driving time: do not give a stale spawn pose minutes of
            // travel allowance when a different physics controller takes over.
            if (vehicle.CoastingOwnerGuid != characterGuid && vehicle.LastPoseMs != long.MinValue)
                vehicle.LastPoseMs = nowMs;
            vehicle.EndCoast();
            vehicle.OwnerGuid = characterGuid;
        }

        seat = target;
        return VehicleActionResult.Ok;
    }

    /// <summary>
    /// Takes <paramref name="characterGuid"/> out of whatever it is in.
    /// Full-speed exits are allowed by the owner's policy. Speed is retained in the API for
    /// callers reporting the current motion; it does not restrict leaving the seat.
    ///
    /// <para>On success the caller sends <c>70 04</c> → cleared <c>88 02</c> → cleared <c>88 01</c>
    /// → <c>11 0039 ManagedObjectResponseControl(false)</c> → <c>0f 3b(0,0)</c>, and — unlike the
    /// parachute — <b>no <c>0f 01 RemovePlayer</c>: the car stays in the world</b> (docs/43 §4.6).</para>
    /// </summary>
    public VehicleActionResult TryExit(
        ulong characterGuid,
        long nowMs,
        float speed,
        out MatchVehicle? vehicle,
        out int seat)
    {
        seat = -1;
        if (!_byOccupant.TryGetValue(characterGuid, out vehicle))
        {
            return VehicleActionResult.NotMounted;
        }

        if (IsCoolingDown(vehicle.LastInteractionMs, nowMs, Roster.Constants.InteractionCooldownMs))
        {
            return VehicleActionResult.Cooldown;
        }

        seat = vehicle.SeatOf(characterGuid);
        vehicle.Seat(seat, 0);
        _byOccupant.Remove(characterGuid);
        vehicle.LastInteractionMs = nowMs;

        if (vehicle.OwnerGuid == characterGuid)
        {
            // Release the seat now, but let the client finish the physical coast before revoking
            // its managed object. Revoking immediately freezes both the car and its tyre state.
            vehicle.CoastingOwnerGuid = characterGuid;
            vehicle.CoastStartedMs = nowMs;
            vehicle.CoastRestSinceMs = long.MinValue;
            vehicle.OwnerGuid = 0;
            vehicle.EngineOn = false;
        }

        return VehicleActionResult.Ok;
    }

    /// <summary>
    /// Moves an occupant to another seat of the same vehicle. The client refuses this while the
    /// vehicle is moving ("You cannot switch seats while the vehicle is moving.") and inside
    /// <c>VehicleSeatSwapCooldownMs</c> (250), so the server applies both.
    ///
    /// <para><b>Note the c2s trigger is blocked</b>: <c>70 0a Mount.SeatChangeRequest</c>'s body is
    /// not recoverable from the binary — the Command/Mount send path has no per-packet serializer,
    /// the same reason docs/36 gives for <c>09 07 InteractRequest</c> — so this is reachable today
    /// only from a dev command, and the s2c half (<see cref="SeatChangeResponse"/>) is what it
    /// answers with. One live capture of a passenger pressing the seat key unblocks it
    /// (docs/43 §5.3, blocker 2).</para>
    /// </summary>
    public VehicleActionResult TryChangeSeat(
        ulong characterGuid,
        int seatIndex,
        long nowMs,
        float speed,
        out MatchVehicle? vehicle)
    {
        if (!_byOccupant.TryGetValue(characterGuid, out vehicle))
        {
            return VehicleActionResult.NotMounted;
        }

        if (IsCoolingDown(vehicle.LastSeatChangeMs, nowMs, Roster.Constants.SeatSwapCooldownMs))
        {
            return VehicleActionResult.Cooldown;
        }

        if (speed > 0f)
        {
            return VehicleActionResult.TooFast;
        }

        if (!vehicle.Definition.TryGetSeat(seatIndex, out VehicleSeatDefinition? seat))
        {
            return VehicleActionResult.NoSuchSeat;
        }

        if (vehicle.IsSeatOccupied(seatIndex))
        {
            return VehicleActionResult.SeatOccupied;
        }

        int from = vehicle.SeatOf(characterGuid);
        vehicle.Seat(from, 0);
        vehicle.Seat(seatIndex, characterGuid);
        vehicle.LastSeatChangeMs = nowMs;

        // Ownership follows the wheel in both directions.
        if (seat.IsDriver)
        {
            // Changing seats can hand control to a different client after a long parked period,
            // just like TryEnter. That idle time is not a legitimate movement allowance.
            if (vehicle.CoastingOwnerGuid != characterGuid && vehicle.LastPoseMs != long.MinValue)
                vehicle.LastPoseMs = nowMs;
            vehicle.EndCoast();
            vehicle.OwnerGuid = characterGuid;
        }
        else if (vehicle.OwnerGuid == characterGuid)
        {
            vehicle.CoastingOwnerGuid = characterGuid;
            vehicle.CoastStartedMs = nowMs;
            vehicle.CoastRestSinceMs = long.MinValue;
            vehicle.OwnerGuid = 0;
            vehicle.EngineOn = false;
        }

        return VehicleActionResult.Ok;
    }

    /// <summary>
    /// Accepts a pose the owning client reported for one of its managed vehicles, or refuses it.
    ///
    /// <para>This is the whole of the server's "referee" role over movement (docs/43 §3.4): the pose
    /// itself is the client's — Cranberry never authors one for an owned vehicle — but the server
    /// keeps the authoritative last-known copy for players outside the owner's interest range and
    /// for the moment the owner disconnects, and it refuses a delta no legitimate drive could
    /// produce.</para>
    ///
    /// <para>Refusal keeps the previous pose rather than snapping the car, and counts the reject; it
    /// never disconnects, because a stall in the owner's uplink looks exactly like a small
    /// teleport.</para>
    /// </summary>
    public bool TryApplyOwnerPose(
        uint transientId,
        ulong reporterGuid,
        Vector3 position,
        float yaw,
        long nowMs,
        [NotNullWhen(true)] out MatchVehicle? vehicle)
    {
        // Only the owner's stream is authoritative for this object; anyone else claiming it is a
        // spoof, or a stale pose from the previous driver.
        if (!TryGetForSimulator(transientId, reporterGuid, out vehicle)) return false;

        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
            || !float.IsFinite(yaw)) return false;

        if (vehicle.LastPoseMs != long.MinValue)
        {
            // Adjacent packets and a first post-grant report can share a server millisecond.
            // That must not bypass the distance guard and authorize the cached spawn position.
            float seconds = Math.Max(1L, nowMs - vehicle.LastPoseMs) / 1000f;
            float dx = position.X - vehicle.Position.X;
            float dz = position.Z - vehicle.Position.Z;
            float travelled = MathF.Sqrt((dx * dx) + (dz * dz));
            if (travelled > Options.MaxPoseSpeedMetresPerSecond * seconds)
            {
                vehicle.RejectedPoses++;
                vehicle = null;
                return false;
            }

            // The dismount guard's own number, measured rather than assumed: metres per second
            // between two poses the owner authored and this server accepted.
            vehicle.LastSpeed = seconds > 0f ? travelled / seconds : 0f;
            if (vehicle.CoastingOwnerGuid != 0)
            {
                float spatialSpeed = Vector3.Distance(position, vehicle.Position) / seconds;
                if (spatialSpeed > 0.1f) vehicle.CoastRestSinceMs = long.MinValue;
                else if (vehicle.CoastRestSinceMs == long.MinValue) vehicle.CoastRestSinceMs = nowMs;
            }
        }

        vehicle.SetPose(position, yaw);
        vehicle.LastPoseMs = nowMs;
        return true;
    }

    /// <summary>
    /// Records whether the owning client's latest pose has this car on its roof, and says whether
    /// that answer just changed. The rotation is the client's — the server integrates nothing —
    /// and the threshold is <see cref="VehicleDamageOptions.UpsideDownDotThreshold"/>.
    /// </summary>
    public bool NoteAttitude(MatchVehicle vehicle, System.Numerics.Quaternion rotation, float threshold)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        if (!float.IsFinite(rotation.LengthSquared()) || rotation.LengthSquared() < 0.0001f)
            return false;
        vehicle.LastRotation = Quaternion.Normalize(rotation);
        bool upsideDown = VehicleFlipDetector.IsUpsideDown(rotation, threshold);
        if (upsideDown == vehicle.UpsideDown)
        {
            return false;
        }

        vehicle.UpsideDown = upsideDown;
        // A car that has just been righted owes no pulse for the time it spent inverted, and a car
        // that has just gone over owes its first pulse a full period from now, not immediately.
        vehicle.LastFlipPulseMs = Environment.TickCount64;
        vehicle.UpsideDownSinceMs = upsideDown ? vehicle.LastFlipPulseMs : long.MinValue;
        return true;
    }

    /// <summary>
    /// Burns fuel on every running vehicle and stops the engine on any that runs dry. Returns the
    /// vehicles whose engine just stopped, so the caller can send
    /// <c>88 1b Vehicle.Engine(off)</c> for each.
    /// </summary>
    public IReadOnlyList<MatchVehicle> BurnFuel(float elapsedSeconds, ulong? driverGuid = null)
    {
        if (elapsedSeconds <= 0f || Options.FuelBurnPerSecond <= 0f)
        {
            return [];
        }

        List<MatchVehicle>? stalled = null;
        float burn = Options.FuelBurnPerSecond * elapsedSeconds;

        foreach (MatchVehicle vehicle in _byGuid.Values)
        {
            if (!vehicle.EngineOn || (driverGuid is ulong driver && vehicle.DriverGuid != driver))
            {
                continue;
            }

            vehicle.Fuel = MathF.Max(0f, vehicle.Fuel - burn);
            if (vehicle.Fuel > 0f)
            {
                continue;
            }

            vehicle.EngineOn = false;
            (stalled ??= []).Add(vehicle);
        }

        return stalled is null ? Array.Empty<MatchVehicle>() : stalled;
    }

    /// <summary>Adds fuel from a Biofuel/Ethanol use, clamped to the tank. Returns what went in.</summary>
    public float Refuel(MatchVehicle vehicle, float amount)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        float before = vehicle.Fuel;
        vehicle.Fuel = MathF.Min(Options.MaxFuel, vehicle.Fuel + amount);
        return vehicle.Fuel - before;
    }

    /// <summary>
    /// Applies damage. Returns the condition after it, so the caller can decide whether to send
    /// <c>88 1e HealthUpdateOwner</c> (the driver's health bar) and <c>88 04 StateDamage</c>, and
    /// whether the car became a wreck.
    ///
    /// <para>Kept as the thin form for the callers that only want the band;
    /// <see cref="Damage"/> is the one the wire path uses, because it also says who was thrown out
    /// of the wreck and which stage effect the crossing owes.</para>
    /// </summary>
    public VehicleCondition ApplyDamage(MatchVehicle vehicle, uint amount) =>
        Damage(vehicle, amount).Condition;

    /// <summary>
    /// Applies damage and reports everything the wire path owes: the points actually taken, the
    /// band, the <c>VEH_Damage_&lt;family&gt;_Stage0n</c> effect if the hit crossed into a new one,
    /// and — when the car reached zero — who was put out of it.
    ///
    /// <para><b>The amount is never scaled here.</b> A crash charges the client's own
    /// <c>8e 01 damage</c> word, a bullet charges what the hit rule resolved. The one subtraction
    /// this method makes is the client's own <c>COLLISION_RESISTANCE</c>, and only when the caller
    /// says the hit was a collision.</para>
    /// </summary>
    public VehicleDamageOutcome Damage(MatchVehicle vehicle, uint amount, bool collision = false)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        uint before = vehicle.Health;

        if (collision)
        {
            // COLLISION_RESISTANCE: 100 on the PickupTruck, 0 on the other three. The client's own
            // column, applied as flat points off a crash and nothing else.
            uint resistance = AugustVehicleDamageFacts.CollisionResistance(vehicle.Definition.VehicleId);
            amount = amount > resistance ? amount - resistance : 0;
        }

        vehicle.Health = amount >= vehicle.Health ? 0 : vehicle.Health - amount;
        uint charged = before - vehicle.Health;

        VehicleCondition condition = vehicle.ConditionUnder(Options);
        int stage = vehicle.DamageStageUnder(Options);
        uint effect = 0;
        if (stage != vehicle.DamageStage)
        {
            vehicle.DamageStage = stage;
            effect = AugustVehicleDamageFacts.DamageStageEffect(vehicle.Definition.VehicleId, stage);
        }

        if (condition != VehicleCondition.Destroyed)
        {
            return new VehicleDamageOutcome(
                vehicle, charged, before, vehicle.Health, condition, effect, false, []);
        }

        var evicted = new List<ulong>(vehicle.OccupantCount);
        foreach (ulong occupant in vehicle.Seats)
        {
            if (occupant != 0)
            {
                _byOccupant.Remove(occupant);
                evicted.Add(occupant);
            }
        }

        vehicle.ClearSeats();
        vehicle.OwnerGuid = 0;
        vehicle.EngineOn = false;
        vehicle.UpsideDown = false;
        return new VehicleDamageOutcome(
            vehicle, charged, before, vehicle.Health, condition, effect, true, evicted);
    }

    /// <summary>
    /// Takes one <c>UPSIDE_DOWN_DAMAGE_PULSE</c> off every car that is on its roof and is due
    /// another one, and returns what happened to each. The pulse AMOUNT is the client's own
    /// (<c>Vehicles.txt</c>, 5,000 / 10,000 on the PickupTruck); the PERIOD is
    /// <see cref="VehicleDamageOptions.FlipPulseIntervalMs"/>, which the build does not carry.
    /// </summary>
    public IReadOnlyList<VehicleDamageOutcome> PulseUpsideDown(long nowMs, VehicleDamageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || !options.Flip)
        {
            return [];
        }

        List<VehicleDamageOutcome>? hit = null;
        foreach (MatchVehicle vehicle in _byGuid.Values)
        {
            if (!vehicle.UpsideDown || vehicle.Health == 0)
            {
                continue;
            }

            if (vehicle.UpsideDownSinceMs != long.MinValue
                && nowMs - vehicle.UpsideDownSinceMs < options.FlipInitialGraceMs)
                continue;

            if (vehicle.LastFlipPulseMs != long.MinValue
                && nowMs - vehicle.LastFlipPulseMs < options.FlipPulseIntervalMs)
            {
                continue;
            }

            uint pulse = options.FlipDamageFor(vehicle.Definition.VehicleId);
            vehicle.LastFlipPulseMs = nowMs;
            if (pulse == 0)
            {
                continue;
            }

            VehicleDamageOutcome outcome = Damage(vehicle, pulse);
            if (outcome.Moved)
            {
                (hit ??= []).Add(outcome);
            }
        }

        return hit is null ? [] : hit;
    }

    /// <summary>
    /// Drops a player out of whatever they were in — disconnect, death, or leaving the match. The
    /// caller still owes the client the same clearing burst <see cref="TryExit"/> describes.
    /// </summary>
    public MatchVehicle? Evict(ulong characterGuid)
    {
        foreach (MatchVehicle coasting in Vehicles)
            if (coasting.CoastingOwnerGuid == characterGuid) coasting.EndCoast();
        if (!_byOccupant.Remove(characterGuid, out MatchVehicle? vehicle))
        {
            return null;
        }

        int seat = vehicle.SeatOf(characterGuid);
        if (seat >= 0)
        {
            vehicle.Seat(seat, 0);
        }

        if (vehicle.OwnerGuid == characterGuid)
        {
            vehicle.OwnerGuid = 0;
            vehicle.EngineOn = false;
        }

        return vehicle;
    }

    /// <summary>
    /// Whether <paramref name="lastMs"/> is recent enough to block an action.
    ///
    /// <para>The <see cref="long.MinValue"/> "never happened" sentinel is tested explicitly rather
    /// than subtracted: <c>nowMs - long.MinValue</c> overflows and wraps <i>negative</i>, which would
    /// make a car nobody has ever touched read as permanently on cooldown — so the very first player
    /// to walk up to a parked car could never get in.</para>
    /// </summary>
    private static bool IsCoolingDown(long lastMs, long nowMs, int cooldownMs) =>
        lastMs != long.MinValue && nowMs - lastMs < cooldownMs;

    public void Clear()
    {
        _byGuid.Clear();
        _byTransient.Clear();
        _byOccupant.Clear();
    }
}
