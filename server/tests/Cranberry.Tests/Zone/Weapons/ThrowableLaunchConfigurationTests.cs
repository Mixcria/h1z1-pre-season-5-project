using System.Numerics;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

/// <summary>
/// Regression for the seven backwards throws preserved in
/// out/throwable-research-20260906/capture-audit.md. August FUN_1411be900 uses
/// camera + forward * RANGE for TYPE 12; RANGE zero makes the hand aim at the camera.
/// Read the final 1148 list-2 bytes, including captured overrides and the frag's generated modes.
/// </summary>
public sealed class ThrowableLaunchConfigurationTests
{
    // Absolute wire positions in a 639-byte 1148 FireMode record, including its two ids.
    // FUN_140a422d0 reads rec+0x24 at 11, rec+0x58 at 30, rec+0x198 at 302.
    // Keep these independent of the writer's offset table so a misplaced field fails.
    private const int TypeAt = 11;
    private const int RangeAt = 30;
    private const int LaunchPitchAt = 302;

    private static readonly uint[] ThrowableItems = [65, 2235, 2236, 2237, 14];

    [Theory]
    [InlineData(WeaponTableSource.Generated)]
    [InlineData(WeaponTableSource.Captured)]
    public void EveryThrowableAndBothModesHaveForwardRangeAndLaunchPitchOnTheWire(
        WeaponTableSource source)
    {
        WeaponDefinitionsBlob blob = CreateBlob(source, throwables: true);
        Dictionary<uint, byte[]> wireModes = ReadWireModes(blob);

        foreach (uint itemId in ThrowableItems)
        {
            Assert.True(AugustThrowables.TryGet(itemId, out ThrowableFact fact));
            FireGroupRecord group = Assert.Single(blob.FireGroups!, row => row.FireGroupId == fact.FireGroupId);
            Assert.Equal(2, group.FireModeIds!.Count);

            for (int index = 0; index < 2; index++)
            {
                uint modeId = group.FireModeIds[index];
                FireModeRecord mode = Assert.Single(blob.FireModes!, row => row.FireModeId == modeId);
                byte[] wire = wireModes[modeId];
                Assert.Equal(100f, mode.Range);
                Assert.Equal(8f, mode.LaunchPitchAdditiveDegrees);
                Assert.Equal(12, wire[TypeAt]);
                Assert.Equal(100f, BitConverter.ToSingle(wire, RangeAt));
                Assert.Equal(8f, BitConverter.ToSingle(wire, LaunchPitchAt));

                // Follow the actual item's group/mode/projectile join, especially the frag's
                // synthetic group 1404, which cannot join the captured table through August group 0.
                FireModeProjectileRecord mapping = Assert.Single(blob.FireModeProjectiles!, row =>
                    row.FireModeDefinitionId == modeId && row.AmmoItemId == 0);
                Assert.Equal(fact.ProjectileId, mapping.ProjectileDefinitionId);
            }
        }
    }

    [Theory]
    [InlineData(WeaponTableSource.Generated)]
    [InlineData(WeaponTableSource.Captured)]
    public void DisablingThrowablesRetainsThePreviousTableWithoutTheLaunchOverlay(
        WeaponTableSource source)
    {
        WeaponDefinitionsBlob blob = CreateBlob(source, throwables: false);
        Dictionary<uint, byte[]> modes = ReadWireModes(blob);
        Assert.True(AugustThrowables.TryGet(65, out ThrowableFact frag));
        Assert.DoesNotContain(blob.FireGroups!, group => group.FireGroupId == frag.FireGroupId);

        foreach (uint itemId in ThrowableItems.Where(item => item != 65))
        {
            Assert.True(AugustThrowables.TryGet(itemId, out ThrowableFact fact));
            for (int index = 0; index < 2; index++)
            {
                uint modeId = AugustWeaponTable.FireModeIdFor(fact.FireGroupId, index);
                FireModeRecord mode = Assert.Single(blob.FireModes!, row => row.FireModeId == modeId);
                Assert.Equal(0f, mode.Range);
                Assert.Equal(0f, mode.LaunchPitchAdditiveDegrees);
                Assert.Equal(0f, BitConverter.ToSingle(modes[modeId], LaunchPitchAt));
                // The old captured rows already carried RANGE=100; the generated rows did not.
                Assert.Equal(source == WeaponTableSource.Captured ? 100f : 0f,
                    BitConverter.ToSingle(modes[modeId], RangeAt));
                Assert.DoesNotContain(blob.FireModeProjectiles!, mapping =>
                    mapping.FireModeDefinitionId == modeId && mapping.ProjectileDefinitionId == fact.ProjectileId);
            }
        }
    }

