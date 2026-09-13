using System.Numerics;
using Cranberry.Zone.Gas;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Gas;

public sealed class GasPauseTests
{
    [Fact]
    public void PauseFreezesMovingCircleExposureAndDamageThenResumesWithoutCatchup()
    {
        var gas = new GasController(new GasSettings());
        var schedule = gas.Start(1000);
        long now = 1000 + schedule.Phase(1).ShrinkStartAtMs + 10000;
        PlayerSample[] players = [new(0, new Vector3(100000, 0, 100000), true)];
        gas.Tick(now, players);
        uint exposure = gas.ToxicityForPlayer(0);
        var circle = gas.ActiveCircleAt(now);
        long clock = gas.MatchClockAt(now);
        Assert.True(gas.Pause(now));
        Assert.False(gas.Pause(now + 10000));
        Assert.True(gas.Running);
        Assert.False(gas.AdvanceToNextEvent(now + 50000));
        Assert.Equal(clock, gas.MatchClockAt(now + 50000));
        Assert.Equal(circle, gas.ActiveCircleAt(now + 50000));
        Assert.Empty(gas.Tick(now + 50000, players).Damage.ToArray());
        Assert.Equal(exposure, gas.ToxicityForPlayer(0));
        Assert.True(gas.Resume(now + 50000));
        Assert.False(gas.Resume(now + 50001));
        Assert.Equal(clock, gas.MatchClockAt(now + 50000));
        Assert.Empty(gas.Tick(now + 50000, players).Damage.ToArray());
        Assert.Single(gas.Tick(now + 50000 + gas.Settings.TickPeriodMs, players).Damage.ToArray());
    }

    [Fact]
    public void StopAndNewMatchDiscardPauseState()
    {
        var gas = new GasController(new GasSettings());
        Assert.False(gas.Pause(100));
        Assert.False(gas.Resume(100));
        gas.Start(100); gas.Pause(200); gas.Stop();
        Assert.False(gas.Paused);
        gas.Start(500);
        Assert.Equal(200, gas.MatchClockAt(700));
    }

    [Fact]
    public void LateSharedJoinInheritsPauseAndResumedClock()
    {
        var shared = new SharedMatchGas();
        ulong seed = 42;
        long start = 1000;
        shared.Join(ref seed, ref start);
        var first = new GasController(new GasSettings()); first.Start(start, seed); first.Pause(5000);
        shared.SynchronizeClock(first.StartMs, first.PausedAtMs);
        ulong otherSeed = 1; long otherStart = 100000;
        Assert.True(shared.Join(ref otherSeed, ref otherStart));
        var second = new GasController(new GasSettings()); second.Start(otherStart, otherSeed); second.Pause(shared.PausedAtMs!.Value);
        Assert.Equal(first.MatchClockAt(100000), second.MatchClockAt(100000));
        first.Resume(100000); second.Resume(100000);
        shared.SynchronizeClock(first.StartMs, first.PausedAtMs);
        shared.Join(ref otherSeed, ref otherStart);
        var third = new GasController(new GasSettings()); third.Start(otherStart, otherSeed);
        Assert.Null(shared.PausedAtMs);
        Assert.Equal(first.ActiveCircleAt(100100), third.ActiveCircleAt(100100));
        shared.Clear(); Assert.Null(shared.PausedAtMs);
    }
}
