using System.Diagnostics.CodeAnalysis;
using Cranberry.Protocol;

namespace Cranberry.Zone.Vehicles;

/// <summary>
/// <b>The August build's own boost id table.</b> Four separate id spaces meet on one key press and
/// the client's <c>ClientEffects.txt</c> is what ties them together — one row per vehicle family,
/// each carrying the client-effect id, the ability it belongs to, the server-effect id that rides
/// beside it on the wire, and the composite-effect tag other players' clients draw:
///
/// <code>
/// TYPE_NAME  *ID    ABILITY_ID  SERVER_EFFECT_ID  ACTIVE_COMP_EFFECT_ID   family
/// Turbo      90000  1111141     100023            5016   VEH_Engine_Boost_OffRoader
/// Turbo      90068  1111292     110268             319   VEH_Engine_Boost_PickupTruck
/// Turbo      90069  1111294     110270             279   VEH_Engine_Boost_PoliceCar
/// Turbo      90193  1111611     120654             354   VEH_Engine_Boost_ATV
/// </code>
///
/// <para>
/// <b>Every number here is [P-data] from the August client</b> —
/// <c>out/data_aug/ClientEffects.txt</c> for the four rows, <c>out/data_aug/AbilityEx.txt</c> for
/// the ability rows (<c>TYPE_NAME MaintainOnMountedVehicle</c>, <c>INPUT_ACTION_KEY VehicleTurbo</c>,
/// <c>RESOURCE_TYPE 50</c> = Fuel, <c>FLAG_RUN_ON_CLIENT 1</c>), and
/// <c>src/Cranberry.Zone/Generated/AugustEffectCatalog.g.cs</c> for the four composite tags. The
/// owner's Z1 carries the identical four (<c>C:\Z1\Server\Zone\ZoneVehicleEffects.cs:130-136</c>,
/// <c>ZoneVehicles.cs:1008-1015</c>); the agreement is a cross-check, not the source.
/// </para>
/// <para>
/// <b>Boost is not gated on a part in this build.</b> The per-family turbo <i>items</i> exist
/// (90, 1729, 1731, 2727, and 3162 "Universal Turbo") but carry
/// <c>ACTIVE_EQUIP_SLOT_ID = 7</c> (RHand), and <c>EquipSlotItemClasses.txt</c> has no row for the
/// vehicle slots 45/47/69/70/71/73 — there is no vehicle turbo equipment slot at all.
/// </para>
/// </summary>
public static class AugustVehicleBoostFacts
{
    /// <summary><c>ClientEffects.txt</c> <c>*ID</c> — what the client's own effect manager keys on.</summary>
    public static uint TurboClientEffect(uint vehicleId) => vehicleId switch
    {
        1 => 90_000,
        2 => 90_068,
        3 => 90_069,
        5 => 90_193,
        _ => 0,
    };

    /// <summary><c>ClientEffects.txt</c> <c>SERVER_EFFECT_ID</c> — the second id in the wire pair.</summary>
    public static uint TurboServerEffect(uint vehicleId) => vehicleId switch
    {
        1 => 100_023,
        2 => 110_268,
        3 => 110_270,
        5 => 120_654,
        _ => 0,
    };

    /// <summary>
    /// <c>ClientEffects.txt</c> <c>ACTIVE_COMP_EFFECT_ID</c> — <c>VEH_Engine_Boost_*</c> in the
    /// August catalogue, and what a <i>bystander's</i> client draws.
    /// </summary>
    public static uint TurboCompositeEffect(uint vehicleId) => vehicleId switch
    {
        1 => 5_016,
        2 => 319,
        3 => 279,
        5 => 354,
        _ => 0,
    };

    /// <summary><c>AbilityEx.txt</c> <c>*ID</c> — the <c>VehicleTurbo</c> ability row.</summary>
    public static uint TurboAbility(uint vehicleId) => vehicleId switch
    {
        1 => 1_111_141,
        2 => 1_111_292,
        3 => 1_111_294,
        5 => 1_111_611,
        _ => 0,
    };

