using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Commands;
using Cranberry.Zone.DevConsole.Menu;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// The SHIPPED catalogue - the one <c>ZoneService</c> actually builds - against the design's
/// Appendix A.
/// <para>
/// The engine lane's <c>CommandRegistryTests</c> asserts the same shape against its own
/// <c>TestCatalog</c>, which was the stand-in while the backends were being written. This class is
/// where that assertion belongs now: a rename in <c>CommandCatalog</c> is a change to the fifty
/// names the August client will be told about, and it must fail here rather than on the first
/// click.
/// </para>
/// </summary>
public class CommandCatalogTests
{
    [Fact]
    public void DevelopmentDefaultsExposeWorkingCommandsAndParkTheModMenu()
    {
        var registry = CommandCatalog.Build();
        Assert.True(ConsoleOptions.Default.SelfFlagOpensConsole);
        Assert.False(ConsoleOptions.Default.ModMenuEnabled);
        Assert.Equal(0, registry.NotYetCount);
        Assert.NotNull(registry.ByName("give"));
        Assert.NotNull(registry.ByName("commands"));
        foreach (var name in CommandCatalog.MenuNames.Concat(["win", "raw"]))
            Assert.Null(registry.ByName(name));
    }

    [Fact]
    public void TheShippedNamesAreExactlyTheOnesOfAppendixA()
    {
        CommandRegistry registry = CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = true });

        Assert.Equal(
            ConsoleNameVectors.Appendix.Select(v => v.Name).Concat(["ammo", "combat", "hostgame", "places", "rank", "spawnvehicle", "location", "gm", "bots", "spawnbots", "pausegas", "resumegas"]).OrderBy(n => n, StringComparer.Ordinal),
            registry.Names.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(63, registry.Names.Count);
    }

    [Fact]
    public void NotOneShippedNameIsInTheClientsOwnRegistry()
    {
        foreach (string name in CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = true }).Names)
        {
            uint hash = CommandHash.Compute(name);
            Assert.False(
                ClientRegistry1148.Contains(hash),
                $"'{name}' (0x{hash:x8}) is in the client's own registry: {ClientRegistry1148.Describe(hash)} "
                + "- the client would log it to AdminCommands.log and never send the command");
        }
    }

    [Fact]
    public void HelpIsRegisteredButNeverPushed()
    {
        CommandRegistry registry = CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = true });

        ConsoleCommand? help = registry.ByName("help");
        Assert.NotNull(help);
        Assert.False(help!.Registered);
        Assert.DoesNotContain("help", registry.Names);

        // The catch-all is the only way it is reachable, so the hash the client sends for an
        // unknown name must resolve to it.
        Assert.Same(help, registry.ByHash(CommandHash.Help));
    }

    [Fact]
    public void TheGroupsAreInTheOrderHelpPageOneDraws()
    {
        Assert.Equal(
            ["player", "items", "vehicles", "loot", "match", "world", "players", "debug", "info", "menu"],
            CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = true }).ByGroup().Select(g => g.Key));
    }

    [Fact]
    public void EveryLiveMenuLeafBindsACommandTheCatalogueHas()
    {
        CommandRegistry registry = CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = true });
        MenuTree tree = MenuTree.Build(registry);

        List<string> missing = [];
        Walk(tree.Root);
        Assert.Empty(missing);

        void Walk(MenuNode node)
        {
            foreach (MenuNode child in node.Children)
            {
                if (child.Kind != MenuKind.Submenu
                    && child.SettingKey is null
                    && child.NotYet is null
                    && child.CommandName is not null
                    && registry.ByName(child.CommandName) is null)
                {
                    missing.Add($"{child.Id} -> /{child.CommandName}");
                }

                Walk(child);
            }
        }
    }

    [Fact]
    public void EveryNotYetCommandExplainsItself()
    {
        foreach (ConsoleCommand command in CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = true }).Commands.Where(c => c.NotYet is not null))
        {
            Assert.False(
                string.IsNullOrWhiteSpace(command.NotYet),
                $"/{command.Name} is a LATER row with no reason");
            Assert.True(
                command.NotYet!.Length > 20,
                $"/{command.Name}'s reason is too short to be honest: '{command.NotYet}'");
        }
    }

    [Fact]
    public void TheCountsTheBootBannerPrintsAreTheRealOnes()
    {
        CommandRegistry registry = CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = true });

        // 43 commands, of which the nine LATER rows are chute, speed, give, kit, drop, players,
        // evict, tier, tphere. If this number moves, a backend either landed or regressed.
        Assert.Equal(51, registry.Commands.Count);
        Assert.Equal(0, registry.NotYetCount);
        Assert.Contains("63 names", registry.Summary);
        Assert.Contains("0 refused last run", registry.Summary);
    }

    [Theory]
    [InlineData("ar15", 2425u)]
    [InlineData("AR15", 2425u)]
    [InlineData("ak47", 2229u)]
    [InlineData("bandage", 2423u)]
    [InlineData("armor", 2271u)]
    public void TheItemRosterResolvesTheNamesTheMenuBinds(string name, uint definitionId)
    {
        Assert.Equal((int)definitionId, ItemNames.Resolve(name));
    }

    [Fact]
    public void AMistypedItemNameSuggestsTheNearestOne()
    {
        Assert.Equal("ar15", ItemNames.Nearest("ar16"));
        Assert.Null(ItemNames.Nearest("rocketlauncher"));
    }

    [Theory]
    [InlineData("offroader", 1u)]
    [InlineData("truck", 2u)]
    [InlineData("police", 3u)]
    [InlineData("quad", 5u)]
    [InlineData("5", 5u)]
    [InlineData("jeep", 1u)]
    [InlineData("off-roader", 1u)]
    [InlineData("pickuptruck", 2u)]
    [InlineData("copcar", 3u)]
    public void TheVehicleRosterTakesEverySpellingTheOwnerMightType(string word, uint vehicleId)
    {
        Assert.Equal(vehicleId, VehicleCommands.ResolveVehicle(word));
    }

    [Fact]
    public void AVehicleNameTheRosterDoesNotHaveIsRefused()
    {
        Assert.Null(VehicleCommands.ResolveVehicle("tank"));
    }
    /// <summary>
    /// Every shipped Prompt leaf, driven the way the owner drives it: open the row, answer each
    /// argument it still needs, and read the line the menu would run.
    /// <para>
    /// A row's <c>BoundArgs</c> answers the leading declared arguments - Loot &gt; Spawn binds the
    /// <c>verb</c> argument to "spawn" - so the prompt must start at the FIRST unanswered one. The
    /// prompt used to start at 0 and ask for <c>verb</c> again, so accepting the defaults built
    /// "/loot spawn stats bandage 1" and <c>LootCommands.Spawn</c> read "stats" as the item
    /// (review-2 major). The menu lane's own tests never met it because <c>TestCatalog</c>'s
    /// <c>loot</c> declares one argument and the shipped one declares three, which is exactly why
    /// this walk lives here, over the SHIPPED catalogue.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryPromptLeafAsksOnlyForTheArgumentsItsRowHasNotAlreadyBound()
    {
        CommandRegistry registry = CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = true });
        MenuTree tree = MenuTree.Build(registry);
        List<string> wrong = [];
        int walked = 0;

        Walk(tree.Root, []);

        Assert.Empty(wrong);
        Assert.True(walked >= 2, $"only {walked} prompt leaves were walked - the tree lost its prompts");

        void Walk(MenuNode node, List<string> path)
        {
            IReadOnlyList<MenuNode> rows = tree.VisibleChildren(node, ConsoleTier.Owner, showAll: false);
            for (int i = 0; i < rows.Count; i++)
            {
                MenuNode row = rows[i];
                if (row.Kind == MenuKind.Submenu)
                {
                    Walk(row, [.. path, row.Id]);
                    continue;
                }

                if (row.Kind != MenuKind.Prompt || row.CommandName is null)
                {
                    continue;
                }

                ConsoleCommand? command = tree.CommandOf(row);
                if (command is null)
                {
                    continue;
                }

                if (tree.NotYetReason(row) is not null)
                {
                    // A LATER row is refused before the prompt ever opens, and its command is a
                    // stub with no declared arguments yet, so there is nothing to walk.
                    continue;
                }

                if (command.Args.Length == 0)
                {
                    // Nothing declared to ask for: Act runs the row outright, which is right.
                    continue;
                }

                walked++;
                IReadOnlyList<string> bound = CommandLine.Tokenise(row.BoundArgs);
                if (bound.Count >= command.Args.Length)
                {
                    wrong.Add($"{row.Id}: binds {bound.Count} of /{command.Name}'s {command.Args.Length} "
                        + "arguments, so it has nothing left to prompt for - make it an Action row");
                    continue;
                }

                Drive(row, node, i, path, command, bound);
            }
        }

        void Drive(
            MenuNode row,
            MenuNode parent,
            int index,
            List<string> path,
            ConsoleCommand command,
            IReadOnlyList<string> bound)
        {
            MenuView view = ViewFor(row.Gate);
            MenuState at = MenuState.Closed with
            {
                Mode = MenuMode.Open,
                Path = [.. path],
                Cursors = MenuState.Closed.Cursors.SetItem(parent.Id, index),
            };

            MenuStep step = at.Apply(new MenuInput(MenuVerb.Select), tree, view);
            if (step.State.Mode != MenuMode.Prompt)
            {
                wrong.Add($"{row.Id}: selecting it did not open a prompt ({step.State.Mode}, {step.ResultLine})");
                return;
            }

            if (step.State.PendingIndex != bound.Count)
            {
                wrong.Add($"{row.Id}: the prompt opens on argument {step.State.PendingIndex} "
                    + $"('{command.Args[step.State.PendingIndex].Name}') but the row already bound "
                    + $"{bound.Count} ('{row.BoundArgs}') - it must open on "
                    + $"'{command.Args[bound.Count].Name}'");
                return;
            }

            // The frame must not ask for an argument the row already answered, and its typed-line
            // preview must not print the bound word twice.
            IReadOnlyList<string> frame = FrameRenderer.Render(step.State, tree, view);
            for (int b = 0; b < bound.Count; b++)
            {
                string asked = command.Args[b].Name + "?";
                if (frame.Any(line => line.Contains(asked, StringComparison.Ordinal)))
                {
                    wrong.Add($"{row.Id}: the prompt frame still asks for '{command.Args[b].Name}', "
                        + $"which the row bound to '{bound[b]}'");
                }
            }

            List<string> answers = [];
            for (int a = bound.Count; a < command.Args.Length; a++)
            {
                string answer = AnswerFor(command.Args[a]);
                answers.Add(answer);
                step = step.State.Apply(new MenuInput(MenuVerb.Text, Raw: answer), tree, view);
                if (step.State.PendingError is not null)
                {
                    wrong.Add($"{row.Id}: '{answer}' was refused for {command.Args[a].Name} "
                        + $"- {step.State.PendingError}");
                    return;
                }
            }

            CommandInvocation? ran = step.Invocation ?? step.State.PendingInvocation;
            if (ran is null)
            {
                wrong.Add($"{row.Id}: the answered prompt ran nothing ({step.State.Mode}, {step.ResultLine})");
                return;
            }

            string expected = string.Join(' ', bound.Concat(answers));
            if (ran.Arguments != expected)
            {
                wrong.Add($"{row.Id}: the menu would run '/{ran.Name} {ran.Arguments}' "
                    + $"but the row binds '{row.BoundArgs}' and was answered "
                    + $"'{string.Join(' ', answers)}' - expected '/{ran.Name} {expected}'");
            }
        }
    }

    /// <summary>An answer a prompt will accept for one declared argument.</summary>
    private static string AnswerFor(ArgSpec spec) => spec.Kind switch
    {
        ArgKind.Int => "1",
        ArgKind.Float => "1.5",
        ArgKind.Enum => spec.Choices is { Length: > 0 } choices ? choices[0] : spec.Default ?? "on",
        ArgKind.Item => "bandage",
        ArgKind.Hex => "00",
        _ => spec.Default is { Length: > 0 } given ? given : "x",
    };

    /// <summary>A view in whichever match step the row's gate wants.</summary>
    private static MenuView ViewFor(MatchGate gate) => new()
    {
        Settings = ConsoleSettings.Default with { Confirms = false },
        Tier = ConsoleTier.Owner,
        MatchStep = gate is MatchGate.MenuOnly or MatchGate.LobbyOrMenu ? "Menu" : "InMatch",
    };
}
