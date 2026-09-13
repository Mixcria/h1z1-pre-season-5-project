namespace Cranberry.Launcher.Core;

public sealed record LeaderboardScore(int Points, uint Placement, int Kills);
public sealed record LeaderboardEntry(int Position, string AccountId, string Name, int TotalScore,
    string Tier, int Matches, uint Wins, uint Kills, int KdMatches, uint KdKills, uint KdDeaths,
    IReadOnlyList<LeaderboardScore> Best);
public sealed record LeaderboardView(string Mode, string Season, int TotalPlayers,
    IReadOnlyList<LeaderboardEntry> Players, LeaderboardEntry? Me);
