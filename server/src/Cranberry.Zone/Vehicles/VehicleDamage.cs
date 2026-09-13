using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone.Vehicles;

/// <summary>
/// The August build's own vehicle damage numbers, re-read from <c>out/data_aug/Vehicles.txt</c>
/// this lane. <b>Nothing here is a Cranberry choice</b> — the choices live in
/// <see cref="VehicleDamageOptions"/> and are labelled there.
///
/// <para>
/// docs/43 §8.3 said the damage data was absent. It is not: twelve <c>Vehicles.txt</c> columns are
/// populated per land row and three of them are load-bearing here. The HEALTH / MASS / EXPLODE /
/// RAM zeros of docs/43 §2.3 all still hold — this class carries only the columns that are not
/// zero.
/// </para>
/// </summary>
public static class AugustVehicleDamageFacts
{
    /// <summary>
    /// <c>UPSIDE_DOWN_DAMAGE_PULSE</c> — <b>the client's own flip-damage tick</b>, per vehicle id:
    /// OffRoader 5,000 · PickupTruck <b>10,000</b> · PoliceCar 5,000 · ATV 5,000. The parachute
    /// (13) and the observer (1337) carry 0, which is why an upside-down canopy costs nothing.
    /// [P-data] <c>out/data_aug/Vehicles.txt</c>.
    /// </summary>
    public static uint UpsideDownDamagePulse(uint vehicleId) => vehicleId switch
    {
        1 => 5_000,
        2 => 10_000,
        3 => 5_000,
        5 => 5_000,
        _ => 0,
    };

    /// <summary>
    /// <c>UPSIDE_DOWN_UNDO</c> — 280 / 600 / 850 / 100. The client's own auto-right threshold.
    /// Recorded, <b>not acted on</b>: righting a car is client-side physics, and nothing in
    /// Cranberry integrates a vehicle. [P-data]
    /// </summary>
    public static uint UpsideDownUndo(uint vehicleId) => vehicleId switch
    {
        1 => 280,
        2 => 600,
        3 => 850,
        5 => 100,
        _ => 0,
    };

    /// <summary>
    /// <c>COLLISION_RESISTANCE</c> — 0 on every land row but the PickupTruck's <b>100</b>. Flat
    /// points subtracted from a crash before it reaches the condition bar, which is what makes the
    /// truck the tough one. [P-data]
    /// </summary>
    public static uint CollisionResistance(uint vehicleId) => vehicleId == 2 ? 100u : 0u;

    /// <summary>
    /// <c>IMPACT_DAMAGE_MULTIPLIER</c> — 1 on all four land rows (0 on the parachute, which is why
    /// a canopy landing costs nothing). The crash scale. [P-data]
    /// </summary>
    public static float ImpactDamageMultiplier(uint vehicleId) => vehicleId is 1 or 2 or 3 or 5 ? 1f : 0f;

    /// <summary>
    /// The four <c>VEH_Damage_&lt;family&gt;_Stage01..04</c> composite effects, straight out of the
    /// August client's own <c>AugustEffectCatalog</c> — 0 when the pair has no row.
    ///
    /// <para><b>Every one of these sixteen ids is in the August client's own catalogue with the
    /// matching name</b> (<c>src/Cranberry.Zone/Generated/AugustEffectCatalog.g.cs</c>:
    /// OffRoader 182/181/180/5227, PickupTruck 325/324/323/5228, PoliceCar 285/284/283/5229,
    /// ATV 360/359/358/5226), so this is [P-data] from the August build, not a port. The owner's Z1
    /// carries the same sixteen (<c>C:\Z1\Server\Zone\ZoneVehicleResources.cs:366-385</c>) and the
    /// agreement is the cross-check, not the source.</para>
    /// </summary>
    public static uint DamageStageEffect(uint vehicleId, int stage) => (vehicleId, stage) switch
    {
        (1, 1) => 182,
        (1, 2) => 181,
        (1, 3) => 180,
        (1, 4) => 5_227,
        (2, 1) => 325,
        (2, 2) => 324,
        (2, 3) => 323,
        (2, 4) => 5_228,
        (3, 1) => 285,
        (3, 2) => 284,
        (3, 3) => 283,
        (3, 4) => 5_229,
        (5, 1) => 360,
        (5, 2) => 359,
        (5, 3) => 358,
        (5, 4) => 5_226,
        _ => 0,
    };

