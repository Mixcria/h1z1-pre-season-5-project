using System.Globalization;

namespace Cranberry.Zone.DevConsole.Commands;

/// <summary>
/// <c>/win</c> - the server opens the client's own windows, with zero files added to the client.
/// <para>
/// This is recommendation <b>(a)</b> of R6 (<c>out\devconsole-20260901\R6-graphical-menu.md</c>
/// §4.0, §4.1, §6). R6 overturned R2 §6.1 in one specific way: there is no s2c "open this named
/// window" <i>packet</i>, but <c>Ui.ExecuteScript</c> (<c>1a 07</c>) reaches <b>238 zero-argument
/// Lua methods</b> in the client's own <c>UI\ScriptsBase.bin</c>, and those 238 include every
/// window the client has. The writer for the packet already existed
/// (<see cref="UiExecuteScript"/>); all that was missing was a curated way to name one.
/// </para>
/// <para>
/// <b>The reply is deliberately modest.</b> The invoker's failed-lookup arm
/// (<c>FUN_140ba89e0</c> <c>LAB_140ba8c1a</c>) returns 0 with <b>no print</b>, and the call is made
/// with <c>nresults = 0</c>, so a wrong name is silent and a right one tells us nothing either.
/// Every send therefore answers with what went out and how big it was, and then says plainly that
/// the client does not acknowledge. <c>/win probe</c> is the way round that: a numbered sequence
/// with a positive and a negative control, so the owner can say <i>which</i> line changed his
/// screen (R6 §5.1, §5.4).
/// </para>
/// </summary>
public static class WindowCommands
{
    /// <summary>Milliseconds between the steps of <c>/win probe</c> (R6 §5.4 is typed by hand at this pace).</summary>
    public const int ProbeSpacingMs = 1000;

    /// <summary>Adds <c>/win</c> to the registry. It joins the World group; see docs/103 §11.</summary>
    public static CommandRegistry AddTo(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register(new ConsoleCommand
        {
            Name = "win",
            Group = "world",
            Tier = ConsoleTier.Tester,

            // KeepCase, because `/win raw HudHandler.ShowInventory` must survive the parser: the
            // client's lua_getglobal / lua_getfield are case-SENSITIVE even though the command hash
            // is not. Alias lookup does its own case-insensitive compare.
            KeepCase = true,
            Usage = "/win <window>|list|probe|raw <Object.Method>",
            Summary = "open one of the client's own windows (Ui.ExecuteScript)",
            Args =
            [
                new ArgSpec("window", ArgKind.Word, Help: "an alias, or list / probe / raw"),
                new ArgSpec("name", ArgKind.Word, Help: "for raw: the Object.Method to send"),
            ],
            Examples = ["/win inventory", "/win hudoff", "/win list", "/win probe", "/win raw HudHandler.Show"],
            Detail =
            [
                "1a 07 | String8 \"Object.Method\" | u32 0 - the packet that calls the client's own Lua.",
                "A wrong name fails SILENTLY (R6 §1.2): nothing is printed and nothing is returned,",
                "so judge every send on the screen, never on the console. /win probe sends a",
                "positive and a negative control so you can tell the two apart.",
                "/win raw is Owner only and asks once before it sends.",
            ],
            MenuPaths = ["Windows > Inventory > Show", "Windows > Probe the route"],
            Run = Run,
        });

        return registry;
    }

    private static ConsoleReply Run(CommandCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        string? verb = call.Line.Word(0);
        if (verb is null)
        {
            return call.Usage($"try /win list -- {WindowScripts.All.Count} windows");
        }

        return verb.ToLowerInvariant() switch
        {
            "list" => List(call),
            "probe" => Probe(call),
            "raw" => Raw(call),
            _ => Named(call, verb),
        };
    }

    // --- /win list --------------------------------------------------------------------------

    private static ConsoleReply List(CommandCall call)
    {
        List<string> lines =
        [
            $"* /win -- {WindowScripts.All.Count} allow-listed windows; [P] proven zero-arg, [I] inferred",
        ];

        foreach (WindowScript row in WindowScripts.All)
        {
            if (row.Tier > call.Tier)
            {
                continue;
            }

            lines.Add($"  {row.Alias.PadRight(14)}{row.Script.PadRight(34)}{row.Mark} {row.Note}");
        }

        lines.Add("  probe         the R6 first-click triple, 1 s apart");
        if (call.Tier >= ConsoleTier.Owner)
        {
            lines.Add("  raw <name>    any Object.Method (asks once) -- exit/disconnect names are refused");
        }

        return ConsoleReply.Plain(lines);
    }

    // --- /win <alias> -----------------------------------------------------------------------

    private static ConsoleReply Named(CommandCall call, string alias)
    {
        WindowScript? row = WindowScripts.ByAlias(alias);
        if (row is null)
        {
            return ConsoleReply.Failed(
                $"no such window: '{alias}' -- /win list names all {WindowScripts.All.Count}");
        }

        if (row.Tier > call.Tier)
        {
            return ConsoleReply.Refused($"/win {row.Alias} is {row.Tier} only");
        }

        // Belt and braces: the allow-list is asserted clean by a test, and the runtime check means a
        // future edit that slips an exit/disconnect name into the table still cannot send it.
        if (WindowScripts.IsDenied(row.Script, out string why))
        {
            return ConsoleReply.Refused($"refused -- {why}");
        }

        return Send(call, row.Script, []);
    }

