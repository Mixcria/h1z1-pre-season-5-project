using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Menu;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// The tree of design §2.5, and the one rule that keeps the two front doors honest: every live
/// leaf IS a registered command, so the menu can never do something the owner could not type.
/// </summary>
public sealed class MenuTreeTests
{
    private static MenuTree Tree() => MenuTree.Build(TestCatalog.Build());

    private static IEnumerable<MenuNode> Walk(MenuNode node)
    {
        yield return node;
        foreach (MenuNode child in node.Children)
        {
            foreach (MenuNode deeper in Walk(child))
            {
                yield return deeper;
            }
        }
    }

    [Fact]
    public void TheRootHoldsTheTwelveCategoriesInTheDesignsOrder()
    {
        Assert.Equal(
            ["Player", "Weapons", "Items", "Vehicles", "Loot", "Match", "World", "Players", "Debug", "Info", "Windows", "Settings"],
            Tree().Root.Children.Select(c => c.Label));
    }

    [Fact]
    public void EveryNodeIdIsUnique()
    {
        List<string> ids = [.. Walk(Tree().Root).Select(n => n.Id)];

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void EveryLiveLeafBindsARegisteredCommand()
    {
        MenuTree tree = Tree();

        foreach (MenuNode leaf in Walk(tree.Root).Where(n => n.IsLeaf))
        {
            if (tree.NotYetReason(leaf) is not null || leaf.SettingKey is not null)
            {
                continue;
            }

            Assert.NotNull(leaf.CommandName);
            Assert.NotNull(tree.CommandOf(leaf));
        }
    }

    [Fact]
    public void NoLeafIsGatedLooserThanTheCommandItRuns()
    {
        MenuTree tree = Tree();

        foreach (MenuNode leaf in Walk(tree.Root).Where(n => n.IsLeaf))
        {
            ConsoleCommand? command = tree.CommandOf(leaf);
            if (command is null)
            {
                continue;
            }

            Assert.True(
                leaf.Tier >= command.Tier,
                $"{leaf.Id} is drawn for {leaf.Tier} but /{command.Name} needs {command.Tier}");
            Assert.True(
                leaf.Gate == command.Gate || command.Gate == MatchGate.Any,
                $"{leaf.Id} is gated {leaf.Gate} but /{command.Name} needs {command.Gate}");
        }
    }

    [Fact]
    public void EveryLabelFitsTheGridAtTheNarrowestWidth()
    {
        foreach (MenuNode node in Walk(Tree().Root))
        {
            Assert.InRange(node.Label.Length, 1, 26);
        }
    }

    [Fact]
    public void ALeafWhoseCommandIsNotYetIsGreyedWithItsReason()
    {
        MenuTree tree = Tree();
        MenuNode? give = tree.ById("items.give");

        Assert.NotNull(give);
        Assert.Equal("the inventory commit is not on main yet", tree.NotYetReason(give));
    }

    [Fact]
    public void ALeafWithItsOwnReasonKeepsItEvenWithoutACommand()
    {
        MenuTree tree = Tree();
        MenuNode? noclip = tree.ById("player.noclip");

        Assert.NotNull(noclip);
        Assert.Equal("the August client has no server-driven noclip", tree.NotYetReason(noclip));
    }

    [Fact]
    public void ARefusedNameIsSeenThroughTheRegistry()
    {
        CommandRegistry registry = TestCatalog.Build();
        MenuTree tree = MenuTree.Build(registry);
        MenuNode announce = tree.ById("players.all.announce")!;

        Assert.False(tree.IsRefused(announce));
        registry.MarkRefused("announce");
        Assert.True(tree.IsRefused(announce));
    }

    [Fact]
    public void RowsAboveTheCallersTierAreHiddenUnlessShowAllIsOn()
    {
        MenuTree tree = Tree();
        MenuNode match = tree.ById("match")!;

        IReadOnlyList<MenuNode> forPlayer = tree.VisibleChildren(match, ConsoleTier.Player, showAll: false);
        IReadOnlyList<MenuNode> forOwner = tree.VisibleChildren(match, ConsoleTier.Owner, showAll: false);
        IReadOnlyList<MenuNode> greyed = tree.VisibleChildren(match, ConsoleTier.Player, showAll: true);

        Assert.Equal(["Status"], forPlayer.Select(r => r.Label));
        Assert.Equal(6, forOwner.Count);
        Assert.Equal(6, greyed.Count);
    }

    [Fact]
    public void ResolveWalksIdsAndStopsAtTheLastGoodOne()
    {
        MenuTree tree = Tree();

        Assert.Equal("player", tree.Resolve(["player"]).Id);
        Assert.Equal("player.tp", tree.Resolve(["player", "player.tp"]).Id);
        Assert.Equal("player", tree.Resolve(["player", "nonsense"]).Id);
        Assert.Equal("root", tree.Resolve([]).Id);
    }

    [Fact]
    public void LabelPathIsWhatTheBreadcrumbPrints()
    {
        Assert.Equal(["Player", "Teleport"], Tree().LabelPath(["player", "player.tp"]));
    }

    [Fact]
    public void SettingsHasTheTwelveRowsThatNeedTwoDigitPicks()
    {
        MenuNode settings = Tree().ById("settings")!;

        Assert.Equal(12, settings.Children.Count);
        Assert.All(settings.Children, row => Assert.NotNull(row.SettingKey));
        Assert.Equal(
            ["Remember path", "Theme", "Surface", "Live refresh", "Show all rows"],
            settings.Children.Skip(7).Select(r => r.Label));
    }
}
