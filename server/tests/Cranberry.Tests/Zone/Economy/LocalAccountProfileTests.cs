using Cranberry.Zone;
using Cranberry.Zone.Economy;

namespace Cranberry.Tests.Zone.Economy;

public sealed class LocalAccountProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-local-profile", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewAndPreviewAccountsGetAllSkinsAndVisibleCratesWithoutRefillingPurchases(bool previewClient)
    {
        var store = new AccountEconomyStore(_root);
        if (previewClient)
        {
            Assert.True(StarterAccountProfile.Apply(store, "local").Succeeded);
            Assert.True(ClientSkinGrant.Apply(store, "local").Succeeded);
        }
        else
            Assert.True(store.Execute("local", "saved", "test", draft =>
            { draft.SetState("progress", "keep"); draft.Credit(5, 123); return null; }).Succeeded);

        var result = LocalAccountProfile.Apply(store, "local");
        Assert.True(result.Succeeded, result.Error);
        var account = result.Snapshot!;
        Assert.All(EconomyCatalog.Default.Skins.Keys, id => Assert.True(account.Owns(id), $"Missing skin {id}"));
        var purchases = new EconomyPurchaseService(store);
        var crates = EconomyMenuInventory.VisibleItems(account.Items, purchases.Offers)
            .Where(item => EconomyCatalog.Default.Crates.ContainsKey(item.AccountItemId)).ToArray();
        Assert.Equal(31, crates.Length);
        Assert.All(crates, item => Assert.Equal(500u, item.Count));
        if (!previewClient)
        { Assert.Equal("keep", account.States["progress"]); Assert.Equal(123u, account.Balance(5)); }
        var crate = StarterAccountProfile.CrateFamilies[0];
        var order = new EconomyOrderRequest(EconomyOrderRequest.Place, "", "4097", 1,
            "en_US", "", "KH$", "", [new(crate.UnlockBundleId, 1, 10, "0", EconomyOrderLine.NativeDefaultOutOfBandData)]);
        var bought = purchases.Place("local", "test-link", 4097, order);
        Assert.True(bought.Succeeded, bought.Error);
        var replay = LocalAccountProfile.Apply(new AccountEconomyStore(_root), "local");
        Assert.True(replay.Replayed);
        Assert.Equal(bought.Snapshot!.Revision, replay.Snapshot!.Revision);
        Assert.Equal(197_500u, replay.Snapshot.Balance(4));
        Assert.Equal(490u, replay.Snapshot.Items.Single(item => item.AccountItemId == crate.ItemId).Count);
        Assert.Equal(bought.Snapshot.Items, replay.Snapshot.Items);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
