using System.Text.Json;

namespace Cranberry.Zone.Economy;

/// <summary>
/// The owner's September 12 Client account entitlement. Give each missing catalogue skin
/// once, preserving existing copies and the separate starter wallet/crate receipt.
/// See docs/client-skin-grant-20260912.md for the exact event-item exclusions.
/// </summary>
public static class ClientSkinGrant
{
    public const string OperationId = "client-skins:20260912-v1";

    // AccountRecipe IDs, never physical reward IDs. The ordinary Showdown Crate AR-15
    // (2780) remains eligible, as explicitly confirmed by the owner.
    public static IReadOnlyList<uint> ExcludedItemIds { get; } = Array.AsReadOnly<uint>(
    [
        2797, // Showdown Gold AR-15
        2888, // Twin Galaxies Hoodie (black set)
        3752, // Infernal 12GA Pump Shotgun
        3876, // Twin Galaxies Warmup Pants (black set)
        4075, // Royalty Showdown AR-15
        4076, // Showdown 2017 AR-15
        4077, // Gold Showdown 2017 AR-15
    ]);

    public static IReadOnlyList<uint> SkinItemIds { get; } = Array.AsReadOnly(
        EconomyCatalog.Default.Skins.Keys.Except(ExcludedItemIds).Order().ToArray());

    public static AccountEconomyResult Apply(AccountEconomyStore store, string accountId) =>
        store.Execute(accountId, OperationId, "client-skin-grant", draft =>
        {
            var granted = new List<uint>();
            foreach (uint itemId in SkinItemIds)
            {
                if (draft.Owns(itemId)) continue;
                var skin = EconomyCatalog.Default.Skins[itemId];
                draft.Grant(itemId, skin.RewardItemId, 1, OperationId, skin.CanScrap);
                granted.Add(itemId);
            }
            // Keep the durable receipt compact even when the complete catalogue is granted.
            return JsonSerializer.Serialize(new { GrantedItemIds = granted });
        });
}
