using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Cranberry.Zone.Gas;

namespace Cranberry.Zone.Loot;

/// <summary>
/// One spawn marker placed in <c>Z2.zone</c>: exactly the transform the client's own world file
/// carries, plus the two small indices that say what may spawn there and where it is.
/// <para>
/// Deliberately a 24-byte value laid out to match the file byte-for-byte, so the 168,322 markers
/// are one 4.0 MB array rather than 168,322 objects (which would be ~9 MB of heap plus a
/// 1.3 MB pointer array, and would scatter the query's memory access).
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct LootSpawnPoint
{
    /// <summary>File and in-memory size of one record. The loader asserts this.</summary>
    public const int SizeInBytes = 24;

    /// <summary><see cref="AreaIndex"/> for a marker that lies in no named area.</summary>
    public const ushort NoArea = 0xFFFF;

    public readonly float X;

    /// <summary>World height. Y is up, and this is the marker's exact placement height, so ground
    /// loot needs no terrain query (docs/29 §7).</summary>
    public readonly float Y;

    public readonly float Z;

    /// <summary>Marker yaw in radians, the client's own first rotation component.</summary>
    public readonly float Yaw;

    /// <summary>The ZONE instance id. Stable across runs, so it seeds this point's roll.</summary>
    public readonly uint InstanceId;

    /// <summary>Index into <see cref="Z2LootSpawns.Areas"/>, or <see cref="NoArea"/>.</summary>
    public readonly ushort AreaIndex;

    /// <summary>Index into <see cref="Z2LootSpawns.Categories"/>.</summary>
    public readonly byte CategoryIndex;

    /// <summary>Padding that keeps the record at <see cref="SizeInBytes"/>; always 0 in v1.</summary>
    public readonly byte Reserved;

    public Vector3 Position => new(X, Y, Z);

    public bool HasArea => AreaIndex != NoArea;
}

/// <summary>What one spawn point produced: the ids the spawn packets need, at the marker's pose.</summary>
public readonly record struct LootSpawnRoll(
    uint InstanceId,
    int CategoryIndex,
    uint ItemDefinitionId,
    uint GroundModelId,
    uint NameId,
    uint Count,
    Vector3 Position,
    float Yaw);

/// <summary>
/// The Z2 ground-loot spawn points, read from <c>z2-loot-spawns.bin</c> (format <c>CRLP</c> v1,
/// written by <c>tools/data/gen-loot-tables.py</c>; layout in <c>docs/33-z2-loot-placement.md</c>).
///
/// <para><b>Spatial index.</b> The file already stores its 168,322 points sorted by cell of a
/// 128 × 128 grid of 64 m cells over Z2's ±4,096 m terrain, together with the 16,385-entry prefix
/// offset table. So there is no load-time sort, no separate key array, and
/// <see cref="Query(in Vector3, float, Span{int})"/> is pure index arithmetic over contiguous
/// memory: it visits ⌈r/64⌉ rings of cells and touches only the points inside them.</para>
///
/// <para><b>Not <see cref="World.InterestGrid"/>.</b> That grid is a mutable head/next bucket list
/// sized for the few hundred entities that <i>move</i> — its per-key <c>_next</c> array plus
/// pointer-chasing traversal costs an extra 673 KB here and scatters the walk, and its 256 m cell
/// is four times coarser than a loot query wants. Static, never-moving, ~500× more numerous data
/// wants the compressed-row layout above instead; the two are complementary, not duplicates.</para>
/// </summary>
public sealed class Z2LootSpawns
{
    /// <summary>File magic: <c>CRLP</c>, Cranberry Loot Placements.</summary>
    public const uint Magic = 0x50_4C_52_43; // 'C','R','L','P' little-endian

    public const int FormatVersion = 1;

    /// <summary>Default file name inside <see cref="LootDataPaths"/>' directory.</summary>
    public const string DefaultFileName = "z2-loot-spawns.bin";

