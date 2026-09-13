using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Menu;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// The state machine of design §2.8. Every case here is one the owner will hit in the first ten
/// minutes: moving, wrapping, picking a two-digit row, walking in and out, being refused by a gate,
/// and answering a prompt.
/// </summary>
public sealed class MenuStateTests
{
    private static MenuTree Tree() => MenuTree.Build(TestCatalog.Build());

    private static MenuView View(
        ConsoleTier tier = ConsoleTier.Owner,
        string step = "InMatch",
        ConsoleSettings? settings = null,
        Func<MenuNode, bool>? toggle = null) => new()
        {
            Settings = settings ?? ConsoleSettings.Default,
            Tier = tier,
            MatchStep = step,
            Toggle = toggle,
        };

    private static MenuState Opened() => MenuState.Closed with { Mode = MenuMode.Open };

    [Fact]
    public void AClosedMenuAnswersEveryVerbButOpenWithAHint()
    {
        MenuStep step = MenuState.Closed.Apply(new MenuInput(MenuVerb.Down), Tree(), View());

        Assert.False(step.State.IsOpen);
        Assert.False(step.Redraw);
        Assert.Equal("? menu closed -- /m opens it", step.ResultLine);
    }

    [Fact]
    public void ABareMenuOpensAndDraws()
    {
        MenuStep step = MenuState.Closed.Apply(new MenuInput(MenuVerb.Toggle), Tree(), View());

        Assert.Equal(MenuMode.Open, step.State.Mode);
        Assert.True(step.Redraw);
        Assert.Null(step.ResultLine);
    }

    [Fact]
    public void TheCursorWrapsAtBothEnds()
    {
        MenuTree tree = Tree();
        MenuState state = Opened();

        MenuStep up = state.Apply(new MenuInput(MenuVerb.Up), tree, View());
        Assert.Equal(11, up.State.CursorOf("root"));

        MenuStep down = up.State.Apply(new MenuInput(MenuVerb.Down), tree, View());
        Assert.Equal(0, down.State.CursorOf("root"));
    }

    [Fact]
    public void WrapCanBeTurnedOffInSettings()
    {
        MenuView view = View(settings: ConsoleSettings.Default with { WrapAround = false });
        MenuStep up = Opened().Apply(new MenuInput(MenuVerb.Up), Tree(), view);

        Assert.Equal(0, up.State.CursorOf("root"));
    }

    [Fact]
    public void FastStepsClampAndNeverWrap()
    {
        MenuTree tree = Tree();

        MenuStep down = Opened().Apply(new MenuInput(MenuVerb.FastDown), tree, View());
        Assert.Equal(5, down.State.CursorOf("root"));

        MenuStep again = down.State.Apply(new MenuInput(MenuVerb.FastDown), tree, View());
        Assert.Equal(10, again.State.CursorOf("root"));

        MenuStep more = again.State.Apply(new MenuInput(MenuVerb.FastDown), tree, View());
        Assert.Equal(11, more.State.CursorOf("root"));

        MenuStep clamped = more.State.Apply(new MenuInput(MenuVerb.FastDown), tree, View());
        Assert.Equal(11, clamped.State.CursorOf("root"));

        MenuStep top = clamped.State.Apply(new MenuInput(MenuVerb.Home), tree, View());
        Assert.Equal(0, top.State.CursorOf("root"));

        MenuStep end = top.State.Apply(new MenuInput(MenuVerb.End), tree, View());
        Assert.Equal(11, end.State.CursorOf("root"));
    }

    [Fact]
    public void TheWindowFollowsTheCursorAndTheRootNeedsTwoRowsOfScroll()
    {
        MenuTree tree = Tree();
        MenuStep end = Opened().Apply(new MenuInput(MenuVerb.End), tree, View());

        Assert.Equal(11, end.State.CursorOf("root"));
        Assert.Equal(2, end.State.WindowOf("root"));
    }

