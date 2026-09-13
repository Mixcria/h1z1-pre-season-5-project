using Cranberry.Protocol;

namespace Cranberry.Login;

/// <summary>
/// The counted payload inside a successful <see cref="CharacterLoginReply"/>. The August
/// parser is <c>FUN_140f0cda0</c>; <c>FUN_140b094e0</c> copies the address, ticket, key,
/// cipher mode, gateway id, and character guid into the gateway client.
/// </summary>
public sealed record GatewayConnectInfo(
    uint GatewayId,
    string Address,
    string Ticket,
    byte[] Key,
    uint CipherMode,
    ulong Guid,
    ulong Reserved,
    string Text10,
    string Text11,
    string Text12,
    ulong FeatureBits)
{
    public const byte FamilyTag = 0xA6;
    public const byte SubTag = 0x0D;
    public const uint CipherNone = 0;
    public const uint CipherRc4 = 3;

    public byte[] ToArray()
    {
        using var writer = new PacketWriter();
        WriteTo(writer);
        return writer.Written.ToArray();
    }

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(FamilyTag);
        writer.WriteByte(SubTag);
        writer.WriteUInt32(GatewayId);
        writer.WriteString(Address);
        writer.WriteString(Ticket);
        writer.WriteCountedBytes(Key);
        writer.WriteUInt32(CipherMode);
        writer.WriteUInt64(Guid);
        writer.WriteUInt64(Reserved);
        writer.WriteString(Text10);
        writer.WriteString(Text11);
        writer.WriteString(Text12);
        writer.WriteUInt64(FeatureBits);
    }
}
