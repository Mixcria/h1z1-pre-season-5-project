namespace Cranberry.Zone.DevConsole;

/// <summary>
/// How well this project knows that a name is zero-argument and reachable by
/// <c>Ui.ExecuteScript</c>, in R6's own evidence grammar.
/// </summary>
public enum ScriptEvidence : byte
{
    /// <summary>
    /// <b>[P]</b> - R6 read it: the method is in <c>lua\executescript-reachable.txt</c> (a
    /// byte-exact walk of all 586 <c>ScriptsBase.bin</c> prototypes) <i>and</i> R6 §1.5 or §2.1
    /// names it as one of the useful ones, or the client's own C++ passes the literal to the Lua
    /// invoker.
    /// </summary>
    Proven = 0,

    /// <summary>
    /// <b>[I]</b> - inferred from a proven neighbour: the method is in the same reachable dump with
    /// the same arity, but R6 does not name it - typically the <c>Hide*</c> half of a <c>Show*</c>
    /// pair R6 does name.
    /// </summary>
    Inferred = 1,
}

/// <summary>One allow-listed row: a short word the owner types and the Lua name it sends.</summary>
/// <param name="Alias">The word after <c>/win</c>. Lower-case ASCII, never a reserved sub-verb.</param>
/// <param name="Script">The <c>Object.Method</c> the <c>1a 07</c> carries.</param>
/// <param name="Tier">The lowest console tier allowed to send it.</param>
/// <param name="Note">One line for <c>/win list</c> and the docs table.</param>
/// <param name="Evidence">[P] or [I] that the method is zero-arg and reachable (R6 §1.5).</param>
public sealed record WindowScript(
    string Alias,
    string Script,
    ConsoleTier Tier,
    string Note,
    ScriptEvidence Evidence)
{
    /// <summary><c>[P]</c> or <c>[I]</c>, as R6 writes it.</summary>
    public string Mark => Evidence == ScriptEvidence.Proven ? "[P]" : "[I]";
}

/// <summary>
/// The curated set of client windows the server may open with <c>Ui.ExecuteScript</c>
/// (<c>1a 07 | String8 "Object.Method" | u32 0</c>), and the names it will never send.
/// <para>
/// <b>Where the list comes from.</b> R6 (<c>out\devconsole-20260901\R6-graphical-menu.md</c>)
/// decompiled the client's whole Lua layer - <c>UI\ScriptsBase.bin</c>, 586 Lua 5.1 prototypes,
/// parsed byte-exactly to EOF - and enumerated the <b>238 methods whose signature is exactly
/// <c>(self)</c></b> (<c>lua\executescript-reachable.txt</c>). Those are the ones the <c>1a 07</c>
/// invoker can call, because the packet's argument list is a counted run of <c>u32</c> and nothing
/// else: the marshaller <c>FUN_140b7fdf0</c> stamps tag 1 (integer) unconditionally, so no string
/// can ever reach a script from the wire (R6 §1.4). Every row below is one of those 238.
/// </para>
/// <para>
/// <b>Why the reply cannot be trusted.</b> The invoker <c>FUN_140ba89e0</c> does
/// <c>lua_getglobal(table)</c> then <c>lua_getfield(method)</c> and bails at <c>LAB_140ba8c1a</c>
/// with <b>no print at all</b> when either lookup has the wrong type. A misspelt name is therefore
/// <i>silent</i>: only a method that resolves and then throws prints anything, and even then it
/// prints into the client's pane, not back to us (<c>nresults = 0</c>, R6 §1.2). That is why
/// <c>/win</c>'s reply says what was sent and how many bytes, and then tells the owner to watch the
/// screen - and why <see cref="ProbeSteps"/> exists at all.
/// </para>
/// <para>
/// <b>Nothing here is clicked yet.</b> Every row is [P]/[I] for <i>arity and reachability</i>; that
/// the client is in a state where the window actually appears is [U] until the owner's first click
/// (R6 §2.3, §5.4). Several are gated on <c>HudHandler.IsDisabledSWFs</c> and on the current
/// <c>GameStates</c> value.
/// </para>
/// </summary>
public static class WindowScripts
{
    /// <summary>The words <c>/win</c> keeps for itself; no alias may take one.</summary>
    public static IReadOnlyList<string> ReservedVerbs { get; } = ["list", "raw", "probe"];