    [Fact]
    public void EachNodeRemembersItsOwnCursor()
    {
        MenuTree tree = Tree();
        MenuState state = Opened();

        state = state.Apply(new MenuInput(MenuVerb.Down), tree, View()).State;     // root -> Weapons
        state = state.Apply(new MenuInput(MenuVerb.Down), tree, View()).State;     // root -> Items
        state = state.Apply(new MenuInput(MenuVerb.Select), tree, View()).State;   // into Items
        state = state.Apply(new MenuInput(MenuVerb.Down), tree, View()).State;     // Items row 2
        state = state.Apply(new MenuInput(MenuVerb.Back), tree, View()).State;     // back to root

        Assert.Equal(2, state.CursorOf("root"));
        Assert.Equal(1, state.CursorOf("items"));
        Assert.True(state.Path.IsEmpty);
    }

    [Fact]
    public void BackAtTheRootClosesTheMenuAndKeepsTheCursors()
    {
        MenuTree tree = Tree();
        MenuState state = Opened().Apply(new MenuInput(MenuVerb.Down), tree, View()).State;

        MenuStep step = state.Apply(new MenuInput(MenuVerb.Back), tree, View());

        Assert.Equal(MenuMode.Closed, step.State.Mode);
        Assert.Equal(1, step.State.CursorOf("root"));
    }

    [Fact]
    public void SelectingASubmenuWalksIn()
    {
        MenuStep step = Opened().Apply(new MenuInput(MenuVerb.Select), Tree(), View());

        Assert.Equal(["player"], step.State.Path);
        Assert.True(step.Redraw);
    }

    [Fact]
    public void PickRunsTheRowWithThatPrintedNumber()
    {
        MenuTree tree = Tree();
        MenuState player = Opened().Apply(new MenuInput(MenuVerb.Select), tree, View()).State;

        MenuStep step = player.Apply(new MenuInput(MenuVerb.Pick, 5), tree, View());

        Assert.NotNull(step.Invocation);
        Assert.Equal("/where", step.Invocation.Typed);
        Assert.Equal(4, step.State.CursorOf("player"));
    }

    [Fact]
    public void PickAcceptsTwoDigitRowsInSettings()
    {
        MenuTree tree = Tree();
        MenuState settings = Opened() with { Path = ["settings"] };
        settings = settings.Apply(new MenuInput(MenuVerb.End), tree, View()).State;

        MenuStep step = settings.Apply(new MenuInput(MenuVerb.Pick, 12), tree, View());

        Assert.NotNull(step.Setting);
        Assert.Equal("showall", step.Setting.Key);
        Assert.Equal("on", step.Setting.Value);
    }

    [Fact]
    public void PickRefusesZeroAndAnythingOffTheEnd()
    {
        MenuTree tree = Tree();

        Assert.Equal("? not a row: 0", Opened().Apply(new MenuInput(MenuVerb.Pick, 0), tree, View()).ResultLine);
        Assert.Equal("? not a row: 99", Opened().Apply(new MenuInput(MenuVerb.Pick, 99), tree, View()).ResultLine);
    }

    [Fact]
    public void PickRefusesARowThatIsScrolledOffScreen()
    {
        MenuTree tree = Tree();
        MenuState settings = (Opened() with { Path = ["settings"] })
            .Apply(new MenuInput(MenuVerb.End), tree, View()).State;

        MenuStep step = settings.Apply(new MenuInput(MenuVerb.Pick, 1), tree, View());

        Assert.Equal("? row 1 is not on screen -- /d /u first", step.ResultLine);
        Assert.Null(step.Setting);
    }

    [Fact]
    public void RowsAboveTheCallersTierAreNotEvenOnThePage()
    {
        // Hiding is the gate in the menu: a Player never sees Start now, so he can neither pick it
        // nor be refused by it. The out-loud "! Owner only" belongs to the typed door, where the
        // owner can name any command he likes (ConsoleEngineTests).
        MenuTree tree = Tree();
        MenuState match = Opened() with { Path = ["match"] };

        MenuStep step = match.Apply(new MenuInput(MenuVerb.Pick, 1), tree, View(tier: ConsoleTier.Player));

        Assert.Equal("/match status", step.Invocation?.Typed);
    }

    [Fact]
    public void ShowAllRowsGreysThemForAnOwnerAndIsIgnoredBelowThatTier()
    {
        ConsoleSettings showAll = ConsoleSettings.Default with { ShowAllRows = true };

        Assert.True(View(tier: ConsoleTier.Owner, settings: showAll).ShowAllRows);
        Assert.False(View(tier: ConsoleTier.Tester, settings: showAll).ShowAllRows);
    }

