using Cranberry.Transport;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    public void SeedRankedIdentities(IEnumerable<(string Account, ulong Guid, string Name)> characters)
    {
        RankedScores.SeedIdentities(characters);
        _log.Info($"ranked leaderboard ready: {RankedScores.DatabasePath ?? "memory"}; imported {RankedScores.ImportedProfiles} legacy profiles");
    }

    public void FlushRankedScores() => _rankedScores?.FlushPending();

    private void HandleRankingRequest(SoeConnection connection, GatewaySessionState state, byte[] payload)
    {
        if (!state.Authenticated || !state.RankingBudget.TryTake(Environment.TickCount64)) return;
        string account = ScoreAccount(state);
        string accountKey = RankedScoreStore.AccountKey(account);
        var board = RankedScores.Leaderboard;
        if (payload[1] == 0x06)
        {
            if (payload.Length != 26) return;
            uint category = BitConverter.ToUInt32(payload, 18);
            var mode = LeaderboardPackets.Mode(category);
            if (mode == MatchMode.Unknown) return;
            var profile = RankedScores.Read(account, mode);
            SendTunnel(connection, new MatchRankingReply(category, profile, state.Guid, board.Position(accountKey, mode),
                RankedScoring.Season, RankedScoring.SeasonNameId).WriteTo);
            return;
        }
        if (SelectLeaderboardRequest.TryParse(payload, out var selection))
        {
            var rows = board.Select(accountKey, selection.Mode, selection.Tier, selection.Division, selection.AroundMe);
            SendTunnel(connection, w => LeaderboardPackets.WriteLeaderboard(w, selection.RequestId, state.Guid, accountKey, rows));
            if (_log.IsEnabled(TransportLogLevel.Trace))
                _log.Log(TransportLogLevel.Trace, $"{connection} leaderboard: {selection.Mode}, tier={selection.Tier}/{selection.Division}, aroundMe={selection.AroundMe}, {rows.Length} rows");
            return;
        }
        if (PlayerTopTenRequest.TryParse(payload, out var player))
        {
            // Resolve only public character identity, never a client-supplied account or path.
            SendTunnel(connection, w => LeaderboardPackets.WritePlayerTopTen(w, player, board.Player(player.CharacterGuid, player.Mode)));
        }
    }
}