    /// <summary>Any of the four families' turbo client-effect ids.</summary>
    public static bool IsTurboClientEffect(uint effectId) =>
        effectId is 90_000 or 90_068 or 90_069 or 90_193;

    /// <summary>
    /// The <c>MotorRun</c> client-effect ids the client also raises on this family
    /// (<c>ClientEffects.txt</c> rows 90001 / 90062 / 90063 / 90187, server pair 100042 / 110237 /
    /// 110260 / 120649). These drive the current driver's engine toggle through the
    /// component/fuel checks; they do not revoke managed physics.
    /// </summary>
    public static bool IsMotorRunClientEffect(uint effectId) =>
        effectId is 90_001 or 90_062 or 90_063 or 90_187 or 100_042 or 110_237 or 110_260 or 120_649;

    /// <summary>Any of the four families' <c>VehicleTurbo</c> ability ids, plus the shared 1111702.</summary>
    public static bool IsTurboAbility(uint abilityId) =>
        abilityId is 1_111_141 or 1_111_292 or 1_111_294 or 1_111_611 or 1_111_702;

    /// <summary>
    /// <c>AbilityEx.txt RESOURCE_TYPE</c> on every <c>VehicleTurbo</c> row: <b>50</b>, which is
    /// <c>ResourceTypeFuel</c>. <b>The boost meter in this build IS the fuel tank</b> — there is no
    /// separate boost resource anywhere in <c>Resources.txt</c>. That is why
    /// <c>CRANBERRY_VEHICLE_BOOST</c> pulls <c>CRANBERRY_VEHICLE_FUEL</c> with it: with no fuel
    /// model there is nothing for a boost to spend and nothing to refuse it on.
    /// </summary>
    public const uint TurboResourceType = 50;
}

/// <summary>
/// The 12-byte <c>abilityEffectData</c> head that opens every sub of the <c>0x9e</c> Effects
/// family: <c>u32 unknownDword1; u32 abilityEffectId1; u32 abilityEffectId2</c>.
/// </summary>
public readonly record struct EffectHead(uint Dword1, uint EffectId1, uint EffectId2)
{
    public const int Length = 12;
}

