namespace Cranberry.Zone.Combat;

/// <summary>Server tuning for red barrels and dropped fuel; effects are August client data.</summary>
public static class WorldExplosive
{
    public const uint EffectId = 5250; // PFX_Explosion_OilBarrel_04m
    public const float Radius = 5f;

    public static uint DamageAt(float distance, uint maximum = 10_000)
    {
        if (!float.IsFinite(distance) || distance < 0 || distance > Radius) return 0;
        return (uint)(maximum / MathF.Max(1f, distance));
    }
}