    /// <summary>
    /// The longest <c>Object.Method</c> <c>/win raw</c> will send. The client copies the name into a
    /// 512-byte stack buffer bounded at <c>local_238 + 0x1ff</c> (<c>FUN_140ba89e0</c>); 128 is far
    /// inside that and is longer than any name in <c>ScriptsBase.bin</c>.
    /// </summary>
    public const int MaximumScriptLength = 128;

    /// <summary>
    /// The curated allow-list, in the order <c>/win list</c> prints it.
    /// <para>
    /// The shape is R6 §4.1's sketch (<c>/win inv</c>, <c>/win hud off</c>, <c>/win console</c>)
    /// flattened into one word per row, because a row is also a menu leaf and a menu leaf binds one
    /// fixed argument tail.
    /// </para>
    /// </summary>
    public static IReadOnlyList<WindowScript> All { get; } =
    [
        // --- inventory: R6's own first click (C1/C2) ------------------------------------------
        new("inventory", "HudHandler.ShowInventory", ConsoleTier.Tester,
            "the real inventory window (R6 §1.5, §2.1)", ScriptEvidence.Proven),
        new("inventoryoff", "HudHandler.HideInventory", ConsoleTier.Tester,
            "closes it again", ScriptEvidence.Proven),
        new("invtoggle", "GameEvents.OnInventoryToggle", ConsoleTier.Tester,
            "the client's own C++ passes this literal to the invoker - first click C1",
            ScriptEvidence.Proven),

        // --- settings: R6 §2.1 calls it the closest thing in the client to a mod menu ---------
        new("settings", "HudHandler.ShowSettings", ConsoleTier.Tester,
            "the settings screen - left rail, option rows, toggles", ScriptEvidence.Proven),
        new("settingsoff", "HudHandler.HideSettings", ConsoleTier.Tester,
            "closes it", ScriptEvidence.Inferred),
        new("keys", "GameEvents.ShowKeyBindings", ConsoleTier.Tester,
            "the key-binding screen - first click C7", ScriptEvidence.Proven),

        // --- escape / map --------------------------------------------------------------------
        new("escape", "HudHandler.ShowEscapeMenu", ConsoleTier.Tester,
            "the pause menu", ScriptEvidence.Proven),
        new("escapeoff", "HudHandler.HideEscapeMenu", ConsoleTier.Tester,
            "closes it", ScriptEvidence.Inferred),
        new("maptoggle", "GameEvents.OnMapToggle", ConsoleTier.Tester,
            "the map, by the name the client's own C++ calls", ScriptEvidence.Proven),
        new("mapoff", "HudHandler.HideMapWindow", ConsoleTier.Tester,
            "closes the map (ShowMapWindow takes 2 args, so it is out of reach)",
            ScriptEvidence.Proven),

        // --- the HUD itself: the fastest visible signal (R6 first click C6) -------------------
        new("hud", "HudHandler.Show", ConsoleTier.Tester,
            "the whole HUD back", ScriptEvidence.Proven),
        new("hudoff", "HudHandler.Hide", ConsoleTier.Tester,
            "the whole HUD away - first click C6", ScriptEvidence.Proven),
        new("hideall", "GameEvents.HideAll", ConsoleTier.Tester,
            "every UI element away (the client's own cinematic path)", ScriptEvidence.Inferred),
        new("restoreall", "GameEvents.RestoreAll", ConsoleTier.Tester,
            "puts them all back", ScriptEvidence.Inferred),
        new("loadingoff", "LoadingScreenHandler.Hide", ConsoleTier.Tester,
            "forces the loading screen away", ScriptEvidence.Proven),

        // --- the console pane: R5's open question, answered from the server side --------------
        new("console", "Console.Show", ConsoleTier.Tester,
            "opens the console pane - the R5 gate, first click C5", ScriptEvidence.Proven),
        new("consoleoff", "Console.Hide", ConsoleTier.Tester,
            "closes the pane - first click C4", ScriptEvidence.Proven),
        new("conunlock", "ConsoleWrapper.UnlockConsole", ConsoleTier.Owner,
            "the console lock R5 was chasing; try it if Console.Show does nothing",
            ScriptEvidence.Proven),
        new("conlock", "ConsoleWrapper.LockConsole", ConsoleTier.Owner,
            "locks it again", ScriptEvidence.Proven),

        // --- the other HudHandler windows, each a Show/Hide pair ------------------------------
        new("deaths", "HudHandler.ShowDeathList", ConsoleTier.Tester,
            "the death list", ScriptEvidence.Proven),
        new("deathsoff", "HudHandler.HideDeathList", ConsoleTier.Tester,
            "closes it", ScriptEvidence.Inferred),
        new("rewards", "HudHandler.ShowRewards", ConsoleTier.Tester,
            "the rewards window (GameEvents.ShowRewards takes 6 args)", ScriptEvidence.Proven),
        new("rewardsoff", "HudHandler.HideRewards", ConsoleTier.Tester,
            "closes it", ScriptEvidence.Inferred),
        new("grinder", "HudHandler.ShowGrinder", ConsoleTier.Tester,
            "the grinder (GameEvents.ShowGrinder takes 1 arg)", ScriptEvidence.Proven),
        new("grinderoff", "HudHandler.HideGrinder", ConsoleTier.Tester,
            "closes it", ScriptEvidence.Inferred),
        new("credits", "HudHandler.ShowCredits", ConsoleTier.Tester,
            "the credits screen", ScriptEvidence.Proven),
        new("creditsoff", "HudHandler.HideCredits", ConsoleTier.Tester,
            "closes it", ScriptEvidence.Inferred),
        new("notes", "HudHandler.ShowNotes", ConsoleTier.Tester,
            "the notes window", ScriptEvidence.Proven),
        new("notesoff", "HudHandler.HideNotes", ConsoleTier.Tester,
            "closes it", ScriptEvidence.Inferred),
        new("builder", "HudHandler.ShowBuilder", ConsoleTier.Tester,
            "the builder window", ScriptEvidence.Proven),
        new("builderoff", "HudHandler.HideBuilder", ConsoleTier.Tester,
            "closes it", ScriptEvidence.Inferred),
        new("container", "HudHandler.ShowContainer", ConsoleTier.Tester,
            "the container window", ScriptEvidence.Proven),
        new("containeroff", "HudHandler.HideContainer", ConsoleTier.Tester,
            "closes it", ScriptEvidence.Inferred),

        // --- GameEvents screens R6 §1.5 names -------------------------------------------------
        new("respawnmap", "GameEvents.openRespawnMap", ConsoleTier.Tester,
            "the respawn map", ScriptEvidence.Proven),
        new("browser", "GameEvents.ShowBrowser", ConsoleTier.Tester,
            "the in-game browser window", ScriptEvidence.Proven),
        new("browseroff", "GameEvents.HideBrowser", ConsoleTier.Tester,
            "closes it", ScriptEvidence.Inferred),
        new("helpscreen", "HelpHandler.Show", ConsoleTier.Tester,
            "the in-game help screen (Main.wndHelpScreen)", ScriptEvidence.Inferred),
        new("helpscreenoff", "HelpHandler.Hide", ConsoleTier.Tester,
            "closes it", ScriptEvidence.Inferred),

        // --- menu-shaped screens R6 §2.1 lists (category rail + tiled rows) -------------------
        new("market", "MarketplaceHandler.Show", ConsoleTier.Tester,
            "the marketplace - a real category+row model (R6 §2.1)", ScriptEvidence.Inferred),
        new("marketoff", "MarketplaceHandler.Hide", ConsoleTier.Tester,
            "closes it", ScriptEvidence.Inferred),

        // --- chat pane ------------------------------------------------------------------------
        new("chattabs", "ChatHandler.CycleChatTabs", ConsoleTier.Tester,
            "cycles the chat tabs", ScriptEvidence.Proven),

        // --- OUR OWN Lua table: R6 recommendation (b'), the graphical menu ---------------------
        // These five are the only rows in this table that are not in the client's shipped
        // ScriptsBase.bin. They resolve only when Client\Resources\Scripts\CranberryMenu.lua has
        // been loaded (see that file's header for the one-line bootstrap); until then they take the
        // invoker's silent LAB_140ba8c1a arm and do nothing at all - the same failure mode as a
        // misspelt name, which is why "menuping" exists as their own positive control.
        new("menu", "CranberryMenu.Toggle", ConsoleTier.Tester,
            "the Cranberry graphical menu on/off (needs CranberryMenu.lua)", ScriptEvidence.Inferred),
        new("menuon", "CranberryMenu.Open", ConsoleTier.Tester,
            "opens it and asks the server for a frame", ScriptEvidence.Inferred),
        new("menuoff", "CranberryMenu.Close", ConsoleTier.Tester,
            "closes it", ScriptEvidence.Inferred),
        new("menuping", "CranberryMenu.Ping", ConsoleTier.Tester,
            "one console line proving our own table is in _G (R6 §5.5 D2)", ScriptEvidence.Inferred),
        new("menudiag", "CranberryMenu.Diag", ConsoleTier.Owner,
            "prints what the script can and cannot see, one line each", ScriptEvidence.Inferred),
        new("menureload", "CranberryMenu.Reload", ConsoleTier.Owner,
            "re-runs the .lua from disk - hot reload without touching the client", ScriptEvidence.Inferred),
    ];

