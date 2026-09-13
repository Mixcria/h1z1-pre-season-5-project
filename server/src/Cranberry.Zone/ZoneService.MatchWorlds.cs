using Cranberry.Transport;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // Public world aliases share one admission lane per mode. Hosted games retain
    // their explicit destination and independent lifecycle.
    private uint SelectMatchWorld(PlayerWorldTransferRequest request)
    {
        if (!_options.MatchAdmissions.TryGetDefinition(request.WorldId, out var requested)
            || requested.QueueKind != MatchQueueKind.Public)
            return request.WorldId;
        return _options.MatchAdmissions.Definitions
            .Where(world => world.QueueKind == MatchQueueKind.Public && world.Mode == requested.Mode
                && world.Region == requested.Region)
            .OrderBy(world => world.WorldNumber).ThenBy(world => world.WorldId).Select(world => world.WorldId)
            .FirstOrDefault(requested.WorldId);
    }

    // Active rounds never block a later round of the same mode. The production allocator
    // applies a separate node-wide capacity limit before freezing the next roster.
    private static bool PublicMatchQueueBlocked(GatewaySessionState state) => false;

    // A queued client can accept an old join prompt after the countdown closed. It
    // belongs to the next round; an already zoning participant keeps its reservation.
    private bool RefreshClosedQueueAdmission(SoeConnection connection, GatewaySessionState state)
    {
        if (state.BountyAdmission.QueueKind != MatchQueueKind.Public
            || !_closedBountyMatches.Contains(state.BountyAdmission.MatchId)) return true;
        var cohort = state.MatchPartyId == 0
            ? new[] { new KeyValuePair<SoeConnection, GatewaySessionState>(connection, state) }
            : _accountSessions.Where(pair => pair.Key.State == ConnectionState.Open
                && pair.Value.Match == MatchStep.Queued && pair.Value.MatchPartyId == state.MatchPartyId
                && pair.Value.BountyAdmission.MatchId == state.BountyAdmission.MatchId).ToArray();
        foreach (var pair in cohort)
            if (pair.Value.MatchTransferRequest is not { } request || !PrepareMatchAdmission(pair.Value, request))
                return false;
        foreach (var pair in cohort) RunQueue(pair.Key, pair.Value);
        return false;
    }
}
