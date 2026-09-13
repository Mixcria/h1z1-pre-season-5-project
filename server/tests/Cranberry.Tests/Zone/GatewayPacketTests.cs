using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public class GatewayPacketTests
{
    [Theory]
    [InlineData(0, 1, 0x01)]
    [InlineData(2, 5, 0x45)]
    [InlineData(7, 6, 0xE6)]
    public void HeaderRoundTrips(byte channel, byte opcode, byte expected)
    {
        byte encoded = new GatewayHeader(opcode, channel).ToByte();
        GatewayHeader decoded = GatewayHeader.Parse(encoded);

        Assert.Equal(expected, encoded);
        Assert.Equal(opcode, decoded.Opcode);
        Assert.Equal(channel, decoded.Channel);
    }

    [Fact]
    public void ParsesExpectedClearAugustLoginRequest()
    {
        byte[] packet = Convert.FromHexString(
            "010110000000000000" +
            "100000006372616E62657272792D7469636B6574" +
            "13000000436C69656E7450726F746F636F6C5F31313438" +
            "0E000000302E302E3131382E323038303539");

        GatewayLoginRequest request = GatewayLoginRequest.Parse(packet);

        Assert.Equal(0x1001ul, request.Guid);
        Assert.Equal("cranberry-ticket", request.Ticket);
        Assert.Equal(GatewayLoginRequest.AugustProtocol, request.ClientProtocol);
        Assert.Equal(GatewayLoginRequest.AugustVersion, request.ClientVersion);
    }

    [Fact]
    public void LoginRequestRejectsWrongHeaderAndTrailingBytes()
    {
        Assert.Throws<PacketFormatException>(() => GatewayLoginRequest.Parse([0x02]));

        byte[] minimal = [
            0x01,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0,
            0, 0, 0, 0,
            0, 0, 0, 0,
            0xFF,
        ];
        Assert.Throws<PacketFormatException>(() => GatewayLoginRequest.Parse(minimal));
    }

    [Fact]
    public void EncryptedSuccessReplyStartsAtRc4PositionZero()
    {
        using var writer = new PacketWriter();
        new GatewayLoginReply(LoggedIn: true).WriteTo(writer);
        Assert.Equal(new byte[] { 0x02, 0x01 }, writer.Written.ToArray());

        byte[] cipher = writer.Written.ToArray();
        var rc4 = new Rc4Cipher(Convert.FromHexString("17BD086B1B94F02FF0EC53D763589B5F"));
        rc4.Transform(cipher);

        Assert.Equal(Convert.FromHexString("A9B7"), cipher);
    }

    [Fact]
    public void TunnelCodecsPreserveChannelAndOpaqueClientProtocolBytes()
    {
        GatewayTunnelFromClient inbound = GatewayTunnelFromClient.Parse(
            Convert.FromHexString("269A05130000004C6F6164696E6753637265656E57696E646F77"));
        Assert.Equal(1, inbound.Channel);
        Assert.Equal(
            Convert.FromHexString("9A05130000004C6F6164696E6753637265656E57696E646F77"),
            inbound.Payload);

        using var writer = new PacketWriter();
        new GatewayTunnelToClient(
            Channel: 7,
            Payload: [0xAA, 0xBB])
            .WriteTo(writer);
        Assert.Equal(Convert.FromHexString("E5AABB"), writer.Written.ToArray());
    }

    [Fact]
    public void MinimumSendSelfProbeHasFrozenGatewayVector()
    {
        using var inner = new PacketWriter();
        new SendSelfToClient([]).WriteTo(inner);
        Assert.Equal(Convert.FromHexString("0300000000"), inner.Written.ToArray());

        using var outer = new PacketWriter();
        new GatewayTunnelToClient(Channel: 0, Payload: inner.Written.ToArray()).WriteTo(outer);
        Assert.Equal(Convert.FromHexString("050300000000"), outer.Written.ToArray());
    }

    [Fact]
    public void MinimumLoginZoneDetailsHasFrozenAugustParserShape()
    {
        using var inner = new PacketWriter();
        new SendZoneDetails("LoginZone").WriteTo(inner);

        // Name, then zone type 4 (HeightfieldLod, the only world implementation the client
        // registers), then 176 zero bytes: u8; 21 x u32; str ""; 12 x u32; 5 x u32; u64; u8;
        // str ""; u8; u8; i32 0 (docs/07 §6 vector C).
        byte[] prefix = Convert.FromHexString(
            "16" +
            "09000000" +
            "4C6F67696E5A6F6E65" +
            "04000000");
        // The weather struct carries the D18 fixed-KOTK values (16 more bytes for the cloud
        // texture name), and D19 activates the client's 15-byte Lighting_Z2.txt table.
        Assert.Equal(225, inner.Position);
        Assert.Equal(prefix, inner.Written.Slice(0, prefix.Length).ToArray());
        using var weather = new PacketWriter();
        WeatherSettings.Kotk2017.WriteTo(weather);
        Assert.Equal(weather.Written.ToArray(), inner.Written.Slice(prefix.Length + 1, weather.Position).ToArray());
        Assert.Equal(0, inner.Written[prefix.Length]);                                  // the u8 before the struct
        int tail = prefix.Length + 1 + weather.Position;
        Assert.All(inner.Written.Slice(tail, 5 * sizeof(uint) + sizeof(ulong) + 1).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(
            Convert.FromHexString("0F0000004C69676874696E675F5A322E747874"),
            inner.Written.Slice(tail + 5 * sizeof(uint) + sizeof(ulong) + 1, 4 + SendZoneDetails.KotkLightingFile.Length).ToArray());
        Assert.All(inner.Written.Slice(inner.Position - 2).ToArray(), value => Assert.Equal(0, value));

        using var outer = new PacketWriter();
        new GatewayTunnelToClient(Channel: 0, Payload: inner.Written.ToArray()).WriteTo(outer);
        Assert.Equal(226, outer.Position);
        Assert.Equal(0x05, outer.Written[0]);
        Assert.Equal(prefix, outer.Written.Slice(1, prefix.Length).ToArray());
    }

    [Fact]
    public void ZoneDetailsWritesWorldTypeImmediatelyAfterZoneName()
    {
        using var writer = new PacketWriter();
        new SendZoneDetails("LoginZone", ZoneType: 4).WriteTo(writer);

        Assert.Equal(Convert.FromHexString("04000000"), writer.Written.Slice(14, 4).ToArray());
    }

    [Fact]
    public void LaterStateGatePacketsHaveTheirBinaryDerivedShapes()
    {
        using var done = new PacketWriter();
        new ZoneDoneSendingInitialData().WriteTo(done);
        Assert.Equal(new byte[] { 0x05 }, done.Written.ToArray());

        using var preload = new PacketWriter();
        new DoneSendingPreloadCharacters(Flag: false).WriteTo(preload);
        Assert.Equal(Convert.FromHexString("11190000"), preload.Written.ToArray());

        using var weather = new PacketWriter();
        new UpdateWeatherData().WriteTo(weather);
        // CA + 21 floats + "sky_Z_clouds.dds" + 12 floats = 1 + 84 + 20 + 48. This is the
        // owner's fixed KOTK palette: no precipitation or cloud coverage, sun axis 45/0/0,
        // wind -1/-.05/-1 at 3, and the Z1-selected layer/lighting values.
        Assert.Equal(153, weather.Position);
        Assert.Equal(
            Convert.FromHexString(
                "CA" +
                "0000803F" + "CEC03539" + "00002041" + "FAED6B3C" +
                "00000000" + "00009642" + "00000000" +
                "00000000" + "00000000" + "00000000" + "00000000" + "00000000" +
                "00003442" + "00000000" + "00000000" +
                "000080BF" + "CDCC4CBD" + "000080BF" + "00004040" +
                "00000000" + "00000000" +
                "10000000" + "736B795F5A5F636C6F7564732E646473" +
                "9A99993E" + "00000000" + "00000000" + "00007A44" +
                "CDCC4C3E" + "00000000" + "6F12033B" + "0000FA45" +
                "EC51B83D" + "0000803E" + "0000E040" + "00000000"),
            weather.Written.ToArray());

        Assert.Same(WeatherSettings.Kotk2017, WeatherSettings.ClearDay);
        Assert.Equal("SendSelfToClient", ZoneOpcodes.Name(0x03));
        Assert.Equal("ClientUpdateBase", ZoneOpcodes.Name(0x11));
        Assert.Equal("UpdateWeatherData", ZoneOpcodes.Name(0xCA));
        Assert.Null(ZoneOpcodes.Name(0x00));
    }

    [Fact]
    public void WeatherRejectsValuesThatWouldPoisonTheClientSkyBlend()
    {
        WeatherSettings[] invalidSettings =
        [
            WeatherSettings.Kotk2017 with { TransitionTime = 0 },
            WeatherSettings.Kotk2017 with { FogDensity = float.NaN },
            WeatherSettings.Kotk2017 with { FogFloor = 0 },
            WeatherSettings.Kotk2017 with { FogGradient = float.PositiveInfinity },
        ];

        Assert.All(invalidSettings, settings =>
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                using var writer = new PacketWriter();
                settings.WriteTo(writer);
            });
        });
    }

    [Fact]
    public void CompleteSendSelfRecordHasFrozenHeadAndExactLoaderLength()
    {
        // The head through the identity sub-record (blob 0..145) is fixed by FUN_140a31140 and
        // FUN_140a40000; the remaining 694 bytes are the empty lists and zero scalars of the
        // other 93 sub-loaders. The loader asserts the record is consumed exactly, so the
        // total length is part of the contract (live cursor trace 2026-08-28 20:05).
        SendSelfToClient packet = SendSelfToClient.FromRecord(new SelfRecord
        {
            Guid = 0x1001,
            Identity = new SelfIdentity { Name = "Cranberry" },
        });
        using var inner = new PacketWriter();
        packet.WriteTo(inner);

        byte[] head = Convert.FromHexString(
            "03" + "50030000" +                              // opcode, i32 length 848
            "0000000000000000" +                              // +0xd0
            "0110000000000000" +                              // guid
            "00" +                                            // varint +0xe0
            "0000000000000000" +                              // server time
            "00000000" +                                      // +0xb9dc
            "0000000000000000" +                              // str, str
            "0000000000000000" +                              // +0xb9e0, +0xb9e4
            "000000000000000000000000" +                      // str x3
            "0000000000000000000000000000000000000000" +      // +0xb9e8..+0xb9f8
            "00000000000000000000000000000000" +              // position
            "0000000000000000000000000000803F" +              // orientation (0,0,0,1)
            "000000000000000000000000" +                      // identity u32 x3
            "090000004372616E6265727279" +                    // "Cranberry"
            "000000000000000000000000" +                      // str x3
            "0000000000000000");                              // identity u64
        Assert.Equal(1 + 4 + SelfRecordCodec.MinimalLength + 9, inner.Position);
        Assert.Equal(head, inner.Written.Slice(0, head.Length).ToArray());
        Assert.All(inner.Written.Slice(head.Length).ToArray(), value => Assert.Equal(0, value));

        using var outer = new PacketWriter();
        new GatewayTunnelToClient(Channel: 0, Payload: inner.Written.ToArray()).WriteTo(outer);
        Assert.Equal(0x05, outer.Written[0]);
        Assert.Equal(1 + inner.Position, outer.Position);
    }
}
