using System.Numerics;

namespace Cranberry.Zone.Loot;

/// <summary>Translate authoritative trajectories into the August client's native visual rails.</summary>
public static class AirdropDisplayPlan
{
    public static IReadOnlyList<AirdropDisplaySegment> Create(
        AirdropFlight flight, AirdropOptions options, long startedAtMs, long clockMs)
    {
        var segments = new List<AirdropDisplaySegment>();
        if (clockMs < flight.DepartAtMs)
        {
            var plane = Segment(options.PlaneModelId, flight.SpawnAtMs, flight.DepartAtMs,
                flight.Start, flight.End, 0, rotation: 0.5f);
            if (flight.ClimbStartAtMs < flight.DepartAtMs && flight.End.Y != flight.Start.Y)
            {
                var knots = new List<AirdropDisplayWaypoint> { new(0, new Vector4(flight.Start, 0)) };
                long climbAt = Math.Max(flight.SpawnAtMs, flight.ClimbStartAtMs);
                for (int i = 0; i <= 32; i++)
                {
                    long at = climbAt + (long)Math.Round((flight.DepartAtMs - climbAt) * (i / 32d));
                    float progress = (float)((double)(at - flight.SpawnAtMs)
                        / (flight.DepartAtMs - flight.SpawnAtMs));
                    if (progress <= knots[^1].Progress) continue;
                    knots.Add(new(progress, new Vector4(flight.PositionAt(at), 0)));
                }
                plane = plane with { Waypoints = knots };
            }
            segments.Add(plane);
        }
        foreach (AirdropPayload payload in flight.Payloads)
        {
            // A late viewer must not replay explosions or spawn expired parachutes.
            if (clockMs >= payload.ImpactAtMs || payload.ImpactAtMs <= payload.ReleaseAtMs) continue;
            uint model = payload.IsBomb ? options.BombModelId : AirdropPackets.GroundCrateModelId;
            uint effect = payload.IsBomb ? options.BombExplosionEffectId : options.LandingEffectId;
            var segment = Segment(model, payload.ReleaseAtMs, payload.ImpactAtMs,
                payload.ReleasePosition, payload.ImpactPosition, effect);
            if (payload.IsBomb)
            {
                // The client interpolates rails linearly. Sample the ballistic curve rather
                // than rendering constant-speed descent while damage follows acceleration.
                var knots = new AirdropDisplayWaypoint[65];
                for (int i = 0; i < knots.Length; i++)
                {
                    float progress = i / 64f;
                    knots[i] = new(progress, new Vector4(payload.PositionAtProgress(progress), 0));
                }
                segment = segment with { Waypoints = knots };
            }
            segments.Add(segment);
            // 9219 is the canopy/cord actor. The crate body is a separate 9218 rail.
            if (!payload.IsBomb)
                segments.Add(segment with { ModelId = AirdropPackets.ParachuteCrateModelId, EndEffectId = 0 });
        }
        return segments;

        AirdropDisplaySegment Segment(uint model, long start, long end,
            Vector3 from, Vector3 to, uint effect, float rotation = 0) => new(
                model, unchecked((uint)(startedAtMs + start)), (end - start) / 1000f,
                rotation, effect, new Vector4(to, 0), 0,
                [new(0, new Vector4(from, 0)), new(1, new Vector4(to, 0))]);
    }
}
