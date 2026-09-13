using Cranberry.Zone.Generated;
using Cranberry.Zone.Inventory;

namespace Cranberry.Zone.Crafting;

/// <summary>
/// One ingredient of a Cranberry recipe: an item definition id and how many of it a single craft
/// consumes. In the <b>retail</b> set the quantity is a decoded capture byte (D256); in the legacy
/// set kept behind <c>CRANBERRY_CRAFT_RECIPES=0</c> it is DESIGN - see <see cref="CraftingCatalog"/>.
/// </summary>
/// <param name="ItemDefinitionId">A <c>ClientItemDefinitions</c> row id.</param>
/// <param name="Quantity">How many one craft consumes. Must be at least 1.</param>
public readonly record struct RecipeIngredient(uint ItemDefinitionId, uint Quantity);

/// <summary>
/// The three display columns of an item's own <c>ClientItemDefinitions</c> row that the recipe and
/// component records carry. <c>NAME_ID</c> is not here - <see cref="InventoryItemFacts"/> already
/// has it.
/// </summary>
/// <param name="ImageSetId"><c>IMAGE_SET_ID</c>.</param>
/// <param name="DescriptionStringId"><c>DESCRIPTION_ID</c>.</param>
/// <param name="LocateDescriptionStringId"><c>LOCATE_DESCRIPTION_ID</c>, the "where do I find this"
/// line. Components carry it; the recipe record has no field for it.</param>
public readonly record struct RecipeItemDisplay(
    uint ImageSetId,
    uint DescriptionStringId,
    uint LocateDescriptionStringId);

/// <summary>
/// The display columns for the fifteen items the retail recipe set names, out of the client's own
/// <c>ClientItemDefinitions.txt</c> (<c>rulings/crafting.json</c>, grade <b>CLIENT</b>).
/// <para>
/// <b>Why this table exists.</b> The friend's server fills every display field of the
/// <c>26 09 Recipe.List</c> record, and every value it writes is that item's own catalogue row -
/// <c>IMAGE_SET_ID</c>, <c>DESCRIPTION_ID</c> and, on components, <c>LOCATE_DESCRIPTION_ID</c>.
/// That is what makes the captured 936-byte packet reproducible from <em>client data plus the
/// captured counts</em> rather than from pinned bytes. <see cref="InventoryItemFacts"/> carries only
/// nine of the sheet's columns and none of these three, so the fifteen rows the recipe set needs
/// come through the rulings file instead of through a second generator run.
/// </para>
/// </summary>
public static class RecipeDisplayFacts
{
    private static readonly IReadOnlyDictionary<uint, RecipeItemDisplay> Rows = Build();

    /// <summary>The item's display columns, or all-zero for an item the table does not carry.</summary>
    public static RecipeItemDisplay For(uint itemDefinitionId) =>
        Rows.TryGetValue(itemDefinitionId, out RecipeItemDisplay display) ? display : default;

    /// <summary>Every item id the table carries, in ascending order.</summary>
    public static IReadOnlyList<uint> Items => Rulings.Crafting.RecipeDisplayItemIds;

    private static Dictionary<uint, RecipeItemDisplay> Build()
    {
        uint[] ids = Rulings.Crafting.RecipeDisplayItemIds;
        uint[] imageSets = Rulings.Crafting.RecipeDisplayImageSetIds;
        uint[] descriptions = Rulings.Crafting.RecipeDisplayDescriptionStringIds;
        uint[] locates = Rulings.Crafting.RecipeDisplayLocateDescriptionStringIds;
        var rows = new Dictionary<uint, RecipeItemDisplay>(ids.Length);
        for (int index = 0; index < ids.Length; index++)
        {
            rows[ids[index]] = new RecipeItemDisplay(
                imageSets[index], descriptions[index], locates[index]);
        }

        return rows;
    }
}

