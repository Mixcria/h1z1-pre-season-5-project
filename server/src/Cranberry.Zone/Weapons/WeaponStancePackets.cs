using System.Buffers.Binary;

using Cranberry.Protocol;

namespace Cranberry.Zone.Weapons;

// Character.WeaponStance - the packet that starts the client's weapon stance machine.
//
// Registrar (out/registrations-1148.json): id 536874752 = 0x20000f00, levels [15, 32], family
// cPacketIdCharacterBase, member "cCharacterPacketIdWeaponStance", registered in FUN_1413c1ba0. So
// base 0x0f sub 0x20 IS WeaponStance on this build, by the client's own name for it.
//
// Bridge (out/wave9-bridge/opcode-map-1087-to-1148.json): the row
//   {"member": "cCharacterPacketIdWeaponStance", "verdict": "identical",
//    "z1_1087": [15, 32], "aug_1148": [15, 32], "base_delta": 0, "sub_delta": 0}
// says the id did not move between 1087 and 1148, so the owner's own 14-byte 1087 body ports as-is.
//
// LABEL: id DERIVED from the August registrar; the 14-byte body and the "send stance 1" behaviour
// ADOPTED from the owner's Z1 work under D53 (C:\Z1\Server\Zone\ZoneAbilities.cs:770-856 - his
// round-26 finding that a client which is never given a stance never leaves stance 0, never enters
// a fire state, and reports "cannot shoot / reload"). Never yet on an August wire.

/// <summary>
/// <c>Character.WeaponStance</c> (<c>0f 20</c>): <c>u8 0x0f; u8 0x20; u64 characterGuid;
/// u32 stance</c> - <b>14 bytes</b>, the same envelope shape as
/// <c>Character.RemovePlayer</c> (<c>0f 01</c>) with a wider trailer.
/// <para>
/// <b>Both directions.</b> The server sends <c>{selfGuid, <see cref="Initial"/>}</c> to arm the
/// stance machine; the client then reports its own stance changes with the identical layout as it
/// aims and hip-fires. Cranberry re-asserts the server-side one until the first c2s
/// <c>0f 20</c> of the session arrives and then stops for good - self-limiting by construction, so
/// a wrong hypothesis costs 14 bytes per draw rather than a permanent packet.
/// </para>
/// </summary>
public sealed record WeaponStance(ulong CharacterGuid, uint Stance)
{
    /// <summary><c>cPacketIdCharacterBase</c>, 0x0f.</summary>
    public const byte Opcode = ZoneOpcodes.CharacterBase;

    /// <summary><c>cCharacterPacketIdWeaponStance</c>, u8 sub 0x20.</summary>
    public const byte SubOpcode = 0x20;

    /// <summary><c>1 + 1 + 8 + 4</c>.</summary>
    public const int Length = 14;

    /// <summary>
    /// The stance the owner's server initialises a client with, and the only value Cranberry sends.
    /// </summary>
    public const uint Initial = 1;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
        writer.WriteUInt32(Stance);
    }

    /// <summary>The 14 bytes as a standalone buffer, for a caller that already holds a raw writer.</summary>
    public byte[] ToArray()
    {
        using var writer = new PacketWriter(Length);
        WriteTo(writer);
        return writer.Written.ToArray();
    }

    /// <summary>
    /// Parse a c2s <c>0f 20</c>. Strict on length: the client's own report is exactly
    /// <see cref="Length"/> bytes, and a shorter payload under this id is something else.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out WeaponStance? stance)
    {
        stance = null;
        if (payload.Length != Length || payload[0] != Opcode || payload[1] != SubOpcode)
        {
            return false;
        }

        stance = new WeaponStance(
            BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(2, 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(10, 4)));
        return true;
    }
}
