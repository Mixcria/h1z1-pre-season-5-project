using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// The fixed 20 Hz step and the bounded catch-up of docs/22 §5.1.
public sealed class MatchClockTests
{
    [Fact]
    public void TheFirstStepIsTickZeroAtTimeZero()
    {
        var clock = new MatchClock();
        TickTime first = clock.Advance();

        Assert.Equal(0, first.Tick);
        Assert.Equal(0, first.ElapsedMs);
    }

    [Fact]
    public void EachStepIsExactlyFiftyMilliseconds()
    {
        var clock = new MatchClock();
        clock.Advance();
        TickTime second = clock.Advance();

        Assert.Equal(1, second.Tick);
        Assert.Equal(MatchClock.FixedDeltaMs, second.ElapsedMs);
        Assert.Equal(0.05f, second.DeltaSeconds);
        Assert.Equal(20, MatchClock.Hz);
    }

    [Fact]
    public void OneSecondIsTwentySteps()
    {
        var clock = new MatchClock();
        TickTime now = default;
        for (int i = 0; i < 20; i++)
        {
            now = clock.Advance();
        }

        Assert.Equal(19, now.Tick);
        Assert.Equal(950, now.ElapsedMs);
    }

    [Fact]
    public void StrideOneIsEveryTick()
    {
        for (long tick = 0; tick < 10; tick++)
        {
            Assert.True(new TickTime(tick, tick * 50).Every(1));
            Assert.True(new TickTime(tick, tick * 50).Every(0));
        }
    }

    [Fact]
    public void StridingPicksEveryNthTick()
    {
        int hits = 0;
        for (long tick = 0; tick < 40; tick++)
        {
            if (new TickTime(tick, tick * 50).Every(4))
            {
                hits++;
            }
        }

        Assert.Equal(10, hits);
    }

    [Fact]
    public void PhaseOffsetsSpreadTwoMatchesOntoDifferentTicks()
    {
        var first = new List<long>();
        var second = new List<long>();
        for (long tick = 0; tick < 8; tick++)
        {
            var now = new TickTime(tick, tick * 50);
            if (now.Every(2, phase: 0))
            {
                first.Add(tick);
            }

            if (now.Every(2, phase: 1))
            {
                second.Add(tick);
            }
        }

        Assert.Equal(4, first.Count);
        Assert.Equal(4, second.Count);
        Assert.Empty(first.Intersect(second));
    }

    [Fact]
    public void TheFirstPumpOnlyEstablishesTheBaseline()
    {
        var match = new Match(1);

        Assert.Equal(0, match.Pump(10_000));
        Assert.Equal(-1, match.Clock.Tick);
    }

    [Fact]
    public void PumpRunsWholeTicksAndKeepsTheRemainder()
    {
        var match = new Match(1);
        match.Pump(0);

        Assert.Equal(2, match.Pump(120));   // 120 ms = 2 ticks, 20 ms carried
        Assert.Equal(1, match.Pump(150));   // 20 + 30 = 50 ms = 1 tick

        // Three steps have run and the first step is tick 0.
        Assert.Equal(2, match.Clock.Tick);
        Assert.Equal(0, match.TickDebt);
    }

    [Fact]
    public void PumpClampsCatchUpAndRecordsTheDebt()
    {
        var match = new Match(1);
        match.Pump(0);

        // A one-second stall: 20 ticks are due, 4 run, the rest are dropped rather than replayed.
        Assert.Equal(MatchClock.MaxCatchUpTicks, match.Pump(1_000));
        Assert.Equal(20 - MatchClock.MaxCatchUpTicks, match.TickDebt);

        // And the accumulator was cleared, so the next pump starts clean.
        Assert.Equal(1, match.Pump(1_050));
    }

    [Fact]
    public void PumpIgnoresTimeGoingBackwards()
    {
        var match = new Match(1);
        match.Pump(1_000);

        Assert.Equal(0, match.Pump(900));
        Assert.Equal(-1, match.Clock.Tick);
    }

    [Fact]
    public void PumpDrivesTheSimulationClock()
    {
        var match = new Match(1);
        match.Pump(0);
        match.Pump(500);

        Assert.Equal(MatchClock.MaxCatchUpTicks - 1, match.Clock.Tick);
        Assert.Equal((MatchClock.MaxCatchUpTicks - 1) * MatchClock.FixedDeltaMs, match.Clock.ElapsedMs);
    }
}
