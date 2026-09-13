using Cranberry.Zone.Combat;

namespace Cranberry.Zone.Weapons;

/// <summary>
/// The August gallery's hidden item 3750 (class 25079, loadout slot 47) names weapon 1457,
/// but has no ClientItemDatasheetData row. Its supplemental profile comes from the adopted
/// captured weapon 1457 / group 110 / mode 185 (CapturedWeaponFacts.SourceBodyMd5).
/// Keep this outside the generated August facts: these are reference values, not client rows.
/// </summary>
public static class CrateOpeningWeapon
{
    public const uint ItemId = 3750;
    public const uint WeaponId = 1457;
    public const uint FireGroupId = 110;
    public const uint LoadoutSlotId = 47;
    public const uint AmmoItemId = 1429;
    public const uint VisualItemId = 10;

    private static readonly CapturedWeaponRow CapturedWeapon = CapturedWeaponFacts.Weapons
        .Single(row => row.WeaponDefinitionId == WeaponId && row.FireGroupId == FireGroupId);
    private static readonly CapturedFireModeRow CapturedMode = CapturedWeaponFacts.FireModes
        .Single(row => row.FireModeId == CapturedWeapon.FireModeIds.Single());

    public static AugustWeaponFact Profile { get; } = new(ItemId, WeaponId, FireGroupId,
        checked((int)CapturedWord(WeaponListLayouts.FireModeRefireTime)),
        checked((int)CapturedWord(WeaponListLayouts.FireModeReloadTime)),
        checked((int)CapturedWeapon.ClipSize));

    public static bool IsSupported(WeaponStageOptions options) => options.SendWeaponDefinitions
        && options.PopulateWeaponDefinitions && options.PopulateFireGroups && options.PopulateFireModes
        && options.PopulateAmmoSlots && options.PopulateFireModeProjectiles && options.SendProjectileDefinitions
        && options.WriteWeaponDefinitionBodyId && options.WriteWeaponItemAddTail
        && options.WriteWeaponTailMagazine && options.AllowWielding;

    /// <summary>
    /// Append only the gallery's missing records. Mode IDs use the existing August writer's
    /// group*2 convention. The capture has one primary mode; the second uses the same gallery
    /// parameters for August's mandatory index-1 local attack gate (FireGroupDefinition).
    /// This compatibility projection does not alter any ordinary weapon's group or mode.
    /// </summary>
    public static WeaponDefinitionsBlob Apply(WeaponDefinitionsBlob ordinary, WeaponStageOptions options)
    {
        if (!IsSupported(options)) return ordinary;
        if (ordinary.WeaponDefinitions!.Any(row => row.WeaponDefinitionId == WeaponId)) return ordinary;
        var handling = CapturedWeaponHandlingFacts.ByWeaponId[WeaponId];
        uint[] modes = [AugustWeaponTable.FireModeIdFor(FireGroupId, 0), AugustWeaponTable.FireModeIdFor(FireGroupId, 1)];
        if (ordinary.FireGroups!.Any(row => row.FireGroupId == FireGroupId)
            || ordinary.FireModes!.Any(row => modes.Contains(row.FireModeId)))
            throw new InvalidOperationException("Crate opening weapon identities collide with the ordinary table.");
        var weapon = new WeaponDefinitionRecord(WeaponId, [FireGroupId],
            AmmoSlots: [new(AmmoItemId, CapturedWeapon.ClipSize)],
            ToIronSightsTimeMs: checked((int)handling.AimInMs),
            FromIronSightsTimeMs: checked((int)handling.AimOutMs))
        {
            WeaponGroupId = handling.WeaponGroupId,
            EquipTimeMs = handling.EquipMs, UnequipTimeMs = handling.UnequipMs,
            AimInAnimationTimeMs = handling.AimInAnimMs, AimOutAnimationTimeMs = handling.AimOutAnimMs,
            SprintRecoveryTimeMs = handling.SprintRecoveryMs, AnimationSetName = handling.AnimationSetName,
            AudioGameObject = CapturedWeaponAudioFacts.ByWeaponDefinitionId[WeaponId].Hash,
        };
        var overrides = CapturedWeaponFacts.CrossedOffsets.Select((offset, index) =>
                new KeyValuePair<short, uint>(offset, CapturedMode.Words[index])).ToDictionary();
        overrides[CapturedWeaponFacts.Flags0Offset] = CapturedMode.Flags0;
        overrides[CapturedWeaponFacts.Flags1Offset] = CapturedMode.Flags1;
        overrides[CapturedWeaponFacts.Flags2Offset] = (uint)(CapturedMode.Flags2 & 0x60);
        overrides[WeaponListLayouts.FireModeAmmoItemId] = AmmoItemId;

        // The gallery item and the standard AR-15 share August locale/name and ammo identity.
        // Use the base AR-15's extracted effect group, not the 1087 effect-group ID.
        uint effectGroup = AugustFireModeFacts.All.First(row => row.FireGroupId == 6 && row.ModeIndex == 0).EffectGroup;
        uint projectileId = AugustProjectileTable.ProjectileForAmmoItem(AmmoItemId);
        var cone = (ordinary.ConeOfFire ?? []).ToList();
        uint coneId = CapturedWord(WeaponListLayouts.FireModePlayerStateGroupId);
        if (!cone.Any(row => row.ConeOfFireId == coneId))
            cone.Add(CapturedWeaponTable.ConeOfFireRecords.Single(row => row.ConeOfFireId == coneId));
        var aim = (ordinary.AimAssist ?? []).ToList();
        uint aimId = CapturedWord(WeaponListLayouts.FireModeAimAssistConfig);
        if (!aim.Any(row => row.AimAssistId == aimId))
            aim.Add(CapturedWeaponTable.AimAssistRecords.Single(row => row.AimAssistId == aimId));
        return ordinary with
        {
            WeaponDefinitions = [.. ordinary.WeaponDefinitions!, weapon],
            FireGroups = [.. ordinary.FireGroups!, new(FireGroupId, handling.FireGroupFlags, modes)],
            FireModes = [.. ordinary.FireModes!, .. modes.Select(id => new FireModeRecord(id, id,
                RefireTimeMs: Profile.RefireTimeMs, ReloadTimeMs: Profile.ReloadTimeMs,
                EffectGroup: options.WriteFireEffect ? effectGroup : 0) { Overrides = overrides })],
            FireModeProjectiles = [.. ordinary.FireModeProjectiles!, .. modes.Select(id => new FireModeProjectileRecord(id, AmmoItemId, projectileId))],
            ConeOfFire = cone, AimAssist = aim,
        };
    }

    private static uint CapturedWord(short offset)
    {
        int index = Array.IndexOf(CapturedWeaponFacts.CrossedOffsets, offset);
        if (index < 0) throw new InvalidOperationException($"Missing captured gallery field {offset:x}.");
        return CapturedMode.Words[index];
    }
}
