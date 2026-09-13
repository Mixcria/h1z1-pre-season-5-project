using System.Diagnostics.CodeAnalysis;

namespace Cranberry.Zone.DevConsole;

/// <summary>
/// What kind of value one argument of a command is. The same list drives the typed parser, the
/// menu's prompt rows and the <c>= /give ar15 1</c> info line, which is what makes the two front
/// doors one engine (design §4.2, R4 §6.1).
/// </summary>
public enum ArgKind : byte
{
    /// <summary>A bare word, lower-cased by the parser unless the command sets <c>KeepCase</c>.</summary>
    Word = 0,

    /// <summary>Everything from this position to the end of the line, case preserved.</summary>
    Text = 1,

    /// <summary>A decimal or <c>0x</c> integer.</summary>
    Int = 2,

    /// <summary>A decimal number.</summary>
    Float = 3,

    /// <summary>Three numbers; <c>~</c> keeps the caller's own value on that axis.</summary>
    Vector3 = 4,

    /// <summary>An item roster name or a definition id.</summary>
    Item = 5,

    /// <summary>A named place - a loot area, a drop point, a spawn.</summary>
    Place = 6,

    /// <summary>One of a fixed set of words, matched by unique prefix.</summary>
    Enum = 7,

    /// <summary>Raw hex bytes (<c>/raw</c>).</summary>
    Hex = 8,
}

/// <summary>One declared argument of a command.</summary>
/// <param name="Name">Shown in the usage line and as the menu's prompt label.</param>
/// <param name="Kind">How the value is read - see <see cref="ArgKind"/>.</param>
/// <param name="Default">The default a prompt shows in brackets; null means the argument is required.</param>
/// <param name="Help">One short line explaining the argument.</param>
/// <param name="Choices">For <see cref="ArgKind.Enum"/>: the words, in the order they are offered.</param>
public sealed record ArgSpec(
    string Name,
    ArgKind Kind = ArgKind.Word,
    string? Default = null,
    string Help = "",
    string[]? Choices = null)
{
    /// <summary>True when the caller must supply a value (no default).</summary>
    public bool Required => Default is null;
}

/// <summary>
/// What a command answers with: the lines the surface will draw, and whether the menu should be
/// redrawn under them.
/// <para>
/// Every line carries the glyph grammar of design §2.3 - <c>+</c> did it, <c>-</c> did not,
/// <c>!</c> refused by a gate, <c>?</c> here is the usage, <c>*</c> a status note - so the owner
/// can read a pane of replies without reading the words. Nothing is ever answered with silence:
/// <see cref="ConsoleEngine"/> substitutes a <c>?</c> line for an empty reply.
/// </para>
/// </summary>
/// <param name="Lines">One console line each; never empty strings (the renderer's blank is one space).</param>
/// <param name="Ok">False when the command refused or failed - used for the log line only.</param>
/// <param name="Redraw">False suppresses the menu redraw that normally follows a menu-driven run.</param>
public sealed record ConsoleReply(IReadOnlyList<string> Lines, bool Ok = true, bool Redraw = true)
{
    /// <summary>A machine-facing UI refresh has no console output; ordinary empty replies retain their diagnostic fallback.</summary>
    public bool SuppressOutput { get; init; }

    /// <summary>A reply that says nothing; the engine turns it into the "ran, see the host log" line.</summary>
    public static ConsoleReply Silent { get; } = new([], Ok: true);

    /// <summary><c>+ text</c> - it worked.</summary>
    public static ConsoleReply Did(string text) => new([Glyph('+', text)]);

    /// <summary><c>+ text</c> for each line - it worked and there is more than one line to say.</summary>
    public static ConsoleReply DidAll(params string[] lines) => new([.. lines.Select(l => Glyph('+', l))]);

    /// <summary><c>- text</c> - it did not work, and this is why.</summary>
    public static ConsoleReply Failed(string text) => new([Glyph('-', text)], Ok: false);

    /// <summary><c>! text</c> - a tier or phase gate refused it. Never silent (design §4.2).</summary>
    public static ConsoleReply Refused(string text) => new([Glyph('!', text)], Ok: false);

    /// <summary><c>? text</c> - the arguments were wrong; this is the usage.</summary>
    public static ConsoleReply Usage(string text) => new([Glyph('?', text)], Ok: false);

    /// <summary><c>* text</c> - a status or event note.</summary>
    public static ConsoleReply Note(string text) => new([Glyph('*', text)]);

    /// <summary>Plain lines that already carry their own glyph (a <c>/help</c> page, an <c>/info</c> frame).</summary>
    public static ConsoleReply Plain(IEnumerable<string> lines) => new([.. lines.Select(NonEmpty)]);

    private static string Glyph(char glyph, string text) =>
        string.IsNullOrWhiteSpace(text) ? $"{glyph} " : $"{glyph} {text}";

    /// <summary>
    /// A line the surfaces can actually carry. <c>ConsolePrint</c> and <c>ChatText</c> throw on an
    /// empty string because the client's <c>PrintConsole</c> (<c>FUN_141250c70</c>) early-outs on
    /// empty text and the line would vanish, so a blank is one space (design §1.3).
    /// </summary>
    public static string NonEmpty(string line) => string.IsNullOrEmpty(line) ? " " : line;
}

