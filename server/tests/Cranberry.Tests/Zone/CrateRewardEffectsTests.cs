using Cranberry.Zone;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Lighting;

namespace Cranberry.Tests.Zone;

public sealed class CrateRewardEffectsTests
{
    [Theory]
    [InlineData(0u, 5632u, "EFX_CrateOpening_Fanfare_Green")]
    [InlineData(5u, 5632u, "EFX_CrateOpening_Fanfare_Green")]
    [InlineData(6u, 5631u, "EFX_CrateOpening_Fanfare_Blue")]
    [InlineData(7u, 5634u, "EFX_CrateOpening_Fanfare_Purple")]
    [InlineData(8u, 5633u, "EFX_CrateOpening_Fanfare_Gold")]
    public void RarityUsesTheMatchingAugustCrateFanfare(uint rarity, uint effectId, string effectName)
    {
        uint actual = CrateRewardEffects.ForRarity(rarity);
        Assert.Equal(effectId, actual);
        var effect = AugustEffectCatalog.ById(actual)!.Value;
        Assert.Equal(effectName, effect.Name);
        Assert.Equal(2, effect.PartCount); // Colour particle plus the sound emitter.
        Assert.True(CompositeEffectGate.IsDefined(actual));
    }
}
