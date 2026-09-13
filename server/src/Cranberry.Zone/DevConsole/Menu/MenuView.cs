namespace Cranberry.Zone.DevConsole.Menu;

/// <summary>
/// Everything a frame needs that is not in the tree or the state: who is looking, what phase the
/// match is in, and the live value of any row that has one.
/// <para>
/// It is a read-only view built fresh for each input, which is what keeps
/// <see cref="MenuState.Apply"/> pure: the state machine never reaches into a session or a service,
/// it is handed the answers.
/// </para>
/// </summary>
public sealed class MenuView
{
    /// <summary>The caller's frame settings.</summary>
    public ConsoleSettings Settings { get; init; } = ConsoleSettings.Default;

    /// <summary>The caller's tier. Rows above it are hidden unless <c>Show all rows</c> is on.</summary>
    public ConsoleTier Tier { get; init; } = ConsoleTier.Player;

    /// <summary>The live match step word, for the phase gate and the root's note column.</summary>
    public string MatchStep { get; init; } = "Menu";

    /// <summary>The server build shown in the title line.</summary>
    public string ServerVersion { get; init; } = "dev";

    /// <summary>The client build shown in the title line.</summary>
    public string ClientBuild { get; init; } = "1148";

    /// <summary>The live state of a toggle row.</summary>
    public Func<MenuNode, bool>? Toggle { get; init; }

    /// <summary>The live value of a row that overrides its own preset cycle (a Settings row).</summary>
    public Func<MenuNode, string?>? Value { get; init; }

    /// <summary>The live right-hand note of a row - the root's <c>InMatch 04:12</c>, <c>1 online</c>.</summary>
    public Func<MenuNode, string?>? Note { get; init; }

    /// <summary>Whether a toggle row is on.</summary>
    public bool IsOn(MenuNode node) => Toggle?.Invoke(node) ?? false;

    /// <summary>The live override of a value row, or null to use the state's own cycle position.</summary>
    public string? ValueOf(MenuNode node) => Value?.Invoke(node);

    /// <summary>The live note of a row, or null.</summary>
    public string? NoteOf(MenuNode node) => Note?.Invoke(node);

    /// <summary>True when the caller may see rows above his tier, greyed (Owner only).</summary>
    public bool ShowAllRows => Settings.ShowAllRows && Tier == ConsoleTier.Owner;
}
