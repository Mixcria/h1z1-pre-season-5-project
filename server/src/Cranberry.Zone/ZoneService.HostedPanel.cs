using System.Globalization;
using Cranberry.Transport;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.HostedGames;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // Cranberry UI data carried by August's verified 0xfc string-setting delta. This is
    // an application format, not a new retail packet. Escaping keeps names and text inert.
    private ConsoleReply ConsoleHostedPanel(SoeConnection connection, GatewaySessionState state, CommandCall call)
    {
        var args = call.Line.RawTokens;
        if (args.Count > 2 || args.Count == 2 && !uint.TryParse(args[1], out _))
            return ConsoleReply.Usage("/hostgame panel [worldId]");
        if (!state.Authenticated || string.IsNullOrWhiteSpace(state.AccountId))
            return ConsoleReply.Refused("an authenticated account is required");
        SendHostedPanel(connection, state, args.Count == 2 ? uint.Parse(args[1], CultureInfo.InvariantCulture) : null);
        return new ConsoleReply([], Redraw: false) { SuppressOutput = true };
    }

    private ConsoleReply RunHostedPanelCommand(SoeConnection connection, GatewaySessionState state, CommandCall call)
    {
        var reply = ConsoleHostedGame(connection, state, call);
        var args = call.Line.RawTokens;
        if (args.Count > 0 && args[0].Equals("panel", StringComparison.OrdinalIgnoreCase) && reply.Ok) return reply;
        if (connection.State != ConnectionState.Open || !state.Authenticated) return reply;

        string secret = string.Empty;
        var feedback = new List<string>();
        foreach (var line in reply.Lines)
        {
            // Issuance is the only reply containing a raw key. Keep it out of the ordinary
            // status field, and let the user select/copy it in the dedicated invitation box.
            const string keyLabel = "  Key (save now): ";
            if (line.StartsWith(keyLabel + HostedGameStore.SecretPrefix, StringComparison.Ordinal)) secret = line[keyLabel.Length..].Trim();
            else feedback.Add(line.Trim());
        }
        SendTunnel(connection, new UpdateStringHashToValueManager("Cranberry.Hosted.Feedback", string.Join("\n", feedback)).WriteTo);
        SendTunnel(connection, new UpdateStringHashToValueManager("Cranberry.Hosted.Secret", secret).WriteTo);
        state.HostedPanelFeedbackSequence++;
        if (state.HostedPanelFeedbackSequence == 0) state.HostedPanelFeedbackSequence++;
        SendTunnel(connection, new UpdateStringHashToValueManager("Cranberry.Hosted.FeedbackSequence",
            state.HostedPanelFeedbackSequence.ToString(CultureInfo.InvariantCulture)).WriteTo);
        bool createUi = args.Count > 1 && args[0].Equals("createui", StringComparison.OrdinalIgnoreCase) && ValidHostedUiNonce(args[1]);
        if (createUi || args.Count > 0 && args[0].Equals("createform", StringComparison.OrdinalIgnoreCase))
        {
            SendTunnel(connection, new UpdateStringHashToValueManager("Cranberry.Hosted.CreateResult",
                createUi ? HostedPanelRow(args[1], reply.Ok, string.Join("\n", feedback))
                    : HostedPanelRow(reply.Ok, string.Join("\n", feedback))).WriteTo);
            state.HostedCreateSequence++;
            if (state.HostedCreateSequence == 0) state.HostedCreateSequence++;
            SendTunnel(connection, new UpdateStringHashToValueManager("Cranberry.Hosted.CreateSequence",
                state.HostedCreateSequence.ToString(CultureInfo.InvariantCulture)).WriteTo);
        }
        uint? selected = args.Count > 1 && uint.TryParse(args[1], out uint world) ? world : null;
        if (reply.Ok && args.Count > 0 && (args[0].Equals("create", StringComparison.OrdinalIgnoreCase)
            || args[0].Equals("createform", StringComparison.OrdinalIgnoreCase) || args[0].Equals("createui", StringComparison.OrdinalIgnoreCase)))
            selected = _hostedGames.ListGames(state.AccountId).Where(game => game.IsActive && game.OwnerAccount == state.AccountId)
                .OrderByDescending(game => game.CreatedAt).ThenByDescending(game => game.WorldId).FirstOrDefault()?.WorldId;
        SendHostedPanel(connection, state, selected);
        return reply;
    }

    private void SendHostedPanel(SoeConnection connection, GatewaySessionState state, uint? selectedWorld = null)
    {
        SendHostedAdminWorld(connection, state, force: true);
        bool admin = ResolveConsoleTier(connection, state) >= ConsoleTier.Owner;
        var games = _hostedGames.ListGames(state.AccountId, admin).Where(game => game.IsActive).ToArray();
        var selected = games.FirstOrDefault(game => game.WorldId == selectedWorld)
            ?? games.FirstOrDefault(game => state.HostedGameId == game.Id)
            ?? games.FirstOrDefault();
        string[] regions = _hostedGames.ListKeys(state.AccountId).Where(key => key.IsActive && key.Kind == HostedKeyKind.Host
            && key.Account == state.AccountId).Select(key => key.Region).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        bool manages = selected is not null && _hostedGames.CanManage(state.AccountId, admin, selected.WorldId);
        bool appoints = selected is not null && (admin || selected.OwnerAccount == state.AccountId);
        bool isMember = selected is not null && IsHostedMember(connection, state, selected);
        bool canFly = manages && isMember && state.DeathSent
            && state.Match is MatchStep.InMatch or MatchStep.Ended && !state.VictorySent && !state.LogoutPrepared;
        bool flying = isMember && state.HostedObserverActive && state.HostedFreeCamera;
        var rows = new List<string>
        {
            HostedPanelRow("H", state.AccountId, selected?.WorldId ?? 0, regions.Length > 0,
                string.Join(",", regions), canFly, flying, appoints, "0x" + state.Guid.ToString("X", CultureInfo.InvariantCulture),
                manages && isMember && CanHostedTeleport(state),
                isMember && state.HostedObserverActive && !state.HostedFreeCamera && state.HostedSpectateTarget != 0
                    ? "0x" + state.HostedSpectateTarget.ToString("X", CultureInfo.InvariantCulture) : string.Empty,
                state.HostedObserverSequence,
                state.HostedCameraPosition?.X ?? state.Movement.Player?.Position?.X ?? 0,
                state.HostedCameraPosition?.Z ?? state.Movement.Player?.Position?.Z ?? 0),
        };
        foreach (var game in games)
        {
            var members = HostedPanelMembers(game).ToArray();
            bool running = HostedWorldRunning(game.WorldId);
            string status = running ? "In progress" : members.Any(member => member.Match == MatchStep.Ended) ? "Round complete"
                : members.Any(member => member.Match != MatchStep.Lobby || !member.PregameClientReady || member.Watchdog.IsStalled) ? "Loading players"
                : members.Length > 0 ? game.QueueDurationMinutes > 0 ? "Queue countdown" : "Waiting for host" : "Open lobby";
            rows.Add(HostedPanelRow("G", game.WorldId, game.Name, HostedMode(game.GameModeId), game.Region,
                _hostedGames.CanManage(state.AccountId, admin, game.WorldId),
                state.Match == MatchStep.Menu && _hostedGames.CanEnter(state.AccountId, game.WorldId) && !running
                    && HostedHasRoom(game.WorldId, [state]) && !HostedQueueClosed(game),
                status, members.Length, game.QueueDurationMinutes, game.MaxPlayers));
        }
        if (manages && selected is not null)
            foreach (var member in HostedPanelMembers(selected).OrderBy(member => member.CharacterName, StringComparer.Ordinal))
                rows.Add(HostedPanelRow("P", "0x" + member.Guid.ToString("X", CultureInfo.InvariantCulture), member.CharacterName,
                    member.Hitpoints > 0 && !member.DeathSent && !member.VictorySent && member.Match != MatchStep.Ended,
                    HostedPlayerStatus(member), member.AccountId));
        if (manages && selected is not null)
            foreach (var key in _hostedGames.ListKeys(state.AccountId, admin)
                .Where(key => key.GameId == selected.Id && key.WorldId == selected.WorldId)
                .OrderByDescending(key => key.IsActive).ThenByDescending(key => key.CreatedAt).Take(100))
                rows.Add(HostedPanelRow("K", key.Id, key.Kind, key.Account ?? key.TargetAccount ?? "Unclaimed",
                    key.Revoked ? "Revoked" : !key.IsActive ? "Expired" : key.Account is null ? "Unclaimed" : "Active",
                    key.ExpiresAt?.ToString("u", CultureInfo.InvariantCulture) ?? "Never"));
        SendTunnel(connection, new UpdateStringHashToValueManager("Cranberry.Hosted.Panel", string.Join("\n", rows)).WriteTo);
    }

    private IEnumerable<GatewaySessionState> HostedPanelMembers(HostedGameInfo game) => HostedMembers(game).Select(pair => pair.Value);

    private static string HostedPlayerStatus(GatewaySessionState state) => state.HostedObserverActive
        ? state.HostedFreeCamera ? "Free flying" : "Spectating"
        : state.DeathSent ? "Eliminated" : state.VictorySent ? "Winner" : state.Match switch
        {
            MatchStep.Lobby => state.PregameClientReady ? "In lobby" : "Loading",
            MatchStep.Transferring or MatchStep.Zoning => "Loading",
            MatchStep.InMatch => "Alive",
            MatchStep.Ended => "Round complete",
            _ => state.Match.ToString(),
        };

    private static string HostedPanelRow(params object[] fields) => string.Join("|", fields.Select(field => Uri.EscapeDataString(
        field is bool value ? value ? "1" : "0" : Convert.ToString(field, CultureInfo.InvariantCulture) ?? string.Empty)));
}