/// <summary>
/// One recipe as Cranberry models it, before it is projected onto the wire.
/// </summary>
/// <param name="OutputItemDefinitionId">The item the craft produces.</param>
/// <param name="OutputCount">
/// How many of it one completed craft <b>grants</b>. This is the wire's <c>BundleCount</c> on five
/// of the six retail recipes and deliberately differs on the sixth - see
/// <see cref="WireBundleCount"/>.
/// </param>
/// <param name="Ingredients">At most four - the window renders four ingredient slots.</param>
/// <param name="SortOrdinal">The crafting tab's sort key.</param>
/// <param name="BusyMilliseconds">
/// How long one crafted unit occupies the player. <b>No client datasheet carries a craft
/// duration</b> (the 1,000 ms in <c>ItemUseOptions</c> is the <em>shred</em> verb's
/// <c>BUSY_MSEC</c>, not a recipe's) and the friend's capture contains no craft, so the retail set
/// takes the owner's own 1,000 ms per unit under D53 (D257) and the legacy set keeps its four
/// wave-6 design numbers.
/// </param>
public sealed record RecipeDefinition(
    uint OutputItemDefinitionId,
    uint OutputCount,
    IReadOnlyList<RecipeIngredient> Ingredients,
    uint SortOrdinal,
    int BusyMilliseconds)
{
    /// <summary>
    /// <c>rec+0x28 BundleCount</c> when it is not simply <see cref="OutputCount"/>. It is set on
    /// exactly one recipe: <b>Flaming Arrow (1434) carries BundleCount 0 on the wire</b> in both
    /// captured packets, and a recipe that produces zero is not a recipe, so the wire keeps its 0
    /// (byte-for-byte parity) while a completed craft grants the owner's own 5
    /// (<c>ZoneCrafting.cs:48</c>, D53). Null means "the same as <see cref="OutputCount"/>".
    /// </summary>
    public uint? WireBundleCount { get; init; }

    /// <summary><c>rec+0x24</c>. 1 on all six retail records; 0 on the legacy four, which is what
    /// Cranberry wrote before the capture was decoded.</summary>
    public uint Reserved { get; init; }

    /// <summary>
    /// Fill <c>ItemImageSetId</c>, <c>ItemDescriptionStringId</c>, the component
    /// <c>ItemLocateDescriptionStringId</c> and the component <c>RecipeType</c> from the client's
    /// own rows, the way the friend's server does. Off on the legacy four so that
    /// <c>CRANBERRY_CRAFT_RECIPES=0</c> reproduces the old 538-byte packet exactly.
    /// </summary>
    public bool RetailDisplayFields { get; init; }

    /// <summary>What <c>rec+0x28</c> actually carries.</summary>
    public uint BundleCount => WireBundleCount ?? OutputCount;

    /// <summary>
    /// The recipe id. Cranberry uses the <b>output item's definition id</b>, which is a decision
    /// the binary supports rather than merely permits: <c>FUN_1415126a0</c> emits
    /// <c>RecipeId</c> and <c>ItemId</c> as separate columns from separate fields, so they need not
    /// agree - but <c>FUN_141518250</c> arm 3 refuses <c>recipeId &lt; 1</c>, the id is the hash key
    /// (<c>&amp; 0x3f</c>), and it is the only value <c>09 1a Command.RecipeStart</c> sends back. An
    /// id that is already unique, already non-zero and already meaningful costs nothing and makes
    /// the craft request self-describing in the host log.
    /// <para>
    /// The capture confirms the choice: <c>RecipeId == OutputItemDefinitionId</c> on all six of the
    /// friend's records (D256).
    /// </para>
    /// </summary>
    public uint RecipeId => OutputItemDefinitionId;

    /// <summary>Project to the wire record. With <see cref="RetailDisplayFields"/> off the display
    /// fields stay zero and the client resolves the icon, name and bulk from its own
    /// <c>ClientItemDefinitions</c> row for
    /// <see cref="RecipeRecord.OutputItemDefinitionId"/>, which <c>FUN_140db5680</c> publishes into
    /// the <c>Items</c> datasource for exactly that purpose.</summary>
    public RecipeRecord ToRecord(bool sentinelFields = false)
    {
        if (sentinelFields)
        {
            return ToSentinelRecord();
        }

        var components = new List<RecipeComponentRecord>(Ingredients.Count);
        foreach (RecipeIngredient ingredient in Ingredients)
        {
            InventoryItemFacts.TryGet(ingredient.ItemDefinitionId, out InventoryItemFact fact);
            RecipeItemDisplay display = RetailDisplayFields
                ? RecipeDisplayFacts.For(ingredient.ItemDefinitionId)
                : default;
            components.Add(new RecipeComponentRecord
            {
                Key = ingredient.ItemDefinitionId,
                ItemNameStringId = fact.NameId,
                ItemImageSetId = display.ImageSetId,
                ItemDescriptionStringId = display.DescriptionStringId,
                ItemLocateDescriptionStringId = display.LocateDescriptionStringId,
                RequiredCount = ingredient.Quantity,
                RecipeType = RetailDisplayFields ? Rulings.Crafting.RetailComponentRecipeType : 0,
                ItemDefinitionId = ingredient.ItemDefinitionId,
            });
        }

        InventoryItemFacts.TryGet(OutputItemDefinitionId, out InventoryItemFact output);
        RecipeItemDisplay self = RetailDisplayFields
            ? RecipeDisplayFacts.For(OutputItemDefinitionId)
            : default;
        return new RecipeRecord
        {
            RecipeId = RecipeId,
            ItemNameStringId = output.NameId,
            ItemImageSetId = self.ImageSetId,
            ItemDescriptionStringId = self.DescriptionStringId,
            Reserved = Reserved,
            BundleCount = BundleCount,
            SortOrdinal = SortOrdinal,
            Components = components,
            OutputItemDefinitionId = OutputItemDefinitionId,
        };
    }

    /// <summary>
    /// The same recipe with every field that has no proven role set to a distinct recognisable
    /// number instead of zero (<see cref="CraftingOptions.SentinelFields"/>).
    /// <para>
    /// docs/59 §1.7 needle 1 proposed a sentinel run to identify the record's fields. The binary
    /// answered that question first (<see cref="RecipeRecord"/>) and the capture has since answered
    /// the rest (D256), so this mode is no longer needed for the named fields - it survives as a
    /// falsification tool: if the panel renders sentinels where it should render real values, the
    /// mapping above is wrong and the number on screen names the field that is wrong.
    /// </para>
    /// </summary>
    public RecipeRecord ToSentinelRecord()
    {
        var components = new List<RecipeComponentRecord>(Ingredients.Count);
        uint index = 0;
        foreach (RecipeIngredient ingredient in Ingredients)
        {
            components.Add(new RecipeComponentRecord
            {
                Key = ingredient.ItemDefinitionId,
                ItemNameStringId = 0xB1,
                ItemImageSetId = 0xB2,
                Reserved = 0xB3,
                ItemDescriptionStringId = 0xB4,
                ItemLocateDescriptionStringId = 0xB5,
                RequiredCount = ingredient.Quantity,
                LiveCounts = RecipeComponentRecord.PackLiveCounts(0xB6, 0xB7),
                RecipeType = 0xB8,
                ItemDefinitionId = ingredient.ItemDefinitionId,
            });
            index++;
        }

        return new RecipeRecord
        {
            RecipeId = RecipeId,
            ItemNameStringId = 0xA1,
            ItemImageSetId = 0xA2,
            ItemTintValue = 0xA3,
            ItemDescriptionStringId = 0xA4,
            Reserved = 0xA5,
            BundleCount = 0xA6,
            SortOrdinal = 0xA7 + index,
            MembersOnly = false,
            FilterType = 0xA8,
            Components = components,
            OutputItemDefinitionId = OutputItemDefinitionId,
        };
    }
}

