namespace Cranberry.Zone.DevConsole.Commands;

/// <summary>Connected-player administration and development diagnostics.</summary>
public static class InfoCommands
{
    /// <summary>The dump sub-verbs.</summary>
    public static IReadOnlyList<string> DumpVerbs { get; } = ["pos", "inv", "self"];

    /// <summary>What <c>/surface</c> accepts.</summary>
    public static IReadOnlyList<string> SurfaceWords { get; } =
        ["probe", "print", "chat", "chat0", "alert"];

    /// <summary>Adds the Players, Debug and Info groups, in that order.</summary>
    public static CommandRegistry AddTo(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);


        // --- players -------------------------------------------------------------------------
        registry.Register(new ConsoleCommand
        {
            Name = "players", Group = "players", Tier = ConsoleTier.Player,
            Usage = "/players", Summary = "list connected players, positions and match states",
            Run = c => c.Ctx.Players?.Invoke() ?? ConsoleReply.Failed("player registry unavailable"),
        });

        registry.Register(new ConsoleCommand
        {
            Name = "announce",
            Group = "players",
            Tier = ConsoleTier.Owner,
            KeepCase = true,
            Usage = "/announce <text>",
            Summary = "a banner across the screen",
            Args = [new ArgSpec("text", ArgKind.Text, Help: "what to say; case is kept")],
            Detail = ["Sends the banner to every connected test player."],
            MenuPaths = ["Players > All players > Announce..."],
            Run = Announce,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "evict", Group = "players", Tier = ConsoleTier.Owner, KeepCase = true,
            Usage = "/evict <name|guid> [reason]", Summary = "disconnect a test client",
            Run = c => c.Count < 1 ? c.Usage() : c.Ctx.Evict?.Invoke(c.Line.Word(0)!, c.Line.Text(1))
                ?? ConsoleReply.Failed("player registry unavailable"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "tier", Group = "players", Tier = ConsoleTier.Owner,
            Usage = "/tier <name|guid> <player|client|tester|owner|admin|reset>", Summary = "change console permission for this connection",
            Run = c => c.Count != 2 ? c.Usage() : c.Ctx.SetTier?.Invoke(c.Line.Word(0)!, c.Line.Word(1)!)
                ?? ConsoleReply.Failed("player registry unavailable"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "tphere", Group = "players", Tier = ConsoleTier.Owner, Gate = MatchGate.InMatch,
            Usage = "/tphere <name|guid>", Summary = "teleport a test player to you",
            Run = c => c.Count != 1 ? c.Usage() : c.Ctx.TeleportHere?.Invoke(c.Line.Word(0)!)
                ?? ConsoleReply.Failed("player registry unavailable"),
        });

        // --- debug ---------------------------------------------------------------------------
        registry.Register(new ConsoleCommand
        {
            Name = "dump",
            Group = "debug",
            Tier = ConsoleTier.Tester,
            Usage = "/dump pos|inv|self [n=10]",
            Summary = "dump server-side state",
            Args =
            [
                new ArgSpec("what", ArgKind.Enum, "pos", "which dump", [.. DumpVerbs]),
                new ArgSpec("n", ArgKind.Int, "10", "how many rows"),
            ],
            MenuPaths = ["Debug > Dump position stream"],
            Run = Dump,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "combat", Group = "debug", Tier = ConsoleTier.Tester,
            Usage = "/combat", Summary = "held weapon, magazine, reserve, reload and target status",
            Run = c => c.Ctx.CombatStatus?.Invoke() ?? ConsoleReply.Failed("combat state unavailable"),
        });

        registry.Register(new ConsoleCommand
        {
            Name = "raw",
            Group = "debug",
            Tier = ConsoleTier.Owner,
            KeepCase = true,
            Confirm = true,
            Usage = "/raw <hex...>",
            Summary = "put bytes on gateway channel 0",
            Args = [new ArgSpec("hex", ArgKind.Hex, Help: "the bytes, spaces allowed")],
            Detail =
            [
                "The bytes go out exactly as typed. Nothing checks them, nothing frames them",
                "beyond the tunnel header, and a malformed packet can drop the client.",
            ],
            Examples = ["/raw 11 31 00 00000004 74657374"],
            MenuPaths = ["Debug > Send raw hex..."],
            Run = Raw,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "watchdog",
            Group = "debug",
            Tier = ConsoleTier.Tester,
            Usage = "/watchdog",
            Summary = "the client-progress watchdog (docs/35)",
            MenuPaths = ["Debug > Watchdog"],
            Run = Watchdog,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "log",
            Group = "debug",
            Tier = ConsoleTier.Tester,
            Usage = "/log wire",
            Summary = "which capture file this session is written to",
            MenuPaths = ["Debug > Wire log"],
            Run = Log,
        });

        // --- info ----------------------------------------------------------------------------
        registry.Register(new ConsoleCommand
        {
            Name = "rank", Group = "info", Tier = ConsoleTier.Player,
            Usage = "/rank [status|bronze|silver|gold|plat|diamond|emerald|royalty|staff|reset]",
            Summary = "show score and season rank; admins can preview feed badges",
            Run = c => c.Count > 1 ? c.Usage() : c.Ctx.Rank?.Invoke(c.Line.Word(0) ?? "status")
                ?? ConsoleReply.Failed("scoring unavailable"),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "info",
            Group = "info",
            Tier = ConsoleTier.Player,
            Usage = "/info",
            Summary = "build, client, session, options, registry",
            MenuPaths = ["Info > Show info"],
            Run = Info,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "surface",
            Group = "info",
            Tier = ConsoleTier.Tester,
            Usage = "/surface probe|print|chat|chat0|alert",
            Summary = "which packet a console line becomes",
            Args = [new ArgSpec("which", ArgKind.Enum, "probe", "surface or probe", [.. SurfaceWords])],
            Detail =
            [
                "probe sends one labelled line on each of the four; whichever you can SEE names",
                "the surface to keep. CRANBERRY_CONSOLE_SURFACE makes it the boot default.",
            ],
            MenuPaths = ["Info > Surface probe", "Info > Surface"],
            Run = Surface,
        });

        return registry;
    }

    private static ConsoleReply Announce(CommandCall call)
    {
        string text = call.Line.Text();
        if (text.Length == 0)
        {
            return call.Usage("say something");
        }

        return call.Ctx.Announce is { } announce
            ? announce(text)
            : ConsoleReply.Failed("not available yet -- no banner writer is wired on this build");
    }

    private static ConsoleReply Dump(CommandCall call)
    {
        if (call.Ctx.Dump is not { } dump)
        {
            return ConsoleReply.Failed("not available yet -- no dumps are wired on this build");
        }

        string what = call.Line.SubVerb(0, DumpVerbs, out string? error) ?? "pos";
        if (error is not null)
        {
            return ConsoleReply.Failed(error);
        }

        int rows = call.Line.Int(1, 10);
        return dump(what, Math.Clamp(rows, 1, 40));
    }

    private static ConsoleReply Raw(CommandCall call)
    {
        if (call.Ctx.Raw is not { } raw)
        {
            return ConsoleReply.Failed("not available yet -- no raw sender is wired on this build");
        }

        byte[]? bytes = call.Line.HexBytes();
        if (bytes is null || bytes.Length == 0)
        {
            return call.Usage("give an even run of hex digits");
        }

        // The armed latch is what the status ticker prints on close: once /raw has actually put
        // bytes on the wire this session, the owner is told so every time he leaves the menu.
        call.Session.RawArmed = true;
        return raw(bytes);
    }

    private static ConsoleReply Watchdog(CommandCall call) =>
        call.Ctx.Watchdog is { } watchdog
            ? watchdog()
            : ConsoleReply.Failed("not available yet -- the watchdog is not wired on this build");

    private static ConsoleReply Log(CommandCall call) =>
        call.Ctx.WireLog is { } wire
            ? wire()
            : ConsoleReply.Failed("not available yet -- no capture path is wired on this build");

    private static ConsoleReply Info(CommandCall call) =>
        call.Ctx.InfoLines is { } info
            ? ConsoleReply.Plain(info())
            : ConsoleReply.Failed("not available yet -- no info source is wired on this build");

    private static ConsoleReply Surface(CommandCall call)
    {
        string word = call.Line.Word(0, "probe");
        if (word == "probe")
        {
            return Probe(call);
        }

        ConsoleSurfaceKind? kind = word switch
        {
            "print" => ConsoleSurfaceKind.Print,
            "chat" or "chat1" => ConsoleSurfaceKind.Chat,
            "chat0" => ConsoleSurfaceKind.Chat0,
            "alert" => ConsoleSurfaceKind.Alert,
            _ => null,
        };

        if (kind is null)
        {
            return call.Usage($"'{word}' is not a surface -- {string.Join(' ', SurfaceWords)}");
        }

        call.Session.Surface = kind.Value;
        return ConsoleReply.Did(
            $"Surface {ConsoleOptions.SurfaceWord(kind.Value)} "
            + $"({ConsoleOptions.SurfaceOpcode(kind.Value)})");
    }

    /// <summary>
    /// The one command that answers design open question 1. It sends four labelled lines, one on
    /// each surface, so the owner can read the pane and say which arrived - and then a fifth line
    /// on his session's own surface naming what is in force. If the fifth is the only one he sees,
    /// the answer is already in his hands (design §4.5).
    /// </summary>
    private static ConsoleReply Probe(CommandCall call)
    {
        if (call.Ctx.SurfaceFor is not { } surfaceFor)
        {
            return ConsoleReply.Failed("not available yet -- no surface factory is wired on this build");
        }

        (ConsoleSurfaceKind Kind, string Label)[] probes =
        [
            (ConsoleSurfaceKind.Print, "print"),
            (ConsoleSurfaceKind.Chat, "chat1"),
            (ConsoleSurfaceKind.Chat0, "chat0"),
            (ConsoleSurfaceKind.Alert, "alert"),
        ];

        for (int index = 0; index < probes.Length; index++)
        {
            (ConsoleSurfaceKind kind, string label) = probes[index];
            surfaceFor(kind).Line($"[{label}] Cranberry probe {index + 1}/{probes.Length}");
        }

        string current = call.Session.Surface is { } chosen
            ? ConsoleOptions.SurfaceWord(chosen)
            : "the boot default";
        return ConsoleReply.Note(
            $"probe sent on print, chat1, chat0, alert -- this session draws on "
            + $"{current}; /surface <one> keeps whichever you saw");
    }
}