/// <summary>
/// <b><c>9e 01 Effect.AddEffect</c> / <c>9e 03 Effect.RemoveEffect</c> — the packets the car boost
/// actually runs on in August.</b>
///
/// <code>
/// u8  0x9e            cPacketIdEffectsBase      (registrations-1148.json 0x9e00)
/// u8  0x01 | 0x03     AddEffect | RemoveEffect
/// u32 unknownDword1
/// u32 abilityEffectId1    the CLIENT-effect id  (90000 / 90068 / 90069 / 90193)
/// u32 abilityEffectId2    the SERVER-effect id  (100023 / 110268 / 110270 / 120654)
/// u64 targetCharacterData.characterId   the PLAYER
/// u64 targetCharacterId                 the VEHICLE
/// u64 guid2                             0
/// f32 x4  unknownVector1                0
///                                                   = 54 bytes for the Remove form
/// </code>
///
/// <para>
/// <b>Not <c>0x9f</c>.</b> The brief's opcode map is a 1087 map: at 1148 <c>0x9f00</c> is
/// <c>cPacketIdRewardBuffsBase</c> and has nothing to do with vehicles, while <c>0x9e00</c> is
/// <c>cPacketIdEffectsBase</c> and the August receive dispatcher has <c>case 0x9e</c>. The owner's
/// Z1 runs the same boost on 1087's <c>0x9f</c>; under the 1087→1148 minus-one base shift — the one
/// the owner's own admin capture confirms on his own bytes, <c>cVehicleOwner</c> arriving as
/// <c>89 01</c> where August's is <c>88 01</c> — that is August's <c>0x9e</c>. The <b>sub</b>
/// numbers 01/03 and the field order are the owner's, from his own click session's bytes
/// (<c>C:\Z1\Server\Zone\ZoneVehicleEffects.cs:164-186</c>), adopted under D53; the Effects family
/// registers only its base in 1148, so the subs are <b>[I]</b> until one live press confirms them.
/// </para>
/// </summary>
public sealed record EffectRequest(
    byte Sub,
    EffectHead Head,
    ulong SourceCharacterId,
    ulong TargetCharacterId)
{
    public const byte Opcode = ZoneOpcodes.EffectsBase;

    public const byte AddSub = 0x01;

    public const byte UpdateSub = 0x02;

    public const byte RemoveSub = 0x03;

    /// <summary>Head plus both character ids — the shortest form this server can act on.</summary>
    public const int MinimumLength = 2 + EffectHead.Length + 8 + 8;

    /// <summary>The full <c>Remove</c> form: head, both ids, a guid and a float4.</summary>
    public const int RemoveLength = MinimumLength + 8 + 16;

    public bool IsAdd => Sub == AddSub;

    public bool IsRemove => Sub == RemoveSub;

    public static bool TryParse(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out EffectRequest? request)
    {
        request = null;
        if (payload.Length < MinimumLength || payload[0] != Opcode)
        {
            return false;
        }

        var reader = new PacketReader(payload);
        reader.Skip(1);
        byte sub = reader.ReadByte();
        var head = new EffectHead(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
        // August AddEffect (140ce9e70) includes actor type, transient and an extra guid.
        // RemoveEffect (140cea390) is the compact 54-byte form. Reading both alike made
        // live adds target 0x0000000100000000 instead of the car; releases decoded correctly.
        if (sub == AddSub && payload.Length >= 71)
        {
            reader.Skip(4);
            ulong actor = reader.ReadUInt64();
            reader.Skip(4 + 8);
            ulong targetActor = reader.ReadUInt64();
            request = new EffectRequest(sub, head, actor, targetActor);
            return true;
        }
        ulong source = reader.ReadUInt64();
        ulong target = reader.ReadUInt64();
        request = new EffectRequest(sub, head, source, target);
        return true;
    }

    /// <summary>
    /// <b>The echo, and it is NOT optional.</b> The client's own effect manager holds
    /// <c>ClientEffects</c> row 90000 with <c>EXPIRE_MSEC = 0</c> and <c>FLAG_CAN_STACK = 0</c>
    /// (checked against <c>out/data_aug/ClientEffects.txt</c> this lane, all four turbo rows), so
    /// the effect can never age out on its own and may not be added twice. Only an explicit
    /// <c>Effect.RemoveEffect</c> from the server frees the slot. Without this echo, boost press #1
    /// works and every later press is refused <i>by the client</i> with
    /// <c>ClientEffectManager::InitAndAddClientEffect, failed to add busy effectId=90000</c> — the
    /// owner watched exactly that happen five times in one session (D53,
    /// <c>C:\Z1\Server\Zone\ZoneVehicleEffects.cs:20-49</c>). <b>Ship the boost whole or not at
    /// all.</b>
    ///
    /// <para>The two character ids are <b>swapped</b> relative to the request and
    /// <c>unknownDword1</c> is forced to 4: the c2s form names the player as the effect's owner and
    /// the car as its target, the s2c form names the car as the thing the effect comes off.</para>
    /// </summary>
    public byte[] RemoveEcho()
    {
        using var writer = new PacketWriter(RemoveLength);
        writer.WriteByte(Opcode);
        writer.WriteByte(RemoveSub);
        writer.WriteUInt32(4);
        writer.WriteUInt32(Head.EffectId1);
        writer.WriteUInt32(Head.EffectId2);
        writer.WriteUInt64(TargetCharacterId);
        writer.WriteUInt64(SourceCharacterId);
        writer.WriteUInt64(0);
        for (int index = 0; index < 4; index++)
        {
            writer.WriteSingle(0f);
        }

        return writer.Written.ToArray();
    }
}

/// <summary>
/// <b><c>0f 33 Character.Turbo</c> — 11 bytes, new at 1148.</b>
///
/// <code>
/// u8 0x0f ; u8 0x33 ; u64 characterGuid ; u8 value          = 11 bytes exactly
/// </code>
///
/// <para>
/// Parser <c>FUN_140a65200</c> reads it strictly (<c>param_4 = 0</c>: one byte over or under and
/// the packet is dropped). Handler <c>FUN_140af9ca0</c> case <c>0x33</c> takes the <b>local
/// player's</b> actor (<c>client+0x321a0</c>, gated on actor type <c>0x23</c>) — it does not look
/// anything up by the guid — and then sets bit 0 of <c>actor+0x4f8</c> when the byte is <b>0</b>
/// and clears it for any other value. The field is pre-seeded to <c>2</c> before the parse, i.e.
/// to neither state.
/// </para>
/// <para>
/// <b>The polarity is [I].</b> "0 sets the bit" reads more like <i>turbo-blocked cleared</i> than
/// <i>turbo on</i>, and one live press settles it — which is why
/// <see cref="VehicleBoostOptions.TurboOnValue"/> is a switch rather than a literal.
/// </para>
/// </summary>
public sealed record CharacterTurbo(ulong CharacterGuid, byte Value)
{
    public const byte Opcode = ZoneOpcodes.CharacterBase;

    public const byte SubOpcode = 0x33;

    public const int Length = 11;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
        writer.WriteByte(Value);
    }
}

