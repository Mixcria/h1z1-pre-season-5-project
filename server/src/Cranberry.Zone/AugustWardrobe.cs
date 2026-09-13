using Cranberry.Zone.Inventory;

namespace Cranberry.Zone;

/// <summary>
/// Server-authoritative cosmetic selections for one character. The zone service keeps this object
/// by character guid, so a selection survives menu-to-world zoning and reconnects for the lifetime
/// of the host process.
/// </summary>
public sealed class AugustWardrobeState
{
    private static readonly IReadOnlySet<uint> EmptyCategories = new HashSet<uint>();

    private readonly Dictionary<uint, SelectedSkin> _selectedByCategory = [];
    private ulong _nextOrder;

    public IReadOnlyList<AugustSkinCatalogEntry> Snapshot()
    {
        lock (_selectedByCategory)
        {
            return
            [
                .. _selectedByCategory.Values
                    .OrderBy(selection => selection.Entry.CategoryPrototypeId)
                    .Select(selection => selection.Entry),
            ];
        }
    }

    /// <summary>
    /// Returns the rows that may be placed in SetSkinItemManager's protocol "worn" array and on
    /// the actual body. With no equipped pickup categories this is the menu/pre-game outfit.
    /// Passing category ids for real equipped world items lets their pickup-only preset replace
    /// ordinary clothing in the same physical slot (a helmet over a hat, for example). Empty
    /// backpack/armour/helmet slots are omitted completely: even a default manager row can make
    /// the client composite gear that the character does not possess.
    /// </summary>
    public IReadOnlyList<AugustSkinCatalogEntry> WornSnapshot(
        IReadOnlySet<uint>? equippedPickupCategoryIds = null)
    {
        lock (_selectedByCategory)
        {
            IReadOnlySet<uint> equipped = equippedPickupCategoryIds ?? EmptyCategories;
            IEnumerable<SelectedSkin> candidates = _selectedByCategory.Values.Where(selection =>
                AugustWardrobeCatalog.ProjectsOntoStarterBody(selection.Entry)
                || (AugustWardrobeCatalog.IsPickupOnly(selection.Entry)
                    && equipped.Contains(selection.Entry.CategoryPrototypeId)));

            // An actually equipped pickup item wins its physical slot regardless of when its skin
            // preset was chosen. Otherwise the newest explicit visible choice wins.
            return
            [
                .. candidates
                    .GroupBy(selection => selection.Entry.EquipmentSlotId)
                    .Select(group => group
                        .OrderByDescending(selection =>
                            equipped.Contains(selection.Entry.CategoryPrototypeId))
                        .ThenByDescending(selection => selection.Order)
                        .First()
                        .Entry)
                    .OrderBy(entry => entry.EquipmentSlotId),
            ];
        }
    }

    /// <summary>Alias for equipment projectors, making the physical meaning explicit.</summary>
    public IReadOnlyList<AugustSkinCatalogEntry> BodySnapshot(
        IReadOnlySet<uint>? equippedPickupCategoryIds = null) =>
        WornSnapshot(equippedPickupCategoryIds);

    /// <summary>
    /// Weapon presets are mappings for a future looted weapon, never character clothing and never
    /// manager-worn apparel. They are announced separately with SetSkinItem.
    /// </summary>
    public IReadOnlyList<AugustSkinCatalogEntry> WeaponSnapshot()
    {
        lock (_selectedByCategory)
        {
            return
            [
                .. _selectedByCategory.Values
                    .Where(selection => !AugustWardrobeCatalog.IsApparel(selection.Entry))
                    .OrderBy(selection => selection.Entry.CategoryPrototypeId)
                    .Select(selection => selection.Entry),
            ];
        }
    }

