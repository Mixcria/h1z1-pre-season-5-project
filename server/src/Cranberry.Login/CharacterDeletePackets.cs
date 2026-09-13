using Cranberry.Protocol;

namespace Cranberry.Login;

/// <summary>
/// <c>LoginUdp_14</c> <c>CharacterDeleteRequest</c> (c2s 0x09): <c>u8 0x09; u64 characterId</c>
/// (ctor <c>FUN_142127210</c>, field writer <c>FUN_14211dba0</c>). Sent by the UI binding
/// <c>DeleteCharacter(guid)</c> for the selected character; the client's guard
/// (loginClient+0x2e2) blocks further requests until a reply arrives.
/// </summary>
public sealed record CharacterDeleteRequest(ulong EntityKey)
{
    public const byte Opcode = 0x09;

    public static CharacterDeleteRequest Parse(ReadOnlySpan<byte> body)
    {
        var reader = new PacketReader(body);
        var request = new CharacterDeleteRequest(reader.ReadUInt64());
        if (!reader.AtEnd)
        {
            throw new PacketFormatException($"CharacterDeleteRequest has {reader.Remaining} trailing byte(s).");
        }

        return request;
    }
}

/// <summary>
/// <c>CharacterDeleteReply</c> (s2c 0x0A): <c>u8 0x0A; u64 characterId; u32 status; bytes payload</c>
/// (unserializer <c>FUN_14211e4e0</c>, handler <c>FUN_142119d40 → FUN_140f18dc0 → FUN_140f28260</c>).
/// Status 1 removes the row with that guid from the character-select list and fires
/// <c>EVENT_CHARACTER_DELETE(true, 1)</c>; any other status only fires the event with
/// success = false. The payload is never read; the client sends nothing afterwards, so the
/// roster must simply not list the character on the next <c>CharacterSelectInfoReply</c>.
/// </summary>
public sealed record CharacterDeleteReply(ulong EntityKey, uint Status)
{
    public const byte Opcode = 0x0A;
    public const uint Success = 1;
    public const uint Failure = 0;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt64(EntityKey);
        writer.WriteUInt32(Status);
        writer.WriteCountedBytes([]);
    }
}
