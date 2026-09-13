using Cranberry.Zone.Weapons;
using Xunit;
using Xunit.Abstractions;

namespace Cranberry.Tests.Zone.Weapons;

/// <summary>
/// Wave 17 (D291-D293): the FireModes <c>TYPE</c> column (<c>rec+0x24</c>) - the field the
/// client's fire executor dispatches on and the field its hotbar getters test before writing the
/// ammo readout. See docs/119's 2026-09-04 addendum for the derivation.
/// </summary>
public sealed class FireModeTypeTests(ITestOutputHelper output)
{
    private static IReadOnlyList<FireModeRecord> Records(
        bool types = true, bool binoculars = true) =>
        AugustWeaponTable.FireModeRecordsFor(
            AugustFireModeFacts.AdsZoom,
            adsFirstPerson: false,
            fpCameraFovDegrees: 0.0f,
            ironSightsArmedOnly: true,
            meleeAbilityIds: true,
            writeFireModeTypes: types,
            binocularsTriggerAbility: binoculars);

    private static int TypeOf(IReadOnlyList<FireModeRecord> records, uint group, int index)
    {
        uint id = AugustWeaponTable.FireModeIdFor(group, index);
        return records.Single(r => r.FireModeId == id).Type;
    }

    private static uint Group(uint itemDefinitionId)
    {
        Assert.True(AugustWeaponFacts.TryGet(itemDefinitionId, out AugustWeaponFact fact));
        Assert.NotEqual(0u, fact.FireGroupId);
        return fact.FireGroupId;
    }

    /// <summary>
    /// The fists (85), the crowbar (82), the machete (83), the knife (84), the wood axe (58) and
    /// the bat (1724) all carry <c>TYPE 3</c> on BOTH modes: the executor reaches the
    /// <c>MELEE_ATTACK</c> event only for 3, and the hotbar hides the readout only for 3.
    /// </summary>
    [Theory]
    [InlineData(85u)]
    [InlineData(82u)]
    [InlineData(83u)]
    [InlineData(84u)]
    [InlineData(58u)]
    [InlineData(1724u)]
    [InlineData(1536u)]
    [InlineData(5u)]
    public void TheStrikersCarryTheMeleeTypeOnBothModes(uint itemDefinitionId)
    {
        uint group = Group(itemDefinitionId);
        Assert.Contains(group, AugustWeaponTable.MeleeFireGroupIds);
        Assert.DoesNotContain(group, AugustWeaponTable.ArmedFireGroupIds);

        IReadOnlyList<FireModeRecord> records = Records();
        Assert.Equal(WeaponListLayouts.FireModeTypeMelee, TypeOf(records, group, 0));
        Assert.Equal(WeaponListLayouts.FireModeTypeMelee, TypeOf(records, group, 1));
        Assert.Equal(WeaponListLayouts.FireModeTypeMelee, AugustWeaponTable.FireModeTypeFor(group));
    }

    /// <summary>
    /// The five grenades (and the throwing hatchet 119 and the brick 1710, which share their
    /// <c>ITEM_CLASS 25078</c>) carry <c>TYPE 12</c>: the executor skips <c>OnFireBegin</c> and
    /// fires <c>OnThrowEnd</c> for 12 and nothing else.
    /// </summary>
    [Theory]
    [InlineData(14u)]
    [InlineData(2235u)]
    [InlineData(2236u)]
    [InlineData(2237u)]
    [InlineData(119u)]
    [InlineData(1710u)]
    public void TheThrowablesCarryTheThrowableType(uint itemDefinitionId)
    {
        uint group = Group(itemDefinitionId);
        Assert.Contains(group, AugustWeaponTable.ThrowableFireGroupIds);
        Assert.DoesNotContain(group, AugustWeaponTable.MeleeFireGroupIds);

        IReadOnlyList<FireModeRecord> records = Records();
        Assert.Equal(WeaponListLayouts.FireModeTypeThrowable, TypeOf(records, group, 0));
        Assert.Equal(WeaponListLayouts.FireModeTypeThrowable, TypeOf(records, group, 1));
    }

