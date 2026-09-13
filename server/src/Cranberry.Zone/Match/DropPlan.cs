using System.Globalization;
using System.Numerics;

namespace Cranberry.Zone.Match;

/// <summary>Which rung of docs/48 §5.7's ladder produced the place a match dropped over.</summary>
public enum DropSelection : byte
{
    /// <summary>The normal case: the place's anchor is inside the first safe circle.</summary>
    InsideFirstCircle,

    /// <summary>Nothing was inside the circle, so <see cref="DropOptions.RingFactor"/> was widened. Never seen under the shipped gas settings.</summary>
    WidenedCircle,

    /// <summary>Even the widest rung was empty, so the place nearest the ring centre was taken.</summary>
    NearestToCircle,

    /// <summary>There is no ring at all (gas disabled), so the draw ran over every place on the map.</summary>
    WholeMap,
}

/// <summary>
/// Where one match drops, and enough of the reasoning to put it on one log line. Pure data: the
/// planner sends nothing, and the caller decides what to do with the two positions.
/// </summary>
/// <param name="Poi">The named place drawn.</param>
/// <param name="Position">The touchdown point — one of <paramref name="Poi"/>'s own markers, or its anchor.</param>
/// <param name="AirPosition">Where the player and the parachute are placed.</param>
/// <param name="Selection">Which rung of the ladder chose <paramref name="Poi"/>.</param>
/// <param name="RingFactorUsed">The ring multiple in force when the eligible set was taken.</param>
/// <param name="WidenSteps">How many widening rungs ran; 0 in the normal case.</param>
/// <param name="EligibleCount">How many places were eligible at that rung.</param>
/// <param name="EligibleRank">1-based rank of <paramref name="Poi"/> among the eligible set by distance to the ring centre.</param>
/// <param name="DistanceToCircleCentre">Horizontal distance from <paramref name="Position"/> to the first circle's centre, or <see cref="float.NaN"/> without a ring.</param>
/// <param name="MarkersWithinLootRadius">Markers within <see cref="DropOptions.LootRadius"/> of <paramref name="Position"/>.</param>
/// <param name="JitterAttemptsUsed">How many marker draws it took; 0 means the anchor was used directly.</param>
/// <param name="UsedAnchor">True when the jitter draw never met <see cref="DropOptions.MinimumMarkers"/> and the anchor was taken.</param>
/// <param name="MatchSeed">The match seed this plan replays from.</param>
/// <param name="DropSeed">The salted sub-seed the draws actually came from.</param>
public sealed record DropPlan(
    DropPoi Poi,
    Vector3 Position,
    Vector3 AirPosition,
    DropSelection Selection,
    float RingFactorUsed,
    int WidenSteps,
    int EligibleCount,
    int EligibleRank,
    float DistanceToCircleCentre,
    int MarkersWithinLootRadius,
    int JitterAttemptsUsed,
    bool UsedAnchor,
    ulong MatchSeed,
    ulong DropSeed)
{
    /// <summary>Coordinates came from the shared roster scatter; Poi is the nearest named place.</summary>
    public bool PopulationPlacement { get; init; }
    /// <summary>The touchdown point in the <c>f32x4</c> form <c>UpdateLocation</c> takes (w = 1).</summary>
    public Vector4 PositionVector4 => new(Position.X, Position.Y, Position.Z, 1f);

    /// <summary>The air spawn in the <c>f32x4</c> form <c>UpdateLocation</c> and <c>SendParachute</c> take.</summary>
    public Vector4 AirVector4 => new(AirPosition.X, AirPosition.Y, AirPosition.Z, 1f);

    /// <summary>How far the player falls.</summary>
    public float DescentMetres => AirPosition.Y - Position.Y;

    /// <summary>
    /// The one line the host log prints, which is the whole point of the lane: it names the place,
    /// says how much loot is under the player, and carries the seed that replays the match.
    /// </summary>
    public string Describe()
    {
        if (PopulationPlacement)
            return FormattableString.Invariant($"{Poi.Area} vicinity, roster placement <{Position.X:F1}, {Position.Y:F1}, {Position.Z:F1}>, air Y {AirPosition.Y:F0} m, {MarkersWithinLootRadius} loot markers within {LootRadiusUsed:F0} m, match seed {MatchSeeds.Format(MatchSeed)}");
        var invariant = CultureInfo.InvariantCulture;
        string ring = Selection switch
        {
            DropSelection.WholeMap => "no ring (gas disabled)",
            DropSelection.NearestToCircle =>
                string.Format(invariant, "{0:F0} m from the phase-1 ring centre, nearest of {1} places (ring empty at x{2:F2})",
                    DistanceToCircleCentre, EligibleCount, RingFactorUsed),
            DropSelection.WidenedCircle =>
                string.Format(invariant, "{0:F0} m from the phase-1 ring centre, {1} of {2} eligible (ring widened x{3:F2}, {4} rung(s))",
                    DistanceToCircleCentre, EligibleRank, EligibleCount, RingFactorUsed, WidenSteps),
            _ => string.Format(invariant, "{0:F0} m from the phase-1 ring centre, {1} of {2} eligible",
                    DistanceToCircleCentre, EligibleRank, EligibleCount),
        };

        string point = UsedAnchor
            ? "anchor"
            : string.Format(invariant, "marker (draw {0})", JitterAttemptsUsed);

        return string.Format(
            invariant,
            "{0} ({1:N0} markers, {2:N0} within {3} m) at {4}, <{5:F1}, {6:F1}, {7:F1}> from air <{8:F1}, {9:F1}, {10:F1}> ({11:F0} m fall); {12}; match seed {13}",
            Poi.Area,
            Poi.MarkerCount,
            MarkersWithinLootRadius,
            (int)MathF.Round(LootRadiusUsed),
            point,
            Position.X,
            Position.Y,
            Position.Z,
            AirPosition.X,
            AirPosition.Y,
            AirPosition.Z,
            DescentMetres,
            ring,
            MatchSeeds.Format(MatchSeed));
    }

    /// <summary>The radius <see cref="MarkersWithinLootRadius"/> was counted at, carried so <see cref="Describe"/> needs no options.</summary>
    public float LootRadiusUsed { get; init; } = 80f;

    public override string ToString() => Describe();
}
