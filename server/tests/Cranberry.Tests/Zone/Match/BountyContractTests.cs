using Cranberry.Protocol;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed class BountyContractTests
{
    [Theory]
    [InlineData(true, "CE150001")]
    [InlineData(false, "CE150000")]
    public void InBoxIsTrueInPregameAndFalseAtDrop(bool inBox, string expected)
    {
        using var writer = new PacketWriter();
        new BountyLobbyState(inBox).WriteTo(writer);
        Assert.Equal(expected, Convert.ToHexString(writer.Written));
    }

    [Fact]
    public void CostsSupplyBothHardCurrencyPlatformCodesAndEarnedCreditRequirement()
    {
        using var writer = new PacketWriter();
        new MatchBountyCosts(500, 100, 100, 0x0102030405060708).WriteTo(writer);
        Assert.Equal(MatchBountyCosts.Length, writer.Written.Length);
        var reader = new PacketReader(writer.Written);
        Assert.Equal(0x67, reader.ReadByte());
        Assert.Equal(0x10, reader.ReadByte());
        Assert.Equal(0x0102030405060708ul, reader.ReadUInt64());
        Assert.Equal(4u, reader.ReadUInt32());
        string[] codes = ["SOE", "KH$", "KS$", "KF$"];
        uint[] amounts = [500, 500, 100, 100];
        for (int i = 0; i < codes.Length; i++)
        {
            Assert.Equal((uint)(i + 1), reader.ReadUInt32());
            Assert.Equal(codes[i], reader.ReadString());
            Assert.Equal(amounts[i], reader.ReadUInt32());
        }
        Assert.True(reader.AtEnd);
    }

    [Theory]
    [InlineData(1u, 4u, 500u)]
    [InlineData(2u, 5u, 100u)]
    [InlineData(3u, 6u, 100u)]
    public void ButtonOptionIsDifferentFromCurrencyId(uint option, uint currency, uint amount)
    {
        Assert.True(BountyOptions.Default.TryGetAnte(option, out BountyAnte? ante));
        Assert.Equal(currency, ante.CurrencyId);
        Assert.Equal(amount, ante.Amount);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    [InlineData(6u)]
    [InlineData(uint.MaxValue)]
    public void UnrecognizedOptionsCannotBecomeFreeBacking(uint option)
    {
        Assert.False(BountyOptions.Default.TryGetAnte(option, out BountyAnte? ante));
        Assert.Null(ante);
        Assert.Throws<ArgumentOutOfRangeException>(() => BountyOptions.Default.AnteFor(option));
    }

    [Fact]
    public void FreeBackingHasItsOwnNonzeroTypeAndEarnedCreditCost()
    {
        BountyAnte free = BountyOptions.Default.AnteFor(BountyOptions.FreeCreditsOption);
        Assert.Equal(6u, free.CurrencyId);
        Assert.True(free.Amount > 0);
        Assert.NotEqual(MatchBountyState.None, new MatchBountyState(free.Amount, free.BountyType));
    }

    [Fact]
    public void UnavailableOrDisabledOffersZeroEveryCost()
    {
        Assert.Equal(MatchBountyCosts.Unavailable, BountyOptions.Default.Costs(false));
        Assert.Equal(MatchBountyCosts.Unavailable, (BountyOptions.Default with { Enabled = false }).Costs(true));
        Assert.Equal(MatchBountyCosts.Unavailable, (BountyOptions.Default with { AcceptAnte = false }).Costs(true));
    }

    [Theory]
    [InlineData(0u, 4u)]
    [InlineData(500u, 5u)]
    [InlineData(uint.MaxValue, 4u)]
    public void InvalidCostConfigurationCannotAdvertiseOrAcceptThatOption(uint cost, uint currency)
    {
        var options = BountyOptions.Default with { Antes = [new(1, currency, cost, "invalid")] };
        Assert.False(options.TryGetAnte(1, out _));
        Assert.Equal(0u, options.Costs(true).Crowns);
    }

    [Fact]
    public void DuplicateOptionConfigurationCannotDisplayOnePriceAndDebitAnother()
    {
        var options = BountyOptions.Default with
        {
            Antes = [new(1, 4, 500, "first"), new(1, 4, 100, "second")],
        };
        Assert.False(options.TryGetAnte(1, out _));
        Assert.Equal(0u, options.Costs(true).Crowns);
    }

    [Theory]
    [InlineData(uint.MaxValue, 0u, 0u)]
    [InlineData(0u, uint.MaxValue, 0u)]
    [InlineData(0u, 0u, uint.MaxValue)]
    public void OversizedWireCostsFailBeforeWriting(uint crowns, uint skulls, uint credits)
    {
        using var writer = new PacketWriter();
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchBountyCosts(crowns, skulls, credits).WriteTo(writer));
        Assert.Empty(writer.Written.ToArray());
    }

    [Fact]
    public void OnlyPublicSoloHasAnOfferAndOnlyPregameCanAcceptIt()
    {
        foreach (MatchQueueKind queue in Enum.GetValues<MatchQueueKind>())
        foreach (MatchMode mode in Enum.GetValues<MatchMode>())
        foreach (BountyPhase phase in Enum.GetValues<BountyPhase>())
        {
            var context = new MatchAdmissionContext(27, queue, mode);
            bool eligible = queue == MatchQueueKind.Public && mode == MatchMode.Solo;
            bool canBack = eligible && phase is BountyPhase.Lobby or BountyPhase.Countdown;
            Assert.Equal(eligible, BountyEligibility.IsEligible(context));
            Assert.Equal(canBack, BountyEligibility.CanBack(context, phase));
            Assert.False(BountyEligibility.CanBack(context, phase, enabled: false));
            Assert.False(BountyEligibility.CanBack(context, phase, acceptAnte: false));
        }
        Assert.False(BountyEligibility.IsEligible(null));
        Assert.False(BountyEligibility.IsEligible(new(0, MatchQueueKind.Public, MatchMode.Solo)));
    }
}
