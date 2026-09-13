using System.Numerics;
using Cranberry.Zone.Combat;

namespace Cranberry.Zone.Destructibles;

public readonly record struct DestructibleDamage(DestructibleProp Prop, int RemainingHealth, int Damage, bool Destroyed);

public sealed partial class DestructibleWorld
{
    private readonly Dictionary<uint, int> _health = [];
    private readonly List<DestructibleProp> _destroyed = [];
    public IReadOnlyList<DestructibleProp> Destroyed => _destroyed;
    public bool IsDestroyed(uint objectId) => _health.TryGetValue(objectId, out int health) && health <= 0;

    public DestructibleDamage? BreakGlass(DestructibleCatalog catalog, uint objectId)
    {
        if (!catalog.Props.TryGetValue(objectId, out var prop) || !prop.IsGlass || IsDestroyed(objectId)) return null;
        int remaining = _health.GetValueOrDefault(objectId, prop.Health);
        _health[objectId] = 0;
        _destroyed.Add(prop);
        return new(prop, 0, remaining, true);
    }

    public DestructibleDamage? Hit(DestructibleCatalog catalog, in DestructibleHit report,
        ShooterCombatState shooter, CombatOptions options, Vector3 shooterPosition, long nowMs)
    {
        if (!options.Enabled || options.Layout == WeaponFireLayout.Unknown || !options.EnableCombatDamage
            || !catalog.Props.TryGetValue(report.ObjectId, out var prop)
            || !string.Equals(prop.Model, report.Model, StringComparison.OrdinalIgnoreCase)) return null;
        int remaining = _health.GetValueOrDefault(prop.ObjectId, prop.Health);
        if (remaining <= 0) return null;
        float playerDistance = Vector3.Distance(shooterPosition, prop.Position);
        if (!float.IsFinite(playerDistance) || playerDistance > options.MaxHitDistance) return null;
        if (!shooter.TryConsumeHint(report.ProjectileId, nowMs, options.FireHintLifetimeMs, out var fire)) return null;
        float distance = Vector3.Distance(new(fire.X, fire.Y, fire.Z), prop.Position);
        if (!float.IsFinite(distance) || distance > options.MaxHitDistance) return null;
        uint weaponDefinitionId = RetailBalance.WeaponDefinitionIdFor(fire.ItemDefinitionId);
        int damage = RetailBalance.BodyDamageUnits(weaponDefinitionId,
            distance, options.UnmappedWeaponBodyUnits);
        if (damage <= 0) return null;
        // Owner tuning, 2026-09-06: five non-shotgun bullets per 5000-health wooden fence.
        // Keep shotgun pellet damage/falloff and glass on their existing weapon damage rules.
        if (prop.IsWoodenFence && RetailBalance.RowFor(weaponDefinitionId)?.Class != WeaponClass.Shotgun)
            damage = 1000;
        remaining = Math.Max(0, remaining - damage);
        _health[prop.ObjectId] = remaining;
        if (remaining == 0) _destroyed.Add(prop);
        return new(prop, remaining, damage, remaining == 0);
    }
}
