using Cranberry.Zone.Combat;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

public sealed class ResponsiveWeaponHandlingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShippedWireChangesOnlyThreeEquipDurationsAndServerUsesThoseSameClocks(bool gallery)
    {
        var baseline = new WeaponSession(WeaponStageOptions.Default with { FastLongGunDraw = false, Z1LiveGunplay = false }, gallery);
        var candidate = new WeaponSession(WeaponStageOptions.Default with { Z1LiveGunplay = false }, gallery);
        Assert.True(baseline.TryCreateWeaponDefinitions(out var before));
        Assert.True(candidate.TryCreateWeaponDefinitions(out var after));
        byte[] expected = before.Payload.ToArray();
        int offset = 4, changed = 0;
        foreach (var record in baseline.Blob.WeaponDefinitions!)
        {
            if (ResponsiveWeaponHandling.AppliesTo(record.WeaponDefinitionId))
            {
                Assert.Equal(550u, record.EquipTimeMs);
                Assert.Equal(150u, record.UnequipTimeMs);
                Assert.Equal(300u, record.SprintRecoveryTimeMs);
                BitConverter.GetBytes(150u).CopyTo(expected, offset + 13);
                Assert.Equal((150u, 150u), candidate.EquipmentTiming(record.WeaponDefinitionId));
                changed++;
            }
            offset += record.Length;
        }
        Assert.Equal(3, changed);
        // All other bytes, including fire rate, damage dependencies, reload, recoil,
        // animation groups, ammunition and the .308 draw duration, are untouched.
        Assert.Equal(expected, after.Payload);
        Assert.Equal((750u, 150u), candidate.EquipmentTiming(1373));
    }

    [Theory]
    [InlineData(6u)]
    [InlineData(1374u)]
    [InlineData(1405u)]
    public void SwitchingBetweenCommonGunsIsReadyAt300Milliseconds(uint weaponId)
    {
        var session = new WeaponSession(WeaponStageOptions.Default);
        var timing = session.EquipmentTiming(weaponId);
        var draw = new WeaponDrawState();
        draw.Select(1, 150, 150, 0);
        draw.Select(2, timing.Equip, timing.Unequip, 1000);
        Assert.False(draw.IsReady(1299));
        Assert.True(draw.IsReady(1300));
        Assert.False(draw.Select(2, timing.Equip, timing.Unequip, 1200));
        Assert.Equal(1300, draw.ReadyAtMs);
    }

    [Fact]
    public void RollbackSwitchAndGeneratedTableKeepThePreviousBytes()
    {
        Assert.False(WeaponStageOptions.FromEnvironment(name => name == WeaponStageOptions.FastLongGunDrawVariable ? "0" : null).FastLongGunDraw);
        Assert.True(WeaponStageOptions.FromEnvironment(_ => null).FastLongGunDraw);
        var original = new WeaponSession(WeaponStageOptions.Default with { FastLongGunDraw = false, WeaponTable = WeaponTableSource.Generated });
        var current = new WeaponSession(WeaponStageOptions.Default with { WeaponTable = WeaponTableSource.Generated });
        Assert.Equal(original.Blob.ToArray(), current.Blob.ToArray());
    }
}
