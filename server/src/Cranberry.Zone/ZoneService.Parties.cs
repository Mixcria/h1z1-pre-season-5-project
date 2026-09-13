using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private readonly PartyRegistry _parties = new();

    private bool HandlePartyPacket(SoeConnection connection, GatewaySessionState state, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != ZoneOpcodes.GroupsBase
            || payload[1] is not (PartyPacket.InviteSub or PartyPacket.JoinSub or PartyPacket.LeaveSub)) return false;
        if (!PartyPacket.TryParse(payload, out PartyPacket? packet) || packet.Execute != 1 || packet.Error != 0)
        {
            _log.Warn($"{connection} rejected malformed group request");
            return true;
        }
        if (!state.Authenticated || state.Match != MatchStep.Menu)
        {
            SendPartyNotice(connection, "Return to the main menu before changing your party.");
            return true;
        }
        if (packet.SubOpcode == PartyPacket.LeaveSub)
        {
            PartyLinkClosed(state);
            SendTunnel(connection, new PartyPacket(PartyPacket.LeaveSub, 3, 0, 0).WriteTo);
            return true;
        }
        if (packet.SubOpcode == PartyPacket.InviteSub)
        {
            PartyInviteData requested = packet.Invite!;
            if (requested.Source.Guid != state.Guid)
            {
                SendPartyNotice(connection, "That party invitation could not be sent.");
                return true;
            }
            var candidates = _accountSessions.Where(pair => pair.Key.State == ConnectionState.Open
                && pair.Value.Authenticated && pair.Value.Match == MatchStep.Menu && pair.Value.Guid != state.Guid
                && (requested.Target.Guid != 0 && requested.Target.Guid != ulong.MaxValue
                    ? pair.Value.Guid == requested.Target.Guid
                    : string.Equals(pair.Value.CharacterName, requested.Target.Identity.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                .Take(2).ToArray();
            if (candidates.Length != 1)
            {
                SendPartyNotice(connection, "That player is not available in the main menu.");
                return true;
            }
            var target = candidates[0];
            PartyInvitation? invitation = _parties.Invite(state.Guid, target.Value.Guid, System.Environment.TickCount64);
            if (invitation is null)
            {
                SendPartyNotice(connection, "Only the party leader can invite an ungrouped player into a party with space.");
                return true;
            }
            PartyInviteData data = MakePartyInvite(invitation, state, target.Value);
            if (!target.Value.SocialLinked)
                SendTunnel(target.Key, new PartyPacket(PartyPacket.InviteSub, 1, 0, packet.Context, data).WriteTo);
            _log.Info($"group invitation created: source={state.Guid} target={target.Value.Guid} delivery={(target.Value.SocialLinked ? "social" : "native")}");
            SendPartyNotice(connection, $"Invitation sent to {target.Value.CharacterName}.");
            return true;
        }

        PartyInviteData response = packet.Invite!;
        long now = System.Environment.TickCount64;
        PartyInvitation? pending = _parties.Pending(response.Token, state.Guid, now);
        if (pending is null || response.Source.Guid != pending.Inviter || response.Target.Guid != state.Guid
            || packet.JoinState is not (1 or 2)
            || !TryFindPartyMember(pending.Inviter, out SoeConnection sourceConnection, out GatewaySessionState sourceState)
            || sourceState.Match != MatchStep.Menu)
        {
            SendPartyNotice(connection, "That party invitation has expired or is no longer available.");
            return true;
        }
        bool accept = packet.JoinState == 1;
        PartySnapshot? joined = _parties.Respond(response.Token, state.Guid, accept, now);
        PartyInviteData acknowledged = MakePartyInvite(pending, sourceState, state);
        if (accept && joined is null)
        {
            SendPartyNotice(connection, "That party is no longer available or has reached five players.");
            return true;
        }
        var reply = new PartyPacket(PartyPacket.JoinSub, 2, 0, packet.Context, acknowledged, packet.JoinState);
        if (!state.SocialLinked) SendTunnel(connection, reply.WriteTo);
        if (!sourceState.SocialLinked) SendTunnel(sourceConnection, reply.WriteTo);
        else SendPartyNotice(sourceConnection, accept ? $"{state.CharacterName} joined your group."
            : $"{state.CharacterName} declined your invitation.");
        if (joined is not null) PublishPartyRoster(joined);
        _log.Info($"group invitation response: source={pending.Inviter} target={state.Guid} accepted={accept}");
        return true;
    }

    private PartyInviteData MakePartyInvite(PartyInvitation invitation, GatewaySessionState source, GatewaySessionState target) =>
        new(invitation.Token, 0, new(source.Guid, new SelfIdentity { Name = source.CharacterName }),
            new(target.Guid, new SelfIdentity { Name = target.CharacterName }), _parties.Find(source.Guid)?.Id ?? 0);

    private void SendPartyNotice(SoeConnection connection, string text)
    {
        if (connection.Tag is GatewaySessionState { SocialLinked: true })
            SendTunnel(connection, new ConsolePrint(SocialPrefix + "N|" + SocialField(text)).WriteTo);
        else SendTunnel(connection, new SystemMessageCard(text).WriteTo);
    }

    private bool TryFindPartyMember(ulong guid, out SoeConnection connection, out GatewaySessionState state)
    {
        foreach (var pair in _accountSessions)
        {
            if (pair.Value.Guid != guid || !pair.Value.Authenticated || pair.Key.State != ConnectionState.Open) continue;
            connection = pair.Key;
            state = pair.Value;
            return true;
        }
        connection = null!;
        state = null!;
        return false;
    }

    private void PublishPartyRoster(PartySnapshot party)
    {
        var members = new List<GroupMember>(party.Members.Count);
        foreach (ulong guid in party.Members)
            if (TryFindPartyMember(guid, out _, out GatewaySessionState state))
                members.Add(new(guid, state.CharacterName, Vector3.Zero, (uint)members.Count));
        var packet = new GroupRoster(party.Id, party.Leader, members);
        foreach (ulong guid in party.Members)
            if (TryFindPartyMember(guid, out SoeConnection connection, out GatewaySessionState state)
                && state.Match == MatchStep.Menu)
                SendTunnel(connection, packet.WriteTo);
    }

    private void PartyLinkClosed(GatewaySessionState state)
    {
        PartySnapshot? before = _parties.Find(state.Guid);
        PartySnapshot? remaining = _parties.Leave(state.Guid);
        if (before is null) return;
        foreach (ulong guid in before.Members)
            if ((guid == state.Guid || remaining is null)
                && TryFindPartyMember(guid, out SoeConnection connection, out GatewaySessionState peer)
                && peer.Match == MatchStep.Menu)
                SendTunnel(connection, new RemoveGroup(before.Id).WriteTo);
        if (remaining is not null) PublishPartyRoster(remaining);
    }
}
