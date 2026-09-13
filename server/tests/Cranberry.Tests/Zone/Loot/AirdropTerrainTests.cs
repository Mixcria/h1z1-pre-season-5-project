using System.Buffers.Binary;
using System.IO.Compression;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

public sealed class AirdropTerrainTests
{
    private static readonly Lazy<AirdropTerrain> Retail = new(AirdropTerrain.LoadDefault);

    [Theory]
    [InlineData(62.3437f, 317.0875f, 33.25f)]
    [InlineData(146.9012f, 309.2652f, 33.25f)]
    [InlineData(-58.2939f, 300.2261f, 33.21875f)]
    [InlineData(-87.3864f, -185.862f, 32.53125f)]
    [InlineData(-4096f, -4096f, 330.09375f)]
    [InlineData(-1.25f, -0.75f, 33.90625f)]
    [InlineData(0f, 0f, 33.90625f)]
    [InlineData(64f, 64f, 34.03125f)]
    [InlineData(256f, 256f, 32.25f)]
    [InlineData(4095f, 4095f, 662.96875f)]
    public void ActualClientHeightSamplesKeepWorldAxesScaleAndNegativeCoordinates(float x, float z, float expected)
    {
        Assert.Equal(1024, Retail.Value.ChunkCount);
        Assert.True(Retail.Value.TryGetHeight(x, z, out float actual));
        Assert.Equal(expected, actual, 5);
    }

    [Fact]
    public void PhysXDiagonalFlagSelectsTheActualTriangle()
    {
        // A nonplanar quad must not use bilinear averaging: 00/10/01 = 0; 11 = 1.
        Assert.Equal(0.25f, AirdropTerrain.TriangleHeight(0, 0, 0, 1, 0.25f, 0.25f, true));
        Assert.Equal(0f, AirdropTerrain.TriangleHeight(0, 0, 0, 1, 0.25f, 0.25f, false));
        Assert.Equal(0.25f, AirdropTerrain.TriangleHeight(0, 0, 0, 1, 0.75f, 0.25f, true));
        Assert.Equal(0.5f, AirdropTerrain.TriangleHeight(0, 0, 0, 1, 0.75f, 0.75f, false));

        var (flatBytes, samples) = OneChunk();
        BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan((65 + 1) * 4), 32);
        AirdropTerrain clearFlag = AirdropTerrain.Parse(Pack(samples));
        samples[2] |= 0x80;
        AirdropTerrain setFlag = AirdropTerrain.Parse(Pack(samples));
        Assert.Equal(0f, clearFlag.HeightAt(0.25f, 0.25f));
        Assert.Equal(0.25f, setFlag.HeightAt(0.25f, 0.25f));
        Assert.NotEmpty(flatBytes);
    }

    [Fact]
    public void TerrainBoundsAreStrictForPayloadsAndClampedForPlaneApproach()
    {
        AirdropTerrain terrain = Retail.Value;
        Assert.False(terrain.TryGetHeight(float.NaN, 0, out _));
        Assert.False(terrain.TryGetHeight(-4096.01f, 0, out _));
        Assert.False(terrain.TryGetHeight(0, 4096.01f, out _));
        Assert.True(terrain.TryGetHeight(4096, 4096, out float edge));
        Assert.Equal(edge, terrain.HeightAt(8000, 8000));
        Assert.Throws<ArgumentOutOfRangeException>(() => terrain.HeightAt(float.PositiveInfinity, 0));
    }

    [Fact]
    public void TruncatedAndDuplicateOrOutOfBoundsChunkIndexesAreRejected()
    {
        var (file, _) = OneChunk();
        Assert.Throws<InvalidDataException>(() => AirdropTerrain.Parse(file[..^1]));
        byte[] badOrigin = (byte[])file.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(badOrigin.AsSpan(28), 1);
        Assert.Throws<InvalidDataException>(() => AirdropTerrain.Parse(badOrigin));
        byte[] badCount = (byte[])file.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(badCount.AsSpan(24), 2);
        Assert.Throws<InvalidDataException>(() => AirdropTerrain.Parse(badCount));
    }

    private static (byte[] File, byte[] Samples) OneChunk()
    {
        byte[] samples = new byte[16 * 65 * 65 * 4];
        return (Pack(samples), samples);
    }

    private static byte[] Pack(byte[] samples)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(samples);
        byte[] payload = compressed.ToArray();
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write("CBAHT01\0"u8);
        writer.Write(1u);
        writer.Write(0);
        writer.Write(256);
        writer.Write(256u);
        writer.Write(1u);
        writer.Write(0);
        writer.Write(0);
        writer.Write((uint)payload.Length);
        writer.Write(payload);
        return output.ToArray();
    }
}
