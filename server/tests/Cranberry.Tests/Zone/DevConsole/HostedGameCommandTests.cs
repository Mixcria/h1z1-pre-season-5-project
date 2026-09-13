using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Commands;

namespace Cranberry.Tests.Zone.DevConsole;

public sealed class HostedGameCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostedGamesAreRegisteredForPlayersWithoutNativeCommandCollisions(bool modMenu)
    {
        var registry = CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = modMenu });
        var command = Assert.IsType<ConsoleCommand>(registry.ByName("hostgame"));

        Assert.Equal(ConsoleTier.Player, command.Tier);
        Assert.True(command.KeepCase);
        Assert.True(command.SensitiveArguments);
        Assert.Equal(MatchGate.Any, command.Gate);
        Assert.Empty(command.Aliases);
        Assert.Same(command, registry.ByHash(CommandHash.Compute("hostgame")));
        Assert.False(ClientRegistry1148.Contains(CommandHash.Compute("hostgame")));
        Assert.Null(registry.ByName("hosted"));
    }

    [Theory]
    [InlineData(ConsoleTier.Player)]
    [InlineData(ConsoleTier.Tester)]
    [InlineData(ConsoleTier.Owner)]
    public void DispatchPreservesTheKeyAndCallerWhileRedactingTheHostLog(ConsoleTier tier)
    {
        const string key = "AbCDEF012345_-Secret";
        var registry = CommandCatalog.Build();
        var engine = ConsoleEngine.Create(ConsoleOptions.Default, registry, clock: () => 1000);
        var surface = new RecordingSurface();
        List<string> logs = [];
        CommandCall? received = null;
        var session = new ConsoleSession { Tier = tier };
        var context = new ConsoleContext
        {
            Surface = surface,
            Log = logs.Add,
            HostedGame = call =>
            {
                received = call;
                return ConsoleReply.Did("access granted");
            },
        };

        engine.Execute(context, session,
            new ExecuteCommandRequest(0x09, 0x0042, CommandHash.Compute("hostgame"), $"redeem {key}", false));

        Assert.NotNull(received);
        Assert.Same(context, received.Ctx);
        Assert.Same(session, received.Session);
        Assert.Equal(tier, received.Tier);
        Assert.Equal(key, received.Line.Word(1));
        Assert.Equal(["+ access granted"], surface.Lines);
        Assert.Contains(logs, line => line.Contains("/hostgame args=[redacted]", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnavailableBackendAnswersWithoutExposingTheKey()
    {
        var console = ConsoleFixture.Over(CommandCatalog.Build());
        console.Session.Tier = ConsoleTier.Player;

        console.Send("hostgame", "redeem AbCdSecret");

        Assert.Equal(["- hosted games unavailable"], console.Lines);
        Assert.DoesNotContain(console.Logged, line => line.Contains("AbCdSecret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PanelRefreshCanSuppressChatWithoutChangingOrdinaryEmptyReplyDiagnostics(bool suppress)
    {
        var engine = ConsoleEngine.Create(ConsoleOptions.Default, CommandCatalog.Build(), clock: () => 1000);
        var surface = new RecordingSurface();
        var session = new ConsoleSession { Tier = ConsoleTier.Player };
        var context = new ConsoleContext
        {
            Surface = surface,
            HostedGame = _ => new ConsoleReply([]) { SuppressOutput = suppress },
        };
        engine.Execute(context, session,
            new ExecuteCommandRequest(0x09, 0x0042, CommandHash.Compute("hostgame"), "panel", false));
        if (suppress) Assert.Empty(surface.Lines);
        else Assert.Contains(surface.Lines, line => line.Contains("ran; details in the host log", StringComparison.Ordinal));
    }

    [Fact]
    public void CommandHelpExplainsTheHostingAndInvitationWorkflow()
    {
        var console = ConsoleFixture.Over(CommandCatalog.Build());
        console.Session.Tier = ConsoleTier.Player;

        console.Send("commands", "hostgame");

        foreach (string verb in new[] { "key", "redeem", "create", "list", "keys", "invite", "revoke", "close", "join", "start", "mode" })
            Assert.Contains(console.Lines, line => line.Contains($"/hostgame {verb}", StringComparison.Ordinal));
        Assert.Contains(console.Lines, line => line.Contains("owner/admin only", StringComparison.Ordinal));
        Assert.Contains(console.Lines, line => line.Contains("solo|duos|fives", StringComparison.Ordinal));
        Assert.DoesNotContain(console.Lines, line => line.StartsWith("...", StringComparison.Ordinal));
    }

    [Fact]
    public void AdminAndClientAliasesPreserveTheExistingPermissionLadder()
    {
        Assert.Equal(0, (int)ConsoleTier.Player);
        Assert.Equal(1, (int)ConsoleTier.Tester);
        Assert.Equal(2, (int)ConsoleTier.Owner);
        Assert.Equal(ConsoleTier.Owner, ConsoleTier.Admin);
        var options = ConsoleOptions.Default with { LocalIsOwner = false, Tiers = "Host=admin,*=client" };

        Assert.Equal(ConsoleTier.Admin, ConsolePermission.Resolve(null, "Host", 1, options));
        Assert.Equal(ConsoleTier.Player, ConsolePermission.Resolve(null, "Guest", 2, options));
    }
}
