using System.Numerics;
using Cranberry.Zone.Gas;

namespace Cranberry.Zone.Loot;

/// <summary>
/// One of the ammunition boxes beside a gun on the floor (docs/39 §5; <b>one</b> of them since
/// D272, two before it). It is an extra ground entity, not an extra marker roll: it consumes no
/// spawn point, it is exempt from the room caps, and it exists only for as long as its gun does.
/// </summary>
/// <param name="GunInstanceId">The ZONE instance id of the marker whose gun this box belongs to.</param>
/// <param name="BoxIndex">0, or 1 for the second box of a pre-D272 pair. A box has no instance id of its own:
/// Z2's 168,322 marker ids are unique but span the whole 32-bit range (0x0001_5863 … 0xFFF9_B06D),
/// so there is no spare bit to synthesise one in. <see cref="Key"/> is the collision-free identity
/// a caller should hold instead.</param>
public readonly record struct LootClusterItem(
    uint GunInstanceId,
    int BoxIndex,
    uint ItemDefinitionId,
    uint GroundModelId,
    uint NameId,
    uint Count,
    Vector3 Position,
    float Yaw)
{
    /// <summary>
    /// This box's identity, unique across the whole map and distinct from any marker's: the gun's
    /// instance id in the high 32 bits and the box index in the low. A session's already-sent set
    /// can key on it directly.
    /// </summary>
    public ulong Key => ((ulong)GunInstanceId << 32) | (uint)BoxIndex;
}

/// <summary>
/// <b>D275 — what the laminated-armour pass did to one map.</b> Every field is a count of vests,
/// and <c>Rolled == Kept + Refused</c> always holds.
/// </summary>
/// <param name="Rolled">Markers that rolled the vest and survived the room caps.</param>
/// <param name="Kept">Vests actually standing on the floor after all three retail rules.</param>
/// <param name="RefusedByChance">Refused by the 5 % world spawn chance.</param>
/// <param name="RefusedBySpacing">Refused by the 250 m anti-cluster radius.</param>
/// <param name="RefusedBySquare">Refused by the cap of three per map square.</param>
public readonly record struct ArmourPassResult(
    int Rolled,
    int Kept,
    int RefusedByChance,
    int RefusedBySpacing,
    int RefusedBySquare)
{
    /// <summary>Vests the rules took back off the floor.</summary>
    public int Refused => RefusedByChance + RefusedBySpacing + RefusedBySquare;
}

/// <summary>
/// <b>The whole match's ground loot, decided once.</b> Where <see cref="Z2LootSpawns"/> answers
/// "what would this one marker roll?", this answers "what is actually on the floor of Z2 this
/// match?" — which is the question the owner's two complaints are about.
///
/// <para><b>What it adds over a bare <c>TryRoll</c> loop.</b> The spawn gate makes each marker
/// independent, and independence alone still piles six items into one corner of a room; the client
/// packs its markers 12.45 to a 4 m room (docs/39 §4.2). So after the gate this class walks the
/// whole map once and applies the owner's counted retail caps — 6 items, 2 weapons, one bag, one
/// vest, one helmet per 4 m / ±2 m room — suppressing the surplus. The caps are <b>symmetric</b>:
/// a marker is kept only if <i>every</i> already-kept neighbour's room stays inside the caps too,
/// so afterwards the guarantee holds from every item's own point of view, not just the first one
/// looked at.</para>
///
/// <para><b>Why once per match and not per burst.</b> A cap applied to the packet burst is the
/// blob-then-nothing defect this replaces (docs/39 §L4): it produced 64 items inside 20 m and
/// nothing at all beyond. This cap belongs to the world. <see cref="QueryLive"/> then hands a
/// burst the nearest <i>items</i> rather than the nearest markers, so a cap on the burst truncates
/// a real neighbourhood instead of a quarter-empty one, and a streaming top-up as the player moves
/// is just another call.</para>
///
/// <para><b>Determinism.</b> Everything here is a pure function of
/// <c>(matchSeed, marker instance id)</c>: the gate, the item pick, the cap order (ascending
/// instance id — the file's own order is a grid sort and must not decide who survives) and the
/// cluster geometry. The same seed reproduces the same map, on any machine, in any run.</para>
/// </summary>
public sealed class Z2LootLayout
{
    private readonly Z2LootSpawns _spawns;
    private readonly LootTables _tables;
    private readonly bool[] _live;
    private readonly LootItemKind[] _kind;

