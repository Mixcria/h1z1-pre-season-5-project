using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

// Frozen vectors for the second half of the ground-loot spawn
// (docs/19-loot-full-npc-findings.md): Character.FullCharacterDataRequest (0F 45),
// LightweightToFullNpc (0xda) and the Replication.CreateComponent (ea 04) rep-data list that binds
// the [F] prompt.
public sealed class LootFullNpcPacketTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    /// <summary>The world guid of the first dev ground-loot object (<c>LootWorld</c> base).</summary>
    private const ulong FirstWorldGuid = 0x2000_0000_0000_0001;

    [Fact]
    public void FullCharacterDataRequestParsesTheLiveTenByteRequest()
    {
        // logs/host-20260829-151220.log 15:12:48.228, the client's answer to the d6 carrying world
        // guid 0x2000000000000001 (docs/19 §2).
        FullCharacterDataRequest request = FullCharacterDataRequest.Parse(
            Convert.FromHexString("0F45" + "0100000000000020"));

        Assert.Equal(FirstWorldGuid, request.CharacterGuid);
        Assert.Equal(10, FullCharacterDataRequest.Length);
        Assert.Equal(0x0f, FullCharacterDataRequest.Opcode);
        Assert.Equal(0x45, FullCharacterDataRequest.SubOpcode);

        // The other two of the same live burst, in the order the client sent them.
        Assert.Equal(
            FirstWorldGuid + 2,
            FullCharacterDataRequest.Parse(Convert.FromHexString("0F45" + "0300000000000020")).CharacterGuid);
        Assert.Equal(
            FirstWorldGuid + 1,
            FullCharacterDataRequest.Parse(Convert.FromHexString("0F45" + "0200000000000020")).CharacterGuid);
    }

    [Fact]
    public void FullCharacterDataRequestRejectsAnotherSubShortBodiesAndTrailingBytes()
    {
        // 0f 01 is RemovePlayer, not the full-data request.
        Assert.Throws<PacketFormatException>(() => FullCharacterDataRequest.Parse(
            Convert.FromHexString("0F01" + "0100000000000020")));
        // 09 45 is Command.ItemDefinitionRequest — a different family that shares the sub number
        // (docs/19 §6a corrects loot-notes.md on exactly this pair).
        Assert.Throws<PacketFormatException>(() => FullCharacterDataRequest.Parse(
            Convert.FromHexString("0945" + "0100000000000020")));
        Assert.Throws<PacketFormatException>(() => FullCharacterDataRequest.Parse(
            Convert.FromHexString("0F45" + "01000000")));
        // The live packet is exactly 10 bytes; anything longer is not this layout.
        Assert.Throws<PacketFormatException>(() => FullCharacterDataRequest.Parse(
            Convert.FromHexString("0F45" + "0100000000000020" + "00")));
    }

    [Fact]
    public void LightweightToFullNpcIsTheVehicleRecordWithoutItsSixtyTwoByteTail()
    {
        // docs/19 §1: 0xdb = FUN_140a2f2f0, whose first call is FUN_140a2ded0 = this record. So the
        // npc form must be the live-proven 271-byte vehicle record minus its 62-byte tail, with the
        // opcode byte changed — 271 - 62 = 209, and the vehicle's guid offset 90 lands on +0x120.
        byte[] npc = Bytes(new LightweightToFullNpc(2, FirstWorldGuid).WriteTo);
        byte[] vehicle = Bytes(new LightweightToFullVehicle(2, FirstWorldGuid).WriteTo);

        Assert.Equal(0xda, npc[0]);
        Assert.Equal(0xdb, vehicle[0]);
        Assert.Equal(LightweightToFullNpc.MinimalLength, npc.Length);
        Assert.Equal(LightweightToFullVehicle.MinimalLength, vehicle.Length);
        Assert.Equal(62, vehicle.Length - npc.Length);
        Assert.Equal(vehicle[1..npc.Length], npc[1..]);
        Assert.Equal(LightweightToFullVehicle.VehicleGuidOffset, LightweightToFullNpc.MinimalGuidOffset);
    }

    [Fact]
    public void LightweightToFullNpcIsAllZeroApartFromTheOpcodeTransientIdAndObjectGuid()
    {
        byte[] bytes = Bytes(new LightweightToFullNpc(2, FirstWorldGuid).WriteTo);

        // Byte-exact snapshot: 209 zero bytes with 0xda at 0, the transient varint at 1 and the
        // object guid at 90 (FUN_140b80960 consumes only zero-safe fields for an inert prop).
        byte[] expected = new byte[LightweightToFullNpc.MinimalLength];
        expected[0] = 0xda;
        expected[1] = 0x08;     // ClientVarInt 2 = (2 << 2) | 0
        BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(LightweightToFullNpc.MinimalGuidOffset), FirstWorldGuid);
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void LightweightToFullNpcWidensWithTheTransientVarintAndMovesTheGuidWithIt()
    {
        // LootWorld issues transient ids from 1000, which need a two-byte varint: 210 bytes with
        // the guid at 91 (docs/19 §1, §7).
        var record = new LightweightToFullNpc(1000, FirstWorldGuid);
        byte[] bytes = Bytes(record.WriteTo);

        Assert.Equal(210, bytes.Length);
        Assert.Equal(record.Length, bytes.Length);
        Assert.Equal(91, record.GuidOffset);
        Assert.Equal(Convert.FromHexString("DA" + "A10F"), bytes[..3]);      // (1000 << 2) | 1
        Assert.Equal(FirstWorldGuid, BitConverter.ToUInt64(bytes, record.GuidOffset));

        // Every varint width keeps Length in step with the bytes actually written.
        foreach (uint transientId in new uint[] { 0, 63, 64, 1000, 16383, 16384, 4194303, 4194304 })
        {
            var wide = new LightweightToFullNpc(transientId, FirstWorldGuid);
            byte[] written = Bytes(wide.WriteTo);
            Assert.Equal(wide.Length, written.Length);
            Assert.Equal(FirstWorldGuid, BitConverter.ToUInt64(written, wide.GuidOffset));
        }
    }

    [Fact]
    public void LightweightToFullNpcAnswersTheSpawnItFollowsWithTheSameTransientId()
    {
        // The apply FUN_140b02060 matches by transient id (+0x10), not by guid: the 0F 45 names the
        // guid, so the server maps guid → transient through LootWorld (docs/19 §1a).
        var world = new LootWorld();
        GroundLootItem item = world.Spawn(2423, 9066, new Vector3(1, 2, 3));
        byte[] spawn = Bytes(new AddLightweightItem(
            item.WorldGuid, item.TransientId, item.GroundModelId, item.Position, item.NameId).WriteTo);

        FullCharacterDataRequest request = FullCharacterDataRequest.Parse(
            Convert.FromHexString("0F45" + "0100000000000020"));
        Assert.True(world.TryGet(request.CharacterGuid, out GroundLootItem? requested));
        Assert.Equal(item, requested);

        var full = new LightweightToFullNpc(item.TransientId, item.WorldGuid);
        byte[] bytes = Bytes(full.WriteTo);

        // The transient varint is byte-identical to the one the 0xd6 carried (spawn offset 9, after
        // the opcode and the u64 guid), and the guid round-trips at +0x120.
        Assert.Equal(spawn[9..11], bytes[1..3]);
        Assert.Equal(item.WorldGuid, BitConverter.ToUInt64(bytes, full.GuidOffset));
    }

    [Fact]
    public void InteractReplicationDataIsTheElevenBytePayloadOfFUN_1413dfe80()
    {
        var payload = new InteractReplicationData(3f);
        byte[] bytes = Bytes(payload.WriteTo);

        Assert.Equal(InteractReplicationData.PayloadLength, bytes.Length);
        Assert.Equal(
            Convert.FromHexString(
                "00004040" +    // +0x20 f32 interaction range 3.0
                "00000000" +    // +0x24 UNVERIFIED
                "00" + "00" + "00"),    // +0x28/+0x29/+0x2a UNVERIFIED bools
            bytes);
        Assert.Equal(bytes, payload.ToPayload());

        // A range of 0 is safe: FUN_14146d0a0 substitutes DAT_143f696a0+0x326b8 (docs/19 §4b).
        Assert.Equal(
            Convert.FromHexString("00000000" + "00000000" + "000000"),
            Bytes(new InteractReplicationData(0f, 0, false, false, false).WriteTo));
        // Non-default values land in the derived order.
        Assert.Equal(
            Convert.FromHexString("0000803F" + "07000000" + "01" + "00" + "01"),
            Bytes(new InteractReplicationData(1f, 7, true, false, true).WriteTo));
    }

    [Fact]
    public void ReplicationDataEntryWritesIdHashSequenceAndTheLengthPrefixedPayload()
    {
        // FUN_1413e0380: u32 repId; u32 classHash; u8 sequence; i32 length + bytes. The order of the
        // first two is fixed by the dispatcher's "RepData(%08x) id(%u)" log string (docs/19 §3).
        ReplicationDataEntry entry = new InteractReplicationData(3f).ToEntry(repId: 7);
        byte[] bytes = Bytes(entry.WriteTo);

        Assert.Equal(InteractReplicationData.ClassHash, entry.ClassHash);
        Assert.Equal(ReplicationDataEntry.HeaderLength + InteractReplicationData.PayloadLength, bytes.Length);
        Assert.Equal(entry.Length, bytes.Length);
        Assert.Equal(
            Convert.FromHexString(
                "07000000" +        // repId
                "9D1CD550" +        // class hash 0x50d51c9d
                "00" +              // sequence
                "0B000000" +        // payload length 11
                "00004040" + "00000000" + "000000"),
            bytes);
    }

    [Fact]
    public void CreateComponentWithRepDataMatchesTheEmptyWriterUpToTheRepDataCount()
    {
        // The envelope FUN_1413e1980 is unchanged from LootPackets.CreateComponent; only the final
        // list is populated, which is the whole fix (docs/19 §4b).
        byte[] empty = Bytes(new CreateComponent(OwnerTransientId: 1000).WriteTo);
        var bound = CreateComponentWithRepData.ForGroundItem(ownerTransientId: 1000, repId: 1000);
        byte[] bytes = Bytes(bound.WriteTo);

        Assert.Equal(bound.Length, bytes.Length);
        Assert.Equal(empty.Length + ReplicationDataEntry.HeaderLength + InteractReplicationData.PayloadLength, bytes.Length);
        Assert.Equal(empty[..^4], bytes[..(empty.Length - 4)]);          // through the name-table count
        Assert.Equal(0, BitConverter.ToInt32(empty, empty.Length - 4));  // the inert form: zero entries
        Assert.Equal(1, BitConverter.ToInt32(bytes, empty.Length - 4));
    }

    [Fact]
    public void CreateComponentWithRepDataForAGroundItemIsByteExact()
    {
        var bound = CreateComponentWithRepData.ForGroundItem(ownerTransientId: 1000, repId: 1000);
        byte[] bytes = Bytes(bound.WriteTo);

        Assert.Equal(
            Convert.FromHexString("EA" + "04" + "A10F" + "1700")
                .Concat("ClientInteractComponent"u8.ToArray())
                .Concat(Convert.FromHexString(
                    "00" +              // the packed name's NUL (the reader advances length + 1)
                    "00000000" +        // FUN_1413e1610 name table: none
                    "01000000" +        // FUN_1413e0380 rep-data count
                    "E8030000" +        // repId 1000
                    "9D1CD550" +        // InteractReplicationData
                    "00" +              // sequence
                    "0B000000" +        // payload length
                    "00004040" + "00000000" + "000000"))
                .ToArray(),
            bytes);
        Assert.Equal(CreateComponent.Opcode, CreateComponentWithRepData.Opcode);
        Assert.Equal(CreateComponent.SubOpcode, CreateComponentWithRepData.SubOpcode);
    }

    [Fact]
    public void CreateComponentWithRepDataCarriesSeveralEntriesInOrder()
    {
        // repId must be unique per rep-data instance for the session (docs/19 §7 rule 3).
        var component = new CreateComponentWithRepData(
            1000,
            [
                new InteractReplicationData(3f).ToEntry(repId: 1000),
                NpcReplicationData.ToEntry(repId: 1001),
            ],
            CreateComponent.NpcComponentClass);
        byte[] bytes = Bytes(component.WriteTo);

        Assert.Equal(component.Length, bytes.Length);
        int listCount = 2 + 2 + ClientPackedName.Length(CreateComponent.NpcComponentClass) + 4;
        Assert.Equal(2, BitConverter.ToInt32(bytes, listCount));
        Assert.Equal(1000u, BitConverter.ToUInt32(bytes, listCount + 4));
        Assert.Equal(InteractReplicationData.ClassHash, BitConverter.ToUInt32(bytes, listCount + 8));
        int second = listCount + 4 + ReplicationDataEntry.HeaderLength + InteractReplicationData.PayloadLength;
        Assert.Equal(1001u, BitConverter.ToUInt32(bytes, second));
        Assert.Equal(NpcReplicationData.ClassHash, BitConverter.ToUInt32(bytes, second + 4));
        Assert.Equal(NpcReplicationData.PayloadLength, BitConverter.ToInt32(bytes, second + 9));
    }

    [Fact]
    public void NpcReplicationDataUsesTheAugustWireWidthRatherThanExpandedNativeFieldWidth()
    {
        // The penultimate native u32 at +0x70 is read as u8 on the wire (1413e0247..24e).
        Assert.Equal(82, NpcReplicationData.PayloadLength);
        Assert.Equal((15 * 4) + 8 + (2 * 4) + 1 + 4 + 1, NpcReplicationData.PayloadLength);
        Assert.Equal(new byte[82], NpcReplicationData.ZeroPayload());
        Assert.Equal(
            ReplicationDataEntry.HeaderLength + 82,
            Bytes(NpcReplicationData.ToEntry(repId: 1).WriteTo).Length);
    }

    [Theory]
    [InlineData(1000u, 11023u)]
    [InlineData(999999u, 9943u)]
    public void WorldItemNpcPublishesTheNativeWorldFlagAndUsesADistinctReplicationId(uint transient, uint nameId)
    {
        var npc = CreateComponentWithRepData.ForWorldItemNpc(transient, nameId);
        var interact = CreateComponentWithRepData.ForGroundItem(transient, transient);
        Assert.Equal(CreateComponent.NpcComponentClass, npc.ComponentClass);
        Assert.Equal(transient, npc.OwnerTransientId);
        var entry = Assert.Single(npc.RepData);
        Assert.NotEqual(Assert.Single(interact.RepData).RepId, entry.RepId);
        Assert.Equal(NpcReplicationData.ClassHash, entry.ClassHash);
        var expected = new byte[82];
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(12), nameId);
        expected[81] = 1; // reader1413dffa0 -> object+0x78 -> getter142296c90
        Assert.Equal(expected, entry.Payload);
        Assert.Equal(npc.Length, Bytes(npc.WriteTo).Length);
    }
}
