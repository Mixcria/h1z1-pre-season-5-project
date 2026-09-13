namespace Cranberry.Zone.Weapons;

/// <summary>Shorter native draw transitions for the three common long guns.</summary>
public static class ResponsiveWeaponHandling
{
    // ROTK reference 2026-09-11, WeaponDefinitions frames 381/86216: the corresponding
    // list-0 equip fields are 150, versus 550 in our older captured overlay. This is
    // an adaptation through August's verified +0x28 field, not a cross-version blob.
    // Retain the native draw state and outgoing unequip, reload, sprint and fire clocks.
    public const uint EquipTimeMs = 150;

    public static bool AppliesTo(uint weaponDefinitionId) => weaponDefinitionId is 6 or 1374 or 1405;

    public static WeaponDefinitionsBlob Apply(WeaponDefinitionsBlob blob) => blob.WeaponDefinitions is null
        ? blob
        : blob with
        {
            WeaponDefinitions = [.. blob.WeaponDefinitions.Select(record => AppliesTo(record.WeaponDefinitionId)
                ? record with { EquipTimeMs = EquipTimeMs } : record)],
        };
}
