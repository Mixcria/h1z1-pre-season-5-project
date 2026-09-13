using Cranberry.Protocol;

namespace Cranberry.Zone.DevConsole;

/// <summary>
/// Opcodes for server-registered console commands, their registration and reply surfaces.
/// Original client commands such as vehicle, item and goto use dedicated native packets.
/// <para>
/// Every layout in this file is read from the August client's own handler for that packet (build
/// 0.0.118.208059, <c>ClientProtocol_1148</c>), and the function is named at the field it explains.
/// Nothing is ported from a schema. Design: <c>out\devconsole-20260901\DESIGN-dev-console.md</c>
/// §1.1-§1.3; surfaces: <c>R2-august-client-surfaces.md</c> §3.1-§3.5, §5.
/// </para>
/// <para>
/// <b>Direction.</b> <see cref="ExecuteCommandRequest"/> and <see cref="SpectateNotice"/> are c2s and
/// only parsed; everything else is s2c and only written. All of them ride gateway channel 0.
/// </para>
/// </summary>
public static class ConsoleOpcodes
{
    /// <summary>Base 0x09 - <c>cPacketIdCommandBase</c> (out\registrations-1148.json).</summary>
    public const byte CommandBase = ZoneOpcodes.CommandBase;

    /// <summary>
    /// Base 0x0a - <c>cPacketIdAdminBase</c>. New at 1148: the same two sub-opcodes are registered
    /// under it as well. The client never <i>dispatches</i> 0x0a inbound (no <c>case 0xa</c> in
    /// <c>FUN_140af3950</c>, R2 §2), but the reader accepts it so a client that sends one is not
    /// dropped on the floor.
    /// </summary>
    public const byte AdminBase = ZoneOpcodes.AdminBase;

    /// <summary>Sub 0x0040 - <c>cCommandPacketIdAddWorldCommand</c>, s2c (handler <c>FUN_14129ad10</c> case 0x40).</summary>
    public const ushort AddWorldCommandSub = 0x0040;

    /// <summary>Sub 0x0042 - <c>cCommandPacketIdExecuteCommand</c>, c2s (sender <c>FUN_141296a90</c>).</summary>
    public const ushort ExecuteCommandSub = 0x0042;

    /// <summary>
    /// Sub 0x0043 - <c>cCommandPacketIdZoneExecuteCommand</c>. The same two fields as
    /// <see cref="ExecuteCommandSub"/> (R1 §1.3.1); never observed on the wire, accepted anyway.
    /// </summary>
    public const ushort ZoneExecuteCommandSub = 0x0043;

    /// <summary>
    /// Sub 0x0510 - <c>cAdminCommandPacketIdSpectate</c>, c2s. The client's own console-toggle
    /// command <c>FUN_141291d50</c> sends one of these naming "ObserverCamera" on <b>every</b>
    /// toggle (<c>FUN_141291d50_141291d50.c:100-104</c>: <c>local_8f0 = 9; local_8e8 = 0x510;</c>).
    /// Cranberry must never answer it - see <see cref="SpectateNotice"/>.
    /// </summary>
    public const ushort SpectateSub = 0x0510;

    /// <summary>Chat sub 0x0003 - console-only text, s2c (parser <c>FUN_141251d90</c>).</summary>
    public const ushort ChatConsolePrintSub = 0x0003;

    /// <summary>Chat sub 0x0005 - <c>Chat.ChatText</c>, s2c (parser <c>FUN_141251ba0</c>).</summary>
    public const ushort ChatTextSub = 0x0005;

    /// <summary>ClientUpdate sub 0x002f - <c>StartTimer</c>, s2c (parser <c>FUN_140a36640</c>).</summary>
    public const ushort StartTimerSub = 0x002f;

    /// <summary>Ui sub 0x07 - <c>Ui.ExecuteScript</c>, s2c. One <b>u8</b> sub, no third header byte (R2 §2).</summary>
    public const byte UiExecuteScriptSub = 0x07;

    /// <summary>
    /// The <c>06 03</c> "code" value that also raises a red HUD card:
    /// <c>if (code == 0x40000) SystemMessage(text, 0, "#FC0909")</c> (dispatcher
    /// <c>FUN_1412568e0:688-716</c>). Any other value prints to the console only.
    /// </summary>
    public const uint ConsolePrintRedCardCode = 0x0004_0000;
}