    // --- /win raw ---------------------------------------------------------------------------

    private static ConsoleReply Raw(CommandCall call)
    {
        if (call.Ctx.UiScript is null)
        {
            return ConsoleReply.Failed("not available yet -- no Ui.ExecuteScript sender is wired on this build");
        }

        if (call.Tier < ConsoleTier.Owner)
        {
            return ConsoleReply.Refused("/win raw is Owner only -- /win list names the allow-listed windows");
        }

        // RawTokens, not Tokens: the command keeps case, but read the untouched token anyway so the
        // rule holds even if KeepCase is ever turned off. Lua lookups are case-sensitive.
        string? script = call.Line.RawTokens.Count > 1 ? call.Line.RawTokens[1] : null;
        if (script is null)
        {
            return call.Usage("/win raw <Object.Method> [ints...]");
        }

        if (!WindowScripts.IsWellFormed(script, out string shape))
        {
            return ConsoleReply.Failed(shape);
        }

        if (WindowScripts.IsDenied(script, out string why))
        {
            return ConsoleReply.Refused($"refused -- {why}");
        }

        // The packet's argument list is a counted run of u32 and nothing else: FUN_1412caa00 case 7
        // pushes every element through FUN_140b7fdf0, which stamps tag 1 (integer) unconditionally,
        // so no other type can be expressed (R6 §1.4). The writer already carries them
        // (DESIGN §5.1's second vector, `UiExecuteScript("hi", 1, 0xFFFFFFFF)`).
        List<uint> ints = [];
        for (int i = 2; i < call.Line.Count; i++)
        {
            if (!call.Line.TryInt(i, out int value))
            {
                return call.Usage($"'{call.Line.Word(i)}' is not an integer -- 1a 07 carries ints only");
            }

            ints.Add(unchecked((uint)value));
        }

        string pending = Canonical(script, ints);
        if (call.Session.Settings.Confirms && call.Session.PendingWindowScript != pending)
        {
            call.Session.PendingWindowScript = pending;
            return ConsoleReply.Refused(
                $"send Ui.ExecuteScript {script}? repeat the line to confirm -- "
                + "nothing checks the name and a wrong one fails silently");
        }

        call.Session.PendingWindowScript = null;
        return Send(call, script, ints);
    }

    private static string Canonical(string script, IReadOnlyList<uint> ints) =>
        ints.Count == 0
            ? script
            : script + " " + string.Join(' ', ints.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    // --- /win probe -------------------------------------------------------------------------

    private static ConsoleReply Probe(CommandCall call)
    {
        if (call.Ctx.UiScript is null)
        {
            return ConsoleReply.Failed("not available yet -- no Ui.ExecuteScript sender is wired on this build");
        }

        IReadOnlyList<(string Script, string Why)> steps = WindowScripts.ProbeSteps;
        IConsoleSurface surface = call.Ctx.Surface;
        Func<int, Action, bool>? later = call.Ctx.Later;

        List<string> lines =
        [
            $"* /win probe -- {steps.Count} sends, {ProbeSpacingMs} ms apart (R6 §5.4).",
            "  Watch the SCREEN, not this pane: a wrong name prints nothing at all.",
            $"  {Numbered(1, steps.Count, steps[0])}",
        ];

        call.Ctx.Info($"console: /win probe step 1/{steps.Count} {steps[0].Script}");
        ConsoleReply first = Send(call, steps[0].Script, [], announce: false);
        lines.AddRange(first.Lines);

        for (int i = 1; i < steps.Count; i++)
        {
            (string script, string why) = steps[i];
            int number = i + 1;
            void Step()
            {
                surface.Line($"  {Numbered(number, steps.Count, (script, why))}");
                call.Ctx.Info($"console: /win probe step {number}/{steps.Count} {script}");
                foreach (string line in call.Ctx.UiScript!(script, []).Lines)
                {
                    surface.Line(line);
                }
            }

            if (later is null || !later(i * ProbeSpacingMs, Step))
            {
                // No listener-thread dispatcher (a unit test, or a host without Post): send it now
                // rather than lose it, and say so, because the whole point of the spacing is that
                // the owner can tell the four sends apart on his screen.
                lines.Add($"  {Numbered(number, steps.Count, (script, why))}   (sent now: no scheduler)");
                lines.AddRange(call.Ctx.UiScript(script, []).Lines);
            }
        }

        return ConsoleReply.Plain(lines);
    }

    private static string Numbered(int number, int total, (string Script, string Why) step) =>
        string.Create(CultureInfo.InvariantCulture, $"{number}/{total} {step.Script} -- {step.Why}");

    // --- the one place a 1a 07 leaves ---------------------------------------------------------

    private static ConsoleReply Send(
        CommandCall call,
        string script,
        IReadOnlyList<uint> ints,
        bool announce = true)
    {
        if (call.Ctx.UiScript is not { } send)
        {
            return ConsoleReply.Failed("not available yet -- no Ui.ExecuteScript sender is wired on this build");
        }

        if (announce)
        {
            call.Ctx.Info($"console: /win {script}"
                + (ints.Count == 0 ? string.Empty : $" [{string.Join(' ', ints)}]"));
        }

        return send(script, ints);
    }
}
