namespace Cranberry.Zone.DevConsole;

/// <summary>
/// Which s2c packet a console line becomes. The default is <see cref="Print"/> because it is the
/// only one whose handler ends in the console pane and nowhere else; the others exist so the first
/// click can answer "which of these does the open pane actually draw?" without a rebuild
/// (design §1.3, §4.5).
/// </summary>
public enum ConsoleSurfaceKind : byte
{
    /// <summary><c>Chat 06 03</c> - console only (<c>FUN_141251d90</c> then <c>PrintConsole</c>).</summary>
    Print = 0,

    /// <summary><c>Chat.ChatText 06 05</c> with the last byte 1 - chat pane <b>and</b> console (<c>:775-777</c>).</summary>
    Chat = 1,

    /// <summary><c>Chat.ChatText 06 05</c> with the last byte 0 - the owner's exact 1087 bytes, chat pane only.</summary>
    Chat0 = 2,

    /// <summary><c>ClientUpdate.TextAlert 11 31</c> - the emergency surface; a banner, not a pane.</summary>
    Alert = 3,

    /// <summary>
    /// <c>Chat 06 03</c> again, but every line prefixed <c>CRANBERRY:</c> and tagged with a frame
    /// command, for the client-side script <c>Resources\Scripts\CranberryMenu.lua</c> to draw in a
    /// real window (R6 recommendation (b'), <see cref="Surfaces.LuaMenuSurface"/>). Opt-in: on a
    /// client with no script loaded the prefixed text prints raw in the pane.
    /// </summary>
    Lua = 4,
}

/// <summary>
/// When the <c>Command.AddWorldCommand</c> burst is sent. Re-sending a name the client already
/// holds is a proven silent no-op - <c>FUN_141280280:36-46</c> returns on a hit in the per-object
/// table before the conflict check and before any log - which is what makes
/// <see cref="EachZone"/> safe as the default (design §1.2).
/// </summary>
public enum ConsoleRegisterPolicy : byte
{
    /// <summary>Every <c>ClientIsReady</c>, menu and zoning. The default: the only placement that
    /// re-fills the table if the zone transfer rebuilds it.</summary>
    EachZone = 0,

    /// <summary>The first <c>ClientIsReady</c> on the link - the owner's 1087 placement.</summary>
    Once = 1,

    /// <summary>The zoning <c>ClientIsReady</c> only - the retreat if the menu burst ever disturbs
    /// the login-critical menu path (D178). The console then opens only in the match.</summary>
    Zoning = 2,

    /// <summary>Never. Nothing is typeable; useful only for the surface probe.</summary>
    Off = 3,
}

/// <summary>
/// Every switch of the developer console, in one record, read from the environment so the owner can
/// change one behaviour at a time without a rebuild - the <c>CombatOptions</c> pattern.
/// <para>
/// <c>CRANBERRY_CONSOLE=0</c> is the one-word revert: the engine is never constructed, the
/// dispatch clause cannot match, and a typed <c>09 42</c> falls to the same unknown-packet log arm
/// it reaches today (design §4.6).
/// </para>
/// </summary>
public sealed record ConsoleOptions
{
    /// <summary>Environment switch for <see cref="Enabled"/>.</summary>
    public const string EnabledVariable = "CRANBERRY_CONSOLE";

    /// <summary>Environment variable selecting <see cref="Surface"/>.</summary>
    public const string SurfaceVariable = "CRANBERRY_CONSOLE_SURFACE";

    /// <summary>Environment switch for <see cref="LocalIsOwner"/>.</summary>
    public const string LocalOwnerVariable = "CRANBERRY_CONSOLE_LOCAL_OWNER";

    /// <summary>Environment variable holding <see cref="Tiers"/>.</summary>
    public const string TiersVariable = "CRANBERRY_CONSOLE_TIERS";

    /// <summary>Environment variable selecting <see cref="Register"/>.</summary>
    public const string RegisterVariable = "CRANBERRY_CONSOLE_REGISTER";

    /// <summary>Environment switch for <see cref="SelfFlagOpensConsole"/>.</summary>
    public const string SelfFlagVariable = "CRANBERRY_CONSOLE_SELF_FLAG";

    /// <summary>Environment variable holding <see cref="ClientLogsPath"/>.</summary>
    public const string ClientLogsVariable = "CRANBERRY_CLIENT_LOGS";

    /// <summary>Environment variable holding <see cref="MenuRows"/>.</summary>
    public const string RowsVariable = "CRANBERRY_CONSOLE_ROWS";

