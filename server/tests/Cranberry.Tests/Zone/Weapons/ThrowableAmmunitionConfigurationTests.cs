using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

/// <summary>
/// The 16:49-16:50 September 6 smoke/gas retest emitted 19 windups and no Fire packet.
/// August FUN_1422934f0 independently checks AMMO_PER_SHOT at execution, even with
/// CHECK_ENTER_FIRE_STATE clear; FUN_14228e080 returns zero when no ammo slot exists.
/// A stack throwable therefore cannot inherit a one-round gun prerequisite.
/// </summary>
public sealed class ThrowableAmmunitionConfigurationTests
{
    // Absolute 1148 FireMode wire offsets including both ids. RANGE is 30..33,
    // then AMMO_PER_SHOT (rec+0x5c) is byte 34, not byte 35 (RELOAD_TIME_MS).
    private const int AmmoPerShotAt = 34;
    private const int Flags1At = 9;
    private const ulong SelfGuid = 0x3100_0000_0000_0001;
    private const ulong ItemGuid = 0x3100_0000_0000_0004;
    private static readonly uint[] ThrowableItems = [65, 2235, 2236, 2237, 14];

    [Theory]
    [InlineData(WeaponTableSource.Generated)]
    [InlineData(WeaponTableSource.Captured)]
    public void AllFiveStackThrowablesCanExecuteWithTheirActualMagazineLessDefinitionAndTail(
        WeaponTableSource source)
    {
        WeaponSession session = CreateSession(source, throwables: true);
        WeaponDefinitionsBlob blob = session.Blob;

        foreach (uint itemId in ThrowableItems)
        {
            Assert.True(AugustWeaponFacts.TryGet(itemId, out AugustWeaponFact item));
            Assert.True(AugustThrowables.TryGet(itemId, out ThrowableFact throwable));
            WeaponDefinitionRecord weapon = Assert.Single(blob.WeaponDefinitions!,
                record => record.WeaponDefinitionId == item.WeaponId);
            Assert.Equal(0, ReadDefinitionAmmoSlots(weapon));
            Assert.Equal(0, ReadItemAddAmmoSlots(session, itemId));

            FireGroupRecord group = Assert.Single(blob.FireGroups!,
                record => record.FireGroupId == throwable.FireGroupId);
            Assert.Equal(2, group.FireModeIds!.Count);
            foreach (uint modeId in group.FireModeIds)
            {
                FireModeRecord mode = Assert.Single(blob.FireModes!, record => record.FireModeId == modeId);
                byte[] bytes = Serialize(mode.WriteTo);
                Assert.Equal(639, bytes.Length);
                Assert.Equal(12, bytes[11]);
                Assert.Equal(0, bytes[AmmoPerShotAt]);
            }
        }
    }

    [Theory]
    [InlineData(2236u, 125u, 300u)]
    [InlineData(2237u, 63u, 299u)]
    public void SmokeAndGasDoNotKeepTheCapturedExecutionAmmoCheckWhenTheEntryGuardIsClear(
        uint itemId, uint primaryCapturedMode, uint secondaryCapturedMode)
    {
        WeaponSession session = CreateSession(WeaponTableSource.Captured, throwables: true);
        Assert.True(AugustThrowables.TryGet(itemId, out ThrowableFact throwable));
        Assert.Equal(new[] { primaryCapturedMode, secondaryCapturedMode },
            CapturedWeaponTable.CapturedModesByAugustFireGroup[throwable.FireGroupId]);

        // Pin the actual failing combination, rather than assuming clearing the entry flag
        // bypasses the later execution check. The source row still legitimately describes a
        // one-round captured weapon; this server gives the August stack no magazine at all.
        Assert.Equal(0, ReadItemAddAmmoSlots(session, itemId));
        for (int index = 0; index < 2; index++)
        {
            uint modeId = AugustWeaponTable.FireModeIdFor(throwable.FireGroupId, index);
            IReadOnlyDictionary<short, uint>? captured = CapturedWeaponTable.OverridesFor(modeId);
            Assert.NotNull(captured);
            Assert.Equal(1u, captured[0x05c]);
            Assert.Equal(0u, captured[0x021] & 0x04);

            FireModeRecord actual = Assert.Single(session.Blob.FireModes!, mode => mode.FireModeId == modeId);
            byte[] wire = Serialize(actual.WriteTo);
            Assert.Equal(0, wire[Flags1At] & 0x04);
            Assert.Equal(0, wire[AmmoPerShotAt]);
        }
    }