/// <summary>
/// One command: everything the typed door, the menu and <c>/help</c> need to know about it.
/// <para>
/// The name is the key the client will hash (<see cref="CommandHash"/>), so it must survive
/// <see cref="CommandRegistry.Register"/>'s collision test against the client's own 1,025-entry
/// registry (<see cref="ClientRegistry1148"/>) - a name the client already owns is refused at
/// registration rather than becoming an un-typeable menu row (design §1.4).
/// </para>
/// <para>
/// <b>Two constructors on purpose.</b> The positional one is the design's §4.1 signature, so a
/// backend lane can write a command straight from the design; the object-initialiser form is the
/// same thing with defaults, which is how a command with no arguments reads best.
/// </para>
/// </summary>
public sealed record ConsoleCommand
{
    /// <summary>The typed name, without the slash. ASCII, at most 16 characters.</summary>
    public required string Name { get; init; }

    /// <summary>Other names that reach the same command; each is pushed and hashed separately.</summary>
    public string[] Aliases { get; init; } = [];

    /// <summary>The <c>/help</c> group: player, items, vehicles, loot, match, world, players, debug, info, menu.</summary>
    public required string Group { get; init; }

    /// <summary>The lowest tier allowed to run it.</summary>
    public ConsoleTier Tier { get; init; } = ConsoleTier.Player;

    /// <summary>The match phase it needs.</summary>
    public MatchGate Gate { get; init; } = MatchGate.Any;

    /// <summary>The one-line usage, e.g. <c>/give &lt;item|id&gt; [count]</c>.</summary>
    public required string Usage { get; init; }

    /// <summary>The one-line description shown on the menu's info line and by <c>/help</c>.</summary>
    public required string Summary { get; init; }

    /// <summary>The declared arguments, in order.</summary>
    public ArgSpec[] Args { get; init; } = [];

    /// <summary>True when the argument tail keeps its case (<c>/announce</c>, <c>/raw</c>).</summary>
    public bool KeepCase { get; init; }

    /// <summary>True when arguments contain secrets and must be redacted from console logs.</summary>
    public bool SensitiveArguments { get; init; }

    /// <summary>The body. Called with the caller's context, this command and the parsed line.</summary>
    public required Func<CommandCall, ConsoleReply> Run { get; init; }

    /// <summary>True when a menu selection must be confirmed before it runs (design §2.6).</summary>
    public bool Confirm { get; init; }

    /// <summary>
    /// Non-null marks a LATER row: the menu draws <c>(n/a)</c> and the command answers
    /// <c>- not available yet -- &lt;reason&gt;</c>. The command is still registered and still
    /// typed-reachable, so the answer explains itself instead of looking broken.
    /// </summary>
    public string? NotYet { get; init; }

    /// <summary>
    /// False for <c>help</c> alone: it is reached through the client's HELP catch-all hash and must
    /// never be pushed with <c>AddWorldCommand</c> (design §1.4, §4.2).
    /// </summary>
    public bool Registered { get; init; } = true;

    /// <summary>Example lines shown by <c>/help &lt;command&gt;</c>.</summary>
    public string[] Examples { get; init; } = [];

    /// <summary>Menu paths shown by <c>/help &lt;command&gt;</c>, e.g. <c>Items &gt; Give item...</c>.</summary>
    public string[] MenuPaths { get; init; } = [];

    /// <summary>The longer explanation shown by <c>/help &lt;command&gt;</c>, one line per element.</summary>
    public string[] Detail { get; init; } = [];

    /// <summary>Object-initialiser form; every optional member has the default the design names.</summary>
    public ConsoleCommand()
    {
    }

    /// <summary>The design's §4.1 positional signature, so a backend can be written straight from it.</summary>
    [SetsRequiredMembers]
    public ConsoleCommand(
        string name,
        string[] aliases,
        string group,
        ConsoleTier tier,
        MatchGate gate,
        string usage,
        string summary,
        ArgSpec[] args,
        bool keepCase,
        Func<CommandCall, ConsoleReply> run,
        bool confirm = false,
        string? notYet = null,
        bool registered = true)
    {
        Name = name;
        Aliases = aliases;
        Group = group;
        Tier = tier;
        Gate = gate;
        Usage = usage;
        Summary = summary;
        Args = args;
        KeepCase = keepCase;
        Run = run;
        Confirm = confirm;
        NotYet = notYet;
        Registered = registered;
    }

    /// <summary>Every name this command answers to: <see cref="Name"/> first, then the aliases.</summary>
    public IEnumerable<string> AllNames
    {
        get
        {
            yield return Name;
            foreach (string alias in Aliases)
            {
                yield return alias;
            }
        }
    }

    /// <summary>
    /// A LATER row: registered, typed-reachable, greyed in the menu, and honest about why.
    /// </summary>
    public static ConsoleCommand NotAvailable(
        string name,
        string group,
        string usage,
        string summary,
        string reason,
        ConsoleTier tier = ConsoleTier.Tester,
        MatchGate gate = MatchGate.Any,
        string[]? aliases = null) => new()
        {
            Name = name,
            Aliases = aliases ?? [],
            Group = group,
            Tier = tier,
            Gate = gate,
            Usage = usage,
            Summary = summary,
            NotYet = reason,
            Run = _ => ConsoleReply.Failed($"not available yet -- {reason}"),
        };
}
