using Cranberry.Protocol;

namespace Cranberry.Login;

/// <summary>
/// Client-to-server login tunnel (opcode 0x10): a game-server id followed by one counted
/// application payload. Layout derived from the August client parser at FUN_14211ecc0.
/// </summary>
public sealed record TunnelAppPacketClientToServer(ulong ServerId, byte[] Payload)
{
    public const byte Opcode = 0x10;

    /// <summary>Parses the body that follows the login opcode.</summary>
    public static TunnelAppPacketClientToServer Parse(ReadOnlySpan<byte> body)
    {
        var reader = new PacketReader(body);
        ulong serverId = reader.ReadUInt64();
        byte[] payload = reader.ReadCountedBytes().ToArray();
        if (!reader.AtEnd)
        {
            throw new PacketFormatException(
                $"TunnelAppPacketClientToServer has {reader.Remaining} trailing byte(s).");
        }

        return new TunnelAppPacketClientToServer(serverId, payload);
    }
}

/// <summary>
/// Server-to-client login tunnel (opcode 0x11). The August client uses the same
/// server-id/count/payload body as the client-to-server form.
/// </summary>
public sealed record TunnelAppPacketServerToClient(ulong ServerId, byte[] Payload)
{
    public const byte Opcode = 0x11;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt64(ServerId);
        writer.WriteCountedBytes(Payload);
    }
}