    /// <summary>
    /// <c>Resources.txt</c> id 561, <c>RESOURCE_TYPE</c> 1 <c>ResourceTypeHealth</c>,
    /// <c>INITIAL_VALUE</c> and <c>MAX_VALUE</c> both <b>100,000</b>,
    /// <c>PACKET_BROADCAST_RANGE</c> 30. This is the resource row the vehicle condition bar rides
    /// on, and it is where <see cref="VehicleFleetOptions.MaxHealth"/>'s 100,000 comes from.
    /// [P-data + D53] — the owner's Z1 names the same id
    /// (<c>C:\Z1\Server\Zone\ZoneVehicleResources.cs:132-138</c>, <c>ResourceIdCondition = 561</c>,
    /// <c>ResourceTypeCondition = 1</c>) and gives every vehicle <c>Condition = 100_000</c>
    /// (<c>ZoneVehicleRegistry.cs:253, 277, 316</c>).
    /// </summary>
    public const uint ConditionResourceId = 561;

    /// <inheritdoc cref="ConditionResourceId"/>
    public const uint ConditionResourceType = 1;

    /// <inheritdoc cref="ConditionResourceId"/>
    public const uint ConditionMaxValue = 100_000;

    /// <summary>
    /// <c>DestroyedInfo.txt</c> <c>CORPSE_EXPIRE_SECONDS</c> — 60. Recorded for the wreck timer the
    /// next lane owes; this lane leaves the wreck in the world. [P-data]
    /// </summary>
    public const int WreckExpireSeconds = 60;
}

/// <summary>
/// The switches and the numbers over vehicle damage: what a crash costs, what a flip costs, and
/// the three gates that stop the client's own report ramps killing everybody.
///
/// <para>
/// <b>The damage VALUE is never invented here.</b> <c>8e 01</c> carries the client's own
/// <c>damage</c> word and it is charged verbatim (grade CLIENT). There is deliberately no
/// threshold, no cap and no divisor: Z1's 800 / 5,000 / 100 / ×4 are attributed by Z1's own
/// comments to the forbidden tree and do <b>not</b> cross (AUDIT-vehicles §4.3), and inventing
/// replacements would put a Cranberry number in front of a client fact.
/// </para>
/// </summary>
public sealed record VehicleDamageOptions
{
    public const string EnabledVariable = "CRANBERRY_VEHICLE_DAMAGE";

    public const string BulletsVariable = "CRANBERRY_VEHICLE_BULLET_DAMAGE";

    public const string FlipVariable = "CRANBERRY_VEHICLE_FLIP_DAMAGE";

    public static VehicleDamageOptions Default { get; } = new();

    /// <summary>
    /// The whole vehicle damage model — crashes, bullets, flips, the condition bar and the wreck.
    /// Off restores the pre-lane behaviour exactly: <c>VehicleFleet.ApplyDamage</c> has no callers
    /// and a car cannot be hurt.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>A <c>82 06 ProjectileHitReport</c> whose target guid names a vehicle damages it.</summary>
    public bool Bullets { get; init; } = true;

    /// <summary>Owner-requested zero-health blast. Radius/damage are server tuning, not recovered retail values.</summary>
    public bool Explosions { get; init; } = true;
    public uint ExplosionDamage { get; init; } = 10000;
    public float ExplosionFullDamageRadius { get; init; } = 3f;
    public float ExplosionRadius { get; init; } = 8f;

