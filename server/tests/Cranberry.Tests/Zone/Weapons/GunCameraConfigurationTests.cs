using Cranberry.Protocol;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

public sealed class GunCameraConfigurationTests
{
    [Theory]
    [InlineData(2u, 85, 85)]
    [InlineData(6u, 266, 266)]
    [InlineData(1373u, 250, 75)]
    [InlineData(1374u, 266, 266)]
    [InlineData(1405u, 266, 266)]
    public void AimTransitionTimesMatchEachWeaponsAnimation(uint weaponId, int aimInMs, int aimOutMs)
    {
        WeaponDefinitionRecord weapon = Assert.Single(new WeaponSession(WeaponStageOptions.Default).Blob.WeaponDefinitions!,
            x => x.WeaponDefinitionId == weaponId);
        using var writer = new PacketWriter();
        weapon.WriteTo(writer);
        byte[] bytes = writer.Written.ToArray();
        // Key + own ID + group + flags byte + five clocks precede the aim clocks.
        Assert.Equal(aimInMs, BitConverter.ToInt32(bytes, 33)); // def+0x38
        Assert.Equal(aimOutMs, BitConverter.ToInt32(bytes, 37)); // def+0x3c
        Assert.Equal((uint)aimInMs, weapon.AimInAnimationTimeMs);
        Assert.Equal((uint)aimOutMs, weapon.AimOutAnimationTimeMs);
    }

    [Theory]
    [InlineData(6u, 1.2f)]
    [InlineData(1405u, 1.2f)]
    [InlineData(1374u, 1.001f)]
    public void AimingMovesTheThirdPersonCameraInEveryStance(uint weaponId, float zoom)
    {
        FireModeRecord[] modes = ModesFor(weaponId);
        Assert.Equal(1u, Word(modes[0], 0x19c)); // TP_FORCE_CAMERA_OVERRIDES
        Assert.Equal(1u, Word(modes[1], 0x19c));
        Assert.Equal(1.67f, Float(modes[0], "TP_CAMERA_DISTANCE"), 4);
        Assert.Equal(1.15f, Float(modes[1], "TP_CAMERA_DISTANCE"), 4);
        Assert.Equal(1.67f, Float(modes[0], "TP_CR_CAMERA_DISTANCE"), 4);
        Assert.Equal(1.15f, Float(modes[1], "TP_CR_CAMERA_DISTANCE"), 4);
        Assert.Equal(0.42f, Float(modes[1], "TP_PR_CAMERA_DISTANCE"), 4);
        Assert.Equal(-0.1f, Float(modes[1], "TP_CAMERA_POSITION_OFFSET_X"), 4);
        Assert.Equal(1.85f, Float(modes[1], "TP_CAMERA_POSITION_OFFSET_Y"), 4);
        Assert.Equal(1.4f, Float(modes[1], "TP_CR_CAMERA_POSITION_OFFSET_Y"), 4);
        Assert.Equal(0.55f, Float(modes[1], "TP_PR_CAMERA_POSITION_OFFSET_Y"), 4);
        Assert.Equal(zoom, Float(modes[1], "DEFAULT_ZOOM"), 4);
        Assert.Equal(0u, Word(modes[0], 0x2dc));
    }

    [Fact]
    public void RifleAndShotgunRetainTheirDistinctFirstPersonArmsPresentation()
    {
        FireModeRecord[] ar = ModesFor(6);
        FireModeRecord[] pump = ModesFor(1374);
        Assert.Equal(-0.01f, Float(ar[0], "FIRST_PERSON_OFFSET_Y"), 4);
        Assert.Equal(0.15f, Float(ar[0], "FIRST_PERSON_OFFSET_Z"), 4);
        Assert.Equal(0.11f, Float(ar[1], "FIRST_PERSON_OFFSET_Z"), 4);
        Assert.Equal(1.5f, Float(ar[1], "ARMS_FOV_SCALAR"), 4);
        Assert.Equal(0.8f, Float(pump[0], "ARMS_FOV_SCALAR"), 4);
        Assert.Equal(1f, Float(pump[1], "ARMS_FOV_SCALAR"), 4);
    }

    [Theory]
    [InlineData(6u, 5u, 10u)]
    [InlineData(1405u, 5u, 10u)]
    [InlineData(1374u, 3u, 10u)]
    [InlineData(1373u, 5u, 2u)]
    public void HipAndAimedReticlesResolveToTheAugustHudViews(uint weaponId, uint hip, uint aimed)
    {
        // Shipped HudReticleWindow.gfx Reticles.createReticleById: 2 sniper, 3 shotgun,
        // 5 crosshair, 10 empty. Reticles.txt is an unrelated legacy naming table.
        FireModeRecord[] modes = ModesFor(weaponId);
        Assert.Equal(hip, Word(modes[0], 0x138));
        Assert.Equal(aimed, Word(modes[1], 0x138));
    }

    [Fact]
    public void HuntingRifleCanEnterItsScopeFromTheLivePrimaryMode()
    {
        FireModeRecord[] modes = ModesFor(1373);
        // FUN_14158ad50 tests FORCE_FP_SCOPE on the live primary before changing modes.
        Assert.Equal(1u, Word(modes[0], 0x2dc));
        Assert.Equal(1u, Word(modes[1], 0x2dc));
        Assert.Equal(1f, Float(modes[0], "DEFAULT_ZOOM"), 4);
        Assert.Equal(3.4f, Float(modes[1], "DEFAULT_ZOOM"), 4);
    }

    [Fact]
    public void CrossingGunCameraFieldsPreservesTheConfirmedBinocularScope()
    {
        var session = new WeaponSession(WeaponStageOptions.Default);
        foreach (uint id in new uint[] { 42, 43 })
        {
            FireModeRecord before = Assert.Single(session.GeneratedBlob.FireModes!, x => x.FireModeId == id);
            FireModeRecord after = Assert.Single(session.Blob.FireModes!, x => x.FireModeId == id);
            foreach (short offset in CapturedWeaponFacts.GunPresentationOffsets)
                Assert.Equal(Word(before, offset), Word(after, offset));
        }
    }

    private static FireModeRecord[] ModesFor(uint weaponId)
    {
        WeaponDefinitionsBlob table = new WeaponSession(WeaponStageOptions.Default).Blob;
        WeaponDefinitionRecord weapon = Assert.Single(table.WeaponDefinitions!, x => x.WeaponDefinitionId == weaponId);
        FireGroupRecord group = Assert.Single(table.FireGroups!, x => x.FireGroupId == weapon.FireGroupIds[0]);
        return [.. group.FireModeIds!.Select(id => Assert.Single(table.FireModes!, x => x.FireModeId == id))];
    }

    private static float Float(FireModeRecord mode, string column) =>
        BitConverter.UInt32BitsToSingle(Word(mode,
            Assert.Single(WeaponListLayouts.FireModeColumns, x => x.Name == column).RecordOffset));

    private static uint Word(FireModeRecord mode, short offset)
    {
        using var writer = new PacketWriter();
        mode.WriteTo(writer);
        byte[] bytes = writer.Written.ToArray();
        int cursor = 8;
        foreach (WeaponListField field in WeaponListLayouts.FireModeBody)
        {
            if (field.RecordOffset == offset)
                return field.Size switch
                {
                    1 => bytes[cursor],
                    2 => BitConverter.ToUInt16(bytes, cursor),
                    _ => BitConverter.ToUInt32(bytes, cursor),
                };
            cursor += field.Size;
        }
        throw new InvalidOperationException($"Unknown mode offset {offset:x}.");
    }
}
