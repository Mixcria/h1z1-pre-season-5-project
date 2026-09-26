using Cranberry.NetworkBots;

namespace Cranberry.Harness.Tests;

public sealed class MovementRateOptionsTests
{
    [Fact]
    public void OmittedRateRetainsTheLegacyProfile() => Assert.Null(Program.ParseMovementHz(["--bots", "2"]));

    [Theory]
    [InlineData("20", 20)]
    [InlineData("60", 60)]
    [InlineData("120", 120)]
    public void ExplicitRateIsParsedInvariantly(string value, int expected) =>
        Assert.Equal(expected, Program.ParseMovementHz(["--movement-hz", value]));

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("19")]
    [InlineData("121")]
    [InlineData("-60")]
    [InlineData("60.0")]
    [InlineData("NaN")]
    [InlineData("--bots")]
    public void InvalidRateCannotSilentlyFallBack(string value) =>
        Assert.Throws<ArgumentException>(() => Program.ParseMovementHz(["--movement-hz", value]));

    [Fact]
    public void MissingOrRepeatedRateIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Program.ParseMovementHz(["--movement-hz"]));
        Assert.Throws<ArgumentException>(() => Program.ParseMovementHz(["--movement-hz", "60", "--movement-hz", "25"]));
    }

    [Theory]
    [InlineData("--multi-match")]
    [InlineData("--menu-only")]
    [InlineData("--server-only")]
    [InlineData("--public-profile")]
    public async Task UnsupportedModesRejectBeforeOpeningAConnection(string mode) =>
        await Assert.ThrowsAsync<ArgumentException>(() => Program.Main([mode, "--movement-hz", "60"]));
}
