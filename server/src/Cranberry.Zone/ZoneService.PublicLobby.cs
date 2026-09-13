using System.Runtime.CompilerServices;
using Cranberry.Transport;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private sealed class PublicLobbyClock
    {
        public bool Sent;
        public ulong MatchId;
        public int AdmissionGeneration;
        public int LobbyGeneration;
        public long? DeadlineMs;
    }

    // The cache suppresses redundant ce/0f packets without retaining disconnected sessions.
    private readonly ConditionalWeakTable<GatewaySessionState, PublicLobbyClock> _publicLobbyClocks = new();

    private IReadOnlyList<ulong> ReadyPublicLobbyPlayers(ulong matchId)
    {
        if (!_sharedLootMatches.TryGetValue(matchId, out var match)) return Array.Empty<ulong>();
        return match.Members.Where(pair => pair.Value.State == ConnectionState.Open
                && pair.Key.Match == MatchStep.Lobby && pair.Key.PregameClientReady
                && pair.Key.PendingLogout is null && !pair.Key.LogoutPrepared && UsesPublicQueue(pair.Key))
            .Select(pair => pair.Key.Guid).Distinct().ToArray();
    }

    /// <summary>
    /// Publishes the coordinator's absolute lobby clock. A null deadline is the native waiting
    /// label with an empty timer; the physical lobby stays open. Only the coordinator freezes
    /// the roster and begins the drop, so this method never schedules a per-player drop timer.
    /// </summary>
    private void PublishPublicLobbyCountdown(ulong matchId, long? deadlineMs)
    {
        if (!_sharedLootMatches.TryGetValue(matchId, out var match)) return;
        // The loading monitor uses this shared deadline to distinguish a legitimate waiting
        // lobby from a stalled transition after the countdown has actually expired.
        match.LobbyDeadlineMs = deadlineMs;
        long now = Environment.TickCount64;
        foreach (var (state, connection) in match.Members)
        {
            if (connection.State != ConnectionState.Open || state.Match != MatchStep.Lobby
                || !state.PregameClientReady || state.PendingLogout is not null || state.LogoutPrepared || !UsesPublicQueue(state)) continue;
            var clock = _publicLobbyClocks.GetValue(state, static _ => new PublicLobbyClock());
            if (clock.Sent && clock.MatchId == matchId && clock.AdmissionGeneration == state.MatchAdmissionGeneration
                && clock.LobbyGeneration == state.DevConsole.LobbyGeneration && clock.DeadlineMs == deadlineMs) continue;

            // Resetting the countdown must also cancel old 60/30/10-second banners. Their
            // existing callbacks already compare this generation before writing any bytes.
            int lobbyGeneration = ++state.DevConsole.LobbyGeneration;
            uint remainingMs = deadlineMs is long deadline
                ? (uint)Math.Clamp(deadline - now, 0, uint.MaxValue) : 0;
            uint labelId = deadlineMs is null
                ? Generated.AugustStrings.HudLabels.WaitingForPlayers
                : Generated.AugustStrings.HudLabels.StartingMatch;
            SendTunnel(connection, writer => GameModeHud.WriteCountdown(writer, remainingMs, labelId));
            if (deadlineMs is long bannerDeadline) ArmLobbyBanners(connection, state, bannerDeadline);

            clock.Sent = true;
            clock.MatchId = matchId;
            clock.AdmissionGeneration = state.MatchAdmissionGeneration;
            clock.LobbyGeneration = lobbyGeneration;
            clock.DeadlineMs = deadlineMs;
            _log.Info($"{connection} public lobby {matchId}: "
                + (deadlineMs is null ? "waiting for opponents" : $"countdown {remainingMs} ms"));
        }
    }
}
