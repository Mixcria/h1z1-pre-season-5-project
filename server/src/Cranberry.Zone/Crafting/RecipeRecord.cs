using Cranberry.Protocol;

namespace Cranberry.Zone.Crafting;

// The crafting recipe record, byte for byte.
//
// Derivation: docs/62-crafting.md, which builds on the research pass docs/59 Part 1. Every layout
// below was re-read from the August binary's own decompiles by this lane rather than taken on
// docs/59's word, and the field NAMES are new this pass - they come from the two Scaleform
// datasource row builders, which turned docs/59's ten anonymous u32s into named UI columns
// (docs/59 §1.7 needles 1 and 3, both now closed without the live sentinel run they asked for).
// Nothing here has been on the wire yet, so nothing is LIVE-VERIFIED (D29, docs/32).
//
// The fact that makes crafting cheap: this record is ALSO the element of the SendSelfToClient list
// at blob offset 0x11a, which SelfRecord.cs has always written as a zero count. The client loads it
// with FUN_140a54bc0 (i32 count + FUN_140a44390 elements) and hands every element to
// FUN_141516280 - the SAME manager entry point 0x26 09 Recipe.List uses (FUN_140db5680, dumped in
// out/wave6-prep/ghidra-recipes3). One writer therefore serves both delivery paths, and the bytes
// one proves are the bytes the other sends.

/// <summary>
/// One ingredient of a recipe - 44 bytes on the wire, and one <c>BaseClient.RecipeComponents</c>
/// row in the crafting window's ingredient panel.
/// <para>
/// <b>Parsers</b> (re-read this pass): <c>FUN_140a4da70</c> reads the list and the per-element key;
/// <c>FUN_140a441a0</c> reads the element body
/// (<c>out/wave6-prep/ghidra-recipes/_141518250_.../FUN_140a4da70_140a4da70.c</c>,
/// <c>out/wave6-prep/ghidra-recipes2/_140a441a0_.../FUN_140a441a0_140a441a0.c</c>).
/// </para>
/// <para>
/// <b>Field names</b> come from <c>FUN_1415119f0</c>
/// (<c>out/wave6-prep/ghidra-recipes3/_14143a3e0_.../FUN_1415119f0_1415119f0.c</c>), the builder
/// that emits one <c>RecipeComponents</c> row per element, cross-checked against the column
/// registration <c>FUN_141510490</c> (<c>out/crafting/ghidra-recipecomponents</c>), whose nine
/// declared columns are <c>AutoIndex, RecipeId, ItemId, ItemName, ItemDescription,
/// ItemLocateDescription, ItemImageSetId, RequiredCount, RecipeType</c>. The builder emits them in
/// exactly that order, and the element offsets it reads are:
/// </para>
/// <code>
/// wire  elem off   builder expression   UI column
///  1    +0x48      (list reader)        -- the element's hash key, bucket = key &amp; 0x3f
///  2    +0x00      *puVar10             ItemName              (localisation string id)
///  3    +0x10      puVar10[4]           ItemImageSetId
///  4    +0x14      puVar10[5]           -- not read by the builder
///  5    +0x18      puVar10[6]           ItemDescription       (localisation string id)
///  6    +0x1c      puVar10[7]           ItemLocateDescription (localisation string id)
///  7    +0x20      puVar10[8]           RequiredCount
///  8    +0x28 u64  (0x26 02's target)   the live counts
///  9    +0x30      puVar10[0xc]         RecipeType
/// 10    +0x04      puVar10[1]           ItemId
/// </code>
/// <para>
/// The element carries the vtable <c>PTR_LAB_14310bb38</c> at <c>elem+0x08</c>; the recipe record
/// carries the same vtable at <c>rec+0x10</c>, which is why docs/59 §1.5.4 reads
/// <c>rec+0x08..+0x2c</c> as an embedded object of this class.
/// </para>
/// </summary>
public sealed record RecipeComponentRecord
{
    /// <summary>Bytes one component occupies: the key plus eight u32s and one u64.</summary>
    public const int Length = 44;

