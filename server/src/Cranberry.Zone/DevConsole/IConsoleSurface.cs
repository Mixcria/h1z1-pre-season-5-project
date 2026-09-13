namespace Cranberry.Zone.DevConsole;

/// <summary>
/// Where a console line is drawn. Exactly two verbs, because that is all the August client offers a
/// server: a line of text in a pane, and a banner across the screen.
/// <para>
/// Which packet each verb becomes is the surface implementation's business (Lane C's
/// <c>Surfaces/PacketSurfaces.cs</c>): <c>print</c> is <c>Chat 06 03</c> (console only,
/// <c>FUN_141251d90</c>), <c>chat</c>/<c>chat0</c> are <c>Chat.ChatText 06 05</c> with the
/// <c>alsoConsole</c> byte set or clear (<c>FUN_1412568e0:775-777</c>), <c>alert</c> is
/// <c>ClientUpdate.TextAlert 11 31</c>. Which of them the open pane actually draws is the first
/// click's question, which is why the engine talks to an interface and <c>/surface probe</c> sends
/// one labelled line on each (design §1.3, §4.5).
/// </para>
/// <para>
/// <b>One rule for every implementation.</b> A line is never the empty string: the client's
/// <c>PrintConsole</c> (<c>FUN_141250c70</c>) early-outs on empty text, so a blank row is one
/// space. <see cref="ConsoleReply.NonEmpty"/> is the guard, and the packet writers throw rather
/// than let an empty line vanish.
/// </para>
/// </summary>
public interface IConsoleSurface
{
    /// <summary>Draws one line of text in the console pane.</summary>
    void Line(string text);

    /// <summary>Draws one banner. Used for <c>/announce</c> and the emergency <c>alert</c> surface.</summary>
    void Banner(string text);
}

/// <summary>
/// A surface that keeps what it was told. Used by the engine's own tests and by <c>/surface probe</c>
/// bookkeeping; putting it beside the interface keeps the test project free of a fake per file.
/// </summary>
public sealed class RecordingSurface : IConsoleSurface
{
    private readonly List<string> _lines = [];
    private readonly List<string> _banners = [];

    /// <summary>Every line, in the order it was drawn.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Every banner, in the order it was raised.</summary>
    public IReadOnlyList<string> Banners => _banners;

    /// <summary>Lines and banners together, in order, banners prefixed <c>[banner] </c>.</summary>
    public IReadOnlyList<string> All => _all;

    private readonly List<string> _all = [];

    /// <inheritdoc/>
    public void Line(string text)
    {
        _lines.Add(text);
        _all.Add(text);
    }

    /// <inheritdoc/>
    public void Banner(string text)
    {
        _banners.Add(text);
        _all.Add($"[banner] {text}");
    }

    /// <summary>Forgets everything, so one test can assert on one step at a time.</summary>
    public void Clear()
    {
        _lines.Clear();
        _banners.Clear();
        _all.Clear();
    }
}
