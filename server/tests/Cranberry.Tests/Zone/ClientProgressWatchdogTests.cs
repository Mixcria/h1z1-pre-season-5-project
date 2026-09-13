using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

/// <summary>
/// docs/35 §7: the watchdog's own required tests. It had none, which is how the recovery bug below
/// shipped — the suite was green because nothing in it ever armed a milestone.
/// </summary>
public sealed class ClientProgressWatchdogTests
{
    private long _now;

    private ClientProgressWatchdog NewWatchdog() => new(() => _now);

    [Fact]
    public void AnArrivalBeforeTheDeadlineDisarmsTheSlotAndReportsItsLatency()
    {
        ClientProgressWatchdog watchdog = NewWatchdog();
        watchdog.Expect(ClientMilestone.ZoningClientIsReady, 12_000, "ClientBeginZoning");
        Assert.True(watchdog.IsArmed(ClientMilestone.ZoningClientIsReady));
        Assert.Equal(1, watchdog.ArmedCount);

        _now += 2_052; // the live latency of the 2026-08-29 session
        MilestoneArrival arrival = watchdog.Observe(ClientMilestone.ZoningClientIsReady);

        Assert.True(arrival.WasExpected);
        Assert.False(arrival.WasLate);
        Assert.Equal(2_052, arrival.LatencyMs);
        Assert.False(watchdog.IsArmed(ClientMilestone.ZoningClientIsReady));
        Assert.Equal(0, watchdog.ArmedCount);
        Assert.False(watchdog.IsStalled);
    }

    [Fact]
    public void AMissedSuppressingMilestoneIsReportedOnceAndStallsTheSession()
    {
        ClientProgressWatchdog watchdog = NewWatchdog();
        watchdog.Expect(ClientMilestone.ZoningClientIsReady, "ClientBeginZoning");

        _now += ClientProgressWatchdog.DefaultTimeoutMs(ClientMilestone.ZoningClientIsReady) - 1;
        Assert.False(watchdog.TryTakeExpired(out _));

        _now += 1;
        Assert.True(watchdog.TryTakeExpired(out ClientProgressStall stall));
        Assert.Equal(ClientMilestone.ZoningClientIsReady, stall.Milestone);
        Assert.True(stall.Suppresses);
        Assert.Equal("ClientBeginZoning", stall.LastSent);
        Assert.True(watchdog.IsStalled);

        // Reported exactly once, however often the pump polls.
        Assert.False(watchdog.TryTakeExpired(out _));
        Assert.Equal(0, watchdog.ArmedCount);
    }

    /// <summary>
    /// The regression this file exists for. <c>TryTakeExpired</c> disarms the slot BEFORE it sets
    /// the stall, so an answer that arrives after the poll has nothing armed to match on. When the
    /// unstall lived in the armed branch, recovery only worked inside the ≤1 s window between the
    /// deadline and the poll: a merely slow client — one that took more than 15 s from
    /// <c>ClientIsReady</c> to <c>ClientFinishedLoading</c> — stayed suppressed for the life of the
    /// session and silently lost its lobby HUD, StartMatch, parachute and gas.
    /// </summary>
    [Fact]
    public void ALateArrivalClearsTheStallAndReportsItselfAsLate()
    {
        ClientProgressWatchdog watchdog = NewWatchdog();
        watchdog.Expect(ClientMilestone.ClientFinishedLoading, "zone-done");

        _now += ClientProgressWatchdog.DefaultTimeoutMs(ClientMilestone.ClientFinishedLoading);
        Assert.True(watchdog.TryTakeExpired(out ClientProgressStall stall));
        Assert.True(watchdog.IsStalled);

        // Well after the poll that reported it — the case that used to be unrecoverable.
        _now += 30_000;
        MilestoneArrival arrival = watchdog.Observe(ClientMilestone.ClientFinishedLoading);

        Assert.True(arrival.WasExpected);
        Assert.True(arrival.WasLate);
        Assert.Equal(stall.WaitedMs, arrival.LatencyMs);
        Assert.False(watchdog.IsStalled);
        Assert.Equal(default, watchdog.Stall);
    }

    [Fact]
    public void ADifferentMilestoneArrivingDoesNotClearAnotherMilestonesStall()
    {
        ClientProgressWatchdog watchdog = NewWatchdog();
        watchdog.Expect(ClientMilestone.ZoningClientIsReady, "ClientBeginZoning");
        _now += ClientProgressWatchdog.DefaultTimeoutMs(ClientMilestone.ZoningClientIsReady);
        Assert.True(watchdog.TryTakeExpired(out _));
        Assert.True(watchdog.IsStalled);

        // Movement from a client that is still not where it claims to be proves nothing about the
        // milestone that stalled.
        Assert.Equal(default, watchdog.Observe(ClientMilestone.DescentMovement));
        Assert.True(watchdog.IsStalled);
    }

    [Fact]
    public void AnUnsolicitedArrivalIsNotExpectedAndDoesNotUnderflowTheArmedCount()
    {
        ClientProgressWatchdog watchdog = NewWatchdog();

        MilestoneArrival arrival = watchdog.Observe(ClientMilestone.FullCharacterDataRequest, key: 7);

        Assert.False(arrival.WasExpected);
        Assert.Equal(0, watchdog.ArmedCount);
        Assert.False(watchdog.IsStalled);
    }

