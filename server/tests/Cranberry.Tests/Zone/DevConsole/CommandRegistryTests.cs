using Cranberry.Zone.DevConsole;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// The registry, and the reason it exists: a name the August client already owns can never be
/// typed against this server, so it must be refused at start-up rather than becoming a menu row
/// that does nothing (design §1.4, §4.2).
/// </summary>
public sealed class CommandRegistryTests
{
    private static ConsoleCommand Stub(string name, string[]? aliases = null, bool registered = true) => new()
    {
        Name = name,
        Aliases = aliases ?? [],
        Group = "debug",
        Usage = $"/{name}",
        Summary = name,
        Registered = registered,
        Run = _ => ConsoleReply.Did(name),
    };

    [Fact]
    public void TheShippedNamesAreExactlyTheOnesOfAppendixA()
    {
        CommandRegistry registry = TestCatalog.Build();

        Assert.Equal(
            ConsoleNameVectors.Appendix.Select(v => v.Name).OrderBy(n => n, StringComparer.Ordinal),
            registry.Names.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(51, registry.Names.Count);
    }

    [Fact]
    public void EveryShippedNameIsShortPrintableAsciiAndUnknownToTheClient()
    {
        CommandRegistry registry = TestCatalog.Build();

        foreach (string name in registry.Names)
        {
            Assert.InRange(name.Length, 1, CommandRegistry.MaximumNameLength);
            Assert.All(name, c => Assert.InRange(c, ' ', '~'));
            Assert.Equal(name, name.ToLowerInvariant());
            Assert.False(
                ClientRegistry1148.Contains(CommandHash.Compute(name)),
                $"'{name}' is in the client's own registry: {ClientRegistry1148.Describe(CommandHash.Compute(name))}");
        }
    }

    [Fact]
    public void NoTwoShippedNamesShareAHash()
    {
        CommandRegistry registry = TestCatalog.Build();
        List<uint> hashes = [.. registry.AllNames.Select(CommandHash.Compute)];

        Assert.Equal(hashes.Count, hashes.Distinct().Count());
    }

    [Theory]
    [InlineData("god")]
    [InlineData("respawn")]
    [InlineData("serverinfo")]
    [InlineData("netstats")]
    [InlineData("spectate")]
    [InlineData("vehicle")]
    [InlineData("item")]
    [InlineData("run")]
    [InlineData("goto")]
    public void ANameTheClientOwnsIsRefusedAndTheMessageNamesTheClientEntry(string name)
    {
        CommandRegistry registry = new();

        ConsoleCommandRefusedException refused =
            Assert.Throws<ConsoleCommandRefusedException>(() => registry.Register(Stub(name)));

        Assert.Contains($"'{name}'", refused.Message, StringComparison.Ordinal);
        Assert.Contains(ClientRegistry1148.Describe(CommandHash.Compute(name)), refused.Message, StringComparison.Ordinal);
        Assert.Empty(registry.Commands);
    }

    [Fact]
    public void ACommandThatIsNeverPushedMayCarryANameTheClientOwns()
    {
        // `help` is reached through the client's HELP catch-all and never registered with
        // AddWorldCommand, so the collision test does not apply to it.
        CommandRegistry registry = new();
        registry.Register(Stub("god", registered: false));

        Assert.Single(registry.Commands);
        Assert.Empty(registry.Names);
    }

    [Fact]
    public void HelpIsReachableByHashButNeverPushed()
    {
        CommandRegistry registry = TestCatalog.Build();

        Assert.DoesNotContain("help", registry.Names);
        Assert.True(registry.TryResolve(CommandHash.Help, out ConsoleCommand help));
        Assert.Equal("help", help.Name);
    }

    [Fact]
    public void TheShortMenuVerbsArePushed()
    {
        CommandRegistry registry = TestCatalog.Build();

        foreach (string verb in (string[])["m", "menu", "d", "u", "s", "b", "q", "r", "commands"])
        {
            Assert.Contains(verb, registry.Names);
        }
    }

    [Fact]
    public void AnAliasResolvesToItsCommandAndNameOfSaysWhichWasTyped()
    {
        CommandRegistry registry = TestCatalog.Build();

        Assert.Equal("tp", registry.ByName("teleport")?.Name);
        Assert.Equal("teleport", registry.NameOf(CommandHash.Compute("teleport")));
        Assert.Equal("tp", registry.NameOf(CommandHash.Compute("tp")));
    }

    [Fact]
    public void ANameLongerThanSixteenCharactersIsRefused()
    {
        CommandRegistry registry = new();

        Assert.Throws<ConsoleCommandRefusedException>(() => registry.Register(Stub("averyverylongcommandname")));
    }

    [Theory]
    [InlineData("Give")]
    [InlineData("gi ve")]
    [InlineData("gi/ve")]
    [InlineData("café")]
    public void ANameTheClientCannotCarryIsRefused(string name)
    {
        CommandRegistry registry = new();

        Assert.Throws<ConsoleCommandRefusedException>(() => registry.Register(Stub(name)));
    }

    [Fact]
    public void RegisteringTheSameNameTwiceIsRefused()
    {
        CommandRegistry registry = new();
        registry.Register(Stub("wobble"));

        Assert.Throws<ConsoleCommandRefusedException>(() => registry.Register(Stub("wobble")));
    }

    [Fact]
    public void MarkRefusedDropsTheNameFromTheNextBurst()
    {
        CommandRegistry registry = TestCatalog.Build();
        Assert.Contains("announce", registry.Names);

        Assert.True(registry.MarkRefused("announce"));
        Assert.False(registry.MarkRefused("announce"));

        Assert.DoesNotContain("announce", registry.Names);
        Assert.True(registry.IsRefused(registry.ByName("announce")));
        Assert.Equal(50, registry.Names.Count);
    }

    [Fact]
    public void GroupsComeOutInFirstSeenOrder()
    {
        CommandRegistry registry = TestCatalog.Build();

        Assert.Equal(
            ["player", "items", "vehicles", "loot", "match", "world", "players", "debug", "info", "menu"],
            registry.ByGroup().Select(g => g.Key));
    }

    [Fact]
    public void TheSummaryCountsWhatTheBootBannerPrints()
    {
        CommandRegistry registry = TestCatalog.Build();

        Assert.Contains($"{registry.Commands.Count} commands", registry.Summary, StringComparison.Ordinal);
        Assert.Contains("51 names", registry.Summary, StringComparison.Ordinal);
        Assert.Contains($"0 collide with the client registry ({ClientRegistry1148.Entries.Count} hashes)",
            registry.Summary, StringComparison.Ordinal);
        Assert.Contains("0 refused last run", registry.Summary, StringComparison.Ordinal);
    }
}
