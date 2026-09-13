namespace Cranberry.Harness.Wire;

/// <summary>
/// SOE datagram opcodes, as the first big-endian u16 of a datagram. These are wire facts read off
/// the captures; the harness declares its own copy rather than importing the server's enum so
/// that the two sides can be compared instead of assumed equal.
/// </summary>
public static class SoeOp
{
    public const ushort SessionRequest = 0x0001;
    public const ushort SessionReply = 0x0002;
    public const ushort Multi = 0x0003;
    public const ushort Disconnect = 0x0005;
    public const ushort Ping = 0x0006;
    public const ushort NetStatusRequest = 0x0007;
    public const ushort NetStatusReply = 0x0008;
    public const ushort Data = 0x0009;
    public const ushort DataFragment = 0x000D;
    public const ushort OutOfOrder = 0x0011;
    public const ushort Ack = 0x0015;

    /// <summary>Not a datagram opcode: the marker beginning a reliable payload that carries
    /// several length-prefixed application messages.</summary>
    public const ushort Bundle = 0x0019;

    public const ushort FatalError = 0x001D;
    public const ushort FatalErrorReply = 0x001E;

    public static string Name(ushort opcode) => opcode switch
    {
        SessionRequest => "SessionRequest",
        SessionReply => "SessionReply",
        Multi => "Multi",
        Disconnect => "Disconnect",
        Ping => "Ping",
        NetStatusRequest => "NetStatusRequest",
        NetStatusReply => "NetStatusReply",
        Data => "Data",
        DataFragment => "DataFragment",
        OutOfOrder => "OutOfOrder",
        Ack => "Ack",
        FatalError => "FatalError",
        FatalErrorReply => "FatalErrorReply",
        _ => $"0x{opcode:x4}",
    };
}

/// <summary>Reason codes carried by a Disconnect datagram.</summary>
public static class SoeDisconnectReason
{
    public const ushort None = 0;

    /// <summary>What the August client sends when the player quits; the server logs PeerRequested.</summary>
    public const ushort Application = 6;

    public const ushort ProtocolError = 7;
    public const ushort Timeout = 8;
}

/// <summary>
/// The variable-length size prefixing each sub-packet of a Multi datagram and each chunk of a
/// bundle. Re-implemented here (see <see cref="HarnessRc4"/> for why): first byte below 0xFF is
/// the length; 0xFF then a big-endian u16; 0xFF 0xFF 0xFF then a big-endian u32.
/// </summary>
public static class SoeVarSize
{
    public static int Read(ReadOnlySpan<byte> buffer, ref int offset)
    {
        Need(buffer, offset, 1);
        byte first = buffer[offset];
        if (first < 0xFF)
        {
            offset += 1;
            return first;
        }

        Need(buffer, offset, 3);
        if (buffer[offset + 1] == 0xFF && buffer[offset + 2] == 0xFF)
        {
            Need(buffer, offset, 7);
            uint wide = (uint)((buffer[offset + 3] << 24) | (buffer[offset + 4] << 16)
                | (buffer[offset + 5] << 8) | buffer[offset + 6]);
            if (wide > int.MaxValue)
            {
                throw new WireFormatException("Sub-packet length does not fit in an int.");
            }

            offset += 7;
            return (int)wide;
        }

        int medium = (buffer[offset + 1] << 8) | buffer[offset + 2];
        offset += 3;
        return medium;
    }

    public static int SizeOf(int value) => value switch
    {
        < 0 => throw new ArgumentOutOfRangeException(nameof(value)),
        < 0xFF => 1,
        < 0xFFFF => 3,
        _ => 7,
    };

    public static void Write(WireWriter writer, int value)
    {
        switch (SizeOf(value))
        {
            case 1:
                writer.U8((byte)value);
                break;
            case 3:
                writer.U8(0xFF).BeU16((ushort)value);
                break;
            default:
                writer.U8(0xFF).U8(0xFF).U8(0xFF).BeU32((uint)value);
                break;
        }
    }

    private static void Need(ReadOnlySpan<byte> buffer, int offset, int count)
    {
        if (offset < 0 || buffer.Length - offset < count)
        {
            throw new WireFormatException($"Sub-packet length prefix at {offset} runs past the datagram.");
        }
    }
}

/// <summary>
/// What the server dictated in its SessionReply. The August server sends CrcLength 0 and
/// Compression 0; the harness parses whatever arrives and refuses loudly if it is asked to speak
/// a variant it has no capture evidence for, rather than guessing a CRC polynomial.
/// </summary>
public sealed record SoeSessionParameters(
    uint SessionId,
    uint CrcSeed,
    byte CrcLength,
    ushort Compression,
    uint UdpLength,
    uint ProtocolVersion)
{
    public const int ReplyLength = 21;

    /// <summary>Opcode (2) + optional compression flag (1) + sequence (2).</summary>
    public int ReliableHeaderLength => 4 + (Compression != 0 ? 1 : 0);

    /// <summary>Application bytes one reliable datagram can carry.</summary>
    public int MaxReliablePayload => (int)UdpLength - ReliableHeaderLength - CrcLength;

    public static SoeSessionParameters ParseReply(ReadOnlySpan<byte> datagram)
    {
        var r = new WireReader(datagram);
        ushort opcode = r.BeU16();
        if (opcode != SoeOp.SessionReply)
        {
            throw new WireFormatException($"Expected a SessionReply, got {SoeOp.Name(opcode)}.");
        }

        return new SoeSessionParameters(
            SessionId: r.BeU32(),
            CrcSeed: r.BeU32(),
            CrcLength: r.U8(),
            Compression: r.BeU16(),
            UdpLength: r.BeU32(),
            ProtocolVersion: r.BeU32());
    }

    public static byte[] BuildRequest(uint crcLength, uint sessionId, uint udpLength, string protocolName)
    {
        var w = new WireWriter(32);
        w.BeU16(SoeOp.SessionRequest).BeU32(crcLength).BeU32(sessionId).BeU32(udpLength).CString(protocolName);
        return w.ToArray();
    }
}