    public bool TryApply(
        SkinItemSelectionRequest request,
        out AugustSkinCatalogEntry selected,
        out bool removed,
        out string reason)
    {
        selected = default;
        removed = false;

        if (request.SubOpcode == SkinItemSelectionRequest.RequestUnsetSkinItem)
        {
            lock (_selectedByCategory)
            {
                removed = _selectedByCategory.Remove(request.CategoryPrototypeId);
            }

            reason = removed
                ? $"category {request.CategoryPrototypeId} returned to its default"
                : $"category {request.CategoryPrototypeId} had no server selection";
            return true;
        }

        if (!AugustWardrobeCatalog.TryResolveClicked(request.ClickedId, out selected))
        {
            reason = $"clicked id {request.ClickedId} is neither an owned account item nor an August reward";
            return false;
        }

        if (!AugustWardrobeCatalog.TryPlaceInRequestedCategory(
                selected,
                request.CategoryPrototypeId,
                out AugustSkinCatalogEntry categorized))
        {
            reason = $"reward {selected.RewardItemId} belongs to category "
                + $"{selected.CategoryPrototypeId}, not requested category {request.CategoryPrototypeId}";
            return false;
        }

        selected = categorized;

        lock (_selectedByCategory)
        {
            // Most apparel categories represent the garment that is physically worn now. There
            // can be only one such selection per character equipment slot. Pickup-only categories
            // are deliberately different: a hat and two helmet presets, for example, coexist so
            // the hat can be worn in the lobby while the helmet preset waits for a helmet pickup.
            if (AugustWardrobeCatalog.ProjectsOntoStarterBody(selected))
            {
                // Copy the out parameter before entering a lambda; out/ref parameters cannot be
                // captured, and the local also makes the physical-slot invariant explicit.
                uint equipmentSlotId = selected.EquipmentSlotId;
                uint selectedCategoryPrototypeId = selected.CategoryPrototypeId;
                uint[] staleVisibleCategories =
                [
                    .. _selectedByCategory
                        .Where(pair => pair.Key != selectedCategoryPrototypeId
                            && pair.Value.Entry.EquipmentSlotId == equipmentSlotId
                            && AugustWardrobeCatalog.ProjectsOntoStarterBody(pair.Value.Entry))
                        .Select(pair => pair.Key),
                ];

                foreach (uint staleCategory in staleVisibleCategories)
                {
                    _selectedByCategory.Remove(staleCategory);
                }
            }

            _selectedByCategory[selected.CategoryPrototypeId] =
                new SelectedSkin(selected, ++_nextOrder);
        }

        reason = $"category {selected.CategoryPrototypeId} -> reward {selected.RewardItemId} "
            + $"(account item {selected.AccountItemId})";
        return true;
    }

    private sealed record SelectedSkin(AugustSkinCatalogEntry Entry, ulong Order);
}

/// <summary>Fast validated indexes over the generated August catalogue.</summary>
public static class AugustWardrobeCatalog
{
    private static readonly AugustSkinCatalogEntry[] AllEntries =
    [
        .. AugustSkinCatalog.Apparel,
        .. AugustSkinCatalog.Weapons,
    ];

    private static readonly IReadOnlyDictionary<uint, AugustSkinCatalogEntry> ByAccountItem =
        AllEntries.ToDictionary(entry => entry.AccountItemId);

    private static readonly IReadOnlyDictionary<uint, AugustSkinCatalogEntry> ByRewardItem =
        AllEntries.ToDictionary(entry => entry.RewardItemId);

    private static readonly HashSet<uint> ApparelRewardIds =
        [.. AugustSkinCatalog.Apparel.Select(entry => entry.RewardItemId)];

    // SkinItemSlotItem.txt's native apparel strips. A few strips share PARAM1 and are consequently
    // folded onto the first prototype in the generated catalogue. The click still carries the
    // exact strip prototype, so this physical-slot map validates and restores that information.
    private static readonly IReadOnlyDictionary<uint, uint> ApparelEquipmentSlotByCategory =
        new Dictionary<uint, uint>
        {
            [2158] = 1,   // hats
            [2827] = 1,   // motorcycle helmets (pickup preset)
            [2172] = 1,   // tactical helmets (pickup preset)
            [3250] = 3,   // ordinary chest clothing
            [2570] = 3,   // ghillie suit (pickup preset)
            [2038] = 10,  // small backpacks (pickup preset)
            [2121] = 10,  // military backpacks (pickup preset)
            [2125] = 10,  // makeshift backpacks (pickup preset)
            [2209] = 5,   // ordinary footwear
            [2215] = 5,   // running shoes (pickup preset)
            [2563] = 5,   // stealth footwear (pickup preset)
            [2109] = 4,   // trousers
            [2668] = 2,   // gloves
            [2870] = 28,  // face cosmetics
            [2254] = 29,  // eyewear
            [2271] = 100, // body armour (pickup preset)
            [3378] = 100, // alternate body-armour prototype (pickup preset)
        };

    public static bool TryResolveClicked(uint clickedId, out AugustSkinCatalogEntry entry) =>
        ByAccountItem.TryGetValue(clickedId, out entry)
        || ByRewardItem.TryGetValue(clickedId, out entry);

    public static bool IsApparel(AugustSkinCatalogEntry entry) =>
        ApparelRewardIds.Contains(entry.RewardItemId);

    /// <summary>
    /// Validates a click's native SkinItemSlotItem prototype and preserves it on the selected row.
    /// Head, chest and footwear strips can share a PARAM1, so the generated catalogue's first-row
    /// category is insufficient to distinguish a hat from a helmet or work boots from running
    /// shoes. The client-supplied category is accepted only when its August equipment slot agrees
    /// with the conversion-backed reward.
    /// </summary>
    public static bool TryPlaceInRequestedCategory(
        AugustSkinCatalogEntry entry,
        uint requestedCategoryPrototypeId,
        out AugustSkinCatalogEntry categorized)
    {
        categorized = default;

        if (requestedCategoryPrototypeId == entry.CategoryPrototypeId)
        {
            categorized = entry;
            return true;
        }

        if (!IsApparel(entry)
            || !ApparelEquipmentSlotByCategory.TryGetValue(
                requestedCategoryPrototypeId,
                out uint requestedEquipmentSlotId)
            || requestedEquipmentSlotId != entry.EquipmentSlotId)
        {
            return false;
        }

        categorized = entry with { CategoryPrototypeId = requestedCategoryPrototypeId };
        return true;
    }