    /// <summary>
    /// The M67 Frag (65) has datasheet <c>FIRE_GROUP_ID 0</c> - the sheet names no group for it.
    /// docs/120 D300 gives it the ruled group 1404 (its own <c>WEAPON_ID</c>), which is a
    /// throwable's group by the same <c>ITEM_CLASS 25078</c> rule and therefore carries
    /// <c>TYPE 12</c> on both of its ruled modes.
    /// </summary>
    [Fact]
    public void TheFragGrenadeNamesItsRuledFireGroup()
    {
        Assert.True(AugustWeaponFacts.TryGet(65u, out AugustWeaponFact frag));
        Assert.Equal(0u, frag.FireGroupId);
        Assert.Equal(0, AugustWeaponTable.FireModeTypeFor(frag.FireGroupId));

        uint ruled = AugustThrowables.FireGroupFor(65u);
        Assert.Equal(Cranberry.Zone.Generated.Rulings.Throwables.FragFireGroupId, ruled);
        Assert.Contains(ruled, AugustWeaponTable.ThrowableFireGroupIds);
        Assert.Equal(WeaponListLayouts.FireModeTypeThrowable, AugustWeaponTable.FireModeTypeFor(ruled));

        IReadOnlyList<FireModeRecord> records = AugustWeaponTable.FireModeRecordsFor(
            AugustFireModeFacts.AdsZoom, adsFirstPerson: false, fpCameraFovDegrees: 0.0f,
            writeFireModeTypes: true, throwables: true, throwableWindupMs: 400);
        Assert.Equal(WeaponListLayouts.FireModeTypeThrowable, TypeOf(records, ruled, 0));
        Assert.Equal(WeaponListLayouts.FireModeTypeThrowable, TypeOf(records, ruled, 1));
    }

    /// <summary>
    /// The binoculars (1542, group 21; 1695, group 22) and the military flashlight (1380, group
    /// 17) carry the maintain ability 1111157 and no calibre, so they get <c>TYPE 8</c> - the
    /// trigger runs the item's own ability - on both modes, and never the melee type.
    /// </summary>
    [Theory]
    [InlineData(1380u)]
    public void TheHeldUpItemsCarryTheTriggerAbilityType(uint itemDefinitionId)
    {
        uint group = Group(itemDefinitionId);
        Assert.Contains(group, AugustWeaponTable.TriggerAbilityFireGroupIds);
        Assert.DoesNotContain(group, AugustWeaponTable.MeleeFireGroupIds);
        Assert.DoesNotContain(group, AugustWeaponTable.ThrowableFireGroupIds);

        IReadOnlyList<FireModeRecord> records = Records();
        Assert.Equal(WeaponListLayouts.FireModeTypeTriggerItemAbility, TypeOf(records, group, 0));
        Assert.Equal(WeaponListLayouts.FireModeTypeTriggerItemAbility, TypeOf(records, group, 1));

        // The revert leaves them at the constructor's 0 while the strikers keep 3.
        IReadOnlyList<FireModeRecord> without = Records(binoculars: false);
        Assert.Equal(0, TypeOf(without, group, 0));
        Assert.Equal(WeaponListLayouts.FireModeTypeMelee, TypeOf(without, Group(85u), 0));
    }

    /// <summary>
    /// Every ARMED group - the AR-15 (10), the AK-47 (2229), the shotgun (1374), the .308 (1373),
    /// the Makeshift Bow (113, armed by Cranberry's own arrow pairing) - keeps <c>TYPE 0</c>, the
    /// projectile shot it always carried. So does an unarmed GUN row (the unpaired M16 variant
    /// 1889 and the crossbow 200), which is not a striker.
    /// </summary>
    [Theory]
    [InlineData(10u)]
    [InlineData(2229u)]
    [InlineData(1374u)]
    [InlineData(1373u)]
    [InlineData(113u)]
    [InlineData(1889u)]
    [InlineData(200u)]
    public void TheGunsAndBowsKeepTheProjectileType(uint itemDefinitionId)
    {
        uint group = Group(itemDefinitionId);
        Assert.DoesNotContain(group, AugustWeaponTable.MeleeFireGroupIds);
        Assert.DoesNotContain(group, AugustWeaponTable.ThrowableFireGroupIds);
        Assert.DoesNotContain(group, AugustWeaponTable.TriggerAbilityFireGroupIds);

        IReadOnlyList<FireModeRecord> records = Records();
        Assert.Equal(0, TypeOf(records, group, 0));
        Assert.Equal(0, TypeOf(records, group, 1));
    }

    /// <summary>
    /// The three sets are disjoint, none of them contains an armed group, and together with the
    /// armed set they leave every other shipped group at 0. Printed so the doc can list them.
    /// </summary>
    [Fact]
    public void TheClassificationIsDisjointAndListed()
    {
        IReadOnlySet<uint> melee = AugustWeaponTable.MeleeFireGroupIds;
        IReadOnlySet<uint> thrown = AugustWeaponTable.ThrowableFireGroupIds;
        IReadOnlySet<uint> held = AugustWeaponTable.TriggerAbilityFireGroupIds;
        IReadOnlySet<uint> armed = AugustWeaponTable.ArmedFireGroupIds;

        Assert.Empty(melee.Intersect(thrown));
        Assert.Empty(melee.Intersect(held));
        Assert.Empty(thrown.Intersect(held));
        Assert.Empty(melee.Intersect(armed));
        Assert.Empty(thrown.Intersect(armed));
        Assert.Empty(held.Intersect(armed));

        output.WriteLine("melee:  " + string.Join(", ", melee.Order()));
        output.WriteLine("thrown: " + string.Join(", ", thrown.Order()));
        output.WriteLine("held:   " + string.Join(", ", held.Order()));
        output.WriteLine("armed:  " + string.Join(", ", armed.Order()));
        IEnumerable<uint> zero = AugustWeaponFacts.FireGroupIds
            .Where(g => g != 0 && !melee.Contains(g) && !thrown.Contains(g) && !held.Contains(g) && !armed.Contains(g))
            .Order();
        output.WriteLine("other (TYPE 0, unarmed): " + string.Join(", ", zero));

        foreach (uint group in AugustWeaponFacts.FireGroupIds.Where(g => g != 0))
        {
            int type = AugustWeaponTable.FireModeTypeFor(group);
            int expected = thrown.Contains(group) ? WeaponListLayouts.FireModeTypeThrowable
                : held.Contains(group) ? WeaponListLayouts.FireModeTypeTriggerItemAbility
                : melee.Contains(group) ? WeaponListLayouts.FireModeTypeMelee
                : 0;
            Assert.Equal(expected, type);
        }
    }

