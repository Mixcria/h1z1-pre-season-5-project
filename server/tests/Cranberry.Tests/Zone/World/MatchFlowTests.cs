using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// The phase table of docs/22 §4.6 driven by the tick scheduler: Forming -> Lobby -> Countdown ->
// Dropping -> Live -> Ending -> Finished, with every transition carried by a named timer action.
public sealed class MatchFlowTests
{
    private static MatchSettings Fast() => MatchSettings.Default with
    {
        MinPlayersToStart = 2,
        LobbyCountdownMs = 1_000,
        DropDurationMs = 1_000,
        EndingDurationMs = 500,
    };

    [Fact]
    public void AMatchWithNoPlayersStaysForming()
    {
        var harness = new MatchHarness();
        harness.Step(50);

        Assert.Equal(MatchPhase.Forming, harness.Match.Phase);
    }

    [Fact]
    public void TheFirstPlayerMovesTheMatchIntoTheLobby()
    {
        var harness = new MatchHarness(Fast());
        harness.AddPlayer("Alice");
        harness.Step();

        Assert.Equal(MatchPhase.Lobby, harness.Match.Phase);
    }

    [Fact]
    public void TheCountdownArmsOnlyWhenEnoughPlayersAreIn()
    {
        var harness = new MatchHarness(Fast());
        harness.AddPlayer("Alice");
        harness.Step(20);
        Assert.Equal(MatchPhase.Lobby, harness.Match.Phase);

        harness.AddPlayer("Bob");
        harness.Step();
        Assert.Equal(MatchPhase.Countdown, harness.Match.Phase);
        Assert.Equal(1, harness.Match.Scheduler.PendingCount);
    }

    [Fact]
    public void TheCountdownAbortsBackToLobbyWhenEveryoneLeaves()
    {
        var harness = new MatchHarness(Fast());
        MatchPlayer alice = harness.AddPlayer("Alice");
        MatchPlayer bob = harness.AddPlayer("Bob");
        harness.Step(2);
        Assert.Equal(MatchPhase.Countdown, harness.Match.Phase);

        harness.Match.RemovePlayer(alice);
        harness.Match.RemovePlayer(bob);
        harness.Step();

        Assert.Equal(MatchPhase.Lobby, harness.Match.Phase);

        // The abort also cancels its own StartMatch: nothing fires when its due tick arrives.
        harness.StepSeconds(1.5);
        Assert.Equal(MatchPhase.Lobby, harness.Match.Phase);
        Assert.Equal(0, harness.Match.TimersFired);
    }

    [Fact]
    public void AnAbortedCountdownDoesNotDropTheNextMatchEarly()
    {
        // The aborted countdown's StartMatch has Subject = EntityId.None, so neither guard in
        // Match.Fire stops it and it is still legal in Countdown: left armed, it fires at its
        // original due tick and gives the next lobby a one-tick countdown instead of a full one.
        var harness = new MatchHarness(Fast());
        MatchPlayer alice = harness.AddPlayer("Alice");
        MatchPlayer bob = harness.AddPlayer("Bob");
        harness.Step(2);
        Assert.Equal(MatchPhase.Countdown, harness.Match.Phase);

        harness.Match.RemovePlayer(alice);
        harness.Match.RemovePlayer(bob);
        harness.Step();
        Assert.Equal(MatchPhase.Lobby, harness.Match.Phase);

        // Re-arm most of a countdown late, then step past the FIRST countdown's due tick.
        harness.StepSeconds(0.9);
        harness.AddPlayer("Carol");
        harness.AddPlayer("Dave");
        harness.Step(2);
        Assert.Equal(MatchPhase.Countdown, harness.Match.Phase);

        harness.StepSeconds(0.5);
        Assert.Equal(MatchPhase.Countdown, harness.Match.Phase);

        harness.StepSeconds(0.6);
        Assert.Equal(MatchPhase.Dropping, harness.Match.Phase);
    }

    [Fact]
    public void TheStartMatchTimerOpensTheDrop()
    {
        var harness = new MatchHarness(Fast());
        harness.AddPlayer("Alice");
        harness.AddPlayer("Bob");

        harness.StepSeconds(1.2);

        Assert.Equal(MatchPhase.Dropping, harness.Match.Phase);
        Assert.Equal(TimerKind.StartMatch, harness.Match.LastFired.Kind);
    }

