namespace Cranberry.Zone.Appearance;

/// <summary>
/// <b>THE SKIN ON A PICKUP</b> (docs/106 §13, D283). The one resolver that answers "which wardrobe
/// selection, if any, colours this looted item on this body" - for every worn slot and every stow
/// peg, not just the hand.
/// <para>
/// <b>The defect.</b> The 2026-09-03 19:48 session (<c>captures\wire-20260903-194804.txt</c>) has
/// the owner carrying eighteen wardrobe selections, three of them weapon skins, and picking up a
/// military backpack, two helmets and three guns. Every worn attachment on the wire carried the
/// BASE item's rows: <c>slot 10 grp 237 app [332,333]</c> for the backpack (item 2124's own rows,
/// not selection 2121 -&gt; reward 2778), <c>slot 76 grp 29 app [27,28]</c> for the stowed pump
/// shotgun (item 1374's own rows, not selection 1374 -&gt; 3720), <c>slot 77 grp 168 app
/// [181,182]</c> for the stowed AR-15 and <c>slot 80 grp 71 app [97,98]</c> for the stowed AK-47.
/// Only body slot 7 was skinned - <c>grp 207 app [249,250]</c> on <c>Weapon_AK47_3P.adr</c> at
/// 19:58:47.188 - and that is D225, which reaches the hand and nothing else. That is the owner's
/// report: <i>"when I pick up items in the world they don't get skinned (backpack, guns etc.); the
/// AK-47 was the only one skinned."</i>
/// </para>
/// <para>
/// <b>Why nothing else could have applied it.</b> <c>AugustWorldEquipmentVisuals.ApplySkin</c> is
/// the only other site that puts a wardrobe pick on a looted item, and it reads
/// <c>AugustWorldEquipmentState</c> - which <b>nothing in this server ever fills</b>: there is no
/// call to <c>AugustWorldEquipmentState.TryEquip</c> outside its own tests, so
/// <c>BuildAttachments</c> takes its <c>equipped.Count == 0</c> early return on every dress and
/// <c>ApplySkin</c> has never run in a live session. Pickups are dressed by
/// <see cref="AugustWornVisuals.Dress"/> from <c>PlayerInventory.EquipmentSlots</c>, and that path
/// resolved <c>item.ItemDefinitionId</c> and only ever that.
/// </para>
/// <para>
/// <b>The base item names its own category.</b> <c>AugustSkinCatalog</c> already holds a row whose
/// <c>RewardItemId</c> IS the looted item - 2124 -&gt; category 2121, 2170 -&gt; 2827, 2172 -&gt;
/// 2172, 2215 -&gt; 2215, 10 -&gt; 10, 1374 -&gt; 1374, 2229 -&gt; 2229 - so no new table is needed
/// and no id is typed by hand. A weapon's catalogue category equals its item id, which is why
/// D225's narrower rule (<c>pick.CategoryPrototypeId == itemDefinitionId</c>) is a special case of
/// this one and the hand and the pegs cannot now disagree.
/// </para>
/// <para>
/// Helmet, backpack and body armour selections may replace the mesh within the same wardrobe
/// category. Toxic armour and ASUS ROG backpacks use different authored meshes from their bases.
/// Other equipment retains the mesh guard. See docs/fixes-20260905-handoff.md.
/// </para>
/// </summary>
public static class AugustWornSkins
{
    private static readonly IReadOnlyDictionary<uint, AugustSkinCatalogEntry> ByRewardItem =
        AugustSkinCatalog.Apparel
            .Concat(AugustSkinCatalog.Weapons)
            .ToDictionary(entry => entry.RewardItemId);

