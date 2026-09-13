using Cranberry.Zone.Generated;

namespace Cranberry.Zone;

/// <summary>The August client's own crate reward fanfare, including its matching colour flash.</summary>
public static class CrateRewardEffects
{
    // ItemRarity.txt: 0/5 use client colour 5 (green), then 6 blue, 7 purple, 8 gold.
    // ActorCompositeEffectDefinitions.xml pairs these effects with SOUND 6961..6964;
    // ActorSoundEmitterDefinitions.xml resolves those to UI_KotK_Crate_Fanfare_01..04
    // in Main.bnk. Resolve composite names here, never send sound-event hashes as effect ids.
    public static uint ForRarity(uint rarityId) => AugustEffectCatalog.IdOf(rarityId switch
    {
        6 => "EFX_CrateOpening_Fanfare_Blue",
        7 => "EFX_CrateOpening_Fanfare_Purple",
        8 => "EFX_CrateOpening_Fanfare_Gold",
        _ => "EFX_CrateOpening_Fanfare_Green",
    });
}
