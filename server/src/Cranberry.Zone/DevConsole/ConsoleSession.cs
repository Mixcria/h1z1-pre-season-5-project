using Cranberry.Zone.DevConsole.Menu;

namespace Cranberry.Zone.DevConsole;

/// <summary>How the frame is drawn. ASCII in every theme - the console font's glyph coverage is [U].</summary>
public enum MenuTheme : byte
{
    /// <summary><c>+===+</c> top rule, <c>+---+</c> bottom rule, <c>|</c> sides. The default.</summary>
    Plain = 0,

    /// <summary><c>#</c> sides and <c>#===#</c> rules - louder, for a busy pane.</summary>
    Heavy = 1,
}

/// <summary>
/// The menu's own Settings page, per session and never persisted: the owner tunes the frame to his
/// pane, not to a file (design §2.5 Settings).
/// </summary>
public sealed record ConsoleSettings
{
    /// <summary>Visible rows in a window (6..16).</summary>
    public int Rows { get; init; } = 10;

    /// <summary>Frame width in columns (40..60).</summary>
    public int Width { get; init; } = 46;

    /// <summary>Draw the <c>/d /u move ...</c> hint line under every frame.</summary>
    public bool HintKeys { get; init; } = true;

    /// <summary>Draw the <c>= /godmode off ...</c> info line, which teaches the typed form.</summary>
    public bool TypedHints { get; init; } = true;

    /// <summary>Ask before a destructive row runs.</summary>
    public bool Confirms { get; init; } = true;

    /// <summary>The cursor wraps from the last row to the first.</summary>
    public bool WrapAround { get; init; } = true;

    /// <summary>Emit nothing when a redraw would be identical to the last frame (R4 §7.1 rule 9).</summary>
    public bool SuppressIdenticalFrames { get; init; } = true;

    /// <summary>Remember the cursor of each node, so <c>/b</c> then <c>/s</c> lands where it was.</summary>
    public bool RememberPath { get; init; } = true;

    /// <summary>Frame decoration.</summary>
    public MenuTheme Theme { get; init; } = MenuTheme.Plain;

    /// <summary>The surface this session draws on; null follows the boot default.</summary>
    public ConsoleSurfaceKind? Surface { get; init; }

    /// <summary>Redraw a frame holding live values once a second. Designed in, off in v1 (design §2.8).</summary>
    public bool LiveRefresh { get; init; }

    /// <summary>Draw rows above the caller's tier greyed instead of hiding them (Owner only).</summary>
    public bool ShowAllRows { get; init; }

    /// <summary>The shipped defaults.</summary>
    public static ConsoleSettings Default { get; } = new();

    /// <summary>The defaults with the host's geometry and confirm switch folded in.</summary>
    public static ConsoleSettings From(ConsoleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ConsoleSettings
        {
            Rows = options.EffectiveRows,
            Width = options.EffectiveWidth,
            Confirms = options.Confirms,
        };
    }
}

/// <summary>
/// Everything the console remembers about one player: who he is allowed to be, where his menu
/// cursor is, which toggles he has flipped, and how many name bursts his client has had.
/// <para>
/// It hangs off the per-link <c>GatewaySessionState</c> (one added property, design §4.6 #2), so it
/// dies with the link and nothing has to be cleaned up. The menu survives a zone transfer as a
/// closed menu with its cursors intact, which is why <see cref="Menu"/> is replaced rather than
/// discarded when the second <c>ClientIsReady</c> arrives.
/// </para>
/// </summary>
public sealed class ConsoleSession
{
    /// <summary>Resolved once at login by <see cref="ConsolePermission.Resolve"/>.</summary>
    public ConsoleTier Tier { get; set; } = ConsoleTier.Player;

    /// <summary>The per-session surface override from <c>/surface</c>; null follows the boot default.</summary>
    public ConsoleSurfaceKind? Surface { get; set; }

    /// <summary>Where the menu is. Closed until the first <c>/m</c>.</summary>
    public MenuState Menu { get; set; } = MenuState.Closed;

    /// <summary>How many <c>AddWorldCommand</c> bursts this link has had (design §1.2).</summary>
    public int BurstsSent { get; set; }

    /// <summary>The <c>* Cranberry console ready</c> line goes out once per link.</summary>
    public bool ReadyLineSent { get; set; }

    /// <summary>God mode. Read by the one guard line at the top of <c>ApplyGasDamage</c>.</summary>
    public bool Invulnerable { get; set; }
    public float SpeedMultiplier { get; set; } = 1f;
    /// <summary>Absolute native /run override in m/s; null follows the normal footwear profile.</summary>
    public float? NativeRunMetresPerSecond { get; set; }
    public int LobbyGeneration { get; set; }
    public ConsoleTier? TierOverride { get; set; }

    /// <summary>
    /// Where the player stood before the last <c>/tp</c>, so <c>/tp back</c> can undo it. Lane C's
    /// one addition to this type; it is per-link like everything else here and is never persisted.
    /// </summary>
    public System.Numerics.Vector3? LastPosition { get; set; }
    /// <summary>Personal map bookmarks, kept until disconnect.</summary>
    public Dictionary<string, System.Numerics.Vector3> SavedPlaces { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>/raw</c> has been confirmed once and will send bytes without asking again.</summary>
    public bool RawArmed { get; set; }

    /// <summary>
    /// The <c>/win raw</c> line awaiting its confirming repeat, canonicalised as
    /// <c>Object.Method [ints]</c>; null when nothing is pending.
    /// <para>
    /// <c>/win raw</c> sends an arbitrary name into the client's Lua state, so it asks once - the
    /// typed equivalent of the menu's confirm on <c>/raw</c>. It is per link and never persisted,
    /// like everything else here, and <c>Settings.Confirms</c> turns it off with the rest.
    /// </para>
    /// </summary>
    public string? PendingWindowScript { get; set; }

    /// <summary>The tick of the last accepted input, for the rate limit. Negative = none yet.</summary>
    public long LastInputTick { get; set; } = long.MinValue;

    /// <summary>This session's Settings page.</summary>
    public ConsoleSettings Settings { get; set; } = ConsoleSettings.Default;

    /// <summary>The surface kind actually in force, given the boot default.</summary>
    public ConsoleSurfaceKind SurfaceKind(ConsoleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Surface ?? Settings.Surface ?? options.Surface;
    }

    /// <summary>
    /// The Jiggy status ticker (R4 §7.1 rule 14): the ON toggles, printed after a toggle changes
    /// and when the menu closes. Empty when nothing is on, and then nothing is printed.
    /// </summary>
    public string StatusTicker()
    {
        List<string> flags = [];
        if (Invulnerable)
        {
            flags.Add("GOD");
        }

        if (RawArmed)
        {
            flags.Add("RAW-ARMED");
        }

        return flags.Count == 0 ? string.Empty : string.Join("  ", flags);
    }
}
