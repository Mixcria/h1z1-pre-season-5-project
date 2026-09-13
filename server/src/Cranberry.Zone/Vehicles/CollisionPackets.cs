using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone.Vehicles;

/// <summary>
/// <c>8e 01 cCollisionPacketIdDamage</c>'s <c>causeOfDamage</c> word.
///
/// <para><b>Values 2 and 4 are proven from the August client's own bytes</b> — 44 reports carrying
/// <c>2</c> while a character stood in gas and 3 carrying <c>4</c> on a fall, in
/// <c>C:\Aug2017\logs\host-*.log</c> (the 47 samples are frozen in
/// <c>CollisionDamagePacketTests</c>). The remaining three names are the owner's own Z1 enum
/// (<c>C:\Z1\Server\Zone\ZoneCombatWire.cs:1010-1018</c>) adopted under D53 and are <b>[I]</b>
/// until the wire shows one: nothing in Cranberry acts on 0 or 3, and 1 is the one a live car
/// crash confirms.</para>
/// </summary>
public enum CollisionDamageCause : uint
{
    /// <summary>[I] — never observed on the August wire.</summary>
    WeaponDamage = 0,

    /// <summary>[I] — the crash report. One live car crash confirms it in one log line.</summary>
    VehicleCollision = 1,

    /// <summary>[P-live] — 44 of the 47 recovered samples.</summary>
    ToxicGas = 2,

    /// <summary>[I] — never observed on the August wire.</summary>
    ExplosiveDamage = 3,

    /// <summary>[P-live] — 3 of the 47 recovered samples.</summary>
    FallDamage = 4,
}

/// <summary>
/// <b><c>8e 01 Collision.Damage</c> — 44 bytes, client to server, and the body is proven live.</b>
///
/// <code>
/// u8  0x8e            CollisionBase
/// u8  0x01            cCollisionPacketIdDamage
/// u8  unknownByte1    0 in all 47 recovered samples
/// u64 characterId     the reporting character
/// u64 objectCharacterId  what it hit; == characterId for self-damage (gas, a fall)
/// u32 unknownDword1   0 in all 47 recovered samples
/// u32 damage          the CLIENT's own number: 7 .. 42,637 across the samples
/// u32 causeOfDamage   <see cref="CollisionDamageCause"/>
/// f32 x, y, z         world position of the impact
/// u8  unknownByte2    0 in all 47 recovered samples
///                                                        = 44 bytes exactly
/// </code>
///
/// <para>
/// <b>Why there is no decompiled reader and none is expected.</b> The client's zone receive
/// dispatcher <c>FUN_140af3950</c> has 164 cases and <b>none of them is <c>0x8e</c></b>
/// (<c>0x8d</c> and <c>0x8f</c> are both there), and the string
/// <c>cCollisionPacketIdDamage</c> is referenced by exactly one function in the whole 1148 image —
/// the name registrar <c>FUN_1413d1490</c>, which attaches no reader and no writer factory. So the
/// packet is <b>send-only from the client</b>, which is what it must be: the client owns the
/// physics (docs/43 §3), so it is the only party that can know it hit something. There is nothing
/// to decompile on the receive side, and the wire is the only thing that could answer. It has:
/// 47 distinct records in <c>C:\Aug2017\logs\host-*.log</c>, <b>every one exactly 44 bytes</b>,
/// all parsing cleanly against this layout.
/// </para>
/// <para>
/// Corroborated by the 1087→1148 minus-one base shift: the owner's Z1 handles the
/// identically-named packet as <c>8f 01</c>, 44 bytes, byte-for-byte the same field order
/// (<c>C:\Z1\Server\Zone\ZoneCombatWire.cs:978-1007</c>, <c>CollisionDamageLength = 44</c> at
/// <c>:987</c>) — D53. <b>This closes docs/97 D155.</b>
/// </para>
/// <para>
/// The writer exists only so a test can produce the exact 44 bytes the client produces; Cranberry
/// never sends this packet.
/// </para>
/// </summary>
public sealed record CollisionDamageReport(
    ulong CharacterId,
    ulong ObjectCharacterId,
    uint Damage,
    CollisionDamageCause Cause,
    Vector3 Position,
    byte UnknownByte1 = 0,
    uint UnknownDword1 = 0,
    byte UnknownByte2 = 0)
{
    public const byte Opcode = ZoneOpcodes.CollisionBase;

    public const byte SubOpcode = 0x01;

    /// <summary>44, exactly, on all 47 recovered samples.</summary>
    public const int Length = 44;

    /// <summary>True when the report is about the reporter itself — gas, a fall, drowning.</summary>
    public bool IsSelfReport => CharacterId == ObjectCharacterId;

    /// <summary>The cause as a number, for the log line when it is not one of the five known.</summary>
    public uint CauseValue => (uint)Cause;

    public static bool TryParse(ReadOnlySpan<byte> payload, out CollisionDamageReport? report)
    {
        report = null;
        if (payload.Length != Length || payload[0] != Opcode || payload[1] != SubOpcode)
        {
            return false;
        }

        report = Parse(payload);
        return true;
    }

    public static CollisionDamageReport Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        byte opcode = reader.ReadByte();
        byte sub = reader.ReadByte();
        if (opcode != Opcode || sub != SubOpcode)
        {
            throw new PacketFormatException(
                $"Expected Collision.Damage {Opcode:x2} {SubOpcode:x2}, got {opcode:x2} {sub:x2}.");
        }

        byte unknownByte1 = reader.ReadByte();
        ulong characterId = reader.ReadUInt64();
        ulong objectCharacterId = reader.ReadUInt64();
        uint unknownDword1 = reader.ReadUInt32();
        uint damage = reader.ReadUInt32();
        uint cause = reader.ReadUInt32();
        float x = reader.ReadSingle();
        float y = reader.ReadSingle();
        float z = reader.ReadSingle();
        byte unknownByte2 = reader.ReadByte();

        if (!reader.AtEnd)
        {
            throw new PacketFormatException(
                $"Collision.Damage has {reader.Remaining} trailing byte(s); the body is exactly {Length}.");
        }

        return new CollisionDamageReport(
            characterId,
            objectCharacterId,
            damage,
            (CollisionDamageCause)cause,
            new Vector3(x, y, z),
            unknownByte1,
            unknownDword1,
            unknownByte2);
    }

    /// <summary>Reproduces the client's own 44 bytes. Test-only: Cranberry never sends <c>8e 01</c>.</summary>
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteByte(UnknownByte1);
        writer.WriteUInt64(CharacterId);
        writer.WriteUInt64(ObjectCharacterId);
        writer.WriteUInt32(UnknownDword1);
        writer.WriteUInt32(Damage);
        writer.WriteUInt32((uint)Cause);
        writer.WriteSingle(Position.X);
        writer.WriteSingle(Position.Y);
        writer.WriteSingle(Position.Z);
        writer.WriteByte(UnknownByte2);
    }

    public override string ToString() =>
        $"8e 01 character=0x{CharacterId:x16} object=0x{ObjectCharacterId:x16} "
        + $"damage={Damage} cause={CauseValue} (0x{CauseValue:x2}) "
        + $"at ({Position.X:0.0}, {Position.Y:0.0}, {Position.Z:0.0})";
}
