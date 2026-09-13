using System.Numerics;

namespace Cranberry.Zone.Match;

/// <summary>Clear, terrain-supported marks in the August Box of Destiny courtyard.</summary>
public static class LobbySpawnPlanner
{
    public static ReadOnlySpan<Vector4> Points => LobbySpawnPoints.All;

    public static Vector4 Choose(ulong seed, IEnumerable<Vector4> occupied, Vector4 previous = default)
    {
        var taken = occupied.ToArray();
        int start = (int)(MatchSeeds.For(seed, MatchSeeds.DropSalt) % (ulong)Points.Length);
        Vector4 best = Points[start];
        float bestDistance = -1;
        for (int n = 0; n < Points.Length; n++)
        {
            Vector4 candidate = Points[(start + n) % Points.Length];
            if (candidate == previous) continue;
            float distance = taken.Length == 0 ? float.PositiveInfinity
                : taken.Min(p => (p.X - candidate.X) * (p.X - candidate.X) + (p.Z - candidate.Z) * (p.Z - candidate.Z));
            if (distance >= 2.5f * 2.5f) return candidate;
            if (distance > bestDistance) { best = candidate; bestDistance = distance; }
        }
        return best;
    }
}
