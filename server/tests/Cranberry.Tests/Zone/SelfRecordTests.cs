using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Emotes;

namespace Cranberry.Tests.Zone;

public class SelfRecordTests
{
    [Fact]
    public void EmoteAssignmentsPopulateTheSelfManagerWithoutShiftingItsFollowingFields()
    {
        var empty = new SelfRecord
        {
            Resources = CharacterResource.Starter,
            Field47 = 0x11223344,
            Field63 = 0xabcdef01,
        };
        byte[] baseline = SelfRecordCodec.ToArray(empty);
        byte[] bytes = SelfRecordCodec.ToArray(empty with { Emotes = AugustEmotes.DefaultSlots });

        // Native FUN_140a461b0: two u32s, a string, worn-map count, then emote-map count.
        // The default-record schema places this count at 0x29a plus the sixteen bytes proven
        // for FUN_140a3b9a0; the resource map is likewise 0x2c6 + sixteen = 0x2d6.
        const int emoteCountOffset = 0x2aa;
        const int addedBytes = 12 * 20;
        Assert.Equal(baseline.Length + addedBytes, bytes.Length);
        Assert.Equal(baseline[..emoteCountOffset], bytes[..emoteCountOffset]);
        var reader = new PacketReader(bytes.AsSpan(emoteCountOffset, 4 + addedBytes));
        Assert.Equal(12, reader.ReadInt32());
        foreach (AugustEmote emote in AugustEmotes.DefaultSlots)
        {
            Assert.Equal(emote.SlotId, reader.ReadUInt32());
            Assert.Equal(emote.SlotId, reader.ReadUInt32());
            Assert.Equal(0ul, reader.ReadUInt64());
            Assert.Equal(emote.ItemDefinitionId, reader.ReadUInt32());
        }
        Assert.True(reader.AtEnd);

        int followingOffset = emoteCountOffset + 4 + addedBytes;
        Assert.Equal(baseline[(emoteCountOffset + 4)..], bytes[followingOffset..]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(followingOffset))); // saved collections
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(followingOffset + 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x2d6 + addedBytes))); // resources
        Assert.Equal(0xabcdef01u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4)));
    }

    [Fact]
    public void EmptyRecordHasTheLoaderMinimalLength()
    {
        byte[] bytes = SelfRecordCodec.ToArray(new SelfRecord());

        // 723 bytes: the read sequence of FUN_140a31140 and its 88 sub-loaders with every list
        // empty (tools/selfschema/selfschema.py over out/ghidra-aug/fable-self-bootstrap).
        Assert.Equal(SelfRecordCodec.MinimalLength, bytes.Length);

        // The default orientation is the unit quaternion at blob 93; everything else is zero.
        byte[] expected = new byte[SelfRecordCodec.MinimalLength];
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(93 + 12), 1f);
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void GuidPositionAndNameLandAtTheOffsetsTheLoaderReads()
    {
        var record = new SelfRecord
        {
            Guid = 0x1001,
            TransientId = 0,
            Position = new Vector4(10.5f, 20.25f, -3f, 1f),
            Identity = new SelfIdentity { Name = "Cranberry" },
        };

        byte[] bytes = SelfRecordCodec.ToArray(record);

        Assert.Equal(SelfRecordCodec.MinimalLength + "Cranberry".Length, bytes.Length);
        Assert.Equal(0x1001ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(0, bytes[16]);                                             // varint 0
        Assert.Equal(10.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(77)));
        Assert.Equal(20.25f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(81)));
        Assert.Equal(-3f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(85)));
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(89)));
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(105)));   // orientation w
        Assert.Equal(9, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(121)));     // name length
        Assert.Equal("Cranberry", Encoding.ASCII.GetString(bytes, 125, 9));
    }

    [Fact]
    public void TheTwoTrailingSubLoaderFieldsLandAtTheirOwnOffsets()
    {
        // Field20A is FUN_140a331a0's trailing u32 (blob 245); Field20 is FUN_140a33390's trailing
        // u32 (blob 307-tree, byte 311). Distinct sentinels prove they are not swapped — the bug
        // was invisible while both were zero because the byte output and length were identical.
        var record = new SelfRecord { Field20A = 0xAABBCCDD, Field20 = 0x11223344 };
        byte[] bytes = SelfRecordCodec.ToArray(record);

        Assert.Equal(SelfRecordCodec.MinimalLength, bytes.Length);
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(245)));
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(311)));
    }

    [Fact]
    public void StarterResourcesEncodeAugustHealthAndStaminaRows()
    {
        byte[] bytes = SelfRecordCodec.ToArray(new SelfRecord
        {
            Resources = CharacterResource.Starter,
        });

        Assert.Equal(
            SelfRecordCodec.MinimalLength
                + CharacterResource.Starter.Count * CharacterResource.WireLength,
            bytes.Length);
        byte[] resourcePrefix = Convert.FromHexString(
            "020000000100000001000000010000000000000010270000");
        int resourceListOffset = bytes.AsSpan().IndexOf(resourcePrefix);
        // The live-derived FUN_140a3b9a0 fields add 16 bytes versus the older static schema,
        // moving this list from the provisional 0x2c6 to its actual default-record offset.
        Assert.Equal(0x2d6, resourceListOffset);

        AssertResource(bytes.AsSpan(resourceListOffset + 4, CharacterResource.WireLength), 1, 1, 1, 10_000, 10_000);
        AssertResource(
            bytes.AsSpan(resourceListOffset + 4 + CharacterResource.WireLength, CharacterResource.WireLength),
            6,
            6,
            6,
            600,
            600);
    }

    [Fact]
    public void VarIntLengthGrowsWithTheTransientId()
    {
        var one = SelfRecordCodec.ToArray(new SelfRecord { TransientId = 63 });
        var two = SelfRecordCodec.ToArray(new SelfRecord { TransientId = 64 });

        Assert.Equal(SelfRecordCodec.MinimalLength, one.Length);
        Assert.Equal(SelfRecordCodec.MinimalLength + 1, two.Length);
        Assert.Equal(0xFC, one[16]);                       // 63 << 2
        Assert.Equal(new byte[] { 0x01, 0x01 }, two[16..18]);  // (64 << 2) | 1 = 0x101
    }

    [Theory]
    [InlineData(0u, "00")]
    [InlineData(1u, "04")]
    [InlineData(63u, "FC")]
    [InlineData(64u, "0101")]
    [InlineData(16383u, "FDFF")]
    [InlineData(16384u, "020001")]
    [InlineData(4194304u, "03000001")]
    public void ClientVarIntMatchesTheClientDecoder(uint value, string hex)
    {
        using var writer = new PacketWriter();
        ClientVarInt.Write(writer, value);

        Assert.Equal(hex, Convert.ToHexString(writer.Written));
        Assert.Equal(hex.Length / 2, ClientVarInt.Length(value));

        // Decode the way FUN_140a190f0 does: low two bits of byte 0 = extra byte count.
        byte[] bytes = Convert.FromHexString(hex);
        int extra = bytes[0] & 3;
        uint packed = 0;
        for (int i = 0; i <= extra; i++)
        {
            packed |= (uint)bytes[i] << (8 * i);
        }

        Assert.Equal(value, packed >> 2);
    }

    [Fact]
    public void SendSelfToClientWrapsTheRecordWithOpcodeAndCount()
    {
        var packet = SendSelfToClient.FromRecord(new SelfRecord { Guid = 0x1001 });

        using var writer = new PacketWriter();
        packet.WriteTo(writer);
        byte[] bytes = writer.Written.ToArray();

        Assert.Equal(1 + 4 + SelfRecordCodec.MinimalLength, bytes.Length);
        Assert.Equal(SendSelfToClient.Opcode, bytes[0]);
        Assert.Equal(SelfRecordCodec.MinimalLength, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1)));
        Assert.Equal(0x1001ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(5 + 8)));
    }

    private static void AssertResource(
        ReadOnlySpan<byte> row,
        uint expectedOuterKey,
        uint expectedResourceId,
        uint expectedResourceType,
        uint expectedCurrentValue,
        uint expectedPreviousValue)
    {
        Assert.Equal(expectedOuterKey, BinaryPrimitives.ReadUInt32LittleEndian(row));
        Assert.Equal(expectedResourceId, BinaryPrimitives.ReadUInt32LittleEndian(row[4..]));
        Assert.Equal(expectedResourceType, BinaryPrimitives.ReadUInt32LittleEndian(row[8..]));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(row[12..]));
        Assert.Equal(expectedCurrentValue, BinaryPrimitives.ReadUInt32LittleEndian(row[16..]));
        Assert.Equal(expectedPreviousValue, BinaryPrimitives.ReadUInt32LittleEndian(row[20..]));
        Assert.True(row[24..].SequenceEqual(new byte[CharacterResource.WireLength - 24]));
    }
}
