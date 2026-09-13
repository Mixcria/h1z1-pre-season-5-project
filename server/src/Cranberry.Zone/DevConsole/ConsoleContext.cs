using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone.DevConsole;

/// <summary>
/// The <see cref="MatchGate"/> arithmetic, in one place so the typed door and the menu refuse
/// identically. The step words are the live server's own
/// (<c>ZoneService.cs:6675</c>: Menu, Queued, Transferring, Zoning, Lobby, Dropping, InMatch).
/// </summary>
public static class MatchGates
{
    /// <summary>True when a command with this gate may run in this step.</summary>
    public static bool Allows(MatchGate gate, string? step) => gate switch
    {
        MatchGate.Any => true,
        MatchGate.MenuOnly => Is(step, "Menu"),
        MatchGate.InMatch => Is(step, "InMatch"),
        MatchGate.LobbyOrMenu => Is(step, "Menu") || Is(step, "Lobby"),
        _ => true,
    };

    /// <summary>What the menu draws in the value column of a row this step cannot run.</summary>
    public static string Marker(MatchGate gate) => gate switch
    {
        MatchGate.MenuOnly => "(Menu)",
        MatchGate.InMatch => "(InMatch)",
        MatchGate.LobbyOrMenu => "(Menu/Lobby)",
        _ => string.Empty,
    };

    /// <summary>The refusal line: <c>not now: Lobby (needs InMatch)</c> (design §2.3).</summary>
    public static string Refusal(MatchGate gate, string? step) =>
        $"not now: {(string.IsNullOrEmpty(step) ? "?" : step)} (needs {Needs(gate)})";

    private static string Needs(MatchGate gate) => gate switch
    {
        MatchGate.MenuOnly => "Menu",
        MatchGate.InMatch => "InMatch",
        MatchGate.LobbyOrMenu => "Menu or Lobby",
        _ => "any step",
    };