/// <summary>
/// <b>The one editable place.</b> The six King-of-the-Kill recipes retail August actually ships, and
/// the four Cranberry used to ship in their place.
/// <para>
/// <b>D256 - the recipe set is decoded capture bytes, not design.</b> docs/59 §1.1's premise is
/// still true (the client ships no recipe data at all: no datasheet is a recipe sheet, and no item
/// in the 2,643-row catalogue carries <c>DATASHEET_ID</c> 25) but D47's conclusion from it - that
/// every quantity must therefore be a Cranberry design value - is not, because the recipes are
/// <em>server</em>-authored and the owner captured a server authoring them.
/// <c>C:\Project\out\ingest-admin-20260822-part1\ops\cPacketIdRecipeBase.txt</c> holds two identical
/// <b>936-byte</b> <c>26 09 Recipe.List</c> packets, and docs/62 §2/§2b's August-derived record
/// layout consumes <b>936 of 936 bytes</b> of them. Four independent self-checks say the decode is
/// right: <c>SortOrdinal</c> is exactly a permutation of 1…6, <c>RecipeId == OutputItemDefinitionId</c>
/// on all six rows, every component's hash key equals its <c>ItemId</c>, and every string id on the
/// wire equals that item's own <b>August</b> <c>ClientItemDefinitions</c> column. So one decode does
/// two jobs: it confirms the record layout from live bytes, and it recovers the set.
/// </para>
/// <para>
/// <b>What changed against the wave-6 four.</b> Field Bandage costs 2 cloth, not 1; Procoagulant is
/// 10 bandages + 1 first aid kit, not 2 bandages + tape; Makeshift Armor takes 4 composite fabric,
/// not 3 - which is exactly the widely repeated public figure D47 declined as unconfirmable, now
/// confirmed. There is <b>no</b> recipe for item 93 Crafted Backpack: retail's container recipe is
/// the <b>Satchel (2125) for 6 cloth</b>. Explosive Arrow (138), Flaming Arrow (1434) and the
/// Satchel were missing entirely.
/// </para>
/// <para>
/// <b>What is still design.</b> The craft duration (D257), the craft animation (D257) and Flaming
/// Arrow's granted count of 5 against the wire's 0 (D53, <c>ZoneCrafting.cs:48</c>). Everything else
/// below is either a byte in the capture or a column of the client's own item sheet.
/// </para>
/// <para>
/// <c>CRANBERRY_CRAFT_RECIPES=0</c> restores <see cref="LegacyRecipes"/> - the wave-6 four, byte for
/// byte, display fields and all.
/// </para>
/// </summary>
public static class CraftingCatalog
{
    // --- item ids, all resolved through item-names-en_us.json (docs/59 §1.4.3) -----------------

