namespace Cranberry.Zone.DevConsole;

/// <summary>
/// One invocation: who is calling, what they called, and how they typed it. This is the only
/// argument a command body takes, so the same body serves a typed <c>/give ar15</c> and the menu
/// leaf that builds the identical line - the "one engine, two front doors" rule made literal
/// (design §0 item 5, §4.4).
/// </summary>
/// <param name="Ctx">The caller's world: surface, log, position, backends.</param>
/// <param name="Cmd">The command being run - its own metadata, for usage and gate messages.</param>
/// <param name="Line">The parsed argument tail.</param>
/// <param name="Session">The caller's console session: tier, toggles, menu, settings.</param>
/// <param name="FromMenu">True when a menu leaf produced this call rather than a typed line.</param>
public sealed record CommandCall(
    ConsoleContext Ctx,
    ConsoleCommand Cmd,
    CommandLine Line,
    ConsoleSession Session,
    bool FromMenu = false)
{
    /// <summary>The caller's tier.</summary>
    public ConsoleTier Tier => Session.Tier;

    /// <summary>The live match step word.</summary>
    public string Step => Ctx.Step();

    /// <summary>How many argument tokens were typed.</summary>
    public int Count => Line.Count;

    /// <summary>The command's own usage line, as a <c>?</c> reply.</summary>
    public ConsoleReply Usage() => ConsoleReply.Usage(Cmd.Usage);

    /// <summary>The command's usage with one extra clause, as a <c>?</c> reply.</summary>
    public ConsoleReply Usage(string because) => ConsoleReply.Usage($"{Cmd.Usage}   -- {because}");
}
