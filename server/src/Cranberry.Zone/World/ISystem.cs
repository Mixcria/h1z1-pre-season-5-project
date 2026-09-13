namespace Cranberry.Zone.World;

/// <summary>
/// What one system is handed. A readonly struct passed by <c>in</c>: never stored, never captured.
/// It carries no clock of its own — <see cref="Time"/> is the only time a system may read, which is
/// what makes a recorded trace replayable (docs/22 §4.5, §9.1).
/// </summary>
public readonly record struct TickContext(Match Match, TickTime Time)
{
    public World World => Match.World;

    public MatchSettings Settings => Match.Settings;
}

/// <summary>Every system: one method, no I/O, no wall clock, no <c>SoeConnection</c>.</summary>
public interface ISystem
{
    void Tick(in TickContext context);
}
