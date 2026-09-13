using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Menu;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// The dispatcher end to end, with no server underneath it: a hash arrives, lines come out
/// (design §4.1, §5.4).
/// </summary>
public sealed class ConsoleEngineTests
{
    [Fact]
    public void ATypedCommandAnswersWithOneLine()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().Send("where");

        Assert.Equal(["+ where"], fixture.Lines);
    }

    [Fact]
    public void AnAliasReachesTheSameCommand()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().Send("teleport", "1 2 3");

        Assert.Equal(["+ tp 1 2 3"], fixture.Lines);
    }

    [Fact]
    public void AnUnknownHashIsAnsweredNotDropped()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().SendHash(0xdeadbeef);

        Assert.Single(fixture.Lines);
        Assert.Equal("? unknown command 0xdeadbeef -- /commands lists everything", fixture.Lines[0]);
    }

    [Fact]
    public void TheHelpCatchAllOpensPageOneAndSaysWhy()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().SendHash(CommandHash.Help);

        Assert.Equal(HelpFormatter.CatchAllNote, fixture.Lines[0]);
        Assert.Contains(fixture.Lines, l => l.StartsWith("Cranberry console -- ", StringComparison.Ordinal));
    }

    [Fact]
    public void CommandsIsTheRegisteredSynonymOfHelp()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().Send("commands");

        Assert.DoesNotContain(HelpFormatter.CatchAllNote, fixture.Lines);
        Assert.StartsWith("Cranberry console -- ", fixture.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ATesterTierGateIsAnsweredWithoutGrantingOwnerCommands()
    {
        ConsoleFixture fixture = ConsoleFixture.Create();
        fixture.Session.Tier = ConsoleTier.Tester;

        fixture.Send("gas", "start");

        Assert.Equal(["! Owner only (you are Tester)"], fixture.Lines);
        Assert.Contains(fixture.Logged, l => l.Contains("REFUSED /gas", StringComparison.Ordinal));
    }

    [Fact]
    public void APhaseGateNamesTheStepItNeeds()
    {
        ConsoleFixture fixture = ConsoleFixture.Create();
        fixture.Step = "Lobby";

        fixture.Send("heal");

        Assert.Equal(["! not now: Lobby (needs InMatch)"], fixture.Lines);
    }

    [Fact]
    public void ANotYetCommandExplainsItselfInsteadOfRunning()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().Send("give", "ar15");

        Assert.Equal(["- not available yet -- the inventory commit is not on main yet"], fixture.Lines);
    }

    [Fact]
    public void ACommandThatAnswersNothingIsStillAnswered()
    {
        CommandRegistry registry = new();
        registry.Register(TestCatalog.Silent);
        ConsoleFixture fixture = ConsoleFixture.Over(registry).Send("quiet");

        Assert.Equal(["? /quiet ran; details in the host log"], fixture.Lines);
    }

    [Fact]
    public void KeepCaseCommandsSeeWhatWasTyped()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().Send("announce", "Hello There");

        Assert.Equal(["+ announce Hello There"], fixture.Lines);
    }

    [Fact]
    public void OtherCommandsSeeLowerCasedWords()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().Send("car", "ATV");

        Assert.Equal(["+ car ATV"], fixture.Lines);
        Assert.Equal("atv", CommandLine.Parse("car", "ATV").Word(0));
    }

    [Fact]
    public void TwoInputsInsideTheRateLimitOnlyRunOnce()
    {
        ConsoleFixture fixture = ConsoleFixture.Create();

        fixture.Send("where");
        fixture.Immediate("where");

        Assert.Single(fixture.Lines);
    }

    [Fact]
    public void OpeningTheMenuDrawsAWholeFrameAndNothingElse()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().Send("m");

        Assert.Equal(18, fixture.Lines.Count);
        Assert.StartsWith("+===", fixture.Lines[0], StringComparison.Ordinal);
        Assert.StartsWith("+---", fixture.Lines[^1], StringComparison.Ordinal);
        Assert.All(fixture.Lines, l => Assert.NotEqual(string.Empty, l));
    }

    [Fact]
    public void TheShortVerbsSteerTheMenu()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().Send("m").Clear();

        fixture.Send("d");
        Assert.Contains(fixture.Lines, l => l.Contains(">  2 Weapons", StringComparison.Ordinal));

        fixture.Clear().Send("s");
        Assert.Contains(fixture.Lines, l => l.Contains("root > Weapons", StringComparison.Ordinal));

        fixture.Clear().Send("b");
        Assert.Contains(fixture.Lines, l => l.Contains("By Cranberry", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeadVerbWithTheMenuClosedAnswersWithTheHint()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().Send("d");

        Assert.Equal(["? menu closed -- /m opens it"], fixture.Lines);
    }

    [Fact]
    public void SelectingGodModeRunsItAndRedrawsUnderTheResultLine()
    {
        ConsoleFixture fixture = ConsoleFixture.Create();
        fixture.Send("m", "1").Clear();

        fixture.Send("s");

        Assert.Equal("+ God mode [ON]", fixture.Lines[0]);
        Assert.StartsWith("+===", fixture.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheFrameRedrawsAfterTheCommandHasChangedTheWorldNotBefore()
    {
        // The whole reason MenuState.Apply does not render: the row reads the flag the invocation
        // is about to move, so a frame drawn first would contradict the result line above it.
        ConsoleFixture fixture = ConsoleFixture.Create();
        fixture.Send("m", "1").Clear();
        Assert.False(fixture.Session.Invulnerable);

        fixture.Send("s");

        Assert.True(fixture.Session.Invulnerable);
        Assert.Contains(fixture.Lines, l => l.Contains("1 God mode", StringComparison.Ordinal) && l.Contains("[ON]", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIdenticalRedrawIsSuppressed()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().Send("m").Clear();

        fixture.Send("r");

        Assert.Empty(fixture.Lines);
    }

    [Fact]
    public void SuppressionCanBeTurnedOff()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().Send("m").Clear();
        fixture.Session.Settings = fixture.Session.Settings with { SuppressIdenticalFrames = false };

        fixture.Send("r");

        Assert.NotEmpty(fixture.Lines);
    }

    [Fact]
    public void ClosingTheMenuPrintsTheStatusTickerWhenSomethingIsOn()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().Send("m").Clear();
        fixture.Session.Invulnerable = true;

        fixture.Send("q");

        Assert.Equal(["* GOD"], fixture.Lines);
        Assert.False(fixture.Session.Menu.IsOpen);
    }

    [Fact]
    public void ClosingWithNothingOnPrintsNothing()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().Send("m").Clear();

        fixture.Send("q");

        Assert.Empty(fixture.Lines);
    }

    [Fact]
    public void ASettingsRowChangesThisSessionOnly()
    {
        ConsoleFixture fixture = ConsoleFixture.Create();
        fixture.Send("m", "settings").Clear();

        fixture.Send("m", "1");

        Assert.Equal("+ Rows 12", fixture.Lines[0]);
        Assert.Equal(12, fixture.Session.Settings.Rows);
    }

    [Fact]
    public void TheSurfaceRowMovesTheSessionsSurface()
    {
        ConsoleFixture fixture = ConsoleFixture.Create();
        fixture.Send("m", "settings").Clear();

        fixture.Send("m", "10");

        Assert.Equal("+ Surface chat (06 05)", fixture.Lines[0]);
        Assert.Equal(ConsoleSurfaceKind.Chat, fixture.Session.SurfaceKind(fixture.Options));
    }

    [Fact]
    public void JumpAndRunFromAClosedMenuAnswersWithoutAFrame()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().Send("m", "vehicles atv");

        Assert.Equal(["+ car atv"], fixture.Lines);
        Assert.False(fixture.Session.Menu.IsOpen);
    }

    [Fact]
    public void JumpAndRunOntoAConfirmRowFromAClosedMenuDrawsTheConfirmFrameAndThenRuns()
    {
        ConsoleFixture fixture = ConsoleFixture.Create();

        // The engine's "never silent" rule through the whole path the owner walks in recipe step 6:
        // /m player kill with the menu closed used to emit nothing at all (review-1 M3).
        fixture.Send("m", "player kill");

        Assert.NotEmpty(fixture.Lines);
        Assert.True(fixture.Session.Menu.IsOpen);
        Assert.Contains(fixture.Lines, line => line.Contains("Kill me", StringComparison.Ordinal));

        // /m y is the confirm ("y" is not a name of its own; the frame's own hint line says so).
        fixture.Clear().Send("m", "y");

        Assert.Contains(fixture.Lines, line => line.StartsWith("+ kill", StringComparison.Ordinal));
    }

    [Fact]
    public void EachZoneSendsTheBurstEveryTimeAndTheReadyLineOnce()
    {
        ConsoleFixture fixture = ConsoleFixture.Create();

        fixture.Engine.OnClientIsReady(fixture.Context, fixture.Session, zoning: false);
        fixture.Engine.OnClientIsReady(fixture.Context, fixture.Session, zoning: true);

        Assert.Equal(102, fixture.Sent.Count);
        Assert.Equal(2, fixture.Session.BurstsSent);
        Assert.Single(fixture.Lines);
        Assert.Equal(ConsoleEngine.ReadyLine, fixture.Lines[0]);
    }

    [Fact]
    public void TheBurstIsOneAddWorldCommandPerNameInRegistryOrder()
    {
        ConsoleFixture fixture = ConsoleFixture.Create();

        fixture.Engine.OnClientIsReady(fixture.Context, fixture.Session, zoning: false);

        Assert.Equal(fixture.Registry.Names.Count, fixture.Sent.Count);
        for (int i = 0; i < fixture.Sent.Count; i++)
        {
            byte[] expected = Bytes(fixture.Registry.Names[i]);
            Assert.Equal(expected, fixture.Sent[i]);
        }
    }

    [Fact]
    public void OnceSendsTheBurstOnlyOnTheFirstClientIsReady()
    {
        ConsoleFixture fixture = ConsoleFixture.Create(
            ConsoleOptions.Default with { Register = ConsoleRegisterPolicy.Once });

        fixture.Engine.OnClientIsReady(fixture.Context, fixture.Session, zoning: false);
        int afterFirst = fixture.Sent.Count;
        fixture.Engine.OnClientIsReady(fixture.Context, fixture.Session, zoning: true);

        Assert.Equal(51, afterFirst);
        Assert.Equal(51, fixture.Sent.Count);
    }

    [Fact]
    public void ZoningSendsNothingOnTheMenuArm()
    {
        ConsoleFixture fixture = ConsoleFixture.Create(
            ConsoleOptions.Default with { Register = ConsoleRegisterPolicy.Zoning });

        fixture.Engine.OnClientIsReady(fixture.Context, fixture.Session, zoning: false);
        Assert.Empty(fixture.Sent);

        fixture.Engine.OnClientIsReady(fixture.Context, fixture.Session, zoning: true);
        Assert.Equal(51, fixture.Sent.Count);
    }

    [Fact]
    public void OffSendsNothingAtAll()
    {
        ConsoleFixture fixture = ConsoleFixture.Create(
            ConsoleOptions.Default with { Register = ConsoleRegisterPolicy.Off });

        fixture.Engine.OnClientIsReady(fixture.Context, fixture.Session, zoning: false);
        fixture.Engine.OnClientIsReady(fixture.Context, fixture.Session, zoning: true);

        Assert.Empty(fixture.Sent);
        Assert.Empty(fixture.Lines);
    }

    [Fact]
    public void ZoningClosesTheMenuAndKeepsTheCursors()
    {
        ConsoleFixture fixture = ConsoleFixture.Create();
        fixture.Send("m").Send("d");

        fixture.Engine.OnClientIsReady(fixture.Context, fixture.Session, zoning: true);

        Assert.False(fixture.Session.Menu.IsOpen);
        Assert.Equal(1, fixture.Session.Menu.CursorOf("root"));
    }

    [Fact]
    public void ARefusedNameLeavesTheNextBurstAndWarnsOnce()
    {
        ConsoleFixture fixture = ConsoleFixture.Create();
        fixture.Engine.RefusalSource = () => ["announce"];

        fixture.Engine.RefreshRefusals(fixture.Context);
        fixture.Engine.RefreshRefusals(fixture.Context);
        fixture.Engine.OnClientIsReady(fixture.Context, fixture.Session, zoning: false);

        Assert.Single(fixture.Warned);
        Assert.Contains("refused 'announce'", fixture.Warned[0], StringComparison.Ordinal);
        Assert.Equal(50, fixture.Sent.Count);
    }

    [Fact]
    public void EveryLineTheEngineDrawsIsSafeForTheClientToPrint()
    {
        ConsoleFixture fixture = ConsoleFixture.Create();
        fixture.Send("m").Send("d").Send("s").Send("m", "1").Send("commands").Send("m", "11").Send("m", "10");

        Assert.NotEmpty(fixture.Lines);
        Assert.All(fixture.Lines, line => Assert.NotEqual(string.Empty, line));
    }

    [Fact]
    public void TheEngineLogsOneLinePerExecutedCommand()
    {
        ConsoleFixture fixture = ConsoleFixture.Create().Send("where");

        Assert.Contains(
            fixture.Logged,
            l => l.StartsWith("console: /where args=[] tier=Owner -> 1 line(s) (print)", StringComparison.Ordinal));
    }

    private static byte[] Bytes(string name)
    {
        using Cranberry.Protocol.PacketWriter writer = new();
        new AddWorldCommand(name).WriteTo(writer);
        return writer.Written.ToArray();
    }
}
