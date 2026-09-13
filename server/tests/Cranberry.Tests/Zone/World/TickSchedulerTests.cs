using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// docs/22 §4.3: deterministic (dueTick, insertion) order, cancellation, and the two dispatch guards
// today's Later(...) carries (ZoneService.cs:934 liveness, :952 phase legality).
public sealed class TickSchedulerTests
{
    private sealed class RecordingTimerSink : ITimerSink
    {
        public List<ScheduledAction> Fired { get; } = [];

        public List<long> Ticks { get; } = [];

        public void Fire(in ScheduledAction action, TickTime now)
        {
            Fired.Add(action);
            Ticks.Add(now.Tick);
        }
    }

    private static TickTime At(long tick) => new(tick, tick * MatchClock.FixedDeltaMs);

    [Fact]
    public void NothingFiresBeforeItsDueTick()
    {
        var scheduler = new TickScheduler();
        var sink = new RecordingTimerSink();
        scheduler.At(5, new ScheduledAction(TimerKind.StartMatch, EntityId.None));

        for (long tick = 0; tick < 5; tick++)
        {
            Assert.Equal(0, scheduler.Advance(At(tick), sink));
        }

        Assert.Equal(1, scheduler.Advance(At(5), sink));
        Assert.Single(sink.Fired);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void ActionsFireInDueTickOrderRegardlessOfInsertionOrder()
    {
        var scheduler = new TickScheduler();
        var sink = new RecordingTimerSink();

        scheduler.At(9, new ScheduledAction(TimerKind.MatchEnd, EntityId.None));
        scheduler.At(3, new ScheduledAction(TimerKind.StartMatch, EntityId.None));
        scheduler.At(6, new ScheduledAction(TimerKind.ReleaseTeleport, EntityId.None));

        scheduler.Advance(At(100), sink);

        Assert.Equal(
            new[] { TimerKind.StartMatch, TimerKind.ReleaseTeleport, TimerKind.MatchEnd },
            sink.Fired.Select(action => action.Kind).ToArray());
    }

    [Fact]
    public void TiesFireInInsertionOrder()
    {
        var scheduler = new TickScheduler();
        var sink = new RecordingTimerSink();

        for (int i = 0; i < 20; i++)
        {
            scheduler.At(4, new ScheduledAction(TimerKind.LobbyHud, EntityId.None, Argument: i));
        }

        scheduler.Advance(At(4), sink);

        Assert.Equal(Enumerable.Range(0, 20), sink.Fired.Select(action => action.Argument));
    }

    [Fact]
    public void AfterRoundsUpToTheNextWholeTick()
    {
        var scheduler = new TickScheduler();
        var sink = new RecordingTimerSink();

        scheduler.After(At(0), 1, new ScheduledAction(TimerKind.LobbyHud, EntityId.None));       // < one tick
        scheduler.After(At(0), 100, new ScheduledAction(TimerKind.QueueUpdate, EntityId.None));  // exactly two

        Assert.Equal(1, scheduler.Advance(At(1), sink));
        Assert.Equal(1, scheduler.Advance(At(2), sink));
    }

    [Fact]
    public void CancelStopsOneAction()
    {
        var scheduler = new TickScheduler();
        var sink = new RecordingTimerSink();

        TickScheduler.Handle handle = scheduler.At(2, new ScheduledAction(TimerKind.StartMatch, EntityId.None));
        scheduler.At(2, new ScheduledAction(TimerKind.MatchEnd, EntityId.None));

        Assert.True(scheduler.Cancel(handle));
        Assert.False(scheduler.Cancel(handle));

        scheduler.Advance(At(2), sink);
        Assert.Equal(new[] { TimerKind.MatchEnd }, sink.Fired.Select(action => action.Kind).ToArray());
    }

    [Fact]
    public void CancelAllForDropsEveryActionNamingASubject()
    {
        var scheduler = new TickScheduler();
        var sink = new RecordingTimerSink();
        EntityId leaver = EntityId.Create(EntityKind.Character, 1, 1);
        EntityId stayer = EntityId.Create(EntityKind.Character, 1, 2);

        scheduler.At(1, new ScheduledAction(TimerKind.ParachuteSpawn, leaver));
        scheduler.At(2, new ScheduledAction(TimerKind.SpectateHandoff, leaver));
        scheduler.At(3, new ScheduledAction(TimerKind.ParachuteSpawn, stayer));

        Assert.Equal(2, scheduler.CancelAllFor(leaver));

        scheduler.Advance(At(10), sink);
        Assert.Single(sink.Fired);
        Assert.Equal(stayer, sink.Fired[0].Subject);
    }

    [Fact]
    public void SchedulingAndFiringAreFreeOfManagedAllocation()
    {
        var scheduler = new TickScheduler(capacity: 256);
        var sink = new RecordingTimerSink();

        // Warm the heap and the sink's lists.
        for (int i = 0; i < 64; i++)
        {
            scheduler.At(i, new ScheduledAction(TimerKind.LobbyHud, EntityId.None, i));
        }

        scheduler.Advance(At(1_000), sink);
        sink.Fired.Clear();
        sink.Ticks.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++)
        {
            scheduler.At(1_001 + i, new ScheduledAction(TimerKind.LobbyHud, EntityId.None, i));
        }

        scheduler.Advance(At(2_000), sink);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(64, sink.Fired.Count);
    }

    [Fact]
    public void TheLivenessGuardDropsAnActionForADisconnectedSubject()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        harness.Match.Scheduler.At(0, new ScheduledAction(TimerKind.ZoneIntoMatch, player.Id));

        MatchHarness.SinkOf(player).IsOpen = false;
        harness.Step();

        Assert.Equal(1, harness.Match.TimersDroppedLiveness);
        Assert.Equal(0, harness.Match.TimersFired);
    }

    [Fact]
    public void TheLivenessGuardDropsAnActionForAPlayerWhoLeft()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        harness.Match.Scheduler.At(1, new ScheduledAction(TimerKind.ZoneIntoMatch, player.Id));

        // Leaving also cancels the action outright; both halves of the guard are exercised.
        harness.Match.RemovePlayer(player);
        harness.Step(3);

        Assert.Equal(0, harness.Match.TimersFired);
    }

    [Fact]
    public void ThePhaseGuardDropsAnActionThatIsIllegalRightNow()
    {
        var harness = new MatchHarness();

        // MatchEnd is only legal in Ending; the match is still Forming.
        harness.Match.Scheduler.At(0, new ScheduledAction(TimerKind.MatchEnd, EntityId.None));
        harness.Step();

        Assert.Equal(1, harness.Match.TimersDroppedPhase);
        Assert.Equal(MatchPhase.Forming, harness.Match.Phase);
    }

    [Fact]
    public void TheLegalityTableCoversEveryTimerKind()
    {
        foreach (TimerKind kind in Enum.GetValues<TimerKind>())
        {
            bool legalSomewhere = Enum.GetValues<MatchPhase>().Any(phase => TimerPhaseTable.IsLegal(kind, phase));
            Assert.True(legalSomewhere, $"{kind} is legal in no phase at all.");
            Assert.False(TimerPhaseTable.IsLegal(kind, MatchPhase.Finished));
        }
    }
}
