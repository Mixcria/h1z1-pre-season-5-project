using System.Numerics;

namespace Cranberry.Zone.DevConsole.Commands;

/// <summary>
/// The Player group: where you are, where you go, and how much health you have.
/// <para>
/// Every body here is a thin adapter. It parses the tail, applies the owner's own defaults (2,500
/// for a bare <c>/hurt</c>, 50 m for a bare <c>/up</c> - R1 §2.6, D176), and calls one delegate on
/// <see cref="ConsoleContext"/>. Nothing in this file knows what a packet is: the delegate is bound
/// in <c>ZoneService.Console.cs</c> and answers with the line the world can honestly report
/// (design §4.6).
/// </para>
/// </summary>
public static class PlayerCommands
{
    /// <summary>The owner's own default for a bare <c>/hurt</c> (his Z1 server's number, D176).</summary>
    public const uint DefaultHurt = 2_500;

    /// <summary>Metres a bare <c>/up</c> lifts you.</summary>
    public const float DefaultHover = 50f;

    /// <summary>Adds every Player-group command to the registry, in <c>/help</c> order.</summary>
    public static CommandRegistry AddTo(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register(new ConsoleCommand
        {
            Name = "where",
            Aliases = ["location"],
            Group = "player",
            Tier = ConsoleTier.Player,
            Usage = "/where",
            Summary = "your position as a pasteable /tp line",
            Detail = ["Answers the line you would type to come back here."],
            MenuPaths = ["Player > Where am I", "Debug > Where am I"],
            Run = Where,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "tp",
            Aliases = ["teleport"],
            Group = "player",
            Tier = ConsoleTier.Tester,
            Usage = "/tp <x> <y> <z> | <place> | back",
            Summary = "move yourself",
            Args =
            [
                new ArgSpec("x", ArgKind.Float, Help: "world x, or ~ to keep it"),
                new ArgSpec("y", ArgKind.Float, Help: "world y, or ~"),
                new ArgSpec("z", ArgKind.Float, Help: "world z, or ~"),
            ],
            Examples = ["/tp pv", "/tp cranberry", "/tp back", "/tp save shooting range"],
            Detail =
            [
                "Use /places [search] [page] for map destinations; spaces and short names work.",
                "~ keeps your own value on that axis and ~5 offsets it.",
                "/tp save <name>, /tp saved, /tp delete <name>: bookmarks until disconnect.",
            ],
            MenuPaths = ["Player > Teleport"],
            Run = Teleport,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "up",
            Group = "player",
            Tier = ConsoleTier.Tester,
            Gate = MatchGate.InMatch,
            Usage = "/up [m=50]",
            Summary = "hover straight up",
            Args = [new ArgSpec("m", ArgKind.Float, "50", "metres")],
            MenuPaths = ["Player > Hover +50 m"],
            Run = Up,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "chute", Group = "player", Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch,
            Usage = "/chute [height=700]", Summary = "lift yourself and deploy a parachute",
            Run = c => PositiveFloat(c, 700f, 50f, 1500f, c.Ctx.Parachute),
        });

        registry.Register(new ConsoleCommand
        {
            Name = "heal",
            Group = "player",
            Tier = ConsoleTier.Tester,
            Gate = MatchGate.InMatch,
            Usage = "/heal [hp]",
            Summary = "restore health (full when no number is given)",
            Args = [new ArgSpec("hp", ArgKind.Int, "full", "health units")],
            MenuPaths = ["Player > Heal"],
            Run = Heal,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "hurt",
            Group = "player",
            Tier = ConsoleTier.Tester,
            Gate = MatchGate.InMatch,
            Usage = "/hurt [hp=2500]",
            Summary = "damage yourself through the real damage path",
            Args = [new ArgSpec("hp", ArgKind.Int, "2500", "health units")],
            Detail = ["Goes through ApplyGasDamage, so god mode and the death path both apply."],
            MenuPaths = ["Player > Hurt"],
            Run = Hurt,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "kill",
            Group = "player",
            Tier = ConsoleTier.Tester,
            Gate = MatchGate.InMatch,
            Usage = "/kill",
            Summary = "die, through the real death path",
            Confirm = true,
            MenuPaths = ["Player > Kill me"],
            Run = Kill,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "godmode",
            Group = "player",
            Tier = ConsoleTier.Tester,
            Usage = "/godmode [on|off]",
            Summary = "ignore all damage to you",
            Args = [new ArgSpec("state", ArgKind.Enum, "toggle", "on or off", ["on", "off"])],
            Detail =
            [
                "Prevents ordinary damage. /kill remains an explicit death test.",
            ],
            MenuPaths = ["Player > God mode"],
            Run = GodMode,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "gm", Group = "player", Tier = ConsoleTier.Tester,
            Usage = "/gm invuln [on|off]",
            Summary = "server endpoint for the original /god alias",
            Detail = ["The August client expands /god to gm invuln (FUN_141277a50)."],
            Run = c => c.Line.Word(0) == "invuln"
                ? GodMode(c with { Line = CommandLine.Parse("gm", c.Line.Text(1)) })
                : c.Usage("supported subcommand: invuln"),
        });

        registry.Register(new ConsoleCommand
        {
            Name = "speed", Group = "player", Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch,
            Usage = "/speed [multiplier=1]", Summary = "set your movement multiplier (1 restores normal)",
            Run = c => PositiveFloat(c, 1f, 0.1f, 10f, c.Ctx.Speed),
        });
        registry.Register(new ConsoleCommand
        {
            Name = "places", Group = "player", Tier = ConsoleTier.Player,
            Usage = "/places [search] [page]", Summary = "search named map destinations, e.g. /places ranchito",
            Examples = ["/places", "/places 2", "/places pleasant"],
            Run = Places,
        });

        return registry;
    }