    [Fact]
    public void APhaseGatedRowNamesTheStepItNeeds()
    {
        MenuTree tree = Tree();
        MenuState match = Opened() with { Path = ["match"] };

        MenuStep step = match.Apply(new MenuInput(MenuVerb.Pick, 4), tree, View(step: "Lobby"));

        Assert.Null(step.Invocation);
        Assert.Equal("! not now: Lobby (needs InMatch)", step.ResultLine);
    }

    [Fact]
    public void ANotYetRowExplainsItselfInsteadOfLookingBroken()
    {
        MenuTree tree = Tree();
        MenuState match = Opened() with { Path = ["match"] };

        MenuStep step = match.Apply(new MenuInput(MenuVerb.Pick, 2), tree, View());

        Assert.Null(step.Invocation);
        Assert.Equal("- not available yet -- BeginDrop is still inline in SendLobbyHud", step.ResultLine);
    }

    [Fact]
    public void ARefusedNameTellsTheOwnerToRenameIt()
    {
        CommandRegistry registry = TestCatalog.Build();
        registry.MarkRefused("announce");
        MenuTree tree = MenuTree.Build(registry);
        MenuState node = Opened() with { Path = ["players", "players.all"] };

        MenuStep step = node.Apply(new MenuInput(MenuVerb.Pick, 1), tree, View());

        Assert.Null(step.Invocation);
        Assert.Contains("the client refused the name 'announce'", step.ResultLine!, StringComparison.Ordinal);
    }

    [Fact]
    public void AToggleRunsTheOppositeOfWhatItShows()
    {
        MenuTree tree = Tree();
        MenuState player = Opened() with { Path = ["player"] };

        MenuStep on = player.Apply(new MenuInput(MenuVerb.Pick, 1), tree, View(toggle: _ => false));
        Assert.Equal("/godmode on", on.Invocation?.Typed);

        MenuStep off = player.Apply(new MenuInput(MenuVerb.Pick, 1), tree, View(toggle: _ => true));
        Assert.Equal("/godmode off", off.Invocation?.Typed);
    }

    [Fact]
    public void ACommandValueRowRunsWithWhatItShowsThenAdvances()
    {
        MenuTree tree = Tree();
        MenuState player = Opened() with { Path = ["player"] };

        MenuStep first = player.Apply(new MenuInput(MenuVerb.Pick, 3), tree, View());
        Assert.Equal("/hurt 2500", first.Invocation?.Typed);

        MenuStep second = first.State.Apply(new MenuInput(MenuVerb.Pick, 3), tree, View());
        Assert.Equal("/hurt 100", second.Invocation?.Typed);
    }

    [Fact]
    public void ASettingsToggleFlipsAndASettingsValueAdvances()
    {
        MenuTree tree = Tree();
        MenuState settings = Opened() with { Path = ["settings"] };

        MenuStep keys = settings.Apply(new MenuInput(MenuVerb.Pick, 3), tree, View(toggle: _ => true));
        Assert.Equal(new SettingChange("keys", "off"), keys.Setting);

        MenuStep theme = settings.Apply(new MenuInput(MenuVerb.Pick, 9), tree, new MenuView
        {
            Tier = ConsoleTier.Owner,
            MatchStep = "InMatch",
            Value = node => node.Id == "settings.theme" ? "plain" : null,
        });
        Assert.Equal(new SettingChange("theme", "heavy"), theme.Setting);
    }

    [Fact]
    public void AConfirmingLeafShowsTheLineFirstAndOnlyRunsOnYes()
    {
        MenuTree tree = Tree();
        MenuState player = Opened() with { Path = ["player"] };

        MenuStep asked = player.Apply(new MenuInput(MenuVerb.Pick, 4), tree, View());
        Assert.Equal(MenuMode.Confirm, asked.State.Mode);
        Assert.Null(asked.Invocation);
        Assert.Equal("/kill", asked.State.PendingInvocation?.Typed);

        MenuStep no = asked.State.Apply(new MenuInput(MenuVerb.No), tree, View());
        Assert.Null(no.Invocation);
        Assert.Equal("* cancelled", no.ResultLine);
        Assert.Equal(MenuMode.Open, no.State.Mode);

        MenuStep yes = asked.State.Apply(new MenuInput(MenuVerb.Yes), tree, View());
        Assert.Equal("/kill", yes.Invocation?.Typed);
        Assert.Equal(MenuMode.Open, yes.State.Mode);
    }