    /// <summary>Output. "Reduces bleeding. Minor heal over time." Retail: 2 × Scrap of Cloth.</summary>
    public const uint FieldBandage = Rulings.Crafting.FieldBandageItemId;

    /// <summary>Output. "Stops all bleeding." Retail: 10 × Field Bandage + 1 × Tactical First Aid Kit.</summary>
    public const uint Procoagulant = Rulings.Crafting.ProcoagulantItemId;

    /// <summary>Output. "Minor damage protection." Item class 25041, which is also the one class
    /// that can never be shredded.</summary>
    public const uint MakeshiftArmor = Rulings.Crafting.MakeshiftArmorItemId;

    /// <summary>Output. "Slightly increases carry capacity." Retail's container recipe, 6 × cloth.</summary>
    public const uint Satchel = Rulings.Crafting.SatchelItemId;

    /// <summary>Output. "Explodes on impact." The one bundled recipe: <c>BundleCount</c> 5.</summary>
    public const uint ExplosiveArrow = Rulings.Crafting.ExplosiveArrowItemId;

    /// <summary>Output. "Starts a fire on impact." <c>BundleCount</c> <b>0</b> on the wire.</summary>
    public const uint FlamingArrow = Rulings.Crafting.FlamingArrowItemId;

    /// <summary>
    /// <b>Legacy only.</b> "This framed backpack can hold a great deal of items." Item 93 exists in
    /// the catalogue but retail August has <em>no recipe for it</em> - D256 supersedes D47 here.
    /// </summary>
    public const uint CraftedBackpack = Rulings.Crafting.CraftedBackpackItemId;

    /// <summary>Input. "Shred cloth items to get scrap cloth."</summary>
    public const uint ScrapOfCloth = Rulings.Crafting.ScrapOfClothItemId;

    /// <summary>Input. Loot-reachable today (<c>z2-loot-tables.json</c>).</summary>
    public const uint DuctTape = Rulings.Crafting.DuctTapeItemId;

    /// <summary>Input. "It is found as the remnant of destroyed helmets."</summary>
    public const uint ArmorScrap = Rulings.Crafting.ArmorScrapItemId;

    /// <summary>Input. "Shred large backpacks to get this fabric."</summary>
    public const uint CompositeFabric = Rulings.Crafting.CompositeFabricItemId;

    /// <summary>Input to Flaming Arrow.</summary>
    public const uint MolotovCocktail = Rulings.Crafting.MolotovCocktailItemId;

