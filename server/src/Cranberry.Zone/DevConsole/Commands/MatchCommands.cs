namespace Cranberry.Zone.DevConsole.Commands;

/// <summary>
/// The Match group: the match flow and the gas timetable.
/// <para>
/// <b>Why the aliases are names and not sub-verbs.</b> The owner types <c>/startmatch</c>,
/// <c>/endmatch</c>, <c>/lobby</c>, <c>/matchstatus</c> and <c>/kotkdrop</c> on his own server
/// (R1 §2.4), so all five are registered names of their own; each simply supplies the sub-verb the
/// body would have read from the tail. Both doors then run one body, which is the "one engine"
/// rule (design §0 item 5).
/// </para>
/// <para>
/// <b>The gate lives on the sub-verb, not on the command.</b> <c>/match start</c> needs Menu or Lobby
/// step and <c>/match end</c> needs InMatch, so the command itself is ungated and each sub-verb
/// refuses with the same <see cref="MatchGates"/> sentence the menu row would have used. That is
/// what keeps a typed refusal and a menu refusal word for word identical (design §4.2).
/// </para>
/// </summary>
public static class MatchCommands
{
    /// <summary>The match sub-verbs, in the order an ambiguous prefix lists them.</summary>
    public static IReadOnlyList<string> MatchVerbs { get; } = ["start", "drop", "lobby", "end", "status"];

    /// <summary>Gas lifecycle, next scheduled event and current state.</summary>
    public static IReadOnlyList<string> GasVerbs { get; } = ["pause", "resume", "start", "stop", "status", "next"];

    /// <summary>Adds the Match group to the registry.</summary>
    public static CommandRegistry AddTo(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register(new ConsoleCommand
        {
            Name = "match",
            Aliases = ["startmatch", "endmatch", "lobby", "matchstatus", "kotkdrop"],
            Group = "match",
            Tier = ConsoleTier.Player,
            Usage = "/match start|drop|lobby|end|status",
            Summary = "the match flow; /matchstatus is the read-only one",
            Args = [new ArgSpec("verb", ArgKind.Enum, "status", "which action", [.. MatchVerbs])],
            Examples = ["/match status", "/startmatch", "/lobby"],
            Detail =
            [
                "start enters a match from Menu, or begins the drop immediately from Lobby.",
                "drop begins the lobby drop; lobby returns to pregame; end shows the victory flow.",
            ],
            MenuPaths = ["Match > Start now", "Match > Status"],
            Run = Match,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "gas",
            Aliases = ["pausegas", "resumegas"],
            Group = "match",
            Tier = ConsoleTier.Owner,
            Gate = MatchGate.InMatch,
            Usage = "/gas [pause|resume|status|next|start|stop]",
            Summary = "pause or resume the match's gas without losing the circle",
            Args = [new ArgSpec("verb", ArgKind.Enum, "status", "which action", [.. GasVerbs])],
            Examples = ["/gas pause", "/gas resume", "/gas status"],
            Detail = ["pause/resume/next affect everyone sharing this match's gas plan.",
                "Paused gas does no damage. Resume before next. start/stop retain the per-player override."],
            MenuPaths = ["Match > Gas"],
            Run = Gas,
        });

        return registry;
    }

    /// <summary>The sub-verb a name implies, or null when the name carries none.</summary>
    private static string? VerbOfAlias(string name) => name switch
    {
        "startmatch" => "start",
        "endmatch" => "end",
        "lobby" => "lobby",
        "matchstatus" => "status",
        "kotkdrop" => "drop",
        _ => null,
    };

    private static ConsoleReply Match(CommandCall call)
    {
        string? verb = VerbOfAlias(call.Line.Name);
        if (call.Count > (verb is null ? 1 : 0)) return call.Usage("use one match action at a time");
        if (verb is null)
        {
            verb = call.Line.SubVerb(0, MatchVerbs, out string? error) ?? "status";
            if (error is not null)
            {
                return ConsoleReply.Failed(error);
            }
        }

        return verb switch
        {
            "start" => Start(call),
            "drop" => Start(call, dropOnly: true),
            "lobby" => Abandon(call, ended: false),
            "end" => Abandon(call, ended: true),
            _ => Status(call),
        };
    }

    private static ConsoleReply Start(CommandCall call, bool dropOnly = false)
    {
        if (call.Tier < ConsoleTier.Owner)
        {
            return ConsoleReply.Refused($"Owner only (you are {call.Tier})");
        }

        if (!MatchGates.Allows(MatchGate.LobbyOrMenu, call.Step) || (dropOnly && call.Step != "Lobby"))
            return ConsoleReply.Refused($"not now: {call.Step} (needs {(dropOnly ? "Lobby" : "Menu or Lobby")})");

        return call.Ctx.StartMatch is { } start
            ? start()
            : ConsoleReply.Failed("not available yet -- no match flow is wired on this build");
    }

    private static ConsoleReply Abandon(CommandCall call, bool ended)
    {
        if (call.Tier < ConsoleTier.Owner)
        {
            return ConsoleReply.Refused($"Owner only (you are {call.Tier})");
        }

        if (ended && !MatchGates.Allows(MatchGate.InMatch, call.Step))
        {
            return ConsoleReply.Refused(MatchGates.Refusal(MatchGate.InMatch, call.Step));
        }

        return call.Ctx.AbandonMatch is { } abandon
            ? abandon(ended)
            : ConsoleReply.Failed("not available yet -- no match flow is wired on this build");
    }

    private static ConsoleReply Status(CommandCall call) =>
        call.Ctx.MatchStatus is { } status
            ? status()
            : ConsoleReply.Failed("no match state yet");

    private static ConsoleReply Gas(CommandCall call)
    {
        if (call.Line.Name is "pausegas" or "resumegas")
            return call.Count != 0 ? call.Usage() : call.Ctx.Gas?.Invoke(call.Line.Name == "pausegas" ? "pause" : "resume")
                ?? ConsoleReply.Failed("no gas controller yet -- it opens at the drop");
        if (call.Count > 1) return call.Usage("use one action, e.g. /gas pause");
        string verb = call.Line.SubVerb(0, GasVerbs, out string? error) ?? "status";
        if (error is not null)
        {
            return ConsoleReply.Failed(error);
        }

        return call.Ctx.Gas is { } gas
            ? gas(verb)
            : ConsoleReply.Failed("no gas controller yet -- it opens at the drop");
    }
}
