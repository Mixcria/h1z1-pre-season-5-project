using System.Numerics;
using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Gas;

/// <summary>
/// The weighted draw over the August client's own <c>GasWeightArea.&lt;Poi&gt;</c> volumes
/// (docs/118 §3). The <b>boxes</b> are the client's, generated into
/// <see cref="AugustGasWeightAreas"/> by <c>tools/world/gas_weight_areas.py</c>; the <b>rule</b> —
/// a volume is drawn in proportion to its own footprint, and the destination is a point uniform
/// inside it — is D277.
/// <para>
/// <b>Exactly three draws</b>, in this order: the volume, then X, then Z. That is load-bearing:
/// <c>GasSchedule.Create</c> is a pure function of (settings, seed) and lane 3C's
/// <c>SharedMatchGas</c> depends on two controllers with the same seed producing the same circles,
/// so the number and order of <see cref="GasRandom"/> draws is part of this type's contract.
/// </para>
/// </summary>
public static class GasWeightAreas
{
    /// <summary>The client's nine volumes, in the client's own name order.</summary>
    public static IReadOnlyList<AugustGasWeightArea> All => AugustGasWeightAreas.All;

    /// <summary>
    /// The draw weight of one volume: its footprint raised to <paramref name="exponent"/>.
    /// <c>1</c> weights strictly by size (D277's default), <c>0</c> makes the nine equally likely.
    /// Never negative and never NaN — a volume the client ships must always be reachable.
    /// </summary>
    public static double WeightOf(in AugustGasWeightArea area, float exponent)
    {
        double footprint = Math.Max(0d, area.FootprintArea);
        if (footprint <= 0d)
        {
            return 0d;
        }

        if (!float.IsFinite(exponent) || exponent == 0f)
        {
            return 1d;
        }

        double weight = exponent == 1f ? footprint : Math.Pow(footprint, exponent);
        return double.IsFinite(weight) && weight > 0d ? weight : 0d;
    }

    /// <summary>
    /// Picks a volume by weight. Returns the index into <see cref="All"/>; <c>-1</c> only when the
    /// client ships none, which <see cref="AugustGasWeightAreas.Count"/> makes impossible today but
    /// which a caller must still not walk off.
    /// </summary>
    public static int DrawIndex(ref GasRandom random, float exponent)
    {
        AugustGasWeightArea[] areas = AugustGasWeightAreas.All;
        if (areas.Length == 0)
        {
            return -1;
        }

        double total = 0d;
        for (int index = 0; index < areas.Length; index++)
        {
            total += WeightOf(in areas[index], exponent);
        }

        double roll = random.NextDouble() * total;
        if (!(total > 0d))
        {
            // Every footprint is zero or unusable: fall back to a flat draw rather than to area 0,
            // so "the endgame can land in any of them" survives a degenerate table.
            return Math.Min(areas.Length - 1, (int)(roll * areas.Length));
        }

        double running = 0d;
        for (int index = 0; index < areas.Length; index++)
        {
            running += WeightOf(in areas[index], exponent);
            if (roll < running)
            {
                return index;
            }
        }

        return areas.Length - 1;
    }

    /// <summary>
    /// One match's destination: a volume drawn by weight, then a point drawn area-uniform inside
    /// that volume's own box. The point is horizontal — Y never enters a gas circle
    /// (<see cref="GasCircle"/>) — and is returned in world coordinates.
    /// </summary>
    /// <param name="random">Advanced by exactly three draws.</param>
    /// <param name="exponent"><see cref="GasSettings.PoiWeightExponent"/>.</param>
    /// <param name="areaIndex">Which volume was drawn, for the log line and the tests.</param>
    public static Vector2 DrawDestination(ref GasRandom random, float exponent, out int areaIndex)
    {
        areaIndex = DrawIndex(ref random, exponent);
        if (areaIndex < 0)
        {
            // No volume: consume the two remaining draws anyway so the seed stream does not change
            // shape, and let the caller keep the play-area centre.
            _ = random.NextDouble();
            _ = random.NextDouble();
            return Vector2.Zero;
        }

        ref readonly AugustGasWeightArea area = ref AugustGasWeightAreas.All[areaIndex];
        float x = area.MinX + ((float)random.NextDouble() * area.Width);
        float z = area.MinZ + ((float)random.NextDouble() * area.Depth);
        return new Vector2(x, z);
    }

    /// <summary>The client's own name of a drawn volume, or <c>"(none)"</c>.</summary>
    public static string NameOf(int areaIndex) =>
        areaIndex >= 0 && areaIndex < AugustGasWeightAreas.All.Length
            ? AugustGasWeightAreas.All[areaIndex].Name
            : "(none)";
}