/// <summary>
/// <c>Command.ExecuteCommand</c> - a typed server-registered <c>/name args</c> command.
/// <para>
/// <c>09 42 00 | u32 commandHash | u32 argByteCount | argByteCount x utf8</c>
/// </para>
/// <para>
/// <b>Proven.</b> The <c>__sendworldcommand</c> executor <c>FUN_141296a90</c> (dump
/// <c>out\ghidra-aug\devconsole-r5-swc\</c>) builds the message object with
/// <c>local_990 = 9; local_988 = 0x42;</c> (<c>:281-282</c>), stores the name hash at <c>+0x20</c>
/// and the argument tail at <c>+0x28</c>, and hands it to <c>FUN_141266070</c>. That object's
/// serialiser <c>FUN_14126add0</c> writes <c>u16 (obj+0x10)</c> = the sub-opcode,
/// <c>u32 (obj+0x20)</c> = the hash and <c>FUN_140b78e80(obj+0x28)</c> = a String8, i.e. a u32 byte
/// count followed by the bytes (dump
/// <c>out\ghidra-aug\devconsole-refute1-packet-serialiser\_14126add0\FUN_14126add0_14126add0.c:11,17,23</c>).
/// </para>
/// <para>
/// <b>The hash names the command, and only a pushed name can appear.</b> The executor emits a hash
/// only if it is in the client's per-connection world-command table
/// (<c>FUN_141296a90:134-140</c>) - that is, a name the server pushed with
/// <see cref="AddWorldCommand"/>. The leading <c>/</c> and the name are stripped by the client and
/// the remainder of the typed line arrives as <see cref="Arguments"/> with its case intact. An
/// unregistered name collapses to <see cref="CommandHash.Help"/> with empty arguments - measured on
/// the owner's own 1087 server (R1 <c>part1.md:177</c>:
/// <c>09 42 00 | 69 db 1b d5 | 00 00 00 00</c>), inferred at 1148 because that fallback lives in the
/// console's own input handler, which nobody has decompiled yet.
/// </para>
/// </summary>
/// <param name="Base">0x09 <c>CommandBase</c>, or 0x0a <c>AdminBase</c> (both registered at 1148).</param>
/// <param name="Sub">0x0042 <c>ExecuteCommand</c>, or 0x0043 <c>ZoneExecuteCommand</c>.</param>
/// <param name="Hash"><c>CommandHash.Compute(name)</c> - see <see cref="CommandHash"/>.</param>
/// <param name="Arguments">The typed tail, UTF-8, case preserved. Empty for a bare name.</param>
/// <param name="Truncated">
/// True when the declared argument length ran past the payload. Whatever bytes were there are kept
/// rather than dropped silently (R1 §5 item 9); the caller logs the whole packet once.
/// </param>
public sealed record ExecuteCommandRequest(byte Base, ushort Sub, uint Hash, string Arguments, bool Truncated)
{
    /// <summary>u8 base + u16 sub + u32 hash + u32 length: the shortest legal packet, empty arguments.</summary>
    public const int HeaderLength = 11;

    /// <summary>
    /// True when the payload is an ExecuteCommand / ZoneExecuteCommand under either base and long
    /// enough to hold its own header. A shorter body is deliberately <b>not</b> matched: it is not
    /// this packet, and the caller's default arm then logs it as the unknown thing it is.
    /// </summary>
    public static bool Matches(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderLength)
        {
            return false;
        }

        if (payload[0] is not (ConsoleOpcodes.CommandBase or ConsoleOpcodes.AdminBase))
        {
            return false;
        }

        ushort sub = (ushort)(payload[1] | (payload[2] << 8));
        return sub is ConsoleOpcodes.ExecuteCommandSub or ConsoleOpcodes.ZoneExecuteCommandSub;
    }

    /// <summary>
    /// Parses a payload <see cref="Matches"/> accepts. An over-long declared length yields the bytes
    /// that are present and <see cref="Truncated"/> = true; bytes past the declared length are
    /// ignored (the client's serialiser never writes any).
    /// </summary>
    /// <exception cref="PacketFormatException">The payload is not an ExecuteCommand at all.</exception>
    public static ExecuteCommandRequest Parse(ReadOnlySpan<byte> payload)
    {
        if (!Matches(payload))
        {
            throw new PacketFormatException(
                $"Not a Command.ExecuteCommand: {payload.Length} byte(s) starting {Head(payload)}.");
        }

        var reader = new PacketReader(payload);
        byte baseOpcode = reader.ReadByte();
        ushort sub = reader.ReadUInt16();
        uint hash = reader.ReadUInt32();
        uint declared = reader.ReadUInt32();

        int available = reader.Remaining;
        bool truncated = declared > (uint)available;
        int take = truncated ? available : (int)declared;
        string arguments = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(take));

        return new ExecuteCommandRequest(baseOpcode, sub, hash, arguments, truncated);
    }

    private static string Head(ReadOnlySpan<byte> payload)
    {
        int n = Math.Min(4, payload.Length);
        return n == 0 ? "(empty)" : Convert.ToHexString(payload[..n]);
    }
}

