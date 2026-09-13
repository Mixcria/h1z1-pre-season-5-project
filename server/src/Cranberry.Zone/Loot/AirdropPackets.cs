using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone.Loot;

/// <summary>
/// One knot in the client's delivery rail. XYZ is world position; W is an interpolated
/// rotation component, not a homogeneous coordinate. Use W = 0 for a level plane or crate.
/// </summary>
public readonly record struct AirdropDisplayWaypoint(float Progress, Vector4 Position);

/// <summary>
/// A client-owned visual actor on a delivery rail. ActivationTimeMs uses the synchronized
/// server millisecond clock, DurationSeconds is in seconds, and Rotation is an offset in turns.
/// The client plays EndEffectId at EndPosition after removing the actor; ExpirationDelayMs
/// extends its native animation expiry. These actors are visual only: loot and damage remain
/// server-authoritative.
/// </summary>
public sealed record AirdropDisplaySegment(
    uint ModelId,
    uint ActivationTimeMs,
    float DurationSeconds,
    float Rotation,
    uint EndEffectId,
    Vector4 EndPosition,
    uint ExpirationDelayMs,
    IReadOnlyList<AirdropDisplayWaypoint> Waypoints);

/// <summary>
/// August 2017's native delivery display packet, independently decoded from H1Z1.exe 1148.
/// Command dispatcher 14129ad10 case 50 calls 14129c140; parser 14126e8b0 reads the
/// header/list and 14126e510 reads each segment. Runtime 1413d5740 ignores duplicate delivery
/// ids, and 1413d5f80 owns actor creation, route interpolation, sounds, landing and expiry.
/// Evidence: out/ghidra-aug/airdrop-{reader,tick}-20260906. No 1087 opcode translation is used.
/// </summary>
public static class AirdropPackets
{
    public const byte Opcode = 0x09;
    public const ushort DeliveryDisplayInfoSubOpcode = 0x0050;
    public const uint PlaneModelId = 9215;
    public const uint GroundCrateModelId = 9218;
    public const uint ParachuteCrateModelId = 9219;
    public const uint BombModelId = 9372;
    public const uint LandingEffectId = 5038;
    public const uint BombExplosionEffectId = 5328;

    /// <summary>
    /// Writes 09 50 00, u32 delivery id, u32 segment count, and segment records. A segment is
    /// 44 fixed bytes followed by 20 bytes per waypoint. The id must be new while another
    /// delivery with that id is alive; resending that id does not replace its routes.
    /// </summary>
    public static byte[] DeliveryDisplayInfo(uint deliveryId, IReadOnlyList<AirdropDisplaySegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            throw new ArgumentException("A delivery needs at least one visual segment.", nameof(segments));
        }

        int length = 11;
        foreach (AirdropDisplaySegment segment in segments)
        {
            Validate(segment);
            length = checked(length + 44 + 20 * segment.Waypoints.Count);
        }

        using var writer = new PacketWriter(length);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(DeliveryDisplayInfoSubOpcode);
        writer.WriteUInt32(deliveryId);
        writer.WriteUInt32((uint)segments.Count);
        foreach (AirdropDisplaySegment segment in segments)
        {
            writer.WriteUInt32(segment.ModelId);
            writer.WriteUInt32(segment.ActivationTimeMs);
            writer.WriteSingle(segment.DurationSeconds);
            writer.WriteSingle(segment.Rotation);
            writer.WriteUInt32(segment.EndEffectId);
            WriteVector(writer, segment.EndPosition);
            writer.WriteUInt32(segment.ExpirationDelayMs);
            writer.WriteUInt32((uint)segment.Waypoints.Count);
            foreach (AirdropDisplayWaypoint waypoint in segment.Waypoints)
            {
                writer.WriteSingle(waypoint.Progress);
                WriteVector(writer, waypoint.Position);
            }
        }

        return writer.Written.ToArray();
    }

    private static void Validate(AirdropDisplaySegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(segment.Waypoints);
        if (segment.ModelId == 0 || !float.IsFinite(segment.DurationSeconds) || segment.DurationSeconds <= 0
            || !float.IsFinite(segment.Rotation) || !Finite(segment.EndPosition))
        {
            throw new ArgumentException("A delivery segment needs a model, positive finite duration and finite coordinates.", nameof(segment));
        }

        // The runtime interpolates between adjacent knots and divides by their progress delta.
        // Full coverage and strictly increasing keys avoid origin fallback and zero divisors.
        if (segment.Waypoints.Count < 2 || segment.Waypoints[0].Progress != 0f
            || segment.Waypoints[^1].Progress != 1f)
        {
            throw new ArgumentException("Delivery waypoints must cover progress 0 through 1.", nameof(segment));
        }

        float previous = -1f;
        foreach (AirdropDisplayWaypoint waypoint in segment.Waypoints)
        {
            if (!float.IsFinite(waypoint.Progress) || waypoint.Progress <= previous
                || waypoint.Progress > 1f || !Finite(waypoint.Position))
            {
                throw new ArgumentException("Delivery waypoints must have finite coordinates and strictly increasing progress.", nameof(segment));
            }

            previous = waypoint.Progress;
        }
    }

    private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y)
        && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static void WriteVector(PacketWriter writer, Vector4 value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteSingle(value.Z);
        writer.WriteSingle(value.W);
    }
}
