using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchDrop;

/// <summary>
/// The match seed and its salted sub-streams (docs/48 §5.1). The point of these is the property
/// that lets two lanes draw from one match without colliding: adding a draw to the gas must not
/// move the drop, and vice versa.
/// </summary>
public sealed class MatchSeedsTests
{
    [Fact]
    public void EachSaltGivesItsOwnStream()
    {
        for (ulong seed = 1; seed <= 100; seed++)
        {
            ulong gas = MatchSeeds.For(seed, MatchSeeds.GasSalt);
            ulong drop = MatchSeeds.For(seed, MatchSeeds.DropSalt);
            ulong loot = MatchSeeds.For(seed, MatchSeeds.LootSalt);

            Assert.NotEqual(gas, drop);
            Assert.NotEqual(gas, loot);
            Assert.NotEqual(drop, loot);
            Assert.NotEqual(seed, drop);
        }
    }

    [Fact]
    public void TheMixIsDeterministic()
    {
        Assert.Equal(MatchSeeds.For(42, MatchSeeds.DropSalt), MatchSeeds.For(42, MatchSeeds.DropSalt));
        Assert.NotEqual(MatchSeeds.For(42, MatchSeeds.DropSalt), MatchSeeds.For(43, MatchSeeds.DropSalt));
    }

    /// <summary>
    /// Neighbouring match seeds must not give neighbouring sub-seeds, or two consecutive matches
    /// would drop in the same place — which is exactly the complaint this lane exists to answer.
    /// </summary>
    [Fact]
    public void NeighbouringSeedsAvalanche()
    {
        int worst = 64;
        for (ulong seed = 1; seed <= 512; seed++)
        {
            ulong a = MatchSeeds.For(seed, MatchSeeds.DropSalt);
            ulong b = MatchSeeds.For(seed + 1, MatchSeeds.DropSalt);
            int differing = System.Numerics.BitOperations.PopCount(a ^ b);
            worst = Math.Min(worst, differing);
        }

        Assert.True(worst >= 12, $"two consecutive seeds differed in only {worst} of 64 bits");
    }

    [Fact]
    public void APinnedSeedWinsAndAnUnpinnedOneIsDrawn()
    {
        Assert.Equal(0xDEADUL, MatchSeeds.Draw(0xDEAD, sessionGuid: 4099, utcTicks: 1234));
        Assert.Equal(4099UL ^ 1234UL, MatchSeeds.Draw(0, sessionGuid: 4099, utcTicks: 1234));
    }

    /// <summary>0 is the "not pinned" sentinel, so a drawn seed must never be 0 or it could not be replayed.</summary>
    [Fact]
    public void ADrawnSeedIsNeverZero() =>
        Assert.NotEqual(0UL, MatchSeeds.Draw(0, sessionGuid: 1234, utcTicks: 1234));

    [Fact]
    public void SeedsRoundTripThroughTheirLoggedForm()
    {
        foreach (ulong seed in (ulong[])[1, 0xDEAD_BEEF, ulong.MaxValue, 0x0000_0000_0000_000A])
        {
            string text = MatchSeeds.Format(seed);
            Assert.Equal(16, text.Length);
            Assert.True(MatchSeeds.TryParse(text, out ulong parsed));
            Assert.Equal(seed, parsed);
            Assert.True(MatchSeeds.TryParse("0x" + text, out ulong prefixed));
            Assert.Equal(seed, prefixed);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-seed")]
    [InlineData("0x")]
    public void RubbishIsRejected(string? text) => Assert.False(MatchSeeds.TryParse(text, out _));
}
