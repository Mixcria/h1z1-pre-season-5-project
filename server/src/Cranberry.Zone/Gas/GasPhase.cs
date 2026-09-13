namespace Cranberry.Zone.Gas;

/// <summary>
/// One revealed circle of a match, expressed on the match clock (milliseconds since the
/// <c>ce 16</c> StartMatch that opened it).
/// </summary>
/// <param name="Index">1-based phase number.</param>
/// <param name="Origin">The circle the ring closes from — the previous phase's target.</param>
/// <param name="Target">The circle revealed by <c>ce 01</c>/<c>ce 02</c>.</param>
/// <param name="RevealAtMs">When the target circle becomes visible to the client.</param>
/// <param name="ShrinkStartAtMs">When the active circle starts moving (end of the warning head).</param>
/// <param name="ClosedAtMs">When the active circle has fully become <paramref name="Target"/>.</param>
/// <param name="DamagePerTick">Health removed per tick from a player outside the active circle.</param>
public readonly record struct GasPhase(
    int Index,
    GasCircle Origin,
    GasCircle Target,
    long RevealAtMs,
    long ShrinkStartAtMs,
    long ClosedAtMs,
    uint DamagePerTick)
{
    /// <summary>Reveal → fully-closed window.</summary>
    public long WindowMs => ClosedAtMs - RevealAtMs;

    /// <summary>The moving part of the window (window minus the warning head).</summary>
    public long ShrinkDurationMs => ClosedAtMs - ShrinkStartAtMs;

    /// <summary>
    /// The active (damaging) circle at a match-clock time. It holds at <see cref="Origin"/> through
    /// the warning head, then interpolates linearly to <see cref="Target"/> — the same
    /// <c>t = clamp((now − start)/duration, 0, 1)</c>, <c>lerp(from, to, t)</c> model the client's
    /// own ring renderer uses (docs/15 §3b, <c>FUN_140bbe3e0</c>), extended to the centre because
    /// the server has to answer "is this player in the gas" while the ring is travelling.
    /// </summary>
    public GasCircle CircleAt(long matchClockMs)
    {
        if (matchClockMs <= ShrinkStartAtMs)
        {
            return Origin;
        }

        if (matchClockMs >= ClosedAtMs)
        {
            return Target;
        }

        long duration = ShrinkDurationMs;
        float t = duration <= 0 ? 1f : (matchClockMs - ShrinkStartAtMs) / (float)duration;
        return GasCircle.Lerp(Origin, Target, t);
    }
}
