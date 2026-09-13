using System.Collections.Immutable;

namespace Cranberry.Zone.DevConsole.Menu;

/// <summary>Where the menu is (design §2.8).</summary>
public enum MenuMode : byte
{
    /// <summary>Nothing drawn. Cursors are still remembered.</summary>
    Closed = 0,

    /// <summary>A frame of rows, with a cursor.</summary>
    Open = 1,

    /// <summary>Collecting one argument at a time for a leaf.</summary>
    Prompt = 2,

    /// <summary>Showing the exact line that will run, waiting for <c>/m y</c>.</summary>
    Confirm = 3,
}

/// <summary>The line a menu step wants run, exactly as if the owner had typed it.</summary>
/// <param name="Name">The command name, without the slash.</param>
/// <param name="Arguments">The argument tail.</param>
/// <param name="NodeId">The leaf that produced it, for the log line.</param>
public sealed record CommandInvocation(string Name, string Arguments, string NodeId)
{
    /// <summary>The line as it would be typed: <c>/give ar15 1</c>.</summary>
    public string Typed => Arguments.Length == 0 ? $"/{Name}" : $"/{Name} {Arguments}";
}

/// <summary>One of the menu's own Settings, changed by a Settings row.</summary>
/// <param name="Key">The <see cref="MenuNode.SettingKey"/>.</param>
/// <param name="Value">The new value, as text; for a toggle, <c>on</c> or <c>off</c>.</param>
public sealed record SettingChange(string Key, string Value);

/// <summary>
/// The result of one menu step: the new state, whether the frame should be drawn again, one result
/// line above it, and at most one thing to actually do.
/// <para>
/// <b>The frame is not rendered here.</b> A toggle's row reads the world it is about to change, so
/// the redraw has to happen after the invocation has run - otherwise <c>/s</c> on God mode would
/// answer <c>+ God mode [ON]</c> over a frame still saying <c>[OFF]</c>.
/// <see cref="ConsoleEngine"/> renders, which also keeps the identical-frame suppression in one
/// place.
/// </para>
/// </summary>
/// <param name="State">The state after the step.</param>
/// <param name="Redraw">True when the caller should render and send a frame.</param>
/// <param name="ResultLine">One line above the frame, already carrying its glyph, or null.</param>
/// <param name="Invocation">The command to run, or null.</param>
/// <param name="Setting">The Settings change to apply, or null.</param>
public sealed record MenuStep(
    MenuState State,
    bool Redraw = false,
    string? ResultLine = null,
    CommandInvocation? Invocation = null,
    SettingChange? Setting = null);

/// <summary>
/// The per-player menu state machine. Pure and total: every <see cref="MenuInput"/> in every
/// <see cref="MenuMode"/> yields a new state and at most one frame, one result line and one thing
/// to run (design §2.8, §4.4).
/// <para>
/// Nothing here knows about packets, sessions or the server. That is deliberate: the state machine
/// is the part with the most behaviour and the least evidence behind it, so it is the part that has
/// to be testable without a client.
/// </para>
/// </summary>
public sealed record MenuState
{
    /// <summary>How many rows <c>dd</c> and <c>uu</c> move.</summary>
    public const int FastStep = 5;

    /// <summary>Where the menu is.</summary>
    public MenuMode Mode { get; init; } = MenuMode.Closed;

    /// <summary>The node ids from the root down, root itself excluded.</summary>
    public ImmutableList<string> Path { get; init; } = [];

    /// <summary>The remembered cursor row of each node, by node id.</summary>
    public ImmutableDictionary<string, int> Cursors { get; init; } = ImmutableDictionary<string, int>.Empty;

    /// <summary>The remembered window top of each node, by node id.</summary>
    public ImmutableDictionary<string, int> Windows { get; init; } = ImmutableDictionary<string, int>.Empty;

    /// <summary>Where each value row's cycle has got to, by node id.</summary>
    public ImmutableDictionary<string, int> Cycles { get; init; } = ImmutableDictionary<string, int>.Empty;

    /// <summary>The leaf a prompt or a confirm is about.</summary>
    public string? PendingId { get; init; }

    /// <summary>The arguments a prompt has collected so far.</summary>
    public ImmutableList<string> PendingArgs { get; init; } = [];

    /// <summary>Which declared argument the prompt is asking for.</summary>
    public int PendingIndex { get; init; }