/// <summary>
/// <c>Command.Spectate</c> c2s - <c>09 10 05 | String8 "ObserverCamera"</c>: noise on the console
/// path rather than a request.
/// <para>
/// The client's console-toggle command (<c>FUN_141291d50</c>, the function the opener tool's gate
/// patch unlocks) sends this on every toggle, before it opens or closes anything. On retail the
/// server flipped the player into the observer camera; Cranberry never answers it, and matching it
/// here is what keeps one Debug line in the log instead of an "unknown packet" hex dump per
/// keypress. Evidence:
/// <c>out\ghidra-aug\devconsole-ToggleDebugConsole\...\FUN_141291d50_141291d50.c:100-104</c>
/// (<c>local_8f0 = 9; local_8e8 = 0x510; PTR_s_ObserverCamera</c>) and
/// <c>out\registrations-1148.json</c> (<c>cAdminCommandPacketIdSpectate</c> 0x510 under base 9).
/// </para>
/// </summary>
public static class SpectateNotice
{
    /// <summary>u8 base + u16 sub + u32 string length.</summary>
    public const int HeaderLength = 7;

    /// <summary>The only camera the console toggle ever names.</summary>
    public const string ConsoleToggleTarget = "ObserverCamera";

    /// <summary>True when the payload is a <c>09 10 05</c> Spectate under either command base.</summary>
    public static bool Matches(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderLength)
        {
            return false;
        }

        if (payload[0] is not (ConsoleOpcodes.CommandBase or ConsoleOpcodes.AdminBase))
        {
            return false;
        }

        return (ushort)(payload[1] | (payload[2] << 8)) == ConsoleOpcodes.SpectateSub;
    }

    /// <summary>The camera name, for the one Debug line; null when the string is malformed.</summary>
    public static string? TryReadTarget(ReadOnlySpan<byte> payload)
    {
        if (!Matches(payload))
        {
            return null;
        }

        try
        {
            var reader = new PacketReader(payload);
            reader.Skip(3);
            return reader.ReadString();
        }
        catch (PacketFormatException)
        {
            return null;
        }
    }
}

/// <summary>Everything the console hook claims off the tunnel: a typed command, or the toggle's Spectate.</summary>
public static class ConsoleInbound
{
    /// <summary>True for a payload the console owns - one guard clause, two shapes.</summary>
    public static bool Matches(ReadOnlySpan<byte> payload) =>
        ExecuteCommandRequest.Matches(payload) || SpectateNotice.Matches(payload);
}

/// <summary>
/// <c>Command.AddWorldCommand</c> s2c - <c>09 40 00 | u32 len | len x utf8 name</c>, one packet per
/// name, and the reason a typed <c>/name</c> produces a packet at all.
/// <para>
/// <b>What the client does with it</b> (all proven; dump <c>out\ghidra-aug\devconsole-_141280280\</c>).
/// <c>FUN_14129ad10</c> case 0x40 parses exactly one string with <c>FUN_141273a10</c> (u8, u16,
/// i32 length, bytes; a negative length fails, and <c>param_4 = 0</c> means the packet must be
/// consumed exactly - no trailing bytes) and calls
/// <c>FUN_141280280(client, "__sendworldcommand", name)</c>, which:
/// </para>
/// <list type="number">
/// <item>hashes the name with the inlined <see cref="CommandHash"/> and walks the client's
/// per-connection world-command table (<c>+0x1478/+0x1498</c>) - <b>a hit returns immediately</b>,
/// so re-sending a name the client already knows is a silent no-op (<c>:36-46</c>);</item>
/// <item>otherwise asks the <b>global</b> CVar/command registry (<c>FUN_141e9ac10</c> to
/// <c>FUN_141e9d950</c> to <c>FUN_141e9d680</c>): a hit writes
/// <c>Server sent %s (%s) that conflicts with a local command</c> to
/// <c>Client\Logs\AdminCommands.log</c> and adds nothing, leaving the name un-typeable
/// (<c>:47-60</c>) - which is what <see cref="ClientRegistry1148"/> exists to prevent;</item>
/// <item>otherwise inserts the name and registers the process-lifetime alias
/// <c>name</c> -&gt; <c>"__sendworldcommand name"</c> (<c>FUN_141e9b980</c>, <c>:61-</c>).</item>
/// </list>
/// </summary>
/// <param name="Name">
/// The typed name without its slash. Empty is refused: the client would register a nameless alias.
/// </param>
public sealed record AddWorldCommand(string Name)
{
    /// <summary>The name, validated at construction so an unsendable packet cannot exist.</summary>
    public string Name { get; init; } = RequireName(Name);