    /// <summary>
    /// The names <c>/win</c> refuses outright - allow-list <b>and</b> <c>/win raw</c>. These end the
    /// session, throw the player out of the world, or destroy something the owner cannot get back.
    /// <para>
    /// <c>Ui.Logout</c> and <c>Ui.Quit</c> are native <c>Ui</c> methods that end the session outright
    /// (R6 §1.5: "never put those in a table a command can reach").
    /// <c>SettingsHandler.SetAllControlsToDefault</c> and <c>.SetProfileToDefault</c> are marked
    /// <b>destructive - never send</b> in R6's own table: they wipe the owner's key bindings.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Denied { get; } =
    [
        "Ui.Logout",
        "Ui.Quit",
        "Ui.ConvertToSpectator",
        "GameEvents.OnPlayerLogout",
        "GameEvents.UnloadAll",
        "CharacterSelectHandler.OnForcedDisconnect",
        "SettingsHandler.SetAllControlsToDefault",
        "SettingsHandler.SetProfileToDefault",
        "Console.ClearHistory",
        "ConsoleWrapper.ClearConsoleHistory",
        "ConsoleWrapper.ClearConsoleLogFile",
        "ChatHandler.ResetChatHistory",
        "ChatChannelGroup.ResetLog",
    ];

    /// <summary>
    /// Substrings that deny a name nobody has enumerated yet - the <c>/win raw</c> tail of
    /// <see cref="Denied"/>. Matched case-insensitively against the whole <c>Object.Method</c>, so a
    /// name this project has never seen still cannot log the owner out or wipe his profile.
    /// </summary>
    public static IReadOnlyList<string> DeniedWords { get; } =
    [
        "logout", "quit", "exit", "disconnect", "delete", "todefault", "unloadall",
        "clearhistory", "clearconsole", "resetchat", "resetlog",
    ];