    private static ConsoleReply PositiveFloat(CommandCall call, float fallback, float min, float max,
        Func<float, ConsoleReply>? action)
    {
        float value = fallback;
        if (call.Count > 1 || (call.Count == 1 && !call.Line.TryFloat(0, out value))
            || !float.IsFinite(value) || value < min || value > max)
            return call.Usage($"value must be between {min} and {max}");
        return action?.Invoke(value) ?? ConsoleReply.Failed("player is unavailable");
    }

    private static ConsoleReply Where(CommandCall call)
    {
        if (call.Ctx.Position?.Invoke() is not Vector4 position)
        {
            return ConsoleReply.Failed("no position yet -- move once so a channel-2 record arrives");
        }

        float? yaw = call.Ctx.Yaw?.Invoke();
        string heading = yaw is null ? string.Empty : $", yaw {yaw.Value:0}";
        return ConsoleReply.Did($"/tp {ConsoleFormat.Position(position)}   ({call.Step}{heading})");
    }

    private static ConsoleReply Teleport(CommandCall call)
    {
        if (call.Ctx.Teleport is not { } teleport)
        {
            return ConsoleReply.Failed("not available yet -- no teleport is wired on this build");
        }

        if (call.Line.Word(0) is "save" or "delete" or "saved") return Bookmark(call);
        Vector4? here = call.Ctx.Position?.Invoke();
        Vector3 origin = here is { } current ? ConsoleFormat.Xyz(current) : Vector3.Zero;

        Vector3 target;
        switch (call.Count)
        {
            case 0:
                return call.Usage("give three numbers, a place, or 'back'");

            case 1 when call.Line.Word(0) == "back":
                if (call.Session.LastPosition is not Vector3 previous)
                {
                    return ConsoleReply.Failed("no previous position -- /tp somewhere first");
                }

                target = previous;
                break;

            case var _ when !LooksLikeCoordinate(call.Line.Word(0)!):
                string place = string.Join(' ', call.Line.Tokens);
                if (call.Session.SavedPlaces.TryGetValue(place, out var saved))
                {
                    if (call.Step != "InMatch") return ConsoleReply.Refused("saved map locations need InMatch; /startmatch first");
                    target = saved;
                    break;
                }
                if (call.Ctx.TeleportPlace is { } named)
                    return named(place);
                if (call.Ctx.Place?.Invoke(place) is not Vector3 resolved)
                {
                    return ConsoleReply.Failed(
                        $"unknown place '{place}'; /places {place} searches the map");
                }

                target = resolved;
                break;

            default:
                if (call.Count != 3 || !call.Line.TryVector3(0, origin, out target)
                    || here is null && call.Line.Tokens.Any(t => t.StartsWith('~')))
                {
                    return call.Usage($"'{call.Line.Raw}' is not three numbers");
                }

                break;
        }

        var reply = teleport(target);
        if (reply.Ok && here is { } from)
        {
            call.Session.LastPosition = ConsoleFormat.Xyz(from);
        }

        return reply;
    }

    private static bool LooksLikeCoordinate(string word) => word.StartsWith('~')
        || word.Length > 0 && (char.IsDigit(word[0]) || word[0] is '-' or '+' or '.')
        || word.Equals("nan", StringComparison.OrdinalIgnoreCase)
        || word.Contains("infinity", StringComparison.OrdinalIgnoreCase);