    /// <summary>
    /// The first declared argument this prompt is collecting. A row whose <c>BoundArgs</c> already
    /// answers the leading arguments (Loot &gt; Spawn binds <c>verb</c> to "spawn") starts past
    /// them, so <c>PendingArgs[i]</c> is the answer to <c>Args[PendingStart + i]</c>.
    /// </summary>
    public int PendingStart => Math.Max(0, PendingIndex - PendingArgs.Count);

    /// <summary>The line the confirm will run.</summary>
    public CommandInvocation? PendingInvocation { get; init; }

    /// <summary>Why the last prompt answer was refused; shown on the info line.</summary>
    public string? PendingError { get; init; }

    /// <summary>A live-value frame wants redrawing. Designed in, unused in v1 (design §2.8).</summary>
    public bool Stale { get; init; }

    /// <summary>The hash of the last frame sent, for identical-frame suppression.</summary>
    public int LastFrameHash { get; init; }

    /// <summary>A closed menu with nothing remembered.</summary>
    public static MenuState Closed { get; } = new();

    /// <summary>True when a frame is being drawn at all.</summary>
    public bool IsOpen => Mode != MenuMode.Closed;

    /// <summary>The cursor row of a node, or 0.</summary>
    public int CursorOf(string nodeId) => Cursors.GetValueOrDefault(nodeId, 0);

    /// <summary>The window top of a node, or 0.</summary>
    public int WindowOf(string nodeId) => Windows.GetValueOrDefault(nodeId, 0);

    /// <summary>
    /// The value a value row will use next: its cycle position, defaulting to the first preset.
    /// </summary>
    public string CurrentValue(MenuNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Presets.Length == 0)
        {
            return string.Empty;
        }

