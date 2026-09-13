using System.Numerics;
using Cranberry.Zone.Gas;

namespace Cranberry.Zone.Match;

public readonly record struct DropParticipant(ulong Player, ulong Team);

/// <summary>
/// Match-owned placement, stable after assignment even if players disconnect or finish loading
/// late. Distances are gameplay tuning, not recovered retail constants. See docs/spawns-20260906.md.
/// </summary>
public sealed class PopulationDropPlanner
{
    // Population overflow may use settlements beyond the normal drop ring before squeezing
    // opponents together. This is project tuning, not a recovered retail spawn distance.
    private const float PopulationOverflowOutsideSafeZoneMetres = 3000f;
    private readonly Z2DropPois _places;
    private readonly DropOptions _options;
    private readonly GasCircle? _circle;
    private readonly ulong _seed;
    private readonly DropPlan _origin;
    private readonly GasCircle? _spawnBoundary;
    private readonly float? _spawnHalfExtent;
    private readonly List<Vector3> _candidates;
    private readonly List<Vector3> _expandedCandidates;
    private readonly Dictionary<ulong, Vector3> _teams = [];
    private readonly Dictionary<ulong, DropPlan> _players = [];
    private readonly Dictionary<ulong, int> _playerSlots = [];

    public PopulationDropPlanner(Z2DropPois places, DropOptions options, GasCircle? circle, ulong seed,
        GasCircle? spawnBoundary = null, float? spawnHalfExtent = null, DropPlan? origin = null)
    {
        _places = places;
        _options = options;
        _circle = circle;
        _seed = seed;
        _spawnBoundary = spawnBoundary;
        _spawnHalfExtent = spawnHalfExtent;
        _origin = origin ?? DropPlanner.Plan(places, circle, options, seed)
            ?? throw new ArgumentException("A population drop requires enabled, nonempty map data.");
        // One representative per 40 m cell avoids giving a stack of indoor loot markers
        // hundreds of votes. Keep real placement heights and named and unnamed settlements.
        var cells = new HashSet<(int X, int Z)>();
        var expandedCells = new HashSet<(int X, int Z)>();
        _candidates = [];
        _expandedCandidates = [];
        foreach (ref readonly var marker in places.Spawns.Points)
        {
            Vector3 p = marker.Position;
            if (!InBounds(p)) continue;
            bool ordinary = InRing(p);
            if (!ordinary && !InExpandedRing(p)) continue;
            var cell = ((int)MathF.Floor(p.X / 40f), (int)MathF.Floor(p.Z / 40f));
            bool ordinarySeat = ordinary && !cells.Contains(cell);
            bool expandedSeat = !expandedCells.Contains(cell);
            if ((!ordinarySeat && !expandedSeat)
                || places.CountWithin(p, options.LootRadius) < options.MinimumMarkers) continue;
            // Independent cell sets preserve the original ring's representatives at its edge.
            if (ordinarySeat)
            {
                cells.Add(cell);
                _candidates.Add(p);
            }
            if (expandedSeat)
            {
                expandedCells.Add(cell);
                _expandedCandidates.Add(p);
            }
        }
        if (_candidates.Count == 0)
        {
            if ((_spawnBoundary.HasValue || _spawnHalfExtent.HasValue) && !InBounds(_origin.Position))
                throw new InvalidOperationException("No drop markers fit the match spawn boundary.");
            _candidates.Add(_origin.Position);
        }
        if (_expandedCandidates.Count == 0) _expandedCandidates.Add(_origin.Position);
    }

    public IReadOnlyDictionary<ulong, DropPlan> Players => _players;
    public float RadiusMetres { get; private set; }
    public int ExpandedPlacements { get; private set; }
    public int ReducedSpacingPlacements { get; private set; }

    public void Assign(IEnumerable<DropParticipant> participants, int teamSize)
    {
        ArgumentNullException.ThrowIfNull(participants);
        if (teamSize is not (1 or 2 or 5)) throw new ArgumentOutOfRangeException(nameof(teamSize));
        var roster = participants.Distinct().OrderBy(p => MatchSeeds.For(_seed, p.Team))
            .ThenBy(p => p.Player).ToArray();
        if (roster.GroupBy(p => p.Player).Any(g => g.Count() != 1))
            throw new ArgumentException("A player cannot belong to two teams.", nameof(participants));
        if (roster.GroupBy(p => p.Team).Any(g => g.Count() > teamSize))
            throw new ArgumentException("Team exceeds mode capacity.", nameof(participants));
        int population = Math.Max(roster.Length, _players.Count);
        // A local neighbourhood at low population, growing to a broad map region at 150.
        RadiusMetres = Math.Max(RadiusMetres, _spawnHalfExtent ?? Math.Min(_spawnBoundary?.Radius ?? float.MaxValue,
            550f + 3650f * MathF.Pow(Math.Clamp((population - 2) / 148f, 0f, 1f), 0.8f)));
        // Full solo matches need more seats per settlement. Ease the hard floor gradually,
        // while retaining a larger overall footprint and a useful looting gap at 150 players.
        float fullness = Math.Clamp((population - 20) / 130f, 0f, 1f);
        float separation = teamSize switch { 2 => 340f - 40f * fullness, 5 => 420f, _ => 260f - 60f * fullness };
        if (_spawnBoundary.HasValue)
        {
            float compactFullness = Math.Clamp((population - 20) / 80f, 0f, 1f);
            separation = teamSize switch { 1 => 260f - 140f * compactFullness, 2 => 340f - 110f * compactFullness, _ => 420f };
        }
        if (_spawnHalfExtent.HasValue)
            separation = teamSize switch { 1 => 120f, 2 => 230f, _ => 420f };
        foreach (var group in roster.GroupBy(p => p.Team))
        {
            var occupiedSlots = group.Where(p => _playerSlots.ContainsKey(p.Player))
                .Select(p => _playerSlots[p.Player]).ToHashSet();
            if (!_teams.TryGetValue(group.Key, out Vector3 centre))
            {
                centre = ChooseCentre(group.Key, separation);
                _teams.Add(group.Key, centre);
            }
            foreach (var member in group)
            {
                if (_players.ContainsKey(member.Player)) continue;
                int slot = Enumerable.Range(0, teamSize).First(i => !occupiedSlots.Contains(i));
                occupiedSlots.Add(slot);
                _playerSlots.Add(member.Player, slot);
                // Separate canopies, with a shared destination. Ground is an estimate for
                // streaming until the client's actual landing pose arrives, never a teleport.
                double angle = MatchSeeds.For(_seed, group.Key) / (double)ulong.MaxValue * Math.Tau
                    + slot * Math.Tau / teamSize;
                float offset = teamSize == 1 ? 0f : 24f;
                Vector3 ground = centre + new Vector3((float)Math.Cos(angle) * offset, 0, (float)Math.Sin(angle) * offset);
                var poi = _places.Places.MinBy(p => DistanceSquared(p.Anchor, ground))!;
                _players.Add(member.Player, _origin with
                {
                    Poi = poi, Position = ground,
                    AirPosition = ground with { Y = MathF.Max(_options.SkySpawnAltitude, ground.Y + _options.MinimumClearanceMetres) },
                    DistanceToCircleCentre = _circle?.HorizontalDistanceTo(ground) ?? float.NaN,
                    MarkersWithinLootRadius = _places.CountWithin(ground, _options.LootRadius),
                    UsedAnchor = false, JitterAttemptsUsed = 0, EligibleRank = 0,
                    MatchSeed = _seed, DropSeed = MatchSeeds.For(_seed, member.Player),
                    PopulationPlacement = true,
                });
            }
        }
    }

