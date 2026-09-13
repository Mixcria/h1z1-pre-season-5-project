using System.Text;

namespace Cranberry.Zone.DevConsole.Menu;

/// <summary>
/// Draws one menu frame as monospace ASCII lines, on the fixed grid of design §2.7.
/// <para>
/// The console pane is <c>$MainFontMono</c> (<c>ConsoleWindow.gfx</c>, R2 §3.12), so columns hold,
/// and every glyph is plain ASCII because the font's coverage of anything else is [U]. One row is
/// one line and one line is one packet, so nothing here may ever produce an empty string: the
/// client's <c>PrintConsole</c> drops empty text and the row would silently vanish. A blank row is
/// one space, and the packet writers throw on <c>""</c> so a regression is a red test rather than a
/// hole in a frame (design §1.3).
/// </para>
/// <para>
/// <b>The grid, at W columns.</b> Column 1 is the side rule, column 3 the <c>&gt;</c> cursor,
/// columns 5-6 the row number right-aligned (digits only - letters would collide with the verbs
/// <c>d u s b q r</c>), column 8 the label, and the value column is right-aligned to column W-2
/// with one space before the closing rule.
/// </para>
/// </summary>
public static class FrameRenderer
{
    /// <summary>Draws the frame for a state. Never returns an empty line.</summary>
    public static IReadOnlyList<string> Render(MenuState state, MenuTree tree, MenuView view)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(view);

