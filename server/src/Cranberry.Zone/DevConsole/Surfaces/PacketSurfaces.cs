using Cranberry.Protocol;
using Cranberry.Zone.Gas;

namespace Cranberry.Zone.DevConsole.Surfaces;

/// <summary>
/// The five packet surfaces a console line can become, and the factory that picks one.
/// <para>
/// The fifth, <see cref="LuaMenuSurface"/>, lives in its own file: it is the <c>06 03</c> packet
/// again, prefixed <c>CRANBERRY:</c> for the client-side script of R6 recommendation (b').
/// </para>
/// <para>
/// Which of them the August client's open debug pane actually draws is the first click's question
/// (design §1.3, open question 1): every parser below is read from the 1148 binary, but no console
/// line has ever been on a wire against this client. That is exactly why the engine talks to
/// <see cref="IConsoleSurface"/> and why <c>/surface probe</c> sends one labelled line on each of
/// the four - the owner reads the pane and tells us which one arrived, and
/// <c>CRANBERRY_CONSOLE_SURFACE</c> makes it the boot default without a rebuild.
/// </para>
/// <para>
/// <b>One line, one packet, one channel.</b> Every surface writes through
/// <see cref="ConsoleContext.Send"/>, which is <c>ZoneService.SendTunnel</c> on gateway channel 0,
/// so the 20 Hz movement channels 2 and 3 are untouched. No surface ever sends two packets for one
/// line: <c>chat</c> and <c>alert</c> together would print the text twice (design §1.3).
/// </para>
/// </summary>
public static class SurfaceFactory
{
    /// <summary>
    /// The surface for a kind. <paramref name="send"/> is <c>ZoneService.SendTunnel</c> bound to
    /// one connection; a null <paramref name="send"/> yields a surface that drops its lines, which
    /// is what a context with no wire (a unit test, a closed link) needs.
    /// </summary>
    public static IConsoleSurface For(ConsoleSurfaceKind kind, Action<Action<PacketWriter>>? send) =>
        send is null
            ? NullSurface.Instance
            : kind switch
            {
                ConsoleSurfaceKind.Chat => new ChatTextSurface(send, alsoConsole: true),
                ConsoleSurfaceKind.Chat0 => new ChatTextSurface(send, alsoConsole: false),
                ConsoleSurfaceKind.Alert => new TextAlertSurface(send),
                ConsoleSurfaceKind.Lua => new LuaMenuSurface(send),
                _ => new ConsolePrintSurface(send),
            };
}

/// <summary>
/// The default surface: <c>Chat 06 03 00 | String8 | u8 0 | u32 0</c>
/// (<see cref="ConsolePrint"/>).
/// <para>
/// It is the default because its handler ends in the console pane and nowhere else: the chat
/// dispatcher <c>FUN_1412568e0:688-716</c> hands a <c>06 03</c> to the parser
/// <c>FUN_141251d90</c>, and with the flag byte clear and the code word zero the only call left is
/// <c>PrintConsole(text, 0, 0)</c> = <c>FUN_141250c70</c>, the Lua
/// <c>ChatHandler:OnPrintConsole</c> / <c>EVENT_PRINT_CONSOLE</c> pair (design §1.3, R2 §3.1.1).
/// </para>
/// <para>
/// A banner has no <c>06 03</c> form, so <see cref="Banner"/> falls back to
/// <c>ClientUpdate.TextAlert 11 31</c> - the one packet in this file that is already live at 1148
/// (<c>GasAlerts.Write</c>, docs/87).
/// </para>
/// </summary>
public sealed class ConsolePrintSurface(Action<Action<PacketWriter>> send) : IConsoleSurface
{
    private readonly Action<Action<PacketWriter>> _send =
        send ?? throw new ArgumentNullException(nameof(send));

    /// <inheritdoc/>
    public void Line(string text)
    {
        ConsolePrint packet = new(ConsoleReply.NonEmpty(text));
        _send(packet.WriteTo);
    }

    /// <inheritdoc/>
    public void Banner(string text) => _send(w => GasAlerts.Write(w, ConsoleReply.NonEmpty(text)));
}

/// <summary>
/// <c>Chat.ChatText 06 05 00 | String8 | u32 0 | u32 rgb | u32 0 | u8 0 | u8 alsoConsole</c>
/// (<see cref="ChatText"/>).
/// <para>
/// The last byte is the whole point of this surface: the dispatcher's <c>06 05</c> arm calls
/// <c>PrintChat</c> and then, <b>only when the byte at <c>+0x245</c> is non-zero</b>, calls
/// <c>PrintConsole(text, flag, 0)</c> as well (<c>FUN_1412568e0:775-777</c>). So
/// <c>alsoConsole = true</c> is "chat pane and console pane" and <c>alsoConsole = false</c> is the
/// owner's exact 1087 bytes, chat pane only - the <c>chat0</c> variant kept because it is the shape
/// his Z1 server has been sending for months (design §1.3, refute-1 §2.5).
/// </para>
/// </summary>
public sealed class ChatTextSurface(Action<Action<PacketWriter>> send, bool alsoConsole) : IConsoleSurface
{
    private readonly Action<Action<PacketWriter>> _send =
        send ?? throw new ArgumentNullException(nameof(send));

    /// <summary>True when the <c>+0x245</c> byte is set, i.e. the line also reaches the console pane.</summary>
    public bool AlsoConsole { get; } = alsoConsole;

    /// <inheritdoc/>
    public void Line(string text)
    {
        ChatText packet = new(ConsoleReply.NonEmpty(text), AlsoConsole: AlsoConsole);
        _send(packet.WriteTo);
    }

    /// <inheritdoc/>
    public void Banner(string text) => _send(w => GasAlerts.Write(w, ConsoleReply.NonEmpty(text)));
}

/// <summary>
/// The emergency surface: every line is a <c>ClientUpdate.TextAlert 11 31 00 | String8</c> banner.
/// <para>
/// It is not a pane and it cannot draw a menu frame legibly - the client's handler
/// <c>FUN_140b82b40</c> puts one EQNScreenText banner across the middle of the screen - but it is
/// the only console-shaped packet this project has ever seen a live August client accept, so it is
/// the fallback of last resort if none of the three pane surfaces draws anything (design §1.3).
/// </para>
/// </summary>
public sealed class TextAlertSurface(Action<Action<PacketWriter>> send) : IConsoleSurface
{
    private readonly Action<Action<PacketWriter>> _send =
        send ?? throw new ArgumentNullException(nameof(send));

    /// <inheritdoc/>
    public void Line(string text) => Banner(text);

    /// <inheritdoc/>
    public void Banner(string text) => _send(w => GasAlerts.Write(w, ConsoleReply.NonEmpty(text)));
}

/// <summary>
/// A surface with nowhere to write. Used when a context has no <see cref="ConsoleContext.Send"/> -
/// the engine must still be able to run a command and log its answer rather than throwing inside a
/// packet handler.
/// </summary>
public sealed class NullSurface : IConsoleSurface
{
    /// <summary>The shared instance; it holds no state.</summary>
    public static NullSurface Instance { get; } = new();

    /// <inheritdoc/>
    public void Line(string text)
    {
    }

    /// <inheritdoc/>
    public void Banner(string text)
    {
    }
}
