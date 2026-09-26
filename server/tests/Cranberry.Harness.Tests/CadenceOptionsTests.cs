using Cranberry.NetworkBots;

namespace Cranberry.Harness.Tests;

public sealed class CadenceOptionsTests
{
    [Theory]
    [InlineData("1", 1)]
    [InlineData("6", 6)]
    [InlineData("60", 60)]
    [InlineData("120", 120)]
    public void ExplicitCanopyRateAcceptsItsIndependentRange(string text, int expected) =>
        Assert.Equal(expected, Program.ParseCanopyHz(["--canopy-hz", text]));

    [Theory]
    [InlineData("0")]
    [InlineData("121")]
    [InlineData("6.0")]
    [InlineData("-1")]
    [InlineData("--movement-hz")]
    public void InvalidCanopyRateIsRejected(string text) =>
        Assert.Throws<ArgumentException>(() => Program.ParseCanopyHz(["--canopy-hz", text]));

    [Fact]
    public void MissingAndDuplicateRatesAreRejectedAndOmissionIsLegacy()
    {
        Assert.Null(Program.ParseCanopyHz([]));
        Assert.Throws<ArgumentException>(() => Program.ParseCanopyHz(["--canopy-hz"]));
        Assert.Throws<ArgumentException>(() => Program.ParseCanopyHz(["--canopy-hz", "6", "--canopy-hz", "60"]));
        Program.ValidateCadenceOptions([], null, null, false);
        Program.ValidateCadenceOptions([], 30, 60, true);
        Assert.Throws<ArgumentException>(() => Program.ValidateCadenceOptions([], null, 6, false));
    }

    [Theory]
    [InlineData("--server-only")]
    [InlineData("--multi-match")]
    [InlineData("--menu-only")]
    [InlineData("--public-profile")]
    [InlineData("--prepare-cloud")]
    public void ControlledPoliciesRejectUnsupportedModesBeforeConnecting(string mode)
    {
        Assert.Throws<ArgumentException>(() => Program.ValidateCadenceOptions([mode], 60, 6, false));
        Assert.Throws<ArgumentException>(() => Program.ValidateCadenceOptions([mode], null, null, true));
    }

    [Fact]
    public void IndependentCanopyRequiresOneCycleButJournalPolicySupportsReconnections()
    {
        Assert.Throws<ArgumentException>(() => Program.ValidateCadenceOptions(["--match-cycles", "2"], 60, 6, false));
        Program.ValidateCadenceOptions(["--match-cycles", "2"], 60, null, true);
    }
}
