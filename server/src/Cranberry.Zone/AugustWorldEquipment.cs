using Cranberry.Zone.Appearance;

namespace Cranberry.Zone;

/// <summary>
/// Gameplay equipment whose wardrobe entry is a skin for a looted base item, rather than clothing
/// the player may wear into the pre-game lobby.  The category ids and equipment slots come from
/// the August client's own <c>SkinItemSlot*.txt</c> and <c>ClientItemDefinitions.txt</c> tables.
/// </summary>
public enum AugustWorldEquipmentKind
{
    Helmet,
    GhillieSuit,
    Backpack,
    FastFootwear,
    StealthFootwear,
    BodyArmor,
}

/// <summary>
/// One pickup-only wardrobe strip, the character attachment slot it renders in, and the loadout
/// slot which owns the real item.  <see cref="LoadoutSlotId"/> is not a generic bag index: it may
/// be used only when the item is moved into the player's loadout container.
/// </summary>
public readonly record struct AugustWorldEquipmentRule(
    uint CategoryPrototypeId,
    uint EquipmentSlotId,
    uint LoadoutSlotId,
    AugustWorldEquipmentKind Kind);

/// <summary>
/// The August retail split between ordinary wardrobe clothing and skins which wait for a matching
/// item to be equipped in the world.
/// </summary>
public static class AugustWorldEquipmentPolicy
{
    private static readonly AugustWorldEquipmentRule[] Rules =
    [
        // Head slot 1: hats are ordinary clothing; these two strips are protective helmets.
        new(2827, 1, 11, AugustWorldEquipmentKind.Helmet),
        new(2172, 1, 11, AugustWorldEquipmentKind.Helmet),

        // Chest slot 3: the ordinary shirt strip is 3250; 2570 is the pickup-only ghillie suit.
        new(2570, 3, 10, AugustWorldEquipmentKind.GhillieSuit),

        // Back slot 10: every backpack is inventory capacity, never lobby clothing.
        new(2038, 10, 12, AugustWorldEquipmentKind.Backpack),
        new(2121, 10, 12, AugustWorldEquipmentKind.Backpack),
        new(2125, 10, 12, AugustWorldEquipmentKind.Backpack),

        // Feet slot 5: 2209 is ordinary sturdy footwear.  The performance footwear waits for loot.
        new(2215, 5, 13, AugustWorldEquipmentKind.FastFootwear),
        new(2563, 5, 13, AugustWorldEquipmentKind.StealthFootwear),

        // The August table names this dedicated render slot ChestArmor.
        new(2271, 100, 38, AugustWorldEquipmentKind.BodyArmor),
        new(3378, 100, 38, AugustWorldEquipmentKind.BodyArmor),
    ];

    private static readonly IReadOnlyDictionary<uint, AugustWorldEquipmentRule> RuleByCategory =
        Rules.ToDictionary(rule => rule.CategoryPrototypeId);

    public static IReadOnlyList<AugustWorldEquipmentRule> PickupOnlyRules => Rules;

    public static bool TryGetRule(
        uint categoryPrototypeId,
        out AugustWorldEquipmentRule rule) =>
        RuleByCategory.TryGetValue(categoryPrototypeId, out rule);

    /// <summary>
    /// Cranberry gates the three backpack prototypes (2038/2121/2125) and the two
    /// performance-footwear prototypes (2215/2563) as pickup-only, which neither the August
    /// client's own <c>SELECT_PREVIEW_ONLY</c> column nor the owner's Z1 server does - both call
    /// them ordinary lobby clothing.
    /// <para>
    /// <b>Default <c>true</c>: no behaviour change in this wave</b> (docs/80 edit 12,
    /// <c>CRANBERRY_LOBBY_PACKS=1</c> flips it). The divergence has a stated reason - a default
    /// backpack manager row was observed making the client composite gear the character does not
    /// possess (<c>AugustWardrobe.cs</c> s "WornSnapshot") - and nobody has re-tested it. Surfacing
    /// it as one named variable is the point; the owner flips it, not this lane.
    /// </para>
    /// </summary>
    public static bool GateBackpacksAndPerformanceFootwear { get; set; } = true;