    [Theory]
    [InlineData(14u)]
    [InlineData(2235u)]
    [InlineData(2236u)]
    [InlineData(2237u)]
    public void TheDisabledThrowableOverlayRetainsTheCapturedAmmoRequirement(uint itemId)
    {
        WeaponDefinitionsBlob blob = CreateSession(WeaponTableSource.Captured, throwables: false).Blob;
        Assert.True(AugustThrowables.TryGet(itemId, out ThrowableFact throwable));
        for (int index = 0; index < 2; index++)
        {
            uint modeId = AugustWeaponTable.FireModeIdFor(throwable.FireGroupId, index);
            FireModeRecord mode = Assert.Single(blob.FireModes!, record => record.FireModeId == modeId);
            Assert.Equal(1, Serialize(mode.WriteTo)[AmmoPerShotAt]);
        }
    }

    [Theory]
    [InlineData(2425u)] // AR-15
    [InlineData(2229u)] // AK-47
    [InlineData(1374u)] // 12GA
    public void CapturedFirearmsStillRequireOneRoundAndKeepTheirMagazine(uint itemId)
    {
        WeaponSession session = CreateSession(WeaponTableSource.Captured, throwables: true);
        WeaponDefinitionsBlob blob = session.Blob;
        Assert.True(AugustWeaponFacts.TryGet(itemId, out AugustWeaponFact item));
        WeaponDefinitionRecord weapon = Assert.Single(blob.WeaponDefinitions!,
            record => record.WeaponDefinitionId == item.WeaponId);
        Assert.Equal(1, ReadDefinitionAmmoSlots(weapon));
        Assert.Equal(1, ReadItemAddAmmoSlots(session, itemId));

        FireGroupRecord group = Assert.Single(blob.FireGroups!, record => record.FireGroupId == item.FireGroupId);
        foreach (uint modeId in group.FireModeIds!)
        {
            FireModeRecord mode = Assert.Single(blob.FireModes!, record => record.FireModeId == modeId);
            Assert.Equal(1, Serialize(mode.WriteTo)[AmmoPerShotAt]);
        }
    }

    private static WeaponSession CreateSession(WeaponTableSource source, bool throwables) =>
        new(WeaponStageOptions.Default with { WeaponTable = source, Throwables = throwables });

    private static int ReadDefinitionAmmoSlots(WeaponDefinitionRecord record)
    {
        byte[] wire = Serialize(record.WriteTo);
        // FUN_140a46ef0: string length at wire 93, string bytes, seven u32s,
        // then the def+0xd0 ammo-slot count. No model-side null check substitutes for the wire.
        int animationLength = BitConverter.ToInt32(wire, 93);
        return BitConverter.ToInt32(wire, 125 + animationLength);
    }

    private static int ReadItemAddAmmoSlots(WeaponSession session, uint itemId)
    {
        var item = new InventoryItem(itemId, ItemGuid, Count: 1, OwnerGuid: SelfGuid,
            ContainerGuid: 0, ContainerDefinitionId: 0, SlotId: 1);
        byte[] wire = Serialize(session.CreateItemAdd(SelfGuid, item));
        // 15-byte ItemAdd envelope, 62-byte base item, baseFlag, then comp+0x10 count.
        return BitConverter.ToInt32(wire, 78);
    }

    private static byte[] Serialize(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }
}
