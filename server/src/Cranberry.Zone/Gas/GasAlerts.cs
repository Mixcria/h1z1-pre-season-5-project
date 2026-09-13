using System.Globalization;
using System.Text;
using Cranberry.Protocol;
using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Gas;

/// <summary>
/// <c>ClientUpdate.TextAlert</c> — the banner channel — and the August client's own BR sentences.
/// <para>
/// <b>Wave 9, and it is a straight port of what the owner's own server broadcasts</b> (docs/87 §4.1,
/// §4.2). Before this wave Cranberry had never sent a TextAlert of any kind, which is why the first
/// 4:30 of a match carried two gas sends where his carries nine visible events.
/// </para>
/// <para>
/// <b>PORT-DIRECT, and proven at 1148 twice.</b> The registration is byte-identical in both builds —
/// 1087 <c>{baseOpcode 17, subOpcode 49}</c>, 1148 <c>{idHex 0x31001100, levels [17, 49], family
/// cPacketIdClientUpdateBase}</c> — so no opcode-map shift applies, consistent with the bridge
/// lane's finding that the <c>cClientUpdatePacket</c> family (base 0x11) did not move at all. And
/// the August dispatcher's <c>case 0x31</c>
/// (<c>out\gas-safezone-research\clientupdate-dispatch\_140afc660\FUN_140afc660_140afc660.c:1793</c>)
/// pre-seeds the message object with <c>0x11</c> / <c>0x31</c>, installs <b>exactly one</b>
/// <c>SoeUtil::IString&lt;char&gt;</c>, deserialises, and on success calls
/// <c>thunk_FUN_140b82b40(this, str, 0, 1)</c> to display it — no other field is read.
/// </para>
/// <para>
/// Wire: <c>11 31 00</c> + <c>u32</c> UTF-8 byte count + the bytes = <c>7 + count</c>. The owner's
/// writer (<c>C:\Z1\Server\Zone\ZoneMatchFlow.cs:4992-5000</c>) is the identical three header bytes
/// plus one <c>WriteString</c>, and <see cref="PacketWriter.WriteString"/> already encodes
/// <c>u32</c> + UTF-8, so nothing had to be derived.
/// </para>
/// <para>
/// <b>The sentences are the August build's own words</b>, read out of
/// <c>C:\Aug2017\Client\Locale\en_us_data.dat</c>, and every code name is in the same client's
/// <c>CodeStringMappings.txt</c>. <c>TextAlert</c> carries a <b>string</b> and not a locale id, so
/// <c>#count([*slot0*])</c> is expanded here, server-side, exactly as the owner's server does it.
/// </para>
/// <para>
/// <b>Lane 2B (docs/104): not one of those sentences is typed here any more.</b> Each is
/// <see cref="AugustStrings"/>'s copy of the client's own en_us text, resolved from the client's
/// <c>CodeStringMappings.txt</c> message name (<c>BR.Start</c>, <c>BR.Proceed</c>,
/// <c>BR.ReleasingGas</c>, <c>BR.SafeZoneAnnounce</c>) by <c>tools/data/derive_strings.py</c>. The
/// wire bytes are unchanged and <c>Wave9GasPresenceTests</c> pins them.
/// </para>
/// </summary>
public static class GasAlerts
{
    /// <summary>Base opcode 0x11 — <c>cPacketIdClientUpdateBase</c>, unmoved between 1087 and 1148.</summary>
    public const byte Family = ZoneOpcodes.ClientUpdateBase;

    /// <summary>Sub-opcode 0x0031 — <c>cClientUpdatePacketIdTextAlert</c>, unmoved between 1087 and 1148.</summary>
    public const ushort SubOpcode = 0x0031;

    /// <summary>Bytes a TextAlert writes before its text: <c>u8</c> base, <c>u16</c> sub, <c>u32</c> length.</summary>
    public const int HeaderLength = 7;

