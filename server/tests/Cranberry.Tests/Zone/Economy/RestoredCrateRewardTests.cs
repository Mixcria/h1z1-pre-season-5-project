using System.Text.Json;
using Cranberry.Zone;
using Cranberry.Zone.Economy;

namespace Cranberry.Tests.Zone.Economy;

public sealed class RestoredCrateRewardTests
{
    [Fact]
    public void EveryAugustReferencePoolRetainsAllOriginalOutcomesAndWeights()
    {
        using var reference = JsonDocument.Parse(File.ReadAllBytes(
            "C:/h1z1project/reference/h1z1-server/data/2016/dataSources/AccountCrates.json"));
        var bagCrates = new Dictionary<uint, uint>
        {
            [1840] = 3812, [1879] = 3813, [2033] = 3814, [3239] = 3815,
            [1841] = 3816, [1880] = 3817, [3240] = 3818, [3274] = 3807,
            [3365] = 3808, [3447] = 3809, [3523] = 3810, [3633] = 3811,
        };
        int checkedPools = 0;
        foreach (var source in reference.RootElement.EnumerateArray())
        {
            uint id = source.GetProperty("itemDefinitionId").GetUInt32();
            uint crateId = bagCrates.GetValueOrDefault(id, id);
            if (!EconomyCatalog.Default.TryGetCrate(crateId, out var crate)) continue; // Non-August crate families.
            var expected = source.GetProperty("rewards").EnumerateArray().Select(r =>
                (Id: r.GetProperty("itemDefinitionId").GetUInt32(),
                 Weight: (uint)(r.GetProperty("rewardChance").GetDecimal() * 1000))).OrderBy(r => r.Id).ToArray();
            Assert.Equal(expected, crate.Rewards.Select(r => (Id: r.SourceItemId, r.Weight)).OrderBy(r => r.Id).ToArray());
            checkedPools++;
        }
        Assert.Equal(45, checkedPools);
    }

    [Theory]
    [InlineData(2326u, 2305u, 2296u)]
    [InlineData(2327u, 2303u, 2298u)]
    [InlineData(2328u, 2301u, 2293u)]
    [InlineData(2329u, 2302u, 2294u)]
    [InlineData(2330u, 2307u, 2297u)]
    [InlineData(2331u, 2304u, 2295u)]
    [InlineData(2332u, 2306u, 2292u)]
    [InlineData(2724u, 2680u, 2670u)]
    [InlineData(2725u, 2679u, 2669u)]
    public void BundledWinGrantsBothEquippablePiecesInOneAtomicDraw(uint sourceId, uint first, uint second)
    {
        uint crateId = sourceId < 2400 ? 3626u : 3628u;
        var catalog = EconomyCatalog.Default;
        var crate = catalog.Crates[crateId];
        var reward = Assert.Single(crate.Rewards, r => r.SourceItemId == sourceId);
        Assert.Equal(new[] { first, second }, EconomyCatalog.ExpandReward(reward).Select(r => r.AccountItemId));
        uint[] preview = EconomyCatalog.PreviewRewards(crate).Select(r => r.AccountItemId).ToArray();
        Assert.Contains(first, preview);
        Assert.Contains(second, preview);
        Assert.Equal(preview.Length, preview.Distinct().Count());
        var wardrobe = new AugustWardrobeState();
        foreach (uint item in new[] { first, second })
        {
            var entry = Assert.Single(AugustSkinCatalog.Apparel, entry => entry.AccountItemId == item
                && entry.RewardItemId == catalog.Skins[item].RewardItemId);
            Assert.True(wardrobe.TryApply(new(SkinItemSelectionRequest.RequestSetSkinItem, 0, 0, 1,
                entry.CategoryPrototypeId, item), out _, out _, out var reason), reason);
        }
        Assert.Equal(new[] { first, second }.Order(), wardrobe.BodySnapshot().Select(e => e.AccountItemId).Order());
        int ticket = checked((int)crate.Rewards.TakeWhile(r => r.SourceItemId != sourceId).Sum(r => (long)r.Weight));
        string root = Path.Combine(Path.GetTempPath(), "cranberry-bundled-reward", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AccountEconomyStore(root);
            store.GetOrCreate("account", new(Items: [new(1, crateId, 0, 1, "test", false)]));
            int draws = 0;
            var operations = new AccountEconomyOperations(store);
            var result = operations.OpenCrates("account", "open", [new(crateId)], _ => { draws++; return ticket; });
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(1, draws);
            Assert.Equal(new[] { first, second }.Order(), result.Snapshot!.Items.Select(i => i.AccountItemId).Order());
            Assert.All(result.Snapshot.Items, item => Assert.Equal(1u, item.Count));
            Assert.True(operations.OpenCrates("account", "open", [new(crateId)], _ => throw new Exception("No redraw")).Replayed);
            var reloaded = new AccountEconomyStore(root).GetOrCreate("account");
            Assert.Equal(result.Snapshot.Items, reloaded.Items);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FormerlyExcludedNativeItemsAreAvailableInTheirCorrectCrates()
    {
        var catalog = EconomyCatalog.Default;
        Assert.Contains(catalog.Crates[3623].Rewards, r => r.SourceItemId == 2497 && r.AccountItemId == 1821 && r.RewardItemId == 2064);
        Assert.Equal(22, catalog.Crates[3626].Rewards.Count);
        Assert.Equal(28, catalog.Crates[3628].Rewards.Count);
        Assert.Contains(catalog.Crates[3119].Rewards, r => r.AccountItemId == 3154);
        Assert.Contains(catalog.Crates[3624].Rewards, r => r.AccountItemId == 2005);
        Assert.Contains(catalog.Crates[3629].Rewards, r => r.AccountItemId == 2440);
        Assert.Contains(catalog.Crates[3821].Rewards, r => r.AccountItemId == 3743);
        Assert.Contains(catalog.Crates[3926].Rewards, r => r.AccountItemId == 2005);
        Assert.Contains(catalog.Crates[4161].Rewards, r => r.AccountItemId == 4326);
        Assert.Equal(23, catalog.Crates[4061].Rewards.Count);
        Assert.Contains(catalog.Crates[4061].Rewards, r => catalog.Skins[r.AccountItemId].Name == "Anarchy Motorcycle Helmet");
        Assert.Contains("ADOPTED cached SurvivorsRest Nomad", catalog.Crates[4061].Provenance);
    }
}

