namespace Cranberry.Zone.Combat;

/// <summary>Three accepted trigger pulls ignite a can; a shotgun's pellets share one shot.</summary>
public sealed class FuelCanDurability
{
    public const int ShotsToExplode = 3;
    // Every pellet in one accepted fire has the same server timestamp and weapon definition.
    // The weapon's refire gate separates distinct trigger pulls; projectile numbers may repeat.
    private readonly HashSet<(ulong Shooter, uint Weapon, long AtMs)> _shots = [];
    public int Hits => _shots.Count;

    public bool Hit(ulong shooter, FireHint fire)
    {
        if (fire.ItemDefinitionId == 0) return false;
        _shots.Add((shooter, fire.ItemDefinitionId, fire.AtMs));
        return Hits >= ShotsToExplode;
    }
}