/// <summary>
/// <b><c>88 2b Vehicle.ActivateBoostFailed</c> — 10 bytes, and the server's ONLY refusal channel
/// for a boost.</b>
///
/// <code>
/// u8 0x88 ; u8 0x2b ; u64 vehicleGuid                       = 10 bytes
/// </code>
///
/// <para>
/// Parser <c>FUN_140c8e990</c>, handler <c>FUN_140c9a930</c>. This matters more than its size
/// suggests: the Abilities receive switch <c>FUN_140cc44b0</c> has cases <b>0x12-0x2b only</b>, so
/// subs <c>0x01-0x11</c> — including <c>a0 11 VehicleActivateAbilityFailed</c> — have no receive
/// arm at all. A refusal sent on <c>a0 11</c> is silently dropped; this is the one the client
/// listens for.
/// </para>
/// </summary>
public sealed record VehicleActivateBoostFailed(ulong VehicleGuid)
{
    public const byte Opcode = ZoneOpcodes.VehicleBase;

    public const byte SubOpcode = 0x2b;

    public const int Length = 10;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(VehicleGuid);
    }
}

/// <summary>
/// <c>0f 15 Character.AddEffectTagCompositeEffect</c> — <c>u8 0x0f; u8 0x15; u64 characterGuid;
/// u32 unknownDword1; u32 effectId; u64; u64; u32</c>, <b>38 bytes</b>. What a <i>bystander's</i>
/// client draws when a car boosts past it; the driver's own client already knows, having pressed
/// the key.
///
/// <para>Registered at 1148 as <c>0x15000f00 cCharacterPacketIdAddEffectTagCompositeEffect</c>,
/// with the identical sub number to 1087; the body is the owner's own
/// (<c>C:\Z1\Server\Zone\ZoneVehicles.cs:1175-1196</c>) under D53.</para>
/// </summary>
public sealed record AddEffectTagCompositeEffect(ulong CharacterGuid, uint EffectId)
{
    public const byte Opcode = ZoneOpcodes.CharacterBase;

    public const byte SubOpcode = 0x15;

    public const int Length = 38;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
        writer.WriteUInt32(EffectId);
        writer.WriteUInt32(EffectId);
        writer.WriteUInt64(0);
        writer.WriteUInt64(0);
        writer.WriteUInt32(EffectId);
    }
}

/// <summary>
/// <c>0f 16 Character.RemoveEffectTagCompositeEffect</c> — <c>u8 0x0f; u8 0x16; u64 characterGuid;
/// u32 effectId; u32 newEffectId</c>, <b>18 bytes</b>. Registered at 1148 as <c>0x16000f00</c>;
/// body from the owner's own <c>ZoneVehicles.cs:1199-1214</c> under D53.
/// </summary>
public sealed record RemoveEffectTagCompositeEffect(ulong CharacterGuid, uint EffectId)
{
    public const byte Opcode = ZoneOpcodes.CharacterBase;

    public const byte SubOpcode = 0x16;

    public const int Length = 18;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
        writer.WriteUInt32(EffectId);
        writer.WriteUInt32(0);
    }
}