    private static ConsoleReply Bookmark(CommandCall call)
    {
        string verb = call.Line.Word(0)!;
        if (verb == "saved")
            return call.Count != 1 ? call.Usage() : ConsoleReply.Plain(call.Session.SavedPlaces.Count == 0
                ? ["* no saved locations; /tp save <name> saves this spot until disconnect"]
                : call.Session.SavedPlaces.OrderBy(p => p.Key).Select(p => $"* /tp {p.Key} - {p.Value}"));
        string name = string.Join(' ', call.Line.Tokens.Skip(1));
        if (name.Length is < 1 or > 40 || !char.IsLetter(name[0])
            || name.Any(ch => !char.IsLetterOrDigit(ch) && ch is not (' ' or '-' or '_')))
            return call.Usage("bookmark names: 1-40 letters/numbers/spaces, beginning with a letter");
        if (name is "back" or "save" or "saved" or "delete" or "drop" or "staging" or "spawn"
            || call.Ctx.Place?.Invoke(name) is not null)
            return ConsoleReply.Failed("that name belongs to a built-in destination; choose another name");
        if (verb == "delete") return call.Session.SavedPlaces.Remove(name)
            ? ConsoleReply.Did($"deleted bookmark '{name}'") : ConsoleReply.Failed($"no bookmark '{name}'; /tp saved lists yours");
        if (call.Step != "InMatch" || call.Ctx.Position?.Invoke() is not Vector4 position)
            return ConsoleReply.Failed("enter a match and land before saving a map location");
        if (call.Session.SavedPlaces.Count >= 50 && !call.Session.SavedPlaces.ContainsKey(name))
            return ConsoleReply.Failed("50 bookmarks saved; /tp delete <name> frees a slot");
        call.Session.SavedPlaces[name] = ConsoleFormat.Xyz(position);
        return ConsoleReply.Did($"saved '{name}'; /tp {name} returns here (kept until disconnect)");
    }

    private static ConsoleReply Places(CommandCall call)
    {
        int page = 1, words = call.Count;
        if (words > 0 && call.Line.TryInt(words - 1, out int requested))
        {
            if (requested < 1) return call.Usage("page must be 1 or greater");
            page = requested;
            words--;
        }
        string search = string.Join(' ', call.Line.Tokens.Take(words));
        return call.Ctx.FindPlaces?.Invoke(search, page) ?? call.Ctx.Places?.Invoke()
            ?? ConsoleReply.Failed("place table unavailable");
    }

    private static ConsoleReply Up(CommandCall call)
    {
        if (call.Ctx.Teleport is not { } teleport)
        {
            return ConsoleReply.Failed("not available yet -- no teleport is wired on this build");
        }

        if (call.Ctx.Position?.Invoke() is not Vector4 position)
        {
            return ConsoleReply.Failed("no position yet -- move once so a channel-2 record arrives");
        }

        float metres = DefaultHover;
        if (call.Count > 1 || (call.Count == 1 && !call.Line.TryFloat(0, out metres)) || !float.IsFinite(metres) || Math.Abs(metres) > 1500f)
            return call.Usage("height must be a finite number between -1500 and 1500");
        var reply = teleport(new Vector3(position.X, position.Y + metres, position.Z));
        if (reply.Ok) call.Session.LastPosition = ConsoleFormat.Xyz(position);
        return reply;
    }

    private static ConsoleReply Heal(CommandCall call)
    {
        if (call.Ctx.Heal is not { } heal)
        {
            return ConsoleReply.Failed("not available yet -- no health path is wired on this build");
        }

        if (call.Count == 0)
        {
            return heal(null);
        }

        if (!call.Line.TryInt(0, out int hp) || hp < 0)
        {
            return call.Usage($"'{call.Line.Word(0)}' is not a health number");
        }

        return heal((uint)hp);
    }

    private static ConsoleReply Hurt(CommandCall call)
    {
        if (call.Ctx.Hurt is not { } hurt)
        {
            return ConsoleReply.Failed("not available yet -- no damage path is wired on this build");
        }

        int amount = (int)DefaultHurt;
        if (call.Count > 1 || (call.Count == 1 && !call.Line.TryInt(0, out amount)) || amount <= 0)
        {
            return call.Usage("the amount must be above zero");
        }

        return hurt((uint)amount);
    }

    private static ConsoleReply Kill(CommandCall call) =>
        call.Ctx.Kill is { } kill
            ? kill()
            : ConsoleReply.Failed("not available yet -- no death path is wired on this build");

    private static ConsoleReply GodMode(CommandCall call)
    {
        // The engine owns the flag: it lives on ConsoleSession, the menu's toggle row reads it
        // through the renderer, and the one guard line at the top of ApplyGasDamage is the only
        // other reader (design §4.6 #5). Nothing has to travel to the world for this row.
        string? state = call.FromMenu ? call.Line.Word(call.Count - 1) : call.Line.Word(0);
        if ((!call.FromMenu && call.Count > 1) || (state is not (null or "on" or "off" or "toggle")))
            return call.Usage("use on or off");
        call.Session.Invulnerable = state switch
        {
            "on" => true,
            "off" => false,
            _ => !call.Session.Invulnerable,
        };

        return ConsoleReply.Did($"God mode [{(call.Session.Invulnerable ? "ON" : "OFF")}]");
    }
}
