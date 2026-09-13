using System.Numerics;

namespace Cranberry.Zone.Loot;

/// <summary>August uses the C130 for both kinds; the distinct bomber jet arrived in February 2018.</summary>
public enum AirdropFlightKind { Supply, Bomber }

/// <summary>An immutable payload trajectory. Crates descend on parachutes; bombs accelerate downwards.</summary>
public sealed record AirdropPayload(
    int Index,
    long ReleaseAtMs,
    long ImpactAtMs,
    Vector3 ReleasePosition,
    Vector3 ImpactPosition,
    bool IsBomb)
{
    public bool IsActive(long clockMs) => clockMs >= ReleaseAtMs && clockMs < ImpactAtMs;

    public Vector3 PositionAt(long clockMs)
    {
        if (ImpactAtMs <= ReleaseAtMs || clockMs >= ImpactAtMs) return ImpactPosition;
        if (clockMs <= ReleaseAtMs) return ReleasePosition;
        float t = (float)((double)(clockMs - ReleaseAtMs) / (ImpactAtMs - ReleaseAtMs));
        return PositionAtProgress(t);
    }

    public Vector3 PositionAtProgress(float progress)
    {
        float t = Math.Clamp(progress, 0f, 1f);
        Vector3 p = Vector3.Lerp(ReleasePosition, ImpactPosition, t);
        if (IsBomb) p.Y = ReleasePosition.Y + ((ImpactPosition.Y - ReleasePosition.Y) * t * t);
        return p;
    }
}

/// <summary>
/// A flight fixed at scheduling time. Positions are functions of the match clock, never accumulated
/// per-client movement. The route passes through the safe-zone centre used to schedule it.
/// Speeds and trajectory dimensions are reconstruction settings, not recovered retail constants.
/// </summary>
public sealed record AirdropFlight(
    int Index,
    int DropIndex,
    AirdropFlightKind Kind,
    long SpawnAtMs,
    long DepartAtMs,
    Vector3 Start,
    Vector3 End,
    Vector3 Centre,
    IReadOnlyList<AirdropPayload> Payloads)
{
    /// <summary>After the last payload leaves, the plane climbs out. Shape is dated retail;
    /// climb height and easing are reconstruction. MaxValue disables the climb.</summary>
    public long ClimbStartAtMs { get; init; } = long.MaxValue;
    public bool IsActive(long clockMs) => clockMs >= SpawnAtMs && clockMs < DepartAtMs;
    public Vector3 Direction => Vector3.Normalize(new Vector3(End.X - Start.X, 0, End.Z - Start.Z));
    public float SpeedMetresPerSecond => new Vector2(End.X - Start.X, End.Z - Start.Z).Length()
        * 1000f / (DepartAtMs - SpawnAtMs);
    public Vector3 Velocity => Direction * SpeedMetresPerSecond;
    public Quaternion Rotation => Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(Direction.X, Direction.Z));

    public Vector3 PositionAt(long clockMs)
    {
        if (clockMs <= SpawnAtMs) return Start;
        if (clockMs >= DepartAtMs) return End;
        Vector3 position = Vector3.Lerp(Start, End, (float)((double)(clockMs - SpawnAtMs) / (DepartAtMs - SpawnAtMs)));
        float climb = ClimbStartAtMs >= DepartAtMs || clockMs <= ClimbStartAtMs ? 0
            : (float)((double)(clockMs - ClimbStartAtMs) / (DepartAtMs - ClimbStartAtMs));
        position.Y = Start.Y + ((End.Y - Start.Y) * climb * climb);
        return position;
    }

    public Vector3 VelocityAt(long clockMs)
    {
        Vector3 velocity = Velocity;
        if (ClimbStartAtMs < DepartAtMs && clockMs > ClimbStartAtMs)
        {
            double duration = (DepartAtMs - ClimbStartAtMs) / 1000.0;
            double elapsed = Math.Min(clockMs, DepartAtMs) - ClimbStartAtMs;
            velocity.Y = (float)(2 * (End.Y - Start.Y) * (elapsed / 1000) / (duration * duration));
        }
        return velocity;
    }
}
