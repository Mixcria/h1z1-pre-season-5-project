using Cranberry.Transport;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private bool _cancellingPartyQueue;

    private bool TryQueuePartyMatch(SoeConnection connection, GatewaySessionState state, PlayerWorldTransferRequest request)
    {
        PartySnapshot? party = _parties.Find(state.Guid);
        if (party is null) return false;
        var admission = ResolveMatchAdmission(state, request, 1);
        if (admission == MatchAdmissionContext.Unknown)
        {
            RefuseHostedAdmission(connection, request.WorldId);
            return true;
        }
        int capacity = admission.Mode switch { MatchMode.Duos => 2, MatchMode.Fives => 5, _ => 1 };
        if (party.Leader != state.Guid || party.Members.Count > capacity)
        {
            SendPartyNotice(connection, party.Leader != state.Guid
                ? "The party leader chooses the match." : "Choose a game mode with room for your whole party.");
            SendTunnel(connection, new PlayerWorldTransferReply(request.WorldId, Result: 1).WriteTo);
            return true;
        }
        var members = new List<(SoeConnection Connection, GatewaySessionState State)>();
        foreach (ulong guid in party.Members)
        {
            if (!TryFindPartyMember(guid, out var memberConnection, out var member)
                || member.Match != MatchStep.Menu || !member.AppearanceReadySent)
            {
                SendPartyNotice(connection, "Every party member must be in the main menu before joining.");
                SendTunnel(connection, new PlayerWorldTransferReply(request.WorldId, Result: 1).WriteTo);
                return true;
            }
            members.Add((memberConnection, member));
        }
        if (DoorSwingClientReady is { } ready && members.Any(m => !ready(m.State.AccountId)))
        {
            SendPartyNotice(connection, "A party member's game update is still starting. Try Play again in a moment.");
            SendTunnel(connection, new PlayerWorldTransferReply(request.WorldId, Result: 1).WriteTo);
            return true;
        }
        if (members.Any(member => ResolveMatchAdmission(member.State, request, 1) == MatchAdmissionContext.Unknown))
        {
            SendPartyNotice(connection, "Every party member needs a valid invitation for this hosted game.");
            SendTunnel(connection, new PlayerWorldTransferReply(request.WorldId, Result: 1).WriteTo);
            return true;
        }
        if (admission.QueueKind == MatchQueueKind.Hosted && !HostedHasRoom(request.WorldId, members.Select(member => member.State)))
        {
            SendPartyNotice(connection, "This hosted game does not have room for your whole party. Nobody was queued.");
            SendTunnel(connection, new PlayerWorldTransferReply(request.WorldId, Result: 1).WriteTo);
            return true;
        }
        if (!PrepareMatchAdmission(state, request))
        {
            RefuseHostedAdmission(connection, request.WorldId);
            return true;
        }
        foreach (var (memberConnection, member) in members)
        {
            if (!ReferenceEquals(member, state))
            {
                if (!PrepareMatchAdmission(member, request with { WorldId = state.BountyWorldId }))
                {
                    foreach (var rollback in members)
                        AbandonMatch(rollback.Connection, rollback.State, "party admission changed");
                    RefuseHostedAdmission(connection, request.WorldId);
                    return true;
                }
                member.MatchTransferRequest = request;
            }
            if (member.CrateOpening is not null) EndCrateOpening(memberConnection, member);
            member.Match = MatchStep.Queued;
            RunQueue(memberConnection, member);
        }
        PublishInGameSocial();
        return true;
    }

    private void AcceptQueuedMatch(SoeConnection connection, GatewaySessionState state)
    {
        if (!ValidateHostedAdmission(connection, state)) return;
        if (UsesPublicQueue(state)) { AcceptPublicQueue(connection, state); return; }
        if (!RefreshClosedQueueAdmission(connection, state)) return;
        PartySnapshot? party = _parties.Find(state.Guid);
        if (state.MatchPartyId != 0 && party is not null && party.Id == state.MatchPartyId)
        {
            if (party.Leader != state.Guid) return;
            var queued = _accountSessions.Where(pair => pair.Value.MatchPartyId == state.MatchPartyId
                && pair.Value.BountyAdmission.MatchId == state.BountyAdmission.MatchId
                && pair.Value.Match == MatchStep.Queued && pair.Key.State == ConnectionState.Open).ToArray();
            if (queued.Length != party.Members.Count)
            {
                AbandonMatch(connection, state, "party queue changed");
                return;
            }
            if (queued.Any(pair => !ValidateHostedAdmission(pair.Key, pair.Value))) return;
            if (PublicMatchQueueBlocked(state))
            {
                foreach (var pair in queued) RunQueue(pair.Key, pair.Value);
                return;
            }
            foreach (var pair in queued)
            {
                pair.Value.Match = MatchStep.Transferring;
                EnterMatch(pair.Key, pair.Value);
            }
            return;
        }
        if (PublicMatchQueueBlocked(state)) { RunQueue(connection, state); return; }
        state.Match = MatchStep.Transferring;
        EnterMatch(connection, state);
    }

    private void CancelPartyQueuePeers(GatewaySessionState state, string reason)
    {
        // Public reservations own whole-party cleanup across queue, zoning and lobby.
        // Running both cleanup paths can recursively log out the initiating member twice.
        if (UsesPublicQueue(state)) return;
        if (_cancellingPartyQueue || state.MatchPartyId == 0
            || state.Match is not (MatchStep.Queued or MatchStep.Transferring)) return;
        _cancellingPartyQueue = true;
        try
        {
            foreach (var peer in _accountSessions.Where(pair => !ReferenceEquals(pair.Value, state)
                && pair.Value.MatchPartyId == state.MatchPartyId
                && pair.Value.BountyAdmission.MatchId == state.BountyAdmission.MatchId
                && pair.Value.Match is MatchStep.Queued or MatchStep.Transferring).ToArray())
            {
                if (peer.Key.State == ConnectionState.Open)
                    SendTunnel(peer.Key, new QueueExit(0, peer.Value.Guid, Cancel: true).WriteTo);
                AbandonMatch(peer.Key, peer.Value, reason);
            }
        }
        finally { _cancellingPartyQueue = false; }
    }
}
