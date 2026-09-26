namespace Cranberry.NetworkBots;

internal readonly record struct MovementFrame(long Sequence, long ElapsedTicks, long SkippedFrames);

/// <summary>
/// Absolute rational movement deadlines in the caller's monotonic timestamp units.
/// The first frame is due after one period. Late calls emit only the newest due
/// frame; skipped frames never become a catch-up burst. This schedules bot input,
/// not the server simulation or native-client send cadence.
/// </summary>
internal sealed class MovementSchedule
{
    private readonly Int128 _periodNumerator;
    private readonly int _rateNumerator;
    private long _lastObservedTicks;
    private long _lastSequence;

    public MovementSchedule(long timestampFrequency, int rateNumerator, int rateDenominator = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rateNumerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rateDenominator);
        _periodNumerator = (Int128)timestampFrequency * rateDenominator;
        if (rateNumerator > _periodNumerator)
            throw new ArgumentOutOfRangeException(nameof(rateNumerator), "Rate exceeds timestamp resolution.");
        _rateNumerator = rateNumerator;
        NextDeadlineTicks = Deadline(1);
    }

    public long NextDeadlineTicks { get; private set; }
    public long EmittedFrames { get; private set; }
    public long SkippedFrames { get; private set; }

    /// <summary>
    /// Elapsed time is measured from one fixed phase origin. Repeated timestamps
    /// are permitted; negative/backward time is rejected. A next deadline outside
    /// Int64 throws before changing state. Phase-end bounds belong to the caller.
    /// </summary>
    public bool TryTake(long elapsedTicks, out MovementFrame frame)
    {
        frame = default;
        if (elapsedTicks < _lastObservedTicks)
            throw new ArgumentOutOfRangeException(nameof(elapsedTicks), "Elapsed time must be nonnegative and monotonic.");
        if (elapsedTicks < NextDeadlineTicks)
        {
            _lastObservedTicks = elapsedTicks;
            return false;
        }

        long sequence = checked((long)((Int128)elapsedTicks * _rateNumerator / _periodNumerator));
        long nextDeadline = Deadline((Int128)sequence + 1);
        long skipped = checked(sequence - _lastSequence - 1);
        long emittedTotal = checked(EmittedFrames + 1);
        long skippedTotal = checked(SkippedFrames + skipped);
        frame = new MovementFrame(sequence, elapsedTicks, skipped);
        _lastObservedTicks = elapsedTicks;
        _lastSequence = sequence;
        NextDeadlineTicks = nextDeadline;
        EmittedFrames = emittedTotal;
        SkippedFrames = skippedTotal;
        return true;
    }

    private long Deadline(Int128 sequence)
    {
        Int128 numerator = checked(sequence * _periodNumerator);
        return checked((long)((numerator + _rateNumerator - 1) / _rateNumerator));
    }
}
