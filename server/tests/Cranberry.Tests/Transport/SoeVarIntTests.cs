using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

public class SoeVarIntTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(254, 1)]
    [InlineData(255, 3)]
    [InlineData(256, 3)]
    [InlineData(511, 3)]
    [InlineData(65534, 3)]
    [InlineData(65535, 7)]
    [InlineData(70000, 7)]
    [InlineData(int.MaxValue, 7)]
    public void RoundTripsEveryEncodingWidth(int value, int expectedSize)
    {
        Span<byte> buffer = stackalloc byte[7];
        int written = SoeVarInt.Write(buffer, value);

        Assert.Equal(expectedSize, written);
        Assert.Equal(expectedSize, SoeVarInt.EncodedSize(value));

        int offset = 0;
        int read = SoeVarInt.Read(buffer.Slice(0, written), ref offset);
        Assert.Equal(value, read);
        Assert.Equal(written, offset);
    }

    [Fact]
    public void ThreeByteFormIsMarkerThenBigEndianU16()
    {
        Span<byte> buffer = stackalloc byte[3];
        SoeVarInt.Write(buffer, 0x1234);
        Assert.Equal(new byte[] { 0xFF, 0x12, 0x34 }, buffer.ToArray());
    }

    [Fact]
    public void SevenByteFormIsThreeMarkersThenBigEndianU32()
    {
        Span<byte> buffer = stackalloc byte[7];
        SoeVarInt.Write(buffer, 0x01020304);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0x01, 0x02, 0x03, 0x04 }, buffer.ToArray());
    }

    [Fact]
    public void TruncatedPrefixThrowsProtocolException()
    {
        byte[] truncated = [0xFF, 0x12];
        int offset = 0;
        Assert.Throws<SoeProtocolException>(() => SoeVarInt.Read(truncated, ref offset));
    }
}
