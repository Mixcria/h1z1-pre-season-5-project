using Cranberry.Protocol;

namespace Cranberry.Login;

/// <summary>
/// One login-layer roster entry. The first value is the character guid; the August listener
/// consumes the low 32 bits of <see cref="ServerId"/> as the selected server. The third u64 is
/// not consumed by the character-list listener and remains unnamed until another client path
/// gives it a role. Only entries with <see cref="StatusAvailable"/> reach the UI.
/// </summary>
public sealed record CharacterEntry(
    ulong EntityKey,
    ulong ServerId,
    ulong Field3,
    uint Status,
    byte[] Payload,
    string Name = "",
    uint Gender = 0)
{
    public const uint StatusAvailable = 1;
}

/// <summary>
/// Server → client, opcode 0x0C. Layout from the August unserializer `FUN_1421234f0`:
/// u8 id, u32, u8, i32 count, entries { u64, u64, u64, u32, bytes }.
/// </summary>
public sealed record CharacterSelectInfoReply(uint Status, bool Flag, IReadOnlyList<CharacterEntry> Characters)
{
    public const byte Opcode = 0x0C;

    public void WriteTo(PacketWriter w)
    {
        w.WriteByte(Opcode);
        w.WriteUInt32(Status);
        w.WriteBool(Flag);
        w.WriteInt32(Characters.Count);
        foreach (CharacterEntry character in Characters)
        {
            w.WriteUInt64(character.EntityKey);
            w.WriteUInt64(character.ServerId);
            w.WriteUInt64(character.Field3);
            w.WriteUInt32(character.Status);
            w.WriteCountedBytes(character.Payload);
        }
    }
}
