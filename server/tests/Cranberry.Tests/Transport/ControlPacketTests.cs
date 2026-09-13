using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

public class ControlPacketTests
{
    [Fact]
    public void SessionRequestParsesBody()
    {
        byte[] body =
        [
            0x00, 0x00, 0x00, 0x03,            // crc length the client proposes
            0x12, 0x34, 0x56, 0x78,            // session id
            0x00, 0x00, 0x02, 0x00,            // udp length 512
            (byte)'L', (byte)'o', (byte)'g', (byte)'i', (byte)'n', (byte)'U', (byte)'d', (byte)'p', (byte)'_', (byte)'1', (byte)'4', 0x00,
        ];

        SessionRequest request = SessionRequest.Parse(body);

        Assert.Equal(3u, request.CrcLength);
        Assert.Equal(0x12345678u, request.SessionId);
        Assert.Equal(512u, request.UdpLength);
        Assert.Equal("LoginUdp_14", request.ProtocolName);
    }

    [Fact]
    public void SessionRequestWithoutTerminatorIsRejected()
    {
        byte[] body = [0, 0, 0, 3, 1, 2, 3, 4, 0, 0, 2, 0, (byte)'L', (byte)'o'];
        Assert.Throws<SoeProtocolException>(() => SessionRequest.Parse(body));
    }

    [Fact]
    public void SessionReplyIsTwentyOneBytesInDictatedOrder()
    {
        var settings = new SessionSettings { CrcSeed = 0xAABBCCDD, CrcLength = 0, Compression = 0, UdpLength = 512, ProtocolVersion = 3 };
        byte[] buffer = new byte[SessionReply.Length];

        int written = SessionReply.Write(buffer, 0x01020304, settings);

        Assert.Equal(21, written);
        Assert.Equal(
            new byte[]
            {
                0x00, 0x02,
                0x01, 0x02, 0x03, 0x04,
                0xAA, 0xBB, 0xCC, 0xDD,
                0x00,
                0x00, 0x00,
                0x00, 0x00, 0x02, 0x00,
                0x00, 0x00, 0x00, 0x03,
            },
            buffer);
    }

    [Fact]
    public void DisconnectRoundTrips()
    {
        byte[] buffer = new byte[DisconnectPacket.Length];
        int written = new DisconnectPacket(0xDEADBEEF, 6).Write(buffer);

        Assert.Equal(8, written);
        Assert.Equal((ushort)SoeOpcode.Disconnect, (ushort)((buffer[0] << 8) | buffer[1]));

        DisconnectPacket parsed = DisconnectPacket.Parse(buffer.AsSpan(2));
        Assert.Equal(0xDEADBEEFu, parsed.SessionId);
        Assert.Equal(6, parsed.Reason);
    }

    [Fact]
    public void MaxReliablePayloadLeavesRoomForTheHeader()
    {
        var settings = new SessionSettings { UdpLength = 512 };
        Assert.Equal(508, settings.MaxReliablePayload);

        var withCrc = new SessionSettings { UdpLength = 512, CrcLength = 2 };
        Assert.Equal(506, withCrc.MaxReliablePayload);
    }
}
