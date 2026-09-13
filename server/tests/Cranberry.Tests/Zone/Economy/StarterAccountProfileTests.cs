using Cranberry.Zone;
using Cranberry.Zone.Economy;

namespace Cranberry.Tests.Zone.Economy;

public sealed class StarterAccountProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-starter", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FreshAccountGetsOnlyBasicApparelAndEveryLockedCrateCanBePurchased()
    {
        var store = new AccountEconomyStore(_root);
        var result = StarterAccountProfile.Apply(store, "friend");
        Assert.True(result.Succeeded, result.Error);
        var account = result.Snapshot!;
        Assert.Equal(200_000u, account.Balance(4));
        var skins = account.Items.Where(item => EconomyCatalog.Default.Skins.ContainsKey(item.AccountItemId)).ToArray();
        Assert.Equal(StarterAccountProfile.SkinItemIds.Order(), skins.Select(item => item.AccountItemId).Order());
        Assert.All(skins, item =>
        {
            Assert.Equal(1u, item.Count);
            Assert.False(item.Scrappable);
            var skin = EconomyCatalog.Default.Skins[item.AccountItemId];
            Assert.Equal(0u, skin.RarityId);
            Assert.False(skin.CanScrap);
            Assert.True(AugustWardrobeCatalog.TryResolveClicked(item.AccountItemId, out var entry));
            Assert.True(AugustWardrobeCatalog.ProjectsOntoStarterBody(entry));
            Assert.False(string.IsNullOrWhiteSpace(entry.ModelNameFor(1)));
            Assert.False(string.IsNullOrWhiteSpace(entry.ModelNameFor(2)));
            var baseline = Cranberry.Zone.Inventory.SurvivorStarterOutfit.Pieces
                .FirstOrDefault(piece => piece.BodySlotId == entry.EquipmentSlotId);
            if (baseline.ItemDefinitionId != 0 && baseline.ItemDefinitionId != entry.RewardItemId)
                foreach (uint gender in new uint[] { 1, 2 })
                {
                    string mesh = baseline.MeshName.Replace("<gender>", gender == 1 ? "Male" : "Female");
                    Assert.True(Cranberry.Zone.Appearance.AugustWornSkins.TryResolve(baseline.ItemDefinitionId,
                        gender, mesh, [entry], retintOnly: true, out var selected, out string reason), reason);
                    Assert.Equal(entry.AccountItemId, selected.AccountItemId);
                }
        });
        var purchases = new EconomyPurchaseService(store);
        var crates = EconomyMenuInventory.VisibleItems(account.Items, purchases.Offers)
            .Where(item => EconomyCatalog.Default.Crates.ContainsKey(item.AccountItemId)).ToArray();
        Assert.Equal(31, crates.Length);
        Assert.All(crates, item =>
        {
            Assert.Equal(500u, item.Count);
            var crate = EconomyCatalog.Default.Crates[item.AccountItemId];
            Assert.Equal(250u, crate.CrownsCost);
            foreach (uint quantity in new uint[] { 1, 10 })
            {
                var order = new EconomyOrderRequest(EconomyOrderRequest.Preview, "", "4097", 1,
                    "en_US", "", "KH$", "", [new(crate.UnlockBundleId, 1, quantity, "0",
                        EconomyOrderLine.NativeDefaultOutOfBandData)]);
                var quote = purchases.Quote("friend", 4097, order);
                Assert.True(quote.Allowed, quote.Error);
                Assert.Equal(quantity * 250, quote.Total);
            }
        });
    }

    [Fact]
    public void PurchaseAndReconnectDoNotRefillThePackageOrGrantPremiumSkins()
    {
        var store = new AccountEconomyStore(_root);
        Assert.True(StarterAccountProfile.Apply(store, "friend").Succeeded);
        var crate = StarterAccountProfile.CrateFamilies[0];
        var purchases = new EconomyPurchaseService(store);
        var order = new EconomyOrderRequest(EconomyOrderRequest.Place, "", "4097", 1,
            "en_US", "", "KH$", "", [new(crate.UnlockBundleId, 1, 10, "0",
                EconomyOrderLine.NativeDefaultOutOfBandData)]);
        var bought = purchases.Place("friend", "test-link", 4097, order);
        Assert.True(bought.Succeeded, bought.Error);
        var replay = StarterAccountProfile.Apply(new AccountEconomyStore(_root), "friend");
        Assert.True(replay.Replayed);
        Assert.Equal(bought.Snapshot!.Revision, replay.Snapshot!.Revision);
        Assert.Equal(197_500u, replay.Snapshot.Balance(4));
        Assert.Equal(490u, Assert.Single(replay.Snapshot.Items, item => item.AccountItemId == crate.ItemId).Count);
        Assert.Equal(10u, Assert.Single(replay.Snapshot.Items, item => item.AccountItemId == crate.UnlockedItemId).Count);
        Assert.Equal(bought.Snapshot.Items, replay.Snapshot.Items);
    }

    [Fact]
    public void ExistingItemsProgressAndOtherCurrencyArePreservedWithoutDuplicateBasics()
    {
        var store = new AccountEconomyStore(_root);
        var premium = EconomyCatalog.Default.Skins.Values.First(skin => skin.RarityId == 8);
        var basic = EconomyCatalog.Default.Skins[3652];
        var before = store.Execute("friend", "existing", "test", draft =>
        {
            draft.Credit(4, 250_000); draft.Credit(5, 123); draft.Credit(6, 175);
            draft.Grant(premium.AccountItemId, premium.RewardItemId, 2, "earned");
            draft.Grant(basic.AccountItemId, basic.RewardItemId, 1, "old-basic", false);
            draft.SetState("progress", "existing-progress");
            return null;
        }).Snapshot!;
        var after = StarterAccountProfile.Apply(store, "friend").Snapshot!;
        Assert.Equal(250_000u, after.Balance(4));
        Assert.Equal(123u, after.Balance(5));
        Assert.Equal(175u, after.Balance(6));
        Assert.Equal("existing-progress", after.States["progress"]);
        Assert.All(before.Items, item => Assert.Contains(item, after.Items));
        Assert.Single(after.Items, item => item.AccountItemId == basic.AccountItemId);
        var second = StarterAccountProfile.Apply(store, "other").Snapshot!;
        Assert.Equal(200_000u, second.Balance(4));
        Assert.False(second.Owns(premium.AccountItemId));
    }

    [Fact]
    public void FailedCommitDoesNotLeavePartialFundingOrInventory()
    {
        var store = new AccountEconomyStore(_root);
        store.GetOrCreate("friend");
        var broken = new AccountEconomyStore(_root, persistenceFault: stage =>
        {
            if (stage == EconomyPersistenceStage.AfterFlushBeforeReplace) throw new IOException("test disk failure");
        });
        Assert.False(StarterAccountProfile.Apply(broken, "friend").Succeeded);
        Assert.Empty(store.GetOrCreate("friend").Items);
        Assert.Equal(0u, store.GetOrCreate("friend").Balance(4));
        Assert.Equal(0, store.GetOrCreate("friend").Revision);
        Assert.True(StarterAccountProfile.Apply(store, "friend").Succeeded);
        Assert.Equal(200_000u, store.GetOrCreate("friend").Balance(4));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
