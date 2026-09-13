
namespace Cranberry.Zone.Match;

// Preseason 5, June 29–August 28 2017. See docs/scoring-20260906.md.
public enum RankedTier { Unranked, Bronze, Silver, Gold, Platinum, Diamond, Master, Royalty, Staff }

public readonly record struct RankBadge(RankedTier Tier, int Division = 5)
{
    public override string ToString() => Tier == RankedTier.Staff ? "Developer / Admin" : $"{Tier} {Division}";
    public static bool TryParse(string text, out RankedTier tier)
    {
        text = text.ToLowerInvariant() switch { "plat" => "Platinum", "emerald" or "emmerald" => "Master", "admin" or "developer" => "Staff", _ => text };
        return Enum.TryParse(text, true, out tier) && Enum.IsDefined(tier);
    }
}

public sealed class MatchScore
{
    private readonly HashSet<ulong> _victims = [];
    public int Kills => _victims.Count;
    public int PracticeKills { get; private set; }
    public bool PracticeSession { get; set; }
    public int KillPoints => checked(Kills * 1_000);
    public bool Settled { get; set; }
    public uint? FinalPlacement { get; set; }
    // Individual elimination order is distinct from the squad's eventual placement.
    public uint? PlayerPlacement { get; set; }
    public bool Credit(ulong victim, bool practice)
    {
        if (Settled || FinalPlacement is not null || victim == 0 || !_victims.Add(victim)) return false;
        if (practice) PracticeKills++;
        return true;
    }
    public void Reset() { _victims.Clear(); PracticeKills = 0; PracticeSession = false; Settled = false; FinalPlacement = null; PlayerPlacement = null; }
}

public sealed record RankedResult(string Id, uint Placement, int Kills, int Points, bool? Died = null);
public sealed record RankedProfile(int Matches, RankedResult[] Best)
{
    public string[] CompletedIds { get; init; } = [];
    public uint Wins { get; init; }
    public uint TopTens { get; init; }
    public uint TotalKills { get; init; }
    public uint TotalPlacements { get; init; }
    public uint TotalPoints { get; init; }
    public uint TopKills { get; init; }
    // Older records did not retain deaths (a dead teammate can still win). Keep their
    // scores intact and identify the matches with complete K/D data explicitly.
    public int KdMatches { get; init; }
    public uint KdKills { get; init; }
    public uint KdDeaths { get; init; }
    public static RankedProfile Empty { get; } = new(0, []);
    public int Points => Best.Sum(x => x.Points);
    public RankBadge Badge(MatchMode mode) => RankedScoring.Badge(Points, Matches, mode);
}

public static class RankedScoring
{
    public const uint Season = 5;
    // Global.Text.17867 -> locale key 1068839429 -> "Pre-Season 5" in the August archive.
    public const uint SeasonNameId = 17867;
    public static int[] Thresholds(MatchMode mode) => mode switch
    {
        MatchMode.Fives => [0, 1, 500_000, 615_000, 765_000, 975_000, 1_750_000, 1_800_000],
        _ => [0, 1, 375_000, 535_000, 765_000, 1_000_000, 1_750_000, 1_800_000],
    };

    public static RankBadge Badge(int points, int matches, MatchMode mode)
    {
        if (matches < 10) return new(RankedTier.Unranked);
        int[] thresholds = Thresholds(mode);
        for (int tier = 7; tier >= 1; tier--)
            if (points >= thresholds[tier])
            {
                // Equal fifths between published tier boundaries; Royalty steps are a local rule.
                int span = tier == 7 ? 50_000 : thresholds[tier + 1] - thresholds[tier];
                int division = 5 - Math.Clamp((int)((long)(points - thresholds[tier]) * 5 / span), 0, 4);
                return new((RankedTier)tier, division);
            }
        return new(RankedTier.Bronze);
    }

    public static (RankBadge Next, uint PointsRemaining, uint Percent) Progress(RankedProfile profile, MatchMode mode)
    {
        var badge = profile.Badge(mode);
        if (badge.Tier == RankedTier.Unranked) return (new(RankedTier.Bronze), 0, 0);
        if (badge is { Tier: RankedTier.Royalty, Division: 1 }) return (badge, 0, 100);
        int tier = (int)badge.Tier;
        int[] thresholds = Thresholds(mode);
        int span = tier == 7 ? 50_000 : thresholds[tier + 1] - thresholds[tier];
        int low = thresholds[tier] + (int)(((long)span * (5 - badge.Division) + 4) / 5);
        int high = thresholds[tier] + (int)(((long)span * (6 - badge.Division) + 4) / 5);
        var next = badge.Division == 1 ? new RankBadge((RankedTier)(tier + 1)) : badge with { Division = badge.Division - 1 };
        return (next, (uint)Math.Max(0, high - profile.Points),
            (uint)Math.Clamp((long)(profile.Points - low) * 100 / Math.Max(1, high - low), 0, 100));
    }

    public static int PlacementPoints(uint placement) => placement == 0 ? 0
        : placement <= PlacementScores.Length ? PlacementScores[placement - 1] : 0;

    // Filled from Daybreak's linked Preseason 5 placement sheet, not a fitted formula.
    internal static readonly int[] PlacementScores = [175000, 130000, 118310, 110010, 103570, 98310, 93860, 90010, 86610, 83570, 80820, 78310, 76000, 73870, 71880, 70010, 68270, 66620, 65060, 63580, 62170, 60830, 59540, 58320, 57140, 56010, 54920, 53870, 52860, 51880, 50930, 50020, 49130, 48270, 47430, 46620, 45830, 45060, 44310, 43580, 42870, 42170, 41490, 40830, 40180, 39550, 38930, 38320, 37730, 37140, 36570, 36010, 35460, 34920, 34390, 33870, 33360, 32860, 32370, 31880, 31410, 30940, 30470, 30020, 29570, 29130, 28700, 28270, 27850, 27440, 27030, 26620, 26220, 25830, 25440, 25060, 24690, 24310, 23950, 23580, 23220, 22870, 22520, 22180, 21830, 21500, 21160, 20830, 20510, 20190, 19870, 19550, 19240, 18930, 18630, 18320, 18020, 17730, 17440, 17150, 16860, 16570, 16290, 16010, 15740, 15460, 15190, 14930, 14660, 14400, 14130, 13880, 13620, 13370, 13110, 12860, 12620, 12370, 12130, 11890, 11650, 11410, 11170, 10940, 10710, 10480, 10250, 10020, 9800, 9580, 9360, 9140, 8920, 8700, 8490, 8270, 8060, 7850, 7650, 7440, 7230, 7030, 6830, 6630, 6430, 6230, 6030, 5840, 5640, 5450, 5260, 5070, 4880, 4690, 4500, 4320, 4130, 3950, 3770, 3590, 3410, 3230, 3050, 2870, 2700, 2520, 2350, 2180, 2010, 1840, 1670, 1500, 1330, 1170, 1000];
}

