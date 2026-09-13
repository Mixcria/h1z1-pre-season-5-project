namespace Cranberry.Zone.DevConsole;

/// <summary>
/// Who may run a console command. Jiggy's permission ladder (R4 §1.5) collapsed to the three
/// levels this server can actually tell apart: where the packet came from, and what the owner
/// wrote in <c>CRANBERRY_CONSOLE_TIERS</c>.
/// <para>
/// The ladder is ordered, so a gate is one comparison: a command with
/// <see cref="ConsoleCommand.Tier"/> = <see cref="Tester"/> runs for a Tester and for an Owner and
/// is refused - out loud, never silently - for a Player (design §4.2).
/// </para>
/// </summary>
public enum ConsoleTier : byte
{
    /// <summary>Read-only rows: <c>/where</c>, <c>/inv</c>, <c>/cars</c>, <c>/match status</c>.</summary>
    Player = 0,

    /// <summary>Rows that change this player's own world: heal, teleport, give, spawn a car.</summary>
    Tester = 1,

    /// <summary>Rows that change the match or another player: start/end, gas, evict, <c>/raw</c>.</summary>
    Owner = 2,

    /// <summary>An administrator has the same authority as the server owner.</summary>
    Admin = Owner,
}

/// <summary>
/// The <see cref="MatchStep"/>-shaped phase gate a command declares. The engine compares it with
/// <c>ConsoleContext.MatchStep()</c>, which returns the live server value
/// (<c>ZoneService.cs:6675</c>: Menu, Queued, Transferring, Zoning, Lobby, Dropping, InMatch), so
/// the typed door and the menu refuse identically (design §4.2).
/// </summary>
public enum MatchGate : byte
{
    /// <summary>Runs in any phase, including before the player has a character.</summary>
    Any = 0,

    /// <summary>Only before the match starts - the step the client calls <c>Menu</c>.</summary>
    MenuOnly = 1,

    /// <summary>Only once the player is in the world: <c>InMatch</c>.</summary>
    InMatch = 2,

    /// <summary>Menu or Lobby - the two steps where a match can still be armed.</summary>
    LobbyOrMenu = 3,
}
