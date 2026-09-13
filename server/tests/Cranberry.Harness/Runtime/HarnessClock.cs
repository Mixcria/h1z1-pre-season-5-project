using System.Diagnostics;

namespace Cranberry.Harness.Runtime;

/// <summary>
/// Monotonic time for the harness, plus one deliberate knob: <see cref="Scale"/>.
///
/// Every delay the harness imitates is a real client delay measured in the captures (docs/71),
/// and against a live server they must be honoured as measured — a 1.615 s hard-coded pause that
/// the harness shortens is no longer the client's behaviour. <see cref="Scale"/> exists so unit
/// tests of the state machine can run the same timings in milliseconds. Deadlines scale with it,
/// so an assertion budget keeps the same meaning at any scale.
/// </summary>
public sealed class HarnessClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    /// <summary>1.0 = real time, the only value valid against a live server.</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>Elapsed harness time since construction.</summary>
    public TimeSpan Now => _watch.Elapsed;

    /// <summary>Elapsed milliseconds, for the transport's resend timers.</summary>
    public long NowMs => _watch.ElapsedMilliseconds;

    /// <summary>Applies <see cref="Scale"/> to an observed client interval.</summary>
    public TimeSpan Scaled(TimeSpan interval) =>
        Scale == 1.0 ? interval : TimeSpan.FromTicks((long)(interval.Ticks * Scale));

    /// <summary>Waits an observed client interval, scaled.</summary>
    public Task DelayAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        TimeSpan scaled = Scaled(interval);
        return scaled <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(scaled, cancellationToken);
    }

    public static string Format(TimeSpan value) => $"{value.TotalSeconds,8:F3}s";
}
