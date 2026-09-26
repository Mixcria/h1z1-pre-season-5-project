namespace Cranberry.NetworkBots;

internal readonly record struct MovementEmissionBatch(bool PlayerDue, bool CanopyDue, long ElapsedTicks);
internal sealed class MovementEmissionStream
{
    internal readonly MovementSchedule Schedule;
    internal readonly int Rate;
    public long EmittedFrames => Schedule.EmittedFrames;
    public long SkippedFrames => Schedule.SkippedFrames;
    public long MaximumLatenessTicks { get; private set; }
    public long TotalLatenessTicks { get; private set; }
    public long LongestSkippedRun { get; private set; }
    internal MovementEmissionStream(long frequency, int rate) { Schedule = new(frequency, rate); Rate = rate; }
    internal bool Take(long elapsed)
    {
        long deadline = Schedule.NextDeadlineTicks;
        if (!Schedule.TryTake(elapsed, out var frame)) return false;
        long lateness = elapsed - deadline;
        MaximumLatenessTicks = Math.Max(MaximumLatenessTicks, lateness);
        TotalLatenessTicks = checked(TotalLatenessTicks + lateness);
        LongestSkippedRun = Math.Max(LongestSkippedRun, frame.SkippedFrames);
        return true;
    }
}

/// <summary>Explicit independent input streams; never changes legacy scheduling implicitly.</summary>
internal sealed class MovementEmissionSchedule
{
    private readonly long _frequency;
    private long _lastObserved;
    public MovementEmissionStream Player { get; }
    public MovementEmissionStream? Canopy { get; }
    public long NextDeadlineTicks => Math.Min(Player.Schedule.NextDeadlineTicks, Canopy?.Schedule.NextDeadlineTicks ?? long.MaxValue);
    public MovementEmissionSchedule(long frequency, int playerHz, int? canopyHz = null)
    {
        _frequency = frequency;
        Player = new(frequency, playerHz);
        if (canopyHz is { } rate) Canopy = new(frequency, rate);
    }
    public bool TryTake(long elapsed, out MovementEmissionBatch batch)
    {
        batch = default;
        if (elapsed < _lastObserved) throw new ArgumentOutOfRangeException(nameof(elapsed));
        // Validate both future deadlines and timing sums before either stream mutates.
        Preflight(Player, elapsed);
        if (Canopy is not null) Preflight(Canopy, elapsed);
        bool player = Player.Take(elapsed), canopy = Canopy?.Take(elapsed) ?? false;
        _lastObserved = elapsed;
        batch = new(player, canopy, elapsed);
        return player || canopy;
    }
    private void Preflight(MovementEmissionStream stream, long elapsed)
    {
        if (elapsed < stream.Schedule.NextDeadlineTicks) return;
        Int128 sequence = (Int128)elapsed * stream.Rate / _frequency;
        _ = checked((long)(((sequence + 1) * _frequency + stream.Rate - 1) / stream.Rate));
        _ = checked(stream.TotalLatenessTicks + (elapsed - stream.Schedule.NextDeadlineTicks));
    }
}