    [Fact]
    public void ConfirmsCanBeTurnedOffInSettings()
    {
        MenuTree tree = Tree();
        MenuState player = Opened() with { Path = ["player"] };
        MenuView view = View(settings: ConsoleSettings.Default with { Confirms = false });

        MenuStep step = player.Apply(new MenuInput(MenuVerb.Pick, 4), tree, view);

        Assert.Equal("/kill", step.Invocation?.Typed);
    }

    [Fact]
    public void APromptCollectsOneArgumentAtATimeAndThenRuns()
    {
        MenuTree tree = Tree();
        MenuState tp = Opened() with { Path = ["player", "player.tp"] };

        MenuStep opened = tp.Apply(new MenuInput(MenuVerb.Pick, 1), tree, View());
        Assert.Equal(MenuMode.Prompt, opened.State.Mode);
        Assert.Equal("player.tp.xyz", opened.State.PendingId);

        MenuState state = opened.State;
        state = state.Apply(new MenuInput(MenuVerb.Jump, Path: ["348"], Raw: "348"), tree, View()).State;
        state = state.Apply(new MenuInput(MenuVerb.Pick, 32, Raw: "32"), tree, View()).State;
        Assert.Equal(2, state.PendingIndex);

        MenuStep done = state.Apply(new MenuInput(MenuVerb.Jump, Path: ["130"], Raw: "130"), tree, View());

        Assert.Equal("/tp 348 32 130", done.Invocation?.Typed);
        Assert.Equal(MenuMode.Open, done.State.Mode);
    }

    [Fact]
    public void APromptRefusesRubbishAndStaysOpenWithTheReason()
    {
        MenuTree tree = Tree();
        MenuState prompt = (Opened() with { Path = ["player", "player.tp"] })
            .Apply(new MenuInput(MenuVerb.Pick, 1), tree, View()).State;

        MenuStep step = prompt.Apply(new MenuInput(MenuVerb.Jump, Path: ["north"], Raw: "north"), tree, View());

        Assert.Equal(MenuMode.Prompt, step.State.Mode);
        Assert.Equal(0, step.State.PendingIndex);
        Assert.Equal("x: 'north' is not a number", step.State.PendingError);
    }

    [Fact]
    public void APromptWithNoDefaultRefusesAnEmptyAnswer()
    {
        MenuTree tree = Tree();
        MenuState prompt = (Opened() with { Path = ["player", "player.tp"] })
            .Apply(new MenuInput(MenuVerb.Pick, 1), tree, View()).State;

        MenuStep step = prompt.Apply(new MenuInput(MenuVerb.Toggle), tree, View());

        Assert.Equal(MenuMode.Prompt, step.State.Mode);
        Assert.Equal("x is required", step.State.PendingError);
    }

    [Fact]
    public void BackCancelsAPrompt()
    {
        MenuTree tree = Tree();
        MenuState prompt = (Opened() with { Path = ["player", "player.tp"] })
            .Apply(new MenuInput(MenuVerb.Pick, 1), tree, View()).State;

        MenuStep step = prompt.Apply(new MenuInput(MenuVerb.Back), tree, View());

        Assert.Equal(MenuMode.Open, step.State.Mode);
        Assert.Equal("* cancelled", step.ResultLine);
        Assert.Null(step.State.PendingId);
    }

    [Fact]
    public void JumpWalksByUniquePrefixOnLabelsAndCommandNames()
    {
        MenuTree tree = Tree();

        MenuStep step = Opened().Apply(new MenuInput(MenuVerb.Jump, Path: ["veh"]), tree, View());

        Assert.Equal(["vehicles"], step.State.Path);
        Assert.True(step.Redraw);
    }