    private Z2LootLayout(
        Z2LootSpawns spawns,
        LootTables tables,
        ulong matchSeed,
        LootDensityOptions density,
        bool[] live,
        LootItemKind[] kind,
        int gated,
        int suppressed,
        ArmourPassResult armour)
    {
        _spawns = spawns;
        _tables = tables;
        _live = live;
        _kind = kind;
        MatchSeed = matchSeed;
        Density = density;
        GatedCount = gated;
        SuppressedCount = suppressed;
        Armour = armour;
    }

    /// <summary>The seed this layout was built from. Same seed, same map.</summary>
    public ulong MatchSeed { get; }

    /// <summary>The density model in force, gate override included.</summary>
    public LootDensityOptions Density { get; }

    /// <summary>Markers that passed the spawn gate, before the room caps.</summary>
    public int GatedCount { get; }

    /// <summary>Gated markers the room caps then suppressed. Typically ~5 % of <see cref="GatedCount"/>.</summary>
    public int SuppressedCount { get; }

    /// <summary>
    /// <b>D275 — what the laminated-armour pass did.</b> Zero everywhere when
    /// <see cref="LootDensityOptions.LaminatedArmourRules"/> is off.
    /// </summary>
    public ArmourPassResult Armour { get; }

    /// <summary>
    /// Items on the floor: <see cref="GatedCount"/> − <see cref="SuppressedCount"/> − the vests
    /// D275 took back off it.
    /// </summary>
    public int LiveCount => GatedCount - SuppressedCount - Armour.Refused;

    /// <summary>Every marker in the world, live or not.</summary>
    public int MarkerCount => _live.Length;

    /// <summary>The placements this layout was built over.</summary>
    public Z2LootSpawns Spawns => _spawns;

    /// <summary>The tables this layout rolled against.</summary>
    public LootTables Tables => _tables;

    /// <summary>One bool per marker: does an item stand there? Indexed as <see cref="Z2LootSpawns.Points"/>.</summary>
    public ReadOnlySpan<bool> LiveMarkers => _live;

    /// <summary>Does the marker at <paramref name="index"/> carry an item this match?</summary>
    public bool IsLive(int index) => _live[index];

    /// <summary>
    /// The kind of item standing on <paramref name="index"/>, or <see cref="LootItemKind.Unknown"/>
    /// for an empty marker. Free — it is the same array the room caps were computed from.
    /// </summary>
    public LootItemKind KindAt(int index) => _live[index] ? _kind[index] : LootItemKind.Unknown;

    /// <summary>
    /// Decides the whole map's loot. Over 168,322 markers this is one gate draw each plus a 4 m
    /// grid insert for the ~42,000 survivors — milliseconds, once, at match start.
    /// </summary>
    public static Z2LootLayout Build(
        Z2LootSpawns spawns,
        LootTables tables,
        ulong matchSeed,
        LootDensityOptions? density = null)
    {
        ArgumentNullException.ThrowIfNull(spawns);
        ArgumentNullException.ThrowIfNull(tables);

        density ??= tables.Density;

        int count = spawns.Count;
        var live = new bool[count];
        var kind = new LootItemKind[count];
        int gated = 0;

        // Pass 1 — the gate. Independent per marker, so it is exactly reproducible and the caps
        // below can be reasoned about (and re-tuned) without touching it.
        for (int i = 0; i < count; i++)
        {
            if (!spawns.TryRoll(i, tables, matchSeed, density, out LootSpawnRoll roll))
            {
                continue;
            }

            live[i] = true;
            kind[i] = KindOf(tables, spawns.Categories[spawns[i].CategoryIndex], roll.ItemDefinitionId);
            gated++;
        }

        int suppressed = ApplyRoomCaps(spawns, density, live, kind, gated);
        ArmourPassResult armour = ApplyLaminatedArmourRules(spawns, tables, matchSeed, density, live, kind);
        return new Z2LootLayout(
            spawns, tables, matchSeed, density, live, kind, gated, suppressed, armour);
    }

