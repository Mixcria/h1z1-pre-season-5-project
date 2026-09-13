using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>The gateway header packs its five-bit opcode below its three-bit channel.</summary>
public readonly record struct GatewayHeader(byte Opcode, byte Channel)
{
    public static GatewayHeader Parse(byte value) =>
        new((byte)(value & 0x1F), (byte)(value >> 5));

    public byte ToByte()
    {
        if (Opcode > 0x1F)
        {
            throw new ArgumentOutOfRangeException(nameof(Opcode));
        }

        if (Channel > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(Channel));
        }

        return (byte)(Opcode | (Channel << 5));
    }
}

/// <summary>The one clear application packet at the start of an August gateway link.</summary>
public sealed record GatewayLoginRequest(
    ulong Guid,
    string Ticket,
    string ClientProtocol,
    string ClientVersion)
{
    public const byte Opcode = 1;
    public const string AugustProtocol = "ClientProtocol_1148";
    public const string AugustVersion = "0.0.118.208059";

    public static GatewayLoginRequest Parse(ReadOnlySpan<byte> packet)
    {
        var reader = new PacketReader(packet);
        GatewayHeader header = GatewayHeader.Parse(reader.ReadByte());
        if (header.Opcode != Opcode || header.Channel != 0)
        {
            throw new PacketFormatException(
                $"Expected gateway login header 0x01, got opcode {header.Opcode} channel {header.Channel}.");
        }

        var request = new GatewayLoginRequest(
            Guid: reader.ReadUInt64(),
            Ticket: reader.ReadString(),
            ClientProtocol: reader.ReadString(),
            ClientVersion: reader.ReadString());
        if (!reader.AtEnd)
        {
            throw new PacketFormatException(
                $"Gateway LoginRequest has {reader.Remaining} trailing byte(s).");
        }

        return request;
    }
}

/// <summary>Encrypted server response after the clear gateway login request.</summary>
public sealed record GatewayLoginReply(bool LoggedIn)
{
    public const byte Opcode = 2;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(new GatewayHeader(Opcode, Channel: 0).ToByte());
        writer.WriteBool(LoggedIn);
    }
}

/// <summary>Opaque ClientProtocol bytes sent by the August client through gateway opcode 6.</summary>
public sealed record GatewayTunnelFromClient(byte Channel, byte[] Payload)
{
    public const byte Opcode = 6;

    public static GatewayTunnelFromClient Parse(ReadOnlySpan<byte> packet)
    {
        var reader = new PacketReader(packet);
        GatewayHeader header = GatewayHeader.Parse(reader.ReadByte());
        if (header.Opcode != Opcode)
        {
            throw new PacketFormatException(
                $"Expected gateway client-tunnel opcode {Opcode}, got {header.Opcode}.");
        }

        return new GatewayTunnelFromClient(header.Channel, reader.ReadRest().ToArray());
    }
}

/// <summary>Opaque ClientProtocol bytes sent to the August client through gateway opcode 5.</summary>
public sealed record GatewayTunnelToClient(byte Channel, byte[] Payload)
{
    public const byte Opcode = 5;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(new GatewayHeader(Opcode, Channel).ToByte());
        writer.WriteRaw(Payload);
    }
}
