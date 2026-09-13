namespace Cranberry.Transport;

/// <summary>Opening datagram from a client (opcode 0x0001).</summary>
public readonly record struct SessionRequest(uint CrcLength, uint SessionId, uint UdpLength, string ProtocolName)
{
    public const int MaxProtocolNameLength = 32;

    /// <summary>Parses the body that follows the opcode.</summary>
    public static SessionRequest Parse(ReadOnlySpan<byte> body)
    {
        var reader = new SpanReader(body);
        uint crcLength = reader.ReadUInt32();
        uint sessionId = reader.ReadUInt32();
        uint udpLength = reader.ReadUInt32();
        string protocol = reader.ReadCString(MaxProtocolNameLength);
        return new SessionRequest(crcLength, sessionId, udpLength, protocol);
    }
}

/// <summary>Our answer to a SessionRequest (opcode 0x0002); always 21 bytes.</summary>
public static class SessionReply
{
    public const int Length = 21;

    public static int Write(Span<byte> destination, uint sessionId, SessionSettings settings)
    {
        var writer = new SpanWriter(destination);
        writer.WriteUInt16((ushort)SoeOpcode.SessionReply);
        writer.WriteUInt32(sessionId);
        writer.WriteUInt32(settings.CrcSeed);
        writer.WriteByte(settings.CrcLength);
        writer.WriteUInt16(settings.Compression);
        writer.WriteUInt32(settings.UdpLength);
        writer.WriteUInt32(settings.ProtocolVersion);
        return writer.Written;
    }
}

/// <summary>Either side ends the session (opcode 0x0005): session id, then a reason code.</summary>
public readonly record struct DisconnectPacket(uint SessionId, ushort Reason)
{
    public const int Length = 8;

    public static DisconnectPacket Parse(ReadOnlySpan<byte> body)
    {
        var reader = new SpanReader(body);
        return new DisconnectPacket(reader.ReadUInt32(), reader.ReadUInt16());
    }

    public int Write(Span<byte> destination)
    {
        var writer = new SpanWriter(destination);
        writer.WriteUInt16((ushort)SoeOpcode.Disconnect);
        writer.WriteUInt32(SessionId);
        writer.WriteUInt16(Reason);
        return writer.Written;
    }
}

/// <summary>Reason codes we use when we close a session.</summary>
public static class DisconnectReason
{
    public const ushort None = 0;
    public const ushort Application = 6;
    public const ushort ProtocolError = 7;
    public const ushort Timeout = 8;
}

/// <summary>Two-byte control datagrams: Ack (0x0015) and OutOfOrder (0x0011) carry one sequence number.</summary>
public static class SequencePacket
{
    public const int Length = 4;

    public static int Write(Span<byte> destination, SoeOpcode opcode, ushort sequence)
    {
        var writer = new SpanWriter(destination);
        writer.WriteUInt16((ushort)opcode);
        writer.WriteUInt16(sequence);
        return writer.Written;
    }
}
