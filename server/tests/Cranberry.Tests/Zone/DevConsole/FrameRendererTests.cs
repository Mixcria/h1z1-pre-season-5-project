using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Menu;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// The frame, on the fixed grid of design §2.7.
/// <para>
/// The two full frames of design §2.6 are pinned line for line, because the grid is the part the
/// owner will notice first and the part a careless edit breaks silently. Two deliberate departures
/// from the hand-drawn mockups are pinned with them: a submenu carrying a live note keeps its
/// <c>&gt;</c> (the mockup's root prints it on <c>Players</c> and drops it on <c>Match</c> - one of
/// the two has to give), and a truncated label ends in ASCII <c>..</c> rather than an ellipsis
/// glyph, because R4 §7.1 requires every frame to be ASCII.
/// </para>
/// </summary>
public sealed class FrameRendererTests
{
    private static MenuTree Tree() => MenuTree.Build(TestCatalog.Build());

    private static MenuView View(
        ConsoleSettings? settings = null,
        string step = "InMatch",
        Func<MenuNode, bool>? toggle = null,
        Func<MenuNode, string?>? note = null,
        Func<MenuNode, string?>? value = null) => new()
        {
            Settings = settings ?? ConsoleSettings.Default,
            Tier = ConsoleTier.Owner,
            MatchStep = step,
            ServerVersion = "7b8dd95",
            ClientBuild = "1148",
            Toggle = toggle,
            Note = note,
            Value = value,
        };

    private static MenuState Open(params string[] path) =>
        MenuState.Closed with { Mode = MenuMode.Open, Path = [.. path] };

    [Fact]
    public void TheRootFrameIsDrawnLineForLine()
    {
        Dictionary<string, string> notes = new()
        {
            ["match"] = "InMatch 4:1",
            ["players"] = "1 online",
        };

        IReadOnlyList<string> frame = FrameRenderer.Render(
            Open(),
            Tree(),
            View(note: node => notes.GetValueOrDefault(node.Id)));

        Assert.Equal(
            [
                "+============================================+",
                "| CRANBERRY CONSOLE  v7b8dd95  |  1148       |",
                "| By Cranberry  --  root                     |",
                "|                                            |",
                "| >  1 Player                              > |",
                "|    2 Weapons                             > |",
                "|    3 Items                               > |",
                "|    4 Vehicles                            > |",
                "|    5 Loot                                > |",
                "|    6 Match                   InMatch 4:1 > |",
                "|    7 World                               > |",
                "|    8 Players                    1 online > |",
                "|    9 Debug                               > |",
                "|   10 Info                                > |",
                "| v 2 more                                   |",
                "| = /m player   godmode, heal, tp, where...  |",
                "| /d /u move  /s pick  /b back  /q quit  /m 3|",
                "+--------------------------------------------+",
            ],
            frame);
    }

    [Fact]
    public void ThePlayerFrameWithGodModeOnIsDrawnLineForLine()
    {
        IReadOnlyList<string> frame = FrameRenderer.Render(
            Open("player"),
            Tree(),
            View(toggle: node => node.Id == "player.god"));

        Assert.Equal(
            [
                "+============================================+",
                "| CRANBERRY CONSOLE  v7b8dd95  |  1148       |",
                "| root > Player                              |",
                "|                                            |",
                "| >  1 God mode                         [ON] |",
                "|    2 Heal                                  |",
                "|    3 Hurt                         < 2500 > |",
                "|    4 Kill me                               |",
                "|    5 Where am I                            |",
                "|    6 Teleport                            > |",
                "|    7 Hover +50 m                           |",
                "|    8 Parachute                       (n/a) |",
                "|    9 Speed                           (n/a) |",
                "|   10 Noclip                          (n/a) |",
                "| = /godmode off   ignore all damage to you  |",
                "| /d /u move  /s pick  /b back  /q quit  /m 3|",
                "+--------------------------------------------+",
            ],
            frame);
    }