    /// <summary>Input to Explosive Arrow.</summary>
    public const uint FragGrenade = Rulings.Crafting.FragGrenadeItemId;

    /// <summary>Input to both arrow recipes.</summary>
    public const uint WoodenArrow = Rulings.Crafting.WoodenArrowItemId;

    /// <summary>Input to both arrow recipes. "…can be used to make explosive arrows."</summary>
    public const uint BuckshotShell = Rulings.Crafting.BuckshotShellItemId;

    /// <summary>Input to Procoagulant.</summary>
    public const uint FirstAidKit = Rulings.Crafting.FirstAidKitItemId;

    /// <summary>A shred yield, not a recipe input: what a salvaged round becomes (D260).</summary>
    public const uint Gunpowder = Rulings.Crafting.GunpowderItemId;

    /// <summary>A shred yield, not a recipe input: what a Broken Wooden Item becomes (D260).</summary>
    public const uint WoodStick = Rulings.Crafting.WoodStickItemId;

    /// <summary>
    /// <b>The retail six</b>, in the order the friend's server writes them onto the wire - which is
    /// NOT sort order, and which is what <see cref="ToRecords(bool, bool)"/> has to preserve for the
    /// 936-byte packet to be reproduced byte for byte. The client sorts the tab itself from
    /// <see cref="RecipeDefinition.SortOrdinal"/>.
    /// </summary>
    public static IReadOnlyList<RecipeDefinition> RetailRecipes { get; } = BuildRetail();

    /// <summary>
    /// <b>The wave-6 four</b>, unchanged, kept solely so <c>CRANBERRY_CRAFT_RECIPES=0</c> is an exact
    /// revert. Every quantity and duration here is DESIGN (D47); the set was derived from the
    /// client's own SKU column and FTE table (docs/59 §1.4.1-§1.4.2) and is a reasonable game, but it
    /// is not what retail August shipped.
    /// </summary>
    public static IReadOnlyList<RecipeDefinition> LegacyRecipes { get; } =
    [
        new RecipeDefinition(
            OutputItemDefinitionId: FieldBandage,
            OutputCount: Rulings.Crafting.LegacyRecipeOutputCount,
            Ingredients: Pairs(Rulings.Crafting.LegacyFieldBandageIngredients),
            SortOrdinal: 1,
            BusyMilliseconds: Rulings.Crafting.FieldBandageBusyMs),

        new RecipeDefinition(
            OutputItemDefinitionId: Procoagulant,
            OutputCount: Rulings.Crafting.LegacyRecipeOutputCount,
            Ingredients: Pairs(Rulings.Crafting.LegacyProcoagulantIngredients),
            SortOrdinal: 2,
            BusyMilliseconds: Rulings.Crafting.ProcoagulantBusyMs),

        new RecipeDefinition(
            OutputItemDefinitionId: MakeshiftArmor,
            OutputCount: Rulings.Crafting.LegacyRecipeOutputCount,
            Ingredients: Pairs(Rulings.Crafting.LegacyMakeshiftArmorIngredients),
            SortOrdinal: 3,
            BusyMilliseconds: Rulings.Crafting.MakeshiftArmorBusyMs),

        new RecipeDefinition(
            OutputItemDefinitionId: CraftedBackpack,
            OutputCount: Rulings.Crafting.LegacyRecipeOutputCount,
            Ingredients: Pairs(Rulings.Crafting.LegacyCraftedBackpackIngredients),
            SortOrdinal: 4,
            BusyMilliseconds: Rulings.Crafting.CraftedBackpackBusyMs),
    ];

    /// <summary>The shipped set: the retail six.</summary>
    public static IReadOnlyList<RecipeDefinition> Recipes => RetailRecipes;

    /// <summary>The set <paramref name="retailRecipes"/> selects.</summary>
    public static IReadOnlyList<RecipeDefinition> For(bool retailRecipes) =>
        retailRecipes ? RetailRecipes : LegacyRecipes;

    /// <summary>Retail recipes by id (which is the output item's definition id).</summary>
    public static IReadOnlyDictionary<uint, RecipeDefinition> RetailById { get; } =
        RetailRecipes.ToDictionary(r => r.RecipeId);

    /// <summary>Legacy recipes by id.</summary>
    public static IReadOnlyDictionary<uint, RecipeDefinition> LegacyById { get; } =
        LegacyRecipes.ToDictionary(r => r.RecipeId);

