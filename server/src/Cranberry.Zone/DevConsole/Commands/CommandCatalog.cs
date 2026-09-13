namespace Cranberry.Zone.DevConsole.Commands;

/// <summary>
/// Development slash commands for the August client. The parked mod menu, arbitrary packet
/// sender and UI-window probes are excluded from the default registry.
/// Names are checked against the client's local command registry before registration.
/// </summary>
public static class CommandCatalog
{
    /// <summary>The menu's own names: the toggle, its long form and the six short verbs.</summary>
    public static IReadOnlyList<string> MenuNames { get; } = ["m", "menu", "d", "u", "s", "b", "q", "r"];

    /// <summary>Builds the development registry, or the explicitly enabled legacy menu catalogue.</summary>
    public static CommandRegistry Build(ConsoleOptions? options = null)
    {
        options ??= ConsoleOptions.Default;
        CommandRegistry registry = new();

        PlayerCommands.AddTo(registry);
        ItemCommands.AddTo(registry);
        VehicleCommands.AddTo(registry);
        LootCommands.AddTo(registry);
        MatchCommands.AddTo(registry);
        HostedGameCommands.AddTo(registry);
        WorldCommands.AddTo(registry);
        BotCommands.AddTo(registry);

        if (options.ModMenuEnabled) WindowCommands.AddTo(registry);
        InfoCommands.AddTo(registry);
        AddMenu(registry, options.ModMenuEnabled);

        if (!options.ModMenuEnabled)
        {
            var development = new CommandRegistry();
            development.RegisterAll(registry.Commands.Where(command => command.Name != "raw"));
            return development;
        }
        return registry;
    }

    /// <summary>
    /// The menu group. Every one of these names is intercepted by <see cref="ConsoleEngine"/>
    /// before a body could run - <c>MenuInput.IsMenuName</c> - so the bodies below exist only to
    /// keep the registry's shape honest and to answer if that interception is ever removed.
    /// </summary>
    private static void AddMenu(CommandRegistry registry, bool enabled)
    {
        if (enabled)
        {
            registry.Register(new ConsoleCommand
            {
                Name = "m",
                Aliases = ["menu"],
                Group = "menu",
                Tier = ConsoleTier.Player,
                Usage = "/m [row|verb|path]",
                Summary = "open the menu, or step through it",
                Args = [new ArgSpec("step", ArgKind.Word, "-", "a row number, d u s b q r, or a path")],
                Examples = ["/m", "/m 3", "/m dd", "/m vehicles atv", "/m y"],
                Detail =
                [
                    "/m alone opens or redraws. /m <n> picks the row printed with that number.",
                    "/m <words> jumps by name and runs the leaf when the words complete it.",
                ],
                Run = MenuBody,
            });

            foreach (string verb in (string[])["d", "u", "s", "b", "q", "r"])
            {
                registry.Register(new ConsoleCommand
                {
                    Name = verb,
                    Group = "menu",
                    Tier = ConsoleTier.Player,
                    Usage = $"/{verb}",
                    Summary = verb switch
                    {
                        "d" => "cursor down",
                        "u" => "cursor up",
                        "s" => "select the row under the cursor",
                        "b" => "back out of this menu",
                        "q" => "close the menu",
                        _ => "draw the frame again",
                    },
                    Run = MenuBody,
                });
            }

        }

        registry.Register(new ConsoleCommand
        {
            Name = "commands",
            Group = "menu",
            Tier = ConsoleTier.Player,
            Usage = "/commands",
            Summary = "list every command (the always-pushed synonym of /help)",
            Detail =
            [
                "It exists because whether a typed /help reaches the server at 1148 is [U]:",
                "the client turns an UNREGISTERED name into hash(HELP), but nobody has read",
                "the 1148 console input handler. /commands is pushed, so it always arrives.",
            ],
            Run = HelpBody,
        });

        // Not pushed, on purpose. `help` is reached through the client's own HELP catch-all hash
        // 0xd51bdb69, which is what an unregistered name collapses to; pushing it would collide
        // with the console's own local `help` word and, worse, would make the refusal-log reader
        // drop it every boot (design §1.4).
        registry.Register(new ConsoleCommand
        {
            Name = "help",
            Group = "menu",
            Tier = ConsoleTier.Player,
            Usage = "/help [group|command|page]",
            Summary = "the same list, reached by the client's HELP catch-all",
            Registered = false,
            Run = HelpBody,
        });
    }

    /// <summary>Unreachable in practice: the engine steers every menu name into the state machine.</summary>
    private static ConsoleReply MenuBody(CommandCall call) =>
        ConsoleReply.Usage("menu closed -- /m opens it");

    /// <summary>
    /// Unreachable in practice: the engine renders <c>help</c> and <c>commands</c> itself, straight
    /// out of the registry through <see cref="HelpFormatter"/>, so that one page can never drift
    /// from what was actually registered.
    /// </summary>
    private static ConsoleReply HelpBody(CommandCall call) =>
        ConsoleReply.Usage("/commands lists every command");
}
