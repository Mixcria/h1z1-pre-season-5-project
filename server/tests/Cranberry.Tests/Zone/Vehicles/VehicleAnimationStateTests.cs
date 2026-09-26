using System.Buffers.Binary;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed class VehicleAnimationStateTests
{
    private const ulong Vehicle = 0x1122_3344_5566_7788;

    [Fact]
    public void ParsesNativeFlagAndSignedFloatValuesAndPreservesOpaqueField()
    {
        byte[] packet = Write(new VehicleStateData(Vehicle, 0xfedc_ba98,
            [new(0, 0), new(29, 1)], [new(0, 100), new(14, 156)]));

        Assert.True(VehicleStateData.TryParse(packet, out var parsed));
        Assert.Equal(Vehicle, parsed.VehicleGuid);
        Assert.Equal(0xfedc_ba98u, parsed.Value);
        Assert.Equal(new VehicleStateEntry(29, 1), parsed.ListA![1]);
        Assert.Equal(-100, unchecked((sbyte)parsed.ListB![1].Value));
        Assert.Equal(packet, Write(parsed));
    }

    [Fact]
    public void CommonFullRecordBodyIsExactlyTheStatePacketWithoutItsIdentityHeader()
    {
        var packet = new VehicleStateData(Vehicle, 19, [new(3, 1)], [new(0, 206)]);
        using var writer = new PacketWriter();
        packet.WriteBodyTo(writer);
        Assert.Equal(Write(packet)[10..], writer.Written.ToArray());
    }

    [Fact]
    public void FullRecordAnimationPreservesResourcesEngineAndPassengerTail()
    {
        CharacterResource[] resources = [new(0, 50, 50, 2345, 2345), new(1, 561, 1, 75_000, 75_000)];
        VehicleOccupantSlot[] occupants = [new(0, 1001), new(3, 1002)];
        Assert.True(VehicleAnimationSnapshot.Empty.TryApply(
            new(Vehicle, 0, [new(3, 1)], [new(0, 100), new(14, 156)]), out var snapshot));
        var original = new LightweightToFullVehicle(1, Vehicle, resources, occupants, EngineOn: true);
        using var emptyWriter = new PacketWriter();
        original.WriteTo(emptyWriter);
        using var populatedWriter = new PacketWriter();
        (original with { Animation = snapshot }).WriteTo(populatedWriter);
        byte[] empty = emptyWriter.Written.ToArray();
        byte[] populated = populatedWriter.Written.ToArray();

        Assert.Equal(561, empty.Length);
        Assert.Equal(576, populated.Length);
        Assert.Equal(empty[..402], populated[..402]);
        Assert.Equal(2345u, BinaryPrimitives.ReadUInt32LittleEndian(populated.AsSpan(209)));
        Assert.Equal(75_000u, BinaryPrimitives.ReadUInt32LittleEndian(populated.AsSpan(303)));
        Assert.Equal(1, populated[401]);
        Assert.Equal(Write(snapshot.ToPacket(Vehicle))[10..], populated[402..429]);
        Assert.Equal(empty[414..], populated[429..]);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(populated.AsSpan(462)));
        Assert.Equal(1001UL, BinaryPrimitives.ReadUInt64LittleEndian(populated.AsSpan(466)));
        Assert.Equal(0, populated[514]);
        Assert.Equal(1002UL, BinaryPrimitives.ReadUInt64LittleEndian(populated.AsSpan(515)));
        Assert.Equal(3, populated[563]);
    }

    [Fact]
    public void EveryTruncationOfACompleteTwoListPacketIsRejected()
    {
        byte[] bytes = Write(new VehicleStateData(Vehicle, 0, [new(3, 1)], [new(14, 156)]));
        for (int length = 0; length < bytes.Length; length++)
        {
            Assert.False(VehicleStateData.TryParse(bytes.AsSpan(0, length), out var parsed));
            Assert.Null(parsed);
        }
    }

    [Theory]
    [InlineData(0, 0x87)]
    [InlineData(1, 0x04)]
    public void RejectsAnUnrelatedPacketFamilyOrSubtype(int offset, byte value)
    {
        byte[] bytes = Write(new VehicleStateData(Vehicle));
        bytes[offset] = value;
        Assert.False(VehicleStateData.TryParse(bytes, out _));
    }

    [Fact]
    public void NativeNonpositiveCountsAreEmptyAndTrailersAreNotRelayed()
    {
        byte[] bytes = Write(new VehicleStateData(Vehicle));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), -1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), int.MinValue);

        Assert.True(VehicleStateData.TryParse([.. bytes, 0xaa, 0xbb], out var parsed));
        Assert.Empty(parsed.ListA!);
        Assert.Empty(parsed.ListB!);
        Assert.Equal(Write(new VehicleStateData(Vehicle)), Write(parsed));
    }

    [Theory]
    [InlineData(true, 30u, 0)]
    [InlineData(true, uint.MaxValue, 0)]
    [InlineData(true, 0u, 2)]
    [InlineData(true, 29u, 255)]
    [InlineData(false, 15u, 0)]
    [InlineData(false, uint.MaxValue, 0)]
    [InlineData(false, 0u, 101)]
    [InlineData(false, 0u, 155)]
    [InlineData(false, 14u, 128)]
    public void RejectsValuesOutsideNativeProducerAndConsumerBounds(bool flags, uint key, byte value)
    {
        var entries = new VehicleStateEntry[] { new(key, value) };
        var packet = new VehicleStateData(Vehicle, 0, flags ? entries : [], flags ? [] : entries);
        Assert.False(VehicleStateData.TryParse(Write(packet), out _));
    }

    [Theory]
    [InlineData(14, 31)]
    [InlineData(14, int.MaxValue)]
    [InlineData(18, 16)]
    [InlineData(18, int.MaxValue)]
    public void RejectsExcessiveCountsBeforeAllocatingEntries(int offset, int count)
    {
        byte[] bytes = Write(new VehicleStateData(Vehicle));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), count);
        Assert.False(VehicleStateData.TryParse(bytes, out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsRepeatedKeysRatherThanForwardingRepeatedNativeEvents(bool flags)
    {
        var entries = new VehicleStateEntry[] { new(3, 0), new(3, 1) };
        var packet = new VehicleStateData(Vehicle, 0, flags ? entries : [], flags ? [] : entries);
        Assert.False(VehicleStateData.TryParse(Write(packet), out _));
    }

    [Fact]
    public void AllNativeKeysFitAndSnapshotsKeepSignedBytesUnchanged()
    {
        var flags = Enumerable.Range(0, 30).Select(index => new VehicleStateEntry((uint)index, 1)).ToArray();
        var values = Enumerable.Range(0, 15).Select(index => new VehicleStateEntry((uint)index, 156)).ToArray();
        Assert.True(VehicleStateData.TryParse(Write(new VehicleStateData(Vehicle, 0, flags, values)), out var delta));
        Assert.True(VehicleAnimationSnapshot.Empty.TryApply(delta, out var snapshot));
        Assert.Equal(30, snapshot.ListA.Count);
        Assert.Equal(15, snapshot.ListB.Count);
        Assert.Equal(Write(delta), Write(snapshot.ToPacket(Vehicle)));
    }

    [Fact]
    public void DeltaRetainsOmittedKeysAndExplicitZeroClearsOnlyItsKey()
    {
        Assert.True(VehicleAnimationSnapshot.Empty.TryApply(
            new(Vehicle, 0, [new(3, 1), new(19, 1)], [new(0, 100), new(14, 156)]), out var first));
        Assert.True(first.TryApply(new(Vehicle, 7, [new(3, 0)], [new(0, 0)]), out var second));

        Assert.Equal(new VehicleStateEntry(3, 1), first.ListA[0]);
        Assert.Equal(new VehicleStateEntry(3, 0), second.ListA[0]);
        Assert.Equal(new VehicleStateEntry(19, 1), second.ListA[1]);
        Assert.Equal(new VehicleStateEntry(0, 0), second.ListB[0]);
        Assert.Equal(new VehicleStateEntry(14, 156), second.ListB[1]);
        Assert.Equal(7u, second.Value);
        Assert.True(second.TryApply(new(Vehicle, 0), out var emptyDelta));
        Assert.Equal(second.ListA, emptyDelta.ListA);
        Assert.Equal(second.ListB, emptyDelta.ListB);
    }

    [Fact]
    public void SnapshotOwnsItsValuesAndExposesReadOnlyCollections()
    {
        VehicleStateEntry[] source = [new(3, 1)];
        Assert.True(VehicleAnimationSnapshot.Empty.TryApply(new(Vehicle, 0, source), out var snapshot));
        source[0] = new(3, 0);
        Assert.Equal(new VehicleStateEntry(3, 1), Assert.Single(snapshot.ListA));
        var exposed = Assert.IsAssignableFrom<IList<VehicleStateEntry>>(snapshot.ListA);
        Assert.Throws<NotSupportedException>(() => exposed[0] = new(3, 0));
        Assert.Empty(VehicleAnimationSnapshot.Empty.ListA);
    }

    [Fact]
    public void InvalidSecondListDoesNotPartiallyApplyFirstList()
    {
        Assert.True(VehicleAnimationSnapshot.Empty.TryApply(new(Vehicle, 0, [new(3, 1)]), out var snapshot));
        Assert.False(snapshot.TryApply(new(Vehicle, 42, [new(3, 0)], [new(15, 0)]), out var rejected));
        Assert.Same(snapshot, rejected);
        Assert.Equal(new VehicleStateEntry(3, 1), Assert.Single(rejected.ListA));
        Assert.Equal(0u, rejected.Value);
    }

    private static byte[] Write(VehicleStateData packet)
    {
        using var writer = new PacketWriter();
        packet.WriteTo(writer);
        return writer.Written.ToArray();
    }
}
