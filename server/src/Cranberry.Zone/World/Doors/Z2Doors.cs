using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Cranberry.Zone.World.Doors;

/// <summary>
/// One <c>Common_DPO_DoorProxy_*</c> placement from <c>Z2.zone</c>: the invisible marker the
/// client's own world file puts where a door belongs, reduced to the transform and the two small
/// indices the server needs (docs/42 §7).
/// <para>
/// The proxy is <b>not</b> a door. All eight proxy actors carry <c>&lt;Invisible value="1"/&gt;</c>
/// and <c>Models.txt</c> describes every one of them as <i>"DO NOT DELETE!! Used to place objects in
/// the world"</i>; the server spawns a real door NPC at each. Nothing in the client's static world is
/// interactable.
/// </para>
/// <para>
/// Deliberately a 24-byte value laid out to match the file byte-for-byte — the same shape
/// <c>Loot.LootSpawnPoint</c> uses — so the whole door set is one 98 KB array read by
/// reinterpretation, with no per-record object and no load-time parse.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct DoorPlacement
{
    /// <summary>File and in-memory size of one record. The loader asserts this.</summary>
    public const int SizeInBytes = 24;

    /// <summary><see cref="AreaIndex"/> for a door that lies in no named area.</summary>
    public const ushort NoArea = 0xFFFF;

    public readonly float X;

    /// <summary>World height — the proxy's own placement height, so a door needs no terrain query.</summary>
    public readonly float Y;

    public readonly float Z;

    /// <summary>
    /// The proxy's ZONE Euler yaw in radians, and the <b>closed</b> pose of the door.
    /// <para>
    /// The client derives everything else from it: the door controller's constructor reads the
    /// entity's spawn yaw and sets <c>closed = yaw</c>, <c>open = yaw + π/2</c>, taking the hinge
    /// pivot from the model's own bounds (docs/42 §6a). The server has no say in hinge side,
    /// direction or speed, and must <b>not</b> pre-rotate an open door — a door always constructs
    /// itself closed at the yaw it was spawned with.
    /// </para>
    /// </summary>
    public readonly float Yaw;

    /// <summary>
    /// The ZONE instance id — unique across all 4,108 proxies and stable across runs, so it is this
    /// door's identity in a save file, a log line or a test.
    /// </summary>
    public readonly uint InstanceId;

    /// <summary>Index into <see cref="Z2Doors.Areas"/>, or <see cref="NoArea"/>.</summary>
    public readonly ushort AreaIndex;

    /// <summary>Index into <see cref="Z2Doors.Kinds"/>.</summary>
    public readonly byte KindIndex;

    /// <summary>Padding that keeps the record at <see cref="SizeInBytes"/>; always 0 in v1.</summary>
    public readonly byte Reserved;

    public Vector3 Position => new(X, Y, Z);

    public bool HasArea => AreaIndex != NoArea;
}

