namespace Cranberry.Transport;

/// <summary>
/// The parameters this server dictates in its SessionReply. The client proposes values in its
/// SessionRequest (it asks for a 3-byte CRC); what it accepts is what the reply says.
/// </summary>
public sealed record SessionSettings
{
    /// <summary>Bytes of CRC trailer on each reliable datagram. Zero: no trailer at all.</summary>
    public byte CrcLength { get; init; }

    /// <summary>Seed for the CRC. Meaningless while <see cref="CrcLength"/> is zero, but sent anyway.</summary>
    public uint CrcSeed { get; init; }

    /// <summary>Zero: no flag byte between the opcode and the sequence number of reliable datagrams.</summary>
    public ushort Compression { get; init; }

    /// <summary>Largest datagram either side sends. Reliable payloads are cut to fit.</summary>
    public uint UdpLength { get; init; } = 512;

    public uint ProtocolVersion { get; init; } = 3;

    /// <summary>Opcode (2) + sequence (2).</summary>
    public int ReliableHeaderLength => 4 + (Compression != 0 ? 1 : 0);

    /// <summary>Bytes of application data one reliable datagram can carry.</summary>
    public int MaxReliablePayload => (int)UdpLength - ReliableHeaderLength - CrcLength;

    public static SessionSettings WithSeed(uint crcSeed) => new() { CrcSeed = crcSeed };
}