    /// <summary>The shipped set, by id.</summary>
    public static IReadOnlyDictionary<uint, RecipeDefinition> ById => RetailById;

    /// <summary>The set <paramref name="retailRecipes"/> selects, by id.</summary>
    public static IReadOnlyDictionary<uint, RecipeDefinition> ByIdFor(bool retailRecipes) =>
        retailRecipes ? RetailById : LegacyById;

    /// <summary>True when <paramref name="recipeId"/> names one of the shipped recipes.</summary>
    public static bool IsKnown(uint recipeId) => RetailById.ContainsKey(recipeId);

    /// <summary>True when <paramref name="recipeId"/> names a recipe of the selected set.</summary>
    public static bool IsKnownIn(uint recipeId, bool retailRecipes) =>
        ByIdFor(retailRecipes).ContainsKey(recipeId);

    /// <summary>Every item definition id that appears as an output or an ingredient, either set.</summary>
    public static IReadOnlyList<uint> ReferencedItems { get; } =
        RetailRecipes.Concat(LegacyRecipes)
            .SelectMany(r => r.Ingredients.Select(i => i.ItemDefinitionId).Append(r.OutputItemDefinitionId))
            .Distinct()
            .Order()
            .ToArray();

    /// <summary>Project the shipped catalogue to wire records, in emission order.</summary>
    public static IReadOnlyList<RecipeRecord> ToRecords(bool sentinelFields = false) =>
        ToRecords(retailRecipes: true, sentinelFields);

    /// <summary>Project the selected catalogue to wire records, in emission order.</summary>
    public static IReadOnlyList<RecipeRecord> ToRecords(bool retailRecipes, bool sentinelFields)
    {
        IReadOnlyList<RecipeDefinition> source = For(retailRecipes);
        var records = new List<RecipeRecord>(source.Count);
        foreach (RecipeDefinition recipe in source)
        {
            records.Add(recipe.ToRecord(sentinelFields));
        }

        return records;
    }

    /// <summary>Unflatten a rulings <c>[itemId, quantity, …]</c> row.</summary>
    internal static RecipeIngredient[] Pairs(uint[] flattened)
    {
        var ingredients = new RecipeIngredient[flattened.Length / 2];
        for (int index = 0; index < ingredients.Length; index++)
        {
            ingredients[index] = new RecipeIngredient(flattened[index * 2], flattened[(index * 2) + 1]);
        }

        return ingredients;
    }

    private static RecipeDefinition[] BuildRetail()
    {
        uint[] order = Rulings.Crafting.RetailRecipeWireOrder;
        uint[] sortOrdinals = Rulings.Crafting.RetailRecipeSortOrdinals;
        uint[] bundleCounts = Rulings.Crafting.RetailRecipeBundleCounts;
        uint[] grantedCounts = Rulings.Crafting.RetailRecipeOutputCounts;
        var ingredients = new Dictionary<uint, uint[]>
        {
            [Satchel] = Rulings.Crafting.SatchelIngredients,
            [FlamingArrow] = Rulings.Crafting.FlamingArrowIngredients,
            [MakeshiftArmor] = Rulings.Crafting.MakeshiftArmorIngredients,
            [ExplosiveArrow] = Rulings.Crafting.ExplosiveArrowIngredients,
            [Procoagulant] = Rulings.Crafting.ProcoagulantIngredients,
            [FieldBandage] = Rulings.Crafting.FieldBandageIngredients,
        };

        var recipes = new RecipeDefinition[order.Length];
        for (int index = 0; index < order.Length; index++)
        {
            uint id = order[index];
            uint granted = grantedCounts[index];
            recipes[index] = new RecipeDefinition(
                OutputItemDefinitionId: id,
                OutputCount: granted,
                Ingredients: Pairs(ingredients[id]),
                SortOrdinal: sortOrdinals[index],
                BusyMilliseconds: Rulings.Crafting.CraftMillisecondsPerUnit)
            {
                WireBundleCount = bundleCounts[index] == granted ? null : bundleCounts[index],
                Reserved = Rulings.Crafting.RetailRecipeReserved,
                RetailDisplayFields = true,
            };
        }

        return recipes;
    }
}