        int index = Cycles.GetValueOrDefault(node.Id, 0);
        return node.Presets[((index % node.Presets.Length) + node.Presets.Length) % node.Presets.Length];
    }

    /// <summary>Closes the menu but keeps every cursor - what the zoning ClientIsReady does.</summary>
    public MenuState CloseKeepingCursors() => this with
    {
        Mode = MenuMode.Closed,
        PendingId = null,
        PendingArgs = [],
        PendingIndex = 0,
        PendingInvocation = null,
        PendingError = null,
        LastFrameHash = 0,
    };

    /// <summary>Applies one input. Never throws; every unknown combination answers with a line.</summary>
    public MenuStep Apply(MenuInput input, MenuTree tree, MenuView view)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(view);

        return Mode switch
        {
            MenuMode.Closed => WhenClosed(input, tree, view),
            MenuMode.Prompt => WhenPrompting(input, tree, view),
            MenuMode.Confirm => WhenConfirming(input, tree, view),
            _ => WhenOpen(input, tree, view),
        };
    }

    private MenuStep WhenClosed(MenuInput input, MenuTree tree, MenuView view) => input.Verb switch
    {
        MenuVerb.Toggle or MenuVerb.Redraw => new MenuStep(this with { Mode = MenuMode.Open }, Redraw: true),
        MenuVerb.Jump => Jump(input.Path ?? [], tree, view),
        MenuVerb.Pick => (this with { Mode = MenuMode.Open }).WhenOpen(input, tree, view),
        MenuVerb.Close => new MenuStep(this, ResultLine: "? menu already closed -- /m opens it"),
        _ => new MenuStep(this, ResultLine: "? menu closed -- /m opens it"),
    };

    private MenuStep WhenOpen(MenuInput input, MenuTree tree, MenuView view)
    {
        MenuNode node = tree.Resolve(Path);
        IReadOnlyList<MenuNode> rows = tree.VisibleChildren(node, view.Tier, view.ShowAllRows);
        int rowCount = rows.Count;
        int cursor = rowCount == 0 ? 0 : Math.Clamp(CursorOf(node.Id), 0, rowCount - 1);

        switch (input.Verb)
        {
            case MenuVerb.Toggle:
            case MenuVerb.Redraw:
                return new MenuStep(WithCursor(node, cursor, rowCount, view), Redraw: true);

            case MenuVerb.Close:
                return new MenuStep(CloseKeepingCursors());

            case MenuVerb.Up:
                return Move(node, cursor, -1, rowCount, view);

            case MenuVerb.Down:
                return Move(node, cursor, +1, rowCount, view);

            case MenuVerb.FastUp:
                return Move(node, cursor, -FastStep, rowCount, view, wrap: false);

            case MenuVerb.FastDown:
                return Move(node, cursor, +FastStep, rowCount, view, wrap: false);

            case MenuVerb.Home:
                return new MenuStep(WithCursor(node, 0, rowCount, view), Redraw: true);

            case MenuVerb.End:
                return new MenuStep(WithCursor(node, Math.Max(0, rowCount - 1), rowCount, view), Redraw: true);

            case MenuVerb.Back:
                if (Path.IsEmpty)
                {
                    return new MenuStep(CloseKeepingCursors());
                }

                return new MenuStep(this with { Mode = MenuMode.Open, Path = Path.RemoveAt(Path.Count - 1) }, Redraw: true);

            case MenuVerb.Select:
                if (rowCount == 0)
                {
                    return new MenuStep(this, ResultLine: "? nothing on this page");
                }

                return Act(rows[cursor], node, cursor, rowCount, tree, view);

            case MenuVerb.Pick:
            {
                int window = WindowFor(node, cursor, rowCount, view);
                int visible = Math.Min(view.Settings.Rows, rowCount - window);
                if (input.Number < 1 || input.Number > rowCount)
                {
                    return new MenuStep(this, ResultLine: $"? not a row: {input.Number}");
                }

                // Rows are numbered by their place in the whole list, which is what the frame
                // prints (1..9 then 10, 11, ...), so a pick is only valid inside the window.
                if (input.Number <= window || input.Number > window + visible)
                {
                    return new MenuStep(this, ResultLine: $"? row {input.Number} is not on screen -- /d /u first");
                }

                int picked = input.Number - 1;
                MenuState moved = WithCursor(node, picked, rowCount, view);
                return moved.Act(rows[picked], node, picked, rowCount, tree, view);
            }

            case MenuVerb.Jump:
                return Jump(input.Path ?? [], tree, view);

            case MenuVerb.Yes:
            case MenuVerb.No:
                return new MenuStep(this, ResultLine: "? nothing to confirm");

            default:
                return new MenuStep(this, ResultLine: "? not a menu step -- /d /u /s /b /q /m <n>");
        }
    }

    private MenuStep Move(MenuNode node, int cursor, int delta, int rowCount, MenuView view, bool wrap = true)
    {
        if (rowCount == 0)
        {
            return new MenuStep(this, ResultLine: "? nothing on this page");
        }

        int next = cursor + delta;
        if (wrap && view.Settings.WrapAround)
        {
            next = ((next % rowCount) + rowCount) % rowCount;
        }
        else
        {
            next = Math.Clamp(next, 0, rowCount - 1);
        }

        return new MenuStep(WithCursor(node, next, rowCount, view), Redraw: true);
    }

    private MenuState WithCursor(MenuNode node, int cursor, int rowCount, MenuView view)
    {
        int window = WindowFor(node, cursor, rowCount, view);
        return this with
        {
            Mode = MenuMode.Open,
            Cursors = Cursors.SetItem(node.Id, cursor),
            Windows = Windows.SetItem(node.Id, window),
        };
    }

    private int WindowFor(MenuNode node, int cursor, int rowCount, MenuView view)
    {
        int rows = Math.Max(1, view.Settings.Rows);
        if (rowCount <= rows)
        {
            return 0;
        }

        int window = view.Settings.RememberPath ? WindowOf(node.Id) : 0;
        window = Math.Clamp(window, 0, Math.Max(0, rowCount - rows));
        if (cursor < window)
        {
            window = cursor;
        }
        else if (cursor >= window + rows)
        {
            window = cursor - rows + 1;
        }

        return Math.Clamp(window, 0, Math.Max(0, rowCount - rows));
    }

    private MenuStep Act(MenuNode row, MenuNode parent, int cursor, int rowCount, MenuTree tree, MenuView view)
    {
        MenuState moved = WithCursor(parent, cursor, rowCount, view);

        if (row.Kind == MenuKind.Submenu)
        {
            return new MenuStep(moved with { Path = moved.Path.Add(row.Id) }, Redraw: true);
        }

        if (row.Tier > view.Tier)
        {
            return new MenuStep(moved, ResultLine: TierRefusal(row.Tier, view.Tier));
        }

        if (tree.IsRefused(row))
        {
            return new MenuStep(
                moved,
                ResultLine: $"- the client refused the name '{row.CommandName}' -- rename it (see docs/97)");
        }

        string? notYet = tree.NotYetReason(row);
        if (notYet is not null)
        {
            return new MenuStep(moved, ResultLine: $"- not available yet -- {notYet}");
        }

        if (!MatchGates.Allows(row.Gate, view.MatchStep))
        {
            return new MenuStep(moved, ResultLine: $"! {MatchGates.Refusal(row.Gate, view.MatchStep)}");
        }

        if (row.SettingKey is not null)
        {
            return ActSetting(row, moved, view);
        }

        ConsoleCommand? command = tree.CommandOf(row);
        if (command is null)
        {
            return new MenuStep(moved, ResultLine: $"- /{row.CommandName} is not registered on this build");
        }

        switch (row.Kind)
        {
            case MenuKind.Toggle:
            {
                string args = Join(row.BoundArgs, view.IsOn(row) ? row.OffArgs : row.OnArgs);
                return new MenuStep(moved, Invocation: new CommandInvocation(command.Name, args, row.Id), Redraw: true);
            }

            case MenuKind.Value:
            {
                string value = moved.CurrentValue(row);
                int next = row.Presets.Length == 0 ? 0 : moved.Cycles.GetValueOrDefault(row.Id, 0) + 1;
                MenuState cycled = moved with { Cycles = moved.Cycles.SetItem(row.Id, next) };
                return new MenuStep(
                    cycled,
                    Invocation: new CommandInvocation(command.Name, Join(row.BoundArgs, value), row.Id),
                    Redraw: true);
            }

            case MenuKind.Prompt:
            {
                // BoundArgs already answers the leading declared arguments, so the prompt opens on
                // the first one the row has NOT supplied. Starting at 0 asked for 'verb' again and
                // built "/loot spawn stats <item> 1", which LootCommands.Spawn read as the item
                // (review-2 major).
                int bound = BoundCount(row, command);
                if (bound >= command.Args.Length)
                {
                    return Confirmed(moved, command, row, row.BoundArgs, view);
                }

                return new MenuStep(
                    moved with
                    {
                        Mode = MenuMode.Prompt,
                        PendingId = row.Id,
                        PendingArgs = [],
                        PendingIndex = bound,
                        PendingError = null,
                    },
                    Redraw: true);
            }

            default:
                return Confirmed(moved, command, row, row.BoundArgs, view);
        }
    }

    private MenuStep ActSetting(MenuNode row, MenuState moved, MenuView view)
    {
        if (row.Kind == MenuKind.Value)
        {
            string value = moved.CurrentValue(row);
            string? live = view.ValueOf(row);
            if (live is not null && row.Presets.Length > 0)
            {
                // Start the cycle from whatever the setting actually is, so the first select moves
                // it forward rather than jumping back to the first preset.
                int at = Array.IndexOf(row.Presets, live);
                if (at >= 0)
                {
                    value = row.Presets[(at + 1) % row.Presets.Length];
                }
            }

            int next = row.Presets.Length == 0 ? 0 : Array.IndexOf(row.Presets, value) + 1;
            return new MenuStep(
                moved with { Cycles = moved.Cycles.SetItem(row.Id, next) },
                Redraw: true,
                Setting: new SettingChange(row.SettingKey!, value));
        }

        string flipped = view.IsOn(row) ? "off" : "on";
        return new MenuStep(moved, Redraw: true, Setting: new SettingChange(row.SettingKey!, flipped));
    }

    private MenuStep Confirmed(MenuState moved, ConsoleCommand command, MenuNode row, string args, MenuView view)
    {
        CommandInvocation invocation = new(command.Name, args.Trim(), row.Id);
        if (command.Confirm && view.Settings.Confirms)
        {
            return new MenuStep(
                moved with
                {
                    Mode = MenuMode.Confirm,
                    PendingId = row.Id,
                    PendingInvocation = invocation,
                    PendingError = null,
                },
                Redraw: true);
        }

        return new MenuStep(moved, Invocation: invocation, Redraw: true);
    }

    private MenuStep WhenPrompting(MenuInput input, MenuTree tree, MenuView view)
    {
        MenuNode? row = tree.ById(PendingId);
        ConsoleCommand? command = row is null ? null : tree.CommandOf(row);
        if (row is null || command is null)
        {
            return new MenuStep(this with { Mode = MenuMode.Open, PendingId = null }, Redraw: true);
        }

        if (input.Verb == MenuVerb.Back)
        {
            return new MenuStep(
                this with { Mode = MenuMode.Open, PendingId = null, PendingArgs = [], PendingIndex = 0, PendingError = null },
                Redraw: true,
                ResultLine: "* cancelled");
        }

        if (input.Verb == MenuVerb.Close)
        {
            return new MenuStep(CloseKeepingCursors());
        }

        ArgSpec spec = command.Args[Math.Min(PendingIndex, command.Args.Length - 1)];
        string answer = input.Verb == MenuVerb.Toggle ? spec.Default ?? string.Empty : input.Raw.Trim();

        if (answer.Length == 0 && spec.Required)
        {
            return new MenuStep(
                this with { PendingError = $"{spec.Name} is required" },
                Redraw: true);
        }

        if (!Validates(spec, answer, out string? why))
        {
            return new MenuStep(this with { PendingError = why }, Redraw: true);
        }

        ImmutableList<string> collected = PendingArgs.Add(answer);
        int next = PendingIndex + 1;
        if (next < command.Args.Length)
        {
            return new MenuStep(
                this with { PendingArgs = collected, PendingIndex = next, PendingError = null },
                Redraw: true);
        }

        string args = Join(row.BoundArgs, string.Join(' ', collected.Where(a => a.Length > 0)));
        MenuState closedPrompt = this with
        {
            Mode = MenuMode.Open,
            PendingId = null,
            PendingArgs = [],
            PendingIndex = 0,
            PendingError = null,
        };

        return closedPrompt.Confirmed(closedPrompt, command, row, args, view);
    }

    private MenuStep WhenConfirming(MenuInput input, MenuTree tree, MenuView view)
    {
        MenuState back = this with
        {
            Mode = MenuMode.Open,
            PendingId = null,
            PendingInvocation = null,
            PendingError = null,
        };

        if (input.Verb == MenuVerb.Yes && PendingInvocation is not null)
        {
            return new MenuStep(back, Redraw: true, Invocation: PendingInvocation);
        }

        if (input.Verb == MenuVerb.Close)
        {
            return new MenuStep(CloseKeepingCursors());
        }

        return new MenuStep(back, Redraw: true, ResultLine: "* cancelled");
    }

    private MenuStep Jump(IReadOnlyList<string> words, MenuTree tree, MenuView view)
    {
        if (words.Count == 0)
        {
            return new MenuStep(this with { Mode = MenuMode.Open }, Redraw: true);
        }

        MenuNode node = tree.Root;
        ImmutableList<string> path = [];
        int index = 0;

        while (index < words.Count)
        {
            IReadOnlyList<MenuNode> children = tree.VisibleChildren(node, view.Tier, view.ShowAllRows);
            MenuNode? hit = MatchChild(children, words[index], out string? ambiguity);
            if (hit is null)
            {
                if (ambiguity is not null)
                {
                    return new MenuStep(this, ResultLine: $"? {ambiguity}");
                }

                break;
            }

            index++;
            if (hit.Kind == MenuKind.Submenu)
            {
                node = hit;
                path = path.Add(hit.Id);
                continue;
            }

            // A leaf. Anything left on the line is its complete argument list, and from a closed
            // menu the leaf runs without the menu ever opening (design §2.1 "jump and run").
            bool wasClosed = Mode == MenuMode.Closed;
            MenuState at = this with { Mode = MenuMode.Open, Path = path };
            int cursorAt = Math.Max(0, children.ToList().IndexOf(hit));
            MenuStep step = at.Act(hit, node, cursorAt, children.Count, tree, view);

            if (index < words.Count)
            {
                string tail = string.Join(' ', words.Skip(index));
                if (step.Invocation is not null)
                {
                    step = step with
                    {
                        Invocation = step.Invocation with { Arguments = Join(step.Invocation.Arguments, tail) },
                    };
                }
                else if (step.State.Mode == MenuMode.Prompt && tree.CommandOf(hit) is { } prompted)
                {
                    // A prompt leaf given its arguments outright skips the prompt entirely.
                    step = at.Confirmed(at, prompted, hit, Join(hit.BoundArgs, tail), view);
                }
            }

            if (!wasClosed)
            {
                return step;
            }

            // From a closed menu the leaf runs and the menu never opens - unless the step is not
            // finished. A Confirm row (/m player kill, /m debug raw) and a Prompt row given no
            // arguments (/m items give, /m player tp xyz) both need one more keystroke, and the
            // answer they are waiting on lives in the state itself. Closing here would wipe
            // PendingInvocation/PendingId and Redraw:false would draw nothing, so the whole line
            // was silent and ran nothing (review-1 M3, against the engine's "never silent" rule).
            // Keeping the menu open makes the confirm/prompt frame the reply, and /y or the typed
            // answer finishes what the jump started.
            return step.State.Mode is MenuMode.Confirm or MenuMode.Prompt
                ? step with { Redraw = true }
                : step with { State = step.State.CloseKeepingCursors(), Redraw = false };
        }

        if (index == 0)
        {
            return new MenuStep(this, ResultLine: $"? not a menu path: '{words[0]}'");
        }

        MenuState landed = this with { Mode = MenuMode.Open, Path = path };
        return new MenuStep(landed, Redraw: true);
    }

    private static MenuNode? MatchChild(IReadOnlyList<MenuNode> children, string word, out string? ambiguity)
    {
        ambiguity = null;
        List<MenuNode> exact =
        [
            .. children.Where(c =>
                Slug(c.Label).Equals(word, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.CommandName, word, StringComparison.OrdinalIgnoreCase)
                || c.Id.EndsWith('.' + word, StringComparison.OrdinalIgnoreCase)),
        ];

        if (exact.Count == 1)
        {
            return exact[0];
        }

        List<MenuNode> prefixed =
        [
            .. children.Where(c =>
                Slug(c.Label).StartsWith(word, StringComparison.OrdinalIgnoreCase)
                || (c.CommandName?.StartsWith(word, StringComparison.OrdinalIgnoreCase) ?? false)),
        ];

        if (prefixed.Count == 1)
        {
            return prefixed[0];
        }

        if (prefixed.Count > 1)
        {
            ambiguity = $"ambiguous: '{word}' -- {string.Join(' ', prefixed.Select(p => Slug(p.Label)))}";
        }

        return null;
    }

    /// <summary>
    /// A label reduced to one lower-case word a jump can type: every letter and digit, nothing
    /// else. The whole label is kept rather than its first word, because four vehicle rows all
    /// begin "Spawn" and a first-word slug would make every one of them ambiguous. Matching is by
    /// prefix, so <c>/m vehicles spawnat</c> still lands, and the node id's last segment
    /// (<c>vehicles.atv</c>) gives every row a short name as well.
    /// </summary>
    public static string Slug(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        System.Text.StringBuilder text = new();
        foreach (char c in label)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                text.Append(char.ToLowerInvariant(c));
            }
        }

        return text.ToString();
    }

    private static bool Validates(ArgSpec spec, string answer, out string? why)
    {
        why = null;
        if (answer.Length == 0)
        {
            return true;
        }

        switch (spec.Kind)
        {
            case ArgKind.Int:
                if (!int.TryParse(answer, out _) && !answer.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    why = $"{spec.Name}: '{answer}' is not a number";
                    return false;
                }

                return true;

            case ArgKind.Float:
                if (!float.TryParse(answer, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    why = $"{spec.Name}: '{answer}' is not a number";
                    return false;
                }

                return true;

            case ArgKind.Enum:
                if (spec.Choices is { Length: > 0 } choices
                    && !choices.Any(c => c.StartsWith(answer, StringComparison.OrdinalIgnoreCase)))
                {
                    why = $"{spec.Name}: try {string.Join(' ', choices)}";
                    return false;
                }

                return true;

            default:
                return true;
        }
    }

    /// <summary>
    /// How many of a command's declared arguments a row's <c>BoundArgs</c> already answers, capped
    /// at the declared count. Split the same way the command line itself is, so a quoted bound
    /// argument counts once.
    /// </summary>
    private static int BoundCount(MenuNode row, ConsoleCommand command) =>
        row.BoundArgs.Length == 0
            ? 0
            : Math.Min(CommandLine.Tokenise(row.BoundArgs).Count, command.Args.Length);

    private static string Join(string first, string second)
    {
        if (first.Length == 0)
        {
            return second;
        }

        return second.Length == 0 ? first : $"{first} {second}";
    }

    private static string TierRefusal(ConsoleTier needed, ConsoleTier has) => needed switch
    {
        ConsoleTier.Owner => $"! Owner only (you are {has})",
        ConsoleTier.Tester => $"! Tester or above (you are {has})",
        _ => "! refused",
    };
}
