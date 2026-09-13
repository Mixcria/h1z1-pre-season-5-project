using System.Text.Json;
using Cranberry.Zone.Gas;

namespace Cranberry.Zone.Loot;

/// <summary>
/// One item inside an airdrop crate. The same two client ids the ground roster carries
/// (<see cref="ItemDefinitionId"/> for <c>ClientUpdate.ItemAdd</c>, <see cref="GroundModelId"/> for
/// <c>AddLightweightNpc</c>), because an opened crate lays its contents on the floor and every one
/// of them is then an ordinary ground pickup.
/// </summary>
public readonly record struct AirdropItem(
    uint ItemDefinitionId,
    uint NameId,
    uint GroundModelId,
    uint Count,
    LootItemKind Kind,
    string Name);

/// <summary>
/// <b>One bundle — the unit a crate deals in.</b> A gun and its ammunition are one bundle, so the
/// draw can never separate them: <i>"a gun without its pair is an inert prop"</i> is docs/39 §5's
/// rule for the floor and it is no less true inside a crate.
/// </summary>
/// <param name="Weight">Relative odds inside the pool. Ignored for the guaranteed and rifle sets.</param>
/// <param name="Items">Everything this bundle hands over, in file order.</param>
/// <param name="Why">The justification carried out of the generated file, so the table audits itself.</param>
public sealed record AirdropBundle(int Weight, IReadOnlyList<AirdropItem> Items, string Why);

/// <summary>
/// The crate itself, entirely in the client's own ids.
/// </summary>
/// <param name="ItemDefinitionId">1501 "Military Crate", <c>ITEM_CLASS</c> 25016 (World Container).</param>
/// <param name="NameId">1344 — the crate's own display name, and the noun the banners use.</param>
/// <param name="ContainerDefinitionId">51: 100 slots, withdrawal-only, removed when empty.</param>
/// <param name="GroundModelId">9218 <c>Common_Props_MilitaryCrate.adr</c>, <i>"Crate.Military for air drops."</i></param>
/// <param name="DescendingModelId">9219 <c>..._Parachute.adr</c>, <i>"Crate.Military.Parachute for air drops."</i>
/// Native delivery renders this canopy/cord actor alongside a separate 9218 crate-body rail.</param>
/// <param name="PlaneModelId">9215 <c>Vehicle_C130.adr</c>, <i>"AirDropC130"</i>, used for both supply and bomber routes.</param>
public sealed record AirdropCrate(
    uint ItemDefinitionId,
    uint NameId,
    uint ItemClass,
    uint ContainerDefinitionId,
    uint GroundModelId,
    uint DescendingModelId,
    uint PlaneModelId,
    string Name);

/// <summary>
/// <b>What is in an airdrop crate</b> — <c>z2-airdrop.json</c>, format <c>cranberry.airdrop</c> v1,
/// written by <c>tools/data/gen-loot-tables.py</c> from the client's own
/// <c>ClientItemDefinitions.txt</c> and <c>Models.txt</c> under the same D3/D4/D5 rules as the
/// ground tables.
///
/// <para><b>Why its own file and not another category in <c>z2-loot-tables.json</c>.</b> The ground
/// roster's tests say <i>"nothing outside these 72 ids may reach the floor"</i>, and the crate's
/// whole point is to be the one place two of the excluded ids — the .308 rifle and its round —
/// legitimately appear. Folding it into the same document would have made that assertion say
/// something weaker, which is the opposite of what it is for.</para>
///
/// <para><b>Roll shape.</b> Every crate gets <see cref="Guaranteed"/> whole; it gets
/// <see cref="Rifle"/> whole with probability <c>AirdropOptions.RifleChance</c>; and it draws
/// <c>AirdropOptions.PoolDraws</c> bundles from <see cref="Pool"/> by weight. All three draws are
/// pure functions of <c>(matchSeed, dropIndex)</c>, so the same seed delivers the same crates.</para>
/// </summary>
public sealed class AirdropTables
{
    /// <summary>The only format string this loader accepts.</summary>
    public const string FormatName = "cranberry.airdrop";

