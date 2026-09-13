using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Inventory;

/// <summary>
/// <c>11 03 ClientUpdate.ItemUpdate</c>, byte for byte.
/// <para>
/// The layout is derived, not guessed - <c>FUN_140afc660</c> case 3 stamps <c>{0x11, 3}</c> and
/// calls <c>FUN_140a65960(rec, bytes, len, 0)</c>, whose fourth argument being zero makes the
/// success test <c>end − cursor &lt; 1</c>: the packet is the 11-byte envelope plus the 62-byte item
/// record and <b>nothing else</b>. Anything that changes that length is a change to the derivation,
/// which is what this test exists to catch (docs/102 §3).
/// </para>
/// </summary>
public sealed class ItemUpdatePacketTests
{
    private const ulong Owner = 0x0000_0000_0000_1001;
    private const ulong Stack = 0x3100_0000_0000_00AA;

    [Fact]
    public void TheWholePacketIsSeventyThreeBytes()
    {
        Assert.Equal(11, ItemUpdate.EnvelopeLength);
        Assert.Equal(62, InventoryItem.BaseLength);
        Assert.Equal(73, ItemUpdate.Length);
        Assert.Equal(73, Bytes(Record()).Length);
    }

    [Fact]
    public void EveryFieldLandsWhereTheClientsReaderExpectsIt()
    {
        byte[] packet = Bytes(Record());

        // Envelope: u8 base; u16 sub; u64 targetCharacterGuid.
        Assert.Equal(ZoneOpcodes.ClientUpdateBase, packet[0]);
        Assert.Equal(0x0003, BitConverter.ToUInt16(packet, 1));
        Assert.Equal(Owner, BitConverter.ToUInt64(packet, 3));

        // The 62-byte record, in FUN_140a3aa60's own read order.
        Assert.Equal(1429u, BitConverter.ToUInt32(packet, 11));      // +8  definitionId
        Assert.Equal(0u, BitConverter.ToUInt32(packet, 15));         // +c  tint
        Assert.Equal(Stack, BitConverter.ToUInt64(packet, 19));      // +10 itemGuid
        Assert.Equal(17u, BitConverter.ToUInt32(packet, 27));        // +18 count
        Assert.Equal(0, packet[31]);                                 //     no detail block
        Assert.Equal(0x1234UL, BitConverter.ToUInt64(packet, 32));   // +20 containerGuid
        Assert.Equal(117u, BitConverter.ToUInt32(packet, 40));       // +28 containerDefinitionId
        Assert.Equal(4u, BitConverter.ToUInt32(packet, 44));         // +2c slotId
        Assert.Equal(1000u, BitConverter.ToUInt32(packet, 48));      // +30 baseDurability
        Assert.Equal(995u, BitConverter.ToUInt32(packet, 52));       // +34 currentDurability
        Assert.Equal(1000u, BitConverter.ToUInt32(packet, 56));      // +38 maxDurability
        Assert.Equal(1, packet[60]);                                 // +3c flag
        Assert.Equal(Owner, BitConverter.ToUInt64(packet, 61));      // +40 ownerGuid
        Assert.Equal(0u, BitConverter.ToUInt32(packet, 69));         // +48 trailer
    }

    /// <summary>
    /// The whole packet as one hex string. If this moves, the derivation moved: re-read
    /// <c>FUN_140a65960</c> before changing it.
    /// </summary>
    [Fact]
    public void TheBytesAreFrozen()
    {
        Assert.Equal(
            "11" + "0300"
            + "0110000000000000"                                     // owner
            + "95050000"                                             // definitionId 1429
            + "00000000"                                             // tint
            + "AA00000000000031"                                     // itemGuid
            + "11000000"                                             // count 17
            + "00"                                                   // hasDetail = 0
            + "3412000000000000"                                     // containerGuid 0x1234
            + "75000000"                                             // containerDefinitionId 117
            + "04000000"                                             // slotId 4
            + "E8030000" + "E3030000" + "E8030000"                   // 1000 / 995 / 1000
            + "01"                                                   // flag
            + "0110000000000000"                                     // ownerGuid
            + "00000000",                                            // trailer
            Convert.ToHexString(Bytes(Record())));
    }

    /// <summary>
    /// <c>ForStack</c> carries exactly what <c>ItemAdd</c> would have carried for the same instance,
    /// minus the item-class tail - so the client's copy of the tile cannot drift from the server's.
    /// </summary>
    [Fact]
    public void ForStackMatchesTheItemAddRecordForTheSameInstance()
    {
        ulong next = Stack;
        var inventory = new PlayerInventory(Owner, () => next++);
        inventory.Bootstrap();
        InventoryItemInstance box = inventory.CreateInstance(1429, 30);
        Assert.True(inventory.TryStow(box));

        byte[] update = ItemUpdate.ForStack(Owner, box).ToBytes();

        using var expected = new PacketWriter(64);
        box.ToRecord(Owner).WriteTo(expected);

        Assert.Equal(
            Convert.ToHexString(expected.Written.ToArray()),
            Convert.ToHexString(update.AsSpan(ItemUpdate.EnvelopeLength).ToArray()));
    }

    /// <summary>
    /// <c>ForDurability</c> fills the three durability fields and clamps, because
    /// <c>FUN_141479ae0</c> writes all three straight onto the live item.
    /// </summary>
    [Fact]
    public void ForDurabilityFillsAllThreeFieldsAndClamps()
    {
        ulong next = Stack;
        var inventory = new PlayerInventory(Owner, () => next++);
        inventory.Bootstrap();
        InventoryItemInstance gun = inventory.CreateInstance(AugustHeldWeapon.ItemDefinitionId, 1);
        inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);

        byte[] packet = ItemUpdate.ForDurability(Owner, gun, -20, 1000).ToBytes();

        Assert.Equal(1000u, BitConverter.ToUInt32(packet, 48));
        Assert.Equal(0u, BitConverter.ToUInt32(packet, 52));         // clamped up from -20
        Assert.Equal(1000u, BitConverter.ToUInt32(packet, 56));

        byte[] over = ItemUpdate.ForDurability(Owner, gun, 5_000, 1000).ToBytes();
        Assert.Equal(1000u, BitConverter.ToUInt32(over, 52));        // clamped down to the max
    }

    private static InventoryItem Record() => new(
        DefinitionId: 1429,
        ItemGuid: Stack,
        Count: 17,
        OwnerGuid: Owner,
        ContainerGuid: 0x1234,
        ContainerDefinitionId: 117,
        SlotId: 4,
        BaseDurability: 1000,
        CurrentDurability: 995,
        MaxDurability: 1000);

    private static byte[] Bytes(InventoryItem item) => new ItemUpdate(Owner, item).ToBytes();
}
