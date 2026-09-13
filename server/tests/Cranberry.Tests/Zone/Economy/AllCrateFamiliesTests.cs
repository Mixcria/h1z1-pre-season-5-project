using Cranberry.Zone;
using Cranberry.Zone.Economy;

namespace Cranberry.Tests.Zone.Economy;

public sealed class AllCrateFamiliesTests
{
    [Fact]
    public void AllAugustDefinitionsHavePoolsIncludingRetiredAndSpecialCrates()
    {
        var catalog = EconomyCatalog.Default;
        Assert.Equal(121, catalog.Crates.Count); // 108 native rows plus 13 authored locked wrappers.
        uint[] retiredAndSpecial = [3609, 3610, 3611, 3615, 3617, 3618, 3619, 3622,
            3820, 3879, 3926, 3807, 3808, 3809, 3810, 3811, 3812, 3813, 3814, 3815, 3816, 3817, 3818];
        foreach (uint id in retiredAndSpecial)
        {
            Assert.NotEmpty(catalog.Crates[id].Rewards);
            Assert.All(catalog.Crates[id].Rewards, reward => Assert.True(catalog.Skins.ContainsKey(reward.AccountItemId)));
        }
        Assert.Contains(catalog.Crates[3880].Rewards, reward => catalog.Skins[reward.AccountItemId].Name == "Frostbite AR-15");
        Assert.Contains(catalog.Crates[3926].Rewards, reward => catalog.Skins[reward.AccountItemId].Name == "Toxic Shotgun");
    }

    [Fact]
    public void AllThirtyOneBaseFamiliesHaveTheSameCrownUnlockPrice()
    {
        var catalog = EconomyCatalog.Default;
        var families = catalog.Crates.Values.Where(c => c.CrownsCost > 0)
            .GroupBy(c => c.UnlockedItemId).Select(g => g.MinBy(c => c.ItemId)!).ToArray();
        Assert.Equal(31, families.Length);
        Assert.All(families, crate =>
        {
            Assert.Equal(250u, crate.CrownsCost);
            Assert.Equal(0u, crate.KeyItemId);
            Assert.True(crate.UnlockBundleId > 0);
            Assert.DoesNotContain("Unlocked", crate.Name);
            Assert.Equal(0u, catalog.Crates[crate.UnlockedItemId].CrownsCost);
            Assert.Equal(crate.Rewards, catalog.Crates[crate.UnlockedItemId].Rewards);
        });
    }

    [Fact]
    public void SpecialCratesRequireCrownsAndGrantOnlyThePurchasedUnlockedQuantity()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-locked-special", Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = EconomyCatalog.Default;
            var specials = catalog.Crates.Values.Where(c => c.ItemId >= 10000).ToArray();
            Assert.Equal(13, specials.Length);
            var store = new AccountEconomyStore(root);
            var service = new EconomyPurchaseService(store);
            foreach (var crate in specials)
            foreach (uint quantity in new uint[] { 1, 10 })
            {
                string id = $"{crate.ItemId}-{quantity}";
                store.GetOrCreate(id, new(new Dictionary<uint, uint> { [4] = quantity * 250 },
                    [new(1, crate.ItemId, 0, 500, "test", false)]));
                var order = new EconomyOrderRequest(EconomyOrderRequest.Place, "", "4097", 1, "en_US", "", "KH$", "",
                    [new(crate.UnlockBundleId, 1, quantity, "0", EconomyOrderLine.NativeDefaultOutOfBandData)]);
                Assert.Equal(quantity * 250, service.Quote(id, 4097, order).Total);
                var result = service.Place(id, "link", 4097, order);
                Assert.True(result.Succeeded, result.Error);
                Assert.Equal(0u, result.Snapshot!.Balance(4));
                Assert.Equal(500 - quantity, result.Snapshot.Items.Single(i => i.AccountItemId == crate.ItemId).Count);
                Assert.Equal(quantity, result.Snapshot.Items.Single(i => i.AccountItemId == crate.UnlockedItemId).Count);
                Assert.False(service.Quote(id, 4097, order).Allowed);
                Assert.True(service.Place(id, "link", 4097, order).Replayed);
                var opened = new AccountEconomyOperations(store).OpenCrates(id, "open", [new(crate.UnlockedItemId)], _ => 0);
                Assert.True(opened.Succeeded, opened.Error);
                Assert.Equal(0u, opened.Snapshot!.Balance(4));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void UnlockTenFromFiveHundredStockConsumesConsolidatedAliasesOnceAndCanOpenItsReward()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-crates-500", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AccountEconomyStore(root);
            var initial = store.GetOrCreate("account", new(new Dictionary<uint, uint> { [4] = 2_500 },
                [new(1, 3620, 0, 6, "test", false), new(2, 3854, 0, 494, "test", false)]));
            var service = new EconomyPurchaseService(store);
            var visible = Assert.Single(EconomyMenuInventory.VisibleItems(initial.Items, service.Offers));
            Assert.Equal(3620u, visible.AccountItemId);
            Assert.Equal(500u, visible.Count);
            var order = new EconomyOrderRequest(EconomyOrderRequest.Place, "", "4097", 1, "en_US", "", "KH$", "",
                [new(180, 1, 10, "0", EconomyOrderLine.NativeDefaultOutOfBandData)]);
            Assert.Equal(2_500u, service.Quote("account", 4097, order).Total);
            // Two distinct backend IDs must not quote the same stock twice.
            store.GetOrCreate("small", new(new Dictionary<uint, uint> { [4] = 10_000 },
                [new(1, 3620, 0, 5, "test", false), new(2, 3854, 0, 5, "test", false)]));
            Assert.False(service.Quote("small", 4097, order with
                { Lines = [new(180, 1, 6, "0", "0"), new(237, 1, 6, "0", "0")] }).Allowed);
            var result = service.Place("account", "link", 4097, order);
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(0u, result.Snapshot!.Balance(4));
            Assert.Equal(10u, result.Snapshot.Items.Single(item => item.AccountItemId == 3208).Count);
            Assert.Equal(490u, result.Snapshot.Items.Single(item => item.AccountItemId == 3854).Count);
            Assert.True(service.Place("account", "link", 4097, order).Replayed);
            var opened = new AccountEconomyOperations(store).OpenCrates("account", "open", [new(3208)], _ => 0);
            Assert.True(opened.Succeeded, opened.Error);
            Assert.Equal(9u, opened.Snapshot!.Items.Single(item => item.AccountItemId == 3208).Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