/// <summary>
/// One of the eight door proxy families: the marker model the world places, the door mesh the
/// server spawns in its stead, and the <c>Doors.txt</c> row that selects the swing sound
/// (docs/42 §7d).
/// </summary>
/// <param name="Name">Proxy suffix, e.g. <c>ResidentialFront</c>.</param>
/// <param name="ProxyModelId"><c>Models.txt</c> row of the invisible marker. Never spawned; carried
/// so a log line can name what the world file actually held.</param>
/// <param name="ModelId"><c>Models.txt</c> row of the real door mesh — this is what
/// <c>AddLightweightNpc</c> carries at <c>+0x40</c>.</param>
/// <param name="DoorTableId"><c>Doors.txt</c> row id, written into <c>AddLightweightNpc</c>'s
/// <c>+0x19c</c>. <b>Any value &gt; 0 makes a working door</b>; the row selects the open/close sound
/// and nothing else, and an id with no row simply plays none (docs/42 §6c).</param>
/// <param name="CollisionModelId">
/// <c>Models.txt</c> row of this family's <b>kinematic collision twin</b>, or 0 when the family has
/// none (docs/55 §1e, CRDR v2).
/// <para>
/// Collision in 1148 is a property of the actor definition and of nothing on the wire: an actor
/// receives a physics body only from its own <c>&lt;CollisionData … createAsKinematic="…"/&gt;</c>
/// element, and the acquire is additionally gated on the actor's "static" bit
/// (<c>actor+0x4e1 &amp; 4</c>, <c>FUN_141fe6ad0</c>). Exactly 33 of the client's 3,406 actor
/// definitions are <c>createAsKinematic="1"</c> — the authoring convention for a collision body
/// that moves at run time — and 29 of them are <c>Common_Props_Doors_*</c>. Every mesh in the
/// <c>Doors_*</c> family <see cref="ModelId"/> names is <c>createAsKinematic="0"
/// useBoundingBox="0"</c>, i.e. static level geometry, which is the leading explanation for the
/// owner's "doors did spawn but I am able to walk through them".
/// </para>
/// <para>
/// This is <b>not</b> a second entity: it is the model id the spawn packet carries instead of
/// <see cref="ModelId"/> when <see cref="MatchDoorOptions.CollisionMode"/> asks for it. See
/// <see cref="DoorCollision"/> for the selection rule and its cost.
/// </para>
/// </param>
public readonly record struct DoorKind(
    string Name,
    uint ProxyModelId,
    uint ModelId,
    uint DoorTableId,
    uint CollisionModelId = 0)
{
    /// <summary>Whether this family has a kinematic twin the client can collide with.</summary>
    public bool HasCollisionModel => CollisionModelId != 0;
}

/// <summary>
/// The Z2 door set, read from <c>z2-doors.bin</c> (format <c>CRDR</c> v1, written by
/// <c>tools/data/gen-doors.py</c>; derivation in <c>docs/42-doors.md</c>).
/// <para>
/// 4,147 playable doors — every invisible door-placement marker in the client's own
/// <c>Z2.zone</c>: the eight <c>Common_DPO_DoorProxy_*</c> families and the three
/// <c>Hospital_*_Placer</c> families, which carry the identical <c>Models.txt</c> marker text
/// (docs/114 gaps 1 and 2; before 2026-09-03 this was 4,103 — the three hospital families were
/// missing and the five doors of the pre-match LOBBY were dropped as "staging"). The file stores them
/// sorted into a 128 × 128 grid of 64 m cells over Z2's ±4,096 m terrain together with the
/// 16,385-entry prefix offset table, exactly as <c>z2-loot-spawns.bin</c> does, so there is no
/// load-time sort and <see cref="Query"/> is index arithmetic over contiguous memory.
/// </para>
/// </summary>
public sealed class Z2Doors
{
    /// <summary>File magic: <c>CRDR</c>, Cranberry Doors.</summary>
    public const uint Magic = 0x52_44_52_43; // 'C','R','D','R' little-endian

    /// <summary>
    /// The version this build writes and prefers. <b>v2</b> adds the per-kind collision-twin model
    /// id (docs/55): the kind record grew from 12 to 16 bytes and nothing else changed.
    /// </summary>
    public const int FormatVersion = 2;

    /// <summary>
    /// The oldest version this build still reads. A v1 file loads with every
    /// <see cref="DoorKind.CollisionModelId"/> at 0, which
    /// <see cref="DoorCollision.SpawnModelFor"/> resolves back to the visual mesh — i.e. exactly
    /// the wave-4 behaviour, no crash and no silent model swap.
    /// </summary>
    public const int MinimumFormatVersion = 1;

    /// <summary>Default file name inside <see cref="DoorDataPaths"/>' directory.</summary>
    public const string DefaultFileName = "z2-doors.bin";

