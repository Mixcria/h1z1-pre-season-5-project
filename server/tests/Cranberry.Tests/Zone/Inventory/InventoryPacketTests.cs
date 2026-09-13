using System.Buffers.Binary;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Inventory;

// Frozen vectors for the container (0xc8) and loadout (0x86) writers of
// docs/41-inventory-slots.md §§3a and 3c. Every length here is one the derivation states, so a
// silent change to a record shape fails the build rather than the client.
public sealed class InventoryPacketTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    private const ulong Character = 0x0102_0304_0506_0708;

    private static ContainerRecord EmptyRecord(ulong guid = 0x1111, uint definitionId = 117) =>
        new(guid, definitionId, Character, SlotCount: 9999, Items: [], MaxBulk: 1100, BulkUsed: 250);

    // -------------------------------------------------------------------------------------
    // 0xc8/03 Container.Error — the cheapest live probe (docs/41 §6c)
    // -------------------------------------------------------------------------------------

    [Fact]
    public void ContainerErrorIsFifteenBytes()
    {
        byte[] bytes = Bytes(new ContainerError(Character, ContainerErrorCode.UnknownContainer).WriteTo);

        Assert.Equal(ContainerError.Length, bytes.Length);
        Assert.Equal(15, bytes.Length);
        Assert.Equal(
        [
            0xc8, 0x03, 0x00,                                       // u8 opcode, u16 sub
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,         // u64 characterGuid
            0x03, 0x00, 0x00, 0x00,                                 // u32 UnknownContainer
        ], bytes);
    }

    /// <summary>The client's own enum, printed as <c>Container Error: %s</c> by FUN_140d7eb60.</summary>
    [Theory]
    [InlineData(ContainerErrorCode.None, 0u)]
    [InlineData(ContainerErrorCode.ContainerInUse, 1u)]
    [InlineData(ContainerErrorCode.WrongItemType, 2u)]
    [InlineData(ContainerErrorCode.UnknownContainer, 3u)]
    [InlineData(ContainerErrorCode.UnknownContainerSlot, 4u)]
    [InlineData(ContainerErrorCode.SlotDoesNotContainItem, 5u)]
    [InlineData(ContainerErrorCode.InteractionValidationFailed, 6u)]
    public void ContainerErrorCodesMatchTheClientEnum(ContainerErrorCode code, uint value)
    {
        Assert.Equal(value, (uint)code);
        byte[] bytes = Bytes(new ContainerError(Character, code).WriteTo);
        Assert.Equal(value, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(11)));
    }

    [Fact]
    public void MoveItemParsesTheAugustFortyThreeByteProximityDrag()
    {
        const ulong container = 0x1111_2222_3333_4444;
        const ulong source = 0x2000_0000_0000_0001;
        const ulong item = 0x2000_0000_0000_0002;
        const ulong target = 0x0102_0304_0506_0708;

        using var writer = new PacketWriter();
        writer.WriteByte(ContainerOpcodes.ContainerBase);
        writer.WriteUInt16(ContainerOpcodes.MoveItemSub);
        writer.WriteUInt64(container);
        writer.WriteUInt64(source);
        writer.WriteUInt64(item);
        writer.WriteUInt64(target);
        writer.WriteUInt32(7);
        writer.WriteInt32(12);
        byte[] bytes = writer.Written.ToArray();

        Assert.Equal(MoveItemRequest.Length, bytes.Length);
        Assert.True(MoveItemRequest.Matches(bytes));
        Assert.Equal(
            new MoveItemRequest(container, source, item, target, Count: 7, NewSlotId: 12),
            MoveItemRequest.Parse(bytes));
        Assert.False(MoveItemRequest.Matches(bytes[..^1]));
    }

    [Fact]
    public void ShredInteractionCarriesOneSecondAndTheCharacterAnimation()
    {
        byte[] bytes = Bytes(new InteractionStart(
            Character,
            DurationMilliseconds: 1000,
            StringId: 8947,
            AnimationId: 10).WriteTo);

        Assert.Equal(InteractionStart.Length, bytes.Length);
        Assert.Equal(ZoneOpcodes.CharacterStateBase, bytes[0]);
        Assert.Equal(InteractionStart.SubOpcode, bytes[1]);
        Assert.Equal(Character, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(2)));
        Assert.Equal(1000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(10)));
        Assert.Equal(8947u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(38)));
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(42)));
        Assert.All(bytes[46..], value => Assert.Equal(0, value));
    }

    // -------------------------------------------------------------------------------------
    // The container record (FUN_140a37290)
    // -------------------------------------------------------------------------------------

    [Fact]
    public void EmptyContainerRecordIsFortyTwoBytes()
    {
        ContainerRecord record = EmptyRecord();
        byte[] bytes = Bytes(record.WriteTo);

        Assert.Equal(ContainerRecord.EmptyLength, bytes.Length);
        Assert.Equal(42, bytes.Length);
        Assert.Equal(record.Length, bytes.Length);

        // Head, in the order FUN_140a37290 reads it.
        Assert.Equal(0x1111ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0)));
        Assert.Equal(117u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(Character, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(12)));
        Assert.Equal(9999u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)));    // item count

        // Tail: u8 flagA, u32 maxBulk, u32 unknown, u32 bulkUsed, u8 flagB.
        Assert.Equal(1, bytes[28]);
        Assert.Equal(1100u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(29)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(33)));
        Assert.Equal(250u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(37)));
        Assert.Equal(1, bytes[41]);
    }

    /// <summary>
    /// One item element is the collection key plus the shared 62-byte FUN_140a3aa60 record, with no
    /// item-class tail — exactly like a ProximateItem (docs/13 §3c, docs/41 §3a).
    /// </summary>
    [Fact]
    public void ContainerItemElementIsTheSharedRecordWithNoClassTail()
    {
        var item = new InventoryItem(
            DefinitionId: 2423, ItemGuid: 0x9001, Count: 3, OwnerGuid: Character,
            ContainerGuid: 0x1111, ContainerDefinitionId: 117, SlotId: 1);
        byte[] element = Bytes(new ContainerItemEntry(1, item).WriteTo);

        Assert.Equal(ContainerRecord.ItemElementLength, element.Length);
        Assert.Equal(66, element.Length);
        Assert.Equal(4 + InventoryItem.BaseLength, element.Length);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(element.AsSpan(0)));
        Assert.Equal(Bytes(item.WriteTo), element[4..]);

        ContainerRecord record = EmptyRecord() with { Items = [new ContainerItemEntry(1, item)] };
        Assert.Equal(ContainerRecord.EmptyLength + 66, Bytes(record.WriteTo).Length);
        Assert.Equal(record.Length, Bytes(record.WriteTo).Length);
    }

    // -------------------------------------------------------------------------------------
    // 0xc8/02 InitContainers and 0xc8/06 UpdateContainer
    // -------------------------------------------------------------------------------------

    [Fact]
    public void InitContainersCarriesTheDestructiveBootstrapList()
    {
        var packet = new InitContainers(Character, [new ContainerEntry(0x1111, EmptyRecord())]);
        byte[] bytes = Bytes(packet.WriteTo);

        Assert.Equal(packet.Length, bytes.Length);
        Assert.Equal(InitContainers.EmptyLength + 4 + ContainerRecord.EmptyLength, bytes.Length);
        Assert.Equal(70, bytes.Length);

        Assert.Equal(0xc8, bytes[0]);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(1)));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(3)));      // unknownA [lead]
        Assert.Equal(Character, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(11)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(19)));
        Assert.Equal(0x1111u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(23)));  // element key
        Assert.Equal(0, bytes[^1]);                                                        // trailing flag [lead]

        // The sub-dispatcher FUN_140d7f450 requires at least 11 bytes.
        Assert.True(bytes.Length >= ContainerOpcodes.MinimumLength);
    }

    [Fact]
    public void UpdateContainerIsTheEnvelopePlusOneRecord()
    {
        var packet = new UpdateContainer(Character, EmptyRecord());
        byte[] bytes = Bytes(packet.WriteTo);

        Assert.Equal(packet.Length, bytes.Length);
        Assert.Equal(UpdateContainer.EnvelopeLength + ContainerRecord.EmptyLength, bytes.Length);
        Assert.Equal(61, bytes.Length);

        Assert.Equal(0xc8, bytes[0]);
        Assert.Equal(6, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(1)));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(3)));
        Assert.Equal(Character, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(11)));
        Assert.Equal(Bytes(EmptyRecord().WriteTo), bytes[19..]);
    }

    /// <summary>The family uses a u16 sub, unlike the Loadouts family's u8 (docs/41 §3a).</summary>
    [Fact]
    public void ContainerFamilyUsesAUInt16Sub()
    {
        Assert.Equal(0xc8, ContainerOpcodes.ContainerBase);
        foreach (byte[] bytes in new[]
                 {
                     Bytes(new InitContainers(Character, []).WriteTo),
                     Bytes(new ContainerError(Character, ContainerErrorCode.None).WriteTo),
                     Bytes(new UpdateContainer(Character, EmptyRecord()).WriteTo),
                 })
        {
            Assert.Equal(0xc8, bytes[0]);
            Assert.Equal(0, bytes[2]);      // the sub's high byte — proof it is 16 bits wide
        }
    }

    // -------------------------------------------------------------------------------------
    // 0xf0 AccessedCharacter - the local mutation gate
    // -------------------------------------------------------------------------------------

    [Fact]
    public void BeginCharacterAccessIsTheAugustThirtyTwoByteSelfGrant()
    {
        byte[] bytes = Bytes(new BeginCharacterAccess(Character).WriteTo);

        Assert.Equal(BeginCharacterAccess.Length, bytes.Length);
        Assert.Equal(
            Convert.FromHexString(
                "F00100"
                + "0807060504030201"
                + "0807060504030201"
                + "01"
                + "08000000"
                + "00000000"
                + "00000000"),
            bytes);
        Assert.Equal(0xf0, ZoneOpcodes.AccessedCharacterBase);
    }

    [Fact]
    public void EndCharacterAccessIsTheAugustElevenByteRevokeAndParsesInbound()
    {
        byte[] bytes = Bytes(new EndCharacterAccess(Character).WriteTo);

        Assert.Equal(EndCharacterAccess.Length, bytes.Length);
        Assert.Equal(Convert.FromHexString("F002000807060504030201"), bytes);
        Assert.True(EndCharacterAccess.Matches(bytes));
        Assert.Equal(Character, EndCharacterAccess.Parse(bytes).CharacterGuid);
    }

    // -------------------------------------------------------------------------------------
    // 0x86 Loadouts
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// docs/41 §3c — the record is 25 bytes, correcting docs/10 rows 1642-1644, which recorded 12
    /// because FUN_140a3bda0 had no decompile.
    /// </summary>
    [Fact]
    public void LoadoutSlotRecordIsTwentyFiveBytes()
    {
        var record = new LoadoutSlotRecord(SurvivorLoadout.Id, SurvivorLoadout.Head, 0xabcd_ef01);
        byte[] bytes = Bytes(record.WriteTo);

        Assert.Equal(LoadoutSlotRecord.Length, bytes.Length);
        Assert.Equal(25, bytes.Length);
        Assert.NotEqual(12, bytes.Length);

        Assert.Equal(SurvivorLoadout.Id, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0)));
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));      // +0x04 slotId
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));       // +0x08 [lead B]
        Assert.Equal(0xabcd_ef01ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(12)));
        Assert.Equal(0, bytes[20]);                                                       // +0x18 [lead]
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(21)));      // +0x20 [lead C]

        Assert.Equal(29, LoadoutSlotRecord.ListElementLength);
    }

    /// <summary>
    /// S6 §7.1: the reader <c>FUN_140d32ca0</c> takes a trailing <c>u32</c> after the 25-byte
    /// record into packet <c>+0x28</c> and <c>return 1</c>s without it, which makes the applier
    /// <c>FUN_140d38100</c> skip the current slot, the wheel and the listeners. 35 bytes was short.
    /// </summary>
    [Fact]
    public void SetLoadoutSlotIsThirtyNineBytesAndEndsWithTheCurrentSlot()
    {
        var packet = new SetLoadoutSlot(
            Character,
            new LoadoutSlotRecord(SurvivorLoadout.Id, SurvivorLoadout.Wheel1, 0x9002),
            SurvivorLoadout.Fists);
        byte[] bytes = Bytes(packet.WriteTo);

        Assert.Equal(SetLoadoutSlot.Length, bytes.Length);
        Assert.Equal(39, bytes.Length);
        Assert.Equal(ZoneOpcodes.LoadoutsBase, bytes[0]);
        Assert.Equal(0x86, bytes[0]);
        Assert.Equal(0x05, bytes[1]);       // u8 sub for this family, not u16
        Assert.Equal(Character, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(2)));
        // The trailer is the LAST four bytes, after the whole record.
        Assert.Equal(SurvivorLoadout.Fists, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(35)));
        Assert.Equal([0x07, 0x00, 0x00, 0x00], bytes[^4..]);
    }

    [Fact]
    public void SetCurrentLoadoutIsFourteenBytes()
    {
        byte[] bytes = Bytes(new SetCurrentLoadout(Character, SurvivorLoadout.Id).WriteTo);

        Assert.Equal(SetCurrentLoadout.Length, bytes.Length);
        Assert.Equal(14, bytes.Length);
        Assert.Equal([0x86, 0x03], bytes[..2]);
        Assert.Equal((int)SurvivorLoadout.Id, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(10)));
    }

    [Fact]
    public void SelectLoadoutSlotRequestParsesTheFourteenByteClientHotbarPacket()
    {
        byte[] bytes =
        [
            0x86, 0x06,
            0x00, 0x00, 0x00, 0x00,                         // unknown
            0x01, 0x00, 0x00, 0x00,                         // slot id
            0x78, 0x56, 0x34, 0x12,                         // client game time
        ];

        Assert.Equal(SelectLoadoutSlotRequest.Length, bytes.Length);
        Assert.True(SelectLoadoutSlotRequest.TryParse(bytes, out SelectLoadoutSlotRequest? request));
        Assert.Equal(new SelectLoadoutSlotRequest(0, SurvivorLoadout.Wheel1, 0x1234_5678), request);
        Assert.False(SelectLoadoutSlotRequest.TryParse(bytes[..^1], out _));
    }

    [Fact]
    public void SelectLoadoutSlotWritesSlotBeforeLoadoutInTenBytes()
    {
        byte[] bytes = Bytes(new SelectLoadoutSlot(SurvivorLoadout.Wheel1, SurvivorLoadout.Id).WriteTo);

        Assert.Equal(SelectLoadoutSlot.Length, bytes.Length);
        Assert.Equal(
        [
            0x86, 0x07,
            0x01, 0x00, 0x00, 0x00,                         // slot id
            0x11, 0x00, 0x00, 0x00,                         // loadout id
        ], bytes);
        Assert.Equal(SurvivorLoadout.Wheel1, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(2)));
        Assert.Equal(SurvivorLoadout.Id, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(6)));
    }

    /// <summary>
    /// S6 §7.1: <c>FUN_140d32b70</c> reads a trailing <c>u32</c> AFTER <c>FUN_140a55fa0</c> has
    /// advanced the cursor past the whole list, stores it at packet <c>+0x28</c>, and
    /// <c>return 1</c>s when it is absent - which is why every pre-wave-12 <c>86 04</c> landed its
    /// slot records but never its current slot, its wheel rebuild or its listeners. The envelope is
    /// 22 bytes, not 18.
    /// </summary>
    [Fact]
    public void SetLoadoutSlotsIsTheTwentyTwoByteEnvelopePlusTwentyNinePerRow()
    {
        var packet = new SetLoadoutSlots(Character, SurvivorLoadout.Id,
        [
            new LoadoutSlotEntry(SurvivorLoadout.Head,
                new LoadoutSlotRecord(SurvivorLoadout.Id, SurvivorLoadout.Head, 0x9003)),
            new LoadoutSlotEntry(SurvivorLoadout.Wheel1,
                new LoadoutSlotRecord(SurvivorLoadout.Id, SurvivorLoadout.Wheel1, 0x9004)),
        ],
        CurrentSlotId: SurvivorLoadout.Wheel1);
        byte[] bytes = Bytes(packet.WriteTo);

        Assert.Equal(packet.Length, bytes.Length);
        Assert.Equal(SetLoadoutSlots.EmptyLength + (2 * 29), bytes.Length);
        Assert.Equal(22, SetLoadoutSlots.EmptyLength);
        Assert.Equal(80, bytes.Length);
        Assert.Equal([0x86, 0x04], bytes[..2]);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(14)));
        // The element key is the slot id, which is what the applier keys the record's hash on.
        Assert.Equal(SurvivorLoadout.Head, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(18)));
        // ... and the last four bytes, past the last 29-byte element, are the current slot.
        Assert.Equal(SurvivorLoadout.Wheel1, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(76)));
    }

    /// <summary>
    /// An empty list is envelope-only, and the trailer is still there: <c>1+1+8+4+4+4</c>.
    /// </summary>
    [Fact]
    public void AnEmptySetLoadoutSlotsIsTwentyTwoBytesEndingInTheCurrentSlot()
    {
        var packet = new SetLoadoutSlots(Character, SurvivorLoadout.Id, [], SurvivorLoadout.Fists);
        byte[] bytes = Bytes(packet.WriteTo);

        Assert.Equal(22, bytes.Length);
        Assert.Equal(SetLoadoutSlots.EmptyLength, packet.Length);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(14)));
        Assert.Equal([0x07, 0x00, 0x00, 0x00], bytes[^4..]);
    }

    /// <summary>
    /// Z1's rule, adopted under D53 (<c>ZoneLoadout.cs:556-559</c>): the field is never 0, because
    /// "0 is not a slot of any loadout" - an empty hand reports the required Fists slot 7.
    /// </summary>
    [Fact]
    public void TheCurrentSlotRuleNeverYieldsZero()
    {
        Assert.Equal(SurvivorLoadout.Fists, LoadoutSelectionRule.CurrentSlotFor(0u));
        Assert.Equal(7u, LoadoutSelectionRule.CurrentSlotFor(0u));
        Assert.Equal(SurvivorLoadout.Wheel1, LoadoutSelectionRule.CurrentSlotFor(SurvivorLoadout.Wheel1));

        ulong next = 0x5000;
        var inventory = new PlayerInventory(Character, () => ++next);
        inventory.Bootstrap();
        Assert.Equal(
            LoadoutSelectionRule.CurrentSlotFor(inventory.CurrentLoadoutSlotId),
            inventory.ToLoadoutSlots().CurrentSlotId);
        Assert.NotEqual(0u, inventory.ToLoadoutSlots().CurrentSlotId);
    }

    // -------------------------------------------------------------------------------------
    // Projection from the model
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The whole point of the lane: the record a picked-up item is granted with must name a real
    /// container guid and a per-container slot, where before it named ContainerGuid = 0 and a global
    /// counter (docs/41 §0).
    /// </summary>
    [Fact]
    public void GrantedItemRecordNamesARealContainer()
    {
        ulong next = 0x5000;
        var inventory = new PlayerInventory(
            Character, () => ++next, new InventoryOptions { QuickUseConsumables = false });
        inventory.Bootstrap();
        inventory.TryPickUp(2423, 2, out InventoryItemInstance? bandage);

        InventoryItem record = bandage!.ToRecord(Character);

        Assert.NotEqual(0ul, record.ContainerGuid);
        Assert.Equal(inventory.BaseBag!.Guid, record.ContainerGuid);
        Assert.Equal(117u, record.ContainerDefinitionId);
        Assert.Equal(1u, record.SlotId);
        Assert.Equal(Character, record.OwnerGuid);
        Assert.Equal(InventoryItem.BaseLength, Bytes(record.WriteTo).Length);
    }

    /// <summary>
    /// The server keeps cargo in one dynamic bag, but the wire list must also declare the empty
    /// backpack record. The August re-indexer associates each wrapper by its loadout-slot key.
    /// </summary>
    [Fact]
    public void InitContainersProjectionDeclaresEveryEquippedContainerByLoadoutSlot()
    {
        ulong next = 0x6000;
        var inventory = new PlayerInventory(Character, () => ++next,
            new InventoryOptions
            {
                BaseCarryBulk = 100,
                StarterOutfit = [],
                QuickUseConsumables = false,
            });
        inventory.Bootstrap();
        inventory.TryPickUp(2112, 1, out _);         // backpack: worn, PARAM1 22 -> +1000
        inventory.TryPickUp(2423, 5, out _);         // 5 bandages at BULK 1 each

        InitContainers packet = inventory.ToInitContainers();

        Assert.Equal([SurvivorLoadout.Backpack, SurvivorLoadout.Inventory],
            packet.Containers.Select(entry => entry.Key));

        ContainerRecord backpack = packet.Containers
            .Single(entry => entry.Key == SurvivorLoadout.Backpack).Container;
        Assert.Equal(22u, backpack.ContainerDefinitionId);
        Assert.Equal(1000u, backpack.MaxBulk);
        Assert.Empty(backpack.Items);
        Assert.True(backpack.FlagA);
        Assert.True(backpack.FlagB);

        ContainerRecord bag = packet.Containers
            .Single(entry => entry.Key == SurvivorLoadout.Inventory).Container;
        Assert.Equal(117u, bag.ContainerDefinitionId);
        Assert.Equal(1100u, bag.MaxBulk);            // 100 base + 1000 from the backpack
        Assert.Equal(5u, bag.BulkUsed);
        Assert.Equal(2423u, Assert.Single(bag.Items).Key); // definition id, not container slot 1
        Assert.Equal(Character, bag.AssociatedCharacterGuid);
        Assert.True(bag.FlagA);
        Assert.Equal(PlayerInventory.DynamicInventoryUnknownBulkField, bag.UnknownBulkField);
        Assert.True(bag.FlagB);

        Assert.Equal(packet.Length, Bytes(packet.WriteTo).Length);
    }

    [Fact]
    public void LoadoutProjectionBindsEveryFilledSlot()
    {
        ulong next = 0x7000;
        var inventory = new PlayerInventory(Character, () => ++next);
        inventory.Bootstrap();
        inventory.TryPickUp(2168, 1, out InventoryItemInstance? helmet);   // motorcycle helmet
        inventory.TryPickUp(1889, 1, out InventoryItemInstance? rifle);    // AR-15

        SetLoadoutSlots packet = inventory.ToLoadoutSlots();
        Assert.Equal(SurvivorLoadout.Id, packet.LoadoutId);

        LoadoutSlotEntry head = packet.Slots.Single(s => s.Key == SurvivorLoadout.Head);
        Assert.Equal(helmet!.Guid, head.Slot.ItemGuid);

        LoadoutSlotEntry wheel = packet.Slots.Single(s => s.Key == SurvivorLoadout.Wheel1);
        Assert.Equal(rifle!.Guid, wheel.Slot.ItemGuid);

        Assert.Equal(packet.Length, Bytes(packet.WriteTo).Length);
    }
}
