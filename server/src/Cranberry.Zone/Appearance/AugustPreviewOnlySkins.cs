namespace Cranberry.Zone.Appearance;

/// <summary>
/// The August client's own answer to "is this wardrobe strip worn in the lobby, or does it wait
/// for a looted base item?" - the <c>SELECT_PREVIEW_ONLY</c> column of
/// <c>SkinItemSlot.txt</c> (slot level) and <c>SkinItemSlotItem.txt</c> (prototype level).
/// <para>
/// <b>Why this exists (D53, docs/80 edit 12).</b> <c>AugustWorldEquipmentPolicy.IsPickupOnly</c>
/// decided this with three model-name substring heuristics and a blanket rule on equipment slots
/// 10 and 100. The client ships the column, the owner's own Z1 server reads the same two sheets
/// and bakes the identical set, and a substring match on a mesh name is the kind of rule that goes
/// quietly wrong when a new strip is added. So the column is the authority now, and the heuristics
/// survive only in the one job they are actually needed for - see
/// <see cref="AugustWorldEquipmentPolicy.IsPickupOnly"/>.
/// </para>
/// <para>
/// Transcribed from <c>C:\Aug2017\out\data_aug\SkinItemSlot.txt</c> (11 rows) and
/// <c>SkinItemSlotItem.txt</c> (28 rows), H1Z1.exe 0.0.118.208059.
/// </para>
/// </summary>
public static class AugustPreviewOnlySkins
{
    /// <summary>
    /// <c>SkinItemSlot.SELECT_PREVIEW_ONLY = 1</c>: 10 bodyarmor, 11 guns, 12 melee. Every other
    /// skin slot (head, chest, back, feet, legs, hands, face, eyes) is 0, i.e. lobby clothing.
    /// </summary>
    public static IReadOnlySet<uint> PreviewOnlySkinSlots { get; } =
        new HashSet<uint> { 10, 11, 12 };

    /// <summary>
    /// <c>SkinItemSlotItem.SELECT_PREVIEW_ONLY = 1</c>: 2827 motorcycle helmets, 2570 ghillie suit,
    /// 2172 tactical helmets - three prototypes inside otherwise-ordinary slots.
    /// </summary>
    public static IReadOnlySet<uint> PreviewOnlyCategoryPrototypes { get; } =
        new HashSet<uint> { 2172, 2570, 2827 };

    /// <summary>
    /// <c>SkinItemSlotItem</c> maps <c>PROTOTYPE_ITEM_ID</c> to <c>SKIN_ITEM_SLOT_ID</c>. All 28
    /// rows: the sheet numbers them 1..29 with id 8 absent.
    /// </summary>
    public static IReadOnlyDictionary<uint, uint> SkinSlotByCategoryPrototype { get; } =
        new Dictionary<uint, uint>
        {
            [2158] = 1,    // hats
            [2827] = 1,    // motorcycle helmets                      SELECT_PREVIEW_ONLY = 1
            [2172] = 1,    // tactical helmets                        SELECT_PREVIEW_ONLY = 1
            [3250] = 2,    // shirts
            [2570] = 2,    // ghillie suit                            SELECT_PREVIEW_ONLY = 1
            [2038] = 3,    // small backpacks
            [2121] = 3,    // military backpacks
            [2125] = 3,    // makeshift backpacks
            [2209] = 4,    // footwear
            [2215] = 4,    // running shoes
            [2563] = 4,    // stealth footwear
            [2109] = 5,    // trousers
            [2668] = 6,    // gloves
            [2870] = 8,    // face
            [2254] = 9,    // eyewear
            [2271] = 10,   // body armour            slot 10 bodyarmor SELECT_PREVIEW_ONLY = 1
            [3378] = 10,   // alternate body armour  slot 10 bodyarmor SELECT_PREVIEW_ONLY = 1
            [2229] = 11,   // AK-47                  slot 11 guns      SELECT_PREVIEW_ONLY = 1
            [10] = 11,     // AR-15
            [1374] = 11,   // pump shotgun
            [1373] = 11,   // shotgun (alternate row)
            [1991] = 11,   // R380
            [1997] = 11,   // M9
            [2] = 11,      // M1911
            [1718] = 11,   // .44 Magnum
            [1986] = 11,   // bow
            [83] = 12,     // machete                slot 12 melee     SELECT_PREVIEW_ONLY = 1
            [84] = 12,     // combat knife
        };

    /// <summary>
    /// The client's own verdict for one wardrobe category: preview-only at the prototype level, or
    /// inside a preview-only skin slot.
    /// </summary>
    public static bool IsPreviewOnly(uint categoryPrototypeId) =>
        PreviewOnlyCategoryPrototypes.Contains(categoryPrototypeId)
        || (SkinSlotByCategoryPrototype.TryGetValue(categoryPrototypeId, out uint slot)
            && PreviewOnlySkinSlots.Contains(slot));

    /// <summary>
    /// The categories Cranberry additionally treats as pickup-only although both the August sheets
    /// and the owner's Z1 server call them ordinary lobby clothing: the three backpack prototypes
    /// and the two performance-footwear prototypes.
    /// <para>
    /// Gated by <see cref="AugustWorldEquipmentPolicy.GateBackpacksAndPerformanceFootwear"/>,
    /// default <c>true</c> so nothing changes in this wave. The stated reason for the gate
    /// (<c>AugustWardrobe.cs</c>: "even a default manager row can make the client composite gear
    /// that the character does not possess") is an observation about backpacks that nobody has
    /// re-tested, so it is surfaced as one named variable rather than silently flipped.
    /// </para>
    /// </summary>
    public static IReadOnlySet<uint> LobbyCategoriesCranberryGates { get; } =
        new HashSet<uint> { 2038, 2121, 2125, 2215, 2563 };
}
