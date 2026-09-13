using Cranberry.Zone.Gas;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

/// <summary>
/// docs/109 §5 (the S1 §5.4 / D67 minimum): two sessions in one match must at least agree on the
/// gas ring. The whole fix is that <c>GasController</c> is a pure function of
/// <c>(seed, matchClockMs)</c>, so sharing those two values makes the two schedules identical —
/// and these tests prove that claim rather than assuming it.
/// </summary>
public sealed class SharedMatchGasTests
{
    [Fact]
    public void TheFirstSessionOpensThePlanWithItsOwnDraw()
    {
        var shared = new SharedMatchGas();
        ulong seed = 0xDEADBEEFul;
        long startedAt = 12_345;

        Assert.False(shared.Join(ref seed, ref startedAt));

        Assert.True(shared.Active);
        Assert.Equal(0xDEADBEEFul, seed);
        Assert.Equal(12_345, startedAt);
        Assert.Equal(1, shared.Members);
        Assert.Equal(0, shared.Joins);
    }

    [Fact]
    public void ASecondSessionAdoptsTheRunningPlanInsteadOfItsOwn()
    {
        var shared = new SharedMatchGas();
        ulong first = 0xDEADBEEFul;
        long firstStart = 12_345;
        shared.Join(ref first, ref firstStart);

        ulong second = 0x0123456789ABCDEFul;
        long secondStart = 987_654;
        Assert.True(shared.Join(ref second, ref secondStart));

        // Both values are replaced: the seed alone is not enough, because the phase, the radius and
        // every timer are read off the match clock, and a second session that started three minutes
        // later would otherwise be three minutes behind the same circle.
        Assert.Equal(first, second);
        Assert.Equal(firstStart, secondStart);
        Assert.Equal(2, shared.Members);
        Assert.Equal(1, shared.Joins);
    }

    [Fact]
    public void TwoControllersOnOneSharedPlanAgreeOnPhaseCentreRadiusAndTimers()
    {
        var settings = new GasSettings();
        var shared = new SharedMatchGas();

        ulong aliceSeed = 0xA11CEul;
        long aliceStart = 1_000;
        shared.Join(ref aliceSeed, ref aliceStart);
        var alice = new GasController(settings);
        GasSchedule alicePlan = alice.Start(aliceStart, aliceSeed);

        // Bob joins four minutes in with a completely different draw of his own.
        ulong bobSeed = 0xB0Bul;
        long bobStart = 241_000;
        Assert.True(shared.Join(ref bobSeed, ref bobStart));
        var bob = new GasController(settings);
        GasSchedule bobPlan = bob.Start(bobStart, bobSeed);

        Assert.Equal(alicePlan.Phases.Count, bobPlan.Phases.Count);
        Assert.Equal(alicePlan.FinishedAtMs, bobPlan.FinishedAtMs);
        Assert.Equal(alicePlan.InitialCircle.Centre, bobPlan.InitialCircle.Centre);
        Assert.Equal(alicePlan.InitialCircle.Radius, bobPlan.InitialCircle.Radius);

        for (int index = 1; index <= alicePlan.Phases.Count; index++)
        {
            GasPhase mine = alicePlan.Phase(index);
            GasPhase yours = bobPlan.Phase(index);
            Assert.Equal(mine.RevealAtMs, yours.RevealAtMs);
            Assert.Equal(mine.ShrinkStartAtMs, yours.ShrinkStartAtMs);
            Assert.Equal(mine.ClosedAtMs, yours.ClosedAtMs);
            Assert.Equal(mine.Target.Centre, yours.Target.Centre);
            Assert.Equal(mine.Target.Radius, yours.Target.Radius);
        }

        // And the ring they are damaging against at one instant of WALL clock is the same circle,
        // which is the thing the two players actually see.
        long now = 700_000;
        Assert.Equal(alice.ActiveCircleAt(now).Centre, bob.ActiveCircleAt(now).Centre);
        Assert.Equal(alice.ActiveCircleAt(now).Radius, bob.ActiveCircleAt(now).Radius);
    }

    [Fact]
    public void WithoutTheSharedPlanTheTwoSessionsPlayDifferentCircles()
    {
        var settings = new GasSettings();
        GasSchedule alice = new GasController(settings).Start(1_000, 0xA11CEul);
        GasSchedule bob = new GasController(settings).Start(241_000, 0xB0Bul);

        // The regression this lane exists to close, stated as a test: two unshared sessions play
        // two different matches, and it is the PHASE TARGETS that a player is running towards.
        //
        // D278 (docs/118 §3.2) also moved the OPENING circle into the per-match draw - the play
        // area leads toward whichever of the client's nine GasWeightArea volumes the match is
        // aimed at, by exactly the shortfall containment cannot cover - so its centre is no longer
        // a constant either. It is not asserted here because the lead is ZERO whenever the
        // destination is already in reach (about a fifth of matches, and both of these seeds), and
        // a test of the sharing must not depend on which volume a seed happens to draw. The RADIUS
        // is still the same setting for both, and the phase targets - what a player is running
        // towards - are what this test is about.
        Assert.Equal(alice.InitialCircle.Radius, bob.InitialCircle.Radius);
        Assert.NotEqual(alice.Phase(1).Target.Centre, bob.Phase(1).Target.Centre);
        Assert.NotEqual(alice.FinalCircle.Centre, bob.FinalCircle.Centre);
    }

    [Fact]
    public void TheLastSessionOutForgetsThePlanSoTheNextMatchDrawsAFreshOne()
    {
        var shared = new SharedMatchGas();
        ulong seed = 0xA11CEul;
        long startedAt = 1_000;
        shared.Join(ref seed, ref startedAt);
        ulong second = 1;
        long secondStart = 2;
        shared.Join(ref second, ref secondStart);

        shared.Leave();
        Assert.True(shared.Active);         // one player still in the match
        shared.Leave();
        Assert.False(shared.Active);

        ulong next = 0xFEEDul;
        long nextStart = 900_000;
        Assert.False(shared.Join(ref next, ref nextStart));
        Assert.Equal(0xFEEDul, next);
        Assert.Equal(900_000, nextStart);
    }

    [Fact]
    public void LeavingMoreOftenThanJoiningCannotDriveTheCountNegative()
    {
        var shared = new SharedMatchGas();
        shared.Leave();
        shared.Leave();

        Assert.Equal(0, shared.Members);
        Assert.False(shared.Active);
    }
}
