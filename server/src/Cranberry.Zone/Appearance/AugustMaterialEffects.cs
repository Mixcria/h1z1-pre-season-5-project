namespace Cranberry.Zone.Appearance;

/// <summary>
/// <b>D338 - the material-effect skins.</b> Seven of the August client's account rewards are not
/// a tint at all: their look is a material effect the client composites over the mesh (a
/// multiscroll fire, a frost, a shimmer), keyed by <c>REWARD_ITEM_MATERIAL_EFFECT_ID</c> in the
/// client's own <c>AcctItemConversions.txt</c> (column 7; 691 rows, 7 non-zero) and defined in
/// <c>ActorModelMaterialDefinitions.xml</c> (16 = <c>WeaponSkin_Fire_HellfireGreen</c>,
/// <c>Tile_Fire_Green01.dds</c>, every texture shipped). The reference server carries that id in
/// the attachment's <c>effectId</c> (the u32 the client reads at <c>+0x188</c>,
/// <c>FUN_140cd5ee0</c> tracks it per equipped item); Cranberry wrote 0 there on every
/// attachment, which is why the Infernal pump (3720) rendered black on 2026-09-04 and the
/// Showdown 2017 AR-15 (4073) showed only its static crown under-tint and "wasn't animated".
/// Source: <c>out\research-20260904\skins-and-colours.md</c> §4-§6.
/// </summary>
public static class AugustMaterialEffects
{
    /// <summary>
    /// Reward item id -> material effect id, the seven non-zero rows of
    /// <c>AcctItemConversions.txt</c> (<c>REWARD_ITEM_ID</c>, <c>REWARD_ITEM_MATERIAL_EFFECT_ID</c>).
    /// </summary>
    public static IReadOnlyDictionary<uint, uint> ByRewardItemId { get; } =
        new Dictionary<uint, uint>
        {
            [3720] = 16,    // Infernal 12GA Pump Shotgun  - WeaponSkin_Fire_HellfireGreen
            [3513] = 17,    // Frostbite
            [4032] = 1102,  // Nautilus
            [4033] = 19,    // Volcanic
            [4073] = 20,    // Showdown 2017 AR-15
            [4074] = 21,    // Showdown 2017 (second reward)
            [4187] = 1071,  // Nemesis
        };

    /// <summary>The material effect a reward item composites, or 0 for a plain tint skin.</summary>
    public static uint EffectIdFor(uint rewardItemId) =>
        ByRewardItemId.TryGetValue(rewardItemId, out uint effectId) ? effectId : 0;
}
