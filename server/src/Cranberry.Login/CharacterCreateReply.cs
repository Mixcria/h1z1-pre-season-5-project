using Cranberry.Protocol;

namespace Cranberry.Login;

/// <summary>
/// Server → client, opcode 0x06. Layout from the August unserializer `FUN_142119af0` and the
/// XML twin `FUN_142119a00` (field names `Status`, `EntityKey`): u8 id, u32 status, u64 entity
/// key. The login layer's post-parse handler (`FUN_142127520`) treats status 1 as success and
/// hands the entity key to the game listener (vtable slot +0x38, `FUN_140f18a10`).
/// </summary>
public sealed record CharacterCreateReply(uint Status, ulong EntityKey)
{
    public const byte Opcode = 0x06;

    public const uint StatusSuccess = 1;
    public const uint StatusFailure = 0;

    public void WriteTo(PacketWriter w)
    {
        w.WriteByte(Opcode);
        w.WriteUInt32(Status);
        w.WriteUInt64(EntityKey);
    }
}