    /// <summary>
    /// The wardrobe category a looted item belongs to, read out of the catalogue's own row for
    /// that item. Loot-only helmet and backpack colours can inherit a category from an unambiguous shared
    /// male/female mesh. Unrelated loot without a wardrobe family remains unskinnable.
    /// </summary>
    public static bool TryGetCategory(uint itemDefinitionId, out uint categoryPrototypeId)
    {
        // ClientItemDefinitions: gameplay AR-15 2425 and skin category 10 share NAME_ID 32,
        // PARAM1 (native weapon definition) 6 and the same equipment slots. The gameplay
        // variant supplies its model directly and has no reward row in the skin catalogue.
        if (itemDefinitionId == 2425)
        {
            categoryPrototypeId = 10;
            return true;
        }

        if (ByRewardItem.TryGetValue(itemDefinitionId, out AugustSkinCatalogEntry entry))
        {
            categoryPrototypeId = entry.CategoryPrototypeId;
            return true;
        }

        // Loot-only colours (white helmet 2171; black/blue/green/red small backpacks
        // 2112/2113/2115/2117) have no reward row. Both body meshes must identify ONE
        // family, so small backpacks cannot accidentally inherit military backpack skins.
        if (AugustWornMeshCatalog.TryGet(itemDefinitionId, out var mesh))
        {
            uint[] categories = ByRewardItem.Values
                .Where(candidate => candidate.EquipmentSlotId is 1 or 10
                    && string.Equals(candidate.MaleModelName, mesh.ModelNameFor(CharacterVisuals.Male), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.FemaleModelName, mesh.ModelNameFor(CharacterVisuals.Female), StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.CategoryPrototypeId).Distinct().ToArray();
            if (categories.Length == 1)
            {
                categoryPrototypeId = categories[0];
                return true;
            }
        }

        categoryPrototypeId = 0;
        return false;
    }

    public static string? ModelForReward(uint rewardItemId, uint gender) =>
        ByRewardItem.TryGetValue(rewardItemId, out var entry)
            && AugustAssetIndex.Ships(entry.ModelNameFor(gender))
                ? entry.ModelNameFor(gender) : null;

    /// <summary>
    /// The wardrobe selection whose appearance rows and shader group replace
    /// <paramref name="itemDefinitionId"/>'s own on this body, or false with a reason.
    /// </summary>
    /// <param name="itemDefinitionId">The looted item actually in the body slot.</param>
    /// <param name="gender">The wearer's body, for the pick's own mesh spelling.</param>
    /// <param name="baseModelName">
    /// The mesh <see cref="AugustWornVisuals.TryResolveMesh"/> already resolved for the looted
    /// item. The re-model guard compares against this and not against the catalogue's row for the
    /// base item, because the mesh on the wire is what another player reads off you.
    /// </param>
    /// <param name="selections">
    /// <c>AugustWardrobeState.Snapshot()</c> - every selection, apparel and weapon alike. The
    /// pickup-only/lobby split does not apply here: by definition the item is already on the body.
    /// </param>
    /// <param name="retintOnly">D53's rule; <c>CRANBERRY_SKIN_REMODEL=1</c> switches it off.</param>
    /// <param name="pick">The winning selection.</param>
    /// <param name="reason">Why, either way. Logged once per (item, reason) pair by the caller.</param>
    public static bool TryResolve(
        uint itemDefinitionId,
        uint gender,
        string baseModelName,
        IReadOnlyList<AugustSkinCatalogEntry> selections,
        bool retintOnly,
        out AugustSkinCatalogEntry pick,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(selections);
        pick = default;

        if (!TryGetCategory(itemDefinitionId, out uint category))
        {
            reason = $"item {itemDefinitionId} has no wardrobe strip in the August catalogue";
            return false;
        }

        bool found = false;
        foreach (AugustSkinCatalogEntry candidate in selections)
        {
            if (candidate.CategoryPrototypeId == category)
            {
                pick = candidate;
                found = true;
                break;
            }
        }

        if (!found)
        {
            reason = $"no skin selected for category {category}";
            return false;
        }

        if (pick.RewardItemId == itemDefinitionId)
        {
            // The selection IS the looted item. Substituting it would change nothing and would
            // cost a second appearance lookup on every dress.
            reason = $"category {category} selects the looted item itself";
            pick = default;
            return false;
        }

        string pickModel = pick.ModelNameFor(gender);
        if (string.IsNullOrWhiteSpace(pickModel))
        {
            reason = $"reward {pick.RewardItemId} has no mesh for this body";
            pick = default;
            return false;
        }

        if (pick.EquipmentSlotId == 100 && !AugustAssetIndex.Ships(pickModel))
        {
            reason = $"reward {pick.RewardItemId} armour mesh is absent from the client assets";
            pick = default;
            return false;
        }

        // Starter clothes are the body beneath the menu's selected outfit. A Henley,
        // T-shirt or hoodie choice must also survive entry into a match; their shared
        // wardrobe category is authoritative even when their apparel meshes differ.
        bool starterClothing = Inventory.SurvivorStarterOutfit.DefaultItemDefinitionIds.Contains(itemDefinitionId)
            && AugustWardrobeCatalog.ProjectsOntoStarterBody(pick);
        if (retintOnly && !starterClothing && pick.EquipmentSlotId is not 1 and not 10 and not 100
            && !string.Equals(pickModel, baseModelName, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"reward {pick.RewardItemId} names \"{pickModel}\" and the looted item wears "
                + $"\"{baseModelName}\" - a pick may re-tint a looted item, never re-model it "
                + "(D53, docs/80 edit 4)";
            pick = default;
            return false;
        }

        reason = $"category {category} -> reward {pick.RewardItemId}";
        return true;
    }
}
