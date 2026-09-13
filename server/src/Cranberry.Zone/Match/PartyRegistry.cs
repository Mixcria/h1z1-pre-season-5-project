namespace Cranberry.Zone.Match;

public sealed record PartySnapshot(uint Id, ulong Leader, IReadOnlyList<ulong> Members);
public sealed record PartyInvitation(ulong Token, ulong Inviter, ulong Invitee, long ExpiresAtMs);

/// <summary>
/// Listener-thread party authority. Only an unexpired invitation issued to this character can
/// join a party; packet names, source guids and transfer strings never establish membership.
/// </summary>
public sealed class PartyRegistry
{
    public const int MaximumMembers = 5;
    public const long InvitationLifetimeMs = 60_000;
    private readonly Dictionary<uint, List<ulong>> _members = [];
    private readonly Dictionary<ulong, uint> _membership = [];
    private readonly Dictionary<ulong, PartyInvitation> _invitations = [];
    private uint _nextParty = 0x10000000;
    private ulong _nextInvitation;

    public PartySnapshot? Find(ulong character) => _membership.TryGetValue(character, out uint id)
        ? Snapshot(id) : null;

    public PartyInvitation? Invite(ulong inviter, ulong invitee, long nowMs)
    {
        Prune(nowMs);
        if (inviter == 0 || invitee == 0 || inviter == invitee || _membership.ContainsKey(invitee)) return null;
        PartySnapshot? party = Find(inviter);
        if (party is not null && (party.Leader != inviter || party.Members.Count >= MaximumMembers)) return null;
        // One current invitation per target prevents stale popups from accepting a different party.
        foreach (ulong token in _invitations.Where(pair => pair.Value.Invitee == invitee).Select(pair => pair.Key).ToArray())
            _invitations.Remove(token);
        var invitation = new PartyInvitation(++_nextInvitation, inviter, invitee, checked(nowMs + InvitationLifetimeMs));
        _invitations.Add(invitation.Token, invitation);
        return invitation;
    }

    public PartySnapshot? Respond(ulong token, ulong character, bool accept, long nowMs)
    {
        Prune(nowMs);
        if (!_invitations.TryGetValue(token, out PartyInvitation? invite) || invite.Invitee != character) return null;
        _invitations.Remove(token);
        if (!accept || _membership.ContainsKey(character)) return null;
        PartySnapshot? current = Find(invite.Inviter);
        if (current is not null && (current.Leader != invite.Inviter || current.Members.Count >= MaximumMembers)) return null;
        uint id;
        if (current is null)
        {
            id = ++_nextParty;
            _members.Add(id, [invite.Inviter]);
            _membership.Add(invite.Inviter, id);
        }
        else id = current.Id;
        _members[id].Add(character);
        _membership.Add(character, id);
        return Snapshot(id);
    }

    public PartyInvitation? Pending(ulong token, ulong target, long nowMs)
    {
        Prune(nowMs);
        return _invitations.TryGetValue(token, out PartyInvitation? invitation) && invitation.Invitee == target
            ? invitation : null;
    }

    public PartyInvitation? Incoming(ulong target, long nowMs)
    {
        Prune(nowMs);
        return _invitations.Values.FirstOrDefault(invitation => invitation.Invitee == target);
    }

    public PartySnapshot? Leave(ulong character)
    {
        foreach (ulong token in _invitations.Where(pair => pair.Value.Inviter == character || pair.Value.Invitee == character)
                     .Select(pair => pair.Key).ToArray()) _invitations.Remove(token);
        if (!_membership.Remove(character, out uint id)) return null;
        List<ulong> members = _members[id];
        members.Remove(character);
        if (members.Count < 2)
        {
            foreach (ulong remaining in members) _membership.Remove(remaining);
            _members.Remove(id);
            return null;
        }
        return Snapshot(id);
    }

    private PartySnapshot Snapshot(uint id)
    {
        List<ulong> members = _members[id];
        return new(id, members[0], members.ToArray());
    }

    private void Prune(long nowMs)
    {
        foreach (ulong token in _invitations.Where(pair => pair.Value.ExpiresAtMs <= nowMs).Select(pair => pair.Key).ToArray())
            _invitations.Remove(token);
    }
}
