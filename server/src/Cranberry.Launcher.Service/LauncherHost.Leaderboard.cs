using Cranberry.Launcher.Core;
using Cranberry.Zone.Match;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Cranberry.Launcher.Service;

public sealed partial class LauncherHost
{
    private void MapLeaderboard()
    {
        _app.MapGet("/api/leaderboard/{mode}", async (HttpContext context, string mode) =>
        {
            string actor = await Actor(context);
            MatchMode category = mode.ToLowerInvariant() switch
            {
                "solo" => MatchMode.Solo, "duos" => MatchMode.Duos, "fives" => MatchMode.Fives,
                _ => throw new ArgumentException("Choose Solo, Duos or Fives.")
            };
            var directory = _social.InGameDirectory;
            var names = directory.Accounts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var profiles = _zone.LauncherRankedProfiles(names.Keys, category);
            var players = profiles.Where(pair => pair.Value.Matches > 0)
                .OrderByDescending(pair => pair.Value.Points)
                .ThenBy(pair => names[pair.Key], StringComparer.OrdinalIgnoreCase)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select((pair, index) =>
                {
                    var profile = pair.Value;
                    return new LeaderboardEntry(index + 1, pair.Key, names[pair.Key], profile.Points,
                        profile.Badge(category).Tier == RankedTier.Unranked ? "Unranked" : profile.Badge(category).ToString(),
                        profile.Matches, profile.Wins, profile.TotalKills, profile.KdMatches, profile.KdKills, profile.KdDeaths,
                        profile.Best.Select(result => new LeaderboardScore(result.Points, result.Placement, result.Kills)).ToArray());
                }).ToArray();
            return new LeaderboardView(category.ToString(), "Preseason 5", players.Length,
                players.Take(100).ToArray(), players.FirstOrDefault(player => player.AccountId == actor));
        });
    }
}
