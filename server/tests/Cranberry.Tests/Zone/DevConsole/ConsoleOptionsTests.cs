using Cranberry.Zone.DevConsole;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// The switches (design §4.7). The rule the whole record is built around: only the exact string
/// <c>"1"</c> or <c>"0"</c> moves a boolean, so a typo in the environment leaves the shipped
/// default rather than silently changing how the console behaves.
/// </summary>
public sealed class ConsoleOptionsTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, string> map = pairs.ToDictionary(p => p.Key, p => p.Value);
        return key => map.GetValueOrDefault(key);
    }

    [Fact]
    public void TheDefaultsAreWhatTheDesignPromises()
    {
        ConsoleOptions options = ConsoleOptions.Default;

        Assert.True(options.Enabled);
        Assert.Equal(ConsoleSurfaceKind.Print, options.Surface);
        Assert.True(options.LocalIsOwner);
        Assert.Equal(string.Empty, options.Tiers);
        Assert.Equal(ConsoleRegisterPolicy.EachZone, options.Register);
        Assert.True(options.SelfFlagOpensConsole);
        Assert.Equal(@"C:\Aug2017\Client\Logs", options.ClientLogsPath);
        Assert.Equal(10, options.MenuRows);
        Assert.Equal(46, options.MenuWidth);
        Assert.True(options.Confirms);
        Assert.Equal(50, options.RateLimitMs);
    }

    [Fact]
    public void EveryVariableIsRead()
    {
        ConsoleOptions options = ConsoleOptions.FromEnvironment(Env(
            (ConsoleOptions.EnabledVariable, "0"),
            (ConsoleOptions.SurfaceVariable, "chat0"),
            (ConsoleOptions.LocalOwnerVariable, "0"),
            (ConsoleOptions.TiersVariable, "Cranberry=owner"),
            (ConsoleOptions.RegisterVariable, "zoning"),
            (ConsoleOptions.SelfFlagVariable, "1"),
            (ConsoleOptions.ClientLogsVariable, @"D:\Logs"),
            (ConsoleOptions.RowsVariable, "12"),
            (ConsoleOptions.WidthVariable, "52"),
            (ConsoleOptions.ConfirmsVariable, "0")));

        Assert.False(options.Enabled);
        Assert.Equal(ConsoleSurfaceKind.Chat0, options.Surface);
        Assert.False(options.LocalIsOwner);
        Assert.Equal("Cranberry=owner", options.Tiers);
        Assert.Equal(ConsoleRegisterPolicy.Zoning, options.Register);
        Assert.True(options.SelfFlagOpensConsole);
        Assert.Equal(@"D:\Logs", options.ClientLogsPath);
        Assert.Equal(12, options.MenuRows);
        Assert.Equal(52, options.MenuWidth);
        Assert.False(options.Confirms);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData("  1")]
    public void OnlyOneAndZeroMoveASwitch(string typo)
    {
        ConsoleOptions options = ConsoleOptions.FromEnvironment(Env((ConsoleOptions.EnabledVariable, typo)));

        Assert.True(options.Enabled);
    }

    [Theory]
    [InlineData("print", ConsoleSurfaceKind.Print)]
    [InlineData("chat", ConsoleSurfaceKind.Chat)]
    [InlineData("chat1", ConsoleSurfaceKind.Chat)]
    [InlineData("chat0", ConsoleSurfaceKind.Chat0)]
    [InlineData("ALERT", ConsoleSurfaceKind.Alert)]
    [InlineData("nonsense", ConsoleSurfaceKind.Print)]
    public void SurfaceWordsAreRead(string word, ConsoleSurfaceKind expected)
    {
        Assert.Equal(expected, ConsoleOptions.ParseSurface(word, ConsoleSurfaceKind.Print));
    }

    [Theory]
    [InlineData("each-zone", ConsoleRegisterPolicy.EachZone)]
    [InlineData("once", ConsoleRegisterPolicy.Once)]
    [InlineData("zoning", ConsoleRegisterPolicy.Zoning)]
    [InlineData("off", ConsoleRegisterPolicy.Off)]
    [InlineData("sometimes", ConsoleRegisterPolicy.EachZone)]
    public void RegisterWordsAreRead(string word, ConsoleRegisterPolicy expected)
    {
        Assert.Equal(expected, ConsoleOptions.ParseRegister(word, ConsoleRegisterPolicy.EachZone));
    }

    [Fact]
    public void GeometryIsClampedIntoTheGridTheRendererIsDefinedFor()
    {
        ConsoleOptions tiny = ConsoleOptions.Default with { MenuRows = 1, MenuWidth = 4 };
        ConsoleOptions huge = ConsoleOptions.Default with { MenuRows = 99, MenuWidth = 400 };

        Assert.Equal(ConsoleOptions.MinimumRows, tiny.EffectiveRows);
        Assert.Equal(ConsoleOptions.MinimumWidth, tiny.EffectiveWidth);
        Assert.Equal(ConsoleOptions.MaximumRows, huge.EffectiveRows);
        Assert.Equal(ConsoleOptions.MaximumWidth, huge.EffectiveWidth);
    }

    [Fact]
    public void TheCollisionLogPathIsTheClientsAdminCommandsLog()
    {
        Assert.EndsWith("AdminCommands.log", ConsoleOptions.Default.CollisionLogPath, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeNamesTheRevertWordAndTheLiveArms()
    {
        string line = ConsoleOptions.Default.Describe("43 commands, 50 names");

        Assert.StartsWith("console: ON", line, StringComparison.Ordinal);
        Assert.Contains("43 commands, 50 names", line, StringComparison.Ordinal);
        Assert.Contains("names pushed each-zone after ClientIsReady", line, StringComparison.Ordinal);
        Assert.Contains("surface=print (06 03)", line, StringComparison.Ordinal);
        Assert.Contains("self-flag=on", line, StringComparison.Ordinal);
        Assert.Contains("mod menu parked", line, StringComparison.Ordinal);
        Assert.Contains("CRANBERRY_CONSOLE=0 reverts", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeSaysSoWhenTheConsoleIsOff()
    {
        string line = (ConsoleOptions.Default with { Enabled = false }).Describe();

        Assert.Contains("console: off", line, StringComparison.Ordinal);
        Assert.Contains("09 42 is logged and unanswered", line, StringComparison.Ordinal);
    }
}
