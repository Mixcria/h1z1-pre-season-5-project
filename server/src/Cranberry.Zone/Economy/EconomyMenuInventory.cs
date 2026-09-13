namespace Cranberry.Zone.Economy;

/// <summary>
/// Consolidates duplicate locked crate definitions into one native Crown-unlock entry.
/// Saved ownership stays intact; commerce consumes the same family represented by this view.
/// Unlocked rewards remain available to the native opening page.
/// </summary>
public static class EconomyMenuInventory
{
    public static uint CanonicalItemId(uint itemId, EconomyCatalog? catalog = null)
    {
        catalog ??= EconomyCatalog.Default;
        if (!catalog.TryGetCrate(itemId, out var crate) || crate.CrownsCost == 0)
            return itemId;
        return catalog.Crates.Values.Where(c => c.CrownsCost > 0 && c.UnlockedItemId == crate.UnlockedItemId)
            .Min(c => c.ItemId);
    }

    public static IReadOnlyList<OwnedAccountItem> VisibleItems(IReadOnlyList<OwnedAccountItem> items,
        IReadOnlyDictionary<uint, EconomyPurchaseOffer> offers, EconomyCatalog? catalog = null)
    {
        catalog ??= EconomyCatalog.Default;
        var purchasable = offers.Values
            .Where(offer => offer.Bundle.CurrencyId == 4 && offer.Bundle.Price > 0 && offer.LockedCrate is not null)
            .Select(offer => offer.LockedCrate!.ItemId).ToHashSet();
        var visible = items.Where(item => item.Count > 0 &&
            (!catalog.TryGetCrate(item.AccountItemId, out var crate)
                || crate.KeyItemId == 0 && (crate.CrownsCost == 0 || purchasable.Contains(crate.ItemId))))
            .ToArray();
        var result = visible.Where(item => !catalog.Crates.ContainsKey(item.AccountItemId)).ToList();
        foreach (var group in visible.Where(item => catalog.Crates.ContainsKey(item.AccountItemId))
            .GroupBy(item => CanonicalItemId(item.AccountItemId, catalog)))
        {
            if (catalog.Crates[group.Key].CrownsCost > 0 && !purchasable.Contains(group.Key)) continue;
            result.Add(group.First() with { AccountItemId = group.Key,
                Count = checked((uint)group.Sum(item => (long)item.Count)) });
        }
        return result;
    }
}
