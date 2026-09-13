using System.Text.Json;
using Cranberry.Zone;
using Cranberry.Zone.Economy;

namespace Cranberry.Tests.Zone.Economy;

public sealed class EconomyPurchaseServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-purchases", Guid.NewGuid().ToString("N"));
    private static readonly EconomyCatalog Catalogue = new(
        [new(200, 300, "A", 5, 5, true), new(201, 301, "B", 6, 20, true)],
        [new(400, "Locked A", 1, 401, 250, [new(200, 300, 1, 1)], "test", 177),
            new(401, "Unlocked A", 1, 401, 0, [new(200, 300, 1, 1)], "test")],
        [new(200, 300, 1, 1), new(201, 301, 1, 1)], 100, "test");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AccountEconomyStore Store(uint scrap = 300, uint crowns = 1000)
    {
        var store = new AccountEconomyStore(_root);
        store.GetOrCreate("account", new(new Dictionary<uint, uint> { [1] = scrap, [4] = crowns },
            [new(123, 400, 0, 2, "migration", false)]));
        return store;
    }

    private static EconomyOrderRequest Order(uint track = 1, uint bundle = 244, uint quantity = 1, string currency = "SCP") =>
        new(EconomyOrderRequest.Place, "ignored-account-string", "4097", track, "en_US", "", currency, "",
            [new(bundle, 1, quantity, "0", "0")]);

    [Fact]
    public void PreviewAndCancellationDoNotSpendOrChooseAReward()
    {
        var store = Store();
        var service = new EconomyPurchaseService(store, Catalogue);
        var quote = service.Quote("account", 4097, Order() with { SubOpcode = EconomyOrderRequest.Preview });
        Assert.True(quote.Allowed);
        Assert.Equal(100u, quote.Total);
        Assert.Equal(300u, store.GetOrCreate("account").Balance(1));
        Assert.Empty(store.GetOrCreate("account").Receipts);
        Assert.Null(service.LastResult("account", 4097, 244));
    }

    [Fact]
    public void ScrapPurchasePersistsOneDrawAndRecoversTheSameRevealAfterRestart()
    {
        var store = Store();
        var service = new EconomyPurchaseService(store, Catalogue);
        int draws = 0;
        AccountEconomyResult first = service.Place("account", "server-link", 4097, Order(), _ => { draws++; return 1; });
        Assert.True(first.Succeeded, first.Error);
        var replay = new EconomyPurchaseService(new AccountEconomyStore(_root), Catalogue)
            .Place("account", "server-link", 4097, Order(), _ => throw new Exception("Do not reroll."));
        Assert.True(replay.Replayed);
        Assert.Equal(1, draws);
        Assert.Equal(200u, replay.Snapshot!.Balance(1));
        var restored = new EconomyPurchaseService(new AccountEconomyStore(_root), Catalogue).LastResult("account", 4097, 244)!;
        Assert.Equal(first.Receipt!.ResultJson, JsonSerializer.Serialize(restored));
        Assert.Equal(201u, Assert.Single(restored.Award.Granted).AccountItemId);
        Assert.Equal(2, replay.Snapshot.Items.Count);
    }

    [Fact]
    public void NewClientLinkMayReuseTrackOneForANewIntentAndSameLinkCannotChangeTheOrder()
    {
        var store = Store();
        var service = new EconomyPurchaseService(store, Catalogue);
        Assert.True(service.Place("account", "first-client", 4097, Order(), _ => 0).Succeeded);
        var changed = service.Place("account", "first-client", 4097, Order(bundle: 177, currency: "KH$"));
        Assert.False(changed.Succeeded);
        Assert.True(service.Place("account", "new-client", 4097, Order(), _ => 1).Succeeded);
        Assert.Equal(100u, store.GetOrCreate("account").Balance(1));
        Assert.Equal(1000u, store.GetOrCreate("account").Balance(4));
    }

    [Fact]
    public async Task ConcurrentRetryDrawsOnceAndIndependentOrdersCannotOverspend()
    {
        var store = Store(scrap: 100);
        var service = new EconomyPurchaseService(store, Catalogue);
        int draws = 0;
        var retries = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            service.Place("account", "link", 4097, Order(), bound =>
            {
                Interlocked.Increment(ref draws);
                return 0;
            }))));
        Assert.All(retries, result => Assert.True(result.Succeeded, result.Error));
        Assert.Equal(1, retries.Count(result => !result.Replayed));
        Assert.Equal(1, draws);
        Assert.Single(retries.Select(result => result.Receipt!.ResultJson).Distinct());

        var otherOrders = await Task.WhenAll(Enumerable.Range(2, 8).Select(track => Task.Run(() =>
            service.Place("account", "link", 4097, Order((uint)track), _ => throw new Exception("No funds.")))));
        Assert.All(otherOrders, result => Assert.False(result.Succeeded));
        var account = store.GetOrCreate("account");
        Assert.Equal(0u, account.Balance(1));
        Assert.Single(account.Receipts);
    }

    [Fact]
    public void ReplayingEarlierOrderDoesNotReplaceTheLatestDurableResult()
    {
        var service = new EconomyPurchaseService(Store(), Catalogue);
        var first = service.Place("account", "link", 4097, Order(), _ => 0);
        var second = service.Place("account", "link", 4097, Order(2), _ => 1);
        var replay = service.Place("account", "link", 4097, Order(), _ => throw new Exception("Do not reroll."));
        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Receipt!.ResultJson, replay.Receipt!.ResultJson);
        Assert.Equal(second.Receipt!.ResultJson, JsonSerializer.Serialize(service.LastResult("account", 4097, 244)));
        Assert.Equal(100u, replay.Snapshot!.Balance(1));
    }

    [Fact]
    public void CrownUnlockConsumesLockedCopiesAndGrantsAFreeOpenCrate()
    {
        var store = Store();
        var service = new EconomyPurchaseService(store, Catalogue);
        var result = service.Place("account", "link", 4097, Order(bundle: 177, quantity: 2, currency: "KH$"));
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(500u, result.Snapshot!.Balance(4));
        var item = Assert.Single(result.Snapshot.Items);
        Assert.Equal(401u, item.AccountItemId);
        Assert.Equal(2u, item.Count);
        var opened = new AccountEconomyOperations(store, Catalogue).OpenCrates("account", "free-open", [new(401)], _ => 0);
        Assert.True(opened.Succeeded, opened.Error);
        Assert.Equal(500u, opened.Snapshot!.Balance(4));
        Assert.True(opened.Snapshot.Owns(200));
    }

    [Fact]
    public void InvalidQuoteAndInsufficientFundsLeaveBalancesAndInventoryUnchanged()
    {
        var store = Store(scrap: 99);
        var service = new EconomyPurchaseService(store, Catalogue);
        Assert.Equal(EconomyOrderResponse.InsufficientFunds, service.Quote("account", 4097, Order()).Result);
        Assert.False(service.Place("account", "link", 4097, Order()).Succeeded);
        Assert.False(service.Place("account", "link", 4097, Order(currency: "KH$")).Succeeded);
        Assert.False(service.Place("account", "link", 4097, Order() with { CharacterReference = "123" }).Succeeded);
        Assert.False(service.Place("account", "link", 4097, Order(bundle: 999)).Succeeded);
        Assert.False(service.Place("account", "link", 4097, Order(quantity: 2)).Succeeded);
        Assert.False(service.Place("account", "link", 4097, Order(bundle: 177, quantity: 3, currency: "KH$")).Succeeded);
        Assert.Equal(99u, store.GetOrCreate("account").Balance(1));
        Assert.Equal(2u, Assert.Single(store.GetOrCreate("account").Items).Count);
        Assert.Empty(store.GetOrCreate("account").Receipts);
    }

    [Fact]
    public void CrownUnlockTenAcceptsTenButRejectsElevenWithoutMutation()
    {
        var store = new AccountEconomyStore(_root);
        var initial = store.GetOrCreate("account", new(new Dictionary<uint, uint> { [4] = 2_750 },
            [new(123, 400, 0, 11, "test", false)]));
        var service = new EconomyPurchaseService(store, Catalogue);
        var excessive = Order(bundle: 177, quantity: 11, currency: "KH$");
        Assert.False(service.Quote("account", 4097, excessive).Allowed);
        Assert.False(service.Place("account", "link", 4097, excessive).Succeeded);
        Assert.Equal(initial.Revision, store.GetOrCreate("account").Revision);
        Assert.Equal(initial.Items, store.GetOrCreate("account").Items);

        var ten = Order(bundle: 177, quantity: 10, currency: "KH$");
        Assert.True(service.Quote("account", 4097, ten).Allowed);
        var result = service.Place("account", "link", 4097, ten);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(250u, result.Snapshot!.Balance(4));
        Assert.Equal(1u, result.Snapshot.Items.Single(item => item.AccountItemId == 400).Count);
        Assert.Equal(10u, result.Snapshot.Items.Single(item => item.AccountItemId == 401).Count);
        Assert.True(service.Place("account", "link", 4097, ten).Replayed);
        Assert.Equal(1u, service.Offers[244].Bundle.MaximumQuantity);
    }

    [Fact]
    public void LargeCrateStackStillRejectsPriceOverflowBeforeSpending()
    {
        var catalog = new EconomyCatalog(Catalogue.Skins.Values,
            Catalogue.Crates.Values.Select(crate => crate.CrownsCost > 0 ? crate with { CrownsCost = int.MaxValue } : crate),
            Catalogue.ScrapyardRewards, Catalogue.ScrapyardCost, "test");
        var store = new AccountEconomyStore(_root);
        var initial = store.GetOrCreate("account", new(new Dictionary<uint, uint> { [4] = AccountEconomyStore.MaximumValue },
            [new(123, 400, 0, 100, "test", false)]));
        var result = new EconomyPurchaseService(store, catalog).Place("account", "link", 4097,
            Order(bundle: 177, quantity: 10, currency: "KH$"));
        Assert.False(result.Succeeded);
        Assert.Equal("Order total exceeds its limit.", result.Error);
        Assert.Equal(initial.Revision, store.GetOrCreate("account").Revision);
        Assert.Equal(initial.Items, store.GetOrCreate("account").Items);
        Assert.Equal(initial.Balance(4), store.GetOrCreate("account").Balance(4));
    }

    [Fact]
    public void NativeDefaultOutOfBandMetadataIsAcceptedButRentalOrCreatorChangesAreRefused()
    {
        var service = new EconomyPurchaseService(Store(), Catalogue);
        EconomyOrderRequest native = Order() with
        {
            Lines = [new(244, 1, 1, "0", EconomyOrderLine.NativeDefaultOutOfBandData)],
        };
        Assert.True(service.Quote("account", 4097, native).Allowed);
        Assert.True(service.Place("account", "link", 4097, native).Succeeded);
        Assert.False(service.Quote("account", 4097, native with
        {
            Lines = [new(244, 1, 1, "0", "<OobData rentalTermId=\"1\" playerStudioId=\"0\"/>")],
        }).Allowed);
    }

    [Fact]
    public void FailureAfterRewardSelectionBeforeReplaceCannotChargeOrGrant()
    {
        Store();
        var failing = new AccountEconomyStore(_root, persistenceFault: stage =>
        {
            if (stage == EconomyPersistenceStage.AfterFlushBeforeReplace) throw new IOException("Injected crash.");
        });
        var service = new EconomyPurchaseService(failing, Catalogue);
        Assert.False(service.Place("account", "link", 4097, Order(), _ => 1).Succeeded);
        var restored = new AccountEconomyStore(_root).GetOrCreate("account");
        Assert.Equal(300u, restored.Balance(1));
        Assert.Single(restored.Items);
        Assert.Empty(restored.Receipts);
        Assert.Empty(restored.States);
    }

    [Fact]
    public void ProductionOffersHaveOneUnambiguousPaidUnlockAndOneScrapOffer()
    {
        var service = new EconomyPurchaseService(Store());
        Assert.Equal(100u, service.Offers[244].Bundle.Price);
        Assert.True(service.Offers.Count > 2);
        Assert.All(service.Offers.Values.Where(offer => offer.LockedCrate is not null), offer =>
        {
            Assert.Equal(4u, offer.Bundle.CurrencyId);
            Assert.Equal(offer.LockedCrate!.UnlockedItemId, offer.Bundle.ContentItemId);
            Assert.Equal(0u, EconomyCatalog.Default.Crates[offer.Bundle.ContentItemId].CrownsCost);
        });
    }
}