    /// <summary>
    /// What stands on the marker at <paramref name="index"/>, or false when that marker is empty
    /// this match — because it lost the gate, because the room around it was full, or because its
    /// category rolls nothing at all (<c>FireExtinguisher</c>).
    /// </summary>
    public bool TryGet(int index, out LootSpawnRoll roll)
    {
        if (!_live[index])
        {
            roll = default;
            return false;
        }

        return _spawns.TryRoll(index, _tables, MatchSeed, Density, out roll);
    }

    /// <summary>
    /// Fills <paramref name="into"/> with the markers <b>carrying an item</b> nearest to
    /// <paramref name="centre"/>, closest first, and returns how many live items the disc holds in
    /// total (which may exceed the span, exactly as <see cref="Z2LootSpawns.QueryNearest(in Vector3, float, Span{int})"/>
    /// does — so a caller can size a bigger buffer, or stream the rest as the player moves).
    /// </summary>
    public int QueryLive(in Vector3 centre, float radius, Span<int> into) =>
        _spawns.QueryNearest(centre, radius, uint.MaxValue, _live, into);

    /// <summary>
    /// The ammunition boxes that belong to <paramref name="roll"/>: two of the gun's own calibre,
    /// or zero for anything that is not a clustered gun (melee included). Returns how many were
    /// written; <paramref name="into"/> needs room for two.
    /// <para>
    /// Geometry (docs/39 §5, owner-authored O4): ±<see cref="LootTables.ClusterOffsetMetres"/>
    /// along the axis <b>perpendicular</b> to the marker's yaw, so a rifle lying along its own yaw
    /// never has a box under it, at the gun's own height; the second box is turned a further
    /// <see cref="LootTables.ClusterSecondBoxYawOffset"/> radians so a pair does not read as one
    /// object mirrored.
    /// </para>
    /// <para>
    /// A box is identified by its gun's instance id plus its index in the pair
    /// (<see cref="LootClusterItem.Key"/>), never by a synthetic instance id: Z2's marker ids are
    /// unique but occupy the whole 32-bit range, so there is no spare bit to steal.
    /// </para>
    /// <para>
    /// <b>D286 (reverses D272): how many boxes is <see cref="LootTables.BoxesPerGun"/>, and it ships
    /// as 2.</b> A
    /// caller with room for fewer than that gets <b>none</b>: half a cluster is a wrong world
    /// rather than a smaller one, which is why this is an all-or-nothing write.
    /// </para>
    /// </summary>
    public int ClusterFor(in LootSpawnRoll roll, Span<LootClusterItem> into)
    {
        int boxes = _tables.BoxesPerGun;
        if (boxes <= 0
            || into.Length < boxes
            || !_tables.TryGetCluster(roll.ItemDefinitionId, out LootClusterBox box))
        {
            return 0;
        }

        // Perpendicular to the marker's facing: forward is (sin yaw, cos yaw) in the client's
        // Y-up frame, so the normal is (cos yaw, −sin yaw).
        float offset = _tables.ClusterOffsetMetres;
        float sideX = MathF.Cos(roll.Yaw) * offset;
        float sideZ = -MathF.Sin(roll.Yaw) * offset;

        into[0] = new LootClusterItem(
            GunInstanceId: roll.InstanceId,
            BoxIndex: 0,
            ItemDefinitionId: box.ItemDefinitionId,
            GroundModelId: box.GroundModelId,
            NameId: box.NameId,
            Count: box.Count,
            Position: new Vector3(roll.Position.X + sideX, roll.Position.Y, roll.Position.Z + sideZ),
            Yaw: roll.Yaw);

        if (boxes == 1)
        {
            return 1;
        }

        into[1] = new LootClusterItem(
            GunInstanceId: roll.InstanceId,
            BoxIndex: 1,
            ItemDefinitionId: box.ItemDefinitionId,
            GroundModelId: box.GroundModelId,
            NameId: box.NameId,
            Count: box.Count,
            Position: new Vector3(roll.Position.X - sideX, roll.Position.Y, roll.Position.Z - sideZ),
            Yaw: roll.Yaw + _tables.ClusterSecondBoxYawOffset);

        return 2;
    }

