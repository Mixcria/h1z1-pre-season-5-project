using Cranberry.Protocol;

namespace Cranberry.Login;

/// <summary>
/// Server to client, opcode 0x08. The binary parser is <c>FUN_14211e640</c>; its XML twin
/// <c>FUN_142119e70</c> names the fields EntityKey, ServerId, Status, and Payload.
/// </summary>
public sealed record CharacterLoginReply(
    ulong EntityKey,
    ulong ServerId,
    uint Status,
    byte[] Payload)
{
    public const byte Opcode = 0x08;
    public const uint StatusSuccess = 1;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt64(EntityKey);
        writer.WriteUInt64(ServerId);
        writer.WriteUInt32(Status);
        writer.WriteCountedBytes(Payload);
    }
}