    public uint ExplosionDamageAt(float distance)
    {
        if (!Explosions || !float.IsFinite(distance) || distance < 0
            || !float.IsFinite(ExplosionRadius) || ExplosionRadius <= 0 || distance >= ExplosionRadius) return 0;
        float inner = Math.Clamp(float.IsFinite(ExplosionFullDamageRadius) ? ExplosionFullDamageRadius : 0, 0, ExplosionRadius);
        if (distance <= inner) return ExplosionDamage;
        return (uint)Math.Floor(ExplosionDamage * (double)(ExplosionRadius - distance) / (ExplosionRadius - inner));
    }

    /// <summary>An upside-down car takes <c>UPSIDE_DOWN_DAMAGE_PULSE</c> every pulse period.</summary>
    public bool Flip { get; init; } = true;

    /// <summary>Server tuning, not a recovered retail number. A single continuous impact can
    /// cost at most 2 HP of a standard 100-HP car; apply BEFORE cumulative burst accounting.</summary>
    public uint MaximumCollisionDamage { get; init; } = 2_000;

    /// <summary>September 11 driving tuning: ignore small bumps and charge 10% of larger impacts.</summary>
    public uint MinimumCollisionDamage { get; init; } = 1_000;
    public float CollisionDamageMultiplier { get; init; } = 0.1f;
    public float FlipDamageMultiplier { get; init; } = 0.1f;

    public uint CollisionDamageFor(uint reported) => reported <= MinimumCollisionDamage ? 0
        : (uint)Math.Min(MaximumCollisionDamage,
            Math.Floor((reported - MinimumCollisionDamage) * (double)CollisionDamageMultiplier));

    public uint FlipDamageFor(uint vehicleId) =>
        (uint)Math.Floor(AugustVehicleDamageFacts.UpsideDownDamagePulse(vehicleId) * (double)FlipDamageMultiplier);

    /// <summary>Allow a tumbling car time to right itself before charging sustained roof damage.</summary>
    public int FlipInitialGraceMs { get; init; } = 15_000;

    /// <summary>
    /// <b>The burst rule, adopted under D53 from the owner's own round-25 regression fix</b>
    /// (<c>C:\Z1\Server\Zone\ZoneCombat.cs:189</c>, <c>ZoneVehicleResources.cs:119</c>): within this
    /// window one impact costs its <i>peak</i>, once, not once per packet.
    ///
    /// <para>This is not tuning, it is a correctness requirement, and the recovered samples show
    /// exactly why: the 47 live records are two interleaved <b>ramps</b> — 7, 14, 22, 30, 38, 46 …
    /// and 262, 272, 283, 293, 303 … — i.e. the client re-reports one continuing event with a
    /// running total, not a per-tick delta. Charging every packet would kill a standing player in
    /// under a second.</para>
    /// </summary>
    public int CollisionBurstWindowMs { get; init; } = Generated.Rulings.VehiclesPlan.CollisionBurstWindowMs;

    /// <summary>
    /// <b>Gate 2, D53</b> (<c>C:\Z1\Server\Zone\ZoneCombat.cs:196</c>): collision reports inside
    /// this many milliseconds of the drop release are discarded. The client settles its actor onto
    /// the terrain on arrival and reports the settle as an impact; without the grace the first
    /// second of every match is a fall.
    /// </summary>
    public int PostArrivalGraceMs { get; init; } = Generated.Rulings.VehiclesPlan.PostArrivalGraceMs;

    /// <summary>
    /// <b>Gate 3, D53</b> (<c>C:\Z1\Server\Zone\ZoneCombat.cs:2033</c>): suppress
    /// <see cref="CollisionDamageCause.FallDamage"/> while the player is under the canopy. Without
    /// it the very first thing decoding <c>8e 01</c> would do is kill every player on the drop —
    /// one of the recovered samples is a fall reporting <b>42,637</b> against a 10,000 health bar.
    /// </summary>
    public bool SuppressFallDamageUnderCanopy { get; init; } = true;

