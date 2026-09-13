using System.Text;
using Cranberry.Harness.Runtime;

namespace Cranberry.Harness.Behaviour;

/// <summary>A milestone deadline was missed, or the server contradicted the captures.</summary>
public sealed class HarnessAssertionException(string message) : Exception(message)
{
    /// <summary>The §13 row this failure came from, when it had one.</summary>
    public MilestoneDefinition? Definition { get; init; }

    public HarnessMilestone? Milestone { get; init; }

    public TimeSpan Waited { get; init; }
}

/// <summary>
/// Builds the failure report. This is the harness's actual deliverable: a failure that names what
/// was expected, what the client last managed to do, what the server last said, and how long it
/// waited — so the reader can tell a hung client from a slow one without replaying the session.
/// </summary>
public static class HarnessAssertion
{
    /// <summary>How many journal entries a failure report prints from each end.</summary>
    public const int DefaultJournalTail = 24;

    public static HarnessAssertionException Missed(
        HarnessMilestone milestone,
        string after,
        TimeSpan budget,
        TimeSpan waited,
        MilestoneLog milestones,
        PacketJournal journal,
        MilestoneDefinition? definition = null,
        string? extra = null,
        int journalTail = DefaultJournalTail)
    {
        definition ??= MilestoneCatalogue.For(milestone);
        var sb = new StringBuilder();
        string id = definition is null ? string.Empty : $"[{definition.Id}] ";
        sb.AppendLine($"{id}The client never reached {milestone}.");
        sb.AppendLine($"  expected : {definition?.Expect ?? milestone.ToString()}");
        sb.AppendLine($"  after    : {after}");
        sb.AppendLine($"  budget   : {HarnessClock.Format(budget).Trim()}   waited: {HarnessClock.Format(waited).Trim()}");
        if (definition is not null)
        {
            sb.AppendLine($"  evidence : {definition.Evidence}");
        }

        if (extra is not null)
        {
            sb.AppendLine($"  note     : {extra}");
        }

        MilestoneRecord? last = milestones.Last();
        sb.AppendLine($"  client   : last milestone {(last is null ? "(none)" : $"{last.Milestone} at {last.At.TotalSeconds:F3}s")}");
        sb.AppendLine($"  reached  : {milestones.Reached()}");

        JournalEntry? lastServer = journal.LastFromServer();
        sb.AppendLine($"  server   : last message {(lastServer is null ? "(none)" : $"{lastServer.Name} at {lastServer.At.TotalSeconds:F3}s ({lastServer.Bytes.Length} B)")}");
        sb.AppendLine();
        sb.AppendLine(journal.Tail(journalTail));

        return new HarnessAssertionException(sb.ToString())
        {
            Definition = definition,
            Milestone = milestone,
            Waited = waited,
        };
    }

    /// <summary>A server-side expectation the captures require but the server did not meet.</summary>
    public static HarnessAssertionException ServerFailed(
        string what,
        string evidence,
        MilestoneLog milestones,
        PacketJournal journal,
        int journalTail = DefaultJournalTail)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"The server did not do what the captures require: {what}");
        sb.AppendLine($"  evidence : {evidence}");
        sb.AppendLine($"  reached  : {milestones.Reached()}");
        sb.AppendLine();
        sb.AppendLine(journal.Tail(journalTail));
        return new HarnessAssertionException(sb.ToString());
    }
}
