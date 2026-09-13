using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

public sealed class ShotgunClientConfigurationTests
{
    [Theory]
    [InlineData(0, 2.35f)]
    [InlineData(1, 1.35f)]
    public void ZoneConfigurationResolvesTheSystematicSeventeenPelletPattern(int modeIndex, float spread)
    {
        // FUN_140c7bb60 reads this hash before either the pellet count or spread.
        // Enabled with a missing pattern group, it retains the one-pellet default.
        // Read the actual zone packet's replacement map, not just a separate option.
        using var bare = new PacketWriter();
        new SendZoneDetails("Z2").WriteTo(bare);
        using var zone = new PacketWriter();
        new SendZoneDetails("Z2", Values: StringHashValues.Entries).WriteTo(zone);
        byte[] bytes = zone.Written.ToArray();
        int cursor = bare.Position - 4;
        int count = BitConverter.ToInt32(bytes, cursor);
        cursor += 4;
        string? patternFlag = null;
        for (int i = 0; i < count; i++)
        {
            uint hash = BitConverter.ToUInt32(bytes, cursor);
            cursor += 4;
            string value = ReadString(bytes, ref cursor);
            cursor++; // Flag.
            string name = ReadString(bytes, ref cursor);
            if (hash == 0xC8F2471Fu)
            {
                Assert.Equal("PelletPatternsEnabled", name);
                Assert.Null(patternFlag);
                patternFlag = value;
            }
        }
        Assert.Equal(bytes.Length, cursor);
        Assert.Equal("1", patternFlag);

        WeaponDefinitionsBlob table = new WeaponSession(WeaponStageOptions.Default).Blob;
        WeaponDefinitionRecord pump = Assert.Single(table.WeaponDefinitions!, x => x.WeaponDefinitionId == 1374);
        uint groupId = Assert.Single(pump.FireGroupIds);
        FireGroupRecord group = Assert.Single(table.FireGroups!, x => x.FireGroupId == groupId);
        FireModeRecord mode = Assert.Single(table.FireModes!, x => x.FireModeId == group.FireModeIds![modeIndex]);
        Assert.Equal(17u, ReadModeWord(mode, WeaponListLayouts.FireModePelletsPerShot));
        Assert.Equal(spread, BitConverter.UInt32BitsToSingle(ReadModeWord(mode, WeaponListLayouts.FireModePelletSpread)), 4);
        uint groupKey = ReadModeWord(mode, WeaponListLayouts.FireModePelletPatternGroupId);
        var patternGroup = Assert.Single(table.List7!, x => x.Id == groupKey);
        uint patternKey = Assert.Single(patternGroup.Values!);
        var pattern = Assert.Single(table.List6!, x => x.Id == patternKey);
        Assert.Equal(17, pattern.Elements!.Count);
        Assert.Equal(17, pattern.Elements.Select(p => p.Key).Distinct().Count());
        Assert.Single(pattern.Elements, p => p.Word2 == 0);
        Assert.Equal(8, pattern.Elements.Count(p => BitConverter.UInt32BitsToSingle(p.Word2) == 0.5f));
        Assert.Equal(8, pattern.Elements.Count(p => BitConverter.UInt32BitsToSingle(p.Word2) == 1f));

        // Exercise the native wire's angle/radius fields, not only the in-memory row count.
        using var encoded = new PacketWriter();
        pattern.WriteTo(encoded);
        var data = encoded.Written.ToArray();
        Assert.Equal(17, BitConverter.ToInt32(data, 8));
        double x = 0, y = 0;
        for (int index = 0; index < 17; index++)
        {
            int start = 12 + index * 16;
            float angle = BitConverter.ToSingle(data, start + 8) * MathF.PI / 180;
            float radius = BitConverter.ToSingle(data, start + 12);
            x += Math.Cos(angle) * radius;
            y += Math.Sin(angle) * radius;
        }
        Assert.InRange(Math.Abs(x), 0, 0.00001);
        Assert.InRange(Math.Abs(y), 0, 0.00001);
    }

    [Fact]
    public void CompletePatternOverlayIsIdempotentAndDoesNotChangeOtherGuns()
    {
        var before = CapturedWeaponTable.Apply(new WeaponSession(WeaponStageOptions.Default).GeneratedBlob);
        var after = AugustShotgunPattern.Apply(before);
        var repeated = AugustShotgunPattern.Apply(after);
        Assert.Single(repeated.List6!);
        Assert.Single(repeated.List7!);
        foreach (var mode in before.FireModes!.Where(m => m.FireModeId / 2 != 16))
        {
            using var oldBytes = new PacketWriter();
            using var newBytes = new PacketWriter();
            mode.WriteTo(oldBytes);
            Assert.Single(after.FireModes!, m => m.FireModeId == mode.FireModeId).WriteTo(newBytes);
            Assert.Equal(oldBytes.Written.ToArray(), newBytes.Written.ToArray());
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(3)] [InlineData(5)] [InlineData(10)] [InlineData(20)] [InlineData(30)]
    public void PelletDamageUsesSpreadForRangeAndPreservesTheFullHitBudget(double distance)
    {
        int damage = Cranberry.Zone.Combat.RetailBalance.BodyDamageUnits(1374, distance, 1);
        Assert.Equal(706, damage);
        Assert.InRange(damage * 17, 12000, 12002);
    }

    private static string ReadString(byte[] bytes, ref int cursor)
    {
        int length = BitConverter.ToInt32(bytes, cursor);
        cursor += 4;
        string value = System.Text.Encoding.UTF8.GetString(bytes, cursor, length);
        cursor += length;
        return value;
    }

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
