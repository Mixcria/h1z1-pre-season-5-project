namespace Cranberry.Zone.DevConsole;

/// <summary>
/// <c>/help</c> - the typed front door reading the same registry the menu reads, which is what
/// keeps the two doors from drifting apart (design §2.4, R4 §6.2).
/// <para>
/// <b>Why it is also the unknown-name page.</b> The client turns an unregistered <c>/name</c> into
/// <c>hash("HELP")</c> with empty arguments (measured on the owner's 1087 server, R1 §1.3.3), so
/// the server literally cannot tell a typed <c>/help</c> from a typo. Page one therefore says so in
/// its first line rather than pretending the owner asked for help.
/// </para>
/// <para>
/// <c>help</c> itself is not pushed with <c>AddWorldCommand</c>; <c>commands</c> is the registered
/// synonym, so the pages are still reachable if the 1148 client turns out not to forward
/// <c>/help</c> at all (design §1.1, the open [U]).
/// </para>
/// </summary>
public static class HelpFormatter
{
    /// <summary>The most lines one page may hold (design §2.4).</summary>
    public const int PageSize = 12;

    /// <summary>The first line of page one when the request may have been a typo.</summary>
    public const string CatchAllNote = "* (an unknown /name shows this page too)";

    /// <summary>Renders whichever page the arguments ask for.</summary>
    /// <param name="registry">The command registry - the single source for both doors.</param>
    /// <param name="line">The typed line; its first token selects the page.</param>
    /// <param name="unknownName">
    /// True when this call arrived through the client's HELP catch-all with empty arguments, so the
    /// page opens with <see cref="CatchAllNote"/>.
    /// </param>
    public static IReadOnlyList<string> Render(CommandRegistry registry, CommandLine line, bool unknownName = false)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(line);

        string? topic = line.Word(0);
        if (topic is "quick" or "examples") return QuickStart;

        if (string.Equals(topic, "native", StringComparison.OrdinalIgnoreCase))
            return NativeCommandHelp.Render(line.Word(1));

        if (topic is null)
        {
            List<string> page = [.. Overview(registry)];
            if (unknownName)
            {
                page.Insert(0, CatchAllNote);
            }

            return page;
        }

        if (int.TryParse(topic, out int pageNumber) && pageNumber > 0)
        {
            return Flat(registry, pageNumber);
        }

        ConsoleCommand? command = registry.ByName(topic);
        int detailPage = 1;
        if (line.Count > 1 && (!line.TryInt(1, out detailPage) || detailPage < 1))
            return ["? /commands <command or group> [page=1]"];
        if (command is not null)
        {
            return Command(registry, command, detailPage);
        }

        IReadOnlyList<IGrouping<string, ConsoleCommand>> groups = registry.ByGroup();
        IGrouping<string, ConsoleCommand>? group =
            groups.FirstOrDefault(g => string.Equals(g.Key, topic, StringComparison.OrdinalIgnoreCase))
            ?? groups.FirstOrDefault(g => g.Key.StartsWith(topic, StringComparison.OrdinalIgnoreCase));

        if (group is not null)
        {
            return Group(group, detailPage);
        }

