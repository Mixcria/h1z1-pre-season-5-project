namespace Cranberry.Zone.World;

/// <summary>
/// The phase table: who may move where, and what timer carries each transition.
/// <para>
/// <b>Scaffold stub (migration step 4).</b> The state machine and its timers are real — that is what
/// the wave-1 tests pin — but no packet is sent from here yet. The <c>ce 16</c> StartMatch burst,
/// the countdown widget and the queue HUD still live in <c>ZoneService</c> and are redirected here
/// in step 4, one integration note at a time (docs/11, docs/22 §8).
/// </para>
/// </summary>
public sealed class MatchFlowSystem : ISystem
{
    private readonly List<(int Slot, CommandKind Kind)> _staged = [];
    private TickScheduler.Handle _startMatch;

    public long Transitions { get; private set; }

    public long Deaths { get; private set; }

    /// <summary>Timer actions this system was handed but has no handler for yet.</summary>
    public long UnhandledTimers { get; private set; }

    public int StagedCommands => _staged.Count;

    public void Stage(MatchPlayer player, in Command command)
    {
        ArgumentNullException.ThrowIfNull(player);
        _staged.Add((player.Slot, command.Kind));
    }

    public void Tick(in TickContext context)
    {
        Match match = context.Match;
        World world = context.World;

        switch (match.Phase)
        {
            case MatchPhase.Forming:
                if (world.PlayerCount > 0)
                {
                    Transition(match, MatchPhase.Lobby);
                }

                break;

            case MatchPhase.Lobby:
                if (world.PlayerCount >= context.Settings.MinPlayersToStart
                    && Transition(match, MatchPhase.Countdown))
                {
                    _startMatch = match.Scheduler.After(
                        context.Time,
                        context.Settings.LobbyCountdownMs,
                        new ScheduledAction(TimerKind.StartMatch, EntityId.None));
                }

                break;

            case MatchPhase.Countdown:
                if (world.PlayerCount == 0 && Transition(match, MatchPhase.Lobby))
                {
                    // An aborted countdown has to cancel its own StartMatch. The action's subject is
                    // EntityId.None, so neither guard in Match.Fire stops it (CancelAllFor cannot
                    // help either — it would take every match-wide timer with it) and it is still
                    // legal in Countdown, so the next countdown would inherit it and drop the match
                    // early.
                    CancelStartMatch(match);
                }

                break;

            case MatchPhase.Live:
                if (world.PlayerCount > 0 && world.AliveCount <= 1 && Transition(match, MatchPhase.Ending))
                {
                    match.Scheduler.After(
                        context.Time,
                        context.Settings.EndingDurationMs,
                        new ScheduledAction(TimerKind.MatchEnd, EntityId.None));
                }

                break;

            case MatchPhase.Dropping:
            case MatchPhase.Ending:
            case MatchPhase.Finished:
            default:
                break;
        }

        // Cleared at the END of the tick, like LootSystem and CombatSystem: Match.Drain runs before
        // the systems, so clearing first would throw away every Ready/Join/Leave staged this tick.
        _staged.Clear();
    }

    /// <summary>
    /// Dispatched by <c>Match.Fire</c> after both guards have passed: the subject is still connected
    /// and the action is legal in the current phase.
    /// </summary>
    public void OnTimer(Match match, in ScheduledAction action, TickTime now)
    {
        ArgumentNullException.ThrowIfNull(match);

        var context = new TickContext(match, now);
        switch (action.Kind)
        {
            case TimerKind.StartMatch:
                _startMatch = default;
                if (Transition(match, MatchPhase.Dropping))
                {
                    if (match.Settings.GasEnabled)
                    {
                        // ce 16 StartMatch is where the gas clock starts (docs/22 §6.2).
                        match.Gas.Start(in context);
                    }

                    match.Scheduler.After(
                        now,
                        match.Settings.DropDurationMs,
                        new ScheduledAction(TimerKind.ReleaseTeleport, EntityId.None));
                }

                break;

            case TimerKind.ReleaseTeleport:
                Transition(match, MatchPhase.Live);
                break;

            case TimerKind.MatchEnd:
                Transition(match, MatchPhase.Finished);
                break;

            default:
                UnhandledTimers++;
                break;
        }
    }

    /// <summary>
    /// Called by the single damage resolver, never by a producer. Step 8 turns this into the
    /// spectate hand-off; today it schedules the action so the ordering is exercised.
    /// </summary>
    public void OnPlayerDied(in TickContext context, MatchPlayer player, DamageCause cause)
    {
        ArgumentNullException.ThrowIfNull(player);
        _ = cause;

        Deaths++;
        context.Match.Scheduler.After(
            context.Time,
            MatchClock.FixedDeltaMs,
            new ScheduledAction(TimerKind.SpectateHandoff, player.Id));
    }

    /// <summary>Cancels a pending StartMatch, if one is armed. Idempotent.</summary>
    private void CancelStartMatch(Match match)
    {
        if (_startMatch.IsNone)
        {
            return;
        }

        match.Scheduler.Cancel(_startMatch);
        _startMatch = default;
    }

    private bool Transition(Match match, MatchPhase phase)
    {
        if (!match.TryTransition(phase))
        {
            return false;
        }

        Transitions++;
        return true;
    }
}
