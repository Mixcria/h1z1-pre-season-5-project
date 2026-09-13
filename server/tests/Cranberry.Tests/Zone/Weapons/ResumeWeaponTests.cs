using Cranberry.Protocol;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

public sealed class ResumeWeaponTests
{
    [Theory]
    [InlineData(42u, 4)]
    [InlineData(43u, 4)]
    [InlineData(44u, 4)]
    [InlineData(45u, 4)]
    public void BinocularsEnterFirstPersonWithTheHudMaskWithoutAFireAbility(uint modeId, byte reticle)
    {
        var mode = new WeaponSession(WeaponStageOptions.Default).Blob.FireModes!.Single(m => m.FireModeId == modeId);
        Assert.Equal(0, mode.Type);
        Assert.Equal(0u, mode.MeleeAbilityId);
        Assert.Equal(modeId % 2 == 0, mode.ForceFpScope);
        Assert.Equal(modeId % 2 == 0 ? AugustWeaponTable.DefaultOpticFovDegrees : 0f, mode.FpCameraFovDegrees);
        using var writer = new PacketWriter();
        mode.WriteTo(writer);
        Assert.Equal(FireModeRecord.RecordLength, writer.Written.Length);
        int offset = 8 + WeaponListLayouts.FireModeBody.TakeWhile(f => f.RecordOffset != WeaponListLayouts.FireModeReticleId).Sum(f => f.Size);
        Assert.Equal(reticle, writer.Written[offset]);
    }

    [Theory]
    [InlineData(12u)]
    [InlineData(13u)]
    public void ArRecoilUsesTheSeparateZ1TuningInsteadOfTheExperimentalCapture(uint modeId)
    {
        var overrides = CapturedWeaponTable.OverridesFor(modeId)!;
        Assert.Equal(0.7f, BitConverter.UInt32BitsToSingle(overrides[WeaponListLayouts.FireModeRecoilFirstShotModifier]));
        Assert.Equal(1f, BitConverter.UInt32BitsToSingle(overrides[WeaponListLayouts.FireModeAnimKickMagnitude]));
        Assert.Equal(50f, BitConverter.UInt32BitsToSingle(overrides[WeaponListLayouts.FireModeAnimRecoilMagnitude]));
    }
}

