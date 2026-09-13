using Cranberry.Zone.Inventory;

namespace Cranberry.Zone.Weapons;

/// <summary>Resolves cosmetic weapon items through their extracted weapon definition (PARAM1).</summary>
public static class WeaponItemProfiles
{
    // Some skin rewards (including AK item 4033) have no datasheet row of their own.
    // PARAM1 still names the same weapon definition as the base item. Only inherit a
    // profile when every extracted row for that weapon agrees on its gameplay fields.
    private static readonly IReadOnlyDictionary<uint, AugustWeaponFact> ByWeapon =
        AugustWeaponFacts.All.GroupBy(row => row.WeaponId)
            .Select(group => group.Select(row => row with { ItemId = 0 }).Distinct().ToArray())
            .Where(rows => rows.Length == 1)
            .ToDictionary(rows => rows[0].WeaponId, rows => rows[0]);

    public static bool TryGet(uint itemDefinitionId, out AugustWeaponFact fact)
    {
        if (itemDefinitionId == CrateOpeningWeapon.ItemId)
        {
            fact = CrateOpeningWeapon.Profile;
            return true;
        }
        if (AugustWeaponFacts.TryGet(itemDefinitionId, out fact)) return true;
        if (InventoryItemFacts.TryGet(itemDefinitionId, out var item)
            && item.CodeFactory == ItemCodeFactory.Weapon
            && ByWeapon.TryGetValue(item.Param1, out fact))
        {
            fact = fact with { ItemId = itemDefinitionId };
            return true;
        }

        fact = default;
        return false;
    }
}
