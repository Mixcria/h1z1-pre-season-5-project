using Cranberry.Zone.Economy;

namespace Cranberry.Tests.Zone;

public sealed class EconomyCatalogTests
{
    [Fact]
    public void SkinScrapUsesTheOwnedSourceAndExactClientOverride()
    {
        Assert.True(EconomyCatalog.Default.TryGetSkin(1812, out EconomySkin blueFlannel));
        Assert.Equal(2054u, blueFlannel.RewardItemId);
        Assert.Equal(20, blueFlannel.ScrapValue);
        Assert.True(blueFlannel.CanScrap);
        Assert.False(EconomyCatalog.Default.TryGetSkin(2054, out _));
        Assert.Equal(1000, EconomyCatalog.Default.Skins[1806].ScrapValue);
        Assert.Contains(EconomyCatalog.Default.Skins.Values, s => s.ScrapValue == -1 && !s.CanScrap);
    }

    [Fact]
    public void LegacyPredatorUsesItsActualKeyAndModernLockedPredatorUsesCrowns()
    {
        var legacy = EconomyCatalog.Default.Crates[3207];
        Assert.Equal(2837u, legacy.KeyItemId);
        Assert.Equal(0u, legacy.CrownsCost);
        Assert.Equal(0u, legacy.UnlockBundleId);
        Assert.Equal(0u, EconomyCatalog.Default.Crates[3620].KeyItemId);
        Assert.Equal(legacy.Rewards, EconomyCatalog.Default.Crates[3620].Rewards);
    }

    [Fact]
    public void LockedAndUnlockedPredatorShareRewardsButOnlyUnlockingCostsCrowns()
    {
        EconomyCrate locked = EconomyCatalog.Default.Crates[3620];
        EconomyCrate unlocked = EconomyCatalog.Default.Crates[3208];
        Assert.Equal(3208u, locked.UnlockedItemId);
        Assert.Equal(250u, locked.CrownsCost);
        Assert.True(locked.UnlockBundleId > 0);
        Assert.Equal(0u, unlocked.UnlockBundleId);
        Assert.Equal(0u, unlocked.CrownsCost);
        Assert.Equal(locked.Rewards, unlocked.Rewards);
        Assert.NotEqual(unlocked.RewardSetId, EconomyCatalog.Default.Crates[3211].RewardSetId);
        Assert.True(EconomyCatalog.Default.Crates[3211].Rewards.Count < unlocked.Rewards.Count);
    }

    [Fact]
    public void EveryRewardResolvesToARealOwnedAppearanceAndScrapyardIsSeparate()
    {
        EconomyCatalog catalog = EconomyCatalog.Default;
        foreach (EconomyReward reward in catalog.Crates.Values.SelectMany(c => c.Rewards).Concat(catalog.ScrapyardRewards))
        {
            Assert.Equal(catalog.Skins[reward.AccountItemId].RewardItemId, reward.RewardItemId);
            Assert.True(reward.Weight > 0);
            Assert.True(catalog.Skins[reward.AccountItemId].NameLocaleId > 0);
            Assert.True(catalog.Skins[reward.AccountItemId].ImageSetId > 0);
        }
        Assert.Equal(100u, catalog.ScrapyardCost);
        Assert.Equal(41, catalog.ScrapyardRewards.Count);
        Assert.DoesNotContain(catalog.ScrapyardRewards, r => r.AccountItemId == 1812);
        Assert.DoesNotContain(catalog.Crates.Values, c => c.Name.Contains("H1EMU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WeightedDrawHonoursEveryBoundaryAndRejectsOutOfRangeTickets()
    {
        EconomyReward[] pool = [new(1, 10, 1, 2), new(2, 20, 1, 3), new(3, 30, 1, 1)];
        Assert.Equal(1u, EconomyCatalog.Roll(pool, max => { Assert.Equal(6, max); return 0; }).AccountItemId);
        Assert.Equal(1u, EconomyCatalog.Roll(pool, _ => 1).AccountItemId);
        Assert.Equal(2u, EconomyCatalog.Roll(pool, _ => 2).AccountItemId);
        Assert.Equal(2u, EconomyCatalog.Roll(pool, _ => 4).AccountItemId);
        Assert.Equal(3u, EconomyCatalog.Roll(pool, _ => 5).AccountItemId);
        Assert.Throws<ArgumentOutOfRangeException>(() => EconomyCatalog.Roll(pool, _ => -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EconomyCatalog.Roll(pool, _ => 6));
    }

    [Fact]
    public void NemesisUsesItsNamedReferenceMembershipAndAugustOwnership()
    {
        EconomyCatalog catalog = EconomyCatalog.Default;
        EconomyCrate crate = catalog.Crates[4161];
        Assert.Equal(23, crate.Rewards.Count);
        Assert.Contains(crate.Rewards, reward => catalog.Skins[reward.AccountItemId].Name == "Nemesis AR-15");
        Assert.Contains(crate.Rewards, reward => catalog.Skins[reward.AccountItemId].Name == "Nautilus 12GA Pump Shotgun");
        Assert.Contains(crate.Rewards, reward => reward.AccountItemId == 4326);
        Assert.All(catalog.Crates[4164].Rewards, reward => Assert.Equal(8u, catalog.Skins[reward.AccountItemId].RarityId));
        Assert.Equal(crate.Rewards, catalog.Crates[4160].Rewards);
        Assert.Contains("ADOPTED cached SurvivorsRest", crate.Provenance);
        Assert.Contains("DESIGN rarity weights", crate.Provenance);
    }

    [Fact]
    public void InvalidCatalogNeverEnablesAnEmptyOrMismatchedRewardPool()
    {
        EconomySkin[] skins = [new(1, 10, "Skin", 5, 20, true)];
        EconomyReward[] valid = [new(1, 10, 1, 1)];
        Assert.Throws<InvalidDataException>(() => new EconomyCatalog(skins,
            [new(2, "Broken crate", 100, 2, 0, [], "test")], valid, 100, "test"));
        Assert.Throws<InvalidDataException>(() => new EconomyCatalog(skins,
            [], [new(1, 999, 1, 1)], 100, "test"));
        Assert.Throws<InvalidDataException>(() => new EconomyCatalog(skins,
            [], valid, 0, "test"));
    }
}