    [Fact]
    public void TheDropEndsInALiveMatch()
    {
        var harness = new MatchHarness(Fast());
        harness.AddPlayer("Alice");
        harness.AddPlayer("Bob");

        harness.StepSeconds(2.5);

        Assert.Equal(MatchPhase.Live, harness.Match.Phase);
    }

    [Fact]
    public void TheLastPlayerAliveEndsAndThenFinishesTheMatch()
    {
        var harness = new MatchHarness(Fast());
        MatchPlayer alice = harness.AddPlayer("Alice");
        harness.AddPlayer("Bob");
        harness.StepSeconds(2.5);
        Assert.Equal(MatchPhase.Live, harness.Match.Phase);

        harness.Match.Damage(alice, harness.Settings.StartingHealth, DamageCause.ToxicGas);
        harness.Step();

        Assert.Equal(MatchPhase.Ending, harness.Match.Phase);
        Assert.Equal(1, harness.Match.Flow.Deaths);

        harness.StepSeconds(0.6);
        Assert.Equal(MatchPhase.Finished, harness.Match.Phase);
    }

    [Fact]
    public void GasStartsAtTheStartMatchTransitionWhenItIsEnabled()
    {
        var harness = new MatchHarness(Fast() with { GasEnabled = true });
        harness.AddPlayer("Alice");
        harness.AddPlayer("Bob");

        harness.Step(2);
        Assert.False(harness.Match.Gas.Running);

        harness.StepSeconds(1.2);
        Assert.Equal(MatchPhase.Dropping, harness.Match.Phase);
        Assert.True(harness.Match.Gas.Running);
    }

    [Fact]
    public void GasStaysStoppedWhenTheGateIsOff()
    {
        var harness = new MatchHarness(Fast());
        harness.AddPlayer("Alice");
        harness.AddPlayer("Bob");
        harness.StepSeconds(2.5);

        Assert.Equal(MatchPhase.Live, harness.Match.Phase);
        Assert.False(harness.Match.Gas.Running);
    }

    [Fact]
    public void OnlyTheDeclaredTransitionsExist()
    {
        Assert.True(MatchPhases.CanTransition(MatchPhase.Forming, MatchPhase.Lobby));
        Assert.True(MatchPhases.CanTransition(MatchPhase.Countdown, MatchPhase.Dropping));
        Assert.True(MatchPhases.CanTransition(MatchPhase.Dropping, MatchPhase.Live));
        Assert.True(MatchPhases.CanTransition(MatchPhase.Live, MatchPhase.Ending));
        Assert.True(MatchPhases.CanTransition(MatchPhase.Ending, MatchPhase.Finished));

        Assert.False(MatchPhases.CanTransition(MatchPhase.Forming, MatchPhase.Live));
        Assert.False(MatchPhases.CanTransition(MatchPhase.Lobby, MatchPhase.Dropping));
        Assert.False(MatchPhases.CanTransition(MatchPhase.Live, MatchPhase.Lobby));
        Assert.False(MatchPhases.CanTransition(MatchPhase.Live, MatchPhase.Live));
        Assert.False(MatchPhases.CanTransition(MatchPhase.Finished, MatchPhase.Lobby));
    }

    [Fact]
    public void AnAbandonedMatchCanFinishFromAnyPhase()
    {
        foreach (MatchPhase phase in Enum.GetValues<MatchPhase>())
        {
            Assert.Equal(phase != MatchPhase.Finished, MatchPhases.CanTransition(phase, MatchPhase.Finished));
        }
    }

    [Fact]
    public void AnIllegalTransitionIsRefusedRatherThanApplied()
    {
        var match = new Match(1);

        Assert.False(match.TryTransition(MatchPhase.Live));
        Assert.Equal(MatchPhase.Forming, match.Phase);

        Assert.True(match.TryTransition(MatchPhase.Lobby));
        Assert.Equal(MatchPhase.Lobby, match.Phase);
    }
}
