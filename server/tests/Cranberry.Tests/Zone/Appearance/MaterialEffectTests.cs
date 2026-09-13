using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;

namespace Cranberry.Tests.Zone.Appearance;

/// <summary>
/// D338 - the seven material-effect skins (research skins-and-colours.md §4-§6): the client's own
/// <c>AcctItemConversions.txt</c> binds them to a material effect id, and the attachment's
/// <c>effectId</c> (the u32 after <c>compositeEffectId</c>, before the slot) carries it.
/// </summary>
public sealed class MaterialEffectTests
{
    [Fact]
    public void TheSevenRewardsCarryTheClientsOwnMaterialEffectIds()
    {
        Assert.Equal(7, AugustMaterialEffects.ByRewardItemId.Count);
        Assert.Equal(16u, AugustMaterialEffects.EffectIdFor(3720));   // Infernal 12GA - HellfireGreen
        Assert.Equal(20u, AugustMaterialEffects.EffectIdFor(4073));   // Showdown 2017 AR-15
        Assert.Equal(21u, AugustMaterialEffects.EffectIdFor(4074));
        Assert.Equal(17u, AugustMaterialEffects.EffectIdFor(3513));
        Assert.Equal(19u, AugustMaterialEffects.EffectIdFor(4033));
        Assert.Equal(1102u, AugustMaterialEffects.EffectIdFor(4032));
        Assert.Equal(1071u, AugustMaterialEffects.EffectIdFor(4187));
        Assert.Equal(0u, AugustMaterialEffects.EffectIdFor(2600));    // Wildstyle AK-47 is a tint
        Assert.Equal(0u, AugustMaterialEffects.EffectIdFor(10));
    }

    [Fact]
    public void TheAttachmentWritesTheEffectIdAfterTheCompositeEffectAndBeforeTheSlot()
    {
        var attachment = new CharacterEquipmentAttachment(
            "Weapons_PumpShotgun01_3P.adr", 7, EffectId: 16, ShaderParameterGroupId: 1860,
            AppearanceIds: [1425u, 1426u]);
        using var writer = new PacketWriter(256);
        attachment.WriteTo(writer);
        byte[] bytes = writer.Written.ToArray();

        // strings: model, "Default", "Default", "#"; then tintId, compositeEffectId, effectId, slot, group
        int offset = 4 + "Weapons_PumpShotgun01_3P.adr".Length + 4 + 7 + 4 + 7 + 4 + 1;
        Assert.Equal(0u, BitConverter.ToUInt32(bytes, offset));        // tintId
        Assert.Equal(0u, BitConverter.ToUInt32(bytes, offset + 4));    // compositeEffectId
        Assert.Equal(16u, BitConverter.ToUInt32(bytes, offset + 8));   // effectId - D338
        Assert.Equal(7u, BitConverter.ToUInt32(bytes, offset + 12));   // slot
        Assert.Equal(1860u, BitConverter.ToUInt32(bytes, offset + 16)); // group
    }
}