    /// <summary>Environment variable holding <see cref="MenuWidth"/>.</summary>
    public const string WidthVariable = "CRANBERRY_CONSOLE_WIDTH";

    /// <summary>Environment switch for <see cref="Confirms"/>.</summary>
    public const string ConfirmsVariable = "CRANBERRY_CONSOLE_CONFIRMS";

    /// <summary>The narrowest frame the grid still reads at (R4 §7.4).</summary>
    public const int MinimumWidth = 40;

    /// <summary>The widest frame the grid is defined for (R4 §7.4).</summary>
    public const int MaximumWidth = 60;

    /// <summary>The fewest visible rows a window may show.</summary>
    public const int MinimumRows = 6;

    /// <summary>The most visible rows a window may show; also the largest two-digit hot key.</summary>
    public const int MaximumRows = 16;

    /// <summary>Answer <c>09 42</c> at all. Off is the one-word revert to today's behaviour.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Which packet a line becomes by default; a session can override it with <c>/surface</c>.</summary>
    public ConsoleSurfaceKind Surface { get; init; } = ConsoleSurfaceKind.Print;

    /// <summary>Loopback and private remotes are Owner. On, because the owner plays on his own host.</summary>
    public bool LocalIsOwner { get; init; } = true;

    /// <summary>
    /// The explicit tier list, e.g. <c>name=owner,0x1001=tester,*=player</c>. Empty means the
    /// <see cref="LocalIsOwner"/> rule alone decides.
    /// </summary>
    public string Tiers { get; init; } = string.Empty;

    /// <summary>When the <c>AddWorldCommand</c> burst goes out.</summary>
    public ConsoleRegisterPolicy Register { get; init; } = ConsoleRegisterPolicy.EachZone;

    /// <summary>
    /// Door A: set <c>SelfRecord.FlagI</c> for Owner sessions so the client's own Tilde binding
    /// opens the pane with no tool. <b>Off by default</b> - the flag sits on the login-critical
    /// <c>SendSelfToClient</c> and may be overloaded with other admin UI (design §3.5).
    /// </summary>
    public bool SelfFlagOpensConsole { get; init; } = true;

    /// <summary>The experimental mod menu is parked; development uses slash commands.</summary>
    public bool ModMenuEnabled { get; init; }

    /// <summary>Where the client writes <c>AdminCommands.log</c>, the refusal backstop (design §4.3).</summary>
    public string ClientLogsPath { get; init; } = @"C:\Aug2017\Client\Logs";

    /// <summary>Visible rows in a menu window. Clamped to 6..16.</summary>
    public int MenuRows { get; init; } = 10;

    /// <summary>Frame width in columns. Clamped to 40..60.</summary>
    public int MenuWidth { get; init; } = 46;

    /// <summary>Ask before a destructive menu row runs.</summary>
    public bool Confirms { get; init; } = true;

    /// <summary>
    /// The floor between two console inputs from one session, in milliseconds. Inputs closer than
    /// this are dropped without a reply, so a held key cannot turn a menu into a packet storm.
    /// </summary>
    public int RateLimitMs { get; init; } = 50;

    /// <summary>The shipped defaults.</summary>
    public static ConsoleOptions Default { get; } = new();

    /// <summary>Rows clamped into range, so a typo in the environment cannot break the grid.</summary>
    public int EffectiveRows => Math.Clamp(MenuRows, MinimumRows, MaximumRows);

    /// <summary>Width clamped into range.</summary>
    public int EffectiveWidth => Math.Clamp(MenuWidth, MinimumWidth, MaximumWidth);

    /// <summary>The full path of the client's refusal log.</summary>
    public string CollisionLogPath => System.IO.Path.Combine(ClientLogsPath, "AdminCommands.log");

