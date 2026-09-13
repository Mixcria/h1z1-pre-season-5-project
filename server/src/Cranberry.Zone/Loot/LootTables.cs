using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Cranberry.Zone.Gas;

namespace Cranberry.Zone.Loot;

/// <summary>
/// The coarse class of a loot item — what the room caps (docs/39 §4.3) and the regression tests
/// reason about. It is written out by the generator rather than inferred at load time because the
/// client's own <c>ITEM_CLASS</c> cannot separate the cases that matter: 16053 covers both
/// ammunition and the two medical items, and 25000 covers both a motorcycle helmet and a beanie.
/// </summary>
public enum LootItemKind
{
    Unknown = 0,

    /// <summary>Firearm, bow or melee. Capped at <see cref="LootDensityOptions.MaxWeaponsPerRoom"/>.</summary>
    Weapon,

    /// <summary>Rounds and arrows. Reaches the floor only from <c>Ammo01</c> and gun clusters.</summary>
    Ammunition,

    Medical,

    /// <summary>Shirts, trousers, caps, beanies, shoes — the layer that makes a KotK floor look right.</summary>
    Clothing,

    /// <summary>Motorcycle and tactical helmets. One per room.</summary>
    Helmet,

    /// <summary>The one vest in the roster. One per room.</summary>
    BodyArmor,

    /// <summary>Container for the Backpack passive slot. One per room.</summary>
    Backpack,

    Utility,

    Throwable,
}

/// <summary>
/// One item a spawner category may produce, with the two client ids the wire needs
/// (<see cref="ItemDefinitionId"/> for <c>ClientUpdate.ItemAdd 11 02</c> and
/// <see cref="GroundModelId"/> for <c>AddLightweightNpc 0xd6</c>, docs/13 §2) and the relative
/// <see cref="Weight"/> that decides how often it comes up.
/// </summary>
/// <param name="ItemDefinitionId"><c>ClientItemDefinitions.txt</c> row id. Derived.</param>
/// <param name="NameId">That row's <c>NAME_ID</c>, for the interaction prompt. Derived.</param>
/// <param name="GroundModelId"><c>Models.txt</c> row id of the ground actor. Derived.</param>
/// <param name="Count">Stack size handed out, already clamped to MAX_STACK_SIZE. Ours.</param>
/// <param name="Weight">Relative odds inside the category. Owner-authored, docs/39 §3 (O2).</param>
/// <param name="Kind">Coarse class, for the room caps. Ours, docs/39 §4.3.</param>
/// <param name="ItemClass">That row's <c>ITEM_CLASS</c>. Derived.</param>
/// <param name="PassiveEquipSlotId">That row's <c>PASSIVE_EQUIP_SLOT_ID</c> — the slot a pickup
/// auto-equips into (docs/39 §7). Derived; 0 means the item has no passive slot in this build.</param>
/// <param name="Name">Localised name, for logs only; never on the wire.</param>
public readonly record struct LootTableEntry(
    uint ItemDefinitionId,
    uint NameId,
    uint GroundModelId,
    uint Count,
    int Weight,
    LootItemKind Kind,
    uint ItemClass,
    uint PassiveEquipSlotId,
    string Name);

/// <summary>
/// One gun → ammunition pairing: the box (or boxes) beside every gun on the floor (docs/39 §5,
/// owner-authored O4 — <i>"one AR-15 with two boxes next to it with 30 ammo in each"</i>, re-ruled
/// to ONE box by D272). <see cref="Count"/> is one magazine of that calibre, never a random
/// dribble; how many boxes a gun gets is <see cref="LootTables.BoxesPerGun"/>.
/// </summary>
public readonly record struct LootClusterBox(
    uint WeaponItemDefinitionId,
    uint ItemDefinitionId,
    uint NameId,
    uint GroundModelId,
    uint Count,
    string Name);