    /// <summary>
    /// Playable door count in the shipped Z2 set (docs/42 §7b, docs/114 §1–§2): <b>every</b> one of
    /// the 4,147 markers, with nothing dropped.
    /// <para>
    /// It was 4,103 until 2026-09-03. The 44 that were missing: the 39 <c>Hospital_*_Placer</c>
    /// markers, which were simply not in <c>gen-doors.py</c>'s family list, and the five the
    /// <c>--max-height 400</c> filter discarded as an "off-map lobby set piece" — which are the
    /// pre-match lobby's OWN doors, and are exactly the five the friend's live server spawns in the
    /// owner's 2026-08-22 admin capture (position to 0.070 m, yaw to the last decimal, on all five).
    /// </para>
    /// </summary>
    public const int Z2PlayableDoorCount = 4147;

    /// <summary>
    /// How many of <see cref="Z2PlayableDoorCount"/> stand in the pre-match lobby compound — the
    /// five <c>Industrial</c> proxies at y ≈ 506 near <c>ZoneOptions.StagingSpawn</c>.
    /// </summary>
    public const int Z2LobbyDoorCount = 5;

    /// <summary>How many are the three hospital families (25 + 10 + 4).</summary>
    public const int Z2HospitalDoorCount = 39;

    private const int HeaderBytes = 64;

    /// <summary>Kind record in CRDR v1: proxy model, door model, <c>Doors.txt</c> row.</summary>
    private const int KindRecordBytesV1 = 12;

    /// <summary>CRDR v2 appends the collision-twin model id.</summary>
    private const int KindRecordBytesV2 = 16;

    private readonly DoorPlacement[] _doors;
    private readonly int[] _cellOffsets;
    private readonly DoorKind[] _kinds;
    private readonly string[] _areas;

    private Z2Doors(
        DoorPlacement[] doors,
        int[] cellOffsets,
        DoorKind[] kinds,
        string[] areas,
        int dimension,
        float cellMetres,
        float originX,
        float originZ,
        Vector3 minimum,
        Vector3 maximum)
    {
        _doors = doors;
        _cellOffsets = cellOffsets;
        _kinds = kinds;
        _areas = areas;
        Dimension = dimension;
        CellMetres = cellMetres;
        OriginX = originX;
        OriginZ = originZ;
        Minimum = minimum;
        Maximum = maximum;
    }

    public int Count => _doors.Length;

    /// <summary>Cells per axis (128).</summary>
    public int Dimension { get; }

    /// <summary>Cell edge in metres (64).</summary>
    public float CellMetres { get; }

    /// <summary>World X of the grid's low edge (−4,096).</summary>
    public float OriginX { get; }

    /// <summary>World Z of the grid's low edge (−4,096).</summary>
    public float OriginZ { get; }

    /// <summary>Axis-aligned bounds of every door, straight from the file header.</summary>
    public Vector3 Minimum { get; }

    public Vector3 Maximum { get; }

    /// <summary>The eight proxy families, indexed by <see cref="DoorPlacement.KindIndex"/>.</summary>
    public ReadOnlySpan<DoorKind> Kinds => _kinds;

    /// <summary>Named areas, indexed by <see cref="DoorPlacement.AreaIndex"/>.</summary>
    public ReadOnlySpan<string> Areas => _areas;

    /// <summary>Every door, in cell order.</summary>
    public ReadOnlySpan<DoorPlacement> Doors => _doors;

    public ref readonly DoorPlacement this[int index] => ref _doors[index];

    /// <summary>The family a door belongs to.</summary>
    public ref readonly DoorKind KindOf(in DoorPlacement door) => ref _kinds[door.KindIndex];

    /// <summary>Area name for a door, or null when it lies outside every named area.</summary>
    public string? AreaNameOf(in DoorPlacement door) =>
        door.AreaIndex == DoorPlacement.NoArea ? null : _areas[door.AreaIndex];

