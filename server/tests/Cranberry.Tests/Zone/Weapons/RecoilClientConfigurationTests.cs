using Cranberry.Protocol;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

public sealed class RecoilClientConfigurationTests
{
    [Fact]
    public void EveryCapturedArmedModeRetainsTheNativeRecoilGates()
    {
        WeaponDefinitionsBlob table = new WeaponSession(WeaponStageOptions.Default with { Z1LiveGunplay = false }).Blob;
        var sourceModes = CapturedWeaponFacts.FireModes.ToDictionary(x => x.FireModeId);
        int checkedModes = 0;
        foreach (WeaponDefinitionRecord weapon in table.WeaponDefinitions!
            .Where(x => x.AmmoSlots is { Count: > 0 }))
        foreach (uint groupId in weapon.FireGroupIds)
        {
            if (!CapturedWeaponTable.CapturedModesByAugustFireGroup.TryGetValue(groupId, out uint[]? sourceIds))
                continue;
            FireGroupRecord group = Assert.Single(table.FireGroups!, x => x.FireGroupId == groupId);
            for (int index = 0; index < Math.Min(group.FireModeIds!.Count, sourceIds.Length); index++)
            {
                FireModeRecord mode = Assert.Single(table.FireModes!, x => x.FireModeId == group.FireModeIds[index]);
                CapturedFireModeRow source = sourceModes[sourceIds[index]];
                Assert.Equal((uint)(source.Flags2 & 0x60), ReadModeWord(mode, 0x022) & 0x60);
                checkedModes++;
            }
        }
        Assert.True(checkedModes >= 20);
    }

    [Theory]
    [InlineData(6u, 0, 2.4f, 0.85f)]
    [InlineData(1405u, 0, 2.0f, 0.85f)]
    [InlineData(1405u, 1, 0.65f, 0.8f)]
    [InlineData(1374u, 1, 1.3f, 0.1f)]
    public void RecoilMagnitudesAreReachableThroughBothNativeGates(
        uint weaponId, int modeIndex, float verticalMinimum, float horizontalMinimum)
    {
        WeaponDefinitionsBlob table = new WeaponSession(WeaponStageOptions.Default with { Z1LiveGunplay = false }).Blob;
        WeaponDefinitionRecord weapon = Assert.Single(table.WeaponDefinitions!, x => x.WeaponDefinitionId == weaponId);
        FireGroupRecord group = Assert.Single(table.FireGroups!, x => x.FireGroupId == weapon.FireGroupIds[0]);
        FireModeRecord mode = Assert.Single(table.FireModes!, x => x.FireModeId == group.FireModeIds![modeIndex]);

        // FUN_141485bf0 returns without invoking camera recoil when mode+0x22 bit
        // 0x40 is clear. Its horizontal block additionally needs bit 0x20. Numeric
        // recoil assertions alone passed previously while both branches were dead.
        uint flags = ReadModeWord(mode, 0x022);
        Assert.Equal(0x40u, flags & 0x40);
        Assert.Equal(0x20u, flags & 0x20);
        Assert.Equal(verticalMinimum, ReadFloat(mode, 0x0a8), 4);
        Assert.Equal(horizontalMinimum, ReadFloat(mode, 0x0a0), 4);
        Assert.Equal(0.7f, ReadFloat(mode, 0x0cc), 4);
        Assert.True(ReadFloat(mode, 0x0a4) >= ReadFloat(mode, 0x0a0));
    }

    private static float ReadFloat(FireModeRecord mode, short offset) =>
        BitConverter.UInt32BitsToSingle(ReadModeWord(mode, offset));

    private static uint ReadModeWord(FireModeRecord mode, short offset)
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
