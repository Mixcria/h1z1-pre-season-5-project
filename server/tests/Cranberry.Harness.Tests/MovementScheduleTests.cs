using Cranberry.NetworkBots;

namespace Cranberry.Harness.Tests;

public sealed class MovementScheduleTests
{
    [Fact]
    public void SixtyHzUsesAbsoluteRoundedUpDeadlinesWithoutLongRunDrift()
    {
        const long frequency = 10_000_000;
        var schedule = new MovementSchedule(frequency, 60);
        Assert.Equal(166_667, schedule.NextDeadlineTicks);
        Assert.False(schedule.TryTake(0, out _));
        for (long sequence = 1; sequence <= 60 * 60 * 60; sequence++)
        {
            // Integer seconds plus fractional remainder is an independent oracle.
            long expected = sequence / 60 * frequency
                + (sequence % 60 * frequency + 59) / 60;
            Assert.Equal(expected, schedule.NextDeadlineTicks);
            Assert.False(schedule.TryTake(expected - 1, out _));
            Assert.True(schedule.TryTake(expected, out MovementFrame frame));
            Assert.Equal(sequence, frame.Sequence);
            Assert.Equal(expected, frame.ElapsedTicks);
            Assert.Equal(0, frame.SkippedFrames);
            Assert.False(schedule.TryTake(expected, out _));
        }
        Assert.Equal(216_000, schedule.EmittedFrames);
        Assert.Equal(0, schedule.SkippedFrames);
        Assert.Equal(36_000_166_667L, schedule.NextDeadlineTicks);
    }

    [Fact]
    public void TwentyFourMillisecondCadenceIsExactRationalRate()
    {
        var schedule = new MovementSchedule(1000, 125, 3);
        for (long sequence = 1; sequence <= 125_000; sequence++)
        {
            Assert.Equal(sequence * 24, schedule.NextDeadlineTicks);
            Assert.True(schedule.TryTake(sequence * 24, out var frame));
            Assert.Equal(sequence, frame.Sequence);
        }
        Assert.Equal(3_000_024, schedule.NextDeadlineTicks);
        Assert.Equal(0, schedule.SkippedFrames);
    }

    [Fact]
    public void MillisecondClockRoundsDeadlinesUpInsteadOfUsingSixteenMillisecondPeriod()
    {
        var schedule = new MovementSchedule(1000, 60);
        foreach (long deadline in new long[] { 17, 34, 50, 67, 84, 100 })
        {
            Assert.Equal(deadline, schedule.NextDeadlineTicks);
            Assert.False(schedule.TryTake(deadline - 1, out _));
            Assert.True(schedule.TryTake(deadline, out _));
        }
    }

    [Fact]
    public void LongStallEmitsNewestDueFrameOnceAndPreservesActualElapsedTime()
    {
        var schedule = new MovementSchedule(1000, 60);
        Assert.True(schedule.TryTake(17, out _));
        Assert.True(schedule.TryTake(1005, out var frame));
        Assert.Equal(new MovementFrame(60, 1005, 58), frame);
        Assert.Equal(2, schedule.EmittedFrames);
        Assert.Equal(58, schedule.SkippedFrames);
        Assert.Equal(1017, schedule.NextDeadlineTicks);
        Assert.False(schedule.TryTake(1005, out _));
        Assert.False(schedule.TryTake(1016, out _));
        Assert.True(schedule.TryTake(1017, out frame));
        Assert.Equal(new MovementFrame(61, 1017, 0), frame);
        Assert.Equal(frame.Sequence, schedule.EmittedFrames + schedule.SkippedFrames);
    }

    [Fact]
    public void LateFirstTakeAccountsForSkippedFramesFromPhaseStart()
    {
        var schedule = new MovementSchedule(1000, 60);
        Assert.True(schedule.TryTake(10_000, out var frame));
        Assert.Equal(new MovementFrame(600, 10_000, 599), frame);
        Assert.False(schedule.TryTake(10_000, out _));
        Assert.Equal(1, schedule.EmittedFrames);
    }

    [Fact]
    public void BackwardTimeRejectedEvenAfterAnEarlyPollWithoutConsumingState()
    {
        var schedule = new MovementSchedule(1000, 60);
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.TryTake(-1, out _));
        Assert.False(schedule.TryTake(10, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.TryTake(9, out _));
        Assert.Equal(17, schedule.NextDeadlineTicks);
        Assert.Equal(0, schedule.EmittedFrames);
        Assert.True(schedule.TryTake(17, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.TryTake(16, out _));
        Assert.False(schedule.TryTake(17, out _));
        Assert.Equal(1, schedule.EmittedFrames);
    }

    [Theory]
    [InlineData(0L, 60, 1)]
    [InlineData(-1L, 60, 1)]
    [InlineData(1000L, 0, 1)]
    [InlineData(1000L, 60, 0)]
    [InlineData(1000L, -1, 1)]
    [InlineData(1000L, 60, -1)]
    [InlineData(10L, 60, 1)]
    public void InvalidOrSubTimestampCadencesReject(long frequency, int numerator, int denominator)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MovementSchedule(frequency, numerator, denominator));
    }

    [Fact]
    public void LargeProductsUseInt128AndUnrepresentableNextDeadlineDoesNotMutate()
    {
        var large = new MovementSchedule(long.MaxValue, int.MaxValue, int.MaxValue);
        Assert.Equal(long.MaxValue, large.NextDeadlineTicks);
        Assert.Throws<OverflowException>(() => new MovementSchedule(long.MaxValue, 1, 2));
        var schedule = new MovementSchedule(1, 1);
        Assert.True(schedule.TryTake(long.MaxValue - 1, out var frame));
        Assert.Equal(long.MaxValue - 1, frame.Sequence);
        Assert.Equal(long.MaxValue - 2, frame.SkippedFrames);
        Assert.Throws<OverflowException>(() => schedule.TryTake(long.MaxValue, out _));
        Assert.Equal(long.MaxValue, schedule.NextDeadlineTicks);
        Assert.Equal(1, schedule.EmittedFrames);
        Assert.Equal(long.MaxValue - 2, schedule.SkippedFrames);
        Assert.False(schedule.TryTake(long.MaxValue - 1, out _));
    }
}