    private const int HeaderBytes = 64;

    private readonly LootSpawnPoint[] _points;
    private readonly int[] _cellOffsets;
    private readonly string[] _categories;
    private readonly string[] _areas;

    private Z2LootSpawns(
        LootSpawnPoint[] points,
        int[] cellOffsets,
        string[] categories,
        string[] areas,
        int dimension,
        float cellMetres,
        float originX,
        float originZ,
        Vector3 minimum,
        Vector3 maximum)
    {
        _points = points;
        _cellOffsets = cellOffsets;
        _categories = categories;
        _areas = areas;
        Dimension = dimension;
        CellMetres = cellMetres;
        OriginX = originX;
        OriginZ = originZ;
        Minimum = minimum;
        Maximum = maximum;
    }

    public int Count => _points.Length;

    /// <summary>Cells per axis (128).</summary>
    public int Dimension { get; }

    /// <summary>Cell edge in metres (64).</summary>
    public float CellMetres { get; }

    /// <summary>World X of the grid's low edge (−4,096).</summary>
    public float OriginX { get; }

    /// <summary>World Z of the grid's low edge (−4,096).</summary>
    public float OriginZ { get; }

    /// <summary>Axis-aligned bounds of every point, straight from the file header.</summary>
    public Vector3 Minimum { get; }

    public Vector3 Maximum { get; }

    /// <summary>Category names, indexed by <see cref="LootSpawnPoint.CategoryIndex"/>.</summary>
    public ReadOnlySpan<string> Categories => _categories;

    /// <summary>Named <c>Loot.&lt;Poi&gt;</c> areas, indexed by <see cref="LootSpawnPoint.AreaIndex"/>.</summary>
    public ReadOnlySpan<string> Areas => _areas;

    /// <summary>Every point, in cell order.</summary>
    public ReadOnlySpan<LootSpawnPoint> Points => _points;

    public ref readonly LootSpawnPoint this[int index] => ref _points[index];