    private static bool Is(string? step, string want) =>
        string.Equals(step, want, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Everything a command may reach outside itself, as delegates. The engine, the menu and every
/// command body see only this type; <c>ZoneService.Console.cs</c> is the single place where these
/// delegates are bound to the facade's private members (design §4.6).
/// <para>
/// <b>A null delegate means "not available yet"</b>, not a crash: a command whose backend has not
/// been wired answers <c>- not available yet</c> and the menu greys its row. Every nullable session
/// member on the server side (fleet, doors, movement, inventory, gas, schedule) is passed as a
/// nullable accessor for the same reason - the backend answers <c>- no vehicles streamed</c>
/// instead of dereferencing null inside a packet handler.
/// </para>
/// <para>
/// <b>Extension point.</b> Lane C adds one init property here per R3 entry point it wires. Adding a
/// property is additive: nothing already written stops compiling, and an unwired backend keeps
/// answering honestly.
/// </para>
/// </summary>
public sealed class ConsoleContext
{
    /// <summary>Where this session's lines are drawn right now.</summary>
    public required IConsoleSurface Surface { get; init; }

    /// <summary>Builds a surface of a named kind - what <c>/surface probe</c> needs to hit all four.</summary>
    public Func<ConsoleSurfaceKind, IConsoleSurface>? SurfaceFor { get; init; }

    /// <summary>Puts raw bytes on gateway channel 0. The only route to the wire (<c>/raw</c>, backends).</summary>
    public Action<Action<PacketWriter>>? Send { get; init; }

    /// <summary>One <c>console:</c> Info line in the host log.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>One <c>console:</c> Warn line in the host log.</summary>
    public Action<string>? Warn { get; init; }

    /// <summary>The gateway remote, for the log line.</summary>
    public string Remote { get; init; } = "?";

    /// <summary>The character name.</summary>
    public string Name { get; init; } = "?";

    /// <summary>The character guid.</summary>
    public ulong Guid { get; init; }

    /// <summary>The tier resolved at login.</summary>
    public ConsoleTier Tier { get; init; } = ConsoleTier.Player;

    /// <summary>The live match step word; null before there is one.</summary>
    public Func<string>? MatchStep { get; init; }

    /// <summary>The player's position, or null before the first channel-2 record.</summary>
    public Func<Vector4?>? Position { get; init; }

    /// <summary>The player's yaw in degrees, or null.</summary>
    public Func<float?>? Yaw { get; init; }

    /// <summary>How many sessions are online, for the root <c>Players</c> row.</summary>
    public Func<int>? OnlinePlayers { get; init; }

    /// <summary>The server build string shown in the frame title, e.g. the git short hash.</summary>
    public string ServerVersion { get; init; } = "dev";

    /// <summary>The client build shown in the frame title.</summary>
    public string ClientBuild { get; init; } = "1148";

    /// <summary>Resolves a roster name to an item definition id; null when no roster is wired.</summary>
    public Func<string, int?>? ResolveItem { get; init; }

    /// <summary>Resolves a place name to a position; null when no place table is wired.</summary>
    public Func<string, Vector3?>? ResolvePlace { get; init; }

    /// <summary>
    /// The live right-hand note of a menu row, by <c>MenuNode.Id</c> - the root's
    /// <c>InMatch 04:12</c> and <c>1 online</c>. Null, or a null answer, leaves the column to the
    /// row's own kind.
    /// </summary>
    public Func<string, string?>? RowNote { get; init; }

    /// <summary>The live state of a toggle row the engine does not own itself, by node id.</summary>
    public Func<string, bool?>? RowToggle { get; init; }

    /// <summary>The live value of a value row the engine does not own itself, by node id.</summary>
    public Func<string, string?>? RowValue { get; init; }

    // --- Lane C: one property per R3 entry point actually wired ------------------------------
    // Every delegate below is bound in ZoneService.Console.cs and nowhere else. They return
    // ConsoleReply rather than a bare bool because the world knowledge that decides what to SAY -
    // which vehicle, how much fuel went in, how many doors were in range - lives on the server
    // side of the facade; the command bodies in DevConsole/Commands own the parsing, the defaults
    // and the usage lines. A null delegate is answered with "not available yet", never a crash.

    /// <summary>Moves the player: <c>ClientUpdate.UpdateLocation 11 0a</c> (R3 #2).</summary>
    public Func<Vector3, ConsoleReply>? Teleport { get; init; }

    /// <summary>Resolves a named place (<c>drop</c>, <c>staging</c>, a loot area) to a position.</summary>
    public Func<string, Vector3?>? Place { get; init; }

    /// <summary>Restores health; null heals to full. <c>11 01 Hitpoints</c> (R3 #17).</summary>
    public Func<uint?, ConsoleReply>? Heal { get; init; }

    /// <summary>Damages the player through <c>ApplyGasDamage</c>, the real damage path (R3 #16).</summary>
    public Func<uint, ConsoleReply>? Hurt { get; init; }

    /// <summary>Kills the player through <c>SendGasDeath</c> - the one death path (R3 #18).</summary>
    public Func<ConsoleReply>? Kill { get; init; }

    /// <summary>The bag: items, containers, loadout slots, bulk (R3 #28).</summary>
    public Func<ConsoleReply>? Inventory { get; init; }
    public Func<uint, uint, ConsoleReply>? GiveItem { get; init; }
    public Func<string, ConsoleReply>? GiveKit { get; init; }
    public Func<uint, ConsoleReply>? GiveAmmo { get; init; }
    public Func<string, ConsoleReply>? DropItem { get; init; }
    public Func<float, ConsoleReply>? Parachute { get; init; }
    public Func<float, ConsoleReply>? Speed { get; init; }
    public Func<ConsoleReply>? Players { get; init; }
    public Func<string, string, ConsoleReply>? Evict { get; init; }
    public Func<string, string, ConsoleReply>? SetTier { get; init; }
    public Func<string, ConsoleReply>? TeleportHere { get; init; }
    public Func<ConsoleReply>? CombatStatus { get; init; }
    public Func<ConsoleReply>? Places { get; init; }
    public Func<string, int, ConsoleReply>? FindPlaces { get; init; }
    public Func<string, ConsoleReply>? TeleportPlace { get; init; }


    /// <summary>Spawns a vehicle beside the player by roster word (R3 #6).</summary>
    public Func<string, ConsoleReply>? SpawnVehicle { get; init; }

    /// <summary>Seats the player in the nearest streamed vehicle (R3 #7).</summary>
    public Func<ConsoleReply>? EnterVehicle { get; init; }

    /// <summary>Takes the player out of the vehicle (R3 #7).</summary>
    public Func<ConsoleReply>? ExitVehicle { get; init; }

    /// <summary>Refuels the nearest vehicle to a percentage of its tank (R3 #8).</summary>
    public Func<float, ConsoleReply>? Refuel { get; init; }

    /// <summary>The fleet census: planned, streamed, occupied (R3 #6).</summary>
    public Func<ConsoleReply>? VehicleCensus { get; init; }

    /// <summary>Puts an item on the ground in front of the player (R3 #5).</summary>
    public Func<uint, uint, ConsoleReply>? SpawnLoot { get; init; }

    /// <summary>The developer ring of sample items around the player (R3 #5).</summary>
    public Func<ConsoleReply>? SpawnLootRing { get; init; }

    /// <summary>Finds streamed ground loot within a radius, nearest first.</summary>
    public Func<string, float, ConsoleReply>? FindLoot { get; init; }

    /// <summary>Ground-loot counters for this match.</summary>
    public Func<ConsoleReply>? LootStats { get; init; }

    /// <summary>
    /// D274: this match's airdrop state - what has been announced, what is in flight, what is
    /// standing and when the next crate is due. Read-only; it schedules nothing.
    /// </summary>
    public Func<ConsoleReply>? AirdropStats { get; init; }

    /// <summary>Starts the match now, from the Menu step (R3 #12).</summary>
    public Func<ConsoleReply>? StartMatch { get; init; }

    /// <summary>Server-side reset back to a fresh lobby (R3 #14).</summary>
    public Func<bool, ConsoleReply>? AbandonMatch { get; init; }

    /// <summary>Step, seed, ring, hp, loot, fleet, doors (R3 #15).</summary>
    public Func<ConsoleReply>? MatchStatus { get; init; }

    /// <summary>Hosted-game keys, invitations and lifecycle, with per-action authorization.</summary>
    public Func<CommandCall, ConsoleReply>? HostedGame { get; init; }

    /// <summary>The gas sub-verbs <c>start</c> / <c>stop</c> / <c>status</c> (R3 #9).</summary>
    public Func<string, ConsoleReply>? Gas { get; init; }

    /// <summary>Doors near the player: <c>open</c> / <c>close</c> / <c>toggle</c> (R3 #25).</summary>
    public Func<string, float, ConsoleReply>? Doors { get; init; }

    /// <summary>Practice targets: <c>spawn</c> / <c>status</c> (R3 #21).</summary>
    public Func<string, ConsoleReply>? PracticeTarget { get; init; }
    public Func<string, int, string, float, ConsoleReply>? Bots { get; init; }

    public Func<string, ConsoleReply>? Rank { get; init; }

    /// <summary>One <c>ClientUpdate.TextAlert 11 31</c> banner (R3 #29).</summary>
    public Func<string, ConsoleReply>? Announce { get; init; }

    /// <summary>Server-side dumps: <c>pos</c> / <c>inv</c> / <c>self</c>.</summary>
    public Func<string, int, ConsoleReply>? Dump { get; init; }

    /// <summary>The client-progress watchdog's current view (docs/35).</summary>
    public Func<ConsoleReply>? Watchdog { get; init; }

    /// <summary>Which capture file this session is being written to.</summary>
    public Func<ConsoleReply>? WireLog { get; init; }

    /// <summary>Puts raw bytes on gateway channel 0. The one deliberately dangerous row.</summary>
    public Func<byte[], ConsoleReply>? Raw { get; init; }

    /// <summary>
    /// Sends one <c>Ui.ExecuteScript</c> (<c>1a 07 | String8 "Object.Method" | u32 k | k x u32</c>)
    /// and answers the line that says what went out (R6 §1.5, docs/103 §11).
    /// <para>
    /// The client never acknowledges it: a failed lookup returns with no print
    /// (<c>FUN_140ba89e0</c> <c>LAB_140ba8c1a</c>) and the caller discards the Lua return value
    /// (<c>nresults = 0</c>). So the reply describes the <b>send</b>, never the effect.
    /// </para>
    /// </summary>
    public Func<string, IReadOnlyList<uint>, ConsoleReply>? UiScript { get; init; }

    /// <summary>
    /// Runs work on the listener thread after a delay - the server's own <c>Later</c>. False means
    /// the work was refused because no dispatcher is configured, which is the honest answer in a
    /// unit test; <c>/win probe</c> then sends its steps immediately and says so.
    /// </summary>
    public Func<int, Action, bool>? Later { get; init; }

    /// <summary>Build, options, registry and match, for the read-only Info frame.</summary>
    public Func<IReadOnlyList<string>>? InfoLines { get; init; }

    /// <summary>The current match step word, or <c>"?"</c> when nothing is wired.</summary>
    public string Step() => MatchStep?.Invoke() ?? "?";

    /// <summary>Writes one Info line, if a log is wired.</summary>
    public void Info(string message) => Log?.Invoke(message);

    /// <summary>Writes one Warn line, if a log is wired.</summary>
    public void Trouble(string message) => Warn?.Invoke(message);
}
