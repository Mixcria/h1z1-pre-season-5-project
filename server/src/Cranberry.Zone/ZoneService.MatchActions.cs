using Cranberry.Transport;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private const string MatchActionWindow = "CRANBERRY_MATCH_ACTION_V1";
    private sealed record ReplayRequest(GatewaySessionState PreviousSession, PlayerWorldTransferRequest Transfer, long ExpiresAtMs)
    {
        public SoeConnection? WaitingConnection { get; set; }
    }
    private readonly Dictionary<(string Account, ulong Character), ReplayRequest> _replays = [];

    private bool CanReplaySolo(GatewaySessionState state) =>
        state.BountyAdmission is { QueueKind: MatchQueueKind.Public, Mode: MatchMode.Solo }
        && state.MatchTransferRequest is { Role: PlayerWorldTransferRequest.PlayerRole } request
        && _options.MatchAdmissions.TryGetDefinition(request.WorldId, out var world)
        && world is { QueueKind: MatchQueueKind.Public, Mode: MatchMode.Solo };

    private void HandleMatchAction(SoeConnection connection, GatewaySessionState state, string action)
    {
        if (!state.Authenticated || state.LogoutCompleted || state.LogoutPrepared) return;
        _log.Info($"{connection} match action: {action} ({state.Match}, {state.BountyAdmission.Mode})");
        if (action == "exit")
        {
            StartLogout(connection, state, clientManaged: true);
            return;
        }
        if (action is not ("play" or "menu")
            || !(state.Match == MatchStep.Ended || state.Match == MatchStep.InMatch && (state.DeathSent || state.VictorySent)
                || action == "menu" && state.Match == MatchStep.Menu)) return;

        if (action == "play" && !CanReplaySolo(state))
        {
            // Enforce the mode even for an old client or a forged/stale button action.
            SendTunnel(connection, new ConsolePrint("@cranberry/match-exit/1;ready").WriteTo);
            SendPartyNotice(connection, "Play Again is available after a public solo match. Choose Main Menu to leave.");
            return;
        }

        long now = Environment.TickCount64;
        foreach (var key in _replays.Where(p => p.Value.ExpiresAtMs <= now).Select(p => p.Key).ToArray())
            _replays.Remove(key);
        var identity = (state.AccountId, state.Guid);
        _replays.Remove(identity);
        if (action == "play")
        {
            var request = state.MatchTransferRequest!;
            var replay = new ReplayRequest(state, request, now + 120_000);
            _replays[identity] = replay;
            Later(null, 120_000, () =>
            {
                if (_replays.TryGetValue(identity, out var current) && ReferenceEquals(current, replay))
                    _replays.Remove(identity);
            });
        }
        CancelLogout(connection, state, "result selected");
        FinishLogoutCountdown(connection, state, clientManaged: true);
    }

    private void ReplayAfterMenuReady(SoeConnection connection, GatewaySessionState state)
    {
        var key = (state.AccountId, state.Guid);
        if (!_replays.TryGetValue(key, out var replay) || ReferenceEquals(replay.PreviousSession, state)) return;
        if (replay.ExpiresAtMs <= Environment.TickCount64 || state.Match != MatchStep.Menu
            || !ReferenceEquals(connection.Tag, state) || connection.State != ConnectionState.Open
            || !state.Authenticated || state.LogoutPrepared || state.LogoutCompleted
            || state.PendingLogout is not null || state.PendingClientAdmission is not null)
        {
            _replays.Remove(key);
            return;
        }
        if (!state.AppearanceReadySent) return;
        if (!_options.MatchAdmissions.TryGetDefinition(replay.Transfer.WorldId, out var world)
            || world is not { QueueKind: MatchQueueKind.Public, Mode: MatchMode.Solo })
        {
            _replays.Remove(key);
            SendPartyNotice(connection, "That solo game is no longer available. Choose a game from the menu.");
            return;
        }
        if (_parties.Find(state.Guid) is { Members.Count: > 1 })
        {
            // A party formed during the reconnect must not turn a solo replay into a
            // team queue or move another player out of their current game.
            _replays.Remove(key);
            SendPartyNotice(connection, "You are now in a party. Choose your next game from the menu.");
            return;
        }

        if (DoorSwingClientReady is { } ready && !ready(state.AccountId))
        {
            // LoginZone can finish before the launcher's client initialization.
            // Keep the solo intent until ready, with at most one poll per connection.
            if (ReferenceEquals(replay.WaitingConnection, connection)) return;
            replay.WaitingConnection = connection;
            if (!Later(connection, 250, () =>
            {
                if (!_replays.TryGetValue(key, out var current) || !ReferenceEquals(current, replay)
                    || !ReferenceEquals(replay.WaitingConnection, connection)) return;
                replay.WaitingConnection = null;
                ReplayAfterMenuReady(connection, state);
            })) _replays.Remove(key);
            return;
        }

        _replays.Remove(key);
        if (!PrepareMatchAdmission(state, replay.Transfer))
        {
            SendPartyNotice(connection, "That game is no longer available. Choose another game from the menu.");
            return;
        }
        state.AutoAcceptReplay = true;
        state.Match = MatchStep.Queued;
        if (UsesPublicQueue(state)) RunQueue(connection, state);
        else AcceptQueuedMatch(connection, state);
        _log.Info($"{connection} match: Play Again requested the next pregame lobby");
    }
}