        return state.Mode switch
        {
            MenuMode.Prompt => Prompt(state, tree, view),
            MenuMode.Confirm => Confirm(state, tree, view),
            MenuMode.Open => Open(state, tree, view),
            _ => [],
        };
    }

    private static List<string> Open(MenuState state, MenuTree tree, MenuView view)
    {
        int width = Width(view);
        MenuNode node = tree.Resolve(state.Path);
        IReadOnlyList<MenuNode> rows = tree.VisibleChildren(node, view.Tier, view.ShowAllRows);
        int rowCount = rows.Count;
        int cursor = rowCount == 0 ? 0 : Math.Clamp(state.CursorOf(node.Id), 0, rowCount - 1);
        int visible = Math.Max(1, view.Settings.Rows);
        int window = rowCount <= visible ? 0 : Math.Clamp(state.WindowOf(node.Id), 0, rowCount - visible);
        if (rowCount > visible)
        {
            if (cursor < window)
            {
                window = cursor;
            }
            else if (cursor >= window + visible)
            {
                window = cursor - visible + 1;
            }
        }

        List<string> lines = [TopRule(view)];
        lines.Add(Text(view, Title(view)));
        lines.Add(Text(view, Breadcrumb(state, tree)));
        lines.Add(Blank(view));

        int last = Math.Min(rowCount, window + visible);
        for (int i = window; i < last; i++)
        {
            MenuNode row = rows[i];
            lines.Add(Row(
                view,
                cursor: i == cursor,
                key: (i + 1).ToString(),
                label: row.Label,
                value: ValueColumn(state, tree, view, row)));
        }

        if (rowCount == 0)
        {
            lines.Add(Row(view, cursor: false, key: string.Empty, label: "(nothing here for you)", value: string.Empty));
        }

        int above = window;
        int below = rowCount - last;
        if (above > 0 || below > 0)
        {
            string scroll = (above, below) switch
            {
                ( > 0, > 0) => $"^ {above} more   v {below} more",
                ( > 0, _) => $"^ {above} more",
                _ => $"v {below} more",
            };
            lines.Add(Text(view, scroll));
        }

        if (view.Settings.TypedHints)
        {
            MenuNode? highlighted = rowCount == 0 ? null : rows[cursor];
            lines.Add(Text(view, InfoLine(state, tree, view, highlighted)));
        }

        if (view.Settings.HintKeys)
        {
            lines.Add(Text(view, Hint(width)));
        }

        lines.Add(BottomRule(view));
        return lines;
    }

    private static List<string> Prompt(MenuState state, MenuTree tree, MenuView view)
    {
        MenuNode? leaf = tree.ById(state.PendingId);
        ConsoleCommand? command = leaf is null ? null : tree.CommandOf(leaf);
        if (leaf is null || command is null)
        {
            return Open(state with { Mode = MenuMode.Open }, tree, view);
        }

        List<string> lines = [TopRule(view)];
        lines.Add(Text(view, Title(view)));
        lines.Add(Text(view, Breadcrumb(state, tree) + " > " + Trim(leaf.Label)));
        lines.Add(Blank(view));
        lines.Add(Row(view, cursor: true, key: string.Empty, label: Trim(leaf.Label), value: string.Empty));

        // Arguments the row's BoundArgs already answers are not asked for and are not drawn; the
        // prompt starts at PendingStart, and PendingArgs[0] is the answer to Args[PendingStart]
        // (review-2 major).
        int start = state.PendingStart;
        for (int i = start; i < command.Args.Length; i++)
        {
            ArgSpec spec = command.Args[i];
            int answered = i - start;
            string cell = answered < state.PendingArgs.Count
                ? $"{spec.Name}?  {Show(state.PendingArgs[answered])}"
                : i == state.PendingIndex
                    ? $"{spec.Name}?  [{spec.Default ?? "?"}]  _"
                    : $"{spec.Name}?  (next)";
            lines.Add(Row(view, cursor: false, key: string.Empty, label: cell, value: string.Empty));
        }

        if (state.PendingError is not null)
        {
            lines.Add(Text(view, $"! {state.PendingError}"));
        }

        if (view.Settings.TypedHints)
        {
            string preview = PromptPreview(state, leaf, command);
            lines.Add(Text(view, $"= {preview}   /m <x> answers, /b cancels"));
        }

        lines.Add(BottomRule(view));
        return lines;
    }

    private static List<string> Confirm(MenuState state, MenuTree tree, MenuView view)
    {
        MenuNode? leaf = tree.ById(state.PendingId);
        CommandInvocation? invocation = state.PendingInvocation;
        if (leaf is null || invocation is null)
        {
            return Open(state with { Mode = MenuMode.Open }, tree, view);
        }

        ConsoleCommand? command = tree.CommandOf(leaf);

        List<string> lines = [TopRule(view)];
        lines.Add(Text(view, Title(view)));
        lines.Add(Text(view, Breadcrumb(state, tree) + " > " + Trim(leaf.Label)));
        lines.Add(Blank(view));
        lines.Add(Row(view, cursor: false, key: string.Empty, label: $"Run:  {invocation.Typed}", value: string.Empty));

        foreach (string detail in Detail(command))
        {
            lines.Add(Row(view, cursor: false, key: string.Empty, label: detail, value: string.Empty));
        }

        lines.Add(Blank(view));
        lines.Add(Row(view, cursor: true, key: string.Empty, label: "y  yes      n  no", value: string.Empty));

        if (view.Settings.TypedHints)
        {
            lines.Add(Text(view, "= /m y   (anything else cancels)"));
        }

        lines.Add(BottomRule(view));
        return lines;
    }

    private static IEnumerable<string> Detail(ConsoleCommand? command)
    {
        if (command is null)
        {
            yield break;
        }

        if (command.Detail.Length > 0)
        {
            foreach (string line in command.Detail)
            {
                yield return line;
            }

            yield break;
        }

        if (command.Summary.Length > 0)
        {
            yield return command.Summary;
        }
    }

    /// <summary>The value column of one row - the whole vocabulary of design §2.7 rule 2.</summary>
    public static string ValueColumn(MenuState state, MenuTree tree, MenuView view, MenuNode row)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(row);

        if (row.Tier > view.Tier)
        {
            return $"({row.Tier})";
        }

        if (tree.IsRefused(row))
        {
            return "(refused)";
        }

        if (tree.NotYetReason(row) is not null)
        {
            return "(n/a)";
        }

        if (!MatchGates.Allows(row.Gate, view.MatchStep))
        {
            return MatchGates.Marker(row.Gate);
        }

        string? note = view.NoteOf(row);

        switch (row.Kind)
        {
            case MenuKind.Toggle:
                return view.IsOn(row) ? "[ON]" : "[OFF]";

            case MenuKind.Value:
                return $"< {Displayed(state, view, row)} >";

            case MenuKind.Submenu:
                return string.IsNullOrEmpty(note) ? ">" : $"{note} >";

            default:
                return note ?? string.Empty;
        }
    }

    /// <summary>
    /// What a value row shows. A Settings row shows the setting as it is now; a command row shows
    /// the value the next select will run with (design §2.6, and the <c>= /surface chat</c> line of
    /// the Settings mockup, which is the next value while the row still reads <c>&lt; print &gt;</c>).
    /// </summary>
    public static string Displayed(MenuState state, MenuView view, MenuNode row)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(row);
        if (row.SettingKey is not null)
        {
            return view.ValueOf(row) ?? state.CurrentValue(row);
        }

        return state.CurrentValue(row);
    }

    /// <summary>The value the next select on this row will use.</summary>
    public static string NextValue(MenuState state, MenuView view, MenuNode row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Presets.Length == 0)
        {
            return string.Empty;
        }

        string shown = Displayed(state, view, row);
        if (row.SettingKey is null)
        {
            return shown;
        }

        int at = Array.IndexOf(row.Presets, shown);
        return at < 0 ? row.Presets[0] : row.Presets[(at + 1) % row.Presets.Length];
    }

    /// <summary>The <c>= typed form   description</c> line for the highlighted row.</summary>
    public static string InfoLine(MenuState state, MenuTree tree, MenuView view, MenuNode? row)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (row is null)
        {
            return "= /m   nothing on this page";
        }

        string typed = TypedForm(state, tree, view, row);
        string summary = row.Summary ?? tree.CommandOf(row)?.Summary ?? string.Empty;
        return summary.Length == 0 ? $"= {typed}" : $"= {typed}   {summary}";
    }

    /// <summary>What the highlighted row would be typed as - the info line's teaching half.</summary>
    public static string TypedForm(MenuState state, MenuTree tree, MenuView view, MenuNode row)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(row);

        if (row.SettingKey is not null)
        {
            string next = row.Kind == MenuKind.Value
                ? NextValue(state, view, row)
                : view.IsOn(row) ? "off" : "on";
            return (row.TypedForm ?? "%v").Replace("%v", next, StringComparison.Ordinal);
        }

        if (row.TypedForm is not null)
        {
            return row.TypedForm;
        }

        if (row.Kind == MenuKind.Submenu)
        {
            return $"/m {MenuState.Slug(row.Label)}";
        }

        ConsoleCommand? command = tree.CommandOf(row);
        string name = command?.Name ?? row.CommandName ?? "?";
        string args = row.Kind switch
        {
            MenuKind.Toggle => Join(row.BoundArgs, view.IsOn(row) ? row.OffArgs : row.OnArgs),
            MenuKind.Value => Join(row.BoundArgs, Displayed(state, view, row)),
            _ => row.BoundArgs,
        };

        return args.Length == 0 ? $"/{name}" : $"/{name} {args}";
    }

    private static string PromptPreview(MenuState state, MenuNode leaf, ConsoleCommand command)
    {
        List<string> parts = [];
        if (leaf.BoundArgs.Length > 0)
        {
            parts.Add(leaf.BoundArgs);
        }

        int start = state.PendingStart;
        for (int i = start; i < command.Args.Length; i++)
        {
            int answered = i - start;
            string value = answered < state.PendingArgs.Count
                ? state.PendingArgs[answered]
                : command.Args[i].Default ?? $"<{command.Args[i].Name}>";
            if (value.Length > 0)
            {
                parts.Add(value);
            }
        }

        return parts.Count == 0 ? $"/{command.Name}" : $"/{command.Name} {string.Join(' ', parts)}";
    }

    /// <summary>The frame's title line: who this is, which server build and which client build.</summary>
    public static string Title(MenuView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return $"CRANBERRY CONSOLE  v{view.ServerVersion}  |  {view.ClientBuild}";
    }

    /// <summary>
    /// The breadcrumb, truncated from the left when it is longer than the frame. At the root it is
    /// the Jiggy "By" line, which is where the menu says whose menu it is.
    /// </summary>
    public static string Breadcrumb(MenuState state, MenuTree tree)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(tree);
        if (state.Path.IsEmpty)
        {
            return "By Cranberry  --  root";
        }

        return "root > " + string.Join(" > ", tree.LabelPath(state.Path).Select(Trim));
    }

    private static string Hint(int width) =>
        width >= 46
            ? "/d /u move  /s pick  /b back  /q quit  /m 3"
            : "/d /u  /s pick  /b back  /q quit";

    private static int Width(MenuView view) =>
        Math.Clamp(view.Settings.Width, ConsoleOptions.MinimumWidth, ConsoleOptions.MaximumWidth);

    private static char Side(MenuView view) => view.Settings.Theme == MenuTheme.Heavy ? '#' : '|';

    private static char Corner(MenuView view) => view.Settings.Theme == MenuTheme.Heavy ? '#' : '+';

    private static string TopRule(MenuView view)
    {
        int width = Width(view);
        return Corner(view) + new string('=', width - 2) + Corner(view);
    }

    private static string BottomRule(MenuView view)
    {
        int width = Width(view);
        char fill = view.Settings.Theme == MenuTheme.Heavy ? '=' : '-';
        return Corner(view) + new string(fill, width - 2) + Corner(view);
    }

    private static string Blank(MenuView view)
    {
        int width = Width(view);
        char side = Side(view);
        return side + new string(' ', width - 2) + side;
    }

    /// <summary>A full-width text line inside the frame: one leading space, then the text.</summary>
    private static string Text(MenuView view, string text)
    {
        int width = Width(view);
        char side = Side(view);
        int body = width - 2;
        string content = " " + text;
        if (content.Length > body)
        {
            content = content[..body];
        }

        return side + content.PadRight(body) + side;
    }

    /// <summary>One grid row: cursor, two-character key, label, right-aligned value.</summary>
    private static string Row(MenuView view, bool cursor, string key, string label, string value)
    {
        int width = Width(view);
        char side = Side(view);
        int inner = width - 3;

        char[] cells = new char[inner];
        Array.Fill(cells, ' ');

        cells[1] = cursor ? '>' : ' ';
        string keyCell = (key.Length > 2 ? key[^2..] : key).PadLeft(2);
        cells[3] = keyCell[0];
        cells[4] = keyCell[1];

        int valueStart = inner;
        if (value.Length > 0)
        {
            valueStart = Math.Max(6, inner - value.Length);
            for (int i = 0; i < value.Length && valueStart + i < inner; i++)
            {
                cells[valueStart + i] = value[i];
            }
        }

        int room = Math.Max(0, valueStart - 6 - (value.Length > 0 ? 1 : 0));
        string text = Truncate(label, room);
        for (int i = 0; i < text.Length; i++)
        {
            cells[6 + i] = text[i];
        }

        StringBuilder line = new(width);
        line.Append(side);
        line.Append(cells);
        line.Append(' ');
        line.Append(side);
        return line.ToString();
    }

    /// <summary>Cuts a label to fit, ASCII only - <c>..</c> rather than an ellipsis glyph.</summary>
    public static string Truncate(string text, int room)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (room <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= room)
        {
            return text;
        }

        return room <= 2 ? text[..room] : text[..(room - 2)] + "..";
    }

    private static string Trim(string label) =>
        label.EndsWith("...", StringComparison.Ordinal) ? label[..^3] : label;

    private static string Show(string answer) => answer.Length == 0 ? "(default)" : answer;

    private static string Join(string first, string second)
    {
        if (first.Length == 0)
        {
            return second;
        }

        return second.Length == 0 ? first : $"{first} {second}";
    }
}