    /// <summary>
    /// Returns true when a selected catalogue row must not be projected onto a character until a
    /// matching base item is equipped.
    /// <para>
    /// <b>The client's own column is the authority</b> (docs/80 edit 12): the prototype-level
    /// <c>SELECT_PREVIEW_ONLY</c> of <c>SkinItemSlotItem.txt</c> and the slot-level one of
    /// <c>SkinItemSlot.txt</c>, both read through <see cref="AugustPreviewOnlySkins"/>. Body armour
    /// arrives that way (prototypes 2271/3378 sit in skin slot 10 <c>bodyarmor</c>, which is
    /// preview-only), so the old blanket rule on equipment slot 100 is gone.
    /// </para>
    /// <para>
    /// <b>The model checks stay, and the spec that said to delete them was wrong.</b> They are not
    /// a duplicate of the column: the generated catalogue folds strips that share a <c>PARAM1</c>
    /// onto the first prototype for that physical slot, so eleven head rows whose mesh is
    /// <c>..._Head_Helmet_TacticalSantaHat</c>, <c>..._Head_Mask_Bunny</c> and friends carry
    /// category <b>2158</b> (hats, preview-only 0), and two footwear rows whose mesh is
    /// <c>..._Feet_Conveys</c> / <c>..._Feet_SilentBoots</c> carry category <b>2209</b>. Deleting
    /// the heuristics would silently move those thirteen rows into the lobby outfit. They are
    /// fold-recovery, and they are labelled as such.
    /// </para>
    /// </summary>
    public static bool IsPickupOnly(AugustSkinCatalogEntry entry)
    {
        if (AugustPreviewOnlySkins.IsPreviewOnly(entry.CategoryPrototypeId))
        {
            return true;
        }

        if (entry.EquipmentSlotId == 100)
        {
            // Body armour. The client agrees (prototypes 2271/3378 sit in skin slot 10 bodyarmor,
            // which is SELECT_PREVIEW_ONLY = 1); this restates it by RENDER slot as well, so that a
            // folded catalogue row carrying a hat category cannot leak a kevlar mesh into the
            // lobby - which is the guard AugustWorldEquipmentTests already had.
            return true;
        }

        if (AugustPreviewOnlySkins.LobbyCategoriesCranberryGates.Contains(entry.CategoryPrototypeId)
            || entry.EquipmentSlotId == 10)
        {
            // Backpacks and performance footwear: Cranberry's own divergence, one variable wide.
            return GateBackpacksAndPerformanceFootwear;
        }

        // Fold recovery only. See the remark above for the thirteen rows this catches.
        return entry.EquipmentSlotId switch
        {
            1 => IsHelmetModel(entry.MaleModelName) || IsHelmetModel(entry.FemaleModelName),
            3 => IsGhillieModel(entry.MaleModelName) || IsGhillieModel(entry.FemaleModelName),
            5 => GateBackpacksAndPerformanceFootwear
                && (IsPerformanceFootwearModel(entry.MaleModelName)
                    || IsPerformanceFootwearModel(entry.FemaleModelName)),
            _ => false,
        };
    }

    /// <summary>
    /// True only for a cosmetic that can be part of the menu and pre-game outfit without a looted
    /// base.  Weapon catalogue rows are excluded even though they have no apparel equipment slot.
    /// </summary>
    public static bool CanWearWithoutWorldItem(AugustSkinCatalogEntry entry) =>
        AugustWardrobeCatalog.IsApparel(entry) && !IsPickupOnly(entry);

    public static bool IsHelmetModel(string modelName) =>
        modelName.Contains("_Head_Helmet", StringComparison.OrdinalIgnoreCase)
        || modelName.Contains("_Head_Mask", StringComparison.OrdinalIgnoreCase);

    public static bool IsGhillieModel(string modelName) =>
        modelName.Contains("Ghillie", StringComparison.OrdinalIgnoreCase);

