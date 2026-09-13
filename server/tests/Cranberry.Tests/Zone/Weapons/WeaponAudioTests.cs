using System.Buffers.Binary;
using Cranberry.Protocol;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

public sealed class WeaponAudioTests
{
    [Theory]
    [InlineData(12u, 1063459730u, "WPN_Empty")]
    [InlineData(6u, 4158717999u, "WPN_Assault_Rifle_M4")]
    [InlineData(1405u, 2430399441u, "WPN_AK47")]
    [InlineData(1374u, 3335203436u, "WPN_Shotgun_12ga")]
    [InlineData(1373u, 239422887u, "WPN_Rifle_308")]
    [InlineData(1388u, 2711705013u, "WPN_44_Magnum")]
    [InlineData(1401u, 3486306879u, "WPN_Pistol_M9")]
    public void AudioHashReachesAugustFieldWithoutMovingAmmoOrFireGroups(
        uint weaponId, uint expectedHash, string objectName)
    {
        var session = new WeaponSession(WeaponStageOptions.Default);
        var original = Assert.Single(session.GeneratedBlob.WeaponDefinitions!, r => r.WeaponDefinitionId == weaponId);
        var crossed = Assert.Single(session.Blob.WeaponDefinitions!, r => r.WeaponDefinitionId == weaponId);
        byte[] expected = Bytes(crossed with { AudioGameObject = original.AudioGameObject });
        byte[] actual = Bytes(crossed);

        // FUN_140a46ef0 wire order: key + 81-byte prefix + melee pair + empty string
        // + five tail words => audio at 117; the new August sprint word remains at 121.
        int audioOffset = 117 + System.Text.Encoding.UTF8.GetByteCount(crossed.AnimationSetName);
        Assert.Equal(expectedHash, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(audioOffset)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(audioOffset + 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(audioOffset), expectedHash);
        Assert.Equal(original.AmmoSlots, crossed.AmmoSlots);
        Assert.Equal(original.FireGroupIds, crossed.FireGroupIds);
        Assert.Equal(expected, actual); // every other byte, including the next arrays, is unchanged
        Assert.Equal(crossed.Length, actual.Length);
        Assert.Equal(expectedHash, CommandHash.Compute(objectName));
    }

    [Fact]
    public void FistsHavePunchAbilityAndNoPistolMuzzleEffect()
    {
        var session = new WeaponSession(WeaponStageOptions.Default);
        Assert.Equal(1111157u, Cranberry.Zone.Combat.AbilityPackets.AbilityIdOf(85));
        Assert.Equal(1111157u, AugustWeaponTable.MeleeAbilityIdFor(12));
        foreach (uint id in new uint[] { 24, 25 })
            Assert.Equal(0u, Assert.Single(session.Blob.FireModes!, m => m.FireModeId == id).EffectGroup);
    }

    [Fact]
    public void UnknownAndUnarmedWeaponsKeepTheirOriginalRecord()
    {
        var unknown = new WeaponDefinitionRecord(999999, [51])
        {
            AmmoSlots = [new WeaponAmmoSlotRow(2325, 30)],
        };
        var unarmed = new WeaponDefinitionRecord(1405, [51]);
        var generated = new WeaponSession(WeaponStageOptions.Default).GeneratedBlob with
        {
            WeaponDefinitions = [unknown, unarmed],
        };
        var captured = CapturedWeaponTable.Apply(generated);
        Assert.Equal(generated.WeaponDefinitions, captured.WeaponDefinitions);
    }

    [Fact]
    public void EveryVerifiedObjectNameReproducesItsCapturedHash()
    {
        Assert.NotEmpty(CapturedWeaponAudioFacts.ByWeaponDefinitionId);
        foreach (var entry in CapturedWeaponAudioFacts.ByWeaponDefinitionId.Values)
            Assert.Equal(entry.Hash, CommandHash.Compute(entry.Name));
    }

    private static byte[] Bytes(WeaponDefinitionRecord record)
    {
        using var writer = new PacketWriter(record.Length);
        record.WriteTo(writer);
        return writer.Written.ToArray();
    }
}
