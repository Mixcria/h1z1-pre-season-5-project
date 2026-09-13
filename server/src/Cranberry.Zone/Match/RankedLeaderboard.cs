using System.Collections.Frozen;

namespace Cranberry.Zone.Match;

public sealed record RankedIdentity(string AccountKey, ulong CharacterGuid, string Name);
public sealed record RankedLeaderboardEntry(RankedIdentity Identity, RankedProfile Profile, RankBadge Badge, uint Position);

/// <summary>Immutable, bounded-query projection. Built by the score writer, never by a menu request.</summary>
public sealed class RankedLeaderboard
{
    public const int PageSize = 50;
    public static RankedLeaderboard Empty { get; } = new([], []);
    private readonly FrozenDictionary<MatchMode, Board> _boards;

    public RankedLeaderboard(IEnumerable<RankedIdentity> identities,
        IEnumerable<KeyValuePair<(string AccountKey, MatchMode Mode), RankedProfile>> profiles)
    {
        var names = identities.ToDictionary(i => i.AccountKey, StringComparer.Ordinal);
        _boards = profiles.Where(p => p.Value.Matches > 0 && names.ContainsKey(p.Key.AccountKey))
            .GroupBy(p => p.Key.Mode).ToFrozenDictionary(g => g.Key, g => new Board(g
                .OrderByDescending(p => p.Value.Points)
                // Equal scores have a stable order, independent of login order or name changes.
                .ThenBy(p => p.Key.AccountKey, StringComparer.Ordinal)
                .Select(p => (Identity: names[p.Key.AccountKey], Profile: p.Value, Badge: p.Value.Badge(g.Key)))));
    }

    public uint Position(string accountKey, MatchMode mode) =>
        _boards.GetValueOrDefault(mode)?.ByAccount.GetValueOrDefault(accountKey)?.Position ?? 0;

    public RankedLeaderboardEntry? Player(ulong guid, MatchMode mode) =>
        _boards.GetValueOrDefault(mode)?.ByGuid.GetValueOrDefault(guid);

    public RankedLeaderboardEntry[] Select(string accountKey, MatchMode mode, uint tier, uint division, bool aroundMe)
    {
        if (!_boards.TryGetValue(mode, out var board)) return [];
        var me = board.ByAccount.GetValueOrDefault(accountKey);
        if (aroundMe)
        {
            if (me is null || me.Position == 0) return [];
            tier = (uint)me.Badge.Tier;
            division = (uint)me.Badge.Division;
        }
        if (!board.Buckets.TryGetValue((tier, division), out var rows)) return [];
        int start = 0;
        if (aroundMe && me is not null)
        {
            int index = board.BucketPositions[accountKey];
            start = Math.Clamp(index - PageSize / 2, 0, Math.Max(0, rows.Length - PageSize));
        }
        return rows.AsSpan(start, Math.Min(PageSize, rows.Length - start)).ToArray();
    }

    private sealed class Board
    {
        public readonly FrozenDictionary<string, RankedLeaderboardEntry> ByAccount;
        public readonly FrozenDictionary<ulong, RankedLeaderboardEntry> ByGuid;
        public readonly FrozenDictionary<(uint, uint), RankedLeaderboardEntry[]> Buckets;
        public readonly FrozenDictionary<string, int> BucketPositions;

        public Board(IEnumerable<(RankedIdentity Identity, RankedProfile Profile, RankBadge Badge)> source)
        {
            uint position = 0;
            var entries = source.Select(p => new RankedLeaderboardEntry(p.Identity, p.Profile, p.Badge,
                p.Badge.Tier == RankedTier.Unranked ? 0 : ++position)).ToArray();
            ByAccount = entries.ToFrozenDictionary(e => e.Identity.AccountKey, StringComparer.Ordinal);
            ByGuid = entries.ToFrozenDictionary(e => e.Identity.CharacterGuid);
            Buckets = entries.Where(e => e.Position > 0)
                .GroupBy(e => ((uint)e.Badge.Tier, (uint)e.Badge.Division))
                .ToFrozenDictionary(g => g.Key, g => g.ToArray());
            BucketPositions = Buckets.Values.SelectMany(rows => rows.Select((e, i) => (e.Identity.AccountKey, Index: i)))
                .ToFrozenDictionary(e => e.AccountKey, e => e.Index, StringComparer.Ordinal);
        }
    }
}
