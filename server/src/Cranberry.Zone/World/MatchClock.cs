namespace Cranberry.Zone.World;

/// <summary>
/// Simulation time. No system ever reads the host's wall clock: the only inputs to a tick are the
/// drained command queue and this value, which is what makes a recorded trace replay identically
/// (docs/22 §5.1, §9.1).
/// </summary>
/// <param name="Tick">Fixed-step index; the first step executed is 0.</param>
/// <param name="ElapsedMs">Match-clock milliseconds at the start of this step: <c>Tick × 50</c>.</param>
public readonly record struct TickTime(long Tick, long ElapsedMs)
{
    public float DeltaSeconds => MatchClock.FixedDeltaSeconds;

    /// <summary>
    /// Tick-striding for sub-rate systems, phase-offset so concurrent matches never land their
    /// heavy passes on the same tick. <c>Every(1)</c> is every tick.
    /// </summary>
    public bool Every(int stride, int phase = 0)
    {
        if (stride <= 1)
        {
            return true;
        }

        long offset = (Tick + phase) % stride;
        return offset == 0;
    }
}

/// <summary>
/// The fixed 20 Hz match clock. 50 ms is an exact multiple of the gateway listener's own 20 ms
/// cadence (<c>SoeListenerOptions.TickIntervalMs</c>) and matches the rate the August client streams
/// channel-2 movement at once it is running, so simulating faster buys nothing (docs/22 §5.1).
/// </summary>
public sealed class MatchClock
{
    public const int Hz = 20;
    public const int FixedDeltaMs = 1000 / Hz;
    public const float FixedDeltaSeconds = 1f / Hz;

    /// <summary>200 ms; beyond that a pump drops time instead of replaying a backlog.</summary>
    public const int MaxCatchUpTicks = 4;

    private long _tick = -1;

    /// <summary>Index of the step currently running; −1 before the first <see cref="Advance"/>.</summary>
    public long Tick => _tick;

    public long ElapsedMs => _tick < 0 ? 0 : _tick * FixedDeltaMs;

    public TickTime Now => new(_tick < 0 ? 0 : _tick, ElapsedMs);

    /// <summary>Moves to the next step and returns its time.</summary>
    public TickTime Advance()
    {
        _tick++;
        return new TickTime(_tick, _tick * FixedDeltaMs);
    }
}
