using System.Text.Json;
using Cranberry.Zone;
using Cranberry.Zone.Economy;

namespace Cranberry.Tests.Zone.Economy;

public sealed class SkullStorePurchaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-skull-purchases", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProductionSkullOffersContainTheTenNativeProductsAndPermitOneCopy()
    {
        var service = new EconomyPurchaseService(Store());
        var offers = SkullOffers(service);
        Assert.Equal(new uint[] { 3791, 3792, 3793, 3794, 3795, 3796, 3802, 3803, 3804, 3857 },
            offers.Select(offer => offer.Bundle.ContentItemId).Order().ToArray());
        Assert.All(offers, offer =>
        {
            Assert.True(offer.Bundle.Price > 0);
            Assert.Equal(1u, offer.Bundle.MaximumQuantity);
            Assert.Null(offer.LockedCrate);
            Assert.Contains(offer.Bundle.ContentItemId, EconomyCatalog.Default.Skins.Keys);
        });
    }

    [Theory]
    [InlineData(3791u, 3785u)]
    [InlineData(3792u, 3786u)]
    [InlineData(3793u, 3787u)]
    [InlineData(3794u, 3788u)]
    [InlineData(3795u, 3789u)]
    [InlineData(3796u, 3790u)]
    [InlineData(3802u, 3802u)]
    [InlineData(3803u, 3803u)]
    [InlineData(3804u, 3804u)]
    [InlineData(3857u, 3857u)]
    public void PreviewIsReadOnlyAndPurchasePersistsTheNativeItemWithoutChargingAReplay(uint itemId, uint rewardId)
    {
        const uint skulls = 100_000;
        var store = Store(skulls);
        var initial = store.GetOrCreate("account");
        var service = new EconomyPurchaseService(store);
        var offer = Assert.Single(SkullOffers(service), offer => offer.Bundle.ContentItemId == itemId);
        var order = Order(offer);

        var quote = service.Quote("account", 4097, order with { SubOpcode = EconomyOrderRequest.Preview });
        Assert.True(quote.Allowed, quote.Error);
        Assert.Equal(offer.Bundle.Price, quote.Total);
        AssertUnchanged(initial, store.GetOrCreate("account"));
        Assert.Null(service.LastResult("account", 4097, offer.Bundle.BundleId));

        var first = service.Place("account", "first-link", 4097, order,
            _ => throw new Exception("A direct Skull Store purchase must not draw a random reward."));
        Assert.True(first.Succeeded, first.Error);
        Assert.False(first.Replayed);
        var owned = Assert.Single(first.Snapshot!.Items);
        Assert.Equal(itemId, owned.AccountItemId);
        Assert.Equal(rewardId, owned.RewardItemId);
        Assert.Equal(1u, owned.Count);
        Assert.Equal(skulls - offer.Bundle.Price, first.Snapshot.Balance(5));
        Assert.Equal(initial.Balance(1), first.Snapshot.Balance(1));
        Assert.Equal(initial.Balance(4), first.Snapshot.Balance(4));
        var receipt = JsonSerializer.Deserialize<EconomyPurchaseReceipt>(first.Receipt!.ResultJson!)!;
        Assert.Empty(receipt.Award.Consumed);
        Assert.Equal(owned, Assert.Single(receipt.Award.Granted));
        Assert.Equal(new KeyValuePair<uint, long>(5, -(long)offer.Bundle.Price), Assert.Single(receipt.Award.CurrencyChanges));

        var restartedStore = new AccountEconomyStore(_root);
        var restarted = new EconomyPurchaseService(restartedStore);
        Assert.Equal(owned, Assert.Single(restartedStore.GetOrCreate("account").Items));
        var replay = restarted.Place("account", "first-link", 4097, order);
        Assert.True(replay.Succeeded, replay.Error);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Receipt.ResultJson, replay.Receipt!.ResultJson);
        AssertUnchanged(first.Snapshot, replay.Snapshot!);

        Assert.False(restarted.Quote("account", 4097, order).Allowed);
        var duplicate = restarted.Place("account", "new-link", 4097, order);
        Assert.False(duplicate.Succeeded);
        AssertUnchanged(first.Snapshot, restartedStore.GetOrCreate("account"));
    }

    [Fact]
    public void InsufficientSkullsCannotBePaidWithScrapOrCrowns()
    {
        var store = Store(skulls: 0);
        var initial = store.GetOrCreate("account");
        var service = new EconomyPurchaseService(store);
        var order = Order(SkullOffers(service)[0]);
        Assert.Equal(EconomyOrderResponse.InsufficientFunds, service.Quote("account", 4097, order).Result);
        Assert.False(service.Place("account", "link", 4097, order).Succeeded);
        AssertUnchanged(initial, store.GetOrCreate("account"));
    }

    [Theory]
    [InlineData("SCP", 1u)]
    [InlineData("KH$", 1u)]
    [InlineData("KS$", 0u)]
    [InlineData("KS$", 2u)]
    public void WrongCurrencyOrQuantityCannotSpendOrGrant(string currency, uint quantity)
    {
        var store = Store();
        var initial = store.GetOrCreate("account");
        var service = new EconomyPurchaseService(store);
        var order = Order(SkullOffers(service)[0], currency, quantity);
        Assert.False(service.Quote("account", 4097, order).Allowed);
        Assert.False(service.Place("account", "link", 4097, order).Succeeded);
        AssertUnchanged(initial, store.GetOrCreate("account"));
    }

    [Fact]
    public void MultipleSkullProductsAreRejectedAtomicallyWhenTheirCombinedPriceExceedsTheWallet()
    {
        var store = new AccountEconomyStore(_root);
        var service = new EconomyPurchaseService(store);
        var offers = SkullOffers(service).Take(2).ToArray();
        uint insufficientTotal = checked(offers[0].Bundle.Price + offers[1].Bundle.Price - 1);
        var initial = store.GetOrCreate("account", new(new Dictionary<uint, uint> { [5] = insufficientTotal }));
        var order = Order(offers[0]) with { Lines = offers.Select(offer => Line(offer)).ToArray() };
        Assert.Equal(EconomyOrderResponse.InsufficientFunds, service.Quote("account", 4097, order).Result);
        Assert.False(service.Place("account", "link", 4097, order).Succeeded);
        AssertUnchanged(initial, store.GetOrCreate("account"));
    }

    [Fact]
    public void AnOwnedSecondProductRejectsTheWholeOrderWithoutGrantingTheFirstProduct()
    {
        var store = new AccountEconomyStore(_root);
        var service = new EconomyPurchaseService(store);
        var offers = SkullOffers(service).Take(2).ToArray();
        var owned = EconomyCatalog.Default.Skins[offers[1].Bundle.ContentItemId];
        var initial = store.GetOrCreate("account", new(new Dictionary<uint, uint> { [5] = 100_000 },
            [new(123, owned.AccountItemId, owned.RewardItemId, 1, "explicit-test-seed")]));
        var order = Order(offers[0]) with { Lines = offers.Select(offer => Line(offer)).ToArray() };
        Assert.False(service.Quote("account", 4097, order).Allowed);
        Assert.False(service.Place("account", "link", 4097, order).Succeeded);
        AssertUnchanged(initial, store.GetOrCreate("account"));
    }

    private AccountEconomyStore Store(uint skulls = 100_000)
    {
        var store = new AccountEconomyStore(_root);
        store.GetOrCreate("account", new(new Dictionary<uint, uint> { [1] = 1234, [4] = 5678, [5] = skulls }));
        return store;
    }

    private static EconomyPurchaseOffer[] SkullOffers(EconomyPurchaseService service) =>
        service.Offers.Values.Where(offer => offer.Bundle.CurrencyId == 5).OrderBy(offer => offer.Bundle.ContentItemId).ToArray();

    private static EconomyOrderLine Line(EconomyPurchaseOffer offer, uint quantity = 1) =>
        new(offer.Bundle.BundleId, EconomyStoreUpdate.StoreId, quantity, "0", EconomyOrderLine.NativeDefaultOutOfBandData);

    private static EconomyOrderRequest Order(EconomyPurchaseOffer offer, string currency = "KS$", uint quantity = 1) =>
        new(EconomyOrderRequest.Place, "ignored-account-string", "4097", 1, "en_US", "", currency, "", [Line(offer, quantity)]);

    private static void AssertUnchanged(AccountEconomySnapshot expected, AccountEconomySnapshot actual)
    {
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.Balances.OrderBy(pair => pair.Key), actual.Balances.OrderBy(pair => pair.Key));
        Assert.Equal(expected.Items, actual.Items);
        Assert.Equal(expected.Receipts.OrderBy(pair => pair.Key), actual.Receipts.OrderBy(pair => pair.Key));
        Assert.Equal(expected.States.OrderBy(pair => pair.Key), actual.States.OrderBy(pair => pair.Key));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
