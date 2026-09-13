using System.Buffers.Binary;
using Cranberry.Zone.Crafting;

namespace Cranberry.Tests.Zone.Crafting;

/// <summary>
/// The tolerant <c>09 1a Command.RecipeStart</c> reader (docs/62 §6).
/// <para>
/// The field widths are the one thing about crafting the binary will not say - the Command family
/// has no per-packet serializer to decompile. These tests pin the tolerance itself: that all four
/// candidate framings decode, that the leading candidate is preferred, that an id the session never
/// delivered is a parse failure rather than a wrong craft, and that a failure hands the caller the
/// packet to log.
/// </para>
/// </summary>
public sealed class RecipeStartRequestTests
{
    private static readonly Func<uint, bool> Known = CraftingCatalog.IsKnown;

    private static byte[] Payload(bool u16Sub, uint recipeId, uint? count, int trailing = 0)
    {
        var bytes = new List<byte> { 0x09, 0x1a };
        if (u16Sub)
        {
            bytes.Add(0x00);
        }

        bytes.AddRange(BitConverter.GetBytes(recipeId));
        if (count is uint c)
        {
            bytes.AddRange(BitConverter.GetBytes(c));
        }

        bytes.AddRange(new byte[trailing]);
        return [.. bytes];
    }

    /// <summary>
    /// The leading candidate: a u16 sub, as every live Command capture shows
    /// (<c>09 07 00 InteractRequest</c>, <c>09 15 00 PlayerSelect</c>), followed by the two 32-bit
    /// integers <c>FUN_14143a3e0</c> puts in the packet.
    /// </summary>
    [Fact]
    public void TheExpectedFramingDecodes()
    {
        Assert.True(RecipeStartRequest.TryParse(
            Payload(u16Sub: true, CraftingCatalog.MakeshiftArmor, 3), Known, out RecipeStartRequest request));

        Assert.Equal(CraftingCatalog.MakeshiftArmor, request.RecipeId);
        Assert.Equal(3u, request.Count);
        Assert.Equal(RecipeStartShape.U16SubWithCount, request.Shape);
        Assert.Equal(0, request.TrailingBytes);
    }

    [Theory]
    [InlineData(true, true, RecipeStartShape.U16SubWithCount)]
    [InlineData(false, true, RecipeStartShape.U8SubWithCount)]
    [InlineData(true, false, RecipeStartShape.U16SubNoCount)]
    [InlineData(false, false, RecipeStartShape.U8SubNoCount)]
    public void AllFourCandidateFramingsDecodeAndReportThemselves(bool u16Sub, bool withCount, RecipeStartShape expected)
    {
        byte[] payload = Payload(u16Sub, CraftingCatalog.FieldBandage, withCount ? 2u : null);

        Assert.True(RecipeStartRequest.TryParse(payload, Known, out RecipeStartRequest request));

        Assert.Equal(expected, request.Shape);
        Assert.Equal(CraftingCatalog.FieldBandage, request.RecipeId);
        Assert.Equal(withCount ? 2u : 1u, request.Count);
    }

    /// <summary>
    /// <c>FUN_14143a3e0</c>'s default when the crafting view passes no second argument is <b>1</b>,
    /// not 0 - "Craft 1" and "Craft Max" differ only in that argument. A zero count therefore means
    /// one craft, never none.
    /// </summary>
    [Fact]
    public void AZeroOrAbsentCountMeansOne()
    {
        Assert.True(RecipeStartRequest.TryParse(
            Payload(u16Sub: true, CraftingCatalog.FieldBandage, 0), Known, out RecipeStartRequest zero));
        Assert.Equal(1u, zero.Count);

        Assert.True(RecipeStartRequest.TryParse(
            Payload(u16Sub: true, CraftingCatalog.FieldBandage, null), Known, out RecipeStartRequest absent));
        Assert.Equal(1u, absent.Count);
    }

    /// <summary>
    /// A recipe id this session never delivered is a parse <em>failure</em>, not a craft of an
    /// unknown recipe. The distinction matters because failure is what makes the caller log the raw
    /// hex, and that log line is the whole of docs/59 §1.7 needle 2's answer.
    /// </summary>
    [Fact]
    public void AnUnknownIdFailsSoTheCallerLogsTheHex()
    {
        Assert.False(RecipeStartRequest.TryParse(
            Payload(u16Sub: true, 424242, 1), Known, out RecipeStartRequest request));
        Assert.Equal(default, request);
    }

    /// <summary>
    /// <c>FUN_14143a3e0</c> refuses to send recipe id 0 at all, so a zero decoded from any framing
    /// is proof that framing is wrong rather than evidence of a zero-id recipe.
    /// </summary>
    [Fact]
    public void RecipeIdZeroIsNeverAccepted()
    {
        Assert.False(RecipeStartRequest.TryParse(Payload(u16Sub: true, 0, 1), _ => true, out _));
    }

    /// <summary>
    /// The disambiguation rule in one test. The same nine bytes cannot decode as both framings,
    /// because the u16 framing requires a zero third byte and no Cranberry recipe id is a multiple
    /// of 256 (pinned separately in <see cref="CraftingCatalogTests"/>).
    /// </summary>
    [Fact]
    public void TheTwoSubFramingsCannotBothMatchTheSameBytes()
    {
        byte[] u8 = Payload(u16Sub: false, CraftingCatalog.Procoagulant, 1);

        Assert.True(RecipeStartRequest.TryParse(u8, Known, out RecipeStartRequest request));
        Assert.Equal(RecipeStartShape.U8SubWithCount, request.Shape);

        // The third byte is the id's low byte, which is non-zero, so the u16 framing is ruled out
        // before its id is even read.
        Assert.NotEqual((byte)0, u8[2]);
    }

    /// <summary>Trailing bytes are counted, not rejected: an unexpected tail is exactly the thing
    /// the log line must carry.</summary>
    [Fact]
    public void TrailingBytesAreReportedRatherThanFatal()
    {
        Assert.True(RecipeStartRequest.TryParse(
            Payload(u16Sub: true, CraftingCatalog.Satchel, 1, trailing: 4),
            Known,
            out RecipeStartRequest request));

        Assert.Equal(4, request.TrailingBytes);
        Assert.Equal(CraftingCatalog.Satchel, request.RecipeId);
    }

    /// <summary>
    /// <see cref="RecipeStartRequest.Matches"/> is the dispatcher guard and must never consume a
    /// packet it cannot decode - a payload that matches here and fails <c>TryParse</c> is precisely
    /// the one whose hex has to reach the log.
    /// </summary>
    [Fact]
    public void MatchesAcceptsWhatTryParseMayStillReject()
    {
        byte[] payload = Payload(u16Sub: true, 424242, 1);

        Assert.True(RecipeStartRequest.Matches(payload));
        Assert.False(RecipeStartRequest.TryParse(payload, Known, out _));

        Assert.False(RecipeStartRequest.Matches([0x09, 0x07, 0x00, 1, 2, 3]));
        Assert.False(RecipeStartRequest.Matches([0x09, 0x1a]));
    }

    /// <summary>The recipe id the client sends is the one Cranberry chose, which is the output
    /// item's definition id - so a craft request is readable in the host log without a lookup.</summary>
    [Fact]
    public void TheEchoedIdIsTheOutputItemId()
    {
        byte[] payload = Payload(u16Sub: true, CraftingCatalog.Procoagulant, 1);
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(3));

        Assert.Equal(CraftingCatalog.ById[id].OutputItemDefinitionId, id);
    }
}
