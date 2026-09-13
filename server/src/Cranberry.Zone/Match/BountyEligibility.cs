namespace Cranberry.Zone.Match;

public enum MatchQueueKind { Unknown, Public, Hosted, Custom }
public enum MatchMode { Unknown, Solo, Duos, Fives, Training, Event }
public enum BountyPhase { Menu, Zoning, Lobby, Countdown, Dropping, Live, Ending, Finished }

/// <summary>
/// Immutable server-resolved admission. Client world ids are lookup keys into the configured
/// admission registry, not permission to back a match.
/// </summary>
public sealed record MatchAdmissionContext(ulong MatchId, MatchQueueKind QueueKind, MatchMode Mode)
{
    public static MatchAdmissionContext Unknown { get; } = new(0, MatchQueueKind.Unknown, MatchMode.Unknown);
}

public static class BountyEligibility
{
    public static bool IsEligible(MatchAdmissionContext? context) =>
        context is { MatchId: > 0, QueueKind: MatchQueueKind.Public, Mode: MatchMode.Solo };

    public static bool CanBack(
        MatchAdmissionContext? context, BountyPhase phase, bool enabled = true, bool acceptAnte = true) =>
        enabled && acceptAnte && IsEligible(context) && phase is BountyPhase.Lobby or BountyPhase.Countdown;
}