    /// <summary>
    /// How often an upside-down car takes its <c>UPSIDE_DOWN_DAMAGE_PULSE</c>, in milliseconds.
    ///
    /// <para><b>[U], a Cranberry ruling.</b> <c>Vehicles.txt</c> names the pulse AMOUNT and nothing
    /// else: <c>UPSIDE_DOWN_DMG_PULSE_C_REQ_ID</c> is 0 on every row and there is no period column
    /// anywhere in the sheet. 3,000 ms puts a flipped OffRoader (5,000 a pulse against a 100,000
    /// bar) at 60 s to a wreck and a flipped PickupTruck at 30 s — long enough to right it, short
    /// enough that leaving it on its roof is a decision.</para>
    /// </summary>
    public int FlipPulseIntervalMs { get; init; } = Generated.Rulings.VehiclesPlan.FlipPulseIntervalMs;

    /// <summary>
    /// How far the vehicle's own up-axis must tip past horizontal before it counts as upside down,
    /// as the dot product of its up vector with world up. <b>[U], a Cranberry ruling</b> — the
    /// client ships no threshold, only <c>UPSIDE_DOWN_FRICTION</c> 0.8 and the auto-right undo.
    /// −0.25 is roughly 105° from upright: a car on its roof, never a car on a steep bank.
    /// </summary>
    public float UpsideDownDotThreshold { get; init; } = Generated.Rulings.VehiclesPlan.UpsideDownDotThreshold;

    /// <summary>
    /// Optional minimum occupant damage, in player health units. Zero adds no minimum;
    /// occupants still take the configured explosion damage. Kept for existing configurations.
    /// </summary>
    public uint WreckOccupantDamage { get; init; } = Generated.Rulings.VehiclesPlan.WreckOccupantDamage;

    public string Describe() =>
        $"vehicle damage: model={On(Enabled)} bullets={On(Bullets)} flip={On(Flip)} "
        + $"burst={CollisionBurstWindowMs}ms grace={PostArrivalGraceMs}ms "
        + $"flipPulse={FlipPulseIntervalMs}ms wreckDamage={WreckOccupantDamage} "
        + $"collisionCap={MaximumCollisionDamage} flipGrace={FlipInitialGraceMs}ms "
        + $"explosions={On(Explosions)} blast={ExplosionDamage}/{ExplosionRadius}m";

    private static string On(bool value) => value ? "ON" : "off";
}

/// <summary>
/// The burst accounting one reporter (a player or a car) needs, so that a ramp of client reports
/// about one continuing impact costs its peak once.
///
/// <para>Adopted under D53 from the owner's own <c>ZoneCombat.CollisionBurstAccounting</c> /
/// <c>ZoneVehicleResources.OnSelfReportedCollision</c>; re-expressed here as a value type so it can
/// sit on a session and on a vehicle without either owning the rule.</para>
/// </summary>
public struct CollisionBurst
{
    /// <summary>"No report has ever arrived", tested explicitly and never subtracted.</summary>
    public const long Never = long.MinValue;

    private long _lastMs;
    private uint _peak;

    /// <summary>The highest raw value seen in the current window.</summary>
    public readonly uint Peak => _peak;

    /// <summary>When the last report arrived, or <see cref="Never"/>.</summary>
    public readonly long LastMs => _lastMs;

    /// <summary>Nothing has been reported yet.</summary>
    public static CollisionBurst Fresh => new() { _lastMs = Never, _peak = 0 };

    /// <summary>
    /// What <paramref name="raw"/> costs, given what this reporter has already been charged inside
    /// <paramref name="windowMs"/>. The first report of a burst costs its whole value; a later one
    /// costs only the amount by which it exceeds the running peak, so a ramp 7 → 14 → 22 costs 22
    /// in total rather than 43.
    /// </summary>
    public uint Charge(uint raw, long nowMs, int windowMs)
    {
        uint charged;
        if (_lastMs != Never && nowMs - _lastMs < windowMs)
        {
            charged = raw > _peak ? raw - _peak : 0u;
        }
        else
        {
            _peak = 0;
            charged = raw;
        }

        if (raw > _peak)
        {
            _peak = raw;
        }

        _lastMs = nowMs;
        return charged;
    }