    /// <summary>Category ordinal for a key such as <c>Gear01</c>, or −1.</summary>
    public int CategoryIndexOf(string key)
    {
        for (int i = 0; i < _categories.Length; i++)
        {
            if (string.Equals(_categories[i], key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Area name for a point, or null when it lies outside every named area.</summary>
    public string? AreaNameOf(in LootSpawnPoint point) =>
        point.AreaIndex == LootSpawnPoint.NoArea ? null : _areas[point.AreaIndex];

    /// <summary>How many points a category holds. Linear; for diagnostics, not for the tick.</summary>
    public int CountOf(int categoryIndex)
    {
        int total = 0;
        foreach (LootSpawnPoint point in _points)
        {
            if (point.CategoryIndex == categoryIndex)
            {
                total++;
            }
        }

        return total;
    }

    /// <summary>
    /// Fills <paramref name="into"/> with the indices of every point within
    /// <paramref name="radius"/> metres of <paramref name="centre"/>, measured horizontally
    /// (X/Z — the marker's own Y is the ground it sits on, so height must not gate the query).
    /// Allocation-free; the caller owns the span.
    /// <para>
    /// Returns how many points matched, which may exceed the span: the span is filled to capacity
    /// and the surplus is still counted, so a caller can size a bigger buffer and ask again. This
    /// is the same contract <see cref="World.InterestGrid.Query"/> uses.
    /// </para>
    /// <para>
    /// <b>The fill order is the grid scan order</b> (z-row-major, then the row's contiguous strip),
    /// which has nothing to do with distance. A caller that truncates the result at a cap therefore
    /// gets a band at one edge of the disc, not the neighbourhood — use
    /// <see cref="QueryNearest(in Vector3, float, Span{int})"/> for that.
    /// </para>
    /// </summary>
    public int Query(in Vector3 centre, float radius, Span<int> into) =>
        Query(centre, radius, categoryMask: uint.MaxValue, into);

    /// <summary>
    /// As <see cref="Query(in Vector3, float, Span{int})"/>, but only points whose category
    /// ordinal has its bit set in <paramref name="categoryMask"/>.
    /// </summary>
    public int Query(in Vector3 centre, float radius, uint categoryMask, Span<int> into) =>
        Query(centre, radius, categoryMask, pointFilter: default, into);

    /// <summary>
    /// As <see cref="Query(in Vector3, float, uint, Span{int})"/>, but a non-empty
    /// <paramref name="pointFilter"/> (one bool per point, indexed the same as
    /// <see cref="Points"/>) additionally restricts the result to the points it marks true.
    /// <see cref="Z2LootLayout"/> passes its live set here so a query counts <i>items</i> rather
    /// than markers.
    /// </summary>
    public int Query(
        in Vector3 centre,
        float radius,
        uint categoryMask,
        ReadOnlySpan<bool> pointFilter,
        Span<int> into)
    {
        if (!float.IsFinite(radius) || radius <= 0f || categoryMask == 0)
        {
            return 0;
        }

        if (!pointFilter.IsEmpty && pointFilter.Length != _points.Length)
        {
            throw new ArgumentException(
                $"the point filter has {pointFilter.Length} entries for {_points.Length} points.",
                nameof(pointFilter));
        }

        int rings = Math.Min((int)MathF.Ceiling(radius / CellMetres), Dimension);
        int centreX = AxisIndex(centre.X, OriginX);
        int centreZ = AxisIndex(centre.Z, OriginZ);

        int minX = Math.Max(0, centreX - rings);
        int maxX = Math.Min(Dimension - 1, centreX + rings);
        int minZ = Math.Max(0, centreZ - rings);
        int maxZ = Math.Min(Dimension - 1, centreZ + rings);

        float radiusSquared = radius * radius;
        int found = 0;

        for (int z = minZ; z <= maxZ; z++)
        {
            int rowBase = z * Dimension;

            // The row's cells are contiguous in the file, so one span covers the whole strip and
            // the inner loop never re-reads the offset table.
            int start = _cellOffsets[rowBase + minX];
            int end = _cellOffsets[rowBase + maxX + 1];

            for (int i = start; i < end; i++)
            {
                ref readonly LootSpawnPoint point = ref _points[i];
                if ((categoryMask & (1u << point.CategoryIndex)) == 0
                    || (!pointFilter.IsEmpty && !pointFilter[i]))
                {
                    continue;
                }

                float dx = point.X - centre.X;
                float dz = point.Z - centre.Z;
                if ((dx * dx) + (dz * dz) > radiusSquared)
                {
                    continue;
                }

                if (found < into.Length)
                {
                    into[found] = i;
                }

                found++;
            }
        }

        return found;
    }

    /// <summary>
    /// As <see cref="Query(in Vector3, float, Span{int})"/>, but fills <paramref name="into"/> with
    /// the <b>nearest</b> matches, closest first, instead of the first ones the cell scan happens to
    /// reach. Returns the total number of points inside the radius, exactly as <c>Query</c> does.
    /// <para>
    /// This is the overload a spawn burst must use. <c>Query</c> fills its span in grid order
    /// (z-row-major, then the row's contiguous strip), so truncating its output at a cap takes the
    /// markers in the lowest-Z cell row of the disc — a band at the far edge. Measured on the
    /// shipped <c>z2-loot-spawns.bin</c> at the Loot.PVResidential.01 drop point (1,441 markers
    /// within 120 m): the first 64 in grid order lie 74.4–119.1 m away (mean 110.2), while the true
    /// nearest 64 are all within 19.9 m. With <c>InteractReplicationData.DefaultRange</c> at 3 m
    /// that is the difference between landing in the loot and landing in an empty field while the
    /// log still reads "spawned 64".
    /// </para>
    /// <para>
    /// Selection is a bounded max-heap of <paramref name="into"/>'s own length, so the cost is
    /// O(matched · log k) with no per-match allocation and no sort of the full match set; the heap's
    /// distance keys come from <see cref="ArrayPool{T}"/> and are returned before it exits.
    /// </para>
    /// </summary>
    public int QueryNearest(in Vector3 centre, float radius, Span<int> into) =>
        QueryNearest(centre, radius, categoryMask: uint.MaxValue, into);

    /// <summary>As <see cref="QueryNearest(in Vector3, float, Span{int})"/>, restricted to the categories in <paramref name="categoryMask"/>.</summary>
    public int QueryNearest(in Vector3 centre, float radius, uint categoryMask, Span<int> into) =>
        QueryNearest(centre, radius, categoryMask, pointFilter: default, into);

    /// <summary>
    /// As <see cref="QueryNearest(in Vector3, float, uint, Span{int})"/>, but a non-empty
    /// <paramref name="pointFilter"/> (one bool per point) restricts the result to the points it
    /// marks true — <see cref="Z2LootLayout.QueryLive"/>'s route to "the nearest <i>items</i>",
    /// which is what a spawn burst wants: filtering after a nearest-marker query would hand back a
    /// quarter of a cap and call it a full one.
    /// </summary>
    public int QueryNearest(
        in Vector3 centre,
        float radius,
        uint categoryMask,
        ReadOnlySpan<bool> pointFilter,
        Span<int> into)
    {
        if (into.IsEmpty)
        {
            return Query(centre, radius, categoryMask, pointFilter, into);
        }

        if (!float.IsFinite(radius) || radius <= 0f || categoryMask == 0)
        {
            return 0;
        }

        if (!pointFilter.IsEmpty && pointFilter.Length != _points.Length)
        {
            throw new ArgumentException(
                $"the point filter has {pointFilter.Length} entries for {_points.Length} points.",
                nameof(pointFilter));
        }

        int rings = Math.Min((int)MathF.Ceiling(radius / CellMetres), Dimension);
        int centreX = AxisIndex(centre.X, OriginX);
        int centreZ = AxisIndex(centre.Z, OriginZ);

        int minX = Math.Max(0, centreX - rings);
        int maxX = Math.Min(Dimension - 1, centreX + rings);
        int minZ = Math.Max(0, centreZ - rings);
        int maxZ = Math.Min(Dimension - 1, centreZ + rings);

        float radiusSquared = radius * radius;
        int found = 0;
        int kept = 0;

        float[] rented = ArrayPool<float>.Shared.Rent(into.Length);
        try
        {
            Span<float> keys = rented.AsSpan(0, into.Length);

            for (int z = minZ; z <= maxZ; z++)
            {
                int rowBase = z * Dimension;
                int start = _cellOffsets[rowBase + minX];
                int end = _cellOffsets[rowBase + maxX + 1];

                for (int i = start; i < end; i++)
                {
                    ref readonly LootSpawnPoint point = ref _points[i];
                    if ((categoryMask & (1u << point.CategoryIndex)) == 0
                        || (!pointFilter.IsEmpty && !pointFilter[i]))
                    {
                        continue;
                    }

                    float dx = point.X - centre.X;
                    float dz = point.Z - centre.Z;
                    float distanceSquared = (dx * dx) + (dz * dz);
                    if (distanceSquared > radiusSquared)
                    {
                        continue;
                    }

                    found++;

                    if (kept < into.Length)
                    {
                        keys[kept] = distanceSquared;
                        into[kept] = i;
                        kept++;
                        SiftUp(keys, into, kept - 1);
                    }
                    else if (distanceSquared < keys[0])
                    {
                        keys[0] = distanceSquared;
                        into[0] = i;
                        SiftDown(keys[..kept], into[..kept], 0);
                    }
                }
            }

            // Heap-sort the kept set into ascending distance: repeatedly move the farthest (the max
            // heap's root) to the end of the shrinking window.
            for (int last = kept - 1; last > 0; last--)
            {
                (keys[0], keys[last]) = (keys[last], keys[0]);
                (into[0], into[last]) = (into[last], into[0]);
                SiftDown(keys[..last], into[..last], 0);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }

        return found;
    }

    private static void SiftUp(Span<float> keys, Span<int> values, int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (keys[parent] >= keys[index])
            {
                return;
            }

            (keys[parent], keys[index]) = (keys[index], keys[parent]);
            (values[parent], values[index]) = (values[index], values[parent]);
            index = parent;
        }
    }

    private static void SiftDown(Span<float> keys, Span<int> values, int index)
    {
        while (true)
        {
            int left = (2 * index) + 1;
            if (left >= keys.Length)
            {
                return;
            }

            int largest = left;
            int right = left + 1;
            if (right < keys.Length && keys[right] > keys[left])
            {
                largest = right;
            }

            if (keys[index] >= keys[largest])
            {
                return;
            }

            (keys[index], keys[largest]) = (keys[largest], keys[index]);
            (values[index], values[largest]) = (values[largest], values[index]);
            index = largest;
        }
    }

    /// <summary>
    /// Rolls the point at <paramref name="index"/> against <paramref name="tables"/>, <b>through
    /// the spawn gate</b>: most markers stay empty, which is the whole of the "too much on the
    /// floor" fix (docs/39 §L1). Deterministic — the same match seed and the same point always
    /// give the same answer — so a run is reproducible and a test can freeze the result.
    /// <para>
    /// Returns false when the marker loses its gate roll, or when its category has no table or an
    /// empty one (<c>FireExtinguisher</c>). Note that this rolls the marker in isolation: the
    /// per-room caps are a whole-map pass and live on <see cref="Z2LootLayout"/>.
    /// </para>
    /// </summary>
    public bool TryRoll(int index, LootTables tables, ulong matchSeed, out LootSpawnRoll roll) =>
        TryRoll(index, tables, matchSeed, LootDensityOptions.Default, out roll);

    /// <summary>
    /// As <see cref="TryRoll(int, LootTables, ulong, out LootSpawnRoll)"/> with an explicit density
    /// model, so a host or a test can override the gate without editing the generated file.
    /// </summary>
    public bool TryRoll(
        int index,
        LootTables tables,
        ulong matchSeed,
        LootDensityOptions density,
        out LootSpawnRoll roll)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(density);

        ref readonly LootSpawnPoint point = ref _points[index];
        string key = _categories[point.CategoryIndex];

        if (!tables.TryGet(key, out LootCategoryTable? table)
            || !PassesGate(matchSeed, point.InstanceId, density.SpawnChanceFor(table))
            || !table.Roll(SeedFor(matchSeed, point.InstanceId), out LootTableEntry entry))
        {
            roll = default;
            return false;
        }

        roll = new LootSpawnRoll(
            InstanceId: point.InstanceId,
            CategoryIndex: point.CategoryIndex,
            ItemDefinitionId: entry.ItemDefinitionId,
            GroundModelId: entry.GroundModelId,
            NameId: entry.NameId,
            Count: entry.Count,
            Position: point.Position,
            Yaw: point.Yaw);
        return true;
    }

    /// <summary>
    /// The gate itself: does this marker spawn anything at all? Drawn from its own sub-stream
    /// (<see cref="GateSeedFor"/>) rather than from the item pick's, so changing the odds inside a
    /// table never re-shuffles which markers are occupied, and vice versa.
    /// </summary>
    public static bool PassesGate(ulong matchSeed, uint instanceId, double spawnChance)
    {
        if (spawnChance <= 0.0)
        {
            return false;
        }

        if (spawnChance >= 1.0)
        {
            return true;
        }

        var random = new GasRandom(GateSeedFor(matchSeed, instanceId));
        return random.NextDouble() < spawnChance;
    }

    /// <summary>
    /// The per-point seed: the match seed mixed with the marker's ZONE instance id. Instance ids
    /// are unique inside <c>Z2.zone</c> and stable across runs, so two matches with different
    /// seeds lay out different loot and the same seed reproduces a map exactly.
    /// </summary>
    public static ulong SeedFor(ulong matchSeed, uint instanceId)
    {
        unchecked
        {
            return (matchSeed * 0x9E37_79B9_7F4A_7C15UL) ^ (instanceId + 0x1656_67B1_9E37_79F9UL);
        }
    }

    /// <summary>
    /// <see cref="SeedFor"/>'s sibling for the spawn gate, salted so the two draws are independent
    /// streams of the same match seed.
    /// </summary>
    public static ulong GateSeedFor(ulong matchSeed, uint instanceId) =>
        SeedFor(matchSeed ^ 0x4B85_1C77_2D3E_A961UL, instanceId);

    /// <summary>
    /// <b>D275's own sub-stream.</b> A third salt of the same match seed, so the laminated-armour
    /// world chance is drawn independently of both the density gate and the item pick: retuning it
    /// changes which vests survive and <i>nothing else</i> about the map. Same argument as
    /// <see cref="GateSeedFor"/>, same shape.
    /// </summary>
    public static ulong ArmourSeedFor(ulong matchSeed, uint instanceId) =>
        SeedFor(matchSeed ^ 0x2E1F_9A44_6B03_C7D5UL, instanceId);

    /// <summary>Loads the placement file shipped beside the server.</summary>
    public static Z2LootSpawns LoadDefault() => Load(LootDataPaths.Require(DefaultFileName));

    public static Z2LootSpawns Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllBytes(path), path);
    }

    /// <summary>Parses an in-memory copy — the form the tests use.</summary>
    public static Z2LootSpawns Parse(ReadOnlySpan<byte> bytes, string origin)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException(
                "The CRLP placement file is little-endian and is read by reinterpretation.");
        }