    [Fact]
    public void ExpectFirstKeepsTheOldestDeadlineSoARepeatedSendStillTrips()
    {
        ClientProgressWatchdog watchdog = NewWatchdog();

        // A 64-item ground-loot burst arms the same milestone once per item.
        for (int item = 0; item < 64; item++)
        {
            watchdog.ExpectFirst(ClientMilestone.FullCharacterDataRequest, 3_000, "AddLightweightNpc", key: (ulong)item);
            _now += 100;
        }

        Assert.Equal(1, watchdog.ArmedCount);

        // 6,400 ms have passed, so the FIRST arm's 3,000 ms deadline is long gone. Expect() would
        // have pushed it forward on every item and never fired.
        Assert.True(watchdog.TryTakeExpired(out ClientProgressStall stall));
        Assert.Equal(ClientMilestone.FullCharacterDataRequest, stall.Milestone);
        Assert.Equal(0UL, stall.Key);
        Assert.False(stall.Suppresses);
        Assert.False(watchdog.IsStalled);
    }

    [Fact]
    public void AKeyMismatchIsReportedButStillClearsTheSlot()
    {
        ClientProgressWatchdog watchdog = NewWatchdog();
        watchdog.Expect(ClientMilestone.FullCharacterDataRequest, 3_000, "AddLightweightNpc", key: 42);

        MilestoneArrival arrival = watchdog.Observe(ClientMilestone.FullCharacterDataRequest, key: 43);

        Assert.True(arrival.WasExpected);
        Assert.False(arrival.KeyMatched);
        Assert.Equal(0, watchdog.ArmedCount);
    }

    [Fact]
    public void TheIdleWatchFiresOncePerSilenceEpisodeAndOnlyAfterTheClientHasSpoken()
    {
        ClientProgressWatchdog watchdog = NewWatchdog();
        watchdog.IdleTimeoutMs = 20_000;

        // Nothing has arrived yet, so there is no silence to measure.
        _now += 60_000;
        Assert.False(watchdog.TryTakeExpired(out _));
        Assert.False(watchdog.NeedsPolling);

        watchdog.NoteClientPacket();
        Assert.True(watchdog.NeedsPolling);
        _now += 20_000;
        Assert.True(watchdog.TryTakeExpired(out ClientProgressStall stall));
        Assert.Equal(ClientMilestone.ClientTraffic, stall.Milestone);
        Assert.False(stall.Suppresses);

        // Once per episode.
        _now += 20_000;
        Assert.False(watchdog.TryTakeExpired(out _));

        // The client speaks again: a new episode may be reported.
        watchdog.NoteClientPacket();
        _now += 20_000;
        Assert.True(watchdog.TryTakeExpired(out _));
    }

    [Fact]
    public void ResetDisarmsEverythingSoTheAbandonedMatchCannotSuppressTheNextOne()
    {
        ClientProgressWatchdog watchdog = NewWatchdog();
        watchdog.IdleTimeoutMs = 20_000;
        watchdog.NoteClientPacket();
        watchdog.Expect(ClientMilestone.TeleportClientReady, "StartMatch");
        _now += ClientProgressWatchdog.DefaultTimeoutMs(ClientMilestone.ZoningClientIsReady);
        watchdog.Expect(ClientMilestone.ZoningClientIsReady, "ClientBeginZoning");
        _now += ClientProgressWatchdog.DefaultTimeoutMs(ClientMilestone.ZoningClientIsReady);
        while (watchdog.TryTakeExpired(out _))
        {
        }

        Assert.True(watchdog.IsStalled);

        watchdog.Reset();

        Assert.False(watchdog.IsStalled);
        Assert.Equal(0, watchdog.ArmedCount);
        Assert.False(watchdog.NeedsPolling);
        Assert.Null(watchdog.NextDeadlineAtMs);
    }

    [Fact]
    public void OnlyTheTwoZoningMilestonesSuppressTheDownstreamBlindTimers()
    {
        Assert.True(ClientProgressWatchdog.DefaultSuppresses(ClientMilestone.ZoningClientIsReady));
        Assert.True(ClientProgressWatchdog.DefaultSuppresses(ClientMilestone.ClientFinishedLoading));

        foreach (ClientMilestone milestone in Enum.GetValues<ClientMilestone>())
        {
            if (milestone is ClientMilestone.None
                or ClientMilestone.ZoningClientIsReady
                or ClientMilestone.ClientFinishedLoading)
            {
                continue;
            }

            Assert.False(ClientProgressWatchdog.DefaultSuppresses(milestone));
        }
    }

    /// <summary>
    /// docs/35 §3: the zoning deadline has to expire before the 15,000 ms lobby-HUD timer arms its
    /// send, and ClientFinishedLoading before StartMatch, or the suppression lands too late to stop
    /// the monologue it exists to stop.
    /// </summary>
    [Fact]
    public void TheZoningDeadlinesStayUnderTheBlindTimersTheyProtect()
    {
        Assert.True(ClientProgressWatchdog.DefaultTimeoutMs(ClientMilestone.ZoningClientIsReady) < 15_000);
        Assert.True(
            ClientProgressWatchdog.DefaultTimeoutMs(ClientMilestone.ClientFinishedLoading)
            < 15_000 + 20_000);
    }

    [Fact]
    public void NoneIsNotAWaitableMilestone()
    {
        ClientProgressWatchdog watchdog = NewWatchdog();
        Assert.Throws<ArgumentOutOfRangeException>(() => watchdog.Expect(ClientMilestone.None, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => watchdog.Observe(ClientMilestone.None));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => watchdog.Expect(ClientMilestone.DescentMovement, 0, "x"));
    }
}
