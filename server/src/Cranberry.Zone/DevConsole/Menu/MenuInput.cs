namespace Cranberry.Zone.DevConsole.Menu;

/// <summary>
/// The abstract input alphabet of the menu (R4 §4.1). Every front door reduces to one of these, so
/// the state machine never knows whether a step arrived as <c>/d</c>, as <c>/m d</c> or - if a
/// surface that captures keys is ever found - as an arrow key.
/// </summary>
public enum MenuVerb : byte
{
    /// <summary>Nothing the menu understands; the engine answers with a hint.</summary>
    None = 0,

    /// <summary>A bare <c>/m</c>: open the menu, or redraw it, or accept a prompt's default.</summary>
    Toggle = 1,

    /// <summary>Close the menu and print the status ticker.</summary>
    Close = 2,

    /// <summary>Cursor up one row.</summary>
    Up = 3,

    /// <summary>Cursor down one row.</summary>
    Down = 4,

    /// <summary>Cursor up five rows, clamped.</summary>
    FastUp = 5,

    /// <summary>Cursor down five rows, clamped.</summary>
    FastDown = 6,

    /// <summary>Cursor to the first row.</summary>
    Home = 7,

    /// <summary>Cursor to the last row.</summary>
    End = 8,

    /// <summary>Act on the row under the cursor.</summary>
    Select = 9,

    /// <summary>Leave this submenu, cancel a prompt or a confirm; at the root, close.</summary>
    Back = 10,

    /// <summary>Pick a visible row by its printed number.</summary>
    Pick = 11,

    /// <summary>Walk into the tree by name, optionally with a complete argument list.</summary>
    Jump = 12,

    /// <summary>Free text - a prompt's answer.</summary>
    Text = 13,

    /// <summary>Confirm.</summary>
    Yes = 14,

    /// <summary>Refuse a confirm.</summary>
    No = 15,

    /// <summary>Draw the current frame again.</summary>
    Redraw = 16,
}

/// <summary>
/// One menu step, already reduced to the alphabet. <see cref="Parse"/> is purely syntactic - it
/// does not know whether the menu is open or in a prompt - so a line like <c>/m 90</c> arrives as
/// both a <see cref="MenuVerb.Pick"/> and, through <see cref="Raw"/>, the text a prompt would take.
/// The state machine decides which meaning applies (design §2.8).
/// </summary>
/// <param name="Verb">What was asked for.</param>
/// <param name="Number">The row number, for <see cref="MenuVerb.Pick"/>.</param>
/// <param name="Path">The words of a <see cref="MenuVerb.Jump"/>, lower-cased.</param>
/// <param name="Raw">The untouched argument tail, for a prompt's answer.</param>
public sealed record MenuInput(
    MenuVerb Verb,
    int Number = 0,
    IReadOnlyList<string>? Path = null,
    string Raw = "")
{
    /// <summary>The verbs that are registered as commands in their own right (design §2.1).</summary>
    public static IReadOnlyList<string> ShortVerbs { get; } = ["d", "u", "s", "b", "q", "r"];

    /// <summary>True when the typed name is the menu itself or one of its short verbs.</summary>
    public static bool IsMenuName(string? name) =>
        name is not null
        && (string.Equals(name, "m", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "menu", StringComparison.OrdinalIgnoreCase)
            || ShortVerbs.Contains(name.ToLowerInvariant()));

    /// <summary>
    /// Reduces a typed line to one input. <paramref name="name"/> is <c>m</c>/<c>menu</c> or one of
    /// <c>d u s b q r</c>; <paramref name="line"/> is its argument tail.
    /// </summary>
    public static MenuInput Parse(string name, CommandLine line)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(line);

        string verb = name.ToLowerInvariant();
        if (verb is not ("m" or "menu"))
        {
            // /d /u /s /b /q /r - the three-keystroke door. Any tail is ignored on purpose:
            // the console's own input history repeats the last line, and a stray word must not
            // turn a movement into a jump.
            return new MenuInput(Letter(verb), Raw: line.Raw);
        }

        if (line.Count == 0)
        {
            return new MenuInput(MenuVerb.Toggle, Raw: line.Raw);
        }

        string first = line.Tokens[0];

        if (line.Count == 1)
        {
            MenuVerb single = first switch
            {
                "d" => MenuVerb.Down,
                "dd" => MenuVerb.FastDown,
                "u" => MenuVerb.Up,
                "uu" => MenuVerb.FastUp,
                "t" => MenuVerb.Home,
                "e" => MenuVerb.End,
                "s" => MenuVerb.Select,
                "b" => MenuVerb.Back,
                "q" => MenuVerb.Close,
                "r" => MenuVerb.Redraw,
                "y" => MenuVerb.Yes,
                "n" => MenuVerb.No,
                _ => MenuVerb.None,
            };

            if (single != MenuVerb.None)
            {
                return new MenuInput(single, Raw: line.Raw);
            }

            if (IsAllDigits(first))
            {
                return new MenuInput(MenuVerb.Pick, int.Parse(first), Raw: line.Raw);
            }
        }

        return new MenuInput(MenuVerb.Jump, Path: [.. line.Tokens], Raw: line.Raw);
    }

    private static MenuVerb Letter(string verb) => verb switch
    {
        "d" => MenuVerb.Down,
        "u" => MenuVerb.Up,
        "s" => MenuVerb.Select,
        "b" => MenuVerb.Back,
        "q" => MenuVerb.Close,
        "r" => MenuVerb.Redraw,
        _ => MenuVerb.None,
    };

    private static bool IsAllDigits(string token)
    {
        if (token.Length is 0 or > 3)
        {
            return false;
        }

        foreach (char c in token)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