/// <summary>
/// The table one spawner category rolls on. The entries and their odds are Cranberry's design
/// (the August client ships no category → item mapping for KotK, docs/29 §5); the ids inside each
/// entry are the client's.
/// <para>
/// Rolling is seedable and allocation-free: <see cref="Roll(ulong)"/> is a pure function of the
/// seed, so a match replays identically from its seed and a test can freeze an expectation.
/// </para>
/// </summary>
public sealed class LootCategoryTable
{
    private readonly LootTableEntry[] _entries;
    private readonly int[] _cumulativeWeight;

    internal LootCategoryTable(
        string key,
        int spawnerCount,
        double spawnChance,
        string note,
        LootTableEntry[] entries)
    {
        Key = key;
        SpawnerCount = spawnerCount;
        SpawnChance = spawnChance;
        Note = note;
        _entries = entries;

        _cumulativeWeight = new int[entries.Length];
        int running = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            running += Math.Max(0, entries[i].Weight);
            _cumulativeWeight[i] = running;
        }

        TotalWeight = running;
    }

    /// <summary>The marker model's own category name, e.g. <c>Gear01</c> (docs/29 §5).</summary>
    public string Key { get; }

    /// <summary>How many markers of this category Z2 places. Informational.</summary>
    public int SpawnerCount { get; }

    /// <summary>
    /// <b>The density gate.</b> Probability in [0, 1] that one marker of this category spawns
    /// anything at all — the fix for docs/39 §L1, where <see cref="Roll(ulong)"/> always returned
    /// an entry and so every marker in range became an item.
    /// <para>
    /// It lives in the generated file rather than in code so the one number that decides how much
    /// is on the floor can be retuned without a rebuild. It is written by
    /// <c>tools/data/gen-loot-tables.py</c>'s <c>SPAWN_CHANCE</c> and mirrored by
    /// <see cref="LootDensityOptions.DefaultSpawnChance"/>; a test asserts the two agree.
    /// </para>
    /// </summary>
    public double SpawnChance { get; }

    /// <summary>Why this table holds what it holds; copied out of the generated file.</summary>
    public string Note { get; }

    /// <summary>Sum of every entry's weight. Zero means the category rolls nothing.</summary>
    public int TotalWeight { get; }

    public int Count => _entries.Length;

    public ReadOnlySpan<LootTableEntry> Entries => _entries;

    public ref readonly LootTableEntry this[int index] => ref _entries[index];

    /// <summary>
    /// Picks one entry for <paramref name="seed"/>. Returns false for an empty table — which is a
    /// real case, not an error: <c>FireExtinguisher</c> has 1,541 markers in Z2 and no item in the
    /// client to put on them (docs/33 §3.6).
    /// </summary>
    public bool Roll(ulong seed, out LootTableEntry entry)
    {
        if (TotalWeight <= 0 || _entries.Length == 0)
        {
            entry = default;
            return false;
        }

        var random = new GasRandom(seed);
        int pick = (int)(random.NextUInt64() % (ulong)TotalWeight);

        for (int i = 0; i < _cumulativeWeight.Length; i++)
        {
            if (pick < _cumulativeWeight[i])
            {
                entry = _entries[i];
                return true;
            }
        }

        entry = _entries[^1];
        return true;
    }

    /// <summary>Convenience overload for callers that only want the entry or nothing.</summary>
    public LootTableEntry? Roll(ulong seed) => Roll(seed, out LootTableEntry entry) ? entry : null;
}

/// <summary>
/// The whole <c>z2-loot-tables.json</c> file: one <see cref="LootCategoryTable"/> per spawner
/// category. Format <c>cranberry.loot-tables</c> v1, written by
/// <c>tools/data/gen-loot-tables.py</c> from the client's own
/// <c>ClientItemDefinitions.txt</c> / <c>Models.txt</c> / <c>ContentPacks.txt</c>; the layout and
/// the derivation rules are documented in <c>docs/33-z2-loot-placement.md</c>.
/// </summary>
public sealed class LootTables
{
    /// <summary>The only format string this loader accepts.</summary>
    public const string FormatName = "cranberry.loot-tables";