    /// <summary>The only format version this loader accepts.</summary>
    public const int FormatVersion = 1;

    /// <summary>Default file name inside <see cref="LootDataPaths"/>' directory.</summary>
    public const string DefaultFileName = "z2-airdrop.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly AirdropBundle[] _pool;
    private readonly int[] _cumulativeWeight;

    private AirdropTables(
        string zone,
        AirdropCrate crate,
        AirdropBundle[] guaranteed,
        AirdropBundle[] rifle,
        AirdropBundle[] pool)
    {
        Zone = zone;
        Crate = crate;
        Guaranteed = guaranteed;
        Rifle = rifle;
        _pool = pool;

        _cumulativeWeight = new int[pool.Length];
        int running = 0;
        for (int i = 0; i < pool.Length; i++)
        {
            running += Math.Max(0, pool[i].Weight);
            _cumulativeWeight[i] = running;
        }

        TotalPoolWeight = running;
    }

    /// <summary>The zone these tables were generated for (<c>Z2</c>).</summary>
    public string Zone { get; }

    /// <summary>The crate's client ids.</summary>
    public AirdropCrate Crate { get; }

    /// <summary>Handed to every crate. The Laminated Armor lives here — retail's one guarantee.</summary>
    public IReadOnlyList<AirdropBundle> Guaranteed { get; }

    /// <summary>Handed over whole on a <c>RifleChance</c> hit: the .308 and its rounds.</summary>
    public IReadOnlyList<AirdropBundle> Rifle { get; }

    /// <summary>The weighted bundles a crate draws from.</summary>
    public IReadOnlyList<AirdropBundle> Pool => _pool;

    /// <summary>Sum of every pool bundle's weight. Zero means the pool draws nothing.</summary>
    public int TotalPoolWeight { get; }

    /// <summary>Loads the airdrop table shipped beside the server.</summary>
    public static AirdropTables LoadDefault() => Load(LootDataPaths.Require(DefaultFileName));

