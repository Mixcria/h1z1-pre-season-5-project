using Cranberry.Protocol;
using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone.Combat;

public sealed class MedicalEffectPacketTests
{
    [Fact]
    public void NativeTagMatchesNestedAugustLayoutIncludingAbsentAbilityStage()
    {
        using var writer = new PacketWriter();
        new MedicalEffectTag(0x0102030405060708, 0x1112131415161718, 0x20212223, 0x30313233).WriteTo(writer);
        // Independent fixed vector from 140a38b00 and its nested decoder offsets.
        Assert.Equal(Convert.FromHexString(
            "9E0608070605040302011817161514131211000000002322212000000000FFFFFFFF333231300000000000000000080706050403020100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"),
            writer.Written.ToArray());
        Assert.Equal(MedicalEffectTag.WireLength, writer.Position);
    }

    [Fact]
    public void ResourceModifierMatchesTheAugustDecoderFieldOrderWithoutPadding()
    {
        using var writer = new PacketWriter();
        new MedicalResourceEffect(0x0102030405060708, 0x1112131415161718, 1,
            0.125f, 0.25f, 0x2122232425262728, 10_000).WriteTo(writer);
        // Layout independently transcribed from 140cef9a0 and 140a38070.
        Assert.Equal(Convert.FromHexString(
            "9E0708070605040302011817161514131211010000000000003E0000803E282726252423222110270000"),
            writer.Written.ToArray());
        Assert.Equal(MedicalResourceEffect.WireLength, writer.Position);
    }

    [Fact]
    public void RemoveNamesTheSubjectAndFullWidthEffectInstance()
    {
        using var writer = new PacketWriter();
        new RemoveMedicalEffect(0x0102030405060708, 0x1112131415161718).WriteTo(writer);
        Assert.Equal(Convert.FromHexString("9E0808070605040302011817161514131211"), writer.Written.ToArray());
        Assert.Equal(RemoveMedicalEffect.WireLength, writer.Position);
    }
}