    /// <summary>
    /// The only format version this loader accepts. v2 added the per-category
    /// <see cref="LootCategoryTable.SpawnChance"/> gate, the <c>density</c> block and the
    /// <c>clusters</c> table (docs/39). A v1 file is <b>rejected</b> rather than read leniently:
    /// its missing gate would silently mean "every marker spawns", which is the exact defect v2
    /// exists to fix.
    /// </summary>
    public const int FormatVersion = 2;

    /// <summary>Default file name inside <see cref="LootDataPaths"/>' directory.</summary>
    public const string DefaultFileName = "z2-loot-tables.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly LootCategoryTable[] _categories;
    private readonly Dictionary<string, int> _byKey;
    private readonly LootClusterBox[] _clusters;
    private readonly Dictionary<uint, int> _clusterByWeapon;

    private LootTables(
        string zone,
        LootCategoryTable[] categories,
        LootDensityOptions density,
        LootClusterBox[] clusters,
        float clusterOffsetMetres,
        float clusterSecondBoxYawOffset,
        int boxesPerGun)
    {
        Zone = zone;
        _categories = categories;
        Density = density;
        _clusters = clusters;
        ClusterOffsetMetres = clusterOffsetMetres;
        ClusterSecondBoxYawOffset = clusterSecondBoxYawOffset;
        BoxesPerGun = boxesPerGun;

        _byKey = new Dictionary<string, int>(categories.Length, StringComparer.Ordinal);
        for (int i = 0; i < categories.Length; i++)
        {
            _byKey[categories[i].Key] = i;
        }

        _clusterByWeapon = new Dictionary<uint, int>(clusters.Length);
        for (int i = 0; i < clusters.Length; i++)
        {
            _clusterByWeapon[clusters[i].WeaponItemDefinitionId] = i;
        }
    }

    /// <summary>The zone these tables were generated for (<c>Z2</c>).</summary>
    public string Zone { get; }

    /// <summary>
    /// The room geometry and caps the file was generated with (docs/39 §4.3, owner-authored O3).
    /// <see cref="LootDensityOptions.SpawnChanceOverride"/> is always null here — the gate itself
    /// is per-category on <see cref="LootCategoryTable.SpawnChance"/>.
    /// </summary>
    public LootDensityOptions Density { get; }

    /// <summary>Metres from the gun to each of its two boxes, perpendicular to the marker's yaw.</summary>
    public float ClusterOffsetMetres { get; }

    /// <summary>Extra yaw on the second box so a pair does not read as one mirrored object.</summary>
    public float ClusterSecondBoxYawOffset { get; }

    /// <summary>
    /// <b>D272 — how many ammunition boxes a gun on the floor gets.</b> The owner's ruling of
    /// 2026-09-03: <i>"ONE ammo box per gun cluster, not two (the client's own markers pair ammo
    /// only at the 72 <c>Ammo01</c> spots — keep those as they are)."</i>
    /// <para>
    /// <b>What two boxes did.</b> <c>AUDIT-loot.md</c> G2 measured it: <b>34.7 %</b> of every
    /// ground object on the map was an ammunition box, boxes outnumbered weapons 1.77 : 1, the
    /// median nearest-neighbour distance over the whole floor was exactly <b>0.50 m</b> — this
    /// table's own <see cref="ClusterOffsetMetres"/> — and the dominant non-singleton cluster was
    /// size 3, a gun and its two boxes. At one box, and with D270's density, ammunition falls to
    /// <b>20.2 %</b>.
    /// </para>
    /// <para>
    /// The 72 <c>Ammo01</c> markers are untouched by this: they are marker rolls, not cluster
    /// boxes, and the client itself lays them out as 36 pairs 0.65-0.68 m apart (docs/65 §2.6) —
    /// the only pairing the client corroborates at all.
    /// </para>
    /// <para>
    /// It lives in the generated file, like the gates and the offset, so the question is retuned
    /// by editing one line rather than by a rebuild. A file that names none means <b>2</b>: that
    /// is what a pre-D272 file said, and defaulting to 1 would silently rewrite its meaning.
    /// </para>
    /// </summary>
    public int BoxesPerGun { get; }

