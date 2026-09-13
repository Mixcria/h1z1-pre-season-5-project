using Cranberry.Transport;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    public const string OverlayWindow = "CRANBERRY_OVERLAY_V1", OverlayPrefix = "@cranberry/overlay/1;";
    // Bounded nonblocking mailbox. The launcher service handles persistence off the game listener.
    public Func<SocialOverlayRequest, bool>? SocialOverlaySubmit { get; set; }

    public string? ToggleSocialOverlay(string actor, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return "Invalid overlay request.";
        var peers = _accountSessions.Where(p => p.Value.AccountId == actor && p.Value.Authenticated
            && p.Value.AppearanceReadySent && p.Key.State == ConnectionState.Open).Take(2).ToArray();
        if (peers.Length != 1) return "Enter the game before opening friends with Shift+Tab.";
        var (connection, state) = peers[0];
        if (state.OverlayToggleId == id) return null;
        long now = Environment.TickCount64;
        if (now - state.OverlayLastToggleMs < 250) return null;
        state.OverlayLastToggleMs = now; state.OverlayToggleId = id;
        SendTunnel(connection, new ConsolePrint(OverlayPrefix + "T|" + id).WriteTo);
        return null;
    }

    private void HandleSocialOverlay(SoeConnection connection, GatewaySessionState state, string action)
    {
        if (!state.Authenticated || !state.AppearanceReadySent || action.Length > 13000) return;
        long now = Environment.TickCount64;
        if (now - state.OverlayLastActionMs < 100 || state.OverlayPending && now - state.OverlayLastActionMs < 12000) return;
        state.OverlayLastActionMs = now;
        string[] parts = action.Split('|');
        string op = parts[0], target = parts.Length > 1 ? parts[1] : "";
        if (target.Length > 64) return;
        if (op == "invite" && parts.Length == 2)
        {
            string? error = LauncherInviteToGame(state.AccountId, target);
            SendTunnel(connection, new ConsolePrint(OverlayPrefix + (error is null ? "P|" : "E|")
                + SocialField(error ?? "Invitation sent. Your friend can accept to join your lobby.")).WriteTo);
            return;
        }
        if (op == "avatar" && parts.Length == 2)
        {
            var directory = SocialDirectoryProvider?.Invoke() ?? InGameSocialDirectory.Empty;
            bool member = (_parties.Find(state.Guid)?.Members ?? []).Any(guid =>
                TryFindPartyMember(guid, out _, out var peer) && peer.AccountId == target);
            if (target != state.AccountId && !directory.AreFriends(state.AccountId, target) && !member)
            {
                SendTunnel(connection, new ConsolePrint(OverlayPrefix + "E|" + SocialField("That profile is no longer available.")).WriteTo);
                return;
            }
            var avatar = directory.Avatar(target);
            SendTunnel(connection, new ConsolePrint(OverlayPrefix + "A|" + target + "|" + (avatar?.Version ?? "") + "|" + (avatar?.Pixels ?? "")).WriteTo);
            return;
        }
        if (!(op == "state" && parts.Length == 1 || op == "history" && parts.Length == 2
            || op == "read" && parts.Length == 3 || op == "send" && parts.Length == 4)) return;
        string text;
        try { text = op == "send" ? Uri.UnescapeDataString(parts[3]) : op == "read" ? parts[2] : ""; }
        catch (UriFormatException) { return; }
        state.OverlayPending = true;
        var request = new SocialOverlayRequest(state.AccountId, op, target, text, op == "send" ? parts[2] : "", reply =>
        {
            state.OverlayPending = false;
            if (connection.State == ConnectionState.Open && state.Authenticated)
                SendTunnel(connection, new ConsolePrint(OverlayPrefix + reply).WriteTo);
        });
        if (SocialOverlaySubmit?.Invoke(request) != true)
        {
            state.OverlayPending = false;
            SendTunnel(connection, new ConsolePrint(OverlayPrefix + "E|" + SocialField("Friends service is busy. Try again.")).WriteTo);
        }
    }
}
