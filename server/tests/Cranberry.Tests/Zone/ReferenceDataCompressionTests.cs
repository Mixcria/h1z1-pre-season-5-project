using System.Buffers.Binary;
using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class ReferenceDataCompressionTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(4)] [InlineData(12)] [InlineData(13)]
    [InlineData(15)] [InlineData(19)] [InlineData(255)] [InlineData(270)]
    [InlineData(65535)] [InlineData(65536)] [InlineData(65537)] [InlineData(200000)]
    public void BlocksRoundTripAcrossLengthAndWindowBoundaries(int size)
    {
        byte[] input = new byte[size];
        new Random(size).NextBytes(input);
        Assert.Equal(input, Decode(ReferenceDataCompression.Encode(input), input.Length));
        for (int i = 0; i < size; i++) input[i] = (byte)(i % 19);
        Assert.Equal(input, Decode(ReferenceDataCompression.Encode(input), input.Length));
        Array.Clear(input);
        Assert.Equal(input, Decode(ReferenceDataCompression.Encode(input), input.Length));
    }

    [Fact]
    public void EnvelopePreservesTheTableAndUsesDistinctLengthsForCompression()
    {
        byte[] input = Enumerable.Range(0, 100000).Select(i => (byte)(i % 37)).ToArray();
        ReferenceData packet = ReferenceData.CreateCompressed("DynamicAppearanceDefinitions", input);
        using var writer = new PacketWriter();
        packet.WriteTo(writer);
        var reader = new PacketReader(writer.Written);
        Assert.Equal(ReferenceData.Opcode, reader.ReadByte());
        int nameLength = reader.ReadUInt16() & 0x1fff;
        reader.ReadBytes(nameLength + 1);
        Assert.Equal((uint)input.Length, reader.ReadUInt32());
        int compressedLength = reader.ReadInt32();
        Assert.InRange(compressedLength, 1, input.Length / 10);
        Assert.Equal(input, Decode(reader.ReadBytes(compressedLength).ToArray(), input.Length));
        Assert.Equal(input, packet.Payload);
        Assert.Empty(reader.ReadRest().ToArray());
    }

    [Fact]
    public void SmallOrIncompressibleTablesKeepTheRawEnvelope()
    {
        byte[] input = new byte[4096];
        new Random(1229).NextBytes(input);
        AssertRaw(input);
        AssertRaw([0, 0, 0, 0]);
        AssertRaw([]);
    }

    [Fact]
    public void ReplacingAPayloadDoesNotReuseTheCachedCompression()
    {
        var packet = ReferenceData.CreateCompressed("Test", new byte[4096]) with { Payload = [1, 2, 3] };
        using var writer = new PacketWriter();
        using var expected = new PacketWriter();
        packet.WriteTo(writer);
        new ReferenceData("Test", [1, 2, 3]).WriteTo(expected);
        Assert.Equal(expected.Written.ToArray(), writer.Written.ToArray());
    }

    private static void AssertRaw(byte[] input)
    {
        using var actual = new PacketWriter();
        using var expected = new PacketWriter();
        ReferenceData.CreateCompressed("Test", input).WriteTo(actual);
        new ReferenceData("Test", input).WriteTo(expected);
        Assert.Equal(expected.Written.ToArray(), actual.Written.ToArray());
    }

    // A separate strict reader used only by tests. The release is additionally
    // checked by the actual August machine-code decoder and the upstream LZ4 library.
    private static byte[] Decode(byte[] block, int size)
    {
        byte[] result = new byte[size];
        int cursor = 0, written = 0;
        int Length(int value)
        {
            if (value != 15) return value;
            byte more;
            do { more = block[cursor++]; value += more; } while (more == 255);
            return value;
        }
        while (cursor < block.Length)
        {
            byte token = block[cursor++];
            int literals = Length(token >> 4);
            block.AsSpan(cursor, literals).CopyTo(result.AsSpan(written));
            cursor += literals; written += literals;
            if (cursor == block.Length) break;
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(cursor));
            cursor += 2;
            Assert.InRange(offset, 1, Math.Min(65535, written));
            int match = Length(token & 15) + 4;
            Assert.True(size - written >= 12);
            for (int i = 0; i < match; i++) { result[written] = result[written - offset]; written++; }
            Assert.True(size - written >= 5);
        }
        Assert.Equal(block.Length, cursor);
        Assert.Equal(size, written);
        return result;
    }
}
