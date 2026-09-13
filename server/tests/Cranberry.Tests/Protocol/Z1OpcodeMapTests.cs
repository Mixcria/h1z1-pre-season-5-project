using Cranberry.Protocol;

namespace Cranberry.Tests.Protocol;

/// <summary>
/// Pins the ClientProtocol_1087 -> ClientProtocol_1148 opcode translation (docs/84).
/// <para>
/// These are not round-trip tests of our own generator: every expectation below is a pair of ids
/// read out of the two clients' OWN registration tables, so a regenerated map that moves one of
/// them fails here rather than silently putting a wrong first byte on the wire.
/// </para>
/// </summary>
public sealed class Z1OpcodeMapTests
{
    [Theory]
    // The whole WeaponPacket family is a clean base -1 with every sub unchanged: the cheapest
    // port in the wave, and the reason docs/84 grades guns PORT-DIRECT.
    [InlineData(0x83, 0x03, 0x82, 0x03)]   // Weapon.Fire
    [InlineData(0x83, 0x01, 0x82, 0x01)]   // Weapon.FireStateUpdate
    [InlineData(0x83, 0x08, 0x82, 0x08)]   // Weapon.Reload
    [InlineData(0x83, 0x11, 0x82, 0x11)]   // Weapon.AddFireGroup
    [InlineData(0x83, 0x22, 0x82, 0x22)]   // Weapon.MeleeHitMaterial - melee rides the same family
    // Vehicles and mounts: base -1, subs unchanged.
    [InlineData(0x89, 0x01, 0x88, 0x01)]   // Vehicle.Owner
    [InlineData(0x89, 0x02, 0x88, 0x02)]   // Vehicle.Occupy
    [InlineData(0x71, 0x01, 0x70, 0x01)]   // Mount.MountRequest
    [InlineData(0x71, 0x04, 0x70, 0x04)]   // Mount.DismountResponse
    // The account-item (skin) family shifts base -1 AND sub +1, because 1148 inserted
    // LoadItemCrateLookupManager at sub 0x02.
    [InlineData(0xad, 0x23, 0xac, 0x24)]   // Items.SetSkinItem
    [InlineData(0xad, 0x30, 0xac, 0x31)]   // Items.RequestSetSkinItem
    public void TranslatesShiftedOpcodes(byte z1Base, ushort z1Sub, byte augBase, ushort augSub) =>
        Assert.Equal((augBase, augSub), Z1OpcodeMap.Translate(z1Base, z1Sub));

    [Theory]
    // The core inventory family did not move at all - this is why inventory is PORT-DIRECT.
    [InlineData(0x11, 0x02)]   // ClientUpdate.ItemAdd
    [InlineData(0x11, 0x03)]   // ClientUpdate.ItemUpdate
    [InlineData(0x11, 0x04)]   // ClientUpdate.ItemDelete
    // The two packets the doors lane depends on.
    [InlineData(0x0f, 0x0a)]   // Character.UpdateCharacterState - carries door state bit 48
    [InlineData(0x0f, 0x3f)]   // Character.UpdateCharacterStateDelta
    [InlineData(0x0f, 0x1e)]   // Character.SetCollidable
    [InlineData(0x0f, 0x52)]   // Character.RequestToggleDoorState
    public void KeepsIdenticalOpcodes(byte baseOpcode, ushort sub) =>
        Assert.Equal((baseOpcode, sub), Z1OpcodeMap.Translate(baseOpcode, sub));

    /// <summary>
    /// Character.DoorState is the one deletion that costs this project anything: it was Z1's
    /// entire door mechanism and the August client has no handler for it (no case 0x50 in the
    /// 0x0f dispatcher FUN_140c4d240). Translating it must fail loudly.
    /// </summary>
    [Fact]
    public void DoorStateIsDeletedAndThrowsRatherThanGuessing()
    {
        Assert.True(Z1OpcodeMap.IsDeleted(0x0f, 0x51, out string? member));
        Assert.Equal("cCharacterPacketIdDoorState", member);
        Assert.False(Z1OpcodeMap.TryTranslate(0x0f, 0x51, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => Z1OpcodeMap.Translate(0x0f, 0x51));
    }

    /// <summary>Exactly nine packets were removed between the two clients.</summary>
    [Fact]
    public void NineDeletions()
    {
        Assert.Equal(9, Z1OpcodeMap.DeletedMembers.Count);
        Assert.Contains("cPacketIdPlayerUpdateJump", Z1OpcodeMap.DeletedMembers);
    }

    /// <summary>
    /// The census docs/84 §1 reports. A regeneration that changes it means one of the two
    /// registration extracts moved, and every verdict in docs/84 rests on this number.
    /// </summary>
    [Fact]
    public void TranslationCountIsPinned() => Assert.Equal(1610, Z1OpcodeMap.TranslatableCount);

    /// <summary>
    /// An id that was never registered in 1087 is not translated into something plausible.
    /// </summary>
    [Fact]
    public void UnknownOpcodeIsNotInvented() =>
        Assert.False(Z1OpcodeMap.TryTranslate(0xfe, 0xfff, out _, out _));
}