    /// <summary>
    /// <c>elem+0x48</c>, read by the LIST reader before the element is allocated: the element's
    /// hash key (bucket <c>key &amp; 0x3f</c>). Cranberry writes the ingredient's item definition
    /// id, mirroring the recipe node whose key (<c>node+0x288</c>) is its recipe id. <b>[INF]</b> -
    /// nothing reads the key except the hash, so a wrong value here costs nothing visible.
    /// </summary>
    public uint Key { get; init; }

    /// <summary><c>elem+0x00</c> - <c>ItemName</c>, a localisation string id resolved through
    /// <c>DAT_143f696c0</c> vtable <c>+0x10</c>.</summary>
    public uint ItemNameStringId { get; init; }

    /// <summary><c>elem+0x10</c> - <c>ItemImageSetId</c>, the ingredient icon override.</summary>
    public uint ItemImageSetId { get; init; }

    /// <summary><c>elem+0x14</c> - parsed and stored, but no datasource column reads it. Zero.</summary>
    public uint Reserved { get; init; }

    /// <summary><c>elem+0x18</c> - <c>ItemDescription</c>, a localisation string id.</summary>
    public uint ItemDescriptionStringId { get; init; }

    /// <summary><c>elem+0x1c</c> - <c>ItemLocateDescription</c>, a localisation string id (the
    /// "where do I find this" line).</summary>
    public uint ItemLocateDescriptionStringId { get; init; }

    /// <summary><c>elem+0x20</c> - <b><c>RequiredCount</c></b>, how many of this ingredient one
    /// craft consumes. This is the number the ingredient panel prints as the denominator.</summary>
    public uint RequiredCount { get; init; }

    /// <summary>
    /// <c>elem+0x28</c>, the u64 that <c>0x26 02 Recipe.ComponentUpdate</c> rewrites. The crafting
    /// view binds both <c>countInInventory</c> and <c>quantityInProximity</c> and this is the only
    /// 64-bit field in the element, so <b>[INF]</b> it is those two u32s packed low/high;
    /// <see cref="PackLiveCounts"/> and <see cref="RecipeComponentUpdate"/> pack them that way.
    /// </summary>
    public ulong LiveCounts { get; init; }

    /// <summary><c>elem+0x30</c> - <c>RecipeType</c>.</summary>
    public uint RecipeType { get; init; }

    /// <summary><c>elem+0x04</c>, written <b>last</b> - <b><c>ItemId</c></b>, the ingredient's item
    /// definition id. The client resolves its icon, name and bulk from its own
    /// <c>ClientItemDefinitions</c> row for this id.</summary>
    public uint ItemDefinitionId { get; init; }

    /// <summary>
    /// Pack the two live counts the ingredient panel shows into <see cref="LiveCounts"/>:
    /// <c>countInInventory</c> low, <c>quantityInProximity</c> high. <b>[INF]</b>, see
    /// <see cref="LiveCounts"/>.
    /// </summary>
    public static ulong PackLiveCounts(uint countInInventory, uint quantityInProximity) =>
        countInInventory | ((ulong)quantityInProximity << 32);

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteUInt32(Key);                             // read by FUN_140a4da70, -> elem+0x48
        w.WriteUInt32(ItemNameStringId);                // -> elem+0x00
        w.WriteUInt32(ItemImageSetId);                  // -> elem+0x10
        w.WriteUInt32(Reserved);                        // -> elem+0x14
        w.WriteUInt32(ItemDescriptionStringId);         // -> elem+0x18
        w.WriteUInt32(ItemLocateDescriptionStringId);   // -> elem+0x1c
        w.WriteUInt32(RequiredCount);                   // -> elem+0x20
        w.WriteUInt64(LiveCounts);                      // -> elem+0x28
        w.WriteUInt32(RecipeType);                      // -> elem+0x30
        w.WriteUInt32(ItemDefinitionId);                // -> elem+0x04, LAST
    }
}

