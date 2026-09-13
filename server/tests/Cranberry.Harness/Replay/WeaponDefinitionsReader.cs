using Cranberry.Harness.Wire;

namespace Cranberry.Harness.Replay;

/// <summary>One list-0 record of the <c>ReferenceData "WeaponDefinitions"</c> table, as bytes.</summary>
/// <param name="Key">The list key — the record's id as the list states it (<c>def+0x110</c>).</param>
/// <param name="Offset">Where the record starts inside the blob, for a message that cites bytes.</param>
/// <param name="Bytes">The record itself, so an assertion can index the offsets it cares about.</param>
public sealed record WeaponDefinitionRecord(uint Key, int Offset, byte[] Bytes)
{
    /// <summary>
    /// <c>def+0x18</c>, the body copy of the id, at record bytes 4..7. The record's vtable
    /// <c>+0x20</c> getter is <c>mov eax,[rcx+0x18]; ret</c> (<c>FUN_1421e5f50</c>), and that is the
    /// key the local <c>WeaponComponent</c> resolves its definition by — so a zero here means the
    /// drawn weapon never finds its own record (refute-1 §2).
    /// </summary>
    public uint BodyId => BitConverter.ToUInt32(Bytes, 4);

    /// <summary><c>def+0x58</c> <c>Weapon.TurnModifier</c> — record bytes 57..60.</summary>
    public uint TurnModifierBits => BitConverter.ToUInt32(Bytes, 57);

    /// <summary><c>def+0x5c</c> <c>Weapon.MovementModifier</c> — record bytes 61..64.</summary>
    public uint MovementModifierBits => BitConverter.ToUInt32(Bytes, 61);

    /// <summary>The two multipliers as the floats the client reads them as.</summary>
    public float TurnModifier => BitConverter.ToSingle(Bytes, 57);

    /// <inheritdoc cref="TurnModifier"/>
    public float MovementModifier => BitConverter.ToSingle(Bytes, 61);

    public override string ToString() =>
        $"id={Key} body={BodyId} +0x58={TurnModifierBits:x8} +0x5c={MovementModifierBits:x8}";
}

/// <summary>
/// Reads the list-0 records out of a <c>ReferenceData "WeaponDefinitions"</c> message.
///
/// <para><b>Why the harness needs its own reader.</b> The three fields the wave-11 fix writes —
/// <c>def+0x18</c>, <c>def+0x58</c> and <c>def+0x5c</c> — are the whole of that fix, and the only
/// place they exist is inside an 11.5 KB blob. Asserting them from the server's own writer would
/// prove nothing (it would be the writer checking itself); asserting them off the wire, from the
/// same bytes the client parses, is what S9 is for. The walk below is the August reader's field
/// order (<c>FUN_140a46ef0</c>) transcribed as a length walk: it does not name the twenty-odd values
/// it steps over, because it does not need to — it needs the record's boundaries.</para>
/// </summary>
public static class WeaponDefinitionsReader
{
    /// <summary>The ReferenceData type name this reader accepts.</summary>
    public const string TypeName = "WeaponDefinitions";

    /// <summary><c>1.0f</c> as it appears on the wire.</summary>
    public const uint OneBits = 0x3F80_0000;

    /// <summary>Record byte offset of <c>def+0x58</c> <c>Weapon.TurnModifier</c>.</summary>
    public const int TurnModifierOffset = 57;

    /// <summary>Record byte offset of <c>def+0x5c</c> <c>Weapon.MovementModifier</c>.</summary>
    public const int MovementModifierOffset = 61;

    /// <summary>
    /// Parses one whole <c>0x17</c> message (payload from the base opcode byte). Returns null when
    /// it is a different ReferenceData table, or the blob does not walk cleanly to the end of a
    /// record — a partial walk is never returned, because half a table would assert on garbage.
    /// </summary>
    public static IReadOnlyList<WeaponDefinitionRecord>? TryReadListZero(ReadOnlySpan<byte> payload)
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
            if (nameLength <= 0 || nameLength > r.Remaining)
            {
                return null;
            }

            if (!System.Text.Encoding.ASCII.GetString(r.Bytes(nameLength)).Equals(TypeName, StringComparison.Ordinal))
            {
                return null;
            }

            _ = r.U8();         // the NUL the 13-bit length does not count
            _ = r.LeU32();      // declared payload length
            _ = r.LeU32();      // the client's envelope repeats it

            int blobStart = r.Position;
            uint count = r.LeU32();
            if (count is 0 or > 4096)
            {
                return null;
            }

            var records = new List<WeaponDefinitionRecord>((int)count);
            for (uint i = 0; i < count; i++)
            {
                int start = r.Position;
                uint key = r.LeU32();
                _ = r.LeU32();              // +0x18, the body id
                _ = r.LeU32();              // +0x20
                _ = r.U8();                 // +0x24
                for (int w = 0; w < 18; w++)
                {
                    _ = r.LeU32();          // +0x28 .. +0x80, the block 0x58 and 0x5c live in
                }

                _ = r.LeU32();              // the sub-structure's pair
                _ = r.LeU32();
                _ = r.CountedBytes();       // the record's one string
                for (int w = 0; w < 7; w++)
                {
                    _ = r.LeU32();
                }

                uint arrayCount = r.LeU32();
                if (arrayCount > (uint)r.Remaining / 4)
                {
                    return null;
                }

                for (uint a = 0; a < arrayCount; a++)
                {
                    _ = r.LeU32();
                }

                uint fireGroups = r.LeU32();
                if (fireGroups > (uint)r.Remaining / 4)
                {
                    return null;
                }

                for (uint f = 0; f < fireGroups; f++)
                {
                    _ = r.LeU32();
                }

                records.Add(new WeaponDefinitionRecord(
                    key,
                    start - blobStart,
                    payload[start..r.Position].ToArray()));
            }

            return records;
        }
        catch (WireFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The records that do not carry the client's own defaults: an id at bytes 4..7 and
    /// <c>0x3f800000</c> at 57..60 and 61..64. An empty result is what wave 11 is for.
    /// </summary>
    public static IReadOnlyList<WeaponDefinitionRecord> Offenders(IEnumerable<WeaponDefinitionRecord> records) =>
    [
        .. records.Where(record =>
            record.BodyId != record.Key
            || record.TurnModifierBits != OneBits
            || record.MovementModifierBits != OneBits)
    ];
}
