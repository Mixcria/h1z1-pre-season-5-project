using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Cranberry.Zone.Loot;

namespace Cranberry.Zone.Match;

/// <summary>
/// One named place a match may drop over — a <c>Loot.&lt;Place&gt;</c> group of the client's own
/// <c>Z2.zone</c> loot areas, with the one point inside it that has the most loot around it.
/// </summary>
public sealed class DropPoi
{
    private readonly int[] _markers;

    internal DropPoi(
        string area,
        string displayName,
        int boxCount,
        int[] markers,
        Vector3 anchor,
        int anchorNeighbours,
        float edgeMetres)
    {
        Area = area;
        DisplayName = displayName;
        BoxCount = boxCount;
        _markers = markers;
        Anchor = anchor;
        AnchorNeighbours = anchorNeighbours;
        EdgeMetres = edgeMetres;
    }

    /// <summary>The client's own name with the <c>Loot.</c> prefix and the <c>.NN</c> box suffix removed — <c>RubyLakeCampgroundEast</c>.</summary>
    public string Area { get; }

    /// <summary>The camel-case split of <see cref="Area"/> — <c>Ruby Lake Campground East</c>. Log and HUD text only.</summary>
    public string DisplayName { get; }

    /// <summary>How many <c>Loot.&lt;Place&gt;.NN</c> volumes collapsed into this place.</summary>
    public int BoxCount { get; }

    /// <summary>Indices into <see cref="Z2LootSpawns.Points"/>, ascending.</summary>
    public ReadOnlySpan<int> Markers => _markers;

    public int MarkerCount => _markers.Length;

    /// <summary>
    /// The place's densest marker: the one with the most markers of any area within
    /// <see cref="Z2DropPois.LootRadius"/>. <b>Not the centroid</b> — the centroids of
    /// <c>JayWildernessCamp</c> and <c>TakeetaFarms</c> have zero markers within 80 m because both
    /// are three boxes hundreds of metres apart, and the wave-2 fixed drop was a centroid with 376
    /// neighbours where its own place allowed 1,245 (docs/48 §4.3).
    /// </summary>
    public Vector3 Anchor { get; }

    /// <summary>Markers within <see cref="Z2DropPois.LootRadius"/> of <see cref="Anchor"/>, counting every marker on the map.</summary>
    public int AnchorNeighbours { get; }

    /// <summary>Distance from <see cref="Anchor"/> to the nearest terrain edge.</summary>
    public float EdgeMetres { get; }

    public override string ToString() =>
        $"{Area} ({MarkerCount} markers, {AnchorNeighbours} at the anchor, {EdgeMetres:F0} m from the edge)";
}

/// <summary>
/// <b>The map's droppable places, read out of a file the server already loads.</b> docs/48 §4.
///
/// <para><c>z2-loot-spawns.bin</c> already stores a <c>ushort AreaIndex</c> on every one of its
/// 168,322 markers, resolving to 126 <c>Loot.&lt;Place&gt;.NN</c> volumes that collapse to 92
/// distinct named places holding 144,365 markers. So a random drop over a real, nameable location
/// needs <b>no new data file, no new generator and no new loader</b> — only this view, and the
/// place name it carries is exactly the string the host log wants to print.</para>
///
/// <para><b>Why the densest marker and not the centroid.</b> See <see cref="DropPoi.Anchor"/>. The
/// anchor pass is the only real work here: for every one of the 144,365 area-tagged markers it
/// counts the markers within <see cref="LootRadius"/> and keeps the winner per place. It runs
/// against a private <see cref="FineCellDivisor"/>-of-a-radius grid rather than
/// <see cref="Z2LootSpawns.Query"/>'s 64 m cells, because the coarse grid scans a 320 × 320 m block
/// for an 80 m disc (5.1x the area) and the fine one scans 180 × 180 m (1.6x) — the same answer for
/// a third of the work. It is one pass, once per process, so it belongs behind a
/// <see cref="Lazy{T}"/> beside the loot layout and never on a match's critical path.</para>
///
/// <para>Everything here is a pure function of the shipped data — no seed, no session, no clock —
/// which is why it can be cached for the life of the process and pinned by a test.</para>
/// </summary>
public sealed class Z2DropPois
{
    /// <summary>The anchor pass's grid cell is <see cref="DropOptions.LootRadius"/> over this.</summary>
    public const int FineCellDivisor = 4;

