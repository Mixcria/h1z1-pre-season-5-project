using Cranberry.Protocol;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Gas;

namespace Cranberry.Zone.Match;

/// <summary>
/// The end of a match on the wire: the <c>0xce GameMode</c> subs that report a placement, the
/// alive count, the victory screen and the trip back to the menu.
///
/// <para>
/// <b>Reuse, not duplication.</b> Two of the four writers this lane needs already exist and are
/// already general, so this class routes to them instead of writing a second copy of the same
/// bytes: <c>ce 04</c> is <see cref="GasPackets.DeathInfo"/> (a standalone record, not something
/// buried in <c>ZoneService</c>) and <c>ce 09</c> is
/// <see cref="GameModeHud.WritePlayersRemaining"/>. What is new here is the <b>vocabulary</b> —
/// the named <c>ce 04</c> shapes an ending match sends — plus the two writers that did not exist:
/// <see cref="ShowVictoryScreen"/> (<c>ce 18</c>) and <see cref="DeathTimer"/> (<c>ce 12</c>).
/// </para>
///
/// <para>
/// <b>Dispatcher.</b> Base <c>0xce</c> is unregistered; the router special-cases it to
/// <c>FUN_140bba510(DAT_143f69da0)</c>. Its full switch is dumped at
/// <c>out\gas-safezone-research\ce-dispatch\_140bba510\FUN_140bba510_140bba510.c</c> — the
/// sub is read as a <b><c>u16</c></b> at <c>:52</c> and switched at <c>:56</c>; every case in
/// this file cites its own handler below.
/// </para>
///
/// <para>
/// <b>Sub-opcodes this family carries that are relevant to an ending match, and what they are.</b>
/// <list type="table">
/// <item><term><c>ce 04</c></term><description>DeathInfo — rank + cause. Handler
/// <c>FUN_140bbb120</c>, body reader <c>FUN_140bb0cc0</c>; <see cref="GasPackets.DeathInfo"/>,
/// docs/18 §3.</description></item>
/// <item><term><c>ce 09</c></term><description>PlayersRemaining — <c>i32</c>, 7 B, dispatcher case
/// 9 inline at <c>FUN_140bba510:70-113</c>; <c>−1</c> hides the counter and the value <c>2</c>
/// triggers <c>KOTK_MX_COMBAT_FINAL</c> (<c>:98-104</c>).
/// <see cref="GameModeHud.WritePlayersRemaining"/>.</description></item>
/// <item><term><c>ce 12</c></term><description><b>DeathTimer</b>, <c>u32</c>, 7 B — handler
/// <c>FUN_140bb6b50</c>. Reads <c>u8; u16; u32</c> (<c>:24-44</c>, the cursor advances
/// <c>puVar7 + 2</c> over a <c>ushort*</c> = 4 bytes) exact-length gated at <c>:45-46</c>, then
/// sets the audio/telemetry parameter interned as <b><c>"DEATH_TIMER"</c></b>
/// (<c>:49-50</c>). Implemented as <see cref="DeathTimer"/>; nothing drives it yet.</description></item>
/// <item><term><c>ce 15</c></term><description>One <c>bool</c>, handler <c>FUN_140bba020</c> —
/// the game-mode singleton's in-match flag. <b>Already implemented</b> as
/// <see cref="GameModeHud.WriteInMatchState"/>; a new lobby clears it.</description></item>
/// <item><term><c>ce 18</c></term><description><b>ShowVictoryScreen</b> — see
/// <see cref="ShowVictoryScreen"/>. <b>7 bytes, not 4</b>: docs/15 §7 is wrong, see that
/// record.</description></item>
/// <item><term><c>ce 1a</c></term><description>Unnamed. <c>u8; u16; u8</c> = <b>4 bytes</b>
/// (handler <c>FUN_140bbb020</c>, <c>char*</c> cursor throughout: <c>:17-35</c> then the
/// <c>*pcVar2 != '\0'</c> bool at <c>:42</c>), exact-length gated at <c>:37-38</c>, forwarded to
/// <c>FUN_1413dcc20</c> to populate the current world's MatchEndWindow panel table. It works
/// with an active group; unlike <c>ce 18</c>, it has no group exclusion. Implemented by
/// <see cref="PrepareTeamResultScreen"/> before the finished team score raises the UI event.
/// The parsed bool is unused by the table builder; see docs/team-end-panels-20260906.md.
/// </description></item>
/// <item><term><c>ce 1b</c></term><description>LeaveMatch — see <see cref="LeaveMatch"/>.</description></item>
/// </list>
/// </para>
/// </summary>
public static class EndgamePackets
{
    /// <summary>The GameMode / BR-HUD family base opcode.</summary>
    public const byte Opcode = GameModeHud.Opcode;

