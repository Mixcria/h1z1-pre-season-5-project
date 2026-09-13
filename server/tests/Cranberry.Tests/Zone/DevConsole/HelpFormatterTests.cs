using Cranberry.Zone.DevConsole;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// <c>/help</c>, which is the typed door reading the same registry the menu reads (design §2.4).
/// </summary>
public sealed class HelpFormatterTests
{
    private static IReadOnlyList<string> Help(string arguments = "", bool unknown = false) =>
        HelpFormatter.Render(TestCatalog.Build(), CommandLine.Parse("help", arguments), unknown);

    [Fact]
    public void PageOneCountsTheCommandsAndListsOneLinePerGroup()
    {
        IReadOnlyList<string> page = Help();

        Assert.StartsWith("Cranberry console -- ", page[0], StringComparison.Ordinal);
        Assert.Contains("/help <group>  /help <command>  /m = menu", page[0], StringComparison.Ordinal);
        Assert.Contains(page, l => l.StartsWith("player   where tp up chute* heal hurt kill godmode speed*", StringComparison.Ordinal));
        Assert.Contains(page, l => l.StartsWith("items    give* kit* drop* inv", StringComparison.Ordinal));
        Assert.Contains(page, l => l.StartsWith("vehicles car enter exit fuel cars", StringComparison.Ordinal));
        Assert.Contains(page, l => l.StartsWith("match    match gas", StringComparison.Ordinal));
        Assert.Contains(page, l => l.StartsWith("players  players* announce evict* tier* tphere*", StringComparison.Ordinal));
        Assert.Contains(page, l => l.StartsWith("menu     m menu d u s b q r commands", StringComparison.Ordinal));
        Assert.Equal("(* = not available yet: answers \"not available yet -- <reason>\")", page[^1]);
    }

    [Fact]
    public void TheGroupColumnIsNineCharactersWideSoTheNamesLineUp()
    {
        foreach (string line in Help().Skip(1).Where(l => !l.StartsWith('(')))
        {
            Assert.Equal(' ', line[8]);
            Assert.NotEqual(' ', line[9]);
        }
    }

    [Fact]
    public void NoPageIsLongerThanTwelveLines()
    {
        CommandRegistry registry = TestCatalog.Build();

        Assert.InRange(Help().Count, 1, HelpFormatter.PageSize);
        Assert.InRange(Help("player").Count, 1, HelpFormatter.PageSize);
        Assert.InRange(Help("1").Count, 1, HelpFormatter.PageSize);
        Assert.InRange(HelpFormatter.Overview(registry).Count, 1, HelpFormatter.PageSize);
    }

    [Fact]
    public void TheCatchAllPageSaysAnUnknownNameLandsHereToo()
    {
        IReadOnlyList<string> page = Help(unknown: true);

        Assert.Equal(HelpFormatter.CatchAllNote, page[0]);
    }

    [Fact]
    public void ACommandPageShowsUsageTierGateExamplesAndTheMenuPath()
    {
        IReadOnlyList<string> page = Help("tp");

        Assert.Equal("/tp <x> <y> <z> | <place> | back    (Tester, any step)", page[0]);
        Assert.Contains(page, l => l.Contains("move yourself", StringComparison.Ordinal));
        Assert.Contains(page, l => l.Contains("also: /teleport", StringComparison.Ordinal));
        Assert.Contains(page, l => l.Contains("e.g. /tp 348 32 130", StringComparison.Ordinal));
        Assert.Contains(page, l => l.Contains("menu: Player > Teleport", StringComparison.Ordinal));
    }

    [Fact]
    public void ANotYetCommandSaysSoOnItsOwnPage()
    {
        IReadOnlyList<string> page = Help("give");

        Assert.Contains(page, l => l.Contains("NOT AVAILABLE YET -- the inventory commit is not on main yet", StringComparison.Ordinal));
    }

    [Fact]
    public void ARefusedCommandSaysSoOnItsOwnPage()
    {
        CommandRegistry registry = TestCatalog.Build();
        registry.MarkRefused("announce");

        IReadOnlyList<string> page = HelpFormatter.Render(registry, CommandLine.Parse("help", "announce"));

        Assert.Contains(page, l => l.Contains("the client REFUSED this name last run", StringComparison.Ordinal));
    }

    [Fact]
    public void AGroupPageListsUsageAndSummary()
    {
        IReadOnlyList<string> page = Help("vehicles");

        Assert.Equal("vehicles --", page[0]);
        Assert.Contains(page, l => l.Contains("/car <offroader|pickup|policecar|atv>", StringComparison.Ordinal));
    }

    [Fact]
    public void AGroupIsFoundByUniquePrefix()
    {
        Assert.Equal("vehicles --", Help("veh")[0]);
    }

    [Fact]
    public void ANumberIsAPageOfTheFlatList()
    {
        IReadOnlyList<string> one = Help("1");
        IReadOnlyList<string> two = Help("2");

        Assert.StartsWith("Cranberry console -- page 1 of ", one[0], StringComparison.Ordinal);
        Assert.StartsWith("Cranberry console -- page 2 of ", two[0], StringComparison.Ordinal);
        Assert.NotEqual(one[1], two[1]);
    }

    [Fact]
    public void APageBeyondTheEndIsClampedRatherThanEmpty()
    {
        IReadOnlyList<string> page = Help("99");

        Assert.True(page.Count > 1);
        Assert.StartsWith("Cranberry console -- page ", page[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownTopicSaysSoAndOffersTheShapes()
    {
        IReadOnlyList<string> page = Help("wobble");

        Assert.Equal("- no command or group called 'wobble'", page[0]);
        Assert.StartsWith("? /help", page[1], StringComparison.Ordinal);
    }
}