    private static readonly ConditionalWeakTable<Z2LootSpawns, Z2DropPois> Cache = new();

    /// <summary>
    /// Serialises the anchor pass. Without it two threads that ask at the same moment both build,
    /// and one of them walks away with a view that is not the cached one — a hundreds-of-milliseconds
    /// duplicate at boot, and a surprise for anyone comparing places by reference.
    /// </summary>
    private static readonly Lock CacheGate = new();

    private readonly DropPoi[] _places;
    private readonly Dictionary<string, DropPoi> _byArea;

    private Z2DropPois(Z2LootSpawns spawns, DropPoi[] places, float lootRadius, float mapHalfExtent)
    {
        Spawns = spawns;
        _places = places;
        LootRadius = lootRadius;
        MapHalfExtentMetres = mapHalfExtent;
        _byArea = new Dictionary<string, DropPoi>(places.Length, StringComparer.Ordinal);
        foreach (DropPoi place in places)
        {
            _byArea[place.Area] = place;
        }
    }

    /// <summary>The markers these places were distilled from; the planner queries it for the jitter test.</summary>
    public Z2LootSpawns Spawns { get; }

    /// <summary>The radius every <see cref="DropPoi.Anchor"/> was chosen for.</summary>
    public float LootRadius { get; }

    /// <summary>Half-extent of the terrain the edge distances were measured against.</summary>
    public float MapHalfExtentMetres { get; }

    /// <summary>The 92 places, ordered by <see cref="DropPoi.Area"/> (ordinal) so the order is the data's, not the file layout's.</summary>
    public IReadOnlyList<DropPoi> Places => _places;

    public int Count => _places.Length;

    public DropPoi this[int index] => _places[index];

    public DropPoi? Find(string area) =>
        area is not null && _byArea.TryGetValue(area, out DropPoi? place) ? place : null;

    /// <summary>Markers within <paramref name="radius"/> of a position, counting every marker on the map. Allocation-free.</summary>
    public int CountWithin(in Vector3 position, float radius) =>
        Spawns.Query(position, radius, []);

    /// <summary>
    /// The process-wide view over <paramref name="spawns"/>. Cached against the spawn set itself, so
    /// a host that shares one <see cref="Z2LootSpawns"/> (as <c>ZoneService</c> does through
    /// <c>Z2LootLayout.Spawns</c>) pays the anchor pass once. A call whose options ask for a
    /// different radius or map extent builds a fresh, uncached view rather than handing back
    /// anchors chosen for someone else's radius.
    /// </summary>
    public static Z2DropPois For(Z2LootSpawns spawns, DropOptions options)
    {
        ArgumentNullException.ThrowIfNull(spawns);
        ArgumentNullException.ThrowIfNull(options);

        lock (CacheGate)
        {
            if (Cache.TryGetValue(spawns, out Z2DropPois? cached)
                && cached.LootRadius == options.LootRadius
                && cached.MapHalfExtentMetres == options.MapHalfExtentMetres)
            {
                return cached;
            }

            Z2DropPois built = Build(spawns, options);
            Cache.AddOrUpdate(spawns, built);
            return built;
        }
    }

