using Cranberry.Protocol;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

public sealed class BinocularMagazineTests
{
    [Theory]
    [InlineData(1542u)]
    [InlineData(1695u)]
    public void BinocularsKeepAResolvableOpticWithoutAFakeRoundOrMagazine(uint item)
    {
        Assert.True(AugustWeaponFacts.TryGet(item, out var fact));
        Assert.Equal(0, fact.ClipSize);
        var session = new WeaponSession(WeaponStageOptions.Default);
        // Re-announcing the item with an arbitrary magazine must never add ammo to an optic.
        var tail = session.CreateTail(item, magazine: 30);
        Assert.NotNull(tail);
        Assert.False(tail.AmmoSlot);
        Assert.Equal(0, tail.Magazine);
        Assert.Equal(0, Assert.Single(tail.Groups).Modes[0].Charge);
        using var tailWriter = new PacketWriter();
        tail.WriteTo(tailWriter);
        Assert.Equal(0, BitConverter.ToInt32(tailWriter.Written.Slice(1, 4))); // ammo-slot count

        var weapon = Assert.Single(session.Blob.WeaponDefinitions!, row => row.WeaponDefinitionId == fact.WeaponId);
        Assert.Empty(weapon.AmmoSlots ?? []);
        var group = Assert.Single(session.Blob.FireGroups!, row => row.FireGroupId == fact.FireGroupId);
        foreach (uint id in group.FireModeIds!)
        {
            var mode = Assert.Single(session.Blob.FireModes!, row => row.FireModeId == id);
            using var writer = new PacketWriter();
            mode.WriteTo(writer);
            // FUN_14228d970 dereferences the definition and tests this ID at rec+0x18.
            // A zero ItemAdd charge leaves this lookup (and the scope camera) available.
            Assert.True(BitConverter.ToUInt32(writer.Written.Slice(4, 4)) > 0);
        }
    }

    [Theory]
    [InlineData(WeaponTableSource.Generated, 1542u)]
    [InlineData(WeaponTableSource.Generated, 1695u)]
    [InlineData(WeaponTableSource.Captured, 1542u)]
    [InlineData(WeaponTableSource.Captured, 1695u)]
    public void PrimaryClickFailsTheNativeFirePrerequisiteWithoutMakingOpticsReloadable(
        WeaponTableSource source, uint item)
    {
        var session = new WeaponSession(WeaponStageOptions.Default with { WeaponTable = source });
        Assert.True(AugustWeaponFacts.TryGet(item, out var fact));
        var tail = session.CreateTail(item)!;
        using var tailWriter = new PacketWriter();
        tail.WriteTo(tailWriter);
        int slotCount = BitConverter.ToInt32(tailWriter.Written.Slice(1, 4));
        Assert.Equal(0, slotCount);
        var modes = session.Blob.FireModes!.Where(mode => mode.FireModeId / 2 == fact.FireGroupId).ToArray();
        Assert.Equal(2, modes.Length);
        foreach (var mode in modes)
        {
            using var writer = new PacketWriter();
            mode.WriteTo(writer);
            byte[] bytes = writer.Written.ToArray();
            byte flags = bytes[Offset(WeaponListLayouts.FireModeFlags1)];
            int roundsRequired = (sbyte)bytes[Offset(WeaponListLayouts.FireModeAmmoPerShot)];
            Assert.NotEqual(0, flags & WeaponListLayouts.FireModeCheckEnterFireStateFlag);
            Assert.Equal(0, flags & WeaponListLayouts.FireModeFireNeedsLockFlag); // no blocked-fire cue
            Assert.Equal(1, roundsRequired);
            // Local 1414862b0 -> 14228c590 -> 142290a90 requires the magazine count
            // from 14228e080 to cover this prerequisite; an absent slot returns zero.
            Assert.Equal(0u, BitConverter.ToUInt32(bytes, Offset(WeaponListLayouts.FireModeAmmoItemId)));
            Assert.Equal(0, bytes[Offset(WeaponListLayouts.FireModeType)]); // no melee/ability workaround
            Assert.Equal(4, bytes[Offset(WeaponListLayouts.FireModeReticleId)]);
            Assert.Equal(mode.FireModeId % 2 == 0 ? 1 : 0,
                bytes[Offset(WeaponListLayouts.FireModeForceFpScope)]);
        }
    }

    private static int Offset(short recordOffset) => 8 + WeaponListLayouts.FireModeBody
        .TakeWhile(field => field.RecordOffset != recordOffset).Sum(field => field.Size);
}