    [Fact]
    public void APhaseGatedRowNamesTheStepItNeedsInTheValueColumn()
    {
        IReadOnlyList<string> frame = FrameRenderer.Render(Open("match"), Tree(), View(step: "Lobby"));

        Assert.Contains(frame, l => l.Contains("1 Start now", StringComparison.Ordinal) && l.Contains("(Menu)", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("2 Drop now", StringComparison.Ordinal) && l.Contains("(n/a)", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("3 Back to lobby", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("4 End match", StringComparison.Ordinal) && l.Contains("(InMatch)", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("6 Gas", StringComparison.Ordinal) && l.Contains("(InMatch)", StringComparison.Ordinal));
    }

    [Fact]
    public void ARefusedLeafIsGreyedAndTheInfoLineSaysWhy()
    {
        CommandRegistry registry = TestCatalog.Build();
        registry.MarkRefused("announce");

        IReadOnlyList<string> frame = FrameRenderer.Render(
            Open("players", "players.all"),
            MenuTree.Build(registry),
            View());

        Assert.Contains(frame, l => l.Contains("1 Announce", StringComparison.Ordinal) && l.Contains("(refused)", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("2 Evict", StringComparison.Ordinal) && l.Contains("(n/a)", StringComparison.Ordinal));
    }

    [Fact]
    public void AScrolledWindowShowsWhatIsAboveAndTheTwoDigitKeys()
    {
        MenuTree tree = Tree();
        MenuState settings = (Open("settings"))
            .Apply(new MenuInput(MenuVerb.End), tree, View()).State;

        IReadOnlyList<string> frame = FrameRenderer.Render(settings, tree, View());

        Assert.Contains(frame, l => l.Contains("^ 2 more", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("   3 Hint keys", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("> 12 Show all rows", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains(" 1 Rows", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSurfaceSettingShowsWhatItIsAndOffersWhatIsNext()
    {
        MenuTree tree = Tree();
        MenuState settings = (Open("settings"))
            .Apply(new MenuInput(MenuVerb.Pick, 10), tree, View()).State;
        MenuView view = View(value: node => node.Id == "settings.surface" ? "print" : null);

        IReadOnlyList<string> frame = FrameRenderer.Render(settings, tree, view);

        Assert.Contains(frame, l => l.Contains("10 Surface", StringComparison.Ordinal) && l.Contains("< print >", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("= /surface chat   print|chat|chat0|alert", StringComparison.Ordinal));
    }

    [Fact]
    public void APromptFrameAsksOneArgumentAtATime()
    {
        MenuTree tree = Tree();
        MenuState prompt = Open("player", "player.tp")
            .Apply(new MenuInput(MenuVerb.Pick, 1), tree, View()).State;

        IReadOnlyList<string> frame = FrameRenderer.Render(prompt, tree, View());

        Assert.Contains(frame, l => l.Contains("root > Player > Teleport > To coordinates", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("x?  [?]  _", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("y?  (next)", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("= /tp <x> <y> <z>", StringComparison.Ordinal));
    }

    [Fact]
    public void AConfirmFrameShowsTheExactLineAndTheTwoAnswers()
    {
        MenuTree tree = Tree();
        MenuState confirm = Open("player")
            .Apply(new MenuInput(MenuVerb.Pick, 4), tree, View()).State;

        IReadOnlyList<string> frame = FrameRenderer.Render(confirm, tree, View());

        Assert.Contains(frame, l => l.Contains("Run:  /kill", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("y  yes      n  no", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("= /m y   (anything else cancels)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(46, 10)]
    [InlineData(40, 6)]
    [InlineData(60, 16)]
    public void EveryLineIsExactlyTheFrameWidthAndNeverEmpty(int width, int rows)
    {
        ConsoleSettings settings = ConsoleSettings.Default with { Width = width, Rows = rows };
        MenuTree tree = Tree();

        foreach (string[] path in (string[][])[[], ["player"], ["settings"], ["match"], ["weapons", "weapons.give"]])
        {
            IReadOnlyList<string> frame = FrameRenderer.Render(Open(path), tree, View(settings));

            Assert.NotEmpty(frame);
            foreach (string line in frame)
            {
                Assert.Equal(width, line.Length);
                Assert.NotEqual(string.Empty, line);
            }
        }
    }

    [Fact]
    public void TheGridPutsTheCursorTheKeyAndTheLabelInTheirColumns()
    {
        IReadOnlyList<string> frame = FrameRenderer.Render(Open(), Tree(), View());
        string cursorRow = frame[4];
        string tenthRow = frame[13];

        Assert.Equal('|', cursorRow[0]);
        Assert.Equal('>', cursorRow[2]);
        Assert.Equal(" 1", cursorRow[4..6]);
        Assert.Equal('P', cursorRow[7]);
        Assert.Equal("10", tenthRow[4..6]);
        Assert.Equal(' ', tenthRow[2]);
    }

    [Fact]
    public void TheKeyColumnIsAlwaysTwoDigitsNeverALetter()
    {
        // Letters would collide with the verbs d u s b q r, which is why design §2.7 replaced R4's
        // "1-9 then a-z" hot keys with a two-character digit column.
        MenuTree tree = Tree();
        IReadOnlyList<string> frame = FrameRenderer.Render(Open("settings"), tree, View());

        // Rows sit between the blank line under the breadcrumb and the scroll/info/hint lines.
        foreach (string row in frame.Skip(4).Take(10))
        {
            string key = row[4..6].Trim();
            Assert.NotEmpty(key);
            Assert.All(key, c => Assert.True(char.IsAsciiDigit(c), $"key column held '{key}'"));
        }
    }

    [Fact]
    public void TheHeavyThemeChangesTheRulesAndTheSides()
    {
        IReadOnlyList<string> frame = FrameRenderer.Render(
            Open(),
            Tree(),
            View(ConsoleSettings.Default with { Theme = MenuTheme.Heavy }));

        Assert.StartsWith("#=", frame[0], StringComparison.Ordinal);
        Assert.EndsWith("=#", frame[^1], StringComparison.Ordinal);
        Assert.Equal('#', frame[4][0]);
        Assert.Equal('#', frame[4][^1]);
    }

    [Fact]
    public void TheHintAndInfoLinesCanBeTurnedOff()
    {
        ConsoleSettings quiet = ConsoleSettings.Default with { HintKeys = false, TypedHints = false };
        IReadOnlyList<string> frame = FrameRenderer.Render(Open(), Tree(), View(quiet));

        Assert.DoesNotContain(frame, l => l.Contains("/d /u move", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.TrimStart('|', ' ').StartsWith("= ", StringComparison.Ordinal));
    }

    [Fact]
    public void AClosedMenuDrawsNothing()
    {
        Assert.Empty(FrameRenderer.Render(MenuState.Closed, Tree(), View()));
    }

    [Theory]
    [InlineData("Practice target", 20, "Practice target")]
    [InlineData("Practice target", 10, "Practice..")]
    [InlineData("Practice target", 2, "Pr")]
    [InlineData("Practice target", 0, "")]
    public void ALabelTooLongForItsRoomIsCutWithAsciiDots(string label, int room, string expected)
    {
        Assert.Equal(expected, FrameRenderer.Truncate(label, room));
    }

    [Fact]
    public void TheBreadcrumbIsTheJiggyByLineAtTheRoot()
    {
        MenuTree tree = Tree();

        Assert.Equal("By Cranberry  --  root", FrameRenderer.Breadcrumb(Open(), tree));
        Assert.Equal("root > Player > Teleport", FrameRenderer.Breadcrumb(Open("player", "player.tp"), tree));
    }
}