    /// <summary>Loads and validates one airdrop file.</summary>
    public static AirdropTables Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllBytes(path), path);
    }

    /// <summary>Parses an in-memory copy — the form the tests use.</summary>
    public static AirdropTables Parse(ReadOnlySpan<byte> json, string origin)
    {
        AirdropFile file = JsonSerializer.Deserialize<AirdropFile>(json, JsonOptions)
            ?? throw new InvalidDataException($"{origin}: empty airdrop document.");

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

        CrateRow crateRow = file.Crate
            ?? throw new InvalidDataException($"{origin}: no 'crate' block.");

        if (crateRow.ItemDefinitionId == 0 || crateRow.GroundModelId == 0)
        {
            throw new InvalidDataException(
                $"{origin}: the crate has no item definition or no ground model; there would be "
                + "nothing to put in the world.");
        }

        var crate = new AirdropCrate(
            ItemDefinitionId: crateRow.ItemDefinitionId,
            NameId: crateRow.NameId,
            ItemClass: crateRow.ItemClass,
            ContainerDefinitionId: crateRow.ContainerDefinitionId,
            GroundModelId: crateRow.GroundModelId,
            DescendingModelId: crateRow.DescendingModelId,
            PlaneModelId: crateRow.PlaneModelId,
            Name: crateRow.Name ?? string.Empty);

        AirdropBundle[] guaranteed = ReadBundles(file.Guaranteed, origin, "guaranteed");
        AirdropBundle[] rifle = ReadBundles(file.Rifle, origin, "rifle");
        AirdropBundle[] pool = ReadBundles(file.Pool, origin, "pool");

        // An empty pool is legal (a crate of only its guaranteed set); a pool whose weights all sum
        // to zero is not, because PoolDraws would then silently draw nothing at all.
        if (pool.Length > 0 && pool.Sum(b => Math.Max(0, b.Weight)) <= 0)
        {
            throw new InvalidDataException(
                $"{origin}: the airdrop pool has {pool.Length} bundle(s) and no weight between them.");
        }

        return new AirdropTables(file.Zone ?? "Z2", crate, guaranteed, rifle, pool);
    }

    /// <summary>
    /// Picks one pool bundle for <paramref name="seed"/>. Returns false for an empty pool — a real
    /// case, not an error: a crate of only its guaranteed set is a legal table.
    /// </summary>
    public bool RollPool(ulong seed, out AirdropBundle bundle)
    {
        if (TotalPoolWeight <= 0 || _pool.Length == 0)
        {
            bundle = null!;
            return false;
        }

        var random = new GasRandom(seed);
        int pick = (int)(random.NextUInt64() % (ulong)TotalPoolWeight);
        for (int i = 0; i < _cumulativeWeight.Length; i++)
        {
            if (pick < _cumulativeWeight[i])
            {
                bundle = _pool[i];
                return true;
            }
        }

        bundle = _pool[^1];
        return true;
    }

    private static AirdropBundle[] ReadBundles(List<BundleRow>? rows, string origin, string where)
    {
        if (rows is null)
        {
            return [];
        }

        var bundles = new AirdropBundle[rows.Count];
        for (int i = 0; i < bundles.Length; i++)
        {
            BundleRow row = rows[i];
            var items = new AirdropItem[row.Items.Count];
            for (int e = 0; e < items.Length; e++)
            {
                ItemRow item = row.Items[e];
                if (item.ItemDefinitionId == 0 || item.GroundModelId == 0)
                {
                    throw new InvalidDataException(
                        $"{origin}: {where} bundle {i} item {e} has no item definition or ground model id.");
                }

                if (!Enum.TryParse(item.Kind, ignoreCase: true, out LootItemKind kind)
                    || kind == LootItemKind.Unknown)
                {
                    throw new InvalidDataException(
                        $"{origin}: {where} bundle {i} item {item.ItemDefinitionId} has kind "
                        + $"'{item.Kind}', which is not a {nameof(LootItemKind)}.");
                }

                items[e] = new AirdropItem(
                    ItemDefinitionId: item.ItemDefinitionId,
                    NameId: item.NameId,
                    GroundModelId: item.GroundModelId,
                    Count: Math.Max(1u, item.Count),
                    Kind: kind,
                    Name: item.Name ?? string.Empty);
            }

            if (items.Length == 0)
            {
                throw new InvalidDataException($"{origin}: {where} bundle {i} is empty.");
            }

            bundles[i] = new AirdropBundle(row.Weight, items, row.Why ?? string.Empty);
        }

        return bundles;
    }

    private sealed class AirdropFile
    {
        public string? Format { get; set; }
        public int FormatVersion { get; set; }
        public string? Zone { get; set; }
        public CrateRow? Crate { get; set; }
        public List<BundleRow>? Guaranteed { get; set; }
        public List<BundleRow>? Rifle { get; set; }
        public List<BundleRow>? Pool { get; set; }
    }

    private sealed class CrateRow
    {
        public uint ItemDefinitionId { get; set; }
        public uint NameId { get; set; }
        public uint ItemClass { get; set; }
        public uint ContainerDefinitionId { get; set; }
        public uint GroundModelId { get; set; }
        public uint DescendingModelId { get; set; }
        public uint PlaneModelId { get; set; }
        public string? Name { get; set; }
    }

    private sealed class BundleRow
    {
        public int Weight { get; set; }
        public string? Why { get; set; }
        public List<ItemRow> Items { get; set; } = [];
    }

    private sealed class ItemRow
    {
        public uint ItemDefinitionId { get; set; }
        public uint NameId { get; set; }
        public uint GroundModelId { get; set; }
        public uint Count { get; set; }
        public string? Kind { get; set; }
        public string? Name { get; set; }
    }
}