/// <summary>
/// One recipe, byte for byte, as <c>FUN_140a44390</c> reads it.
/// <para>
/// <b>Every field is named.</b> docs/59 §1.7 needle 1 asked which of the ten anonymous u32s is the
/// output item id and proposed a live sentinel run to find out. It is answered here from the
/// binary instead: <c>FUN_1415126a0</c>
/// (<c>out/wave6-prep/ghidra-recipes3/_14143a3e0_.../FUN_1415126a0_1415126a0.c</c>) builds the
/// <c>BaseClient.Recipes</c> row column by column out of the parsed node, and because
/// <c>FUN_141514f50</c> copies the record to <c>node+0x00</c> unchanged, a node index <em>is</em> a
/// record offset:
/// </para>
/// <code>
/// wire  rec off   node[]        UI column (FUN_1415126a0)       Cranberry name
///  1    +0x00     [0]           RecipeId                        RecipeId
///  2    +0x08     [2]           ItemName        (string id)     ItemNameStringId
///  3    +0x18     [6]           ItemImageSetId                  ItemImageSetId
///  4    +0x1c     [7]           ItemTintValue                   ItemTintValue
///  5    +0x20     [8]           ItemDescription (string id)     ItemDescriptionStringId
///  6    +0x24     [9]           -- not read by the row builder  Reserved
///  7    +0x28     [10]          BundleCount                     BundleCount
///  8    +0x2c     [11]          SortOrdinal                     SortOrdinal
///  9    +0x30 u8  byte @ +0x30  MembersOnly                     MembersOnly
/// 10    +0x34     [13]          FilterType                      FilterType
/// 11    i32 count + 44-byte components -> rec+0x50 (FUN_140a4da70)
/// 12    +0x04     [1]           ItemId                          OutputItemDefinitionId
/// </code>
/// <para>
/// Note the wire order: <b>the output item id is written last</b>, after the component list.
/// <c>ComponentCount</c> is <c>node+0x70</c>, which the client computes from the list itself - no
/// wire field feeds it.
/// </para>
/// <para>
/// Two independent confirmations of the same mapping. <c>FUN_141518250</c> sub 1 raises the
/// Scaleform toast <c>"RecipeLearned"</c> with <c>rec+0x08</c> resolved through the localisation
/// manager <c>DAT_143f696c0</c> vtable <c>+0x10</c> - so <c>+0x08</c> is a <em>string id</em> and
/// cannot be the item id. And <c>FUN_140db5680</c>, the <c>SendSelfToClient</c> sink, finishes by
/// publishing an <c>Items</c> datasource row (<c>FUN_141566780</c>) for every item id the recipe
/// list references: the client draws icon, name and bulk from its own <c>ClientItemDefinitions</c>
/// once it has <c>ItemId</c>, which is why the recipe record can leave the display fields at zero.
/// </para>
/// <para>
/// Fixed part = 9 u32s + 1 u8 + the i32 component count + the trailing u32 = <b>45 bytes</b>, plus
/// 44 per ingredient. The in-memory node is 0x278 bytes, which both the <c>0x26 09</c> array stride
/// and <c>FUN_140a54bc0</c> confirm independently.
/// </para>
/// </summary>
public sealed record RecipeRecord
{
    /// <summary>The fixed part: 45 bytes. Each ingredient adds <see cref="RecipeComponentRecord.Length"/>.</summary>
    public const int FixedLength = 45;

    /// <summary>
    /// <c>rec+0x00</c>. The hash key (<c>node+0x288</c>, bucket <c>&amp; 0x3f</c>), the
    /// <c>RecipeId</c> column, and the id the client echoes in <c>09 1a Command.RecipeStart</c>.
    /// </summary>
    public uint RecipeId { get; init; }

    /// <summary><c>rec+0x08</c>. Localisation string id shown as the row's <c>ItemName</c> and as
    /// the <c>RecipeLearned</c> toast's text.</summary>
    public uint ItemNameStringId { get; init; }

    /// <summary><c>rec+0x18</c>. <c>ItemImageSetId</c> - the recipe row's icon override.</summary>
    public uint ItemImageSetId { get; init; }

