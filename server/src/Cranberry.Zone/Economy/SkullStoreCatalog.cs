namespace Cranberry.Zone.Economy;

/// <summary>
/// The ten August ClientItemDefinitions with CURRENCY_TYPE=5. Item IDs and names
/// are native; bundle IDs and prices are server choices because native COST is zero.
/// These are direct cosmetic purchases, with no crate or random reward involved.
/// </summary>
public static class SkullStoreCatalog
{
    public const uint CurrencyId = 5;
    public const string CurrencyCode = "KS$";
    public const uint CategoryId = 13; // MarketplacePageManager.CATEGORY_REWARDS = "0^13"
    // Currency.txt: Skulls locale and icon, also used for the native store category.
    public static EconomyStoreCategory Category { get; } = new(CategoryId, 15165, 1760, 13);

    private static readonly (uint ItemId, uint Price, uint DescriptionLocaleId)[] Products =
    [
        (3791, 250, 13981), (3792, 250, 13981), (3793, 250, 13981), // suit jackets
        (3794, 100, 13983), (3795, 100, 13983), (3796, 100, 13983), // slacks
        (3802, 500, 0), (3803, 500, 0), (3804, 750, 0), (3857, 1000, 0), // Offroaders
    ];

    public static IEnumerable<EconomyPurchaseOffer> Offers(EconomyCatalog catalog)
    {
        foreach (var (itemId, price, description) in Products)
        {
            // Custom/test catalogues need not carry the production Skull Store.
            if (!catalog.TryGetSkin(itemId, out var skin)) continue;
            yield return new(new(50_000 + itemId, skin.Name, CurrencyId, price, itemId, 1,
                CategoryId, skin.NameLocaleId, skin.ImageSetId, description));
        }
    }
}
