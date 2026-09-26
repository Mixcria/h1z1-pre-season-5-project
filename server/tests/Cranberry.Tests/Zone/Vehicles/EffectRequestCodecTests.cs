using Cranberry.Protocol;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

/// <summary>
/// Synthetic frames following August readers 140ce9e70/140cea390 and the five property readers.
/// These fixtures establish parser behavior, not original server gameplay policy or captures.
/// </summary>
public sealed class EffectRequestCodecTests
{
    private const ulong Source = 0x7100_0000_0000_0005;
    private const ulong Target = 0xd000_0000_0000_0002;

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)255)]
    public void CompleteSimpleAddUsesNativeOffsetsAndRefusesEveryShorterPrefix(byte flag)
    {
        byte[] wire = Add(flag);
        Assert.Equal(71, wire.Length);
        Assert.Equal(71, EffectRequest.AddLength);
        AssertRequest(wire, add: true);
        RefusePrefixes(wire);
    }

    [Fact]
    public void CompleteRemoveRequiresItsAncillaryGuidAndWholeVector()
    {
        byte[] wire = Remove();
        Assert.Equal(54, wire.Length);
        Assert.Equal(54, EffectRequest.MinimumLength);
        AssertRequest(wire, add: false);
        RefusePrefixes(wire);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void ComplexAddRequiresAllFiveListsAndEveryStringByte(int stringLength)
    {
        byte[] wire = Add(0, Properties(stringLength));
        AssertRequest(wire, add: true);
        RefusePrefixes(wire);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonpositiveSignedCountsAreEmptyLists(int count)
    {
        using var properties = new PacketWriter();
        for (int i = 0; i < 5; i++) properties.WriteInt32(count);
        AssertRequest(Add(0, properties.Written.ToArray()), add: true);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PositiveCountsCannotOverflowOrExceedThePresentEntries(int list)
    {
        using var properties = new PacketWriter();
        for (int i = 0; i < list; i++) properties.WriteInt32(0);
        properties.WriteInt32(int.MaxValue);
        Assert.False(EffectRequest.TryParse(Add(0, properties.Written.ToArray()), out var request));
        Assert.Null(request);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void NegativeOrTruncatedStringLengthIsRefused(int length)
    {
        using var properties = new PacketWriter();
        for (int i = 0; i < 4; i++) properties.WriteInt32(0);
        properties.WriteInt32(1);
        properties.WriteUInt32(42);
        properties.WriteInt32(length); // No string bytes follow.
        Assert.False(EffectRequest.TryParse(Add(0, properties.Written.ToArray()), out var request));
        Assert.Null(request);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData(EffectRequest.UpdateSub)]
    [InlineData((byte)255)]
    public void UnsupportedSubtypesDoNotEnterTheActionParser(byte sub)
    {
        byte[] wire = Add(1);
        wire[1] = sub;
        Assert.False(EffectRequest.TryParse(wire, out var request));
        Assert.Null(request);
    }

    [Theory]
    [InlineData(0)] // Complex Add.
    [InlineData(1)] // Simple Add.
    [InlineData(3)] // Remove.
    public void CompleteFramesKeepTheNativeReadersTrailingByteTolerance(int form)
    {
        byte[] wire = form switch
        {
            0 => Add(0, new byte[20]),
            1 => Add(1),
            _ => Remove()
        };
        AssertRequest([.. wire, 0xaa, 0xbb], add: form != 3);
    }

    [Fact]
    public void ForeignFamilyIsRefusedEvenWithACompleteAddBody()
    {
        byte[] wire = Add(1);
        wire[0] = 0x9f;
        Assert.False(EffectRequest.TryParse(wire, out var request));
        Assert.Null(request);
    }

    private static void AssertRequest(byte[] wire, bool add)
    {
        Assert.True(EffectRequest.TryParse(wire, out var request));
        Assert.Equal(add, request!.IsAdd);
        Assert.Equal(!add, request.IsRemove);
        Assert.Equal(new EffectHead(1, 90000, 100023), request.Head);
        Assert.Equal(Source, request.SourceCharacterId);
        Assert.Equal(Target, request.TargetCharacterId);
    }

    private static void RefusePrefixes(byte[] wire)
    {
        for (int length = 0; length < wire.Length; length++)
        {
            Assert.False(EffectRequest.TryParse(wire.AsSpan(0, length), out var request),
                $"Accepted incomplete {wire.Length}-byte frame at prefix {length}.");
            Assert.Null(request);
        }
    }

    private static byte[] Add(byte flag, byte[]? properties = null)
    {
        using var writer = new PacketWriter();
        Head(writer, EffectRequest.AddSub);
        writer.WriteUInt32(0x12); // Distinct uninterpreted fields expose offset mistakes.
        writer.WriteUInt64(Source);
        writer.WriteUInt32(0x23);
        writer.WriteUInt64(0x34);
        writer.WriteUInt64(Target);
        writer.WriteUInt64(0x45);
        Vector(writer);
        writer.WriteByte(flag);
        if (properties is not null) writer.WriteRaw(properties);
        return writer.Written.ToArray();
    }

    private static byte[] Remove()
    {
        using var writer = new PacketWriter();
        Head(writer, EffectRequest.RemoveSub);
        writer.WriteUInt64(Source);
        writer.WriteUInt64(Target);
        writer.WriteUInt64(0x45);
        Vector(writer);
        return writer.Written.ToArray();
    }

    private static void Head(PacketWriter writer, byte sub)
    {
        writer.WriteByte(0x9e); writer.WriteByte(sub);
        writer.WriteUInt32(1); writer.WriteUInt32(90000); writer.WriteUInt32(100023);
    }

    private static void Vector(PacketWriter writer)
    {
        writer.WriteSingle(0); writer.WriteSingle(0); writer.WriteSingle(0); writer.WriteSingle(1);
    }

    private static byte[] Properties(int stringLength)
    {
        using var writer = new PacketWriter();
        writer.WriteInt32(1); writer.WriteUInt32(11); writer.WriteUInt32(12);
        writer.WriteInt32(1); writer.WriteUInt32(21); writer.WriteSingle(2.5f);
        writer.WriteInt32(1); writer.WriteUInt32(31); writer.WriteUInt64(32);
        writer.WriteInt32(1); writer.WriteUInt32(41); Vector(writer);
        writer.WriteInt32(1); writer.WriteUInt32(51); writer.WriteInt32(stringLength);
        if (stringLength != 0) writer.WriteRaw(new byte[] { 0xff, 0, 0x81 });
        return writer.Written.ToArray();
    }
}
