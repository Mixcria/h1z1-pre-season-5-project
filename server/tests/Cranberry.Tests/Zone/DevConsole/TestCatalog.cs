using Cranberry.Zone.DevConsole;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// A registry holding exactly the 50 names of design Appendix A, with placeholder bodies.
/// <para>
/// The backend lane owns the real <c>CommandCatalog</c>; this is the engine lane's stand-in, and it
/// is deliberately built to the same shape - the same names, aliases, groups, tiers, gates and
/// not-yet reasons - so the tests that matter here can be real ones: that the registry refuses a
/// name the client owns, that <c>Names</c> is exactly the burst the design promises, that every
/// live menu leaf binds a command, and that <c>/help</c> renders the page design §2.4 draws.
/// </para>
/// <para>
/// Each body answers with a line naming itself and its arguments, so a test can tell which command
/// ran, and with what, without wiring a server. The one exception is <see cref="Silent"/>, which answers nothing, so the
/// engine's "never silent" rule can be pinned.
/// </para>
/// </summary>
internal static class TestCatalog
{
    /// <summary>Builds the registry.</summary>
    public static CommandRegistry Build()
    {
        CommandRegistry registry = new();

        // --- player ---------------------------------------------------------------------------
        registry.Register(new ConsoleCommand
        {
            Name = "where", Group = "player", Usage = "/where", Summary = "your position as a /tp line",
            Run = Echo("where"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "tp", Aliases = ["teleport"], Group = "player", Tier = ConsoleTier.Tester,
            Usage = "/tp <x> <y> <z> | <place> | back", Summary = "move yourself",
            Args =
            [
                new ArgSpec("x", ArgKind.Float, Help: "world x, or ~ to keep"),
                new ArgSpec("y", ArgKind.Float, Help: "world y"),
                new ArgSpec("z", ArgKind.Float, Help: "world z"),
            ],
            Examples = ["/tp 348 32 130", "/tp ~ 200 ~"],
            MenuPaths = ["Player > Teleport"],
            Run = Echo("tp"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "up", Group = "player", Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch,
            Usage = "/up [m=50]", Summary = "hover straight up", Run = Echo("up"),
        });
        registry.Register(ConsoleCommand.NotAvailable(
            "chute", "player", "/chute [m=700]", "put yourself under a parachute",
            "SendParachute is still inline in SendLobbyHud", gate: MatchGate.InMatch));
        registry.Register(new ConsoleCommand
        {
            Name = "heal", Group = "player", Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch,
            Usage = "/heal [hp]", Summary = "restore health", Run = Echo("heal"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "hurt", Group = "player", Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch,
            Usage = "/hurt [hp=2500]", Summary = "damage yourself", Run = Echo("hurt"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "kill", Group = "player", Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch,
            Usage = "/kill", Summary = "die, through the real death path", Confirm = true,
            Run = Echo("kill"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "godmode", Group = "player", Tier = ConsoleTier.Tester,
            Usage = "/godmode [on|off]", Summary = "ignore all damage to you",
            Args = [new ArgSpec("state", ArgKind.Enum, "on", "on or off", ["on", "off"])],

            // The one stand-in that really moves something. It has to: the menu redraws after the
            // command has run, and a toggle whose row never changes could never show that.
            Run = call =>
            {
                call.Session.Invulnerable = call.Line.Word(0) switch
                {
                    "off" => false,
                    "on" => true,
                    _ => !call.Session.Invulnerable,
                };

                return ConsoleReply.Did($"God mode [{(call.Session.Invulnerable ? "ON" : "OFF")}]");
            },
        });
        registry.Register(ConsoleCommand.NotAvailable(
            "speed", "player", "/speed [x]", "run faster",
            "PlayerMovementTracker.SetProfile is not exposed yet", gate: MatchGate.InMatch));

        // --- items ----------------------------------------------------------------------------
        registry.Register(ConsoleCommand.NotAvailable(
            "give", "items", "/give <item|id> [count]", "add an item to your bag",
            "the inventory commit is not on main yet", gate: MatchGate.InMatch));
        registry.Register(ConsoleCommand.NotAvailable(
            "kit", "items", "/kit [pvp|guns|ammo|meds|armour]", "give a whole kit",
            "the inventory commit is not on main yet", gate: MatchGate.InMatch));
        registry.Register(ConsoleCommand.NotAvailable(
            "drop", "items", "/drop [hand|<slot>]", "drop what you hold",
            "the inventory commit is not on main yet", tier: ConsoleTier.Player, gate: MatchGate.InMatch));
        registry.Register(new ConsoleCommand
        {
            Name = "inv", Group = "items", Usage = "/inv", Summary = "list your bag",
            Run = Echo("inv"),
        });

        // --- vehicles ---------------------------------------------------------------------------
        registry.Register(new ConsoleCommand
        {
            Name = "car", Aliases = ["spawncar"], Group = "vehicles", Tier = ConsoleTier.Tester,
            Gate = MatchGate.InMatch, Usage = "/car <offroader|pickup|policecar|atv>",
            Summary = "spawn a vehicle beside you", Run = Echo("car"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "enter", Group = "vehicles", Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch,
            Usage = "/enter", Summary = "enter the nearest vehicle", Run = Echo("enter"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "exit", Group = "vehicles", Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch,
            Usage = "/exit", Summary = "leave the vehicle", Run = Echo("exit"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "fuel", Group = "vehicles", Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch,
            Usage = "/fuel [pct=100]", Summary = "refuel it", Run = Echo("fuel"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "cars", Group = "vehicles", Usage = "/cars", Summary = "the fleet census",
            Run = Echo("cars"),
        });

        // --- loot -------------------------------------------------------------------------------
        registry.Register(new ConsoleCommand
        {
            Name = "loot", Group = "loot", Tier = ConsoleTier.Player,
            Usage = "/loot spawn|ring|find|stats", Summary = "ground loot",
            Args = [new ArgSpec("item", ArgKind.Item, Help: "roster name or id")],
            Run = Echo("loot"),
        });

        // --- match ------------------------------------------------------------------------------
        registry.Register(new ConsoleCommand
        {
            Name = "match",
            Aliases = ["startmatch", "endmatch", "lobby", "matchstatus", "kotkdrop"],
            Group = "match", Usage = "/match start|drop|lobby|end|status",
            Summary = "step, seed, ring, hp", Run = Echo("match"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "gas", Group = "match", Tier = ConsoleTier.Owner, Gate = MatchGate.InMatch,
            Usage = "/gas start|stop|status", Summary = "the gas timetable", Run = Echo("gas"),
        });

        // --- world ------------------------------------------------------------------------------
        registry.Register(new ConsoleCommand
        {
            Name = "doors", Group = "world", Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch,
            Usage = "/doors open|close|toggle [m=60]", Summary = "doors near you",
            Run = Echo("doors"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "target", Group = "world", Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch,
            Usage = "/target [spawn|status]", Summary = "practice dummies", Run = Echo("target"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "win", Group = "world", Tier = ConsoleTier.Tester, KeepCase = true,
            Usage = "/win <window>|list|probe|raw <Object.Method>",
            Summary = "open one of the client's own windows (Ui.ExecuteScript)",
            Args =
            [
                new ArgSpec("window", ArgKind.Word, Help: "an alias, or list / probe / raw"),
                new ArgSpec("name", ArgKind.Word, Help: "for raw: the Object.Method to send"),
            ],
            Run = Echo("win"),
        });

        // --- players ----------------------------------------------------------------------------
        registry.Register(ConsoleCommand.NotAvailable(
            "players", "players", "/players", "who is online", "there is no session registry yet",
            tier: ConsoleTier.Player));
        registry.Register(new ConsoleCommand
        {
            Name = "announce", Group = "players", Tier = ConsoleTier.Owner, KeepCase = true,
            Usage = "/announce <text>", Summary = "banner across the screen",
            Args = [new ArgSpec("text", ArgKind.Text, Help: "what to say")],
            Run = Echo("announce"),
        });
        registry.Register(ConsoleCommand.NotAvailable(
            "evict", "players", "/evict <name> [reason]", "remove a player",
            "there is no session registry yet", tier: ConsoleTier.Owner));
        registry.Register(ConsoleCommand.NotAvailable(
            "tier", "players", "/tier <name> <tier>", "change a player's console tier",
            "there is no session registry yet", tier: ConsoleTier.Owner));
        registry.Register(ConsoleCommand.NotAvailable(
            "tphere", "players", "/tphere <name>", "pull a player to you",
            "there is no session registry yet", tier: ConsoleTier.Owner));

        // --- debug ------------------------------------------------------------------------------
        registry.Register(new ConsoleCommand
        {
            Name = "dump", Group = "debug", Tier = ConsoleTier.Tester,
            Usage = "/dump pos|inv|self [n=10]", Summary = "dump server state", Run = Echo("dump"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "raw", Group = "debug", Tier = ConsoleTier.Owner, KeepCase = true, Confirm = true,
            Usage = "/raw <hex...>", Summary = "put bytes on channel 0",
            Detail = ["Sends the bytes exactly as typed. Nothing checks them."],
            Args = [new ArgSpec("hex", ArgKind.Hex, Help: "the bytes")],
            Run = Echo("raw"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "watchdog", Group = "debug", Tier = ConsoleTier.Tester, Usage = "/watchdog",
            Summary = "the client progress watchdog", Run = Echo("watchdog"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "log", Group = "debug", Tier = ConsoleTier.Tester, Usage = "/log wire",
            Summary = "which wire file is being written", Run = Echo("log"),
        });

        // --- info -------------------------------------------------------------------------------
        registry.Register(new ConsoleCommand
        {
            Name = "info", Group = "info", Usage = "/info", Summary = "build, options, registry",
            Run = Echo("info"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "surface", Group = "info", Tier = ConsoleTier.Tester,
            Usage = "/surface probe|print|chat|chat0|alert", Summary = "print|chat|chat0|alert",
            Run = Echo("surface"),
        });

        // --- menu: nine separate names, exactly as /help prints them -------------------------
        foreach (string verb in (string[])["m", "menu", "d", "u", "s", "b", "q", "r"])
        {
            registry.Register(new ConsoleCommand
            {
                Name = verb,
                Group = "menu",
                Usage = verb == "m" ? "/m [row|verb|path]" : $"/{verb}",
                Summary = verb == "m" ? "open the menu" : $"menu {verb}",
                Run = Echo(verb),
            });
        }

        registry.Register(new ConsoleCommand
        {
            Name = "commands", Group = "menu", Usage = "/commands", Summary = "list every command",
            Run = Echo("commands"),
        });

        registry.Register(new ConsoleCommand
        {
            Name = "help", Group = "menu", Usage = "/help [group|command|page]",
            Summary = "the same list, reached by the client's HELP catch-all",
            Registered = false, Run = Echo("help"),
        });

        return registry;
    }

    /// <summary>A command whose body answers nothing, for the "never silent" rule.</summary>
    public static ConsoleCommand Silent { get; } = new()
    {
        Name = "quiet", Group = "debug", Usage = "/quiet", Summary = "answers nothing", Registered = false,
        Run = _ => ConsoleReply.Silent,
    };

    /// <summary>A command whose body throws, for the wrapper's error path.</summary>
    public static ConsoleCommand Exploding { get; } = new()
    {
        Name = "boom", Group = "debug", Usage = "/boom", Summary = "throws", Registered = false,
        Run = _ => throw new InvalidOperationException("boom"),
    };

    private static Func<CommandCall, ConsoleReply> Echo(string name) => call =>
    {
        string tail = call.Line.Raw.Length == 0 ? string.Empty : " " + call.Line.Raw;
        return ConsoleReply.Did($"{name}{tail}");
    };
}
