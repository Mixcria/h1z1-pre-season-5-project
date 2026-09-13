using Cranberry.Protocol;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Match;

/// <summary>
/// The banner sentences an ending match broadcasts — eliminations, the winner, the celebration
/// clock — on the same <c>ClientUpdate.TextAlert</c> channel the gas already uses.
///
/// <para>
/// <b>This is <see cref="GasAlerts"/> generalised, not re-derived.</b> The wire work was done
/// there and is unchanged: base <c>0x11</c> <c>cPacketIdClientUpdateBase</c>, sub <c>0x0031</c>
/// <c>cClientUpdatePacketIdTextAlert</c>, dispatcher <c>FUN_140afc660</c> case <c>0x31</c>
/// (<c>out\gas-safezone-research\clientupdate-dispatch\_140afc660\FUN_140afc660_140afc660.c:1793</c>),
/// which installs exactly one <c>SoeUtil::IString&lt;char&gt;</c> and displays it — no other field
/// is read. <see cref="Write"/> and <see cref="LengthOf"/> here call straight into
/// <see cref="GasAlerts"/> so there is one writer, one length rule and one placeholder expander
/// for every alert this server sends.
/// </para>
///
/// <para>
/// <b>TextAlert carries a string, not a locale id</b>, so the client's <c>#count([*slot0*])</c>
/// placeholders are expanded here, server-side, exactly as the owner's server does it
/// (Z1 <c>ZoneEndgame.cs:496-505</c>). The expansion itself lives once, in
/// <see cref="GasAlerts.ExpandCount(string, int)"/> / <see cref="GasAlerts.ExpandSlot"/>.
/// </para>
/// <para>
/// <b>Lane 2B (docs/104): no id and no sentence is typed in this file any more.</b> Both come
/// from <see cref="AugustStrings"/>, which <c>tools/data/derive_strings.py</c> resolves out of the
/// client's own <c>CodeStringMappings.txt</c> (<c>BR.RemainingPlayers</c>,
/// <c>BR.AnnounceWinner</c>, <c>BR.EndingWinnerCelebration</c>, <c>BR.PlayersConnected</c>) and
/// <c>Locale\en_us_data.dat</c> in one pass, so the id and the English beside it cannot drift.
/// The produced bytes are unchanged and <c>MatchAlertTests</c> pins them against the literals
/// this file used to carry.
/// </para>
/// </summary>
public static class MatchAlerts
{
    /// <summary>Base opcode <c>0x11</c> — see <see cref="GasAlerts.Family"/>.</summary>
    public const byte Family = GasAlerts.Family;

    /// <summary>Sub-opcode <c>0x0031</c> — see <see cref="GasAlerts.SubOpcode"/>.</summary>
    public const ushort SubOpcode = GasAlerts.SubOpcode;

    /// <summary>Bytes before the text: <c>u8</c> base, <c>u16</c> sub, <c>u32</c> length.</summary>
    public const int HeaderLength = GasAlerts.HeaderLength;

    /// <summary>Locale id of <see cref="Remaining"/> — <c>BR.RemainingPlayers</c>.</summary>
    public const uint RemainingId = AugustStrings.Alerts.Remaining;

    /// <summary>Locale id of <see cref="WinnerAnnounced"/> — <c>BR.AnnounceWinner</c>.</summary>
    public const uint WinnerAnnouncedId = AugustStrings.Alerts.WinnerAnnounced;

    /// <summary>Locale id of <see cref="CelebrationEnding"/> — <c>BR.EndingWinnerCelebration</c>.</summary>
    public const uint CelebrationEndingId = AugustStrings.Alerts.CelebrationEnding;

    /// <summary>Locale id of <see cref="Connected"/> — <c>BR.PlayersConnected</c>.</summary>
    public const uint ConnectedId = AugustStrings.Alerts.Connected;

    /// <summary>
    /// <b><see cref="RemainingId"/></b>, string hash <c>1256554906</c>, raw text
    /// <c>"Only #count([*slot0*]) remain."</c> — broadcast on every elimination (S4 row E7).
    /// <c>#count(...)</c> is the client's plural helper and expands to the bare number, so
    /// server-side the sentence is <c>"Only N remain."</c> — the same expansion the owner's
    /// server makes (Z1 <c>ZoneEndgame.RemainAlert</c>).
    /// </summary>
    public static string Remaining(int remaining) =>
        GasAlerts.ExpandCount(AugustStrings.Alerts.RemainingText, remaining);

    /// <summary>
    /// <b><see cref="WinnerAnnouncedId"/></b>, string hash <c>4204263506</c>, raw text
    /// <c>"[*slot0*] has won the match!"</c> — broadcast to everyone when the match is won
    /// (S4 row E4). <c>[*slot0*]</c> is a plain substitution, so the whole sentence is the name
    /// followed by the literal remainder (Z1 <c>ZoneEndgame.WinnerAlert</c>: round 21's
    /// "&lt;name&gt; wins!" is not text this client ships).
    /// </summary>
    public static string WinnerAnnounced(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return GasAlerts.ExpandSlot(AugustStrings.Alerts.WinnerAnnouncedText, name);
    }

    /// <summary>
    /// <b><see cref="CelebrationEndingId"/></b>, string hash <c>2034493096</c>, raw text
    /// <c>"Ending winner celebration in #count([*slot0*])."</c> — the clock on the winner's
    /// celebration before the match resets (S4 row E4).
    /// <para>
    /// <b>[I] on the unit only.</b> The id, hash and raw text are the client's own; that
    /// <c>#count([*slot0*])</c> should be filled with a whole number of <i>seconds</i> is
    /// inferred — from the countdown being a duration and from the identical treatment
    /// <see cref="GasAlerts.SafeZoneMarked"/> already gives 11102's
    /// <c>"…released in #count([*slot0*])."</c>. The word "seconds" is supplied here for the same
    /// reason it is supplied there: the bare number reads as a broken sentence.
    /// </para>
    /// </summary>
    public static string CelebrationEnding(uint seconds) =>
        GasAlerts.ExpandCount(AugustStrings.Alerts.CelebrationEndingText, seconds);

    /// <summary>
    /// <b><see cref="ConnectedId"/></b>, string hash <c>3204975855</c>, raw text
    /// <c>"#count([*slot0*]) connected."</c> — the lobby's population line (S4 row E7). Same
    /// bare-number expansion as <see cref="Remaining"/>.
    /// </summary>
    public static string Connected(int connected) =>
        GasAlerts.ExpandCount(AugustStrings.Alerts.ConnectedText, connected);

    /// <summary>Whole seconds a millisecond countdown announces, rounded up — <see cref="GasAlerts.SecondsOf"/>.</summary>
    public static uint SecondsOf(long milliseconds) => GasAlerts.SecondsOf(milliseconds);

    /// <summary>Byte length <see cref="Write"/> produces for this message.</summary>
    public static int LengthOf(string message) => GasAlerts.LengthOf(message);

    /// <summary><c>11 31 00</c> then one <c>u32</c>-length-prefixed UTF-8 string, and nothing else.</summary>
    public static void Write(PacketWriter writer, string message) => GasAlerts.Write(writer, message);
}