    /// <summary>
    /// The <c>ce 04</c> a player who was killed by another player receives.
    /// <paramref name="rankIndex"/> is a <b>0-based placement</b> — the client draws
    /// <c>rankIndex + 1</c> (<c>FUN_140bbb120:71-101</c>, docs/18 §3a) — and the owner's server
    /// fills it with the number of participants still alive <i>after</i> the death
    /// (Z1 <c>ZoneEndgame.SendDeathInfoOnce</c>, <c>ZoneEndgame.cs:363-380</c>).
    /// <para>
    /// <c>SourceId</c> stays 0 so the client does not walk its own damage-source table
    /// (<c>FUN_140bbb120:155-191</c>); the killer is named by the <paramref name="killerName"/>
    /// string, which is what the "%0 killed you…" sentence (12604) needs to render at all.
    /// <paramref name="killerHealth"/> is <c>ce 04</c>'s <c>Field4</c>: the owner's server puts
    /// the killer's remaining health there, which is the only reading consistent with 12604's
    /// "%0 had %2 health left." — <b>[I]</b>, the field's own consumer is unrecovered (docs/18
    /// §3a), so 0 is the safe value and the caller opts in.
    /// </para>
    /// </summary>
    public static GasPackets.DeathInfo KilledByPlayer(
        int rankIndex,
        string killerName,
        uint killerHealth = 0) =>
        new(
            Rank: Math.Max(0, rankIndex),
            Draw: false,
            Killer: killerName ?? string.Empty,
            Field4: killerHealth,
            SourceId: 0,
            Cause: (uint)DeathCauseCode.GameModeKillCondition);

    /// <summary>
    /// The <c>ce 04</c> for a death with no second party — gas, a fall, a vehicle, the match
    /// clock. Empty killer name and <c>SourceId = 0</c>, so the client derives the source name
    /// from the cause where the cause has one (<c>FUN_140bbb120:102-152</c>).
    /// </summary>
    public static GasPackets.DeathInfo Environmental(int rankIndex, DeathCauseCode cause) =>
        new(
            Rank: Math.Max(0, rankIndex),
            Draw: false,
            Killer: string.Empty,
            Field4: 0,
            SourceId: 0,
            Cause: (uint)cause);

    /// <summary>
    /// The <c>ce 04</c> that makes the winner's wrap-up slides say <b>"You won!"</b> — rank 0
    /// (drawn as "#1") and cause <see cref="DeathCauseCode.EndOfMatchWinner"/> (<c>0x48</c>,
    /// locale 12600). It rides with <see cref="ShowVictoryScreen"/>: without it the slide prints
    /// whatever the last <c>ce 04</c> left in that field, which for a player who never died is an
    /// empty string (Z1 <c>ZoneEndgame.VictoryPacketsFor</c>, <c>ZoneEndgame.cs:419-445</c>).
    /// </summary>
    public static GasPackets.DeathInfo Winner() =>
        new(
            Rank: 0,
            Draw: false,
            Killer: string.Empty,
            Field4: 0,
            SourceId: 0,
            Cause: (uint)DeathCauseCode.EndOfMatchWinner);

    /// <summary>
    /// The <c>ce 04</c> for somebody still alive when the match ended without winning — a forced
    /// end, or a match clock. Cause <see cref="DeathCauseCode.EndOfMatchRemaining"/> (12595) or
    /// <see cref="DeathCauseCode.EndOfMatchExpired"/> (12594) for the timer variant
    /// (Z1 <c>ZoneEndgame.SurvivorEndPacketsFor</c>).
    /// </summary>
    public static GasPackets.DeathInfo Survivor(
        int rankIndex,
        DeathCauseCode cause = DeathCauseCode.EndOfMatchRemaining) =>
        Environmental(rankIndex, cause);

    /// <summary>
    /// The <c>ce 04</c> of a draw — two deaths on the same tick. <c>Draw</c> is the UI boolean at
    /// object <c>+0x1c</c> (<c>FUN_140bbb120:84-87</c>); the client prints "Draw." (12609) and
    /// "The match ended in a draw…" (14944). S4 row E6.
    /// </summary>
    public static GasPackets.DeathInfo Draw(int rankIndex, DeathCauseCode cause) =>
        new(
            Rank: Math.Max(0, rankIndex),
            Draw: true,
            Killer: string.Empty,
            Field4: 0,
            SourceId: 0,
            Cause: (uint)cause);

    /// <summary>
    /// <c>ce 09 PlayersRemaining</c> — <b>7 bytes</b>, <c>i32</c>. Routes to the existing
    /// <see cref="GameModeHud.WritePlayersRemaining"/>; kept here so the endgame path has one
    /// vocabulary. <c>−1</c> hides the counter (the lobby's own value), and the dispatcher fires
    /// <c>KOTK_MX_COMBAT_FINAL</c> when the value is exactly <c>2</c>
    /// (<c>FUN_140bba510:98-104</c>). The owner's server sends it on change only (S4 row E7).
    /// </summary>
    public static void WriteAliveCount(PacketWriter writer, int alive)
    {
        ArgumentNullException.ThrowIfNull(writer);
        GameModeHud.WritePlayersRemaining(writer, alive);
    }

    /// <summary>Bytes <see cref="WriteAliveCount"/> writes: <c>1 + 2 + 4</c>.</summary>
    public const int AliveCountLength = 7;
}

