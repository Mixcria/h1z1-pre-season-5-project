using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchEndgame;

public sealed class RankedScoringTests
{
    [Theory]
    [InlineData(1, 175000)]
    [InlineData(2, 130000)]
    [InlineData(10, 83570)]
    [InlineData(175, 1000)]
    [InlineData(0, 0)]
    [InlineData(176, 0)]
    public void PublishedPlacementPoints(uint placement, int expected) =>
        Assert.Equal(expected, RankedScoring.PlacementPoints(placement));

    [Theory]
    [InlineData(1, RankedTier.Bronze)]
    [InlineData(375000, RankedTier.Silver)]
    [InlineData(535000, RankedTier.Gold)]
    [InlineData(765000, RankedTier.Platinum)]
    [InlineData(1000000, RankedTier.Diamond)]
    [InlineData(1750000, RankedTier.Master)]
    [InlineData(1800000, RankedTier.Royalty)]
    public void TenMatchesRequiredAndPublishedTierBoundaries(int points, RankedTier tier)
    {
        Assert.Equal(RankedTier.Unranked, RankedScoring.Badge(points, 9, MatchMode.Solo).Tier);
        Assert.Equal(new RankBadge(tier), RankedScoring.Badge(points, 10, MatchMode.Solo));
        if (points > 1) Assert.Equal((RankedTier)((int)tier - 1), RankedScoring.Badge(points - 1, 10, MatchMode.Solo).Tier);
    }

    [Fact]
    public void BestTenAndAllTimeTotalsSurviveRestartAndReplayOfAnUnrankedResult()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-rank-test-" + Guid.NewGuid());
        try
        {
            var store = new RankedScoreStore(root);
            for (int n = 1; n <= 10; n++) store.Complete("player", MatchMode.Solo, new("win" + n, 1, 5, 180000));
            var low = new RankedResult("low", 175, 0, 1000);
            store.Complete("player", MatchMode.Solo, low);
            store = new RankedScoreStore(root);
            var profile = store.Complete("player", MatchMode.Solo, low);
            Assert.Equal(11, profile.Matches); Assert.Equal(10, profile.Best.Length);
            Assert.Equal(1800000, profile.Points); Assert.Equal(1801000u, profile.TotalPoints);
            Assert.Equal(10u, profile.Wins); Assert.Equal(50u, profile.TotalKills);
            Assert.Equal(RankedTier.Royalty, profile.Badge(MatchMode.Solo).Tier);
            Assert.Equal(0, store.Read("player", MatchMode.Duos).Matches);
            Assert.Equal(0, store.Read("other", MatchMode.Solo).Matches);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ResetReopensScoringAndClearsPracticeFlag()
    {
        var score = new MatchScore { PracticeSession = true };
        Assert.True(score.Credit(1, true)); Assert.False(score.Credit(1, true));
        score.Settled = true; Assert.False(score.Credit(2, false));
        score.Reset(); Assert.False(score.PracticeSession); Assert.Equal(0, score.Kills);
        Assert.True(score.Credit(1, false)); Assert.Equal(0, score.PracticeKills);
    }

    [Fact]
    public void RetailFeedCarriesVictimThenKillerWithTheirSeparateRanksAndHeadshot()
    {
        using var w = new PacketWriter();
        new RetailKillFeed(new(20, "Dummy", new(RankedTier.Royalty, 1)),
            new(10, "Admin", new(RankedTier.Staff)), 2425, Headshot: true).WriteTo(w);
        var r = new PacketReader(w.Written);
        Assert.Equal(0xce, r.ReadByte()); Assert.Equal(14, r.ReadUInt16());
        Assert.Equal(8, r.ReadByte()); Assert.Equal(2u, r.ReadUInt32());
        foreach (var expected in new[] { (20UL, "Dummy", 7u, 1u), (10UL, "Admin", 8u, 5u) })
        {
            Assert.Equal(expected.Item1, r.ReadUInt64()); r.Skip(12);
            Assert.Equal(expected.Item2, r.ReadString());
            for (int i = 0; i < 3; i++) Assert.Equal("", r.ReadString());
            r.Skip(8); Assert.Equal(0, r.ReadByte()); r.Skip(8);
            Assert.Equal(expected.Item3, r.ReadUInt32()); Assert.Equal(expected.Item4, r.ReadUInt32());
        }
        Assert.Equal(2425u, r.ReadUInt32()); r.Skip(16); Assert.True(r.ReadBool());
        Assert.Equal("BR.PlayerKilledPlayerWith", r.ReadString()); Assert.Equal("", r.ReadString()); Assert.True(r.AtEnd);
    }

    [Fact]
    public void EnvironmentalFeedUsesTheFirstLocaleStringAndHasNoKiller()
    {
        using var w = new PacketWriter();
        new RetailKillFeed(new(1, "Victim", new(RankedTier.Bronze)), null,
            DeathMessage: "BR.ChokedOnToxicGas", SelfDeathMessage: "BR.KilledSelfToxicGas").WriteTo(w);
        var r = new PacketReader(w.Written);
        r.Skip(3); Assert.Equal(0, r.ReadByte()); Assert.Equal(1u, r.ReadUInt32());
        r.Skip(20); Assert.Equal("Victim", r.ReadString());
        for (int i = 0; i < 3; i++) r.ReadString();
        r.Skip(25 + 21);
        Assert.Equal("BR.ChokedOnToxicGas", r.ReadString());
        Assert.Equal("BR.KilledSelfToxicGas", r.ReadString()); Assert.True(r.AtEnd);
    }

    [Fact]
    public void ResultAndRankingPacketsExposePlacementKillsAndBestTenToRetailUi()
    {
        using var w = new PacketWriter();
        new MatchScoreUpdate(10, 20, 1, 5, 1, true).WriteTo(w);
        var r = new PacketReader(w.Written);
        Assert.Equal(109, w.Written.Length); r.Skip(18);
        Assert.Equal(1u, r.ReadUInt32()); Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal(175000u, r.ReadUInt32()); Assert.Equal(5u, r.ReadUInt32());
        Assert.Equal(5000u, r.ReadUInt32()); Assert.Equal(1000u, r.ReadUInt32());
        r.Skip(8); Assert.Equal(180000u, r.ReadUInt32());
        var store = new RankedScoreStore(null);
        var profile = store.Complete("a", MatchMode.Solo, new("one", 1, 5, 180000));
        using var ranking = new PacketWriter();
        new MatchRankingReply(1, profile, 10).WriteTo(ranking);
        var rr = new PacketReader(ranking.Written);
        Assert.Equal(163, ranking.Written.Length); rr.Skip(35);
        Assert.Equal(1, rr.ReadInt32()); rr.Skip(8);
        Assert.Equal(180000u, rr.ReadUInt32()); Assert.Equal(1u, rr.ReadUInt32());
        Assert.Equal(5u, rr.ReadUInt32()); rr.Skip(16 + 24);
        Assert.Equal(180000u, rr.ReadUInt32());
    }
}