    /// <summary>
    /// <c>BR.Start</c> 11115, locale key 185602287 &#8212; <c>AugustStrings.Alerts.MatchBegunText</c>.
    /// </summary>
    public const string MatchBegun = AugustStrings.Alerts.MatchBegunText;

    /// <summary>
    /// <c>BR.Proceed</c> 11118, locale key 1065232486 &#8212; <c>AugustStrings.Alerts.ProceedText</c>.
    /// </summary>
    public const string Proceed = AugustStrings.Alerts.ProceedText;

    /// <summary>
    /// <c>BR.ReleasingGas</c> 11120, locale key 2344308363 &#8212;
    /// <c>AugustStrings.Alerts.ReleasingGasText</c>.
    /// </summary>
    public const string ReleasingGas = AugustStrings.Alerts.ReleasingGasText;

    /// <summary>
    /// The client's locale id of each sentence, for the pinning tests and for any future packet
    /// that carries an id rather than a string. <c>TextAlert</c> itself never sends these.
    /// </summary>
    public const uint MatchBegunId = AugustStrings.Alerts.MatchBegun;

    /// <inheritdoc cref="MatchBegunId"/>
    public const uint ProceedId = AugustStrings.Alerts.Proceed;

    /// <inheritdoc cref="MatchBegunId"/>
    public const uint ReleasingGasId = AugustStrings.Alerts.ReleasingGas;

    /// <inheritdoc cref="MatchBegunId"/>
    public const uint SafeZoneMarkedId = AugustStrings.Alerts.SafeZoneMarked;

    /// <summary>
    /// <c>BR.SafeZoneAnnounce</c> 11102, locale key 2260824175:
    /// <c>"The safe zone has been marked on your map. Toxic gas will be released in
    /// #count([*slot0*])."</c> with the slot expanded server-side.
    /// </summary>
    public static string SafeZoneMarked(uint seconds) =>
        ExpandCount(AugustStrings.Alerts.SafeZoneMarkedText, seconds);

    /// <summary>
    /// Fill the client's <c>#count([*slot0*])</c> placeholder with a whole number of seconds.
    /// <para>
    /// Every August template that carries the token is a duration, and the bare number reads as a
    /// broken sentence, so the unit word goes in with it &#8212; the same expansion the owner's
    /// server makes (Z1 <c>ZoneMatchFlow.cs:4992-5000</c>, <c>ZoneEndgame.cs:496-505</c>).
    /// </para>
    /// </summary>
    public static string ExpandCount(string template, uint seconds)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.Replace(
            AugustStrings.CountToken,
            seconds.ToString(CultureInfo.InvariantCulture) + " seconds",
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Fill the client's <c>#count([*slot0*])</c> placeholder with a bare count &#8212; no unit
    /// word, because the template already carries the noun ("Only N remain.", "N connected.").
    /// </summary>
    public static string ExpandCount(string template, int count)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.Replace(
            AugustStrings.CountToken,
            Math.Max(0, count).ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    /// <summary>Fill the client's plain <c>[*slot0*]</c> placeholder.</summary>
    public static string ExpandSlot(string template, string value)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(value);
        return template.Replace(AugustStrings.SlotToken, value, StringComparison.Ordinal);
    }

    /// <summary>Whole seconds a millisecond countdown announces, rounded up so 149 880 ms reads 150.</summary>
    public static uint SecondsOf(long milliseconds) =>
        milliseconds <= 0 ? 0u : (uint)((milliseconds + 999L) / 1000L);

    /// <summary>Byte length <see cref="Write"/> produces for this message.</summary>
    public static int LengthOf(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return HeaderLength + Encoding.UTF8.GetByteCount(message);
    }

    /// <summary><c>11 31 00</c> then one <c>u32</c>-length-prefixed UTF-8 string, and nothing else.</summary>
    public static void Write(PacketWriter writer, string message)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(message);
        writer.WriteByte(Family);
        writer.WriteUInt16(SubOpcode);
        writer.WriteString(message);
    }
}
