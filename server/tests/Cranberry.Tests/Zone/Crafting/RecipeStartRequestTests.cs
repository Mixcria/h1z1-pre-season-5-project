using System.Buffers.Binary;
using Cranberry.Zone.Crafting;

namespace Cranberry.Tests.Zone.Crafting;

// Exact August serializer 0x1411b41e0; synthetic inputs, not an original-server capture.
public sealed class RecipeStartRequestTests
{
    private static readonly Func<uint, bool> Known = CraftingCatalog.IsKnown;

    [Fact]
    public void TheNativeElevenByteFramingDecodes()
    {
        byte[] packet = Convert.FromHexString("091A007709000003000000");
        Assert.True(RecipeStartRequest.Matches(packet));
        Assert.True(RecipeStartRequest.TryParse(packet, Known, out var request));
        Assert.Equal(CraftingCatalog.FieldBandage, request.RecipeId);
        Assert.Equal(3u, request.Count);
        Assert.Equal(RecipeStartShape.U16SubWithCount, request.Shape);
        Assert.Equal(0, request.TrailingBytes);
    }

    [Fact]
    public void EveryTruncatedPrefixIsRejectedWithoutConsultingTheCatalogue()
    {
        byte[] complete = Payload(CraftingCatalog.FieldBandage, 1);
        for (int length = 0; length < complete.Length; length++)
        {
            byte[] packet = complete[..length];
            Assert.False(RecipeStartRequest.Matches(packet));
            Assert.False(RecipeStartRequest.TryParse(packet,
                _ => throw new InvalidOperationException("Malformed framing reached the catalogue."), out var request));
            Assert.Equal(default, request);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void FormerGuessedFramingsAreRejected(bool u16Subtype, bool includeCount)
    {
        var packet = new List<byte> { 0x09, 0x1a };
        if (u16Subtype) packet.Add(0);
        packet.AddRange(BitConverter.GetBytes(CraftingCatalog.FieldBandage));
        if (includeCount) packet.AddRange(BitConverter.GetBytes(2u));
        Assert.False(RecipeStartRequest.TryParse(packet.ToArray(), Known, out var request));
        Assert.Equal(default, request);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0xffffffffu)]
    public void TheMandatoryCountIsPreservedWithoutCodecInventedDefaults(uint count)
    {
        Assert.True(RecipeStartRequest.TryParse(Payload(CraftingCatalog.FieldBandage, count), Known, out var request));
        Assert.Equal(count, request.Count);
    }

    [Theory]
    [InlineData(0, 0x0a)]
    [InlineData(1, 0x1b)]
    [InlineData(2, 0x01)]
    public void BothSubtypeBytesAndTheFamilyMustMatch(int offset, int value)
    {
        byte[] packet = Payload(CraftingCatalog.FieldBandage, 1);
        packet[offset] = (byte)value;
        Assert.False(RecipeStartRequest.Matches(packet));
        Assert.False(RecipeStartRequest.TryParse(packet, Known, out _));
    }

    [Fact]
    public void UnknownAndZeroIdsAreNotAcceptedAsCrafts()
    {
        Assert.True(RecipeStartRequest.Matches(Payload(424242, 1)));
        Assert.False(RecipeStartRequest.TryParse(Payload(424242, 1), Known, out var unknown));
        Assert.Equal(default, unknown);
        Assert.False(RecipeStartRequest.TryParse(Payload(0, 1), _ => true, out var zero));
        Assert.Equal(default, zero);
    }

    [Fact]
    public void OnlyTheSuppliedCatalogueDecidesWhetherANonzeroIdIsKnown()
    {
        // A recipe whose low byte is zero no longer needs heuristic framing disambiguation.
        var seen = new List<uint>();
        Assert.True(RecipeStartRequest.TryParse(Payload(0x1200, 2), id =>
        {
            seen.Add(id);
            return id == 0x1200;
        }, out var request));
        Assert.Equal(new uint[] { 0x1200 }, seen);
        Assert.Equal(0x1200u, request.RecipeId);
        Assert.False(RecipeStartRequest.TryParse(Payload(CraftingCatalog.FieldBandage, 1), _ => false, out _));
    }

    [Fact]
    public void TrailingBytesAreReportedAfterTheCompleteNativeBody()
    {
        byte[] packet = [.. Payload(CraftingCatalog.FieldBandage, 3), 0xa5, 0xff, 0, 0x7f];
        Assert.True(RecipeStartRequest.TryParse(packet, Known, out var request));
        Assert.Equal(3u, request.Count);
        Assert.Equal(4, request.TrailingBytes);
    }

    private static byte[] Payload(uint recipeId, uint count)
    {
        byte[] packet = [0x09, 0x1a, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3), recipeId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(7), count);
        return packet;
    }
}