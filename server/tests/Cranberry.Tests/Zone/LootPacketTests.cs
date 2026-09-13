using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

// Frozen vectors of the ground-loot pickup packets (docs/13-loot-protocol.md).
public sealed class LootPacketTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    /// <summary>The bandage of docs/13 §2: definition 2423, ground model 9066, NAME_ID 12302.</summary>
    private const uint BandageDefinitionId = 2423;
    private const uint BandageGroundModelId = 9066;
    private const uint BandageNameId = 12302;

    [Fact]
    public void AddLightweightItemIsTheVehicleBodyWithoutItsTailAndNoVehicleId()
    {
        var position = new Vector3(1, 2, 3);
        var item = new AddLightweightItem(0x2001, 1000, BandageGroundModelId, position, BandageNameId);
        byte[] bytes = Bytes(item.WriteTo);

        // The same body the parachute record starts with (FUN_140a2d040), opcode 0xd6, vehicle id
        // 0, static position-update type — and nothing after it.
        var vehicle = new AddLightweightVehicle(
            0x2001, 1000, BandageGroundModelId, position, new Vector4(0, 0, 0, 1),
            VehicleId: 0, OwnerGuid: 0, NameId: BandageNameId, PositionUpdateType: 0);
        byte[] vehicleBytes = Bytes(vehicle.WriteTo);

        Assert.Equal(0xd6, bytes[0]);
        Assert.Equal(0xd7, vehicleBytes[0]);
        Assert.Equal(item.Length, bytes.Length);
        Assert.Equal(AddLightweightItem.MinimalLength + 1, bytes.Length);    // transient 1000 = 2-byte varint
        Assert.Equal(vehicleBytes[1..bytes.Length], bytes[1..]);
        Assert.Equal(27, vehicleBytes.Length - bytes.Length);                // the FUN_140a2dd10 tail
    }

    [Fact]
    public void AddLightweightItemCarriesTheGroundModelGuidAndPositionAtTheDerivedOffsets()
    {
        var item = new AddLightweightItem(0x2001, 2, BandageGroundModelId, new Vector3(1, 2, 3), BandageNameId);
        byte[] bytes = Bytes(item.WriteTo);

        Assert.Equal(AddLightweightItem.MinimalLength, bytes.Length);
        // op | guid | varint(2) | str "" | nameId 12302 | u8 | ground model 9066
        Assert.Equal(
            Convert.FromHexString("D6" + "0120000000000000" + "08" + "00000000" + "0E300000" + "00" + "6A230000"),
            bytes[..23]);
        // vehicleId (+0x110) must stay 0: any other value selects a vehicle actor class.
        Assert.Equal(Convert.FromHexString("00000000"), bytes[115..119]);
        // position (+0x94) after scale and the two strings
        Assert.Equal(Convert.FromHexString("0000803F" + "00000040" + "00004040"), bytes[51..63]);
    }

    [Fact]
    public void InventoryItemIsTheSixtyTwoByteRecordWithNoDetailBlock()
    {
        var record = new InventoryItem(
            DefinitionId: BandageDefinitionId,
            ItemGuid: 0x3100000000000001,
            Count: 1,
            OwnerGuid: 0x1001,
            ContainerGuid: 0,
            ContainerDefinitionId: 0,
            SlotId: 1);
        byte[] bytes = Bytes(record.WriteTo);

        Assert.Equal(InventoryItem.BaseLength, bytes.Length);
        Assert.Equal(
            Convert.FromHexString(
                "77090000" +               // definition 2423
                "00000000" +               // tint
                "0100000000000031" +       // item instance guid
                "01000000" +               // count
                "00" +                     // FUN_140a496c0: no detail block
                "0000000000000000" +       // container guid
                "00000000" +               // container definition id
                "01000000" +               // slot 1
                "00000000" + "00000000" + "00000000" +   // durability ×3
                "01" +                     // flag
                "0110000000000000" +       // owner guid
                "00000000"),               // trailer
            bytes);
    }

    [Fact]
    public void ItemAddWrapsTheRecordAndTheLeadItemClassTailInTheLengthPrefixedEnvelope()
    {
        var grant = new ItemAdd(
            0x1001,
            new InventoryItem(BandageDefinitionId, 0x3100000000000001, 1, 0x1001, 0, 0, 1));
        byte[] bytes = Bytes(grant.WriteTo);

        Assert.Equal(grant.Length, bytes.Length);
        Assert.Equal(15 + 62 + 1, bytes.Length);
        // envelope FUN_140a358c0: 11 | 0002 | u64 target | i32 blob length
        Assert.Equal(Convert.FromHexString("11" + "0200" + "0110000000000000" + "3F000000"), bytes[..15]);
        // the blob is the item record followed by the lead Generic item-class tail
        Assert.Equal(0x3f, BitConverter.ToInt32(bytes, 11));
        Assert.Equal(0x00, bytes[^1]);
        Assert.Equal(
            Bytes(new InventoryItem(BandageDefinitionId, 0x3100000000000001, 1, 0x1001, 0, 0, 1).WriteTo),
            bytes[15..^1]);
    }

    [Fact]
    public void ItemDeleteIsTheNineteenByteAugustForm()
    {
        byte[] bytes = Bytes(w => new ItemDelete(0x1001, 0x3100000000000001).WriteTo(w));

        Assert.Equal(ItemDelete.Length, bytes.Length);
        Assert.Equal(
            Convert.FromHexString("11" + "0400" + "0110000000000000" + "0100000000000031"),
            bytes);
    }

    [Fact]
    public void RemovePlayerTakesTheWorldObjectOutOfTheWorldInTwelveBytes()
    {
        // docs/13 §4c: effect flag 0 is the plain despawn a pickup needs (1 is death/ragdoll).
        byte[] bytes = Bytes(w => new RemovePlayer(0x2001).WriteTo(w));

        Assert.Equal(RemovePlayer.Length, bytes.Length);
        Assert.Equal(Convert.FromHexString("0F01" + "0120000000000000" + "0000"), bytes);
    }

    [Fact]
    public void CreateComponentNamesTheOwnerTransientIdAndTheInteractComponentClass()
    {
        var component = new CreateComponent(OwnerTransientId: 2);
        byte[] bytes = Bytes(component.WriteTo);

        Assert.Equal(component.Length, bytes.Length);
        // ea | 04 (u8 sub) | varint(2)=08 | packed name (u16 len, bytes, NUL) | 0 names | 0 repdata
        Assert.Equal(
            Convert.FromHexString("EA" + "04" + "08" + "1700")
                .Concat("ClientInteractComponent"u8.ToArray())
                .Concat(Convert.FromHexString("00" + "00000000" + "00000000"))
                .ToArray(),
            bytes);
    }

    [Fact]
    public void ProximateItemsElementsAreSeventyFourBytesWithNoItemClassTail()
    {
        var record = new InventoryItem(BandageDefinitionId, 0x2001, 1, 0, 0, 0, 0);
        var list = new ProximateItems([new ProximateItem(1000, record, 0x2001)]);
        byte[] bytes = Bytes(list.WriteTo);

        Assert.Equal(list.Length, bytes.Length);
        Assert.Equal(6 + ProximateItems.ElementLength, bytes.Length);
        Assert.Equal(Convert.FromHexString("F8" + "01" + "01000000" + "E8030000"), bytes[..10]);
        Assert.Equal(Bytes(record.WriteTo), bytes[10..72]);
        Assert.Equal(Convert.FromHexString("0120000000000000"), bytes[72..]);

        Assert.Equal(Convert.FromHexString("F8" + "01" + "00000000"), Bytes(new ProximateItems([]).WriteTo));
    }

    [Fact]
    public void PackedNameIsALengthTagFollowedByTheStringAndItsTerminator()
    {
        Assert.Equal(
            Convert.FromHexString("0400").Concat("Item"u8.ToArray()).Concat([(byte)0]).ToArray(),
            Bytes(w => ClientPackedName.Write(w, "Item")));
        Assert.Equal(7, ClientPackedName.Length("Item"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Bytes(w => ClientPackedName.Write(w, new string('x', ClientPackedName.LengthMask + 1))));
    }

    [Fact]
    public void InteractRequestParsesTheFKeyPickupAndRejectsAnotherCommand()
    {
        InteractRequest request = InteractRequest.Parse(
            Convert.FromHexString("09" + "0700" + "0120000000000000"));

        Assert.Equal(0x2001ul, request.TargetGuid);
        Assert.Equal(0, request.TrailingBytes);

        // The guid offset is a lead (docs/13 §4a): trailing bytes are reported, not rejected.
        InteractRequest padded = InteractRequest.Parse(
            Convert.FromHexString("09" + "0700" + "0120000000000000" + "0000"));
        Assert.Equal(0x2001ul, padded.TargetGuid);
        Assert.Equal(2, padded.TrailingBytes);

        Assert.Throws<PacketFormatException>(() => InteractRequest.Parse(
            Convert.FromHexString("09" + "4E00" + "00")));
        Assert.Throws<PacketFormatException>(() => InteractRequest.Parse(
            Convert.FromHexString("09" + "0700" + "01200000")));
    }

    [Fact]
    public void PlayerSelectParsesTheSecondPacketOfOneFPress()
    {
        PlayerSelect select = PlayerSelect.Parse(
            Convert.FromHexString("09" + "1500" + "0110000000000000" + "0120000000000000"));

        Assert.Equal(0x1001ul, select.SelectingCharacterGuid);
        Assert.Equal(0x2001ul, select.TargetGuid);
        Assert.Throws<PacketFormatException>(() => PlayerSelect.Parse(
            Convert.FromHexString("09" + "0700" + "0120000000000000")));
    }

    [Fact]
    public void LootWorldClaimsAnObjectExactlyOnce()
    {
        var world = new LootWorld();
        GroundLootItem first = world.Spawn(BandageDefinitionId, BandageGroundModelId, new Vector3(1, 2, 3));
        GroundLootItem second = world.Spawn(BandageDefinitionId, BandageGroundModelId, new Vector3(4, 5, 6));

        Assert.Equal(2, world.Count);
        Assert.NotEqual(first.WorldGuid, second.WorldGuid);
        Assert.NotEqual(first.TransientId, second.TransientId);
        Assert.Equal(LootWorld.DefaultWorldGuidBase, first.WorldGuid);
        Assert.Equal(LootWorld.DefaultTransientIdBase, first.TransientId);

        Assert.True(world.TryGet(first.WorldGuid, out GroundLootItem? found));
        Assert.Equal(first, found);

        // One F press sends both InteractRequest and PlayerSelect: the second must find nothing.
        Assert.True(world.TryClaim(first.WorldGuid, out GroundLootItem? claimed));
        Assert.Equal(first, claimed);
        Assert.False(world.TryClaim(first.WorldGuid, out _));
        Assert.False(world.TryGet(first.WorldGuid, out _));
        Assert.Equal(1, world.Count);

        Assert.NotEqual(world.NextItemGuid(), world.NextItemGuid());
        world.Clear();
        Assert.Equal(0, world.Count);
    }

    /// <summary>
    /// A guid this world minted stays identifiably loot after it has been claimed, and a guid from
    /// another subsystem's range never looks like loot.
    /// <para>
    /// This is what the door dispatcher's "ground loot wins a tie, always" guard needs. One [F]
    /// press is two packets naming the same guid (<c>09 15 PlayerSelect</c> then <c>09 07
    /// InteractRequest</c> ~2 ms later — logs/host-20260829-201025.log 02.179/02.181), and the first
    /// one claims the item destructively. A guard written on <c>TryGet</c> therefore passes on the
    /// SECOND packet — the one that carries the float4 — and hands it to
    /// <c>MatchDoors.TryResolveNearest</c>, which swings whatever door is within 6 m. Loot and doors
    /// share rooms, so that fires on essentially every indoor pickup.
    /// </para>
    /// </summary>
    [Fact]
    public void AClaimedGuidIsStillARecognisableLootGuid()
    {
        var world = new LootWorld();
        GroundLootItem item = world.Spawn(BandageDefinitionId, BandageGroundModelId, new Vector3(1, 2, 3));

        Assert.True(world.IsLootGuid(item.WorldGuid));
        Assert.True(world.TryClaim(item.WorldGuid, out _));
        Assert.False(world.TryGet(item.WorldGuid, out _));
        Assert.True(world.IsLootGuid(item.WorldGuid));

        // Zoning clears the registrations but does not rewind the allocator, so a guid from the
        // previous world is still not a door.
        world.Clear();
        Assert.True(world.IsLootGuid(item.WorldGuid));

        // Never issued, and the neighbouring subsystems' bases: doors mint from 0x4400…, vehicles
        // from 0x4600…, and neither may be mistaken for loot.
        Assert.False(world.IsLootGuid(LootWorld.DefaultWorldGuidBase - 1));
        Assert.False(world.IsLootGuid(item.WorldGuid + 1));
        Assert.False(world.IsLootGuid(0x4400_0000_0000_0001));
        Assert.False(world.IsLootGuid(0x4600_0000_0000_0001));
        Assert.False(world.IsLootGuid(0));
    }
}
