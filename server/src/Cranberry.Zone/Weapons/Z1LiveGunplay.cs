using Cranberry.Zone.Combat;

namespace Cranberry.Zone.Weapons;

/// <summary>Recorded gun tuning adapted to August's native definitions and authority clocks.</summary>
public static class Z1LiveGunplay
{
    public static bool TryClock(uint itemId, int index, short offset, out int milliseconds)
    {
        milliseconds = 0;
        if (!WeaponItemProfiles.TryGet(itemId, out var fact) || !AppliesTo(fact.WeaponId)
            || index < 0 || !Z1LiveGunplayFacts.WeaponModes.TryGetValue(fact.WeaponId, out var modes)
            || index >= modes.Length || !Z1LiveGunplayFacts.Modes[modes[index]].TryGetValue(offset, out uint value)
            || value > short.MaxValue) return false;
        milliseconds = (int)value;
        return true;
    }

    public static bool AppliesTo(uint weaponId) =>
        Z1LiveGunplayFacts.WeaponModes.ContainsKey(weaponId)
        && AmmoTypes.ByWeaponDefinitionId.TryGetValue(weaponId, out uint ammo)
        && ammo != AmmoTypes.ShotgunShellItemDefinitionId;

    public static WeaponDefinitionsBlob Apply(WeaponDefinitionsBlob blob, bool fastDraw)
    {
        if (blob.WeaponDefinitions is null || blob.FireGroups is null || blob.FireModes is not { Count: > 0 })
            return blob;
        var protectedGroups = blob.WeaponDefinitions.Where(w => !AppliesTo(w.WeaponDefinitionId))
            .SelectMany(w => w.FireGroupIds).ToHashSet();
        var replacements = new Dictionary<uint, IReadOnlyDictionary<short, uint>>();
        foreach (var weapon in blob.WeaponDefinitions.Where(w => AppliesTo(w.WeaponDefinitionId)))
        foreach (uint gid in weapon.FireGroupIds.Where(g => !protectedGroups.Contains(g)))
        {
            var group = blob.FireGroups.FirstOrDefault(g => g.FireGroupId == gid);
            if (group?.FireModeIds is null) continue;
            uint[] source = Z1LiveGunplayFacts.WeaponModes[weapon.WeaponDefinitionId];
            for (int i = 0; i < Math.Min(source.Length, group.FireModeIds.Count); i++)
                replacements.TryAdd(group.FireModeIds[i], Z1LiveGunplayFacts.Modes[source[i]]);
        }
        if (replacements.Count == 0) return blob;
        return blob with
        {
            WeaponDefinitions = [.. blob.WeaponDefinitions.Select(w => fastDraw && AppliesTo(w.WeaponDefinitionId)
                ? w with { EquipTimeMs = Math.Min(w.EquipTimeMs, 150u), UnequipTimeMs = Math.Min(w.UnequipTimeMs, 150u) }
                : w)],
            FireModes = [.. blob.FireModes.Select(mode =>
            {
                if (!replacements.TryGetValue(mode.FireModeId, out var live)) return mode;
                var words = mode.Overrides is null ? new Dictionary<short, uint>() : new(mode.Overrides);
                foreach (var (offset, value) in live) words[offset] = value;
                return mode with { Overrides = words };
            })],
            // Clone shared groups so adopting the current AR/AK cone cannot alter the shotgun.
            ConeOfFire = [.. (blob.ConeOfFire ?? []).Where(c => !Z1LiveGunplayFacts.Cones.Any(n => n.ConeOfFireId == c.ConeOfFireId)),
                .. Z1LiveGunplayFacts.Cones],
            AimAssist = [.. (blob.AimAssist ?? []).Where(a => !Z1LiveGunplayFacts.AimAssist.Any(n => n.AimAssistId == a.AimAssistId)),
                .. Z1LiveGunplayFacts.AimAssist],
        };
    }
}
