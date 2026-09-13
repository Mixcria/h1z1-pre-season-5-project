using Cranberry.Harness.Wire;

namespace Cranberry.Harness.Tests;

public sealed class WireTests
{
    [Fact]
    public void Writer_and_reader_round_trip_every_primitive()
    {
        var w = new WireWriter();
        w.U8(0x2A).Bool(true).BeU16(0xBEEF).BeU32(0xDEADBEEF)
            .LeU16(0xBEEF).LeU32(0xDEADBEEF).LeU64(0x0123456789ABCDEF).LeF32(1.5f)
            .CountedString("kotkdefault").CString("ClientProtocol_1148").Raw([1, 2, 3]);

        var r = new WireReader(w.Written);
        Assert.Equal(0x2A, r.U8());
        Assert.True(r.Bool());
        Assert.Equal(0xBEEF, r.BeU16());
        Assert.Equal(0xDEADBEEF, r.BeU32());
        Assert.Equal(0xBEEF, r.LeU16());
        Assert.Equal(0xDEADBEEF, r.LeU32());
        Assert.Equal(0x0123456789ABCDEFUL, r.LeU64());
        Assert.Equal(1.5f, r.LeF32());
        Assert.Equal("kotkdefault", r.CountedString());
        Assert.Equal("ClientProtocol_1148", r.CString(32));
        Assert.Equal([1, 2, 3], r.Bytes(3).ToArray());
        Assert.True(r.AtEnd);
    }

    [Fact]
    public void Big_and_little_endian_are_actually_different()
    {
        var w = new WireWriter();
        w.BeU32(0x01020304).LeU32(0x01020304);
        Assert.Equal("0102030404030201", Convert.ToHexString(w.Written));
    }

    [Fact]
    public void Reader_refuses_to_run_past_the_end() =>
        Assert.Throws<WireFormatException>(static () => ReadUInt32(new byte[] { 1, 2 }));

    private static uint ReadUInt32(byte[] buffer)
    {
        var r = new WireReader(buffer);
        return r.LeU32();
    }

    [Fact]
    public void Counted_string_longer_than_the_message_is_rejected_rather_than_truncated() =>
        // u32 count = 1000, then two bytes.
        Assert.Throws<WireFormatException>(
            static () => ReadCountedString([0xE8, 0x03, 0x00, 0x00, 0x41, 0x42]));

    private static string ReadCountedString(byte[] buffer)
    {
        var r = new WireReader(buffer);
        return r.CountedString();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(0xFE)]
    [InlineData(0xFF)]
    [InlineData(0x100)]
    [InlineData(0xFFFE)]
    [InlineData(0xFFFF)]
    [InlineData(0x10000)]
    [InlineData(1 << 20)]
    public void Var_size_round_trips_across_both_width_boundaries(int value)
    {
        var w = new WireWriter();
        SoeVarSize.Write(w, value);
        Assert.Equal(SoeVarSize.SizeOf(value), w.Length);

        Assert.Equal(value, ReadVarSize(w.ToArray()));
    }

    [Fact]
    public void Var_size_read_past_the_end_is_a_format_error_not_a_crash() =>
        Assert.Throws<WireFormatException>(static () => ReadVarSize([0xFF]));

    private static int ReadVarSize(byte[] buffer)
    {
        int offset = 0;
        return SoeVarSize.Read(buffer, ref offset);
    }

    [Fact]
    public void Session_reply_round_trips_against_the_servers_own_encoder()
    {
        // The cross-check that makes the independence real: the server writes it, the harness
        // parses it, and the two agree field for field.
        var settings = new Cranberry.Transport.SessionSettings { CrcSeed = 0x11223344, UdpLength = 512 };
        Span<byte> datagram = stackalloc byte[Cranberry.Transport.SessionReply.Length];
        int written = Cranberry.Transport.SessionReply.Write(datagram, 0xAABBCCDD, settings);

        SoeSessionParameters parsed = SoeSessionParameters.ParseReply(datagram[..written]);
        Assert.Equal(SoeSessionParameters.ReplyLength, written);
        Assert.Equal(0xAABBCCDDu, parsed.SessionId);
        Assert.Equal(0x11223344u, parsed.CrcSeed);
        Assert.Equal(settings.CrcLength, parsed.CrcLength);
        Assert.Equal(settings.Compression, parsed.Compression);
        Assert.Equal(settings.UdpLength, parsed.UdpLength);
        Assert.Equal(settings.ProtocolVersion, parsed.ProtocolVersion);
        Assert.Equal(settings.MaxReliablePayload, parsed.MaxReliablePayload);
    }

    [Fact]
    public void Session_request_the_harness_builds_is_the_one_the_server_parses()
    {
        byte[] request = SoeSessionParameters.BuildRequest(3, 0x221F1231, 512, "LoginUdp_14");
        Cranberry.Transport.SessionRequest parsed =
            Cranberry.Transport.SessionRequest.Parse(request.AsSpan(2));

        Assert.Equal(3u, parsed.CrcLength);
        Assert.Equal(0x221F1231u, parsed.SessionId);
        Assert.Equal(512u, parsed.UdpLength);
        Assert.Equal("LoginUdp_14", parsed.ProtocolName);
    }
}
