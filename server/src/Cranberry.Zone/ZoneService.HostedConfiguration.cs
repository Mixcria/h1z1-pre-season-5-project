using System.Globalization;
using System.Runtime.CompilerServices;
using Cranberry.Transport;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.HostedGames;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private sealed class HostedLobbyClock
    {
        public bool Pumping;
        public readonly Dictionary<GatewaySessionState, (long? Deadline, int Generation)> Sent = [];
    }
    private readonly ConditionalWeakTable<SharedLootMatch, HostedLobbyClock> _hostedLobbyClocks = new();

    // Admission runs on the zone's serialized loop. Count prepared reservations too, so a
    // party cannot overbook while its members move from Menu to Queued in the same callback.
    private bool HostedHasRoom(uint worldId, IEnumerable<GatewaySessionState>? arrivals = null)
    {
        if (_hostedGames.GetGame(worldId) is not { IsActive: true } game) return false;
        var accounts = _accountSessions.Where(pair => pair.Key.State == ConnectionState.Open
            && pair.Value.HostedGameId == game.Id && pair.Value.BountyWorldId == worldId
            && pair.Value.BountyAdmission.MatchId != 0).Select(pair => pair.Value.AccountId)
            .ToHashSet(StringComparer.Ordinal);
        if (arrivals is null) return accounts.Count < game.MaxPlayers;
        foreach (var state in arrivals) accounts.Add(state.AccountId);
        return accounts.Count <= game.MaxPlayers;
    }

    private bool HostedQueueClosed(HostedGameInfo game) => _formingBountyMatches.TryGetValue(game.WorldId, out var forming)
        && _sharedLootMatches.TryGetValue(forming.MatchId, out var match)
        && match.LobbyDeadlineMs is long deadline && Environment.TickCount64 >= deadline;

    private void SendHostedAdminWorld(SoeConnection connection, GatewaySessionState state, bool force = false)
    {
        uint worldId = state.Authenticated && !state.LogoutPrepared && !state.LogoutCompleted
            && state.HostedGameId is not null && _hostedGames.GetGame(state.BountyWorldId) is { IsActive: true } game
            && IsHostedMember(connection, state, game)
            && _hostedGames.CanManage(state.AccountId, ResolveConsoleTier(connection, state) >= ConsoleTier.Owner, game.WorldId)
                ? game.WorldId : 0;
        if (!force && state.HostedAdminWorldSent == worldId) return;
        state.HostedAdminWorldSent = worldId;
        SendTunnel(connection, new UpdateStringHashToValueManager("Cranberry.Hosted.AdminWorld",
            worldId.ToString(CultureInfo.InvariantCulture)).WriteTo);
    }

    private void RestoreHostedAdminWorldAfterTable(SoeConnection connection, GatewaySessionState state)
    {
        // A full native table has already removed the key (UI default: zero). Restore a
        // current hosted authority even if its identical delta was cached before zoning.
        state.HostedAdminWorldSent = 0;
        SendHostedAdminWorld(connection, state);
    }

    private static bool ValidHostedUiNonce(string nonce) => nonce.Length is > 0 and <= 32 && nonce.All(Uri.IsHexDigit);

    private ConsoleReply RedeemHostedUi(SoeConnection connection, GatewaySessionState state, IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !ValidHostedUiNonce(args[1]))
            return ConsoleReply.Usage("/hostgame redeemui <nonce:hex> <key>");
        var result = args.Count == 3 && args[2].Length is > 0 and <= 256
            ? _hostedGames.Redeem(state.AccountId, args[2])
            : new HostedGameResult(false, "Enter a valid hosted-game key (up to 256 characters).");
        if (result.Success) RefreshHostedAccess();
        SendTunnel(connection, new UpdateStringHashToValueManager("Cranberry.Hosted.RedeemResult",
            HostedPanelRow(args[1], result.Success, result.Message)).WriteTo);
        return result.Success ? ConsoleReply.Did(result.Message) : ConsoleReply.Failed(result.Message);
    }

    // One shared clock belongs to the match, not to whichever connection first arrived.
    // The host must finish loading before it starts. At expiry all admitted clients must be
    // ready; an incomplete load holds the whole roster instead of silently splitting it.
    private void RefreshHostedLobby(ulong matchId, long nowMs)
    {
        if (!_sharedLootMatches.TryGetValue(matchId, out var match)) return;
        var members = _accountSessions.Where(pair => pair.Key.State == ConnectionState.Open
            && pair.Value.BountyAdmission.MatchId == matchId && pair.Value.HostedGameId is not null
            && pair.Value.Match != MatchStep.Menu).ToArray();
        if (members.Length == 0) return;
        var first = members[0].Value;
        if (_hostedGames.GetGame(first.BountyWorldId) is not { IsActive: true } game || game.Id != first.HostedGameId
            || members.Any(pair => pair.Value.HostedGameId != game.Id
                || pair.Value.Match is MatchStep.Dropping or MatchStep.InMatch or MatchStep.Ended)) return;
        if (members.Any(pair => !ValidateHostedAdmission(pair.Key, pair.Value))) return;
        var clock = _hostedLobbyClocks.GetValue(match, static _ => new HostedLobbyClock());
        bool Ready(GatewaySessionState state) => state.Match == MatchStep.Lobby && state.PregameClientReady
            && !state.Watchdog.IsStalled && state.PendingLogout is null && !state.LogoutPrepared;
        if (game.QueueDurationMinutes > 0 && match.LobbyDeadlineMs is null
            && members.Any(pair => pair.Value.AccountId == game.OwnerAccount && Ready(pair.Value)))
            match.LobbyDeadlineMs = nowMs + game.QueueDurationMinutes * 60_000L;
        bool elapsed = match.LobbyDeadlineMs is long deadline && nowMs >= deadline;
        if (elapsed && members.All(pair => Ready(pair.Value)))
        {
            foreach (var pair in members) BeginMatchDrop(pair.Key, pair.Value);
            RefreshHostedAccess();
            return;
        }
        long? displayedDeadline = elapsed ? null : match.LobbyDeadlineMs;
        foreach (var (connection, state) in members)
        {
            if (state.Match != MatchStep.Lobby) continue;
            if (clock.Sent.TryGetValue(state, out var sent) && sent.Deadline == displayedDeadline
                && sent.Generation == state.DevConsole.LobbyGeneration) continue;
            int generation = ++state.DevConsole.LobbyGeneration;
            uint remaining = displayedDeadline is long end ? (uint)Math.Clamp(end - nowMs, 0, uint.MaxValue) : 0;
            SendTunnel(connection, writer => GameModeHud.WriteCountdown(writer, remaining,
                displayedDeadline is null ? Generated.AugustStrings.HudLabels.WaitingForPlayers
                    : Generated.AugustStrings.HudLabels.StartingMatch));
            if (displayedDeadline is long bannerDeadline) ArmLobbyBanners(connection, state, bannerDeadline);
            clock.Sent[state] = (displayedDeadline, generation);
        }
        if (game.QueueDurationMinutes == 0 || clock.Pumping) return;
        clock.Pumping = Later(null, 1000, () =>
        {
            clock.Pumping = false;
            if (_sharedLootMatches.TryGetValue(matchId, out var current) && ReferenceEquals(current, match))
                RefreshHostedLobby(matchId, Environment.TickCount64);
        });
    }
}
