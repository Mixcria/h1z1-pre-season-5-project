using System.Numerics;
using Cranberry.Harness.Wire;

namespace Cranberry.Harness.Protocol;

/// <summary>
/// A decoded <c>AddLightweightNpc</c> (0xd6) or <c>AddLightweightVehicle</c> (0xd7) body.
///
/// <para>The two packets share the body the August dispatcher reads once (docs/12 §1 for the
/// vehicle, docs/13 §3a for the ground item), so one decoder serves both. Only the fields Lane A
/// asserts on are kept: the guid a pickup or a <c>0F 45</c> has to name, the transient id the
/// client matches a <c>0xda</c> by, the model, and the world position — which is what turns
/// "loot arrived" into "loot arrived <b>200 m from where I landed</b>" without the harness ever
/// having to decode a channel-2 movement packet.</para>
/// </summary>
public sealed record LightweightEntity(
    byte Opcode,
    ulong Guid,
    uint TransientId,
    uint ModelId,
    Vector3 Position);

/// <summary>The equipment-slot rows of one <c>Equipment.SetCharacterEquipment</c> (0x94 sub 1).</summary>
public sealed record EquipmentRows(ulong CharacterGuid, IReadOnlyList<uint> SlotIds);

/// <summary>The head of a <c>ReferenceData</c> (0x17) message: its type name and payload length.</summary>
public sealed record ReferenceDataHead(string TypeName, uint DeclaredPayloadLength, int MessageLength);

/// <summary>
/// One line of server text and the surface it was sent on: <c>print</c> (<c>06 03</c>, console
/// only), <c>chat1</c> / <c>chat0</c> (<c>06 05</c>, by its trailing <c>alsoConsole</c> byte) or
/// <c>alert</c> (<c>11 31</c>, the centre banner). The names are the ones the console's
/// <c>/surface probe</c> prints, so a probe row and a harness assertion say the same word.
/// </summary>
public sealed record ConsoleLine(string Surface, string Text);

/// <summary>
/// Decoders for the handful of <b>server</b> packets Lane A has to look inside. Everything here is
/// written from the layouts docs/06-13/32/45 record, not from the server's writers, for the same
/// reason the transport is re-implemented: a decoder that shares code with the encoder cannot see
/// a bug in the shared part. Each decoder is total — it returns null rather than throwing on a
/// short or unexpected packet, because a scenario that dies inside a decoder reports the decoder
/// instead of the server.
/// </summary>
public static class ServerPackets
{
    /// <summary>
    /// The varint the client itself uses (<c>FUN_140a190f0</c> shape): a little-endian word whose
    /// low two bits are the count of <i>extra</i> bytes and whose remaining bits are the value.
    /// </summary>
    public static bool TryReadClientVarInt(ref WireReader reader, out uint value)
    {
        value = 0;
        if (reader.Remaining < 1)
        {
            return false;
        }

        byte first = reader.U8();
        int extra = first & 3;
        if (reader.Remaining < extra)
        {
            return false;
        }

        uint packed = first;
        for (int i = 1; i <= extra; i++)
        {
            packed |= (uint)reader.U8() << (8 * i);
        }

        value = packed >> 2;
        return true;
    }