    /// <summary>Bytes this writes: <c>u8</c> base, <c>u16</c> sub, <c>u32</c> length, the UTF-8 name.</summary>
    public int Length => 7 + System.Text.Encoding.UTF8.GetByteCount(Name);

    /// <summary><c>09 40 00</c> then one String8, and nothing else (the parser consumes it exactly).</summary>
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(ConsoleOpcodes.CommandBase);
        writer.WriteUInt16(ConsoleOpcodes.AddWorldCommandSub);
        writer.WriteString(Name);
    }

    private static string RequireName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length == 0
            ? throw new ArgumentException("A world command name cannot be empty.", nameof(name))
            : name;
    }
}

/// <summary>
/// The console-only text line - <c>06 03 00 | u32 len | len x utf8 | u8 flag | u32 code</c> - and the
/// default LINE surface of the menu.
/// <para>
/// <b>Why this one.</b> The Chat dispatcher <c>FUN_1412568e0:688-716</c> parses it with
/// <c>FUN_141251d90</c> and, for <c>flag == 0</c>, calls <c>PrintConsole(text, 0, 0)</c>
/// (<c>FUN_141250c70</c>, vtable <c>PTR_LAB_1431f95a0[3]</c>) = Lua <c>ChatHandler:OnPrintConsole</c>
/// plus <c>EVENT_PRINT_CONSOLE</c>: one Lua call, no chat duplicate, no banner (R2 §3.1.2). A
/// <c>flag != 0</c> takes the <c>AdminSystemMessage</c> path, which prints only for an admin client,
/// so the flag is hard-wired to 0 here.
/// </para>
/// <para>
/// <c>code == 0x00040000</c> additionally raises the red HUD card
/// (<c>SystemMessage(text, 0, "#FC0909")</c>) before the console print; every other value prints to
/// the console alone.
/// </para>
/// <para>
/// <b>Empty text is refused.</b> <c>PrintConsole</c> early-outs on an empty string, so a blank menu
/// row must be sent as one space or the frame silently loses a line (design §1.3).
/// </para>
/// </summary>
/// <param name="Text">One line. Never empty; a blank row is <c>" "</c>.</param>
/// <param name="RedCard">Sets <c>code</c> to 0x00040000: the text also flashes as a red HUD card.</param>
public sealed record ConsolePrint(string Text, bool RedCard = false)
{
    /// <summary>The line, validated at construction: the client's <c>PrintConsole</c> drops an empty string.</summary>
    public string Text { get; init; } = RequireText(Text);

    /// <summary>Bytes this writes: 3 header + 4 length + the UTF-8 text + 1 flag + 4 code.</summary>
    public int Length => 12 + System.Text.Encoding.UTF8.GetByteCount(Text);

    /// <summary><c>06 03 00</c> | String8 | <c>u8 0</c> | <c>u32 code</c>.</summary>
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(ZoneOpcodes.ChatBase);
        writer.WriteUInt16(ConsoleOpcodes.ChatConsolePrintSub);
        writer.WriteString(Text);
        writer.WriteByte(0);
        writer.WriteUInt32(RedCard ? ConsoleOpcodes.ConsolePrintRedCardCode : 0u);
    }

    private static string RequireText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0
            ? throw new ArgumentException(
                "The client's PrintConsole drops an empty line; send one space for a blank row.",
                nameof(text))
            : text;
    }
}

