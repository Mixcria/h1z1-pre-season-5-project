using Cranberry.Protocol;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

public sealed class Z1LiveGunplayTests
{
    private static FireModeRecord Mode(WeaponDefinitionsBlob blob, uint weaponId, int index)
    {
        var weapon = Assert.Single(blob.WeaponDefinitions!, w => w.WeaponDefinitionId == weaponId);
        var group = Assert.Single(blob.FireGroups!, g => g.FireGroupId == weapon.FireGroupIds[0]);
        return Assert.Single(blob.FireModes!, m => m.FireModeId == group.FireModeIds![index]);
    }

    private static uint Word(FireModeRecord mode, short offset)
    {
        using var writer = new PacketWriter();
        mode.WriteTo(writer);
        int at = 8;
        foreach (var field in WeaponListLayouts.FireModeBody)
        {
            if (field.RecordOffset == offset)
                return field.Size switch { 1 => writer.Written[at],
                    2 => BitConverter.ToUInt16(writer.Written[at..]),
                    _ => BitConverter.ToUInt32(writer.Written[at..]) };
            at += field.Size;
        }
        throw new InvalidOperationException();
    }
    private static float Float(FireModeRecord mode, short offset) => BitConverter.UInt32BitsToSingle(Word(mode, offset));

    [Theory]
    [InlineData(6u, 0, 120u, 0.9f, 2.5f, 0.28f)]
    [InlineData(6u, 1, 125u, 0f, 0f, 0f)]
    [InlineData(1405u, 0, 145u, 0.9f, 2.5f, 0.6f)]
    [InlineData(1405u, 1, 145u, 0.7f, 1.1f, 0f)]
    public void FinalWireUsesRecordedHipAndAdsProfiles(uint weapon, int index, uint refire,
        float minimum, float maximum, float bloom)
    {
        var mode = Mode(new WeaponSession().Blob, weapon, index);
        Assert.Equal(refire, Word(mode, WeaponListLayouts.FireModeRefireTime));
        Assert.Equal(minimum, Float(mode, WeaponListLayouts.FireModeRecoilMagnitudeMin), 5);
        Assert.Equal(maximum, Float(mode, WeaponListLayouts.FireModeRecoilMagnitudeMax), 5);
        Assert.Equal(bloom, Float(mode, WeaponListLayouts.FireModeCofRecoil), 5);
        Assert.Equal(0x60u, Word(mode, 0x22));
    }

    [Fact]
    public void AllFirearmsHaveQuickDrawAndResolvableIndependentStateGroups()
    {
        var session = new WeaponSession();
        var blob = session.Blob;
        uint[] shipped = blob.WeaponDefinitions!.Where(w => Z1LiveGunplay.AppliesTo(w.WeaponDefinitionId))
            .Select(w => w.WeaponDefinitionId).ToArray();
        Assert.Contains(1373u, shipped);
        Assert.True(shipped.Length >= 7);
        foreach (uint id in shipped)
        {
            Assert.True(Z1LiveGunplay.AppliesTo(id));
            var timing = session.EquipmentTiming(id);
            Assert.InRange(timing.Equip, 1u, 150u);
            var draw = new WeaponDrawState();
            draw.Select(1, timing.Equip, timing.Unequip, 0);
            draw.Select(2, timing.Equip, timing.Unequip, 1000);
            Assert.True(draw.IsReady(1300));
            var mode = Mode(blob, id, 0);
            uint cone = Word(mode, WeaponListLayouts.FireModePlayerStateGroupId);
            if (cone != 0) Assert.Contains(blob.ConeOfFire!, c => c.ConeOfFireId == cone);
            uint assist = Word(mode, WeaponListLayouts.FireModeAimAssistConfig);
            if (assist != 0) Assert.Contains(blob.AimAssist!, a => a.AimAssistId == assist);
        }
    }

    [Fact]
    public void ShotgunDefinitionsModesAndEveryOriginalSharedGroupAreByteIdentical()
    {
        var previous = new WeaponSession(WeaponStageOptions.Default with { Z1LiveGunplay = false }).Blob;
        var current = new WeaponSession().Blob;
        foreach (uint id in new uint[] { 1374, 1433 })
        {
            Assert.False(Z1LiveGunplay.AppliesTo(id));
            if (!previous.WeaponDefinitions!.Any(w => w.WeaponDefinitionId == id)) continue;
            Assert.Equal(previous.WeaponDefinitions!.Single(w => w.WeaponDefinitionId == id),
                current.WeaponDefinitions!.Single(w => w.WeaponDefinitionId == id));
            for (int i = 0; i < 2; i++)
            {
                using var a = new PacketWriter(); using var b = new PacketWriter();
                Mode(previous, id, i).WriteTo(a); Mode(current, id, i).WriteTo(b);
                Assert.Equal(a.Written.ToArray(), b.Written.ToArray());
            }
        }
        foreach (var cone in previous.ConeOfFire!)
        {
            using var a = new PacketWriter(); using var b = new PacketWriter();
            cone.WriteTo(a); current.ConeOfFire!.Single(c => c.ConeOfFireId == cone.ConeOfFireId).WriteTo(b);
            Assert.Equal(a.Written.ToArray(), b.Written.ToArray());
        }
        Assert.Equal(previous.FireModeProjectiles, current.FireModeProjectiles);
        Assert.Equal(current.ToArray(), Z1LiveGunplay.Apply(current, true).ToArray());
    }

    [Fact]
    public void ProfileHasAnIndependentRollbackAndLeavesGeneratedTablesAlone()
    {
        Assert.False(WeaponStageOptions.FromEnvironment(n => n == WeaponStageOptions.Z1LiveGunplayVariable ? "0" : null).Z1LiveGunplay);
        var options = WeaponStageOptions.Default with { WeaponTable = WeaponTableSource.Generated };
        Assert.Equal(new WeaponSession(options with { Z1LiveGunplay = false }).Blob.ToArray(),
            new WeaponSession(options with { Z1LiveGunplay = true }).Blob.ToArray());
    }
}