    /// <summary><c>rec+0x1c</c>. <c>ItemTintValue</c>, passed through <c>FUN_1400a10fa</c>.</summary>
    public uint ItemTintValue { get; init; }

    /// <summary><c>rec+0x20</c>. Localisation string id shown as <c>ItemDescription</c>.</summary>
    public uint ItemDescriptionStringId { get; init; }

    /// <summary><c>rec+0x24</c>. Parsed and stored, but <c>FUN_1415126a0</c> never reads it, so no
    /// UI column names it. Cranberry writes zero.</summary>
    public uint Reserved { get; init; }

    /// <summary><c>rec+0x28</c>. <c>BundleCount</c> - how many items one craft yields.</summary>
    public uint BundleCount { get; init; }

    /// <summary><c>rec+0x2c</c>. <c>SortOrdinal</c> - the recipe tab's sort key.</summary>
    public uint SortOrdinal { get; init; }

    /// <summary><c>rec+0x30</c> (u8). <c>MembersOnly</c>. Cranberry writes false.</summary>
    public bool MembersOnly { get; init; }

    /// <summary><c>rec+0x34</c>. <c>FilterType</c> - the crafting tab's filter dropdown.</summary>
    public uint FilterType { get; init; }

    /// <summary>The ingredients. The view renders four slots (<c>ingredientsItem_1..4</c>), so a
    /// recipe with more than four would show a truncated panel.</summary>
    public IReadOnlyList<RecipeComponentRecord> Components { get; init; } = [];

    /// <summary><c>rec+0x04</c>, written <b>last</b>. <c>ItemId</c> - the item the craft produces.</summary>
    public uint OutputItemDefinitionId { get; init; }

    /// <summary>Bytes this record occupies on the wire.</summary>
    public int Length => FixedLength + (Components.Count * RecipeComponentRecord.Length);

    /// <summary>
    /// Write the record bare - no <c>0x26 01</c> envelope. This is the form the
    /// <c>SendSelfToClient</c> list at blob offset <c>0x11a</c> and <c>0x26 09 Recipe.List</c> both
    /// carry, and the form <c>0x26 01</c> appends to its two-byte header.
    /// </summary>
    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteUInt32(RecipeId);                 // -> rec+0x00
        w.WriteUInt32(ItemNameStringId);         // -> rec+0x08
        w.WriteUInt32(ItemImageSetId);           // -> rec+0x18
        w.WriteUInt32(ItemTintValue);            // -> rec+0x1c
        w.WriteUInt32(ItemDescriptionStringId);  // -> rec+0x20
        w.WriteUInt32(Reserved);                 // -> rec+0x24
        w.WriteUInt32(BundleCount);              // -> rec+0x28
        w.WriteUInt32(SortOrdinal);              // -> rec+0x2c
        w.WriteBool(MembersOnly);                // -> rec+0x30
        w.WriteUInt32(FilterType);               // -> rec+0x34

        w.WriteInt32(Components.Count);          // FUN_140a4da70 -> rec+0x50
        foreach (RecipeComponentRecord component in Components)
        {
            component.WriteTo(w);
        }

        w.WriteUInt32(OutputItemDefinitionId);   // -> rec+0x04, LAST on the wire
    }

    /// <summary>
    /// Write an <c>i32 count</c> followed by the bare records - the exact shape of
    /// <c>FUN_140a54bc0</c>, which is both the <c>SendSelfToClient</c> sub-loader at blob offset
    /// <c>0x11a</c> and the body of <c>0x26 09 Recipe.List</c>.
    /// </summary>
    public static void WriteList(PacketWriter w, IReadOnlyList<RecipeRecord> recipes)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(recipes);
        w.WriteInt32(recipes.Count);
        foreach (RecipeRecord recipe in recipes)
        {
            recipe.WriteTo(w);
        }
    }

    /// <summary>Bytes <see cref="WriteList"/> produces, including the count prefix.</summary>
    public static int ListLength(IReadOnlyList<RecipeRecord> recipes)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        int length = 4;
        foreach (RecipeRecord recipe in recipes)
        {
            length += recipe.Length;
        }

        return length;
    }
}
