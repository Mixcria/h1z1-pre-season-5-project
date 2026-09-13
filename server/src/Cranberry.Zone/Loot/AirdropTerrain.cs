using System.Buffers.Binary;
using System.IO.Compression;

namespace Cranberry.Zone.Loot;

/// <summary>
/// The playable Z2 terrain surface from the August client's CNK0 v3 heightfield samples.
/// Retains every one-metre sample and its PhysX diagonal flag; queries interpolate the actual
/// triangle rather than averaging nearby loot placements or flattening the map to the viewer.
/// Building roofs, bridge decks and other separate collision meshes are outside this heightfield.
/// Only queried 256m chunks are inflated, with a bounded cache shared by all matches.
/// </summary>
public sealed class AirdropTerrain
{
    public const string DefaultFileName = "z2-terrain.bin";
    private const int ChunkMetres = 256;
    private const int TileMetres = 64;
    private const int TileSamples = 65;
    private const int ChunkSampleBytes = 16 * TileSamples * TileSamples * 4;
    private const int CachedChunks = 48;
    private readonly byte[] _file;
    private readonly Dictionary<(int X, int Z), (int Offset, int Length)> _index;
    private readonly Dictionary<(int X, int Z), byte[]> _cache = [];
    private readonly Queue<(int X, int Z)> _cacheOrder = [];
    private readonly object _cacheLock = new();

    private AirdropTerrain(byte[] file, int minimum, int maximum,
        Dictionary<(int X, int Z), (int Offset, int Length)> index)
    {
        _file = file;
        Minimum = minimum;
        Maximum = maximum;
        _index = index;
    }

    public int Minimum { get; }
    public int Maximum { get; }
    public int ChunkCount => _index.Count;
    public static AirdropTerrain LoadDefault() => Load(LootDataPaths.Require(DefaultFileName));
    public static AirdropTerrain Load(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Validates an indexed terrain file without inflating its full map.</summary>
    public static AirdropTerrain Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 28 || !data.AsSpan(0, 8).SequenceEqual("CBAHT01\0"u8)
            || U32(data, 8) != 1 || U32(data, 20) != ChunkMetres)
        {
            throw new InvalidDataException("Unsupported airdrop terrain header.");
        }

        int minimum = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        int maximum = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16));
        if (minimum < -8192 || maximum > 8192 || maximum <= minimum
            || minimum % ChunkMetres != 0 || maximum % ChunkMetres != 0)
        {
            throw new InvalidDataException("Invalid terrain bounds.");
        }

        int width = (maximum - minimum) / ChunkMetres;
        uint count = U32(data, 24);
        if (count != width * width)
        {
            throw new InvalidDataException("Terrain chunk count does not cover its bounds.");
        }

        var index = new Dictionary<(int X, int Z), (int Offset, int Length)>((int)count);
        int cursor = 28;
        for (int i = 0; i < count; i++)
        {
            if (data.Length - cursor < 12) throw new InvalidDataException("Truncated terrain index.");
            int x = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor));
            int z = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor + 4));
            uint length = U32(data, cursor + 8);
            cursor += 12;
            if (x < minimum || x >= maximum || z < minimum || z >= maximum
                || x % ChunkMetres != 0 || z % ChunkMetres != 0
                || length < 18 || length > data.Length - cursor
                || !index.TryAdd((x, z), (cursor, (int)length)))
            {
                throw new InvalidDataException("Invalid or duplicate terrain chunk.");
            }

            cursor += (int)length;
        }

        if (cursor != data.Length) throw new InvalidDataException("Unexpected terrain trailing bytes.");
        return new AirdropTerrain(data, minimum, maximum, index);
    }

    /// <summary>Returns false for non-finite or out-of-map coordinates.</summary>
    public bool TryGetHeight(float x, float z, out float height)
    {
        height = 0;
        if (!float.IsFinite(x) || !float.IsFinite(z)
            || x < Minimum || x > Maximum || z < Minimum || z > Maximum)
        {
            return false;
        }

        // At the upper edge use the last cell with fraction1, preserving its boundary sample.
        int cellX = Math.Min((int)Math.Floor(x), Maximum - 1);
        int cellZ = Math.Min((int)Math.Floor(z), Maximum - 1);
        int chunkX = (int)Math.Floor(cellX / (double)ChunkMetres) * ChunkMetres;
        int chunkZ = (int)Math.Floor(cellZ / (double)ChunkMetres) * ChunkMetres;
        byte[] samples = GetChunk(chunkX, chunkZ);
        int localX = cellX - chunkX;
        int localZ = cellZ - chunkZ;
        int tile = (localZ / TileMetres) * 4 + localX / TileMetres;
        int sample = tile * TileSamples * TileSamples
            + localX % TileMetres * TileSamples + localZ % TileMetres;
        float h00 = Height(samples, sample);
        float h10 = Height(samples, sample + TileSamples);
        float h01 = Height(samples, sample + 1);
        float h11 = Height(samples, sample + TileSamples + 1);
        height = TriangleHeight(h00, h10, h01, h11, x - cellX, z - cellZ,
            (samples[sample * 4 + 2] & 0x80) != 0);
        return true;
    }

    /// <summary>
    /// Ground height with finite out-of-map coordinates clamped to the terrain edge. This lets
    /// an off-map plane approach sample a conservative boundary altitude; payload placement
    /// should remain in bounds. Heightfield holes retain their geometric surface height here.
    /// </summary>
    public float HeightAt(float x, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z)) throw new ArgumentOutOfRangeException(nameof(x));
        TryGetHeight(Math.Clamp(x, Minimum, Maximum), Math.Clamp(z, Minimum, Maximum), out float height);
        return height;
    }

    /// <summary>PhysX eS16_TM sample diagonal: bit7 of materialIndex0 selects 00-to-11.</summary>
    public static float TriangleHeight(float h00, float h10, float h01, float h11,
        float x, float z, bool diagonal00To11)
    {
        if (diagonal00To11)
        {
            return x >= z
                ? h00 + x * (h10 - h00) + z * (h11 - h10)
                : h00 + z * (h01 - h00) + x * (h11 - h01);
        }

        return x + z <= 1f
            ? h00 + x * (h10 - h00) + z * (h01 - h00)
            : h11 + (1f - x) * (h01 - h11) + (1f - z) * (h10 - h11);
    }

    private byte[] GetChunk(int x, int z)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue((x, z), out byte[]? cached)) return cached;
            (int offset, int length) = _index[(x, z)];
            using var source = new MemoryStream(_file, offset, length, writable: false);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            byte[] samples = new byte[ChunkSampleBytes];
            gzip.ReadExactly(samples);
            if (gzip.ReadByte() != -1) throw new InvalidDataException("Unexpected terrain sample length.");
            if (_cache.Count >= CachedChunks) _cache.Remove(_cacheOrder.Dequeue());
            _cache.Add((x, z), samples);
            _cacheOrder.Enqueue((x, z));
            return samples;
        }
    }

    private static float Height(byte[] samples, int index) =>
        BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(index * 4)) / 32f;

    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
}