    /// <summary>Forget the window — a new match, or a rider leaving the car.</summary>
    public void Reset()
    {
        _lastMs = Never;
        _peak = 0;
    }
}

/// <summary>
/// Whether the pose the owner just streamed has the car on its roof, and whether it has been there
/// long enough to owe another <c>UPSIDE_DOWN_DAMAGE_PULSE</c>.
///
/// <para><b>The server does not integrate anything.</b> The client owns vehicle physics for every
/// id (docs/43 §0.1), so the only thing the server can know about a car's attitude is the rotation
/// the owning client puts in its own <c>0x90</c> managed-movement record. This type turns that
/// quaternion into one boolean and a clock.</para>
/// </summary>
public static class VehicleFlipDetector
{
    /// <summary>
    /// The vehicle's own up-axis in world space, projected onto world up. <c>+1</c> is level,
    /// <c>0</c> is on its side, <c>−1</c> is fully inverted.
    /// </summary>
    public static float UpDot(Quaternion rotation) =>
        Vector3.Transform(Vector3.UnitY, Quaternion.Normalize(rotation)).Y;

    /// <summary>Is this rotation past <paramref name="threshold"/> — i.e. on its roof.</summary>
    public static bool IsUpsideDown(Quaternion rotation, float threshold) =>
        UpDot(rotation) <= threshold;
}

/// <summary>One vehicle's condition state after damage, and everything the caller owes the wire.</summary>
/// <param name="Vehicle">The car that was hit.</param>
/// <param name="Charged">Condition points actually taken off, after the burst rule.</param>
/// <param name="Before">Condition before the hit.</param>
/// <param name="After">Condition after the hit.</param>
/// <param name="Condition">The band the car is now in.</param>
/// <param name="StageEffectId">
/// The <c>VEH_Damage_*_Stage0n</c> composite effect to play because the car crossed into a new
/// band, or 0 when it did not.
/// </param>
/// <param name="Destroyed">This hit took the car to zero.</param>
/// <param name="EvictedOccupants">Who was put out of the wreck, in seat order.</param>
public readonly record struct VehicleDamageOutcome(
    MatchVehicle Vehicle,
    uint Charged,
    uint Before,
    uint After,
    VehicleCondition Condition,
    uint StageEffectId,
    bool Destroyed,
    IReadOnlyList<ulong> EvictedOccupants)
{
    /// <summary>Nothing was charged — below the burst peak, or the model is off.</summary>
    public bool Moved => Charged > 0;
}

/// <summary>
/// <c>09 1d Command.PlayDialogEffect</c> — <c>u8 0x09; u16 0x001d; u64 characterGuid;
/// u32 effectId</c>, <b>15 bytes</b>. The carrier for an effect that hangs on an entity and has no
/// position of its own, which is exactly what a vehicle's damage smoke is.
///
/// <para>Registered in the August build as <c>0x1d000900
/// cCommandPacketIdPlayDialogEffect</c> (<c>out/registrations-1148.json</c>), and the <c>0x09</c>
/// family's subs are <c>u16</c> in 1148 exactly as <c>09 2d InteractionString</c> already writes
/// them. The body is the owner's own (<c>C:\Z1\Server\Zone\ZoneCombatThrowables.cs:719-728</c>)
/// under D53, with the family byte unchanged because <c>0x09</c> is one of the 957 ids the
/// 1087→1148 bridge leaves alone.</para>
/// </summary>
public sealed record PlayDialogEffect(ulong CharacterGuid, uint EffectId)
{
    public const byte Opcode = ZoneOpcodes.CommandBase;

    public const ushort SubOpcode = 0x001d;

    public const int Length = 15;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
        writer.WriteUInt32(EffectId);
    }
}