    /// <summary>
    /// The TYPE pass changes CONTENT, never LENGTH: the same 120 records, the same 639 bytes each,
    /// the same blob length; and with the switch off every record is what wave 16 shipped.
    /// </summary>
    [Fact]
    public void TheTypePassChangesContentButNotLength()
    {
        IReadOnlyList<FireModeRecord> before = Records(types: false);
        IReadOnlyList<FireModeRecord> after = Records();

        Assert.Equal(before.Count, after.Count);
        Assert.All(before, r => Assert.Equal(0, r.Type));
        Assert.Contains(after, r => r.Type != 0);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].FireModeId, after[i].FireModeId);
            Assert.Equal(before[i] with { Type = after[i].Type }, after[i]);
            Assert.Equal(FireModeRecord.RecordLength, after[i].Length);
        }

        byte[] shipped = new WeaponSession(WeaponStageOptions.Default).Blob.ToArray();
        byte[] without = new WeaponSession(WeaponStageOptions.Default with { WriteFireModeTypes = false })
            .Blob.ToArray();
        Assert.Equal(without.Length, shipped.Length);
        Assert.NotEqual(without, shipped);
    }

    /// <summary>
    /// The writer puts TYPE at <c>rec+0x24</c>: body offset 4, so byte 12 of the record after the
    /// two u32 keys. Nothing else in the record moves.
    /// </summary>
    [Fact]
    public void TheTypeLandsAtRecPlus0x24()
    {
        byte[] plain = Write(new FireModeRecord(4u));
        byte[] melee = Write(new FireModeRecord(4u, Type: WeaponListLayouts.FireModeTypeMelee));
        byte[] thrown = Write(new FireModeRecord(4u, Type: WeaponListLayouts.FireModeTypeThrowable));

        // The body is PACKED in FireModeBody order (no padding byte for the unused rec+0x23),
        // so the TYPE byte sits after the two u32 keys and the three flag bytes: index 11.
        int index = 8;
        foreach (WeaponListField field in WeaponListLayouts.FireModeBody)
        {
            if (field.RecordOffset == WeaponListLayouts.FireModeType)
            {
                break;
            }

            index += field.Size;
        }

        Assert.Equal(11, index);
        Assert.Equal(FireModeRecord.RecordLength, melee.Length);
        Assert.Equal(0, plain[index]);
        Assert.Equal(3, melee[index]);
        Assert.Equal(12, thrown[index]);
        for (int i = 0; i < plain.Length; i++)
        {
            if (i != index)
            {
                Assert.Equal(plain[i], melee[i]);
            }
        }

        static byte[] Write(FireModeRecord record)
        {
            using var writer = new Cranberry.Protocol.PacketWriter(FireModeRecord.RecordLength);
            record.WriteTo(writer);
            return writer.Written.ToArray();
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("garbage", true)]
    public void TheWave17SwitchesReadTheEnvironment(string? value, bool expected)
    {
        WeaponStageOptions types = WeaponStageOptions.FromEnvironment(
            name => name == WeaponStageOptions.FireModeTypesVariable ? value : null);
        WeaponStageOptions binoculars = WeaponStageOptions.FromEnvironment(
            name => name == WeaponStageOptions.BinocularsTriggerAbilityVariable ? value : null);

        Assert.Equal(expected, types.WriteFireModeTypes);
        Assert.Equal(expected, binoculars.BinocularsTriggerAbility);
        Assert.True(WeaponStageOptions.Default.WriteFireModeTypes);
        Assert.True(WeaponStageOptions.Default.BinocularsTriggerAbility);
        Assert.False(WeaponStageOptions.AllOff.WriteFireModeTypes);
        Assert.False(WeaponStageOptions.AllOff.BinocularsTriggerAbility);
        Assert.Contains("fireModeTypes=ON", WeaponStageOptions.Default.Describe());
        Assert.Contains("binocularsAbility=ON", WeaponStageOptions.Default.Describe());
        Assert.Contains(
            "binocularsAbility=off",
            (WeaponStageOptions.Default with { WriteFireModeTypes = false }).Describe());
    }
}