    /// <summary>Index of the family with that name, or −1.</summary>
    public int KindIndexOf(string name)
    {
        for (int i = 0; i < _kinds.Length; i++)
        {
            if (string.Equals(_kinds[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Index of the door with that ZONE instance id, or −1. Linear; for tests and tools.</summary>
    public int IndexOfInstance(uint instanceId)
    {
        for (int i = 0; i < _doors.Length; i++)
        {
            if (_doors[i].InstanceId == instanceId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Fills <paramref name="into"/> with the indices of every door within <paramref name="radius"/>
    /// metres of <paramref name="centre"/>, measured horizontally (X/Z — a door's own Y is the floor
    /// it stands on, and a two-storey house would otherwise drop its upstairs doors). Allocation-free.
    /// <para>
    /// Returns how many doors matched, which may exceed the span: the span is filled to capacity and
    /// the surplus is still counted, so a caller can size a bigger buffer and ask again — the same
    /// contract <see cref="InterestGrid.Query"/> and <c>Z2LootSpawns.Query</c> use. The fill order is
    /// the grid scan order, which has nothing to do with distance; use <see cref="QueryNearest"/>
    /// when a cap will truncate the result.
    /// </para>
    /// </summary>
    public int Query(in Vector3 centre, float radius, Span<int> into)
    {
        if (!float.IsFinite(radius) || radius <= 0f)
        {
            return 0;
        }

        GetWindow(centre, radius, out int minX, out int maxX, out int minZ, out int maxZ);
        float radiusSquared = radius * radius;
        int found = 0;

        for (int z = minZ; z <= maxZ; z++)
        {
            int rowBase = z * Dimension;

            // The row's cells are contiguous in the file, so one span covers the whole strip and the
            // inner loop never re-reads the offset table.
            int start = _cellOffsets[rowBase + minX];
            int end = _cellOffsets[rowBase + maxX + 1];

            for (int i = start; i < end; i++)
            {
                if (DistanceSquared(i, centre) > radiusSquared)
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
    /// As <see cref="Query"/>, but fills <paramref name="into"/> with the <b>nearest</b> doors,
    /// closest first. Returns the total inside the radius, exactly as <see cref="Query"/> does.
    /// <para>
    /// This is the overload a streaming burst must use: <see cref="Query"/> fills in grid-scan order,
    /// so truncating it at a cap keeps a band at one edge of the disc rather than the neighbourhood
    /// — for loot that was the difference between landing in the items and landing in an empty field,
    /// and for doors it would stream in a row of doors 90 m away instead of the house in front of the
    /// player. Selection is a bounded max-heap of <paramref name="into"/>'s own length, so the cost is
    /// O(matched · log k) with no per-match allocation.
    /// </para>
    /// </summary>
    public int QueryNearest(in Vector3 centre, float radius, Span<int> into)
    {
        if (into.IsEmpty)
        {
            return Query(centre, radius, into);
        }

        if (!float.IsFinite(radius) || radius <= 0f)
        {
            return 0;
        }

        GetWindow(centre, radius, out int minX, out int maxX, out int minZ, out int maxZ);
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
                    float distanceSquared = DistanceSquared(i, centre);
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

    /// <summary>
    /// The single door nearest <paramref name="centre"/> within <paramref name="radius"/> metres —
    /// how a toggle request whose guid the server does not recognise is resolved to a door, and how
    /// a test names "the door in front of me" without knowing its instance id.
    /// </summary>
    public bool TryFindNearest(in Vector3 centre, float radius, out int index, out float distance)
    {
        index = -1;
        distance = float.PositiveInfinity;

        if (!float.IsFinite(radius) || radius <= 0f)
        {
            return false;
        }

        GetWindow(centre, radius, out int minX, out int maxX, out int minZ, out int maxZ);
        float best = radius * radius;

        for (int z = minZ; z <= maxZ; z++)
        {
            int rowBase = z * Dimension;
            int start = _cellOffsets[rowBase + minX];
            int end = _cellOffsets[rowBase + maxX + 1];

            for (int i = start; i < end; i++)
            {
                float distanceSquared = DistanceSquared(i, centre);
                if (distanceSquared <= best)
                {
                    best = distanceSquared;
                    index = i;
                }
            }
        }

        if (index < 0)
        {
            return false;
        }

        distance = MathF.Sqrt(best);
        return true;
    }

    /// <summary>Cell index of a world X/Z, clamped at the edges. Y is ignored.</summary>
    public int CellOf(float x, float z) => (AxisIndex(z, OriginZ) * Dimension) + AxisIndex(x, OriginX);

    /// <summary>Loads the door file shipped beside the server.</summary>
    public static Z2Doors LoadDefault() => Load(DoorDataPaths.Require(DefaultFileName));

    public static Z2Doors Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllBytes(path), path);
    }

    /// <summary>Parses an in-memory copy — the form the tests use.</summary>
    public static Z2Doors Parse(ReadOnlySpan<byte> bytes, string origin)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException(
                "The CRDR door file is little-endian and is read by reinterpretation.");
        }

        if (Unsafe.SizeOf<DoorPlacement>() != DoorPlacement.SizeInBytes)
        {
            throw new InvalidOperationException(
                $"DoorPlacement is {Unsafe.SizeOf<DoorPlacement>()} bytes, expected "
                + $"{DoorPlacement.SizeInBytes}; the struct no longer matches the file record.");
        }

        if (bytes.Length < HeaderBytes)
        {
            throw new InvalidDataException($"{origin}: {bytes.Length} bytes is shorter than the header.");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (magic != Magic)
        {
            throw new InvalidDataException($"{origin}: magic 0x{magic:x8}, expected 0x{Magic:x8} ('CRDR').");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        if (version is < MinimumFormatVersion or > FormatVersion)
        {
            throw new InvalidDataException(
                $"{origin}: version {version}, expected {MinimumFormatVersion}..{FormatVersion}.");
        }

        int kindRecordBytes = version >= 2 ? KindRecordBytesV2 : KindRecordBytesV1;

        int doorCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        int kindCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
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

        if (doorCount < 0 || kindCount is <= 0 or > 256 || areaCount is < 0 or >= DoorPlacement.NoArea
            || stringBytes < 0)
        {
            throw new InvalidDataException($"{origin}: header counts are out of range.");
        }

        if (dimension is <= 0 or > 4096 || !float.IsFinite(cellMetres) || cellMetres <= 0f)
        {
            throw new InvalidDataException($"{origin}: grid is {dimension} × {cellMetres} m.");
        }

        int cellCount = dimension * dimension;
        long kindsAt = HeaderBytes + (long)stringBytes;
        long offsetsAt = kindsAt + ((long)kindCount * kindRecordBytes);
        long doorsAt = offsetsAt + ((long)(cellCount + 1) * sizeof(int));
        long expected = doorsAt + ((long)doorCount * DoorPlacement.SizeInBytes);
        if (bytes.Length != expected)
        {
            throw new InvalidDataException(
                $"{origin}: {bytes.Length} bytes, expected exactly {expected} for {doorCount:N0} "
                + $"doors, {kindCount} kinds, {cellCount:N0} cells and a {stringBytes}-byte string table.");
        }

        string[] names = ReadStringTable(bytes.Slice(HeaderBytes, stringBytes), kindCount + areaCount, origin);
        string[] areas = names[kindCount..];

        var kinds = new DoorKind[kindCount];
        for (int i = 0; i < kindCount; i++)
        {
            ReadOnlySpan<byte> record = bytes.Slice((int)kindsAt + (i * kindRecordBytes), kindRecordBytes);
            kinds[i] = new DoorKind(
                names[i],
                BinaryPrimitives.ReadUInt32LittleEndian(record),
                BinaryPrimitives.ReadUInt32LittleEndian(record[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[8..]),
                version >= 2 ? BinaryPrimitives.ReadUInt32LittleEndian(record[12..]) : 0u);

            if (kinds[i].ModelId == 0)
            {
                throw new InvalidDataException($"{origin}: kind '{kinds[i].Name}' has no door model id.");
            }

            if (kinds[i].DoorTableId == 0)
            {
                // +0x19c == 0 means "not a door" to the client: the entity would spawn as inert
                // scenery with no controller and no [F] prompt (docs/42 §2b). A zero here is a
                // generator bug that would show up live as "the door is there but does nothing".
                throw new InvalidDataException(
                    $"{origin}: kind '{kinds[i].Name}' has door id 0, which the client reads as "
                    + "'not a door'.");
            }

            if (kinds[i].CollisionModelId == kinds[i].ModelId && kinds[i].CollisionModelId != 0)
            {
                // A twin equal to the visual mesh means the generator resolved the same name twice.
                // The whole point of the twin is that it is a DIFFERENT actor — a kinematic one —
                // so an identity mapping is a generator bug that would look live like "the fix did
                // nothing" (docs/55 §1e).
                throw new InvalidDataException(
                    $"{origin}: kind '{kinds[i].Name}' names model {kinds[i].ModelId} as its own "
                    + "collision twin; the twin must be a different, kinematic actor.");
            }
        }

        var cellOffsets = new int[cellCount + 1];
        MemoryMarshal.Cast<byte, int>(bytes.Slice((int)offsetsAt, (cellCount + 1) * sizeof(int)))
            .CopyTo(cellOffsets);

        if (cellOffsets[0] != 0 || cellOffsets[cellCount] != doorCount)
        {
            throw new InvalidDataException(
                $"{origin}: cell offsets run {cellOffsets[0]}..{cellOffsets[cellCount]}, expected "
                + $"0..{doorCount}. The file's doors are not sorted into its own grid.");
        }

        for (int i = 1; i <= cellCount; i++)
        {
            if (cellOffsets[i] < cellOffsets[i - 1])
            {
                throw new InvalidDataException($"{origin}: cell offset {i} goes backwards.");
            }
        }

        DoorPlacement[] doors = MemoryMarshal.Cast<byte, DoorPlacement>(bytes[(int)doorsAt..]).ToArray();

        var set = new Z2Doors(
            doors, cellOffsets, kinds, areas, dimension, cellMetres, originX, originZ, minimum, maximum);

        // The whole query rests on "record i is in the cell whose offset range contains i", so the
        // loader proves it rather than trusting the generator.
        for (int cell = 0; cell < cellCount; cell++)
        {
            for (int i = cellOffsets[cell]; i < cellOffsets[cell + 1]; i++)
            {
                ref readonly DoorPlacement door = ref doors[i];
                if (set.CellOf(door.X, door.Z) != cell)
                {
                    throw new InvalidDataException(
                        $"{origin}: door {i} at ({door.X}, {door.Z}) is filed under cell {cell} "
                        + $"but belongs in {set.CellOf(door.X, door.Z)}.");
                }

                if (door.KindIndex >= kindCount
                    || (door.AreaIndex != DoorPlacement.NoArea && door.AreaIndex >= areaCount))
                {
                    throw new InvalidDataException($"{origin}: door {i} names an index that does not exist.");
                }
            }
        }

        return set;
    }

    private void GetWindow(in Vector3 centre, float radius, out int minX, out int maxX, out int minZ, out int maxZ)
    {
        int rings = Math.Min((int)MathF.Ceiling(radius / CellMetres), Dimension);
        int centreX = AxisIndex(centre.X, OriginX);
        int centreZ = AxisIndex(centre.Z, OriginZ);

        minX = Math.Max(0, centreX - rings);
        maxX = Math.Min(Dimension - 1, centreX + rings);
        minZ = Math.Max(0, centreZ - rings);
        maxZ = Math.Min(Dimension - 1, centreZ + rings);
    }

    private float DistanceSquared(int index, in Vector3 centre)
    {
        ref readonly DoorPlacement door = ref _doors[index];
        float dx = door.X - centre.X;
        float dz = door.Z - centre.Z;
        return (dx * dx) + (dz * dz);
    }

    private int AxisIndex(float metres, float origin)
    {
        if (!float.IsFinite(metres))
        {
            return Dimension / 2;
        }

        int index = (int)MathF.Floor((metres - origin) / CellMetres);
        return Math.Clamp(index, 0, Dimension - 1);
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
