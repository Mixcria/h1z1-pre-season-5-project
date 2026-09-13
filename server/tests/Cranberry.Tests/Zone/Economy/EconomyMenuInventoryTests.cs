using Cranberry.Zone.Economy;

namespace Cranberry.Tests.Zone.Economy;

public sealed class EconomyMenuInventoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-menu-inventory", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void MenuConsolidatesLockedAliasesButPreservesSavedOwnershipAndFreeOpenCrates()
    {
        var store = new AccountEconomyStore(_root);
        var catalog = EconomyCatalog.Default;
        OwnedAccountItem[] items = [.. catalog.Crates.Values.Select((crate, index) =>
            new OwnedAccountItem((ulong)index + 1, crate.ItemId, 0, 100, "test", false)),
            new(1000, 1812, 2054, 2, "test"), new(1001, 2837, 0, 2, "test", false)];
        var account = store.GetOrCreate("account", new(Items: items));
        var service = new EconomyPurchaseService(store);

        var visible = EconomyMenuInventory.VisibleItems(account.Items, service.Offers);

        Assert.DoesNotContain(visible, item => catalog.Crates.TryGetValue(item.AccountItemId, out var crate)
            && crate.KeyItemId != 0);
        foreach (var group in catalog.Crates.Values.Where(crate => crate.CrownsCost > 0)
            .GroupBy(crate => EconomyMenuInventory.CanonicalItemId(crate.ItemId)))
        {
            Assert.Equal(100u * (uint)group.Count(), Assert.Single(visible, item => item.AccountItemId == group.Key).Count);
            Assert.DoesNotContain(visible, item => group.Any(crate => crate.ItemId != group.Key && crate.ItemId == item.AccountItemId));
        }
        foreach (var crate in catalog.Crates.Values.Where(crate => crate.CrownsCost == 0 && crate.KeyItemId == 0))
            Assert.Contains(visible, item => item.AccountItemId == crate.ItemId);
        Assert.Contains(visible, item => item.AccountItemId == 1812 && item.Count == 2);
        Assert.Contains(visible, item => item.AccountItemId == 2837 && item.Count == 2);
        Assert.Equal(0u, account.Balance(4)); // Purchase availability does not depend on current funds.
        var restored = new AccountEconomyStore(_root).GetOrCreate("account");
        Assert.Equal(account.Revision, restored.Revision);
        Assert.Equal(account.Items, restored.Items);
    }

    [Fact]
    public void LockedCrateRequiresAnActualOfferWhileOwnedUnlockedCounterpartRemainsVisible()
    {
        var service = new EconomyPurchaseService(new AccountEconomyStore(_root));
        var offers = service.Offers.Where(pair => pair.Value.LockedCrate?.ItemId != 3620)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        OwnedAccountItem[] items = [new(1, 3620, 0, 100, "test", false),
            new(2, 3208, 0, 2, "test", false)];

        var visible = EconomyMenuInventory.VisibleItems(items, offers);

        Assert.Equal(items[1], Assert.Single(visible));
        Assert.Equal(100u, items[0].Count);
    }
}