    /// <summary>
    /// The kind an item id carries in its category's table, or <see cref="LootItemKind.Unknown"/>.
    /// Looked up by the category's <i>name</i>, not by its ordinal: the placement file and the
    /// tables file are written together and agree today, but nothing enforces that they must, and
    /// a silently mismatched ordinal would mis-classify every item the room caps reason about.
    /// </summary>
    private static LootItemKind KindOf(LootTables tables, string categoryKey, uint itemDefinitionId)
    {
        if (!tables.TryGet(categoryKey, out LootCategoryTable? table))
        {
            return LootItemKind.Unknown;
        }

        foreach (LootTableEntry entry in table.Entries)
        {
            if (entry.ItemDefinitionId == itemDefinitionId)
            {
                return entry.Kind;
            }
        }

        return LootItemKind.Unknown;
    }

    /// <summary>
    /// The room-cap post-pass. Walks the gated markers in ascending instance-id order and keeps one
    /// only when doing so leaves <b>every</b> room it touches — its own and each kept neighbour's —
    /// inside the caps. Running counts per kept marker make that check O(neighbours) rather than a
    /// re-count, so the whole map costs one 4 m grid and one pass.
    /// </summary>
    private static int ApplyRoomCaps(
        Z2LootSpawns spawns,
        LootDensityOptions density,
        bool[] live,
        LootItemKind[] kind,
        int gated)
    {
        if (gated == 0)
        {
            return 0;
        }

        // Deterministic order, independent of the file's grid sort: whoever has the lower ZONE
        // instance id wins a contested room, in every run and on every machine. (The file is
        // sorted into 64 m cells, so taking it in file order would let the map's layout decide
        // which item survives — reproducible, but arbitrary and invisible.)
        var order = new int[gated];
        var byInstance = new uint[gated];
        int next = 0;
        for (int i = 0; i < live.Length && next < gated; i++)
        {
            if (live[i])
            {
                order[next] = i;
                byInstance[next] = spawns[i].InstanceId;
                next++;
            }
        }

        Array.Sort(byInstance, order);

        float radius = density.RoomRadiusMetres;
        float radiusSquared = radius * radius;
        float height = density.RoomHeightMetres;
        int maxItems = density.MaxItemsPerRoom;
        int maxWeapons = density.MaxWeaponsPerRoom;
        LootItemKindSet singletons = density.SingletonKinds;

        // Running per-kept-marker state. "Room" always means the disc around that marker itself,
        // so these are what make the symmetric check O(neighbours) instead of a re-count.
        var roomTotal = new byte[live.Length];
        var roomWeapons = new byte[live.Length];
        var roomSingletons = new uint[live.Length];

        // A grid whose cell is exactly the room radius, so every point within one room lies in the
        // 3 × 3 block around the candidate — the 64 m cells the placement file ships are sixteen
        // times too coarse to answer this question.
        var cells = new Dictionary<long, List<int>>(gated);
        var neighbours = new List<int>(32);

        // The candidate's own room, tallied by kind as its neighbours are walked. Without this the
        // caps would only ever protect a room from the item being added to it, and a marker whose
        // two neighbouring helmets are more than a room apart from EACH OTHER would still end up
        // looking at two helmets. (Found by NoItemOnTheFloorSitsInAnOverfilledRoom.)
        var roomKindCount = new int[Enum.GetValues<LootItemKind>().Length];
        int suppressed = 0;

        foreach (int index in order)
        {
            ref readonly LootSpawnPoint point = ref spawns[index];
            LootItemKind candidateKind = kind[index];
            bool candidateIsWeapon = candidateKind == LootItemKind.Weapon;
            uint candidateBit = singletons.Contains(candidateKind) ? 1u << (int)candidateKind : 0u;

            neighbours.Clear();
            Array.Clear(roomKindCount);
            roomKindCount[(int)candidateKind]++;
            int roomWeaponCount = candidateIsWeapon ? 1 : 0;
            bool keep = true;

            int cellX = CellIndex(point.X, radius);
            int cellZ = CellIndex(point.Z, radius);

            for (int dz = -1; dz <= 1 && keep; dz++)
            {
                for (int dx = -1; dx <= 1 && keep; dx++)
                {
                    if (!cells.TryGetValue(CellKey(cellX + dx, cellZ + dz), out List<int>? bucket))
                    {
                        continue;
                    }

                    foreach (int other in bucket)
                    {
                        ref readonly LootSpawnPoint kept = ref spawns[other];
                        float ox = kept.X - point.X;
                        float oz = kept.Z - point.Z;
                        if ((ox * ox) + (oz * oz) > radiusSquared
                            || MathF.Abs(kept.Y - point.Y) > height)
                        {
                            continue;
                        }

                        // The neighbour's own room must stay legal too — that is what makes the
                        // cap symmetric rather than "whoever asked first".
                        if (roomTotal[other] >= maxItems
                            || (candidateIsWeapon && roomWeapons[other] >= maxWeapons)
                            || (candidateBit != 0 && (roomSingletons[other] & candidateBit) != 0))
                        {
                            keep = false;
                            break;
                        }

                        neighbours.Add(other);
                        roomKindCount[(int)kind[other]]++;
                        if (kind[other] == LootItemKind.Weapon)
                        {
                            roomWeaponCount++;
                        }
                    }
                }
            }

            // …and the candidate's own room, which is the half a "does adding this break the
            // neighbour?" check cannot see.
            // Note that the weapon and singleton counts are tested whatever the candidate is: three
            // rifles that are each more than a room apart from EACH OTHER can still all fall inside
            // one bandage's room, and it is that bandage's room the player is standing in.
            if (keep
                && (neighbours.Count + 1 > maxItems
                    || roomWeaponCount > maxWeapons
                    || ExceedsASingleton(roomKindCount, singletons)))
            {
                keep = false;
            }

            if (!keep)
            {
                live[index] = false;
                suppressed++;
                continue;
            }

            roomTotal[index] = (byte)(neighbours.Count + 1);
            roomWeapons[index] = (byte)roomWeaponCount;
            roomSingletons[index] = SingletonBits(roomKindCount, singletons);

            foreach (int other in neighbours)
            {
                roomTotal[other]++;
                if (candidateIsWeapon)
                {
                    roomWeapons[other]++;
                }

                roomSingletons[other] |= candidateBit;
            }

            long key = CellKey(cellX, cellZ);
            if (!cells.TryGetValue(key, out List<int>? own))
            {
                own = new List<int>(4);
                cells[key] = own;
            }

            own.Add(index);
        }

        return suppressed;
    }

