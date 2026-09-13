using System.Numerics;

namespace Cranberry.Zone.Movement;

/// <summary>Accumulates movement from the start of a stationary action, ignoring camera motion and pose jitter.</summary>
public sealed class StationaryCastPosition(Vector3? origin, double radius)
{
    public Vector3? Origin { get; private set; } = origin;

    public bool HasMoved(Vector3? position)
    {
        if (position is not Vector3 current || !float.IsFinite(current.X)
            || !float.IsFinite(current.Y) || !float.IsFinite(current.Z)) return false;
        if (Origin is not Vector3 start)
        {
            Origin = current;
            return false;
        }
        return Vector3.DistanceSquared(start, current) > radius * radius;
    }
}
