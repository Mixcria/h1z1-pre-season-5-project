using Cranberry.Protocol;
using Cranberry.Zone.Gas;

namespace Cranberry.Zone.DevConsole.Surfaces;

/// <summary>
/// The fifth surface: the same <c>Chat 06 03</c> console-print packet the <c>print</c> surface
/// sends, with every line prefixed <c>CRANBERRY:</c> and tagged with one frame command, so the
/// client-side script <c>Resources\Scripts\CranberryMenu.lua</c> eats it and draws a real window
/// instead of the console pane printing it.
/// <para>
/// <b>This is R6 recommendation (b'), server half</b>
/// (<c>out\devconsole-20260901\R6-graphical-menu.md</c> §4.2 part 2). No new packet, no new opcode,
/// no schema change: <c>OnPrintConsole</c> is a bare Lua <b>global</b> that the client's C++
/// <c>PrintConsole</c> slot (<c>FUN_141250c70</c>) calls with the text of our <c>06 03 00</c>
/// packet (R6 §1.6, correcting R2 §3.0), so one added <c>.lua</c> that reassigns that global turns
/// the console-line packet into a server→script string pipe. A line whose text starts with the
/// sentinel never reaches the console pane at all; a client with no script loaded sees the raw
/// prefixed text, which is ugly but harmless, and is why this surface is opt-in
/// (<c>/surface lua</c>, <c>CRANBERRY_CONSOLE_SURFACE=lua</c>) rather than a default.
/// </para>
/// <para>
/// <b>Why the sentinel is a word and not a control byte.</b> R6 §1.6 suggested <c>\1</c>. A word is
/// used instead because the whole point of the fallback is that a client <i>without</i> the script
/// still shows something the owner can read, and because <c>ConsoleReply.NonEmpty</c> and the
/// renderer's "ASCII only" rule (docs/103 §1.3) both keep the line printable. The one prefix that
/// is genuinely unsafe - <c>##</c>, which <c>06 05</c> reads as a locale key (R2 §3.1.1) - is not
/// this one.
/// </para>
/// <para>
/// <b>Framing.</b> The engine draws a menu frame by calling <see cref="Line"/> once per row
/// (<c>ConsoleEngine.Draw</c>), with no begin or end signal, so this surface recovers the frame
/// boundaries from the renderer's own grid: <c>FrameRenderer</c> opens every frame with
/// <c>TopRule</c> and closes it with <c>BottomRule</c>, and both are the only lines that are
/// entirely a corner, a run of <c>=</c> or <c>-</c>, and a corner. One surface instance draws one
/// whole frame (the engine holds a single <c>IConsoleSurface</c> for the length of one
/// <c>ExecuteLine</c>), so the alternating latch below cannot straddle two frames.
/// </para>
/// </summary>
public sealed class LuaMenuSurface(Action<Action<PacketWriter>> send) : IConsoleSurface
{
    /// <summary>The prefix <c>CranberryMenu.lua</c>'s <c>OnPrintConsole</c> hook consumes.</summary>
    public const string Sentinel = "CRANBERRY:";

    /// <summary>
    /// The first string of the inbound <c>WallOfData 9a 05</c> the script sends back, i.e. the
    /// <c>a</c> of <c>Ui.SetWallOfData(a, b)</c>. The client's own handlers use a window name here
    /// (<c>CUSTOMIZATION_WINDOW</c>, <c>InventoryWindow</c>, <c>LoadingScreenWindow</c>), so this
    /// one is deliberately not window-shaped and cannot collide with any of them.
    /// </summary>
    public const string WallOfDataTable = "CRANBERRY";

    /// <summary>Begin a frame: the script clears its row buffer.</summary>
    public const char BeginFrame = 'B';

    /// <summary>One frame row, appended to the buffer.</summary>
    public const char Row = 'R';

    /// <summary>A continuation of the previous row - how a long line is chunked.</summary>
    public const char Continue = '+';

    /// <summary>End a frame: the script shows the window and paints.</summary>
    public const char EndFrame = 'E';

    /// <summary>A loose line (a command's answer, not a frame row): the script's log strip.</summary>
    public const char Loose = 'L';

    /// <summary>Open the window without a frame.</summary>
    public const char OpenWindow = 'O';

    /// <summary>Close the window.</summary>
    public const char CloseWindow = 'X';

    /// <summary>Set the window title.</summary>
    public const char SetTitle = 'T';

    /// <summary>Pin the ActionScript method the script paints through.</summary>
    public const char PinMethod = 'M';

    /// <summary>Echo one line back out through the ORIGINAL console handler - the script's "ping".</summary>
    public const char Echo = 'P';

    /// <summary>
    /// The most payload characters in one packet before a <see cref="Continue"/> is used.
    /// <para>
    /// No maximum is <i>proven</i> for <c>06 03</c>: its parser <c>FUN_141251d90</c> reads a
    /// <c>String8</c> into a heap string with no fixed bound. 180 is a deliberately conservative
    /// choice - the widest fixed string buffer anywhere on the client's console paths is
    /// <c>StringFixed&lt;256&gt;</c> (the Lua command dispatcher <c>FUN_140bad090</c>), and the Lua
    /// invoker's name buffer is 512 (<c>FUN_140ba89e0</c>), so 180 plus the 10-character sentinel
    /// plus the 2-character command sits inside every one of them with room to spare. A menu frame
    /// is at most 60 columns (<see cref="ConsoleOptions.MaximumWidth"/>), so no frame row is ever
    /// chunked; only a long command answer is.
    /// </para>
    /// </summary>
    public const int MaximumPayload = 180;