    /// <summary>Every gun → ammunition pairing (docs/39 §5). Melee has none.</summary>
    public ReadOnlySpan<LootClusterBox> Clusters => _clusters;

    /// <summary>
    /// The box that flanks <paramref name="weaponItemDefinitionId"/>, or false for an item that
    /// gets no cluster (melee, and everything that is not a weapon).
    /// </summary>
    public bool TryGetCluster(uint weaponItemDefinitionId, out LootClusterBox box)
    {
        if (_clusterByWeapon.TryGetValue(weaponItemDefinitionId, out int index))
        {
            box = _clusters[index];
            return true;
        }

        box = default;
        return false;
    }

    public int Count => _categories.Length;

    public ReadOnlySpan<LootCategoryTable> Categories => _categories;

    public LootCategoryTable this[int index] => _categories[index];

    public bool TryGet(string key, [NotNullWhen(true)] out LootCategoryTable? table)
    {
        if (_byKey.TryGetValue(key, out int index))
        {
            table = _categories[index];
            return true;
        }

        table = null;
        return false;
    }

    /// <summary>Category ordinal, or −1. Matches the index encoded in the placement file.</summary>
    public int IndexOf(string key) => _byKey.TryGetValue(key, out int index) ? index : -1;

    /// <summary>Loads and validates one tables file.</summary>
    public static LootTables Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllBytes(path), path);
    }

    /// <summary>Loads the tables shipped beside the server (see <see cref="LootDataPaths"/>).</summary>
    public static LootTables LoadDefault() => Load(LootDataPaths.Require(DefaultFileName));

    /// <summary>Parses an in-memory copy — the form the tests use.</summary>
    public static LootTables Parse(ReadOnlySpan<byte> json, string origin)
    {
        TablesFile? file = JsonSerializer.Deserialize<TablesFile>(json, JsonOptions)
            ?? throw new InvalidDataException($"{origin}: empty loot-table document.");

        if (!string.Equals(file.Format, FormatName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{origin}: format is '{file.Format}', expected '{FormatName}'.");
        }

        if (file.FormatVersion != FormatVersion)
        {
            throw new InvalidDataException(
                $"{origin}: format version {file.FormatVersion}, expected {FormatVersion}.");
        }

        var categories = new LootCategoryTable[file.Categories.Count];
        for (int i = 0; i < categories.Length; i++)
        {
            CategoryRow row = file.Categories[i];
            if (string.IsNullOrEmpty(row.Key))
            {
                throw new InvalidDataException($"{origin}: category {i} has no key.");
            }

            var entries = new LootTableEntry[row.Entries.Count];
            for (int e = 0; e < entries.Length; e++)
            {
                EntryRow entry = row.Entries[e];
                if (entry.ItemDefinitionId == 0 || entry.GroundModelId == 0)
                {
                    throw new InvalidDataException(
                        $"{origin}: {row.Key} entry {e} has no item definition or ground model id.");
                }

                if (entry.Weight <= 0)
                {
                    throw new InvalidDataException(
                        $"{origin}: {row.Key} entry {e} (item {entry.ItemDefinitionId}) has weight "
                        + $"{entry.Weight}; a table entry that can never be rolled should be deleted.");
                }

                entries[e] = new LootTableEntry(
                    ItemDefinitionId: entry.ItemDefinitionId,
                    NameId: entry.NameId,
                    GroundModelId: entry.GroundModelId,
                    Count: Math.Max(1u, entry.Count),
                    Weight: entry.Weight,
                    Kind: ParseKind(entry.Kind, origin, row.Key, entry.ItemDefinitionId),
                    ItemClass: entry.ItemClass,
                    PassiveEquipSlotId: entry.PassiveEquipSlotId,
                    Name: entry.Name ?? string.Empty);
            }

            // A missing gate would silently mean "this category never spawns" (0.0 is the default
            // of the deserialised double), which is as wrong as the p = 1.0 the gate replaces —
            // so the field is nullable and its absence is an error, not a default.
            double spawnChance = entries.Length == 0
                ? 0.0
                : row.SpawnChance ?? throw new InvalidDataException(
                    $"{origin}: category {row.Key} has {entries.Length} entries but no "
                    + "'spawnChance'. The per-marker gate is not optional (docs/39 §4).");

            if (!double.IsFinite(spawnChance) || spawnChance is < 0.0 or > 1.0)
            {
                throw new InvalidDataException(
                    $"{origin}: category {row.Key} spawnChance {spawnChance} is not a probability.");
            }

            categories[i] = new LootCategoryTable(
                row.Key, row.SpawnerCount, spawnChance, row.Note ?? string.Empty, entries);

            if (row.TotalWeight != 0 && row.TotalWeight != categories[i].TotalWeight)
            {
                throw new InvalidDataException(
                    $"{origin}: {row.Key} declares totalWeight {row.TotalWeight} but its entries sum "
                    + $"to {categories[i].TotalWeight}.");
            }
        }

        DensityRow densityRow = file.Density
            ?? throw new InvalidDataException($"{origin}: no 'density' block (docs/39 §4.3).");

        var singletons = default(LootItemKindSet);
        foreach (string name in densityRow.SingletonKinds)
        {
            singletons = singletons.With(ParseKind(name, origin, "density.singletonKinds", 0));
        }

        var density = new LootDensityOptions
        {
            RoomRadiusMetres = densityRow.RoomRadiusMetres,
            RoomHeightMetres = densityRow.RoomHeightMetres,
            MaxItemsPerRoom = densityRow.MaxItemsPerRoom,
            MaxWeaponsPerRoom = densityRow.MaxWeaponsPerRoom,
            SingletonKinds = singletons,
        };
        density.Validate(origin);

        ClusterFile clusterFile = file.Clusters ?? new ClusterFile();
        if (clusterFile.Boxes.Count > 0
            && (!float.IsFinite(clusterFile.OffsetMetres) || clusterFile.OffsetMetres <= 0f))
        {
            // A zero offset would stack both boxes inside the gun's own model rather than beside it.
            throw new InvalidDataException(
                $"{origin}: clusters.offsetMetres is {clusterFile.OffsetMetres}; the two boxes have "
                + "to be placed somewhere.");
        }

        // A file that names no boxesPerGun is a pre-D272 file, and a pre-D272 file meant two.
        int boxesPerGun = clusterFile.BoxesPerGun ?? 2;
        if (boxesPerGun is < 0 or > 2)
        {
            throw new InvalidDataException(
                $"{origin}: clusters.boxesPerGun is {boxesPerGun}; only 0, 1 or 2 boxes are "
                + "placed (the geometry is a single perpendicular axis, docs/39 §5).");
        }

        var clusters = new LootClusterBox[clusterFile.Boxes.Count];
        for (int i = 0; i < clusters.Length; i++)
        {
            ClusterRow row = clusterFile.Boxes[i];
            if (row.WeaponItemDefinitionId == 0 || row.ItemDefinitionId == 0 || row.GroundModelId == 0)
            {
                throw new InvalidDataException($"{origin}: cluster {i} is missing an id.");
            }

            clusters[i] = new LootClusterBox(
                WeaponItemDefinitionId: row.WeaponItemDefinitionId,
                ItemDefinitionId: row.ItemDefinitionId,
                NameId: row.NameId,
                GroundModelId: row.GroundModelId,
                Count: Math.Max(1u, row.Count),
                Name: row.Name ?? string.Empty);
        }

        return new LootTables(
            file.Zone ?? "Z2",
            categories,
            density,
            clusters,
            clusterFile.OffsetMetres,
            clusterFile.SecondBoxYawOffsetRadians,
            boxesPerGun);
    }

    private static LootItemKind ParseKind(string? kind, string origin, string where, uint itemId)
    {
        if (Enum.TryParse(kind, ignoreCase: true, out LootItemKind parsed)
            && parsed != LootItemKind.Unknown)
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"{origin}: {where}{(itemId == 0 ? string.Empty : $" item {itemId}")} has kind "
            + $"'{kind}', which is not a {nameof(LootItemKind)}.");
    }

    private sealed class TablesFile
    {
        public string? Format { get; set; }
        public int FormatVersion { get; set; }
        public string? Zone { get; set; }
        public DensityRow? Density { get; set; }
        public ClusterFile? Clusters { get; set; }
        public List<CategoryRow> Categories { get; set; } = [];
    }

    private sealed class CategoryRow
    {
        public string? Key { get; set; }
        public int SpawnerCount { get; set; }

        /// <summary>Nullable on purpose: a missing gate must throw, not default to "never".</summary>
        public double? SpawnChance { get; set; }

        public int TotalWeight { get; set; }
        public string? Note { get; set; }
        public List<EntryRow> Entries { get; set; } = [];
    }

    private sealed class EntryRow
    {
        public uint ItemDefinitionId { get; set; }
        public uint NameId { get; set; }
        public uint GroundModelId { get; set; }
        public uint Count { get; set; }
        public int Weight { get; set; }
        public string? Kind { get; set; }
        public uint ItemClass { get; set; }
        public uint PassiveEquipSlotId { get; set; }
        public string? Name { get; set; }
    }

    private sealed class DensityRow
    {
        public float RoomRadiusMetres { get; set; }
        public float RoomHeightMetres { get; set; }
        public int MaxItemsPerRoom { get; set; }
        public int MaxWeaponsPerRoom { get; set; }
        public List<string> SingletonKinds { get; set; } = [];
    }

    private sealed class ClusterFile
    {
        public float OffsetMetres { get; set; }
        public float SecondBoxYawOffsetRadians { get; set; }

        /// <summary>Nullable on purpose: absent means the pre-D272 two, not the shipped one.</summary>
        public int? BoxesPerGun { get; set; }

        public List<ClusterRow> Boxes { get; set; } = [];
    }

    private sealed class ClusterRow
    {
        public uint WeaponItemDefinitionId { get; set; }
        public uint ItemDefinitionId { get; set; }
        public uint NameId { get; set; }
        public uint GroundModelId { get; set; }
        public uint Count { get; set; }
        public string? Name { get; set; }
    }
}