    private Vector3 ChooseCentre(ulong team, float separation)
    {
        var random = new GasRandom(MatchSeeds.For(_seed, team));
        double angle = random.NextDouble() * Math.Tau;
        float reach = (float)Math.Sqrt(random.NextDouble()) * RadiusMetres;
        Vector3 target = _origin.Position + new Vector3((float)Math.Cos(angle) * reach, 0, (float)Math.Sin(angle) * reach);
        if (_spawnHalfExtent is float extent)
            target = new Vector3((float)(random.NextDouble() * 2 - 1) * extent, 0,
                (float)(random.NextDouble() * 2 - 1) * extent);
        if (_teams.Count == 0)
            return _candidates.MinBy(p => DistanceSquared(p, _spawnHalfExtent.HasValue ? Vector3.Zero : _origin.Position));

        // Prefer a connected scatter: every new team has an opponent within 750 m.
        // Sparse rural data may need 1 km, a larger footprint within the ring, or bounded
        // settlements beyond it. Exhaust all three expansions before reducing separation.
        for (int rung = 0; rung < 5; rung++)
        {
            float minimum = rung == 4 ? separation * 0.65f : separation;
            float maximum = rung == 0 ? 750f : 1000f;
            Vector3? best = null;
            float score = float.PositiveInfinity;
            foreach (Vector3 candidate in rung == 3 ? _expandedCandidates : _candidates)
            {
                float fromOrigin = DistanceSquared(candidate, _origin.Position);
                if (!_spawnHalfExtent.HasValue && rung < 2 && fromOrigin > RadiusMetres * RadiusMetres) continue;
                float nearest = _teams.Values.Min(p => DistanceSquared(candidate, p));
                if (nearest < minimum * minimum || nearest > maximum * maximum) continue;
                float value = DistanceSquared(candidate, target);
                if (value >= score) continue;
                best = candidate;
                score = value;
            }
            if (best is Vector3 found)
            {
                if (rung >= 2) ExpandedPlacements++;
                if (rung == 4) ReducedSpacingPlacements++;
                return found;
            }
        }
        // Pathological custom datasets: remain near the match rather than throwing during
        // teleport. This degradation is exposed in diagnostics; shipped-map tests cover it.
        ReducedSpacingPlacements++;
        return _candidates.MaxBy(p => _teams.Values.Min(t => DistanceSquared(p, t))
            - 4f * MathF.Max(0, DistanceSquared(p, _origin.Position) - RadiusMetres * RadiusMetres));
    }

    private bool InBounds(Vector3 p) => float.IsFinite(p.Y)
        && MathF.Abs(p.X) <= _options.MapHalfExtentMetres - _options.MinimumEdgeMetres - 24f
        && MathF.Abs(p.Z) <= _options.MapHalfExtentMetres - _options.MinimumEdgeMetres - 24f
        && (_spawnHalfExtent is not float extent || (MathF.Abs(p.X) <= extent - 24f && MathF.Abs(p.Z) <= extent - 24f))
        && (_spawnBoundary is not GasCircle boundary || boundary.HorizontalDistanceTo(p) + 99f <= boundary.Radius);

    private bool InRing(Vector3 p) => _spawnHalfExtent.HasValue || _circle is not GasCircle c
        || _origin.Selection == DropSelection.NearestToCircle
        || c.HorizontalDistanceTo(p) + 24f <= c.Radius * _origin.RingFactorUsed;

    private bool InExpandedRing(Vector3 p) => _spawnHalfExtent.HasValue || _circle is not GasCircle c
        || c.HorizontalDistanceTo(p) + 24f <= c.Radius + PopulationOverflowOutsideSafeZoneMetres;

    public static float DistanceSquared(Vector3 a, Vector3 b) =>
        (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);
}