    private readonly Action<Action<PacketWriter>> _send =
        send ?? throw new ArgumentNullException(nameof(send));

    private bool _inFrame;

    /// <summary>True while a frame has been opened by a rule line and not yet closed.</summary>
    public bool InFrame => _inFrame;

    /// <summary>
    /// True when the line is one of <c>FrameRenderer</c>'s two rules: a corner, a run of <c>=</c>
    /// or <c>-</c>, and a corner. The plain theme draws <c>+===+</c> / <c>+---+</c> and the heavy
    /// theme <c>#===#</c> for both, which is exactly why the latch alternates rather than reading
    /// the fill character.
    /// </summary>
    public static bool IsRule(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length < 3)
        {
            return false;
        }

        char open = text[0];
        char close = text[^1];
        if ((open != '+' && open != '#') || (close != '+' && close != '#'))
        {
            return false;
        }

        for (int i = 1; i < text.Length - 1; i++)
        {
            if (text[i] != '=' && text[i] != '-')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reduces one inbound <c>WallOfData</c> action token to the console line it stands for.
    /// <para>
    /// This is the other half of R6 §4.2 part 3, and the reason <c>MenuInput</c>'s own doc comment
    /// says <i>"every front door reduces to one of these, so the state machine never knows whether
    /// a step arrived as <c>/d</c>, as <c>/m d</c> or - if a surface that captures keys is ever
    /// found - as an arrow key"</i>. The script sends the short verbs verbatim, so most tokens are
    /// already a command name; the rest are folded onto <c>/m</c> and let
    /// <see cref="Menu.MenuInput.Parse"/> do the parsing that the typed door already does.
    /// </para>
    /// </summary>
    /// <param name="action">The second string of the <c>9a 05</c>, trimmed by the caller or not.</param>
    /// <returns>The command name and its argument tail, or null when the token means nothing.</returns>
    public static (string Name, string Arguments)? Route(string? action)
    {
        string token = (action ?? string.Empty).Trim();
        if (token.Length == 0 || token.Length > 64)
        {
            return null;
        }

        string lower = token.ToLowerInvariant();
        switch (lower)
        {
            case "u":
            case "d":
            case "s":
            case "b":
            case "q":
            case "r":
                return (lower, string.Empty);

            case "m":
            case "open":
            case "toggle":
                return ("m", string.Empty);

            case "close":
            case "hide":
                return ("q", string.Empty);

            case "uu":
            case "dd":
            case "home":
            case "end":
            case "y":
            case "n":
                return ("m", lower);
            default:
                break;
        }

        // "m 3", "win inventory" - a whole typed line the script wants run.
        int space = token.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0)
        {
            return (token[..space].ToLowerInvariant(), token[(space + 1)..]);
        }

        // A bare row number, or anything else the menu's own parser understands.
        return ("m", token);
    }

    /// <summary>
    /// The wire text for one command and payload, chunked. The first chunk carries
    /// <paramref name="command"/>; every later chunk carries <see cref="Continue"/>, which the
    /// script appends to the row it already has.
    /// </summary>
    public static IReadOnlyList<string> Encode(char command, string payload)
    {
        payload ??= string.Empty;
        if (payload.Length <= MaximumPayload)
        {
            return [$"{Sentinel}{command}|{payload}"];
        }

        List<string> chunks = [];
        for (int at = 0; at < payload.Length; at += MaximumPayload)
        {
            int take = Math.Min(MaximumPayload, payload.Length - at);
            char tag = at == 0 ? command : Continue;
            chunks.Add($"{Sentinel}{tag}|{payload.Substring(at, take)}");
        }

        return chunks;
    }

    /// <inheritdoc/>
    public void Line(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string line = ConsoleReply.NonEmpty(text);

        if (IsRule(line))
        {
            if (_inFrame)
            {
                Emit(Row, line);
                Emit(EndFrame, string.Empty);
                _inFrame = false;
            }
            else
            {
                Emit(BeginFrame, string.Empty);
                Emit(Row, line);
                _inFrame = true;
            }

            return;
        }

        Emit(_inFrame ? Row : Loose, line);
    }

    /// <summary>
    /// A banner stays a banner. <c>ClientUpdate.TextAlert 11 31</c> is the one packet in this file
    /// that is already live at 1148 (<c>GasAlerts.Write</c>, docs/87), and a screen-wide alert is
    /// not a menu row - routing it into the script's row buffer would lose it.
    /// </summary>
    public void Banner(string text) => _send(w => GasAlerts.Write(w, ConsoleReply.NonEmpty(text)));

    /// <summary>Sends one frame command with no text - <c>O</c>, <c>X</c>, <c>E</c>.</summary>
    public void Control(char command) => Emit(command, string.Empty);

    /// <summary>Sends one frame command with text - <c>T</c>, <c>M</c>, <c>P</c>.</summary>
    public void Control(char command, string payload) => Emit(command, payload);

    /// <summary>Closes a frame the renderer left open. Harmless when none is open.</summary>
    public void CloseFrame()
    {
        if (!_inFrame)
        {
            return;
        }

        Emit(EndFrame, string.Empty);
        _inFrame = false;
    }

    private void Emit(char command, string payload)
    {
        foreach (string chunk in Encode(command, payload))
        {
            ConsolePrint packet = new(chunk);
            _send(packet.WriteTo);
        }
    }
}
