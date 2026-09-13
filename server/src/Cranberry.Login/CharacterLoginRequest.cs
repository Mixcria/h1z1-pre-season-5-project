using Cranberry.Protocol;

namespace Cranberry.Login;

/// <summary>
/// Client → server, opcode 0x07. Wire order from the client's own field writer
/// (`FUN_14211dca0`): u8 id, u64 entity key (the character chosen), u64 server id, u32 length +
/// payload. The payload is the client's login context written by `FUN_140f0c810` and parsed by
/// <see cref="CharacterLoginContext"/>.
/// </summary>
public sealed record CharacterLoginRequest(ulong EntityKey, ulong ServerId, byte[] Payload)
{
    public const byte Opcode = 0x07;

    /// <summary>Parses the body that follows the opcode byte.</summary>
    public static CharacterLoginRequest Parse(ReadOnlySpan<byte> body)
    {
        var r = new PacketReader(body);
        ulong entityKey = r.ReadUInt64();
        ulong serverId = r.ReadUInt64();
        byte[] payload = r.ReadCountedBytes().ToArray();
        if (!r.AtEnd)
        {
            throw new PacketFormatException(
                $"CharacterLoginRequest has {r.Remaining} trailing byte(s).");
        }

        return new CharacterLoginRequest(entityKey, serverId, payload);
    }
}