    /// <summary>The R6 §5.4 first-click triple, in order, one second apart (<c>/win probe</c>).</summary>
    public static IReadOnlyList<(string Script, string Why)> ProbeSteps { get; } =
    [
        ("GameEvents.OnInventoryToggle",
            "positive control - the client's own C++ calls this exact name; the inventory should open or close"),
        ("Cranberry.NoSuchMethod",
            "negative control - no such global table; nothing at all must happen, not even a console line"),
        ("HudHandler.Hide", "the whole HUD should vanish"),
        ("HudHandler.Show", "the whole HUD should come back"),
    ];

    /// <summary>The row this alias names, or null. Case-insensitive.</summary>
    public static WindowScript? ByAlias(string? alias) =>
        alias is null
            ? null
            : All.FirstOrDefault(row => string.Equals(row.Alias, alias, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every alias, in list order.</summary>
    public static IReadOnlyList<string> Aliases => [.. All.Select(row => row.Alias)];

    /// <summary>
    /// True when this <c>Object.Method</c> must never go out. <paramref name="why"/> names the rule
    /// that refused it, so the console line explains itself.
    /// </summary>
    public static bool IsDenied(string? script, out string why)
    {
        why = string.Empty;
        if (string.IsNullOrWhiteSpace(script))
        {
            return false;
        }

        string name = script.Trim();
        foreach (string denied in Denied)
        {
            if (string.Equals(denied, name, StringComparison.OrdinalIgnoreCase))
            {
                why = $"'{denied}' ends the session or destroys something the owner cannot get back";
                return true;
            }
        }

        foreach (string word in DeniedWords)
        {
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                why = $"the name contains '{word}' -- /win never sends anything that "
                    + "exits, disconnects or deletes";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the name has the shape the invoker needs: <c>Identifier(.Identifier)+</c>, ASCII,
    /// no spaces, no parentheses, no arguments.
    /// <para>
    /// The <c>1a 07</c> handler <c>FUN_1412caa00</c> case 7 splits the wire string at the first
    /// <c>.</c> and builds <c>part1 + ":" + part2</c>; it only proceeds when <b>both parts are
    /// non-empty</b>, so a bare word or a trailing dot is rejected by the client before anything
    /// runs. A parenthesis or a comma would be carried into the name and could never resolve.
    /// </para>
    /// </summary>
    public static bool IsWellFormed(string? script, out string why)
    {
        why = string.Empty;
        if (string.IsNullOrWhiteSpace(script))
        {
            why = "give an Object.Method name";
            return false;
        }

        string name = script.Trim();
        if (name.Length > MaximumScriptLength)
        {
            why = $"'{name[..16]}...' is {name.Length} characters; the limit is {MaximumScriptLength}";
            return false;
        }

        string[] parts = name.Split('.');
        if (parts.Length < 2)
        {
            why = $"'{name}' has no dot -- the 1a 07 handler needs Object.Method";
            return false;
        }

        foreach (string part in parts)
        {
            if (part.Length == 0)
            {
                why = $"'{name}' has an empty part -- the client requires both halves non-empty";
                return false;
            }

            if (!char.IsAsciiLetter(part[0]) && part[0] != '_')
            {
                why = $"'{name}' is not Identifier.Identifier";
                return false;
            }

            foreach (char c in part)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                {
                    why = $"'{name}' holds '{c}' -- names are letters, digits and underscores only "
                        + "(no spaces, no parentheses, no arguments)";
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// How many bytes the <c>1a 07</c> for this name and these integers will be: two header bytes,
    /// the <c>u32</c> string length, the UTF-8 text, the <c>u32</c> argument count and four bytes an
    /// argument. <c>Console.Show</c> with no arguments is 22, the number
    /// <c>ConsolePacketTests</c> pins.
    /// </summary>
    public static int ByteLength(string script, int arguments = 0)
    {
        ArgumentNullException.ThrowIfNull(script);
        return 2 + 4 + System.Text.Encoding.UTF8.GetByteCount(script) + 4 + (4 * arguments);
    }
}
