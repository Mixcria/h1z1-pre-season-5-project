using System.Numerics;

namespace Cranberry.Zone.Loot;

/// <summary>
/// Reconstructed bomb falloff, shared by player and vehicle damage integration. August's effect
/// names establish a 10 m visual effect, not the damage radius. The February 2018 reduction confirms
/// earlier bombs could destroy vehicles in one hit; exact August damage curves were not recovered.
/// </summary>
public static class AirdropBombDamage
{
    public static int At(AirdropOptions options, Vector3 impact, Vector3 target, bool vehicle = false)
    {
        float distance = Vector3.Distance(impact, target);
        if (!float.IsFinite(distance) || distance >= options.BombBlastRadiusMetres) return 0;
        int maximum = vehicle ? options.BombVehicleDamage : options.BombPlayerDamage;
        if (distance <= options.BombLethalRadiusMetres) return maximum;
        float fraction = (options.BombBlastRadiusMetres - distance)
            / (options.BombBlastRadiusMetres - options.BombLethalRadiusMetres);
        return Math.Max(0, (int)Math.Ceiling(maximum * fraction));
    }
}