    /// <summary>
    /// <b>D275 — the 2017-06-29 laminated-armour rules, as a post-pass over the deterministic
    /// layout.</b> Retail cut the vest's world spawn chance from 10 % to 5 % and gave it a 250 m
    /// anti-cluster radius and a cap of three per map square (the owner's own research, High
    /// confidence, dated). Cranberry had a flat 18/608 share of <c>Gear01</c> and no spacing rule
    /// at all, which at D270's density lays <b>700</b> vests on the map.
    ///
    /// <para><b>Why a post-pass and not a table weight.</b> A weight cannot express any of the
    /// three rules: it has no notion of distance, no notion of a square, and changing it would
    /// re-shuffle the whole <c>Gear01</c> draw. Here the marker still rolls the vest exactly as it
    /// did; the vest is then taken back off the floor by a rule that can see the rest of the
    /// map.</para>
    ///
    /// <para><b>Order.</b> Ascending ZONE instance id, the same tie-break the room caps use and for
    /// the same reason: the placement file is sorted into 64 m cells, so taking it in file order
    /// would let the map's own layout decide which vest survives — reproducible, but arbitrary and
    /// invisible. Same seed, same 24 vests, on any machine.</para>
    ///
    /// <para>Measured at seed 1 under D270/D271: 700 vests roll, 35 pass the 5 % chance, 11 are
    /// then refused by the 250 m radius and 0 by the per-square cap — <b>24</b> on the whole
    /// map.</para>
    /// </summary>
    private static ArmourPassResult ApplyLaminatedArmourRules(
        Z2LootSpawns spawns,
        LootTables tables,
        ulong matchSeed,
        LootDensityOptions density,
        bool[] live,
        LootItemKind[] kind)
    {
        if (!density.LaminatedArmourRules)
        {
            return default;
        }

        uint vestId = LootDensityOptions.LaminatedArmourItemDefinitionId;

        // BodyArmor is the coarse class and the roster carries exactly one vest, but the pass
        // tests the ITEM ID as well: a second armour row added to the roster later must not
        // silently inherit rules written for this one.
        var candidates = new List<int>(1024);
        for (int i = 0; i < live.Length; i++)
        {
            if (live[i]
                && kind[i] == LootItemKind.BodyArmor
                && spawns.TryRoll(i, tables, matchSeed, density, out LootSpawnRoll roll)
                && roll.ItemDefinitionId == vestId)
            {
                candidates.Add(i);
            }
        }

        if (candidates.Count == 0)
        {
            return default;
        }

        int rolled = candidates.Count;
        candidates.Sort((left, right) => spawns[left].InstanceId.CompareTo(spawns[right].InstanceId));

        float spacing = density.LaminatedArmourSpacingMetres;
        float spacingSquared = spacing * spacing;
        float square = density.LaminatedArmourMapSquareMetres;
        int maxPerSquare = density.LaminatedArmourMaxPerSquare;

        // One grid whose cell is the anti-cluster radius itself, so every vest inside the radius
        // lies in the 3 x 3 block around the candidate; and one tally per map square.
        var kept = new Dictionary<long, List<int>>(64);
        var perSquare = new Dictionary<long, int>(64);
        int keptCount = 0;
        int byChance = 0;
        int bySpacing = 0;
        int bySquare = 0;

        foreach (int index in candidates)
        {
            ref readonly LootSpawnPoint point = ref spawns[index];

            // Retail's world spawn chance, on its own sub-stream (Z2LootSpawns.ArmourSeedFor).
            var random = new GasRandom(Z2LootSpawns.ArmourSeedFor(matchSeed, point.InstanceId));
            if (random.NextDouble() >= density.LaminatedArmourWorldChance)
            {
                live[index] = false;
                byChance++;
                continue;
            }

            long squareKey = CellKey(CellIndex(point.X, square), CellIndex(point.Z, square));
            if (perSquare.TryGetValue(squareKey, out int already) && already >= maxPerSquare)
            {
                live[index] = false;
                bySquare++;
                continue;
            }

            bool tooClose = false;
            if (spacing > 0f)
            {
                int cellX = CellIndex(point.X, spacing);
                int cellZ = CellIndex(point.Z, spacing);
                for (int dz = -1; dz <= 1 && !tooClose; dz++)
                {
                    for (int dx = -1; dx <= 1 && !tooClose; dx++)
                    {
                        if (!kept.TryGetValue(CellKey(cellX + dx, cellZ + dz), out List<int>? bucket))
                        {
                            continue;
                        }

                        foreach (int other in bucket)
                        {
                            ref readonly LootSpawnPoint standing = ref spawns[other];
                            float ox = standing.X - point.X;
                            float oz = standing.Z - point.Z;
                            if ((ox * ox) + (oz * oz) <= spacingSquared)
                            {
                                tooClose = true;
                                break;
                            }
                        }
                    }
                }
            }

            if (tooClose)
            {
                live[index] = false;
                bySpacing++;
                continue;
            }

            keptCount++;
            perSquare[squareKey] = already + 1;
            long key = CellKey(CellIndex(point.X, spacing > 0f ? spacing : 1f),
                               CellIndex(point.Z, spacing > 0f ? spacing : 1f));
            if (!kept.TryGetValue(key, out List<int>? own))
            {
                own = new List<int>(4);
                kept[key] = own;
            }

            own.Add(index);
        }

        return new ArmourPassResult(rolled, keptCount, byChance, bySpacing, bySquare);
    }

    /// <summary>Does this room already hold two of a kind it may hold only one of?</summary>
    private static bool ExceedsASingleton(int[] roomKindCount, LootItemKindSet singletons)
    {
        for (int kind = 0; kind < roomKindCount.Length; kind++)
        {
            if (roomKindCount[kind] > 1 && singletons.Contains((LootItemKind)kind))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Which singleton kinds this room holds, as the running mask a neighbour is tested against.</summary>
    private static uint SingletonBits(int[] roomKindCount, LootItemKindSet singletons)
    {
        uint bits = 0;
        for (int kind = 0; kind < roomKindCount.Length; kind++)
        {
            if (roomKindCount[kind] > 0 && singletons.Contains((LootItemKind)kind))
            {
                bits |= 1u << kind;
            }
        }

        return bits;
    }

    private static int CellIndex(float metres, float cellSize) =>
        (int)MathF.Floor(metres / cellSize);

    private static long CellKey(int x, int z) => ((long)z << 32) | (uint)x;
}