    public static bool IsPerformanceFootwearModel(string modelName) =>
        modelName.Contains("_Feet_Conveys", StringComparison.OrdinalIgnoreCase)
        || modelName.Contains("_Feet_SilentBoots", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One real inventory item currently equipped on the character.  The inventory instance identity
/// remains separate from its cosmetic presentation; <see cref="CategoryPrototypeId"/> chooses the
/// one wardrobe strip which is allowed to skin this base item.
/// </summary>
public sealed record AugustEquippedWorldItem(
    uint ItemDefinitionId,
    ulong ItemGuid,
    uint CategoryPrototypeId,
    uint EquipmentSlotId,
    string ModelName,
    string TextureAlias = "Default",
    uint ShaderParameterGroupId = 0,
    IReadOnlyList<uint>? AppearanceIds = null);

/// <summary>
/// Server-authoritative state for pickup-only equipment.  A physical slot contains at most one
/// item, so replacing a helmet/backpack/etc. cannot leave a stale mesh behind.
/// </summary>
public sealed class AugustWorldEquipmentState
{
    private readonly Dictionary<uint, AugustEquippedWorldItem> _byEquipmentSlot = [];

    public int Count
    {
        get
        {
            lock (_byEquipmentSlot)
            {
                return _byEquipmentSlot.Count;
            }
        }
    }

    public IReadOnlyList<AugustEquippedWorldItem> Snapshot()
    {
        lock (_byEquipmentSlot)
        {
            return [.. _byEquipmentSlot.Values.OrderBy(item => item.EquipmentSlotId)];
        }
    }

    /// <summary>
    /// Equips a looted base item after validating its native wardrobe category against its render
    /// slot.  Ordinary lobby clothing is deliberately refused by this pickup-only state.
    /// </summary>
    public bool TryEquip(
        AugustEquippedWorldItem item,
        out AugustEquippedWorldItem? replaced,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(item);
        replaced = null;

        if (!AugustWorldEquipmentPolicy.TryGetRule(item.CategoryPrototypeId, out var rule))
        {
            reason = $"category {item.CategoryPrototypeId} is ordinary clothing, a weapon, or unknown";
            return false;
        }

        if (item.EquipmentSlotId != rule.EquipmentSlotId)
        {
            reason = $"category {item.CategoryPrototypeId} belongs in equipment slot "
                + $"{rule.EquipmentSlotId}, not {item.EquipmentSlotId}";
            return false;
        }

        if (item.ItemDefinitionId == 0 || item.ItemGuid == 0)
        {
            reason = "an equipped world item needs a non-zero definition id and instance guid";
            return false;
        }

        if (string.IsNullOrWhiteSpace(item.ModelName))
        {
            reason = $"world item {item.ItemDefinitionId} has no render model";
            return false;
        }

        lock (_byEquipmentSlot)
        {
            _byEquipmentSlot.TryGetValue(item.EquipmentSlotId, out replaced);
            _byEquipmentSlot[item.EquipmentSlotId] = item;
        }

        reason = replaced is null
            ? $"equipped world item {item.ItemDefinitionId} in slot {item.EquipmentSlotId}"
            : $"replaced world item {replaced.ItemDefinitionId} with {item.ItemDefinitionId} "
                + $"in slot {item.EquipmentSlotId}";
        return true;
    }

    public bool TryUnequip(
        uint equipmentSlotId,
        out AugustEquippedWorldItem? removed)
    {
        lock (_byEquipmentSlot)
        {
            return _byEquipmentSlot.Remove(equipmentSlotId, out removed);
        }
    }

    public void Clear()
    {
        lock (_byEquipmentSlot)
        {
            _byEquipmentSlot.Clear();
        }
    }
}

/// <summary>
/// Projects real equipped items into character attachments and applies only the selected skin for
/// that base item's exact native strip.  A backpack selection therefore cannot render before a
/// backpack exists, and a military-backpack skin cannot accidentally dress a mansport base.
/// </summary>
public static class AugustWorldEquipmentVisuals
{
    /// <summary>
    /// How many skin transfers this process has declined because the pick would have re-modelled
    /// the looted item, by (base mesh, pick mesh) pair. A silent decline is the failure mode the
    /// rule exists to make visible, so the count is exposed rather than only logged.
    /// </summary>
    public static IReadOnlyDictionary<string, long> DeclinedTransfers => Declined;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long>
        Declined = new(StringComparer.Ordinal);

    /// <summary>
    /// A wardrobe pick may <b>re-tint</b> a looted item. It may never <b>re-model</b> it.
    /// <para>
    /// <b>D53 / docs/80 edit 4</b>, and the rule is the owner own work, measured on his own
    /// server. Pick 2051 (Camouflage Backpack) was smeared over looted 2121 (Green Camo Military
    /// Backpack) and the client drew <c>SurvivorMale_Back_Backpack_Mansport_Tintable.adr</c> - his
    /// report was <i>"I equipped a military backpack and it gave me the smaller incorrect one"</i>,
    /// and his whole-session client log contains no <c>Backpack_Military</c> line at all. The same
    /// line also re-modelled looted body armour from <c>Armor_Kevlar_Basic_Velcro</c> to
    /// <c>Armor_Kevlar_Military</c>.
    /// </para>
    /// <para>
    /// The reasoning is his too: a KOTK skin is a tint, a decal and a shader group on a model. The
    /// silhouette is the item tier, and the tier is what another player reads off you at 80 m. A
    /// skin that changes the mesh changes the tier on screen. For weapons the rule costs nothing -
    /// every catalogue row for a given weapon base names the same <c>_3P</c> mesh - so it cannot
    /// block the colour path.
    /// </para>
    /// <para>Rollback: <c>ZoneOptions.Skins.SkinRetintOnly = false</c> (<c>CRANBERRY_SKIN_REMODEL=1</c>).</para>
    /// </summary>
    public static bool RetintOnly { get; set; } = true;

    public static IReadOnlyList<CharacterEquipmentAttachment> BuildAttachments(
        CharacterVisuals visuals,
        AugustWardrobeState wardrobe,
        AugustDynamicAppearanceTable? appearance,
        AugustWorldEquipmentState equipment,
        bool orderRowsByGender = false,
        bool sendShaderGroups = false)
    {
        ArgumentNullException.ThrowIfNull(visuals);
        ArgumentNullException.ThrowIfNull(wardrobe);
        ArgumentNullException.ThrowIfNull(equipment);

        var attachments =
            new List<CharacterEquipmentAttachment>(
                AugustWardrobeVisuals.BuildAttachments(
                    visuals, wardrobe, appearance, orderRowsByGender, sendShaderGroups));

        IReadOnlyList<AugustEquippedWorldItem> equipped = equipment.Snapshot();
        if (equipped.Count == 0)
        {
            // The common case by far: no looted pickup-only gear, so the wardrobe list is already
            // sorted and complete, and the ToList/ToDictionary/OrderBy this method used to run
            // unconditionally bought nothing (docs/80 edit 14 - tidiness, not a measured win).
            return attachments;
        }

        IReadOnlyList<AugustSkinCatalogEntry> selections = wardrobe.Snapshot();
        foreach (AugustEquippedWorldItem item in equipped)
        {
            CharacterEquipmentAttachment attachment = BaseAttachment(item, appearance);
            AugustSkinCatalogEntry selected = default;
            bool haveSelection = false;
            foreach (AugustSkinCatalogEntry candidate in selections)
            {
                if (candidate.CategoryPrototypeId == item.CategoryPrototypeId)
                {
                    selected = candidate;
                    haveSelection = true;
                    break;
                }
            }

            if (haveSelection
                && selected.EquipmentSlotId == item.EquipmentSlotId
                && AugustWorldEquipmentPolicy.IsPickupOnly(selected))
            {
                attachment = ApplySkin(
                    attachment,
                    item,
                    selected,
                    visuals.Gender,
                    appearance,
                    orderRowsByGender ? visuals.Gender : 0);
            }

            uint slotId = item.EquipmentSlotId;
            int current = attachments.FindIndex(row => row.SlotId == slotId);
            if (current >= 0)
            {
                attachments[current] = attachment;
            }
            else
            {
                attachments.Add(attachment);
            }
        }

        // Slot ids are unique after the replace pass, so List.Sort instability cannot bite.
        attachments.Sort(static (left, right) => left.SlotId.CompareTo(right.SlotId));
        return attachments;
    }

    private static CharacterEquipmentAttachment ApplySkin(
        CharacterEquipmentAttachment attachment,
        AugustEquippedWorldItem item,
        AugustSkinCatalogEntry selected,
        uint gender,
        AugustDynamicAppearanceTable? appearance,
        uint rowGender)
    {
        string selectedModel = selected.ModelNameFor(gender);
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            return attachment;
        }

        bool sameSilhouette =
            string.Equals(selectedModel, item.ModelName, StringComparison.OrdinalIgnoreCase);

        if (!RetintOnly || sameSilhouette)
        {
            // Same silhouette (or the rule is switched off): take the pick tint and rows. When
            // RetintOnly is on and the meshes agree, keeping the looted item own model name rather
            // than the pick spelling is deliberate - they are the same file, and the looted one is
            // the spelling the rest of the packet already used.
            return attachment with
            {
                ModelName = RetintOnly ? attachment.ModelName : selectedModel,
                TextureAlias = selected.TextureAlias,
                AppearanceIds = appearance?.AppearanceRowsFor(selected.RewardItemId, rowGender) ?? [],
            };
        }

        long declined = Declined.AddOrUpdate(
            item.ModelName + " <- " + selectedModel, 1, static (_, count) => count + 1);
        if (declined == 1)
        {
            // Once per pair, for the same reason the owner server logs it once per pair: the
            // interesting event is the first one, and a per-dress line would drown the log.
            Console.Error.WriteLine(
                "[skins] declined skin transfer: looted item " + item.ItemDefinitionId
                    + " wears " + item.ModelName + " and pick " + selected.RewardItemId
                    + " names " + selectedModel + " - a pick may re-tint a looted item, never "
                    + "re-model it (D53, docs/80 edit 4). The looted item keeps its own model, "
                    + "texture alias and appearance rows.");
        }

        // WHOLE, not half. The owner declined transfer returns nothing at all, so the looted item
        // keeps its own model AND its own texture alias AND its own appearance rows. Applying the
        // pick tint to a declined transfer would put a Rasta colourway on a plain military pack -
        // a state he has never played and no capture supports.
        return attachment;
    }

    private static CharacterEquipmentAttachment BaseAttachment(
        AugustEquippedWorldItem item,
        AugustDynamicAppearanceTable? appearance) =>
        new(
            item.ModelName,
            item.EquipmentSlotId,
            item.TextureAlias,
            ShaderParameterGroupId: item.ShaderParameterGroupId,
            AppearanceIds: item.AppearanceIds
                ?? appearance?.AppearanceRowsFor(item.ItemDefinitionId)
                ?? []);
}
