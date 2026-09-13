namespace Cranberry.Zone.Vehicles;

/// <summary>Owner's 2026-09-06 AR reference; other weapons retain their previous baseline pending retail evidence.</summary>
public static class VehicleCombatBalance
{
    // Owner's small wear cost for each newly broken object: 0.5% of maximum condition.
    public static uint DestructibleImpactDamage(uint maxHealth) =>
        maxHealth == 0 ? 0 : Math.Max(1u, maxHealth / 200u);

    public static uint BulletDamage(uint weaponDefinitionId, uint previousDamage) =>
        weaponDefinitionId is 6 or 10 ? 1000u : previousDamage;

    // Exact August DestroyedInfo.txt death-composite ids, not the looping damage-stage effects.
    public static uint ExplosionEffect(uint vehicleId) => vehicleId switch
    {
        1 => 135, 2 => 326, 3 => 286, 5 => 357, _ => 0,
    };

    // Owner tuning, 2026-09-10: retire burnt vehicles seven seconds after the explosion.
    public const long WreckLifetimeMs = 7_000;

    // August DestroyedInfo.txt rows 1, 2, 3 and 5, verified against Models.txt.
    public static uint DestroyedModel(uint vehicleId) => vehicleId switch
    {
        1 => 7226, 2 => 9315, 3 => 9316, 5 => 9593, _ => 0,
    };

    public static uint CorpseEffect(uint vehicleId) => vehicleId switch
    {
        1 => 5207, 2 => 5208, 3 => 5209, 5 => 5206, _ => 0,
    };
}