/// <summary>
/// <c>Chat.ChatText</c> - <c>06 05 00 | u32 len | len x utf8 | u32 a | u32 colour | u32 colour2 |
/// u8 flag | u8 alsoConsole</c> - the fallback LINE surface, and the one the owner's 1087 server
/// uses for its console replies.
/// <para>
/// Parser <c>FUN_141251ba0</c>; dispatcher <c>FUN_1412568e0:717-785</c> calls
/// <c>PrintChat(text, a, colour, colour2, flag, 0, 1)</c> and then, <b>only if the last byte is
/// non-zero</b> (<c>:775-777</c>), <c>PrintConsole(text, flag, 0)</c>. That last byte is therefore
/// the difference between a chat line and a chat line the console pane also shows; it defaults to 1
/// here, and <c>alsoConsole: false</c> reproduces the owner's exact 1087 bytes.
/// </para>
/// <para>
/// Colours are masked <c>and 0xffffff</c> by the client, so white is <c>0x00ffffff</c> and the
/// fourth byte is ignored. A text starting with <c>"##"</c> takes a different dispatcher branch
/// (<c>DAT_1431fa2e0</c>, <c>:730-760</c>, a locale-key path) and is refused here.
/// </para>
/// </summary>
/// <param name="Text">One line. Never empty, never starting with <c>##</c>.</param>
/// <param name="Rgb">24-bit colour of the chat line; the console print takes no colour.</param>
/// <param name="AlsoConsole">The trailing byte: 1 = the console pane prints it too.</param>
public sealed record ChatText(string Text, uint Rgb = 0x00ff_ffff, bool AlsoConsole = true)
{
    /// <summary>The line, validated at construction (empty text, and the <c>##</c> locale-key branch).</summary>
    public string Text { get; init; } = RequireText(Text);

    /// <summary>Bytes this writes: 3 header + 4 length + the UTF-8 text + 12 + 1 + 1.</summary>
    public int Length => 21 + System.Text.Encoding.UTF8.GetByteCount(Text);

    /// <summary><c>06 05 00</c> | String8 | <c>u32 0</c> | <c>u32 rgb</c> | <c>u32 0</c> | <c>u8 0</c> | <c>u8 alsoConsole</c>.</summary>
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(ZoneOpcodes.ChatBase);
        writer.WriteUInt16(ConsoleOpcodes.ChatTextSub);
        writer.WriteString(Text);
        writer.WriteUInt32(0);
        writer.WriteUInt32(Rgb);
        writer.WriteUInt32(0);
        writer.WriteByte(0);
        writer.WriteBool(AlsoConsole);
    }

    private static string RequireText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            throw new ArgumentException("An empty chat line reaches no surface.", nameof(text));
        }

        return text.StartsWith("##", StringComparison.Ordinal)
            ? throw new ArgumentException(
                "A chat line starting with ## takes the client's locale-key branch, not the print path.",
                nameof(text))
            : text;
    }
}

/// <summary>The five colours <c>FUN_140aea220</c> maps a <c>ShowSystemMessage</c> colour index onto.</summary>
public enum CardColour : uint
{
    /// <summary>0 - <c>"0xFF0000"</c>.</summary>
    Red = 0,

    /// <summary>1 - <c>"0x00FF00"</c>.</summary>
    Green = 1,

    /// <summary>2 - <c>"0x0000FF"</c>.</summary>
    Blue = 2,

    /// <summary>3 - <c>"0xFFFF00"</c>.</summary>
    Yellow = 3,

    /// <summary>4 - <c>"0xFFFFFF"</c>.</summary>
    White = 4,
}

/// <summary>
/// <c>ShowSystemMessage</c> - <c>42 | u32 localeId | String8 text | u32 type | u32 colourIdx</c>: the
/// CARD surface, a queued HUD toast.
/// <para>
/// Parser <c>FUN_140a3ef50</c> (u8 base, u32, String, u32, u32). An empty text means "use the locale
/// id"; a non-empty text is used as it stands. The dispatcher hands it to <c>FUN_140b83f80</c>,
/// which drops a text identical to the previous one within 6 ticks and then calls the GFx
/// <c>SystemMessage</c> method of <c>HudSystemMessagesWindow.gfx</c> with the colour string
/// <c>FUN_140aea220</c> derives from <paramref name="Colour"/> (R2 §3.5). The queue is what makes
/// this wrong for a menu frame and right for a one-off notice.
/// </para>
/// </summary>
/// <param name="Text">The card text; never empty (an empty text would draw locale id 0).</param>
/// <param name="Colour">Colour index; the client itself only ever sends <c>type = 0</c>.</param>
public sealed record SystemMessageCard(string Text, CardColour Colour = CardColour.White)
{
    /// <summary>The card text, validated at construction.</summary>
    public string Text { get; init; } = RequireText(Text);