    /// <summary>Whether a saved preset waits for its corresponding item to be looted.</summary>
    public static bool IsPickupOnly(AugustSkinCatalogEntry entry) =>
        AugustWorldEquipmentPolicy.IsPickupOnly(entry);

    /// <summary>Whether this explicit selection belongs on the menu/pre-game body.</summary>
    public static bool ProjectsOntoStarterBody(AugustSkinCatalogEntry entry) =>
        IsApparel(entry)
        && entry.EquipmentSlotId != 0
        && !IsPickupOnly(entry);
}

/// <summary>Projects wardrobe selections onto the equipment packet used by menu and world actors.</summary>
public static class AugustWardrobeVisuals
{
    public static IReadOnlyList<CharacterEquipmentAttachment> BuildAttachments(
        CharacterVisuals visuals,
        AugustWardrobeState wardrobe,
        AugustDynamicAppearanceTable? appearance,
        bool orderRowsByGender = false,
        bool sendShaderGroups = false)
    {
        var attachments = visuals.StarterOutfit.ToList();

        // A full 94/01 rebuilds the attachment group (140cd85e0 -> 140cdccb0).
        // The self record's head/hair strings alone do not preserve customization through
        // that rebuild, and lightweight peers never receive those strings. Keep the selected
        // face and hair in every complete dress, separate from helmet slot 1. August's
        // EquipmentSlotDefinitions names these slots CustomizationHead (15) and Hair (27).
        attachments.Add(new CharacterEquipmentAttachment(
            visuals.HeadModel, 15, ShaderParameterGroupId: visuals.SkinToneId));
        attachments.Add(new CharacterEquipmentAttachment(visuals.HairModel, 27));

        // The state normally evicts competing visible selections as they are made. Grouping here
        // is a defensive boundary for restored/legacy state: exactly one explicit choice can ever
        // project onto a physical slot, and the newest choice wins deterministically.
        foreach (AugustSkinCatalogEntry entry in wardrobe.WornSnapshot())
        {
            string modelName = entry.ModelNameFor(visuals.Gender);
            if (string.IsNullOrWhiteSpace(modelName))
            {
                // The SetSkinItem row still carries the selection. A missing mesh must not invent
                // geometry; the seven catalogue rows absent from the local reference retain the
                // currently attached model for their slot.
                continue;
            }

            // docs/80 §Built: with the gender ordering on, the row naming THIS body's mesh is put
            // first, because the client picks the first wildcard. 49 (item, body) pairs in this
            // catalogue were handed the other body's hoodie without it.
            uint rowGender = orderRowsByGender ? visuals.Gender : 0;
            bool hoodie = ItemUseOptionTable.Allows(entry.RewardItemId, 96)
                && ItemUseOptionTable.Allows(entry.RewardItemId, 97);
            IReadOnlyList<uint> appearanceRows = appearance is null
                ? []
                : hoodie
                    ? appearance.AppearanceRowsForHood(entry.RewardItemId, hoodUp: false, rowGender)
                    : appearance.AppearanceRowsFor(entry.RewardItemId, rowGender);
            // THE FALLBACK COLOUR (docs/106, D190). The client applies the winning appearance
            // row's own shader group and only reads this field when NO row resolves
            // (FUN_140c70d60 tail: attachment+0x190). Sending 0 there is not "no opinion", it is
            // the neutral Default tint of TintSemanticTables row 1 - highlight 1,1,1, midtone .5,
            // shadow 0 - i.e. WHITE on a greyscale tint mask and GREY on a _Tintable mesh
            // (docs/69 A3-A4). Every other dress site in this tree already carries a group;
            // this one did not. Costs no bytes: the field is already on the wire as a zero, and
            // DefinesShaderGroup keeps docs/54 I4's payload-widening experiment untouched.
            uint shaderGroup = sendShaderGroups && appearance is not null
                ? appearance.ShaderGroupFor(entry.RewardItemId, visuals.Gender)
                : 0;
            if (shaderGroup != 0 && appearance?.DefinesShaderGroup(shaderGroup) != true)
            {
                shaderGroup = 0;
            }

            var attachment = new CharacterEquipmentAttachment(
                modelName,
                entry.EquipmentSlotId,
                TextureAlias: entry.TextureAlias,
                ShaderParameterGroupId: shaderGroup,
                AppearanceIds: appearanceRows);

            int current = attachments.FindIndex(existing => existing.SlotId == entry.EquipmentSlotId);
            if (current >= 0)
            {
                attachments[current] = attachment;
            }
            else
            {
                attachments.Add(attachment);
            }
        }

        return [.. attachments.OrderBy(attachment => attachment.SlotId)];
    }
}
