using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Vehicles;

/// <summary>
/// Vehicle interest is loaded ahead of the native draw range and retained across landing.
/// These are server visibility settings, not a change to pad occupancy or client LOD.
/// See docs/vehicle-spawns-20260907.md for the reproduced eviction and coverage checks.
/// </summary>
public sealed record VehicleStreamOptions
{
    /// <summary>The continuous-visibility defaults.</summary>
    public static VehicleStreamOptions Default { get; } = new();

    /// <summary>Enable recurring vehicle interest updates on the shared world pump.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>1,700 m horizontal prefetch, including 200 m beyond the native 1,500 m draw range.</summary>
    public float StreamRadiusMetres { get; init; } = Rulings.VehiclesPlan.StreamRadiusMetres;

    /// <summary>Retain cars until 2,000 m; landing uses the same default disc as descent.</summary>
    public float DespawnRadiusMetres { get; init; } = Rulings.VehiclesPlan.DespawnRadiusMetres;

    /// <summary>192 held cars; the full-map retail audit bounds every retention disc by 163, including between samples.</summary>
    public int MaxLive { get; init; } = Rulings.VehiclesPlan.MaxLive;

    /// <summary>At most 32 additions per sweep, still paid out through DrainBurst packet slices.</summary>
    public int MaxPerRestream { get; init; } = Rulings.VehiclesPlan.MaxPerRestream;

    /// <summary>Reevaluate after 2% of the radius (34 m), within the 200 m prefetch margin while driving.</summary>
    public float RestreamFraction { get; init; } = Rulings.VehiclesPlan.RestreamFraction;


    /// <summary>Check vehicle interest once per second on the existing shared pump. Loot and door arms retain their own rates.</summary>
    public int RestreamIntervalMs { get; init; } = Rulings.VehiclesPlan.RestreamIntervalMs;

    /// <summary>Bytes on the wire for one spawned car: <c>d7</c> 229 + <c>db</c> 274.</summary>
    public const int SpawnBytes = 503;

    /// <summary>Bytes for one destroyed car: <c>0f 01 RemovePlayer</c>.</summary>
    public const int DespawnBytes = 12;

    /// <summary>What one tick of this arm costs on the wire, for the budget tests and the log line.</summary>
    public static int EstimateTickBytes(int spawns, int evictions) =>
        (Math.Max(0, spawns) * SpawnBytes) + (Math.Max(0, evictions) * DespawnBytes);

    /// <summary>
    /// The despawn radius actually used: never below <see cref="StreamRadiusMetres"/>, because a
    /// hand-set despawn radius <i>inside</i> the stream radius makes every tick spawn a car and then
    /// immediately evict it — the exact churn the hysteresis exists to prevent, inverted.
    /// </summary>
    public float EffectiveDespawnRadiusMetres =>
        MathF.Max(StreamRadiusMetres, DespawnRadiusMetres);
}