    /// <summary><c>42</c> | <c>u32 0</c> localeId | String8 | <c>u32 0</c> type | <c>u32</c> colour index.</summary>
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(ZoneOpcodes.ShowSystemMessage);
        writer.WriteUInt32(0);
        writer.WriteString(Text);
        writer.WriteUInt32(0);
        writer.WriteUInt32((uint)Colour);
    }

    private static string RequireText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0
            ? throw new ArgumentException(
                "An empty card text makes the client draw locale id 0 instead.", nameof(text))
            : text;
    }
}

/// <summary>
/// <c>ClientUpdate.StartTimer</c> - <c>11 2f 00 | u32 localeId | u32 seconds | String8 label</c>: the
/// TICKER surface, one HUD line that replaces itself in place.
/// <para>
/// Parser <c>FUN_140a36640</c>; handler <c>FUN_140b7f900</c> writes four UI-bound values and marks
/// them dirty - visible = <c>seconds &gt; 0</c>, the seconds as a float, the locale string and the
/// free text - so <c>seconds = 0</c> hides the line (R2 §3.4). Which widget draws it is inferred
/// (<c>HudActionTimerWindow.gfx</c>, id 32 in <c>ConsoleWidgetDefinitions.xml</c>).
/// </para>
/// </summary>
/// <param name="Label">Free text; never empty.</param>
/// <param name="Seconds">Countdown seconds; 0 hides the line.</param>
public sealed record HudTicker(string Label, uint Seconds)
{
    /// <summary>The label, validated at construction.</summary>
    public string Label { get; init; } = RequireLabel(Label);

    /// <summary><c>11 2f 00</c> | <c>u32 0</c> localeId | <c>u32</c> seconds | String8 label.</summary>
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(ZoneOpcodes.ClientUpdateBase);
        writer.WriteUInt16(ConsoleOpcodes.StartTimerSub);
        writer.WriteUInt32(0);
        writer.WriteUInt32(Seconds);
        writer.WriteString(Label);
    }

    private static string RequireLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return label.Length == 0
            ? throw new ArgumentException("A ticker with no label draws the locale string alone.", nameof(label))
            : label;
    }
}

/// <summary>
/// <c>Ui.ExecuteScript</c> - <c>1a 07 | String8 "Object.Method" | u32 k | k x u32</c>: the one packet
/// that calls into the client's own Lua, and the only server-side lead on opening the console pane.
/// <para>
/// The Ui family's sub-opcode is <b>one byte</b>, not the usual u16 (<c>FUN_1412caa00</c> reads
/// <c>(uint)*(byte*)(p+1)</c>), so <c>1a 07 00</c> would fail the exact-length parse. The parser
/// <c>FUN_1412bfbd0</c> reads the name, then a counted list of <c>u32</c> arguments, and rejects any
/// trailing byte; only integer arguments exist (R2 §2, §5). Whether any Lua object answers
/// <c>Console.Show</c> is untested - this writer exists so the first click can find out.
/// </para>
/// </summary>
/// <param name="ObjectDotMethod">e.g. <c>"Console.Show"</c>; never empty.</param>
/// <param name="Ints">Integer arguments; an empty list writes just the <c>u32 0</c> count.</param>
public sealed record UiExecuteScript(string ObjectDotMethod, params uint[] Ints)
{
    /// <summary>The script name, validated at construction.</summary>
    public string ObjectDotMethod { get; init; } = RequireName(ObjectDotMethod);

    /// <summary><c>1a 07</c> | String8 | <c>u32</c> count | count x <c>u32</c>.</summary>
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(ZoneOpcodes.UiBase);
        writer.WriteByte(ConsoleOpcodes.UiExecuteScriptSub);
        writer.WriteString(ObjectDotMethod);
        writer.WriteUInt32((uint)Ints.Length);
        foreach (uint value in Ints)
        {
            writer.WriteUInt32(value);
        }
    }

    private static string RequireName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length == 0
            ? throw new ArgumentException("Ui.ExecuteScript needs an Object.Method name.", nameof(name))
            : name;
    }
}
