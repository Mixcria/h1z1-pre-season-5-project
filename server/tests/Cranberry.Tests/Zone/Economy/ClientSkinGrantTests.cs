using Cranberry.Zone.Economy;

namespace Cranberry.Tests.Zone.Economy;

public sealed class ClientSkinGrantTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-client-skins", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FreshAccountGetsOneOfEverySkinExceptTheSevenRequestedEventItems()
    {
        uint[] excluded = [2797, 2888, 3752, 3876, 4075, 4076, 4077];
        var result = ClientSkinGrant.Apply(new AccountEconomyStore(_root), "friend");
        Assert.True(result.Succeeded, result.Error);
        var account = result.Snapshot!;
        Assert.Equal(EconomyCatalog.Default.Skins.Keys.Except(excluded).Order(),
            account.Items.Select(item => item.AccountItemId).Order());
        Assert.All(excluded, id => Assert.False(account.Owns(id)));
        Assert.True(account.Owns(2780)); // Ordinary Showdown Crate AR-15 is included.
        Assert.All(account.Items, item =>
        {
            var skin = EconomyCatalog.Default.Skins[item.AccountItemId];
            Assert.Equal(1u, item.Count);
            Assert.Equal(skin.RewardItemId, item.RewardItemId);
            Assert.Equal(skin.CanScrap, item.Scrappable);
            Assert.Equal(ClientSkinGrant.OperationId, item.Source);
        });
        Assert.Empty(account.Balances);
        Assert.Single(account.Receipts);
    }

    [Fact]
    public void ExistingHoldingsAndSpentStarterPackageRemainIntact()
    {
        var store = new AccountEconomyStore(_root);
        Assert.True(StarterAccountProfile.Apply(store, "friend").Succeeded);
        var before = store.Execute("friend", "previous-play", "test", draft =>
        {
            draft.Debit(4, 50_000);
            draft.Credit(1, 123);
            var crate = draft.Items.First(item => item.AccountItemId == StarterAccountProfile.CrateFamilies[0].ItemId);
            draft.Consume(crate.InstanceId, 10);
            draft.Grant(2780, 2781, 3, "earned");
            draft.Grant(3752, 3720, 2, "previous-event-award");
            draft.SetState("progress", "preserved");
            return null;
        }).Snapshot!;

        var result = ClientSkinGrant.Apply(store, "friend");
        Assert.True(result.Succeeded, result.Error);
        var after = result.Snapshot!;
        Assert.All(before.Items, item => Assert.Contains(item, after.Items));
        Assert.Equal(before.Balances.OrderBy(row => row.Key), after.Balances.OrderBy(row => row.Key));
        Assert.Equal(before.States.OrderBy(row => row.Key), after.States.OrderBy(row => row.Key));
        Assert.All(before.Receipts, row => Assert.Equal(row.Value, after.Receipts[row.Key]));
        Assert.Equal(3u, Assert.Single(after.Items, item => item.AccountItemId == 2780).Count);
        Assert.Equal(2u, Assert.Single(after.Items, item => item.AccountItemId == 3752).Count);
        Assert.Single(after.Items, item => item.AccountItemId == 3652);
        Assert.All(after.Items.Except(before.Items), item =>
        {
            Assert.Equal(1u, item.Count);
            Assert.DoesNotContain(item.AccountItemId, new uint[] { 2797, 2888, 3752, 3876, 4075, 4076, 4077 });
        });
    }

    [Fact]
    public void ReconnectAndRestartDoNotDuplicateOrReplaceASpentGrant()
    {
        var store = new AccountEconomyStore(_root);
        var first = ClientSkinGrant.Apply(store, "friend");
        Assert.True(first.Succeeded, first.Error);
        var item = first.Snapshot!.Items.Single(item => item.AccountItemId == 2780);
        var spent = store.Execute("friend", "scrap-once", "test", draft =>
        {
            draft.Consume(item.InstanceId, 1);
            return null;
        }).Snapshot!;
        string path = Path.Combine(_root, AccountEconomyStore.FileNameFor("friend"));
        byte[] bytes = File.ReadAllBytes(path);
        var replay = ClientSkinGrant.Apply(new AccountEconomyStore(_root), "friend");
        Assert.True(replay.Succeeded, replay.Error);
        Assert.True(replay.Replayed);
        Assert.Equal(spent.Revision, replay.Snapshot!.Revision);
        Assert.False(replay.Snapshot.Owns(2780));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.True(ClientSkinGrant.Apply(store, "other-account").Snapshot!.Owns(2780));
    }

    [Fact]
    public void FailedWriteCommitsNothingAndCanBeRetried()
    {
        var store = new AccountEconomyStore(_root);
        var before = StarterAccountProfile.Apply(store, "friend").Snapshot!;
        string path = Path.Combine(_root, AccountEconomyStore.FileNameFor("friend"));
        byte[] bytes = File.ReadAllBytes(path);
        var broken = new AccountEconomyStore(_root, persistenceFault: stage =>
        {
            if (stage == EconomyPersistenceStage.AfterFlushBeforeReplace) throw new IOException("test disk failure");
        });
        Assert.False(ClientSkinGrant.Apply(broken, "friend").Succeeded);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(store.GetOrCreate("friend").Receipts.ContainsKey(ClientSkinGrant.OperationId));
        Assert.True(ClientSkinGrant.Apply(store, "friend").Succeeded);
        Assert.Equal(before.Revision + 1, store.GetOrCreate("friend").Revision);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
