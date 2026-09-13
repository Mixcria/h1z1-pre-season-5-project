using Cranberry.Zone.Loot;

namespace Cranberry.Zone.Inventory;

/// <summary>
/// Item definition id -&gt; the two ids a ground object needs: the <c>Models.txt</c> row that renders
/// it (<c>AddLightweightNpc 0xd6</c>) and the <c>NAME_ID</c> the interaction prompt reads
/// (docs/13 §2).
/// <para>
/// <b>Why this is an index over the loot tables rather than a sheet join.</b> The August client has
/// no item -&gt; ground-model column: <c>ClientItemDefinitions.MODEL_NAME</c> is the item's
/// <em>attachment</em> mesh, populated on only 303 of the 2,643 rows, and the ground actor is a
/// different asset again - <c>&lt;stem&gt;_OnGround.adr</c> in <c>Models.txt</c>. Resolving those is
/// the hand-curated job <c>tools/data/gen-loot-tables.py</c> already did, item by item, with a build
/// error rather than a wrong id when a stem does not resolve (docs/39 D4). Re-deriving it here would
/// duplicate that work and could disagree with it; indexing its output cannot.
/// </para>
/// <para>
/// <b>WAVE 9 - the loot-table index is no longer the whole answer, and the old text here was wrong
/// by a factor of two and a half.</b> It read "an item that was never on the floor cannot be put
/// back on it - in practice that is the four starter garments and nothing else", and the owner's
/// session of 30 Aug refused two drops on the strength of it (items 2613 and 2144). The index holds
/// <b>72</b> item ids; <b>ten</b> items a player can hold are outside it - the four
/// <c>SurvivorStarterOutfit</c> garments <em>and every one of the six things the server itself
/// mints</em> through crafting and shred (23 Scrap of Cloth, 93 Crafted Backpack, 3375
/// Procoagulant, 3378 Makeshift Armor, 3499 Armor Scrap, 3500 Composite Fabric). That claim is
/// superseded by docs/86 SS2.1; it was only ever true of <em>looted</em> items.
/// </para>
/// <para>
/// <b>The fix is the owner's own rule, ported under D53</b> from
/// <c>C:\Z1\Server\Zone\ZoneWorldObjects.cs</c> (<c>ClothesGroundModels</c> /
/// <c>FallbackWorldModel</c>): resolve the loot-table row first, then the item's
/// <c>ITEM_CLASS</c> to the client's own folded-clothes prop, then fall back to
/// <see cref="BurlapBagModel"/>. <b>No Z1 data crosses</b> - every one of the nine model ids was
/// re-read out of the August client's own <c>Models.txt</c> and resolves at the same id to the same
/// asset (docs/86 SS2.2), so what was imported is the rule and not the values. With
/// <c>InventoryOptions.UniversalGroundActor</c> on, <see cref="TryGet(uint, bool, out uint, out uint, out GroundActorSource)"/>
/// is <b>total</b> and a drop is never refused for want of a ground actor.
/// </para>
/// </summary>
/// <summary>Which of the three tiers resolved an item's ground actor.</summary>
public enum GroundActorSource
{
    /// <summary>A real <c>z2-loot-tables.json</c> row - the item has been on this floor before.</summary>
    LootTable,

    /// <summary>
    /// The item's <c>ITEM_CLASS</c> matched <see cref="DroppedItemCatalogue.ApparelGroundModels"/>,
    /// so it lands as the client's own folded-clothes prop for that class.
    /// </summary>
    ApparelClass,

    /// <summary>An item-specific prop present in the August Models sheet.</summary>
    ItemModel,

    /// <summary>Neither - it lands as <see cref="DroppedItemCatalogue.BurlapBagModel"/>.</summary>
    BurlapBag,
}


public sealed class DroppedItemCatalogue
{
    private readonly Dictionary<uint, (uint GroundModelId, uint NameId)> _byItem = [];

    /// <summary>Index every entry of every category, plus the weapon-cluster ammunition boxes.</summary>
    public DroppedItemCatalogue(LootTables tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        foreach (LootCategoryTable category in tables.Categories)
        {
            foreach (LootTableEntry entry in category.Entries)
            {
                Add(entry.ItemDefinitionId, entry.GroundModelId, entry.NameId);
            }
        }

        foreach (LootClusterBox box in tables.Clusters)
        {
            Add(box.ItemDefinitionId, box.GroundModelId, box.NameId);
        }
    }

    /// <summary>How many distinct items can be put back on the ground.</summary>
    public int Count => _byItem.Count;

    /// <summary>Every item id this catalogue can place, ascending.</summary>
    public IEnumerable<uint> ItemDefinitionIds => _byItem.Keys.Order();

    /// <summary>
    /// <c>Models.txt</c> row <b>9</b>, <c>Common_Props_BurlapBag.adr</c> - the prop anything with no
    /// better answer lands as.
    /// <para>
    /// The RULE is the owner's, ported from <c>ZoneWorldObjects.FallbackWorldModel</c> under D53.
    /// The VALUE is not imported: his own citation for it is a file this project may not open, and
    /// row 9 of the <b>August</b> client's <c>Models.txt</c> was read directly this session and is
    /// that asset (docs/86 SS2.2). So the citation is dropped and the id stands on August evidence
    /// alone.
    /// </para>
    /// </summary>
    public const uint BurlapBagModel = 9;

