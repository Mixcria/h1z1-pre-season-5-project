using System.Text.Json;

namespace Cranberry.Zone.Economy;

/// <summary>
/// The September 9 playtest starter package. An account receipt makes the grant survive
/// reconnects, additional characters and restarts without refilling spent Crowns or crates.
/// Item IDs are August AccountRecipe rows, not physical inventory or premium skin IDs.
/// See docs/starter-accounts-20260909.md for the client evidence and owner policy.
/// </summary>
public static class StarterAccountProfile
{
    public const string OperationId = "starter-account:20260909-v1";
    public const uint Crowns = 200_000;
    public const uint CratesPerFamily = 500;

    // August's non-scrappable basic apparel. Excludes the scrappable skinny-jean
    // variants 3661/3662 and White Zeds 3712, plus pickup-only equipment and weapons.
    public static IReadOnlyList<uint> SkinItemIds { get; } = Array.AsReadOnly<uint>(
    [
        3645, 3646, 3647, 3648, 3649, 3650, 3651, 3652, 3653, 3654, // tops
        3655, 3656, 3657, 3658, 3659, 3660, 3663, // jeans
        3664, 3665, 3666, 3667, // caps
        3668, 3669, 3670, 3671, // gloves, shades and bandana
        3672, 3673, 3674, 3713, 3714, 3715, // starter footwear
    ]);

    public static IReadOnlyList<EconomyCrate> CrateFamilies { get; } = Array.AsReadOnly(
        EconomyCatalog.Default.Crates.Values
            .Where(crate => crate.CrownsCost > 0 && crate.UnlockBundleId > 0)
            .GroupBy(crate => crate.UnlockedItemId)
            .Select(group => group.MinBy(crate => crate.ItemId)!)
            .OrderBy(crate => crate.ItemId).ToArray());

    public static AccountEconomyResult Apply(AccountEconomyStore store, string accountId) =>
        store.Execute(accountId, OperationId, "starter-account", draft =>
        {
            uint crownGrant = Crowns > draft.Balance(4) ? Crowns - draft.Balance(4) : 0;
            if (crownGrant > 0) draft.Credit(4, crownGrant);
            var granted = new List<OwnedAccountItem>();
            foreach (uint itemId in SkinItemIds)
            {
                var skin = EconomyCatalog.Default.Skins[itemId];
                if (skin.CanScrap || skin.RarityId != 0 || skin.ScrapValue != -1)
                    draft.Reject("Starter apparel must be a non-scrappable August basic item.");
                if (!draft.Owns(itemId))
                    granted.Add(draft.Grant(itemId, skin.RewardItemId, 1, OperationId, scrappable: false));
            }
            foreach (var crate in CrateFamilies)
            {
                long held = draft.Items.Where(item =>
                    EconomyMenuInventory.CanonicalItemId(item.AccountItemId) == crate.ItemId)
                    .Sum(item => (long)item.Count);
                if (held < CratesPerFamily)
                    granted.Add(draft.Grant(crate.ItemId, 0, checked((uint)(CratesPerFamily - held)),
                        OperationId, scrappable: false));
            }
            return JsonSerializer.Serialize(new { CrownGrant = crownGrant, Granted = granted });
        });
}