/// <summary>
/// Finds the generated loot data on disk. The files are build output-adjacent content rather than
/// embedded resources so that the odds can be retuned without a rebuild (the whole point of
/// keeping them in one editable file, docs/33 §3).
/// <para>
/// Probe order: an explicit <see cref="Override"/>, then <c>&lt;base&gt;/Data/Loot</c>, then every
/// ancestor of the base directory and of the current directory that contains
/// <c>src/Cranberry.Zone/Data/Loot</c> — which is what makes a plain <c>dotnet run</c> from the
/// repository work without a deployment step.
/// </para>
/// </summary>
public static class LootDataPaths
{
    private const string RelativeInOutput = "Data/Loot";
    private const string RelativeInRepository = "src/Cranberry.Zone/Data/Loot";

    /// <summary>Set by the host to point at an explicit data directory; null uses the probe.</summary>
    public static string? Override { get; set; }

    /// <summary>Every directory the probe will look in, in order.</summary>
    public static IEnumerable<string> Candidates()
    {
        if (!string.IsNullOrEmpty(Override))
        {
            yield return Override;
        }

        string baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, RelativeInOutput);

        foreach (string root in new[] { baseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? directory = new(root);
            while (directory is not null)
            {
                yield return Path.Combine(directory.FullName, RelativeInRepository);
                directory = directory.Parent;
            }
        }
    }

    /// <summary>First existing file of that name, or null.</summary>
    public static string? Find(string fileName)
    {
        foreach (string directory in Candidates())
        {
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>As <see cref="Find"/>, but throws with the whole probe list when nothing matches.</summary>
    public static string Require(string fileName) =>
        Find(fileName)
        ?? throw new FileNotFoundException(
            $"Loot data file '{fileName}' not found. Looked in: "
            + string.Join(", ", Candidates()),
            fileName);
}
