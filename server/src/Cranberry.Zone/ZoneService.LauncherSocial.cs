using Cranberry.Transport;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed record LauncherPartyMember(string AccountId, string Name, string Status);
public sealed record LauncherPartyInvitation(string Id, string FromId, string FromName);
public sealed record LauncherPartyState(string? Id, string? LeaderId, IReadOnlyList<LauncherPartyMember> Members,
    LauncherPartyInvitation? Invitation);

public sealed partial class ZoneService
{
    // These APIs run on the game listener. Launcher and overlay actions use the native party authority.
    public bool LauncherHasGame(string actor) => _accountSessions.Any(p => p.Key.State == ConnectionState.Open
        && p.Value.Authenticated && p.Value.AccountId == actor);

    private (SoeConnection Connection, GatewaySessionState State)? LauncherSocialPlayer(string actor)
    {
        var peers = _accountSessions.Where(p => p.Key.State == ConnectionState.Open && p.Value.Authenticated
            && p.Value.AccountId == actor).Take(2).ToArray();
        return peers.Length == 1 ? (peers[0].Key, peers[0].Value) : null;
    }

    public LauncherPartyState? LauncherSocial(string actor)
    {
        if (LauncherSocialPlayer(actor) is not { } player) return null;
        var directory = SocialDirectoryProvider?.Invoke() ?? InGameSocialDirectory.Empty;
        var party = _parties.Find(player.State.Guid);
        var members = new List<LauncherPartyMember>();
        string? leader = null;
        foreach (ulong guid in party?.Members ?? [])
            if (TryFindPartyMember(guid, out _, out var member))
            {
                if (guid == party!.Leader) leader = member.AccountId;
                members.Add(new(member.AccountId, directory.Name(member.AccountId) ?? member.CharacterName,
                    member.AppearanceReadySent ? member.Match.ToString() : "Loading"));
            }
        LauncherPartyInvitation? invitation = null;
        var pending = _parties.Incoming(player.State.Guid, Environment.TickCount64);
        if (player.State.Match == MatchStep.Menu && pending is not null
            && TryFindPartyMember(pending.Inviter, out _, out var inviter) && inviter.Match == MatchStep.Menu)
            invitation = new("game:" + pending.Token, inviter.AccountId, directory.Name(inviter.AccountId) ?? inviter.CharacterName);
        return new(party is null ? null : "game:" + party.Id, leader, members, invitation);
    }

    public string? LauncherInviteToGame(string actor, string target)
    {
        if (LauncherSocialPlayer(actor) is not { } source || LauncherSocialPlayer(target) is not { } recipient
            || !source.State.AppearanceReadySent || !recipient.State.AppearanceReadySent
            || source.State.Match != MatchStep.Menu || recipient.State.Match != MatchStep.Menu)
            return "Both players must reach the game main menu before joining a lobby together.";
        if (!(SocialDirectoryProvider?.Invoke() ?? InGameSocialDirectory.Empty).AreFriends(actor, target))
            return "Select an accepted friend to invite.";
        long now = Environment.TickCount64;
        if (now - source.State.SocialLastInviteMs < 2000) return "Wait a moment before sending another invitation.";
        var invitation = _parties.Invite(source.State.Guid, recipient.State.Guid, now);
        if (invitation is null) return "Only the leader can invite a player who is not already in a lobby. Lobbies hold up to five players.";
        source.State.SocialLastInviteMs = now;
        if (!recipient.State.SocialLinked)
            SendTunnel(recipient.Connection, new PartyPacket(PartyPacket.InviteSub, 1, 0, 0,
                MakePartyInvite(invitation, source.State, recipient.State)).WriteTo);
        PublishInGameSocial();
        return null;
    }

    public string? LauncherRespondToGame(string actor, string id, bool accept)
    {
        if (!id.StartsWith("game:", StringComparison.Ordinal) || !ulong.TryParse(id.AsSpan(5), out ulong token)
            || LauncherSocialPlayer(actor) is not { } recipient || !recipient.State.AppearanceReadySent)
            return "That lobby invitation is no longer available.";
        var pending = _parties.Pending(token, recipient.State.Guid, Environment.TickCount64);
        if (pending is null || !TryFindPartyMember(pending.Inviter, out _, out var source)
            || source.Match != MatchStep.Menu || recipient.State.Match != MatchStep.Menu)
            return "Return to the main menu and ask your friend for a new invitation.";
        using var writer = new Cranberry.Protocol.PacketWriter();
        new PartyPacket(PartyPacket.JoinSub, 1, 0, 0, MakePartyInvite(pending, source, recipient.State), accept ? 1u : 2u).WriteTo(writer);
        HandlePartyPacket(recipient.Connection, recipient.State, writer.Written);
        PublishInGameSocial();
        return accept && _parties.Find(recipient.State.Guid)?.Leader != source.Guid
            ? "That lobby is full or no longer available." : null;
    }
}
