using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Commands;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// <c>/win</c> through the engine: the tier gates, the reply the owner reads, the two-step
/// <c>/win raw</c> and the probe's spacing.
/// <para>
/// The fixture records what <c>ConsoleContext.UiScript</c> was asked for instead of building a
/// packet, and holds what <c>ConsoleContext.Later</c> was handed instead of sleeping - so the probe
/// can be asserted in order, at its declared delays, without a clock or a socket.
/// </para>
/// </summary>
public sealed class WindowCommandTests
{
    private static ConsoleFixture Console(ConsoleTier tier = ConsoleTier.Owner)
    {
        ConsoleFixture fixture = ConsoleFixture.Over(CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = true }));
        fixture.Session.Tier = tier;
        return fixture;
    }

    // ---- /win <alias> --------------------------------------------------------------------------

    [Fact]
    public void AnAliasSendsItsScriptAndSaysWhatWentOut()
    {
        ConsoleFixture console = Console().Clear();

        console.Send("win", "inventory");

        Assert.Equal([("HudHandler.ShowInventory", [])], console.Scripts);
        Assert.Equal(
            "+ sent Ui.ExecuteScript HudHandler.ShowInventory (34 bytes) - "
            + "the client does not acknowledge; watch the screen",
            Assert.Single(console.Lines));
        Assert.Contains(console.Logged, l => l.Contains("/win HudHandler.ShowInventory", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAliasLookupIsCaseInsensitiveEvenThoughTheCommandKeepsCase()
    {
        // KeepCase is there for /win raw (Lua lookups are case-sensitive); the aliases are ours and
        // must not punish a capital letter.
        Console().Send("win", "HudOff");

        Assert.Equal("HudHandler.Hide", Assert.Single(Console().Send("win", "hudoff").Scripts).Script);
    }

    [Fact]
    public void AnUnknownAliasIsAnsweredNotSwallowed()
    {
        ConsoleFixture console = Console().Clear();

        console.Send("win", "nosuchwindow");

        Assert.Empty(console.Scripts);
        Assert.StartsWith("- no such window: 'nosuchwindow'", Assert.Single(console.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void ABareWinAnswersTheUsage()
    {
        ConsoleFixture console = Console().Clear();

        console.Send("win");

        Assert.Empty(console.Scripts);
        Assert.StartsWith("? /win ", Assert.Single(console.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOwnerOnlyRowIsRefusedForATesterAndTheScriptNeverGoesOut()
    {
        ConsoleFixture console = Console(ConsoleTier.Tester).Clear();

        console.Send("win", "conunlock");

        Assert.Empty(console.Scripts);
        Assert.StartsWith("! /win conunlock is Owner only", Assert.Single(console.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void APlayerCannotReachWinAtAll()
    {
        ConsoleFixture console = Console(ConsoleTier.Player).Clear();

        console.Send("win", "inventory");

        Assert.Empty(console.Scripts);
        Assert.Empty(console.Lines);
    }

    // ---- /win list -----------------------------------------------------------------------------

    [Fact]
    public void ListNamesEveryAliasTheCallerMayUseAndHidesTheRest()
    {
        ConsoleFixture owner = Console().Clear();
        owner.Send("win", "list");
        string ownerPage = string.Join('\n', owner.Lines);

        ConsoleFixture tester = Console(ConsoleTier.Tester).Clear();
        tester.Send("win", "list");
        string testerPage = string.Join('\n', tester.Lines);

        foreach (WindowScript row in WindowScripts.All)
        {
            Assert.Contains(row.Alias, ownerPage, StringComparison.Ordinal);
            Assert.Contains(row.Script, ownerPage, StringComparison.Ordinal);
        }

        Assert.Contains("conunlock", ownerPage, StringComparison.Ordinal);
        Assert.Contains("raw <name>", ownerPage, StringComparison.Ordinal);
        Assert.DoesNotContain("conunlock", testerPage, StringComparison.Ordinal);
        Assert.DoesNotContain("raw <name>", testerPage, StringComparison.Ordinal);
        Assert.Empty(owner.Scripts);
    }

    // ---- /win raw ------------------------------------------------------------------------------

    [Fact]
    public void RawAsksOnceThenSends()
    {
        ConsoleFixture console = Console().Clear();

        console.Send("win", "raw HudHandler.ShowInventory");
        Assert.Empty(console.Scripts);
        Assert.StartsWith(
            "! send Ui.ExecuteScript HudHandler.ShowInventory? repeat the line to confirm",
            Assert.Single(console.Lines),
            StringComparison.Ordinal);

        console.Clear().Send("win", "raw HudHandler.ShowInventory");
        Assert.Equal([("HudHandler.ShowInventory", [])], console.Scripts);
        Assert.StartsWith("+ sent Ui.ExecuteScript", Assert.Single(console.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferentNameHasToBeConfirmedOnItsOwn()
    {
        ConsoleFixture console = Console();

        console.Send("win", "raw HudHandler.Show");
        console.Clear().Send("win", "raw HudHandler.Hide");

        Assert.Empty(console.Scripts);
        Assert.StartsWith("! send Ui.ExecuteScript HudHandler.Hide?", Assert.Single(console.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void TurningConfirmsOffSendsStraightAway()
    {
        ConsoleFixture console = Console();
        console.Session.Settings = console.Session.Settings with { Confirms = false };
        console.Clear();

        console.Send("win", "raw Console.Show");

        Assert.Equal([("Console.Show", [])], console.Scripts);
    }

    [Fact]
    public void RawKeepsTheCaseOfTheLuaName()
    {
        // lua_getglobal / lua_getfield are case-SENSITIVE, so a lower-casing parser would make
        // every raw probe fail silently - the one failure mode nothing on the wire would show.
        ConsoleFixture console = Console();
        console.Session.Settings = console.Session.Settings with { Confirms = false };

        console.Send("win", "raw HudHandler.ShowDeathList");

        Assert.Equal("HudHandler.ShowDeathList", Assert.Single(console.Scripts).Script);
    }

    [Fact]
    public void RawCarriesIntegerArgumentsAndRefusesAnythingElse()
    {
        ConsoleFixture console = Console();
        console.Session.Settings = console.Session.Settings with { Confirms = false };

        console.Send("win", "raw Some.Method 1 2 0x10");
        Assert.Equal([1u, 2u, 16u], Assert.Single(console.Scripts).Ints);

        console.Clear().Send("win", "raw Some.Method one");
        Assert.Single(console.Scripts);
        Assert.StartsWith("? /win ", Assert.Single(console.Lines), StringComparison.Ordinal);
        Assert.Contains("ints only", console.Lines[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("raw HudHandler", "no dot")]
    [InlineData("raw HudHandler.", "empty part")]
    [InlineData("raw HudHandler.Show()", "letters, digits")]
    [InlineData("raw 9Bad.Name", "not Identifier")]
    public void RawRefusesAMalformedName(string tail, string fragment)
    {
        ConsoleFixture console = Console();
        console.Session.Settings = console.Session.Settings with { Confirms = false };
        console.Clear();

        console.Send("win", tail);

        Assert.Empty(console.Scripts);
        Assert.Contains(fragment, Assert.Single(console.Lines), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RawWithNoNameAnswersItsOwnUsage()
    {
        ConsoleFixture console = Console().Clear();

        console.Send("win", "raw");

        Assert.Empty(console.Scripts);
        Assert.Contains("/win raw <Object.Method>", Assert.Single(console.Lines), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ui.Logout")]
    [InlineData("Ui.Quit")]
    [InlineData("SettingsHandler.SetAllControlsToDefault")]
    [InlineData("GameEvents.OnPlayerLogout")]
    public void RawRefusesTheDeniedNamesEvenForAnOwnerWithConfirmsOff(string script)
    {
        ConsoleFixture console = Console();
        console.Session.Settings = console.Session.Settings with { Confirms = false };
        console.Clear();

        console.Send("win", $"raw {script}");

        Assert.Empty(console.Scripts);
        Assert.StartsWith("! refused --", Assert.Single(console.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void RawIsOwnerOnly()
    {
        ConsoleFixture console = Console(ConsoleTier.Tester).Clear();

        console.Send("win", "raw HudHandler.Show");

        Assert.Empty(console.Scripts);
        Assert.StartsWith("! /win raw is Owner only", Assert.Single(console.Lines), StringComparison.Ordinal);
    }

    // ---- /win probe ----------------------------------------------------------------------------

    [Fact]
    public void ProbeSendsTheFirstStepNowAndDefersTheRestOneSecondApart()
    {
        ConsoleFixture console = Console().Clear();

        console.Send("win", "probe");

        Assert.Equal([("GameEvents.OnInventoryToggle", [])], console.Scripts);
        Assert.Equal([1000, 2000, 3000], console.Deferred.Select(d => d.Ms));
        Assert.Contains(console.Lines, l => l.Contains("1/4 GameEvents.OnInventoryToggle", StringComparison.Ordinal));
        Assert.Contains(console.Lines, l => l.Contains("positive control", StringComparison.Ordinal));
        Assert.Contains(console.Lines, l => l.Contains("Watch the SCREEN", StringComparison.Ordinal));

        console.RunDeferred();

        Assert.Equal(
            ["GameEvents.OnInventoryToggle", "Cranberry.NoSuchMethod", "HudHandler.Hide", "HudHandler.Show"],
            console.Scripts.Select(s => s.Script));
        Assert.Contains(console.Lines, l => l.Contains("2/4 Cranberry.NoSuchMethod", StringComparison.Ordinal));
        Assert.Contains(console.Lines, l => l.Contains("3/4 HudHandler.Hide", StringComparison.Ordinal));
        Assert.Contains(console.Lines, l => l.Contains("4/4 HudHandler.Show", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryProbeStepIsNumberedBeforeItsSendSoTheOwnerCanSayWhichOneMovedTheScreen()
    {
        ConsoleFixture console = Console().Clear();

        console.Send("win", "probe");
        console.RunDeferred();

        for (int step = 1; step <= WindowScripts.ProbeSteps.Count; step++)
        {
            int numbered = IndexOf(console.Lines, $"{step}/4 ");
            int sent = IndexOf(console.Lines, $"sent Ui.ExecuteScript {WindowScripts.ProbeSteps[step - 1].Script}");
            Assert.True(numbered >= 0, $"step {step} has no numbered line");
            Assert.True(sent > numbered, $"step {step} was sent before it was announced");
        }
    }

    [Fact]
    public void WithNoSchedulerProbeStillSendsEverythingAndSaysSo()
    {
        ConsoleFixture console = Console();
        console.SchedulerWired = false;
        console.Clear();

        console.Send("win", "probe");

        Assert.Equal(
            ["GameEvents.OnInventoryToggle", "Cranberry.NoSuchMethod", "HudHandler.Hide", "HudHandler.Show"],
            console.Scripts.Select(s => s.Script));
        Assert.Empty(console.Deferred);
        Assert.Contains(console.Lines, l => l.Contains("(sent now: no scheduler)", StringComparison.Ordinal));
    }

    // ---- the menu is the same engine -----------------------------------------------------------

    [Fact]
    public void TheWindowsBranchOpensAWindowWithOneSelect()
    {
        // /m windows then /s - the deliverable the owner asked for.
        ConsoleFixture console = Console();

        console.Send("m", "windows");
        console.Send("s");
        console.Send("s");

        Assert.Equal([("HudHandler.ShowInventory", [])], console.Scripts);
    }

    private static int IndexOf(IReadOnlyList<string> lines, string fragment)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains(fragment, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
