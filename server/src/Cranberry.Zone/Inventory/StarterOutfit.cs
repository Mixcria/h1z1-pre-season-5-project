using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Inventory;

/// <summary>
/// One garment of the survivor's starting outfit: the <c>ClientItemDefinitions</c> row that backs a
/// mesh the character is already wearing.
/// </summary>
/// <param name="ItemDefinitionId">The item row granted at bootstrap.</param>
/// <param name="BodySlotId">
/// The <c>PASSIVE_EQUIP_SLOT_ID</c> of that row - recorded only so a test can check that the item
/// and the mesh <see cref="CharacterVisuals"/> attaches agree about which part of the body they
/// dress. <b>Nothing binds this piece to a body slot</b>: see <see cref="SurvivorStarterOutfit"/>.
/// </param>
/// <param name="MeshName">
/// The mesh the item resolves to, with the literal <c>&lt;gender&gt;</c> token where the source
/// carries one. For apparel this comes from <c>AugustSkinCatalog</c>
/// (<c>RewardItemId</c> -&gt; <c>MaleModelName</c>/<c>FemaleModelName</c>), because only 6 of the
/// 253 class-25002 rows carry a <c>MODEL_NAME</c> at all (docs/46 §7b); item 2613 is one of the few
/// that does carry one.
/// </param>
public readonly record struct StarterOutfitPiece(uint ItemDefinitionId, uint BodySlotId, string MeshName);

/// <summary>
/// Physical starter clothing, using the August client's basic item definitions. Account skin
/// ownership is provisioned separately by StarterAccountProfile. The inventory and visible
/// meshes describe the same garments; selected skins override them in the menu and matches.
/// See docs/starter-accounts-20260909.md.
/// </summary>
public static class SurvivorStarterOutfit
{
    /// <summary>Fingerless gloves, class 25008, body slot 2. Loadout slot 16 (Hands).</summary>
    public const uint Gloves = Rulings.Starter.OutfitGloves;

    /// <summary>Grey Henley, class 25002, body slot 3. Loadout slot 10 (Chest). PARAM1 21 -&gt; +50 bulk.</summary>
    public const uint Shirt = Rulings.Starter.OutfitShirt;

    /// <summary>Blue Jeans, class 25003, body slot 4. Loadout slot 14 (Legs). PARAM1 29 -&gt; +50 bulk.</summary>
    public const uint Pants = Rulings.Starter.OutfitPants;

    /// <summary>
    /// Stealth footwear, class 25005, body slot 5. Loadout slot 13 (Feet), matching
    /// <c>Survivor&lt;gender&gt;_Feet_Jeds.adr</c>, from the local reference starter outfit.
    /// </summary>
    public const uint Boots = Rulings.Starter.OutfitBoots;

    /// <summary>The four pieces, with the mesh each one resolves to.</summary>
    public static IReadOnlyList<StarterOutfitPiece> Pieces { get; } =
    [
        new(Gloves, BodySlots.Hands, "Survivor<gender>_Hands_Gloves_Fingerless.adr"),
        new(Shirt, BodySlots.Chest, "Survivor<gender>_Chest_Shirt_Henley.adr"),
        new(Pants, BodySlots.Legs, "Survivor<gender>_Legs_Pants_StraightLeg.adr"),
        new(Boots, BodySlots.Feet, "Survivor<gender>_Feet_Jeds.adr"),
    ];

    /// <summary>
    /// The default value of <see cref="InventoryOptions.StarterOutfit"/> - the item ids of
    /// <see cref="Pieces"/>, in the order <see cref="PlayerInventory.Bootstrap"/> grants them.
    /// </summary>
    public static IReadOnlyList<uint> DefaultItemDefinitionIds { get; } = [Gloves, Shirt, Pants, Boots];
}