    [Fact]
    public void AnAmbiguousJumpListsTheCandidates()
    {
        MenuTree tree = Tree();

        MenuStep step = Opened().Apply(new MenuInput(MenuVerb.Jump, Path: ["p"]), tree, View());

        Assert.StartsWith("? ambiguous: 'p' --", step.ResultLine!, StringComparison.Ordinal);
        Assert.Contains("player", step.ResultLine!, StringComparison.Ordinal);
        Assert.Contains("players", step.ResultLine!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownJumpSaysSo()
    {
        MenuStep step = Opened().Apply(new MenuInput(MenuVerb.Jump, Path: ["zzz"]), Tree(), View());

        Assert.Equal("? not a menu path: 'zzz'", step.ResultLine);
    }

    [Fact]
    public void JumpAndRunFromAClosedMenuRunsWithoutOpeningAnything()
    {
        MenuTree tree = Tree();

        MenuStep step = MenuState.Closed.Apply(
            new MenuInput(MenuVerb.Jump, Path: ["vehicles", "atv"], Raw: "vehicles atv"),
            tree,
            View());

        Assert.Equal("/car atv", step.Invocation?.Typed);
        Assert.Equal(MenuMode.Closed, step.State.Mode);
        Assert.False(step.Redraw);
    }

    [Fact]
    public void JumpAndRunFromAClosedMenuOntoAConfirmRowOpensTheConfirmFrame()
    {
        MenuTree tree = Tree();

        // /m player kill from a closed menu. Closing the menu again here would wipe the pending
        // invocation and draw nothing, so the line would be silent and the player would not die
        // (review-1 M3). The confirm frame IS the reply; /y finishes it.
        MenuStep step = MenuState.Closed.Apply(
            new MenuInput(MenuVerb.Jump, Path: ["player", "kill"], Raw: "player kill"),
            tree,
            View());

        Assert.Equal(MenuMode.Confirm, step.State.Mode);
        Assert.True(step.Redraw);
        Assert.Equal("player.kill", step.State.PendingId);
        Assert.Equal("/kill", step.State.PendingInvocation?.Typed);
        Assert.Null(step.Invocation);

        MenuStep yes = step.State.Apply(new MenuInput(MenuVerb.Yes), tree, View());
        Assert.Equal("/kill", yes.Invocation?.Typed);
    }

    [Fact]
    public void JumpAndRunFromAClosedMenuOntoAPromptRowOpensThePromptFrame()
    {
        MenuTree tree = Tree();

        MenuStep step = MenuState.Closed.Apply(
            new MenuInput(MenuVerb.Jump, Path: ["player", "teleport", "to"], Raw: "player teleport to"),
            tree,
            View());

        Assert.Equal(MenuMode.Prompt, step.State.Mode);
        Assert.True(step.Redraw);
        Assert.Equal("player.tp.xyz", step.State.PendingId);
    }

    [Fact]
    public void JumpAndRunSuppliesAPromptsArgumentsOutright()
    {
        MenuTree tree = Tree();

        MenuStep step = MenuState.Closed.Apply(
            new MenuInput(MenuVerb.Jump, Path: ["player", "teleport", "to", "348", "32", "130"]),
            tree,
            View());

        Assert.Equal("/tp 348 32 130", step.Invocation?.Typed);
        Assert.Equal(MenuMode.Closed, step.State.Mode);
    }

    [Fact]
    public void ZoningClosesTheMenuButKeepsEveryCursor()
    {
        MenuTree tree = Tree();
        MenuState state = Opened()
            .Apply(new MenuInput(MenuVerb.Down), tree, View()).State
            .Apply(new MenuInput(MenuVerb.Select), tree, View()).State;

        MenuState after = state.CloseKeepingCursors();

        Assert.Equal(MenuMode.Closed, after.Mode);
        Assert.Equal(1, after.CursorOf("root"));
        Assert.Equal(state.Path, after.Path);
    }

    [Fact]
    public void ANodeWithNothingVisibleAnswersRatherThanDrawingAnEmptyList()
    {
        MenuTree tree = Tree();
        MenuState gas = Opened() with { Path = ["match", "match.gas"] };

        MenuStep step = gas.Apply(new MenuInput(MenuVerb.Select), tree, View(tier: ConsoleTier.Player));

        Assert.Equal("? nothing on this page", step.ResultLine);
    }

    [Fact]
    public void SlugReducesALabelToTheWordAJumpWouldType()
    {
        Assert.Equal("giveitem", MenuState.Slug("Give item..."));
        Assert.Equal("godmode", MenuState.Slug("God mode"));
        Assert.Equal("spawnoffroader", MenuState.Slug("Spawn Off-Roader"));
    }
}