    /// <summary>Builds the view unconditionally. <see cref="For"/> is what production should call.</summary>
    public static Z2DropPois Build(Z2LootSpawns spawns, DropOptions options)
    {
        ArgumentNullException.ThrowIfNull(spawns);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        ReadOnlySpan<LootSpawnPoint> points = spawns.Points;
        ReadOnlySpan<string> areas = spawns.Areas;

        // 1. Collapse the 126 Loot.<Place>.NN volumes to their places.
        var placeOfArea = new int[areas.Length];
        var keys = new List<string>();
        var keyIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var boxes = new List<int>();
        for (int i = 0; i < areas.Length; i++)
        {
            string key = PlaceKeyOf(areas[i]);
            if (!keyIndex.TryGetValue(key, out int index))
            {
                index = keys.Count;
                keyIndex.Add(key, index);
                keys.Add(key);
                boxes.Add(0);
            }

            placeOfArea[i] = index;
            boxes[index]++;
        }

        int placeCount = keys.Count;
        var markerLists = new List<int>[placeCount];
        for (int i = 0; i < placeCount; i++)
        {
            markerLists[i] = [];
        }

        for (int i = 0; i < points.Length; i++)
        {
            ushort area = points[i].AreaIndex;
            if (area != LootSpawnPoint.NoArea && area < placeOfArea.Length)
            {
                markerLists[placeOfArea[area]].Add(i);
            }
        }

        // 2. The anchor pass, over a grid sized for the query rather than for the file.
        var grid = new FineGrid(points, options.LootRadius, options.MapHalfExtentMetres);

        var built = new List<DropPoi>(placeCount);
        for (int place = 0; place < placeCount; place++)
        {
            int[] markers = [.. markerLists[place]];
            if (markers.Length == 0)
            {
                // A named area with no marker cannot be dropped on; it is also impossible in the
                // shipped file (all 126 are referenced), so this is a data-change guard.
                continue;
            }

            int bestIndex = markers[0];
            int bestCount = -1;
            foreach (int marker in markers)
            {
                // Ties keep the lowest marker index, so the anchor is a function of the file and
                // not of the iteration order a future refactor happens to produce.
                int count = grid.CountWithin(points[marker].Position, options.LootRadius);
                if (count > bestCount)
                {
                    bestCount = count;
                    bestIndex = marker;
                }
            }

            Vector3 anchor = points[bestIndex].Position;
            float edge = options.MapHalfExtentMetres - MathF.Max(MathF.Abs(anchor.X), MathF.Abs(anchor.Z));

            built.Add(new DropPoi(
                keys[place],
                DisplayNameFor(keys[place]),
                boxes[place],
                markers,
                anchor,
                bestCount,
                edge));
        }

        DropPoi[] places = [.. built.OrderBy(static p => p.Area, StringComparer.Ordinal)];
        return new Z2DropPois(spawns, places, options.LootRadius, options.MapHalfExtentMetres);
    }

    /// <summary><c>Loot.PVResidential.01</c> becomes <c>PVResidential</c>. Anything unrecognised is returned unchanged.</summary>
    public static string PlaceKeyOf(string areaName)
    {
        ArgumentNullException.ThrowIfNull(areaName);

        ReadOnlySpan<char> span = areaName;
        if (span.StartsWith("Loot.", StringComparison.Ordinal))
        {
            span = span[5..];
        }

        int dot = span.LastIndexOf('.');
        if (dot > 0 && dot < span.Length - 1)
        {
            bool allDigits = true;
            for (int i = dot + 1; i < span.Length; i++)
            {
                if (!char.IsAsciiDigit(span[i]))
                {
                    allDigits = false;
                    break;
                }
            }

            if (allDigits)
            {
                span = span[..dot];
            }
        }

        return span.ToString();
    }

    /// <summary>
    /// <c>RubyLakeCampgroundEast</c> becomes <c>Ruby Lake Campground East</c>, <c>PVResidential</c>
    /// becomes <c>PV Residential</c>, <c>CWPUtilitiesCompound192</c> becomes
    /// <c>CWP Utilities Compound 192</c>. A run of capitals is only split before its last letter
    /// when the run is at least two long, so <c>JTsPassAndGas</c> stays <c>JTs Pass And Gas</c>
    /// rather than becoming <c>J Ts Pass And Gas</c>. Display only — <see cref="DropPoi.Area"/>
    /// stays the client's own spelling, including its two oddities <c>Unnamed</c> and
    /// <c>UnnamedStorageYard</c>.
    /// </summary>
    public static string DisplayNameFor(string area)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (area.Length == 0)
        {
            return area;
        }

