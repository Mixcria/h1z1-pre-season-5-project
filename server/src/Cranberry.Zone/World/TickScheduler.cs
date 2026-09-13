namespace Cranberry.Zone.World;

/// <summary>
/// Everything <c>ZoneService</c> currently schedules with <c>Task.Delay</c> (its <c>Later(...)</c>
/// helper, <c>ZoneService.cs:929-941</c>), as an enum instead of a captured closure.
/// </summary>
public enum TimerKind : byte
{
    QueueUpdate,
    QueueExit,
    ZoneIntoMatch,
    LobbyHud,
    StartMatch,
    ReleaseTeleport,
    ParachuteSpawn,
    DevGroundLoot,
    GasReveal,
    GasClose,
    LootRespawn,
    SpectateHandoff,
    MatchEnd,
}

/// <summary>
/// A scheduled action with a named payload instead of a captured closure: nothing is allocated, a
/// pending action is inspectable in a dump, and two runs are comparable in a determinism test
/// (docs/22 §4.3).
/// </summary>
/// <param name="Kind">Which handler runs.</param>
/// <param name="Subject">The player or entity the action is about; <see cref="EntityId.None"/> for
/// match-wide work. A player leaving cancels every pending action naming them.</param>
/// <param name="Argument">Small handler-specific value (a phase index, a slot).</param>
public readonly record struct ScheduledAction(TimerKind Kind, EntityId Subject, int Argument = 0);

/// <summary>What a due action is dispatched to. <c>Match</c> is the production implementation.</summary>
public interface ITimerSink
{
    void Fire(in ScheduledAction action, TickTime now);
}

/// <summary>
/// Which phases each <see cref="TimerKind"/> may run in. This is the second of the two guards
/// today's <c>Later</c> chains carry: every match-flow closure re-checks <c>state.Match</c> before
/// doing anything (<c>ZoneService.cs:947-960</c>). Dispatching without it sends packets to a
/// re-queued session that it does not receive today (docs/22 §4.3, §12 row 7).
/// </summary>
public static class TimerPhaseTable
{
    public static bool IsLegal(TimerKind kind, MatchPhase phase) => kind switch
    {
        TimerKind.QueueUpdate or TimerKind.QueueExit or TimerKind.ZoneIntoMatch or TimerKind.LobbyHud =>
            phase is MatchPhase.Forming or MatchPhase.Lobby or MatchPhase.Countdown,
        TimerKind.StartMatch => phase is MatchPhase.Countdown,
        TimerKind.ReleaseTeleport => phase is MatchPhase.Dropping,
        TimerKind.ParachuteSpawn => phase is MatchPhase.Dropping or MatchPhase.Live,
        TimerKind.DevGroundLoot or TimerKind.LootRespawn => phase is MatchPhase.Dropping or MatchPhase.Live,
        TimerKind.GasReveal or TimerKind.GasClose => phase is MatchPhase.Live,
        TimerKind.SpectateHandoff => phase is MatchPhase.Live or MatchPhase.Ending,
        TimerKind.MatchEnd => phase is MatchPhase.Ending,
        _ => false,
    };
}

/// <summary>
/// Deterministic timer wheel on tick granularity: a binary heap keyed by
/// <c>(dueTick, insertion sequence)</c>, so two runs of the same trace fire the same actions in the
/// same order. Replaces every <c>Task.Delay</c> continuation in the zone, including the
/// <c>run =&gt; run()</c> fallback that makes today's orchestration tests a latent race
/// (docs/22 §12 row 7).
/// </summary>
public sealed class TickScheduler
{
    /// <summary>Cancellation token for one pending action.</summary>
    public readonly record struct Handle(long Id)
    {
        public bool IsNone => Id == 0;
    }

    private struct Entry
    {
        public long DueTick;
        public long Sequence;
        public ScheduledAction Action;
        public bool Cancelled;
    }

    private Entry[] _heap;
    private int _count;
    private long _sequence;

    public TickScheduler(int capacity = 64) => _heap = new Entry[Math.Max(4, capacity)];

    /// <summary>Pending actions, including ones already cancelled but not yet swept.</summary>
    public int PendingCount => _count;

    /// <summary>Actions dispatched to a sink since construction.</summary>
    public long FiredCount { get; private set; }

    public Handle At(long dueTick, in ScheduledAction action)
    {
        if (_count == _heap.Length)
        {
            Array.Resize(ref _heap, _heap.Length * 2);
        }

        long sequence = ++_sequence;
        _heap[_count] = new Entry
        {
            DueTick = dueTick,
            Sequence = sequence,
            Action = action,
            Cancelled = false,
        };

        SiftUp(_count++);
        return new Handle(sequence);
    }

    /// <summary>
    /// Schedules relative to now, rounding up so a 1 ms delay still lands on the next tick rather
    /// than the current one.
    /// </summary>
    public Handle After(TickTime now, int milliseconds, in ScheduledAction action)
    {
        long ticks = milliseconds <= 0
            ? 1
            : (milliseconds + MatchClock.FixedDeltaMs - 1) / MatchClock.FixedDeltaMs;
        return At(now.Tick + ticks, action);
    }

    public bool Cancel(Handle handle)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_heap[i].Sequence == handle.Id && !_heap[i].Cancelled)
            {
                _heap[i].Cancelled = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>A player leaving cancels every pending action naming them.</summary>
    public int CancelAllFor(EntityId subject)
    {
        int cancelled = 0;
        for (int i = 0; i < _count; i++)
        {
            if (!_heap[i].Cancelled && _heap[i].Action.Subject == subject)
            {
                _heap[i].Cancelled = true;
                cancelled++;
            }
        }

        return cancelled;
    }

    /// <summary>Fires everything due at or before <paramref name="now"/>, in (dueTick, insertion) order.</summary>
    public int Advance(TickTime now, ITimerSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        int fired = 0;
        while (_count > 0 && _heap[0].DueTick <= now.Tick)
        {
            Entry entry = Pop();
            if (entry.Cancelled)
            {
                continue;
            }

            FiredCount++;
            fired++;
            sink.Fire(entry.Action, now);
        }

        return fired;
    }

    public void Clear() => _count = 0;

    private Entry Pop()
    {
        Entry root = _heap[0];
        _heap[0] = _heap[--_count];
        _heap[_count] = default;
        SiftDown(0);
        return root;
    }

    private void SiftUp(int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (!IsBefore(index, parent))
            {
                return;
            }

            Swap(index, parent);
            index = parent;
        }
    }

    private void SiftDown(int index)
    {
        while (true)
        {
            int left = (index * 2) + 1;
            if (left >= _count)
            {
                return;
            }

            int smallest = left;
            int right = left + 1;
            if (right < _count && IsBefore(right, left))
            {
                smallest = right;
            }

            if (!IsBefore(smallest, index))
            {
                return;
            }

            Swap(index, smallest);
            index = smallest;
        }
    }

    private bool IsBefore(int a, int b) =>
        _heap[a].DueTick != _heap[b].DueTick
            ? _heap[a].DueTick < _heap[b].DueTick
            : _heap[a].Sequence < _heap[b].Sequence;

    private void Swap(int a, int b) => (_heap[a], _heap[b]) = (_heap[b], _heap[a]);
}
