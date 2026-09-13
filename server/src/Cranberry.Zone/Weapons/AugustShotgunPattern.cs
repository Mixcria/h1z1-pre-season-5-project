namespace Cranberry.Zone.Weapons;

/// <summary>
/// May–early August's systematic 17-pellet branch. The client wire layout is derived;
/// this symmetric center/two-ring geometry is a reconstruction, not recovered retail rows.
/// The native client rotates the whole pattern per shot while preserving its distribution.
/// See docs/video-fixes-20260907.md for evidence and balance limits.
/// </summary>
public static class AugustShotgunPattern
{
    public const uint WeaponId = 1374;
    public const uint PatternId = 1374;
    // FireMode.PELLET_PATTERN_GROUP_ID is an 8-bit wire field in this client.
    public const uint GroupId = 1;
    public const int PelletCount = 17;

    // Retain the previous 8 × 15 HP full-hit budget while correcting pellet distribution.
    // 706 internal health units/pellet gives 120.02 HP total after wire-unit rounding.
    public const double PelletDamageHp = 120.0 / PelletCount;

    public static WeaponBlobList6Record Pattern { get; } = CreatePattern();
    public static WeaponBlobList7Record Group { get; } = new(GroupId, Values: [PatternId]);

    private static WeaponBlobList6Record CreatePattern()
    {
        var pellets = new List<WeaponBlobList6Element>(PelletCount) { Pellet(0, 0, 0) };
        for (int index = 0; index < 8; index++)
            pellets.Add(Pellet((uint)pellets.Count, index * 45, 0.5f));
        for (int index = 0; index < 8; index++)
            pellets.Add(Pellet((uint)pellets.Count, index * 45 + 22.5f, 1f));
        return new(PatternId, Elements: pellets.AsReadOnly());
    }

    private static WeaponBlobList6Element Pellet(uint index, float angleDegrees, float radius) =>
        // 140a3f970 -> +0x20 angle degrees, +0x24 spread multiplier;
        // 140c7c75b/761 convert angle to radians before the shared random rotation.
        new(index, Word1: BitConverter.SingleToUInt32Bits(angleDegrees),
            Word2: BitConverter.SingleToUInt32Bits(radius));

    /// <summary>Apply after captured overlays so their earlier eight-pellet rows cannot win.</summary>
    public static WeaponDefinitionsBlob Apply(WeaponDefinitionsBlob blob)
    {
        var groups = (blob.WeaponDefinitions ?? [])
            .Where(weapon => weapon.WeaponDefinitionId == WeaponId)
            .SelectMany(weapon => weapon.FireGroupIds ?? []).ToHashSet();
        var modes = (blob.FireGroups ?? []).Where(group => groups.Contains(group.FireGroupId))
            .SelectMany(group => group.FireModeIds ?? []).ToHashSet();
        if (modes.Count == 0 || blob.FireModes is null) return blob;
        return blob with
        {
            FireModes = [.. blob.FireModes.Select(mode => modes.Contains(mode.FireModeId) ? Apply(mode) : mode)],
            List6 = [.. (blob.List6 ?? []).Where(pattern => pattern.Id != PatternId), Pattern],
            List7 = [.. (blob.List7 ?? []).Where(group => group.Id != GroupId), Group],
        };
    }

    private static FireModeRecord Apply(FireModeRecord mode)
    {
        var words = mode.Overrides?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? [];
        words[WeaponListLayouts.FireModePelletsPerShot] = PelletCount;
        words[WeaponListLayouts.FireModePelletPatternGroupId] = GroupId;
        return mode with { PelletsPerShot = PelletCount, Overrides = words };
    }
}