        return
        [
            $"- no command or group called '{topic}'",
            "? /help   /help <group>   /help <command>   /help 2",
        ];
    }

    /// <summary>Page one: the header, one line per group, and the not-yet footnote.</summary>
    public static IReadOnlyList<string> Overview(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        int later = registry.NotYetCount;
        int now = registry.Commands.Count - later;

        List<string> lines =
        [
            $"Cranberry console -- {now} commands now, {later} later.  "
            + (registry.ByName("m") is not null
                ? "/help <group>  /help <command>  /m = menu; /commands native = original August commands"
                : "/commands quick = useful examples; /commands <name> = details; /commands 2 = next page"),
        ];

        foreach (IGrouping<string, ConsoleCommand> group in registry.ByGroup())
        {
            string names = string.Join(' ', group.Select(Marked));
            lines.Add($"{group.Key.PadRight(8)} {names}");
        }

        if (later > 0)
        {
            lines.Add("(* = not available yet: answers \"not available yet -- <reason>\")");
        }

        return Cap(lines);
    }

    /// <summary>One group's commands, usage and summary, one line each.</summary>
    public static IReadOnlyList<string> Group(IGrouping<string, ConsoleCommand> group, int page = 1)
    {
        ArgumentNullException.ThrowIfNull(group);
        List<string> lines = [$"{group.Key} --"];
        foreach (ConsoleCommand command in group)
        {
            lines.Add($"  {command.Usage}   {command.Summary}");
        }

        return DetailPage(lines, page, group.Key);
    }

    /// <summary>Everything about one command, in the shape design §2.4 draws.</summary>
    public static IReadOnlyList<string> Command(CommandRegistry registry, ConsoleCommand command, int page = 1)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(command);

        List<string> lines = [$"{command.Usage}    ({command.Tier}, {GateWord(command.Gate)})"];

        if (command.NotYet is not null)
        {
            lines.Add($"  NOT AVAILABLE YET -- {command.NotYet}");
        }

        lines.Add($"  {command.Summary}");

        foreach (string detail in command.Detail)
        {
            lines.Add($"  {detail}");
        }

        if (command.Aliases.Length > 0)
        {
            lines.Add($"  also: {string.Join(' ', command.Aliases.Select(a => "/" + a))}");
        }

        foreach (ArgSpec spec in command.Args)
        {
            string bracket = spec.Required ? $"<{spec.Name}>" : $"[{spec.Name}={spec.Default}]";
            lines.Add($"  {bracket}  {spec.Help}".TrimEnd());
        }

        if (command.Examples.Length > 0)
        {
            lines.Add($"  e.g. {string.Join("   ", command.Examples)}");
        }

        if (command.MenuPaths.Length > 0)
        {
            lines.Add($"  menu: {string.Join("   ", command.MenuPaths)}");
        }

        if (registry.IsRefused(command))
        {
            lines.Add("  the client REFUSED this name last run -- rename it (docs/97 §7)");
        }

        return DetailPage(lines, page, command.Name);
    }

    public static IReadOnlyList<string> QuickStart { get; } =
    [
        "* Useful console commands (most changes need you to be in a match)",
        "  /startmatch - begin a match; use again in pregame to drop now",
        "  /gas pause | /gas resume | /gas status - shared gas controls",
        "  /bots 10 - add ten moving, armed bots everyone in this match can fight",
        "  /bots freeze | /bots resume | /bots clear - control shared bots",
        "  /tp pv | /tp cranberry | /tp ranchito | /places - map destinations",
        "  /tp back | /tp save my spot | /tp my spot - return and bookmarks",
        "  /godmode on | /heal | /kit pvp | /ammo 100 - combat supplies",
        "  /target spawn | /target clear - local stationary practice dummies",
        "  /car police | /enter | /exit | /fuel - vehicles",
        "  /commands bots | /commands gas | /commands tp - full syntax",
        "  /commands native - original client commands; details: /commands native vehicle",
    ];

    private static IReadOnlyList<string> DetailPage(List<string> lines, int page, string topic)
    {
        if (lines.Count <= PageSize && page == 1) return lines;
        const int rows = PageSize - 2;
        int pages = Math.Max(1, (lines.Count + rows - 1) / rows);
        if (page > pages) return [$"? only {pages} page(s); /commands {topic} {pages}"];
        List<string> output = [$"* {topic} - page {page}/{pages}"];
        output.AddRange(lines.Skip((page - 1) * rows).Take(rows));
        if (page < pages) output.Add($"Next: /commands {topic} {page + 1}");
        return output;
    }

    /// <summary>The flat list, paged - the owner's <c>/help 2</c> habit.</summary>
    public static IReadOnlyList<string> Flat(CommandRegistry registry, int page)
    {
        ArgumentNullException.ThrowIfNull(registry);
        List<string> rows =
        [
            .. registry.Commands.Select(c => $"{("/" + c.Name).PadRight(14)}{c.Usage}"),
        ];

        int perPage = PageSize - 1;
        int pages = Math.Max(1, (rows.Count + perPage - 1) / perPage);
        int wanted = Math.Clamp(page, 1, pages);
        List<string> lines = [$"Cranberry console -- page {wanted} of {pages}"];
        lines.AddRange(rows.Skip((wanted - 1) * perPage).Take(perPage));
        return lines;
    }

    private static string Marked(ConsoleCommand command) =>
        command.NotYet is null ? command.Name : command.Name + "*";

    private static string GateWord(MatchGate gate) => gate switch
    {
        MatchGate.MenuOnly => "Menu",
        MatchGate.InMatch => "InMatch",
        MatchGate.LobbyOrMenu => "Menu or Lobby",
        _ => "any step",
    };

    private static IReadOnlyList<string> Cap(List<string> lines) =>
        lines.Count <= PageSize ? lines : [.. lines.Take(PageSize - 1), $"... {lines.Count - PageSize + 1} more"];
}
