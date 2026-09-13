using System.Text.Json;
using Cranberry.Zone;
using Cranberry.Zone.Economy;

namespace Cranberry.Tests.Zone.Economy;

public sealed class AccountEconomyOperationsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cranberry-economy-operation-tests", Guid.NewGuid().ToString("N"));
    private AccountEconomyStore NewStore() => new(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static AccountEconomySeed Seed(uint scrap = 0, uint crowns = 0, params OwnedAccountItem[] items) =>
        new(new Dictionary<uint, uint> { [1] = scrap, [4] = crowns }, items);

    private static EconomyAwardReceipt Receipt(AccountEconomyResult result) =>
        JsonSerializer.Deserialize<EconomyAwardReceipt>(result.Receipt!.ResultJson!)!;

    [Fact]
    public void BatchScrapConsumesCountsAcrossInstancesAndReplaysAfterRestart()
    {
        var store = NewStore();
        store.GetOrCreate("a", Seed(100, 0, new OwnedAccountItem(10, 1812, 2054, 1, "test"),
            new OwnedAccountItem(11, 1812, 2054, 2, "test")));
        var result = new AccountEconomyOperations(store).ScrapBatch("a", "batch", [new(1812, 2)]);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(140u, result.Snapshot!.Balance(1));
        Assert.Equal(1u, Assert.Single(result.Snapshot.Items).Count);
        Assert.Equal(2, Receipt(result).Consumed.Count);
        var replay = new AccountEconomyOperations(NewStore()).ScrapBatch("a", "batch", [new(1812, 2)]);
        Assert.True(replay.Replayed);
        Assert.Equal(result.Receipt, replay.Receipt);
        Assert.Equal(140u, replay.Snapshot!.Balance(1));
    }

    [Theory]
    [InlineData(false, 0u)]
    [InlineData(true, 0u)]
    [InlineData(false, AccountEconomyStore.MaximumValue)]
    public void BatchScrapRollsBackEveryConsumptionOnInvalidTailOrOverflow(bool protectedCopy, uint balance)
    {
        var store = NewStore();
        var initial = store.GetOrCreate("a", Seed(balance, 0,
            new OwnedAccountItem(10, 1812, 2054, 1, "test"),
            new OwnedAccountItem(11, 1812, 2054, 1, "test", Scrappable: !protectedCopy)));
        AccountRewardRow[] rows = balance > 0 || protectedCopy ? [new(1812, 2)] : [new(1812, 1), new(uint.MaxValue, 1)];
        var result = new AccountEconomyOperations(store).ScrapBatch("a", "invalid", rows);
        Assert.False(result.Succeeded);
        var saved = NewStore().GetOrCreate("a");
        Assert.Equal(initial.Revision, saved.Revision);
        Assert.Equal(balance, saved.Balance(1));
        Assert.Equal(initial.Items, saved.Items);
    }

    [Fact]
    public void RealBlueFlannelDuplicateScrapCreditsTwentyAndPersistsTheRemainingCopy()
    {
        AccountEconomyStore store = NewStore();
        store.GetOrCreate("a", Seed(100, 0, new OwnedAccountItem(10, 1812, 2054, 2, "test")));
        AccountEconomyResult result = new AccountEconomyOperations(store).Scrap("a", "scrap1", 1812, 2);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(120u, result.Snapshot!.Balance(1));
        OwnedAccountItem remaining = Assert.Single(result.Snapshot.Items);
        Assert.Equal(1u, remaining.Count);
        Assert.Equal(1812u, remaining.AccountItemId);
        Assert.Equal(2054u, remaining.RewardItemId);
        EconomyAwardReceipt receipt = Receipt(result);
        Assert.Equal(20, receipt.CurrencyChanges[1]);
        Assert.Equal(1u, Assert.Single(receipt.Consumed).Count);
        Assert.Empty(receipt.Granted);
        Assert.Equal(120u, NewStore().GetOrCreate("a").Balance(1));
        Assert.Equal(1u, Assert.Single(NewStore().GetOrCreate("a").Items).Count);
    }

    [Fact]
    public void ReplayReturnsTheOriginalScrapReceiptWithoutConsumingTheSecondCopy()
    {
        AccountEconomyStore store = NewStore();
        store.GetOrCreate("a", Seed(0, 0, new OwnedAccountItem(10, 1812, 2054, 2, "test")));
        AccountEconomyResult first = new AccountEconomyOperations(store).Scrap("a", "same", 1812, 2);
        AccountEconomyResult replay = new AccountEconomyOperations(NewStore()).Scrap("a", "same", 1812, 2);
        Assert.True(replay.Succeeded, replay.Error);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Receipt, replay.Receipt);
        Assert.Equal(20u, replay.Snapshot!.Balance(1));
        Assert.Equal(1u, Assert.Single(replay.Snapshot.Items).Count);
    }

    [Fact]
    public void WrongAccountUnownedIdRewardIdAndStaleStackNeverEarnScrap()
    {
        AccountEconomyStore store = NewStore();
        AccountEconomySnapshot initial = store.GetOrCreate("a", Seed(30, 0, new OwnedAccountItem(10, 1812, 2054, 2, "test")));
        store.GetOrCreate("b", Seed());
        var operations = new AccountEconomyOperations(store);
        Assert.False(operations.Scrap("b", "wrong-owner", 1812).Succeeded);
        Assert.False(operations.Scrap("a", "unowned", 1806).Succeeded);
        Assert.False(operations.Scrap("a", "reward-id", 2054).Succeeded);
        Assert.False(operations.Scrap("a", "stale", 1812, 1).Succeeded);
        AccountEconomySnapshot unchanged = store.GetOrCreate("a");
        Assert.Equal(initial.Revision, unchanged.Revision);
        Assert.Equal(30u, unchanged.Balance(1));
        Assert.Equal(2u, Assert.Single(unchanged.Items).Count);
        Assert.Equal(0u, store.GetOrCreate("b").Balance(1));
    }

    [Fact]
    public void NonScrappableCatalogueAndProtectedCopiesCannotBeConsumed()
    {
        EconomySkin disabled = EconomyCatalog.Default.Skins.Values.First(s => !s.CanScrap);
        AccountEconomyStore store = NewStore();
        store.GetOrCreate("a", Seed(0, 0,
            new OwnedAccountItem(10, disabled.AccountItemId, disabled.RewardItemId, 1, "test"),
            new OwnedAccountItem(11, 1812, 2054, 1, "protected", Scrappable: false)));
        var operations = new AccountEconomyOperations(store);
        Assert.False(operations.Scrap("a", "disabled", disabled.AccountItemId).Succeeded);
        Assert.False(operations.Scrap("a", "protected", 1812).Succeeded);
        Assert.Equal(2, store.GetOrCreate("a").Items.Count);
        Assert.Equal(0u, store.GetOrCreate("a").Balance(1));
    }

    [Fact]
    public void CurrencyOverflowRollsBackThePriorScrapConsumption()
    {
        AccountEconomyStore store = NewStore();
        store.GetOrCreate("a", Seed(AccountEconomyStore.MaximumValue, 0, new OwnedAccountItem(10, 1812, 2054, 1, "test")));
        AccountEconomyResult result = new AccountEconomyOperations(store).Scrap("a", "overflow", 1812);
        Assert.False(result.Succeeded);
        AccountEconomySnapshot restored = NewStore().GetOrCreate("a");
        Assert.Equal(AccountEconomyStore.MaximumValue, restored.Balance(1));
        Assert.Equal(1u, Assert.Single(restored.Items).Count);
        Assert.Empty(restored.Receipts);
    }

    [Fact]
    public void LockedPredatorConsumesOneCrateAndCrownsAndPersistsOnlyItsChosenReward()
    {
        AccountEconomyStore store = NewStore();
        store.GetOrCreate("a", Seed(90, 500, new OwnedAccountItem(10, 3620, 3620, 2, "test", Scrappable: false)));
        EconomyCrate crate = EconomyCatalog.Default.Crates[3620];
        int draws = 0;
        AccountEconomyResult first = new AccountEconomyOperations(store).OpenCrates("a", "open1", [new(3620)], _ => { draws++; return 0; });
        Assert.True(first.Succeeded, first.Error);
        Assert.Equal(250u, first.Snapshot!.Balance(4));
        Assert.Equal(90u, first.Snapshot.Balance(1));
        Assert.Equal(1u, first.Snapshot.Items.Single(i => i.AccountItemId == 3620).Count);
        OwnedAccountItem grant = Assert.Single(Receipt(first).Granted);
        Assert.Equal(crate.Rewards[0].AccountItemId, grant.AccountItemId);
        Assert.Equal(crate.Rewards[0].RewardItemId, grant.RewardItemId);
        Assert.Equal(-250, Receipt(first).CurrencyChanges[4]);
        AccountEconomyResult replay = new AccountEconomyOperations(NewStore()).OpenCrates("a", "open1", [new(3620)], _ => throw new InvalidOperationException("Must not reroll"));
        Assert.True(replay.Replayed);
        Assert.Equal(first.Receipt, replay.Receipt);
        Assert.Equal(1, draws);
        Assert.Equal(grant.InstanceId, replay.Snapshot!.Items.Single(i => i.AccountItemId == grant.AccountItemId).InstanceId);
    }

    [Fact]
    public void UnlockedCrateOpensWithoutCrownsAndTwoCopiesYieldTwoOwnedRewards()
    {
        AccountEconomyStore store = NewStore();
        store.GetOrCreate("a", Seed(10, 0, new OwnedAccountItem(10, 3208, 3208, 2, "test", Scrappable: false)));
        AccountEconomyResult result = new AccountEconomyOperations(store).OpenCrates("a", "open-two", [new(3208, 2)], _ => 0);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(0u, result.Snapshot!.Balance(4));
        Assert.DoesNotContain(result.Snapshot.Items, i => i.AccountItemId == 3208);
        Assert.Equal(2, Receipt(result).Granted.Count);
        Assert.Equal(2, result.Snapshot.Items.Count);
        Assert.Equal(2, result.Snapshot.Items.Select(i => i.InstanceId).Distinct().Count());
    }

    [Fact]
    public void InsufficientCrownsOrAnInvalidLaterCrateRollsBackEveryConsumptionAndGrant()
    {
        AccountEconomyStore store = NewStore();
        AccountEconomySnapshot initial = store.GetOrCreate("a", Seed(0, 249,
            new OwnedAccountItem(10, 3620, 3620, 1, "test", false), new OwnedAccountItem(11, 3208, 3208, 1, "test", false)));
        var operations = new AccountEconomyOperations(store);
        Assert.False(operations.OpenCrates("a", "too-poor", [new(3620)], _ => 0).Succeeded);
        Assert.False(operations.OpenCrates("a", "invalid-second", [new(3208), new(999999)], _ => 0).Succeeded);
        Assert.False(operations.OpenCrates("a", "too-many-owned", [new(3208, 2)], _ => 0).Succeeded);
        AccountEconomySnapshot restored = NewStore().GetOrCreate("a");
        Assert.Equal(initial.Revision, restored.Revision);
        Assert.Equal(initial.Items, restored.Items);
        Assert.Equal(249u, restored.Balance(4));
        Assert.Empty(restored.Receipts);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(101u)]
    [InlineData(uint.MaxValue)]
    public void InvalidOpeningQuantitiesNeverMutateTheAccount(uint quantity)
    {
        AccountEconomyStore store = NewStore();
        AccountEconomySnapshot initial = store.GetOrCreate("a", Seed(0, 0, new OwnedAccountItem(10, 3208, 3208, 200, "test", false)));
        AccountEconomyResult result = new AccountEconomyOperations(store).OpenCrates("a", "bad", [new(3208, quantity)], _ => 0);
        Assert.False(result.Succeeded);
        Assert.Equal(initial.Revision, store.GetOrCreate("a").Revision);
        Assert.Equal(200u, Assert.Single(store.GetOrCreate("a").Items).Count);
    }
}