        if (Unsafe.SizeOf<LootSpawnPoint>() != LootSpawnPoint.SizeInBytes)
        {
            throw new InvalidOperationException(
                $"LootSpawnPoint is {Unsafe.SizeOf<LootSpawnPoint>()} bytes, expected "
                + $"{LootSpawnPoint.SizeInBytes}; the struct no longer matches the file record.");
        }

        if (bytes.Length < HeaderBytes)
        {
            throw new InvalidDataException($"{origin}: {bytes.Length} bytes is shorter than the header.");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (magic != Magic)
        {
            throw new InvalidDataException($"{origin}: magic 0x{magic:x8}, expected 0x{Magic:x8} ('CRLP').");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"{origin}: version {version}, expected {FormatVersion}.");
        }

        int pointCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        int categoryCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        int dimension = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        float cellMetres = BinaryPrimitives.ReadSingleLittleEndian(bytes[20..]);
        float originX = BinaryPrimitives.ReadSingleLittleEndian(bytes[24..]);
        float originZ = BinaryPrimitives.ReadSingleLittleEndian(bytes[28..]);
        int areaCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]);
        int stringBytes = BinaryPrimitives.ReadInt32LittleEndian(bytes[36..]);

        var minimum = new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[40..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[44..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[48..]));
        var maximum = new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[52..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[56..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[60..]));

        if (pointCount < 0 || categoryCount is <= 0 or > 32 || areaCount < 0 || stringBytes < 0)
        {
            throw new InvalidDataException($"{origin}: header counts are out of range.");
        }

        if (dimension is <= 0 or > 4096 || !float.IsFinite(cellMetres) || cellMetres <= 0f)
        {
            throw new InvalidDataException($"{origin}: grid is {dimension} × {cellMetres} m.");
        }

        int cellCount = dimension * dimension;
        long offsetsAt = HeaderBytes + (long)stringBytes;
        long pointsAt = offsetsAt + ((long)(cellCount + 1) * sizeof(int));
        long expected = pointsAt + ((long)pointCount * LootSpawnPoint.SizeInBytes);
        if (bytes.Length != expected)
        {
            throw new InvalidDataException(
                $"{origin}: {bytes.Length} bytes, expected exactly {expected} for {pointCount:N0} "
                + $"points, {cellCount:N0} cells and a {stringBytes}-byte string table.");
        }

        string[] names = ReadStringTable(bytes.Slice(HeaderBytes, stringBytes), categoryCount + areaCount, origin);
        string[] categories = names[..categoryCount];
        string[] areas = names[categoryCount..];

        var cellOffsets = new int[cellCount + 1];
        MemoryMarshal.Cast<byte, int>(bytes.Slice((int)offsetsAt, (cellCount + 1) * sizeof(int)))
            .CopyTo(cellOffsets);

        if (cellOffsets[0] != 0 || cellOffsets[cellCount] != pointCount)
        {
            throw new InvalidDataException(
                $"{origin}: cell offsets run {cellOffsets[0]}..{cellOffsets[cellCount]}, expected "
                + $"0..{pointCount}. The file's points are not sorted into its own grid.");
        }

        for (int i = 1; i <= cellCount; i++)
        {
            if (cellOffsets[i] < cellOffsets[i - 1])
            {
                throw new InvalidDataException($"{origin}: cell offset {i} goes backwards.");
            }
        }

        LootSpawnPoint[] points = MemoryMarshal
            .Cast<byte, LootSpawnPoint>(bytes[(int)pointsAt..])
            .ToArray();

        var spawns = new Z2LootSpawns(
            points, cellOffsets, categories, areas, dimension, cellMetres, originX, originZ, minimum, maximum);

        // The whole query rests on "record i is in the cell whose offset range contains i", so the
        // loader proves it rather than trusting the generator.
        for (int cell = 0; cell < cellCount; cell++)
        {
            for (int i = cellOffsets[cell]; i < cellOffsets[cell + 1]; i++)
            {
                ref readonly LootSpawnPoint point = ref points[i];
                if (spawns.CellOf(point.X, point.Z) != cell)
                {
                    throw new InvalidDataException(
                        $"{origin}: point {i} at ({point.X}, {point.Z}) is filed under cell {cell} "
                        + $"but belongs in {spawns.CellOf(point.X, point.Z)}.");
                }

                if (point.CategoryIndex >= categoryCount
                    || (point.AreaIndex != LootSpawnPoint.NoArea && point.AreaIndex >= areaCount))
                {
                    throw new InvalidDataException($"{origin}: point {i} names an index that does not exist.");
                }
            }
        }

        return spawns;
    }

    /// <summary>Cell index of a world X/Z, clamped at the edges. Y is ignored.</summary>
    public int CellOf(float x, float z) =>
        (AxisIndex(z, OriginZ) * Dimension) + AxisIndex(x, OriginX);

    private int AxisIndex(float metres, float origin)
    {
        if (!float.IsFinite(metres))
        {
            return Dimension / 2;
        }

        int index = (int)MathF.Floor((metres - origin) / CellMetres);
        return Math.Clamp(index, 0, Dimension - 1);
    }

    private static string[] ReadStringTable(ReadOnlySpan<byte> table, int expected, string origin)
    {
        var names = new string[expected];
        int produced = 0;
        int start = 0;

        for (int i = 0; i < table.Length; i++)
        {
            if (table[i] != 0)
            {
                continue;
            }

            if (produced == expected)
            {
                throw new InvalidDataException($"{origin}: string table holds more than {expected} names.");
            }

            names[produced++] = Encoding.UTF8.GetString(table[start..i]);
            start = i + 1;
        }

        if (produced != expected || start != table.Length)
        {
            throw new InvalidDataException(
                $"{origin}: string table holds {produced} names over {table.Length} bytes, expected "
                + $"{expected} names ending on the last byte.");
        }

        return names;
    }
}
