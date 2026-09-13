using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

public sealed class WeaponEquipmentTimingTests
{
    [Theory]
    [InlineData(WeaponTableSource.Generated, false)]
    [InlineData(WeaponTableSource.Captured, false)]
    [InlineData(WeaponTableSource.Generated, true)]
    [InlineData(WeaponTableSource.Captured, true)]
    public void OutgoingDefinitionsPreserveEquipmentTimersAfterEveryOverlay(
        WeaponTableSource source, bool gallery)
    {
        var session = new WeaponSession(WeaponStageOptions.Default with { WeaponTable = source, FastLongGunDraw = false },
            crateOpeningWeapon: gallery);
        var baseline = source == WeaponTableSource.Captured
            ? CapturedWeaponTable.Apply(session.GeneratedBlob) : session.GeneratedBlob;
        if (gallery) baseline = CrateOpeningWeapon.Apply(baseline, session.Options);
        baseline = AugustShotgunPattern.Apply(baseline);
        if (source == WeaponTableSource.Captured) baseline = Z1LiveGunplay.Apply(baseline, fastDraw: false);

        Assert.True(session.TryCreateWeaponDefinitions(out var packet));
        Assert.NotNull(baseline.WeaponDefinitions);
        Assert.NotEmpty(baseline.WeaponDefinitions);
        byte[] expected = baseline.ToArray();
        Assert.Equal(baseline.WeaponDefinitions.Count, BitConverter.ToInt32(packet.Payload, 0));
        int recordStart = sizeof(uint);
        foreach (WeaponDefinitionRecord definition in baseline.WeaponDefinitions)
        {
            Assert.Equal(definition.WeaponDefinitionId, BitConverter.ToUInt32(packet.Payload, recordStart));
            // August list-0 record: hash key, body ID, weapon group, u8 flags, then the two u32
            // durations. Native record offsets +0x28/+0x2c are wire offsets 13 and 17.
            Assert.Equal(definition.EquipTimeMs, BitConverter.ToUInt32(packet.Payload, recordStart + 13));
            Assert.Equal(definition.UnequipTimeMs, BitConverter.ToUInt32(packet.Payload, recordStart + 17));
            Assert.Equal((definition.EquipTimeMs, definition.UnequipTimeMs),
                session.EquipmentTiming(definition.WeaponDefinitionId));
            recordStart += definition.Length;
        }

        // Exact comparison also protects every later list, including all reload durations,
        // magazine/projectile definitions, fire modes, audio, aim transitions and animation sets.
        Assert.Equal(expected, packet.Payload);
        if (source == WeaponTableSource.Captured || gallery)
            Assert.Contains(baseline.WeaponDefinitions, definition => definition.EquipTimeMs > 0);
    }
}