/// <summary>
/// Legacy name for the solo result-panel preparation packet, <c>ce 18</c>.
/// Handler <c>FUN_140bbbbc0</c> consumes exactly <c>u8; u16; u32</c> (7 bytes),
/// then calls <c>FUN_1413dcc20</c> to populate MatchEndWindow for the current world mode.
/// It returns early when the native active group ID is nonzero. Teams instead use
/// <see cref="PrepareTeamResultScreen"/> (<c>ce 1a</c>, exactly four bytes).
/// The consumed u32 is unused by the panel-population callee. Finished <c>67/08</c>,
/// not this helper, raises the victory/death UI event. See docs/team-endgame-20260906.md
/// for the corrected native evidence; the previous event-queue interpretation was wrong.
/// </summary>
/// <param name="Value">The structurally required, unused <c>u32</c>; retain zero.</param>
public sealed record ShowVictoryScreen(uint Value = 0)
{
    public const byte Opcode = EndgamePackets.Opcode;
    public const ushort SubOpcode = 0x0018;

    /// <summary><c>1 + 2 + 4</c>, and the handler's exact-length gate accepts no other.</summary>
    public const int Length = 7;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteUInt32(Value);
    }
}

/// <summary>
/// <c>GameMode.LeaveMatch</c> (<c>ce 1b</c>) — <b>3 bytes, header only</b>, <b>[P]</b>.
///
/// <para>
/// Handler <c>FUN_140bbbce0</c>
/// (<c>out\gas-safezone-research\ce-dispatch\_140bba510\FUN_140bbbce0_140bbbce0.c</c>): reads the
/// <c>u8</c> opcode at <c>:25-33</c> and the <c>u16</c> sub at <c>:35-36</c>, then requires
/// <c>(int)local_38 − (int)(puVar5 + 1) &lt; 1</c> at <c>:37</c> — <b>any payload byte silently
/// kills the packet</b> — and raises the event named by the literal <c>"EVENT_LEAVE_MATCH"</c> at
/// <c>:39-46</c> through <c>FUN_1411ee1d0(dispatcher, name, 0, 0, …)</c> with a null payload.
/// </para>
///
/// <para>
/// <b>It logs the client out.</b> <c>UIRoot.handleEventEndMatch</c> →
/// <c>cleanUpMatchData(); closeAllWindows(); UIBindingSystem.Logout()</c>: the client returns to
/// the title screen and has to log in again (S4 row E5). The owner ships his equivalent
/// <b>off</b> — <c>ZoneEndgame.LeaveMatchOnEnd = false</c> (<c>ZoneEndgame.cs:86</c>) — because
/// the re-login path was never tested, and Cranberry follows him (D150).
/// </para>
///
/// <para>Routes to the existing <see cref="GameModeHud.WriteLeaveMatch"/> rather than repeating it.</para>
/// </summary>
public sealed record LeaveMatch
{
    public const byte Opcode = EndgamePackets.Opcode;
    public const ushort SubOpcode = 0x001b;

    /// <summary><c>1 + 2</c>, and one more byte is one too many.</summary>
    public const int Length = 3;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        GameModeHud.WriteLeaveMatch(writer);
    }
}

/// <summary>
/// <c>GameMode.DeathTimer</c> (<c>ce 12</c>) — <b>7 bytes</b>, <b>[P]</b> body, <b>[I]</b> units.
///
/// <para>
/// Wire: <c>u8 ce; u16 0012; u32 value</c>. Handler <c>FUN_140bb6b50</c>
/// (<c>out\gas-safezone-research\ce-dispatch\_140bba510\FUN_140bb6b50_140bb6b50.c</c>):
/// <c>:24-32</c> the <c>u8</c> opcode, <c>:34-42</c> the <c>u16</c> sub, then <c>:43-44</c>
/// advances <c>puVar7 + 2</c> on a <c>ushort *</c> — <b>four bytes</b> — and reads an
/// <c>undefined4</c>. Exact-length gated at <c>:45-46</c>. The value is handed to
/// <c>FUN_1420ca210(audio, DAT_143d948a8, param, value)</c> where <c>param</c> is the interned
/// name <b><c>"DEATH_TIMER"</c></b> (<c>:47-50</c>) — i.e. it drives an audio/telemetry parameter,
/// the same mechanism <c>ce 04</c> uses for <c>KOTK_ELIMINATION_RANK</c>.
/// </para>
///
/// <para>
/// Implemented because the body is closed and it costs nothing; <b>nothing sends it yet</b>, and
/// what its units are (seconds? milliseconds? a countdown or a stamp?) is not derived — the
/// parameter is consumed inside the audio middleware.
/// </para>
/// </summary>
public sealed record DeathTimer(uint Value)
{
    public const byte Opcode = EndgamePackets.Opcode;
    public const ushort SubOpcode = 0x0012;

    /// <summary><c>1 + 2 + 4</c>.</summary>
    public const int Length = 7;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteUInt32(Value);
    }
}