        var text = new StringBuilder(area.Length + 8);
        for (int i = 0; i < area.Length; i++)
        {
            if (i > 0 && NeedsSpaceBefore(area, i))
            {
                text.Append(' ');
            }

            text.Append(area[i]);
        }

        return text.ToString();
    }

    private static bool NeedsSpaceBefore(string area, int i)
    {
        char current = area[i];
        char previous = area[i - 1];

        if (char.IsAsciiDigit(current) != char.IsAsciiDigit(previous))
        {
            return true;
        }

        if (!char.IsAsciiLetterUpper(current) || !char.IsAsciiLetter(previous))
        {
            return false;
        }

        // lowercase then uppercase is always a word boundary.
        if (char.IsAsciiLetterLower(previous))
        {
            return true;
        }

        // Inside a run of capitals, split only before the run's last letter, and only once the run
        // is at least two letters long: FZMAStationAlpha gives "FZMA Station", JTsPassAndGas gives
        // "JTs Pass".
        return i >= 2
            && char.IsAsciiLetterUpper(area[i - 2])
            && i + 1 < area.Length
            && char.IsAsciiLetterLower(area[i + 1]);
    }

    /// <summary>
    /// A counting-sorted uniform grid over every marker, sized to the query radius. Build-time only:
    /// once the anchors are picked nothing needs it again, so it is not retained.
    /// </summary>
    private sealed class FineGrid
    {
        private readonly LootSpawnPoint[] _points;
        private readonly int[] _cellStart;
        private readonly int[] _order;
        private readonly int _dimension;
        private readonly float _cell;
        private readonly float _origin;

        internal FineGrid(ReadOnlySpan<LootSpawnPoint> points, float radius, float halfExtent)
        {
            _points = points.ToArray();
            _cell = MathF.Max(radius / FineCellDivisor, 1f);
            _origin = -halfExtent;
            _dimension = Math.Max(1, (int)MathF.Ceiling((halfExtent * 2f) / _cell));

            _cellStart = new int[(_dimension * _dimension) + 1];
            var cellOf = new int[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                int cell = CellOf(points[i].X, points[i].Z);
                cellOf[i] = cell;
                _cellStart[cell + 1]++;
            }

            for (int i = 1; i < _cellStart.Length; i++)
            {
                _cellStart[i] += _cellStart[i - 1];
            }

            _order = new int[points.Length];
            var cursor = new int[_dimension * _dimension];
            for (int i = 0; i < points.Length; i++)
            {
                int cell = cellOf[i];
                _order[_cellStart[cell] + cursor[cell]] = i;
                cursor[cell]++;
            }
        }

        internal int CountWithin(in Vector3 centre, float radius)
        {
            int rings = Math.Min((int)MathF.Ceiling(radius / _cell), _dimension);
            int centreX = AxisIndex(centre.X);
            int centreZ = AxisIndex(centre.Z);

            int minX = Math.Max(0, centreX - rings);
            int maxX = Math.Min(_dimension - 1, centreX + rings);
            int minZ = Math.Max(0, centreZ - rings);
            int maxZ = Math.Min(_dimension - 1, centreZ + rings);

            float radiusSquared = radius * radius;
            int found = 0;

            for (int z = minZ; z <= maxZ; z++)
            {
                int rowBase = z * _dimension;
                int start = _cellStart[rowBase + minX];
                int end = _cellStart[rowBase + maxX + 1];

                for (int i = start; i < end; i++)
                {
                    ref readonly LootSpawnPoint point = ref _points[_order[i]];
                    float dx = point.X - centre.X;
                    float dz = point.Z - centre.Z;
                    if ((dx * dx) + (dz * dz) <= radiusSquared)
                    {
                        found++;
                    }
                }
            }

            return found;
        }

        private int CellOf(float x, float z) => (AxisIndex(z) * _dimension) + AxisIndex(x);

        private int AxisIndex(float value)
        {
            int index = (int)MathF.Floor((value - _origin) / _cell);
            return Math.Clamp(index, 0, _dimension - 1);
        }
    }
}