/// <summary>The switches over the boost path.</summary>
public sealed record VehicleBoostOptions
{
    public const string EnabledVariable = "CRANBERRY_VEHICLE_BOOST";

    public const string TurboByteVariable = "CRANBERRY_VEHICLE_TURBO_BYTE";

    public static VehicleBoostOptions Default { get; } = new();

    /// <summary>
    /// Answer a boost press at all. Off, <c>0x9e</c> falls back to a hex log and the client's own
    /// effect manager sticks after the first press — which is the state Cranberry shipped in before
    /// this lane, minus the log line.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The byte <c>0f 33 Character.Turbo</c> carries when the boost is granted; the release sends
    /// its complement. <b>[I]</b> — see <see cref="CharacterTurbo"/> for why 0 is the default and
    /// what one live press settles.
    /// </summary>
    public byte TurboOnValue { get; init; } = (byte)Generated.Rulings.VehiclesPlan.TurboOnValue;

    /// <summary>The byte the release sends: 1 when <see cref="TurboOnValue"/> is 0, else 0.</summary>
    public byte TurboOffValue => TurboOnValue == 0 ? (byte)1 : (byte)0;

    /// <summary>
    /// How much faster fuel goes down while the boost is held.
    ///
    /// <para><b>[U], a Cranberry ruling.</b> The client carries no answer: the <c>VehicleTurbo</c>
    /// <c>AbilityEx</c> rows have <c>RESOURCE_FIRST_COST</c>, <c>RESOURCE_COST_PER_MSEC</c>,
    /// <c>FLAG_PAY_RESOURCE_COST</c> and <c>EXPIRE_MSEC</c> all 0, so only the <i>magnitude</i> of
    /// the boost is the client's (<c>MoveInfo</c> mode 2) and the cost is the server's. Four times
    /// the cruising rate puts a full tank at about five minutes of continuous boost against
    /// twenty-one minutes of cruising, which is the shape a limited resource wants. The owner's Z1
    /// happens to carry the same figure, but its own comment sources it to the forbidden tree, so
    /// it is <b>not</b> adopted — this is Cranberry's number.</para>
    /// </summary>
    public float FuelMultiplier { get; init; } = Generated.Rulings.VehiclesPlan.FuelMultiplier;

    public string Describe() =>
        $"vehicle boost: {(Enabled ? "ON" : "off")} turboByte={TurboOnValue}/{TurboOffValue} "
        + $"fuelMultiplier=x{FuelMultiplier:0.#}";
}

/// <summary>
/// Whether the driver of a given car is holding the boost, and the one guard that matters:
/// a second <c>AddEffect</c> for a boost that is already on is not a second boost.
/// </summary>
public sealed class VehicleBoostState
{
    private readonly HashSet<ulong> _boosting = [];

    /// <summary>Presses answered this session.</summary>
    public int Presses { get; private set; }

    /// <summary>Releases echoed this session — the number that must track <see cref="Presses"/>.</summary>
    public int Releases { get; private set; }

    /// <summary>Presses refused with <c>88 2b</c>.</summary>
    public int Refusals { get; private set; }

    public bool IsBoosting(ulong vehicleGuid) => _boosting.Contains(vehicleGuid);

    /// <summary>True when this press turned the boost on; false when it was already on.</summary>
    public bool Press(ulong vehicleGuid)
    {
        if (!_boosting.Add(vehicleGuid))
        {
            return false;
        }

        Presses++;
        return true;
    }

    /// <summary>True when this release turned a live boost off.</summary>
    public bool Release(ulong vehicleGuid)
    {
        if (!_boosting.Remove(vehicleGuid))
        {
            return false;
        }

        Releases++;
        return true;
    }

    public void Refused() => Refusals++;

    /// <summary>A car that stopped being ours — a dismount, a wreck, a match reset.</summary>
    public bool Clear(ulong vehicleGuid) => _boosting.Remove(vehicleGuid);

    public void Clear() => _boosting.Clear();

    public override string ToString() =>
        $"{Presses} press(es), {Releases} release(s), {Refusals} refused, "
        + $"{_boosting.Count} boosting now";
}
