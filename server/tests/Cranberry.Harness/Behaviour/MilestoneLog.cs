using Cranberry.Harness.Runtime;

namespace Cranberry.Harness.Behaviour;

/// <summary>One occurrence of a client milestone.</summary>
public sealed record MilestoneRecord(HarnessMilestone Milestone, TimeSpan At, int Occurrence, string? Detail);

/// <summary>
/// The record of everything the modelled client actually did, and the thing scenarios wait on.
///
/// It is deliberately occurrence-aware. <c>ClientIsReady</c> happens once in the menu and once per
/// zoning; "the client sent ClientIsReady at some point" is exactly the kind of loose check that
/// let the four-hour Z2 hang through, because the menu one had already fired. Waiting is therefore
/// always for the <i>n</i>-th occurrence.
/// </summary>
public sealed class MilestoneLog(HarnessClock clock)
{
    private readonly object _gate = new();
    private readonly List<MilestoneRecord> _timeline = [];
    private readonly List<Waiter> _waiters = [];

    /// <summary>Every milestone in the order it happened.</summary>
    public IReadOnlyList<MilestoneRecord> Timeline
    {
        get
        {
            lock (_gate)
            {
                return [.. _timeline];
            }
        }
    }

    public MilestoneRecord Note(HarnessMilestone milestone, string? detail = null)
    {
        MilestoneRecord record;
        List<Waiter> release = [];
        lock (_gate)
        {
            int occurrence = _timeline.Count(r => r.Milestone == milestone) + 1;
            record = new MilestoneRecord(milestone, clock.Now, occurrence, detail);
            _timeline.Add(record);

            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                Waiter waiter = _waiters[i];
                if (waiter.Milestone == milestone && occurrence >= waiter.Occurrence)
                {
                    release.Add(waiter);
                    _waiters.RemoveAt(i);
                }
            }
        }

        foreach (Waiter waiter in release)
        {
            waiter.Completion.TrySetResult(record);
        }

        return record;
    }

    public int Count(HarnessMilestone milestone)
    {
        lock (_gate)
        {
            return _timeline.Count(r => r.Milestone == milestone);
        }
    }

    public bool Has(HarnessMilestone milestone, int occurrence = 1) => Count(milestone) >= occurrence;

    public MilestoneRecord? Get(HarnessMilestone milestone, int occurrence = 1)
    {
        lock (_gate)
        {
            return _timeline.Where(r => r.Milestone == milestone).Skip(occurrence - 1).FirstOrDefault();
        }
    }

    public TimeSpan? At(HarnessMilestone milestone, int occurrence = 1) => Get(milestone, occurrence)?.At;

    /// <summary>The most recent milestone of any kind, for a failure report.</summary>
    public MilestoneRecord? Last()
    {
        lock (_gate)
        {
            return _timeline.Count == 0 ? null : _timeline[^1];
        }
    }

    /// <summary>
    /// Waits for the <paramref name="occurrence"/>-th time <paramref name="milestone"/> happens.
    /// Returns null when the budget expires — the caller turns that into the failure report,
    /// because only the caller knows what it was waiting after.
    /// </summary>
    public async Task<MilestoneRecord?> WaitAsync(
        HarnessMilestone milestone,
        int occurrence,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        Task<MilestoneRecord> completion;
        lock (_gate)
        {
            MilestoneRecord? already = _timeline.Where(r => r.Milestone == milestone).Skip(occurrence - 1).FirstOrDefault();
            if (already is not null)
            {
                return already;
            }

            var waiter = new Waiter(milestone, occurrence);
            _waiters.Add(waiter);
            completion = waiter.Completion.Task;
        }

        TimeSpan scaled = clock.Scaled(budget);
        Task finished = await Task.WhenAny(completion, Task.Delay(scaled, cancellationToken)).ConfigureAwait(false);
        if (finished == completion)
        {
            return await completion.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    /// <summary>A compact "how far the client got" line for a failure report.</summary>
    public string Reached(int max = 12)
    {
        IReadOnlyList<MilestoneRecord> all = Timeline;
        IEnumerable<MilestoneRecord> tail = all.Skip(Math.Max(0, all.Count - max));
        return all.Count == 0
            ? "(the client reached no milestone at all)"
            : string.Join(", ", tail.Select(r => $"{r.Milestone}@{r.At.TotalSeconds:F3}s"));
    }

    private sealed record Waiter(HarnessMilestone Milestone, int Occurrence)
    {
        public TaskCompletionSource<MilestoneRecord> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
