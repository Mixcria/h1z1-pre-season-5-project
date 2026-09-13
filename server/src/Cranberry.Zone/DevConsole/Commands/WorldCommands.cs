namespace Cranberry.Zone.DevConsole.Commands;

/// <summary>
/// The World group: doors near you and the practice targets.
/// <para>
/// Both are thin adapters over things the match already does on its own. <c>/doors</c> walks
/// <c>MatchDoors.Instances</c>, filters by distance and calls <c>TryToggle</c> then sends
/// <c>door.StateUpdate()</c> exactly as the real interact path does
/// (<c>TryToggleDoor :3936-3972</c>, R3 #25); <c>/target</c> clears
/// <c>Combat.TargetsArmed</c> and re-runs <c>ArmPracticeTargets</c> (R3 #21).
/// </para>
/// <para>
/// Time of day, sky and weather are deliberately <b>absent</b> rather than "later": the owner's
/// ruling is a fixed ~2 pm sky and no weather at all (D98/D99), so there is no row to grey.
/// </para>
/// </summary>
public static class WorldCommands
{
    /// <summary>The door sub-verbs, in the order an ambiguous prefix lists them.</summary>
    public static IReadOnlyList<string> DoorVerbs { get; } = ["open", "close", "toggle"];

    /// <summary>The practice-target sub-verbs.</summary>
    public static IReadOnlyList<string> TargetVerbs { get; } = ["spawn", "clear", "status"];

    /// <summary>Metres a bare <c>/doors open</c> reaches.</summary>
    public const float DefaultDoorRadius = 60f;

    /// <summary>Adds the World group to the registry.</summary>
    public static CommandRegistry AddTo(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register(new ConsoleCommand
        {
            Name = "doors",
            Group = "world",
            Tier = ConsoleTier.Tester,
            Gate = MatchGate.InMatch,
            Usage = "/doors open|close|toggle [m=60]",
            Summary = "the doors near you",
            Args =
            [
                new ArgSpec("verb", ArgKind.Enum, "toggle", "which action", [.. DoorVerbs]),
                new ArgSpec("m", ArgKind.Float, "60", "radius in metres"),
            ],
            Examples = ["/doors open", "/doors close 20", "/doors toggle"],
            Detail = ["toggle flips the single nearest door; open and close sweep the radius."],
            MenuPaths = ["World > Open doors near", "World > Toggle nearest door"],
            Run = Doors,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "target",
            Group = "world",
            Tier = ConsoleTier.Tester,
            Gate = MatchGate.InMatch,
            Usage = "/target [spawn [rank]|clear|status]",
            Summary = "the practice dummies",
            Args = [new ArgSpec("verb", ArgKind.Enum, "status", "which action", [.. TargetVerbs])],
            Detail = ["spawn creates a full-kit player dummy; optional rank previews its retail badge. Kills award points.", "clear removes practice targets."],
            MenuPaths = ["World > Practice target"],
            Run = Target,
        });

        return registry;
    }

    private static ConsoleReply Doors(CommandCall call)
    {
        if (call.Ctx.Doors is not { } doors)
        {
            return ConsoleReply.Failed("no doors streamed -- they arrive with the landing burst");
        }

        string verb = call.Line.SubVerb(0, DoorVerbs, out string? error) ?? "toggle";
        if (error is not null)
        {
            return ConsoleReply.Failed(error);
        }

        float radius = DefaultDoorRadius;
        if (call.Count > 2 || (call.Count == 2 && !call.Line.TryFloat(1, out radius)) || radius is <= 0f or > 1_000f)
        {
            return call.Usage($"'{call.Line.Word(1)}' is not a radius in metres");
        }

        return doors(verb, radius);
    }

    private static ConsoleReply Target(CommandCall call)
    {
        if (call.Ctx.PracticeTarget is not { } target)
        {
            return ConsoleReply.Failed("not available yet -- practice targets are not wired on this build");
        }

        string verb = call.Line.SubVerb(0, TargetVerbs, out string? error) ?? "status";
        if (error is not null) return ConsoleReply.Failed(error);
        if (call.Count > 2 || (call.Count > 1 && verb != "spawn")) return call.Usage();
        return target(call.Count == 2 ? verb + " " + call.Line.Word(1) : verb);
    }
}