    /// <summary>
    /// Decodes the shared lightweight-entity body out of a tunnelled 0xd6 / 0xd7 message payload
    /// (the payload starts at the base opcode). Returns null when the bytes are not that shape.
    /// </summary>
    public static LightweightEntity? TryReadLightweightEntity(ReadOnlySpan<byte> payload)
    {
        try
        {
            var r = new WireReader(payload);
            byte opcode = r.U8();
            if (opcode is not (0xD6 or 0xD7))
            {
                return null;
            }

            ulong guid = r.LeU64();
            if (!TryReadClientVarInt(ref r, out uint transient))
            {
                return null;
            }

            _ = r.CountedString();      // +0x20
            _ = r.LeU32();              // +0x38 display name id
            _ = r.U8();                 // +0x3c
            uint modelId = r.LeU32();   // +0x40
            _ = r.Bytes(16);            // +0x50 scale
            _ = r.CountedString();      // +0x60
            _ = r.CountedString();      // +0x78
            _ = r.LeU32();              // +0x90
            float x = r.LeF32();
            float y = r.LeF32();
            float z = r.LeF32();
            return new LightweightEntity(opcode, guid, transient, modelId, new Vector3(x, y, z));
        }
        catch (WireFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes the equipment-slot row list of <c>0x94 / 01</c>. The rows are what docs/45 proved
    /// fatal: a row whose slot id is 7 (RHand) hard-crashes the August client whatever it names.
    /// </summary>
    public static EquipmentRows? TryReadEquipmentRows(ReadOnlySpan<byte> payload)
    {
        try
        {
            var r = new WireReader(payload);
            if (r.U8() != 0x94 || r.U8() != 0x01)
            {
                return null;
            }

            _ = r.LeU32();                          // profile id
            ulong character = r.LeU64();
            _ = r.LeU32();
            _ = r.CountedString();                  // tint alias
            _ = r.CountedString();                  // decal alias
            uint count = r.LeU32();
            if (count > 1024)
            {
                return null;
            }

            var slots = new List<uint>((int)count);
            for (uint i = 0; i < count; i++)
            {
                uint key = r.LeU32();
                _ = r.LeU32();                      // the same slot id again (row +0x20)
                _ = r.LeU64();                      // item guid
                _ = r.CountedString();
                _ = r.CountedString();
                slots.Add(key);
            }

            return new EquipmentRows(character, slots);
        }
        catch (WireFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes a <c>ReferenceData</c> (0x17) head: <c>u16 tag (0x2000 | 13-bit name length)</c>,
    /// the name, an uncounted NUL, then the <c>u32</c> payload length. Confirmed against
    /// <c>wire-20260829-150206.txt</c>: <c>ProfileDefinitions</c> is 35 B on the wire for a 4-byte
    /// payload and the appearance table is 1,268,872 B for 1,268,831 — both exactly 41 B of
    /// envelope including the tunnel header byte.
    /// </summary>
    public static ReferenceDataHead? TryReadReferenceDataHead(ReadOnlySpan<byte> payload, int messageLength)
    {
        try
        {
            var r = new WireReader(payload);
            if (r.U8() != 0x17)
            {
                return null;
            }

            ushort tag = r.LeU16();
            int nameLength = tag & 0x1FFF;
            if (nameLength > r.Remaining)
            {
                return null;
            }

            string name = System.Text.Encoding.ASCII.GetString(r.Bytes(nameLength));
            _ = r.U8();                             // the NUL the 13-bit length does not count
            uint declared = r.LeU32();
            return new ReferenceDataHead(name, declared, messageLength);
        }
        catch (WireFormatException)
        {
            return null;
        }
    }

    /// <summary>The zone name carried by <c>ClientBeginZoning</c> (0x0b): <c>0b | str name | u32 type</c>.</summary>
    public static string? TryReadBeginZoningZoneName(ReadOnlySpan<byte> payload)
    {
        try
        {
            var r = new WireReader(payload);
            return r.U8() != 0x0B ? null : r.CountedString();
        }
        catch (WireFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// A line of server-sent text, whichever of the three console surfaces it rode on; null when
    /// the payload is not one of them.
    /// <para>
    /// Layouts, from the August parsers rather than from the server's writers:
    /// <c>06 03 00 | String8 | u8 flag | u32 code</c> (<c>FUN_141251d90</c>, console only),
    /// <c>06 05 00 | String8 | u32 | u32 colour | u32 | u8 flag | u8 alsoConsole</c>
    /// (<c>FUN_141251ba0</c>; the last byte decides whether the console pane sees it, dispatcher
    /// <c>FUN_1412568e0:775-777</c>) and <c>11 31 00 | String8</c> (<c>FUN_140afc660</c> case 0x31).
    /// </para>
    /// </summary>
    public static ConsoleLine? TryReadConsoleLine(ReadOnlySpan<byte> payload)
    {
        try
        {
            var r = new WireReader(payload);
            byte baseOpcode = r.U8();
            if (baseOpcode is not (0x06 or 0x11))
            {
                return null;
            }

            ushort sub = r.LeU16();
            return (baseOpcode, sub) switch
            {
                (0x06, 0x0003) => new ConsoleLine("print", r.CountedString()),
                (0x06, 0x0005) => ReadChat(ref r),
                (0x11, 0x0031) => new ConsoleLine("alert", r.CountedString()),
                _ => null,
            };
        }
        catch (WireFormatException)
        {
            return null;
        }

        static ConsoleLine ReadChat(ref WireReader r)
        {
            string text = r.CountedString();
            _ = r.LeU32();                          // unnamed field the client passes to PrintChat
            _ = r.LeU32();                          // colour
            _ = r.LeU32();                          // second colour
            _ = r.U8();                             // flag
            return new ConsoleLine(r.U8() != 0 ? "chat1" : "chat0", text);
        }
    }

    /// <summary>The world-object guid of <c>Character.RemovePlayer</c> (0x0f sub 1).</summary>
    public static ulong? TryReadRemovePlayerGuid(ReadOnlySpan<byte> payload)
    {
        try
        {
            var r = new WireReader(payload);
            return r.U8() != 0x0F || r.U8() != 0x01 ? null : r.LeU64();
        }
        catch (WireFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="message"/> carries <paramref name="guid"/> as a little-endian
    /// u64 anywhere in it. <c>LightweightToFullNpc</c> (0xda) is matched by the client on its
    /// <i>transient</i> id and the harness has no independent derivation of the guid's offset, so
    /// the honest test that a 0xda answers a particular <c>0F 45</c> is that it mentions the guid
    /// at all — not a guessed offset.
    /// </summary>
    public static bool Mentions(ReadOnlySpan<byte> message, ulong guid)
    {
        Span<byte> needle = stackalloc byte[8];
        for (int i = 0; i < 8; i++)
        {
            needle[i] = (byte)(guid >> (8 * i));
        }

        for (int i = 0; i + 8 <= message.Length; i++)
        {
            if (message.Slice(i, 8).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