    /// <summary>
    /// Reads every switch from the environment. Only the exact string <c>"1"</c> or <c>"0"</c>
    /// moves a boolean and only a known word moves an enum, so a typo leaves the default rather
    /// than silently changing how the console behaves.
    /// </summary>
    public static ConsoleOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= System.Environment.GetEnvironmentVariable;
        ConsoleOptions defaults = Default;
        return new ConsoleOptions
        {
            Enabled = Switch(read, EnabledVariable, defaults.Enabled),
            Surface = ParseSurface(read(SurfaceVariable), defaults.Surface),
            LocalIsOwner = Switch(read, LocalOwnerVariable, defaults.LocalIsOwner),
            Tiers = read(TiersVariable) ?? defaults.Tiers,
            Register = ParseRegister(read(RegisterVariable), defaults.Register),
            SelfFlagOpensConsole = Switch(read, SelfFlagVariable, defaults.SelfFlagOpensConsole),
            ClientLogsPath = Blank(read(ClientLogsVariable)) ? defaults.ClientLogsPath : read(ClientLogsVariable)!,
            MenuRows = Number(read(RowsVariable), defaults.MenuRows),
            MenuWidth = Number(read(WidthVariable), defaults.MenuWidth),
            Confirms = Switch(read, ConfirmsVariable, defaults.Confirms),
        };
    }

    /// <summary>Reads a surface word; anything else keeps <paramref name="fallback"/>.</summary>
    public static ConsoleSurfaceKind ParseSurface(string? word, ConsoleSurfaceKind fallback) =>
        word?.Trim().ToLowerInvariant() switch
        {
            "print" => ConsoleSurfaceKind.Print,
            "chat" or "chat1" => ConsoleSurfaceKind.Chat,
            "chat0" => ConsoleSurfaceKind.Chat0,
            "alert" => ConsoleSurfaceKind.Alert,
            "lua" => ConsoleSurfaceKind.Lua,
            _ => fallback,
        };

    /// <summary>Reads a register-policy word; anything else keeps <paramref name="fallback"/>.</summary>
    public static ConsoleRegisterPolicy ParseRegister(string? word, ConsoleRegisterPolicy fallback) =>
        word?.Trim().ToLowerInvariant() switch
        {
            "each-zone" or "eachzone" => ConsoleRegisterPolicy.EachZone,
            "once" => ConsoleRegisterPolicy.Once,
            "zoning" => ConsoleRegisterPolicy.Zoning,
            "off" => ConsoleRegisterPolicy.Off,
            _ => fallback,
        };

    /// <summary>The word <c>/surface</c> and the boot banner use for a surface.</summary>
    public static string SurfaceWord(ConsoleSurfaceKind kind) => kind switch
    {
        ConsoleSurfaceKind.Print => "print",
        ConsoleSurfaceKind.Chat => "chat",
        ConsoleSurfaceKind.Chat0 => "chat0",
        ConsoleSurfaceKind.Lua => "lua",
        _ => "alert",
    };

    /// <summary>The opcode a surface writes, for the boot banner.</summary>
    public static string SurfaceOpcode(ConsoleSurfaceKind kind) => kind switch
    {
        ConsoleSurfaceKind.Print => "06 03",
        ConsoleSurfaceKind.Chat or ConsoleSurfaceKind.Chat0 => "06 05",
        ConsoleSurfaceKind.Lua => "06 03 CRANBERRY:",
        _ => "11 31",
    };

    /// <summary>The word the environment uses for a register policy.</summary>
    public static string RegisterWord(ConsoleRegisterPolicy policy) => policy switch
    {
        ConsoleRegisterPolicy.EachZone => "each-zone",
        ConsoleRegisterPolicy.Once => "once",
        ConsoleRegisterPolicy.Zoning => "zoning",
        _ => "off",
    };

    /// <summary>
    /// The one boot line. <paramref name="registrySummary"/> is
    /// <c>ConsoleEngine.Summary</c> - the command and name counts - so a play-test can never guess
    /// which arms ran or which names went out (design §4.7).
    /// </summary>
    public string Describe(string? registrySummary = null)
    {
        if (!Enabled)
        {
            return $"console: off ({EnabledVariable}=0) - 09 42 is logged and unanswered, as before";
        }

        string registry = string.IsNullOrEmpty(registrySummary) ? string.Empty : registrySummary + ", ";
        return $"console: ON - {registry}"
            + $"names pushed {RegisterWord(Register)} after ClientIsReady; "
            + $"tiers local={(LocalIsOwner ? "Owner" : "Player")}"
            + (string.IsNullOrWhiteSpace(Tiers) ? string.Empty : $" list='{Tiers}'")
            + $"; surface={SurfaceWord(Surface)} ({SurfaceOpcode(Surface)}); "
            + $"self-flag={(SelfFlagOpensConsole ? "on (Door A: SelfRecord.FlagI for Owner links)" : "off")}; "
            + (ModMenuEnabled ? $"menu {EffectiveWidth}x{EffectiveRows}, confirms={(Confirms ? "on" : "off")}; " : "mod menu parked; ")
            + $"refusal log {CollisionLogPath}; {EnabledVariable}=0 reverts";
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static int Number(string? value, int fallback) =>
        int.TryParse(value, out int parsed) ? parsed : fallback;

    private static bool Switch(Func<string, string?> read, string name, bool @default) =>
        read(name) switch
        {
            "1" => true,
            "0" => false,
            _ => @default,
        };
}
