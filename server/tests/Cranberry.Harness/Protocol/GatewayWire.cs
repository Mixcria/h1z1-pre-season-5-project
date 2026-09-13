using Cranberry.Harness.Wire;

namespace Cranberry.Harness.Protocol;

/// <summary>
/// The gateway envelope: one header byte packing a five-bit opcode below a three-bit channel
/// (<c>opcode = b &amp; 0x1F</c>, <c>channel = b &gt;&gt; 5</c>), then the ClientProtocol bytes.
/// </summary>
public static class GatewayWire
{
    public const byte OpcodeLoginRequest = 1;
    public const byte OpcodeLoginReply = 2;

    /// <summary>Server to client tunnel.</summary>
    public const byte OpcodeTunnelToClient = 5;

    /// <summary>Client to server tunnel.</summary>
    public const byte OpcodeTunnelToServer = 6;

    /// <summary>
    /// Channel assignment is per-opcode and fixed (docs/71 §1.1): channel 1 for the UI/lobby set,
    /// channel 0 for everything else, channels 2 and 3 for the two opcode-free movement streams.
    /// A harness that puts a message on the wrong channel is not imitating the client.
    /// </summary>
    public const byte ChannelDefault = 0;

    public const byte ChannelUi = 1;

    public const byte ChannelPlayerMovement = 2;

    public const byte ChannelManagedMovement = 3;

    public static byte Header(byte opcode, byte channel)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(opcode, (byte)0x1F);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, (byte)7);
        return (byte)(opcode | (channel << 5));
    }

    public static (byte Opcode, byte Channel) SplitHeader(byte value) => ((byte)(value & 0x1F), (byte)(value >> 5));

    /// <summary>Wraps ClientProtocol bytes in a client tunnel header on the given channel.</summary>
    public static byte[] Tunnel(byte channel, ReadOnlySpan<byte> zoneBytes)
    {
        var w = new WireWriter(zoneBytes.Length + 1);
        w.U8(Header(OpcodeTunnelToServer, channel)).Raw(zoneBytes);
        return w.ToArray();
    }

    /// <summary>
    /// The one clear application message on a gateway link, 78 bytes in the reference capture:
    /// <c>01 | u64 guid | str ticket | str "ClientProtocol_1148" | str "0.0.118.208059"</c>.
    /// </summary>
    public static byte[] LoginRequest(ulong guid, string ticket, string protocol, string version)
    {
        var w = new WireWriter(96);
        w.U8(Header(OpcodeLoginRequest, ChannelDefault))
            .LeU64(guid)
            .CountedString(ticket)
            .CountedString(protocol)
            .CountedString(version);
        return w.ToArray();
    }

    /// <summary>The encrypted two-byte answer, <c>02 01</c>.</summary>
    public static bool TryParseLoginReply(ReadOnlySpan<byte> message, out bool loggedIn)
    {
        loggedIn = false;
        if (message.Length < 2)
        {
            return false;
        }

        (byte opcode, byte channel) = SplitHeader(message[0]);
        if (opcode != OpcodeLoginReply || channel != ChannelDefault)
        {
            return false;
        }

        loggedIn = message[1] != 0;
        return true;
    }
}

/// <summary>The August client's identity strings, echoed verbatim in the gateway LoginRequest.</summary>
public static class AugustClient
{
    public const string Protocol = "ClientProtocol_1148";
    public const string Version = "0.0.118.208059";
    public const string LoginProtocolName = "LoginUdp_14";
    public const string GatewayProtocolName = "ExternalGatewayApi_3";
}
