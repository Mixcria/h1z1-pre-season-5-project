using System.Net;
using Cranberry.Zone.DevConsole;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>Who the console lets do what (design §4.2).</summary>
public sealed class ConsolePermissionTests
{
    private static IPEndPoint At(string address) => new(IPAddress.Parse(address), 20042);

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.4.4")]
    [InlineData("192.168.1.9")]
    [InlineData("169.254.7.7")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void ALocalRemoteIsOwnerWhenLocalIsOwnerIsOn(string address)
    {
        Assert.Equal(
            ConsoleTier.Owner,
            ConsolePermission.Resolve(At(address), "Cranberry", 0x1001, ConsoleOptions.Default));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("2001:4860::1")]
    public void AStrangerIsAPlayer(string address)
    {
        Assert.Equal(
            ConsoleTier.Player,
            ConsolePermission.Resolve(At(address), "Cranberry", 0x1001, ConsoleOptions.Default));
    }

    [Fact]
    public void LocalIsOwnerOffLeavesEvenLoopbackAPlayer()
    {
        ConsoleOptions options = ConsoleOptions.Default with { LocalIsOwner = false };

        Assert.Equal(ConsoleTier.Player, ConsolePermission.Resolve(At("127.0.0.1"), "Cranberry", 1, options));
    }

    [Fact]
    public void ANullRemoteIsNotLocal()
    {
        Assert.Equal(ConsoleTier.Player, ConsolePermission.Resolve(null, "Cranberry", 1, ConsoleOptions.Default));
    }

    [Fact]
    public void AnExplicitNameEntryBeatsTheLocalRule()
    {
        ConsoleOptions options = ConsoleOptions.Default with { Tiers = "Cranberry=tester,*=player" };

        Assert.Equal(ConsoleTier.Tester, ConsolePermission.Resolve(At("127.0.0.1"), "cranberry", 1, options));
    }

    [Fact]
    public void AnExplicitGuidEntryMatchesInHexOrDecimal()
    {
        ConsoleOptions hex = ConsoleOptions.Default with { Tiers = "0x1001=tester", LocalIsOwner = false };
        ConsoleOptions dec = ConsoleOptions.Default with { Tiers = "4097=tester", LocalIsOwner = false };

        Assert.Equal(ConsoleTier.Tester, ConsolePermission.Resolve(At("8.8.8.8"), "Someone", 0x1001, hex));
        Assert.Equal(ConsoleTier.Tester, ConsolePermission.Resolve(At("8.8.8.8"), "Someone", 0x1001, dec));
    }

    [Fact]
    public void TheWildcardIsTheFallbackAndTheLocalRuleStillBeatsIt()
    {
        ConsoleOptions options = ConsoleOptions.Default with { Tiers = "*=tester" };

        Assert.Equal(ConsoleTier.Tester, ConsolePermission.Resolve(At("8.8.8.8"), "Someone", 5, options));
        Assert.Equal(ConsoleTier.Owner, ConsolePermission.Resolve(At("127.0.0.1"), "Someone", 5, options));
    }

    [Fact]
    public void AMalformedEntryIsSkippedRatherThanThrown()
    {
        ConsoleOptions options = ConsoleOptions.Default with
        {
            Tiers = ",=owner,nonsense,Cranberry=,Cranberry=wizard,*=tester",
            LocalIsOwner = false,
        };

        Assert.Equal(ConsoleTier.Tester, ConsolePermission.Resolve(At("8.8.8.8"), "Cranberry", 1, options));
    }

    [Theory]
    [InlineData("owner", ConsoleTier.Owner)]
    [InlineData("ADMIN", ConsoleTier.Owner)]
    [InlineData("TESTER", ConsoleTier.Tester)]
    [InlineData(" player ", ConsoleTier.Player)]
    [InlineData(" client ", ConsoleTier.Player)]
    public void TierWordsAreReadCaseInsensitively(string word, ConsoleTier expected)
    {
        Assert.True(ConsolePermission.TryParseTier(word, out ConsoleTier tier));
        Assert.Equal(expected, tier);
    }

    [Fact]
    public void AnUnknownTierWordIsNotAccepted()
    {
        Assert.False(ConsolePermission.TryParseTier("superadmin", out _));
    }
}
