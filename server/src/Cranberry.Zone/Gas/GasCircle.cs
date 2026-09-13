using System.Numerics;

namespace Cranberry.Zone.Gas;

/// <summary>
/// One horizontal safe circle. The August client stores a circle as a <c>f32×4</c> centre plus a
/// <c>f32</c> radius (<c>ce 01</c> ring, <c>FUN_140bafe10</c>; <c>ce 02</c> safe zone,
/// <c>FUN_140baff10</c>) and never compares the vertical axis, so containment here is the X/Z
/// distance only — <see cref="Vector3.Y"/> is world height and is carried through untouched.
/// </summary>
public readonly record struct GasCircle(Vector3 Centre, float Radius)
{
    /// <summary>The centre in the <c>f32×4</c> form both <c>ce</c> writers take (w = 1).</summary>
    public Vector4 CentreVector4 => new(Centre.X, Centre.Y, Centre.Z, 1f);

    /// <summary>
    /// The centre as <c>ce 02</c> carries it: <c>[x, 0, z, 0]</c>. The owner's blessed build writes
    /// exactly that for the green next-safe-zone circle
    /// (<c>C:\Z1\Server\Zone\ZoneMatch.cs:2450</c>, <c>WriteVector4(centreX, 0f, centreZ, 0f)</c>)
    /// against Cranberry's <c>w = 1</c>. <c>ce 01</c> keeps <see cref="CentreVector4"/>'s
    /// <c>w = 1.0</c>, which is capture-proven in Z1.
    /// <para>
    /// <b>UNCERTAIN whether it matters</b> (docs/87 §7 edit 7, §8): <c>ce 02</c>'s only consumers
    /// are two Flash data providers and nothing has ever observed either value. It is adopted on
    /// the principle that his bytes are the ones that were click-tested. Today's centres already
    /// carry <c>Y = 0</c>, so in practice this changes one field.
    /// </para>
    /// </summary>
    public Vector4 SafeZoneVector4 => new(Centre.X, 0f, Centre.Z, 0f);

    /// <summary>Horizontal (X/Z) distance from the centre; <see cref="float.NaN"/> for a non-finite position.</summary>
    public float HorizontalDistanceTo(Vector3 position)
    {
        float dx = position.X - Centre.X;
        float dz = position.Z - Centre.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>
    /// True when the position is inside (or exactly on) the circle. A position the server has not
    /// resolved yet — a NaN or infinite sample from a sparse movement record — counts as inside:
    /// the gas must never punish a player for a gap in our own tracking.
    /// </summary>
    public bool Contains(Vector3 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Z))
        {
            return true;
        }

        float dx = position.X - Centre.X;
        float dz = position.Z - Centre.Z;
        return (dx * dx) + (dz * dz) <= Radius * Radius;
    }

    /// <summary>Linear blend of centre and radius, <paramref name="t"/> clamped to 0..1.</summary>
    public static GasCircle Lerp(GasCircle from, GasCircle to, float t)
    {
        float amount = Math.Clamp(t, 0f, 1f);
        return new GasCircle(
            Vector3.Lerp(from.Centre, to.Centre, amount),
            from.Radius + ((to.Radius - from.Radius) * amount));
    }

    /// <summary>
    /// True when <paramref name="inner"/> lies wholly within this circle — the containment rule
    /// every revealed circle is generated under, so a player already inside the current circle can
    /// always reach the next one without leaving the safe area.
    /// </summary>
    public bool Contains(GasCircle inner) =>
        HorizontalDistanceTo(inner.Centre) + inner.Radius <= Radius + ContainmentEpsilon;

    /// <summary>Single-precision slack for <see cref="Contains(GasCircle)"/> at play-area scale.</summary>
    public const float ContainmentEpsilon = 1e-3f;
}
