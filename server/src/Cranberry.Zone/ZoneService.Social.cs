using System.Globalization;
using System.Text;
using Cranberry.Transport;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    public const string SocialWindow = "CRANBERRY_SOCIAL_V1";
    public const string SocialPrefix = "@cranberry/social/1;";
    // This provider only reads an immutable reference. It must never wait on the HTTP service gate.
    public Func<InGameSocialDirectory>? SocialDirectoryProvider { get; set; }

    private void HandleInGameSocial(SoeConnection connection, GatewaySessionState state, string action)
    {
        if (!state.Authenticated || !state.AppearanceReadySent || action.Length > 96) return;
        long now = Environment.TickCount64;
        if (action is "hello" or "state")
        {
            if (state.SocialLinked && now - state.SocialLastPollMs < 750) return;
            state.SocialLinked = true;
            state.SocialLastPollMs = now;
            PublishInGameSocial(connection, state, force: action == "hello");
            return;
        }
        // A sent snapshot is not proof that the lobby UI consumed its invitation.
        // Acknowledge only the current invitation for this authenticated recipient.
        // Keep this separate from click throttling so it cannot swallow Accept.
        if (state.SocialLinked && action.StartsWith("seen ", StringComparison.Ordinal))
        {
            if (ulong.TryParse(action.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out ulong token)
                && _parties.Pending(token, state.Guid, now) is not null)
                state.SocialInviteAcknowledgedToken = token;
            return;
        }
        if (!state.SocialLinked || now - state.SocialLastActionMs < 200) return;
        state.SocialLastActionMs = now;
        if (state.Match != MatchStep.Menu)
        {
            SendPartyNotice(connection, "Return to the main menu before changing your party.");
            return;
        }
        string[] parts = action.Split(' ');
        PartyPacket? packet = null;
        if (action == "leave") packet = new(PartyPacket.LeaveSub, 1, 0, 0);
        else if (parts.Length == 2 && ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong value))
        {
            if (parts[0] == "invite")
            {
                if (!TryFindPartyMember(value, out _, out var target) || !target.AppearanceReadySent
                    || !(SocialDirectoryProvider?.Invoke() ?? InGameSocialDirectory.Empty).AreFriends(state.AccountId, target.AccountId))
                {
                    SendPartyNotice(connection, "Select a friend who is available in the main menu.");
                    return;
                }
                // Rate-limit invitations separately from polling and accept/decline.
                if (now - state.SocialLastInviteMs < 2000) return;
                state.SocialLastInviteMs = now;
                packet = new(PartyPacket.InviteSub, 1, 0, 0,
                    new(0, 0, new(state.Guid, new()), new(value, new()), 0));
            }
            else if (parts[0] is "accept" or "decline")
            {
                var pending = _parties.Pending(value, state.Guid, now);
                if (pending is null || !TryFindPartyMember(pending.Inviter, out _, out var inviter))
                {
                    SendPartyNotice(connection, "That invitation has expired or is no longer available.");
                    return;
                }
                packet = new(PartyPacket.JoinSub, 1, 0, 0, MakePartyInvite(pending, inviter, state),
                    parts[0] == "accept" ? 1u : 2u);
            }
        }
        if (packet is null) return;
        using var writer = new Cranberry.Protocol.PacketWriter();
        packet.WriteTo(writer);
        HandlePartyPacket(connection, state, writer.Written);
        PublishInGameSocial();
    }

    private static string SocialField(string value) => Uri.EscapeDataString(value);

    private string InGameSocialView(GatewaySessionState state)
    {
        var directory = SocialDirectoryProvider?.Invoke() ?? InGameSocialDirectory.Empty;
        var party = _parties.Find(state.Guid);
        var text = new StringBuilder(SocialPrefix);
        int teamSize = state.BountyAdmission.Mode switch { MatchMode.Duos => 2, MatchMode.Fives => 5, _ => 0 };
        text.Append("S|").Append(state.Guid).Append('|').Append(party?.Leader ?? state.Guid).Append('|').Append(state.Match).Append('|').Append(teamSize)
            .Append('|').Append(state.MatchTransferRequest?.WorldId ?? 0).Append('|').Append(state.MatchAdmissionGeneration);
        foreach (ulong guid in party?.Members ?? new[] { state.Guid })
        {
            if (!TryFindPartyMember(guid, out _, out var member)) continue;
            text.Append(";M|").Append(guid).Append('|').Append(SocialField(directory.Name(member.AccountId) ?? member.CharacterName))
                .Append('|').Append(SocialField(member.CharacterName)).Append('|').Append(member.AppearanceReadySent ? member.Match : "Loading");
            text.Append(";P|").Append(guid).Append('|').Append(SocialField(member.AccountId)).Append('|')
                .Append(directory.Avatar(member.AccountId)?.Version ?? "").Append('|').Append(SocialField(directory.Name(member.AccountId) ?? member.CharacterName));
        }
        foreach (string account in directory.Friends(state.AccountId))
        {
            var sessions = _accountSessions.Where(p => p.Key.State == ConnectionState.Open && p.Value.Authenticated && p.Value.AccountId == account)
                .Select(p => p.Value).Take(2).ToArray();
            var friend = sessions.Length == 1 ? sessions[0] : null;
            string status = friend is null ? "Offline" : !friend.AppearanceReadySent ? "Loading" : friend.Match.ToString();
            bool available = friend is { AppearanceReadySent: true, Match: MatchStep.Menu } && _parties.Find(friend.Guid) is null;
            text.Append(";F|").Append(friend?.Guid ?? 0).Append('|').Append(SocialField(directory.Name(account) ?? ""))
                .Append('|').Append(SocialField(friend?.CharacterName ?? "")).Append('|').Append(status).Append('|').Append(available ? '1' : '0');
            text.Append(";P|").Append(friend?.Guid ?? 0).Append('|').Append(SocialField(account)).Append('|')
                .Append(directory.Avatar(account)?.Version ?? "").Append('|').Append(SocialField(directory.Name(account) ?? ""));
        }
        var invitation = _parties.Incoming(state.Guid, Environment.TickCount64);
        if (state.Match == MatchStep.Menu && invitation is not null
            && TryFindPartyMember(invitation.Inviter, out _, out var source) && source.Match == MatchStep.Menu)
            text.Append(";I|").Append(invitation.Token).Append('|').Append(SocialField(source.CharacterName));
        return text.ToString();
    }

    private void PublishInGameSocial(SoeConnection connection, GatewaySessionState state, bool force = false)
    {
        if (!state.SocialLinked || !state.Authenticated || connection.State != ConnectionState.Open) return;
        string snapshot = InGameSocialView(state);
        var incoming = state.Match == MatchStep.Menu ? _parties.Incoming(state.Guid, Environment.TickCount64) : null;
        bool unacknowledgedInvite = incoming is not null && incoming.Token != state.SocialInviteAcknowledgedToken;
        if (!force && snapshot == state.SocialLastView && !unacknowledgedInvite) return;
        state.SocialLastView = snapshot;
        SendTunnel(connection, new ConsolePrint(snapshot).WriteTo);
    }

    private void PublishInGameSocial()
    {
        foreach (var peer in _accountSessions) PublishInGameSocial(peer.Key, peer.Value);
    }
}