    [Theory]
    [InlineData(WeaponTableSource.Generated)]
    [InlineData(WeaponTableSource.Captured)]
    public void TheThrowableOverlayDoesNotChangeAnyGunModeByte(WeaponTableSource source)
    {
        Dictionary<uint, byte[]> on = ReadWireModes(CreateBlob(source, throwables: true));
        Dictionary<uint, byte[]> off = ReadWireModes(CreateBlob(source, throwables: false));
        uint[] gunModes = on.Keys.Where(id => AugustWeaponTable.ArmedFireGroupIds.Contains(id / 2)).ToArray();
        Assert.NotEmpty(gunModes);

        foreach (uint modeId in gunModes)
            Assert.Equal(off[modeId], on[modeId]);
    }

    [Theory]
    [InlineData(WeaponTableSource.Generated, 0f)]
    [InlineData(WeaponTableSource.Generated, 4f)]
    [InlineData(WeaponTableSource.Captured, 0f)]
    [InlineData(WeaponTableSource.Captured, 4f)]
    public void TheNativeCameraTargetStaysAheadOfTheMovingReleasePoint(
        WeaponTableSource source, float cameraBehindPlayer)
    {
        // wire-20260906-125726.txt:16363 feet, :16365 Fire hand, :16359 Orientation.
        // A camera at eye height, optionally behind the player, is an explicit geometric
        // witness; captures do not directly report the camera's world position.
        var feet = new Vector3(831.21f, 55.84f, -2344.14f);
        var hand = new Vector3(831.5033f, 57.7782f, -2343.7969f);
        const float yaw = 1.7146134f;
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        Vector3 camera = feet + (Vector3.UnitY * 1.7f) - (forward * cameraBehindPlayer);

        // The native RANGE=0 case points back from the current hand to the camera.
        Assert.True(Vector3.Dot(Vector3.Normalize(camera - hand), forward) < 0f);

        Assert.True(AugustThrowables.TryGet(65, out ThrowableFact frag));
        uint modeId = AugustWeaponTable.FireModeIdFor(frag.FireGroupId, 0);
        byte[] mode = ReadWireModes(CreateBlob(source, throwables: true))[modeId];
        float range = BitConverter.ToSingle(mode, RangeAt);
        float pitch = BitConverter.ToSingle(mode, LaunchPitchAt) * MathF.PI / 180f;
        Vector3 target = camera + (forward * range);
        Vector3 direction = Vector3.Normalize(target - hand);
        float raisedPitch = Math.Clamp(MathF.Asin(direction.Y) + pitch,
            -85f * MathF.PI / 180f, 85f * MathF.PI / 180f);
        Vector3 horizontal = Vector3.Normalize(new Vector3(direction.X, 0f, direction.Z));
        Vector3 launchDirection = horizontal * MathF.Cos(raisedPitch)
            + Vector3.UnitY * MathF.Sin(raisedPitch);

        Assert.True(Vector3.Dot(launchDirection, forward) > 0.98f);
        Assert.True(launchDirection.Y > 0f);
    }

    [Fact]
    public void UnsupportedThrowingItemsKeepTheirExistingRangeAndPitch()
    {
        WeaponDefinitionsBlob blob = CreateBlob(WeaponTableSource.Generated, throwables: true);
        foreach (FireModeRecord mode in blob.FireModes!)
        {
            if (AugustThrowables.TryGetByFireGroup(mode.FireModeId / 2, out _)) continue;
            Assert.Equal(0f, mode.Range);
            Assert.Equal(0f, mode.LaunchPitchAdditiveDegrees);
        }
    }

    [Fact]
    public void EachCustomGrenadeProvidesTheTwoRadiusEntriesTheClientCopies()
    {
        foreach (ProjectileDefinitionRecord record in AugustThrowables.ProjectileRecords(30f))
            Assert.Equal(new uint[] { 1, 0 }, record.BulletRadii);
    }

    private static WeaponDefinitionsBlob CreateBlob(WeaponTableSource source, bool throwables) =>
        new WeaponSession(WeaponStageOptions.Default with
        {
            WeaponTable = source,
            Throwables = throwables,
        }).Blob;

    private static Dictionary<uint, byte[]> ReadWireModes(WeaponDefinitionsBlob blob)
    {
        byte[] wire = blob.ToArray();
        Assert.Equal(blob.Length, wire.Length);
        // Skip list 0 and list 1, then inspect the actual serialized list 2.
        int cursor = 4 + (blob.WeaponDefinitions?.Sum(row => row.Length) ?? 0);
        cursor += 4 + (blob.FireGroups?.Sum(row => row.Length) ?? 0);
        int count = BitConverter.ToInt32(wire, cursor);
        Assert.Equal(blob.FireModes!.Count, count);
        cursor += 4;
        var modes = new Dictionary<uint, byte[]>(count);
        for (int index = 0; index < count; index++)
        {
            byte[] record = wire.AsSpan(cursor, 639).ToArray();
            modes.Add(BitConverter.ToUInt32(record), record);
            cursor += 639;
        }

        Assert.Equal(blob.ConeOfFire?.Count ?? 0, BitConverter.ToInt32(wire, cursor));
        return modes;
    }
}