    // August Models.txt row 9135: Common_Props_GasCan01.adr (not the spawner marker).
    public const uint FuelCanModel = 9135;

    /// <summary>
    /// <c>ITEM_CLASS</c> -&gt; the folded-clothes prop a dropped garment of that class becomes.
    /// Ported from <c>C:\Z1\Server\Zone\ZoneWorldObjects.cs</c> <c>ClothesGroundModels</c>
    /// (D53); <b>every id re-read from the August <c>Models.txt</c></b>, where all nine resolve at
    /// the same id to the same asset.
    /// <para>
    /// Two rows are PROVEN from the owner's own admin capture - his server's <c>d7</c>
    /// <c>actorModelId</c> when he dropped a shirt (9249) and trousers (9736). 9706 is DERIVED: it
    /// is already the resolved ground prop of items 2112-2117 in <c>z2-loot-tables.json</c>. The
    /// remaining five are his INFERENCE from this client's own model names; if a dropped glove
    /// renders as the wrong prop, this table is the one line to change (docs/86 SS5.3).
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<uint, uint> ApparelGroundModels { get; } =
        new Dictionary<uint, uint>
        {
            [25000] = 66,      // head    Common_Props_Clothes_BaseballCap.adr     INFERRED
            [25002] = 9249,    // chest   Common_Props_Clothes_FoldedShirt.adr     PROVEN
            [25003] = 9736,    // legs    Common_Props_Clothes_FoldedPants.adr     PROVEN
            [25004] = 9706,    // pack    BackpackOnGround_ManSport.adr            DERIVED
            [25005] = 10040,   // feet    Common_Props_Clothes_Jeds.adr            INFERRED
            [25008] = 9491,    // hands   Common_Props_Clothes_Gloves_Basic.adr    INFERRED
            [25040] = 10050,   // face    Common_Props_Clothes_SkiMask.adr         INFERRED
            [25045] = 9504,    // eyes    Common_Props_Clothes_GlassesBiker.adr    INFERRED
        };

    /// <summary>
    /// The ground model and prompt name for an item from a <b>loot-table row only</b>. False means
    /// the item has never been on this floor - which is <em>not</em> a reason to refuse a drop; see
    /// the overload.
    /// </summary>
    public bool TryGet(uint itemDefinitionId, out uint groundModelId, out uint nameId) =>
        TryGet(itemDefinitionId, universalFallback: false, out groundModelId, out nameId, out _);

    /// <summary>
    /// The ground actor an item becomes when it is dropped, in the owner's own three-tier order
    /// (D53, <c>ZoneWorldObjects</c>): the loot-table row, then the folded-clothes prop for its
    /// <c>ITEM_CLASS</c>, then the burlap bag.
    /// <para>
    /// With <paramref name="universalFallback"/> true this <b>never returns false for a real item
    /// row</b>, which is the whole point: an item the player is holding is by definition an item the
    /// player can put down. False can still come back for a definition id that is not a
    /// <c>ClientItemDefinitions</c> row at all, because a prompt with no <c>NAME_ID</c> would draw
    /// an unnamed object.
    /// </para>
    /// <para>
    /// With it false the behaviour is exactly wave 8's, refusal included - that is what the
    /// <c>InventoryOptions.UniversalGroundActor</c> revert switch buys.
    /// </para>
    /// </summary>
    public bool TryGet(
        uint itemDefinitionId,
        bool universalFallback,
        out uint groundModelId,
        out uint nameId,
        out GroundActorSource source)
    {
        if (itemDefinitionId is 73 or 1384 && InventoryItemFacts.TryGet(itemDefinitionId, out var fuel))
        {
            groundModelId = FuelCanModel;
            nameId = fuel.NameId;
            source = GroundActorSource.ItemModel;
            return true;
        }
        if (_byItem.TryGetValue(itemDefinitionId, out (uint GroundModelId, uint NameId) row))
        {
            groundModelId = row.GroundModelId;
            nameId = row.NameId;
            source = GroundActorSource.LootTable;
            return true;
        }

        groundModelId = 0;
        nameId = 0;
        source = GroundActorSource.LootTable;
        if (!universalFallback || !InventoryItemFacts.TryGet(itemDefinitionId, out InventoryItemFact fact))
        {
            return false;
        }

        // The prompt still has to name the thing on the floor, and the item sheet is where its
        // NAME_ID lives - 2144 -> 8947 "Tan Shirt", 2613 -> 12610 "Brown Boots".
        nameId = fact.NameId;
        if (ApparelGroundModels.TryGetValue(fact.ItemClass, out uint garment))
        {
            groundModelId = garment;
            source = GroundActorSource.ApparelClass;
            return true;
        }

        groundModelId = BurlapBagModel;
        source = GroundActorSource.BurlapBag;
        return true;
    }

    private void Add(uint itemDefinitionId, uint groundModelId, uint nameId)
    {
        if (itemDefinitionId == 0 || groundModelId == 0)
        {
            return;
        }

        // First writer wins: the category tables and the cluster list name the same ammunition rows,
        // and LootTables.Parse has already rejected any row whose model does not resolve.
        _ = _byItem.TryAdd(itemDefinitionId, (groundModelId, nameId));
    }
}
