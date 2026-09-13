using System.Buffers.Binary;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Crafting;

namespace Cranberry.Tests.Zone.Crafting;

/// <summary>
/// The one edit this lane made outside its own directory: <c>SelfRecord.cs</c>'s list at blob
/// offset <c>0x11a</c> (docs/62 §3).
/// <para>
/// <b>Why this file exists.</b> The self-record loader <c>FUN_140a31140</c> ends with
/// <c>if (streamError || cursor - base &lt; length) FUN_1409e1080()</c>, and <c>FUN_1409e1080</c>
/// writes <c>0xbadbeef</c> to address zero. A record one byte too long or too short does not
/// degrade - it kills the client at login, before the match exists. Every assertion below is a
/// length assertion for that reason.
/// </para>
/// </summary>
public sealed class SelfRecordRecipeListTests
{
    /// <summary>
    /// <b>The regression pin.</b> With no recipes the record must be byte-for-byte what wave 5
    /// wrote: the list still emits its <c>i32 0</c> and nothing else moves. If this ever fails, the
    /// crafting lane has broken login for a feature that is off by default.
    /// </summary>
    [Fact]
    public void AnEmptyRecipeListIsByteIdenticalToTheWave5Record()
    {
        byte[] bytes = SelfRecordCodec.ToArray(new SelfRecord());

        Assert.Equal(SelfRecordCodec.MinimalLength, bytes.Length);
        Assert.Equal(839, bytes.Length);

        byte[] expected = new byte[SelfRecordCodec.MinimalLength];
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(93 + 12), 1f);
        Assert.Equal(expected, bytes);
    }

    /// <summary>
    /// The record grows by exactly the list's own size and by nothing else - the count prefix is
    /// already paid for by the four bytes the empty list wrote.
    /// </summary>
    [Fact]
    public void TheRecordGrowsByExactlyTheRecipeListsSize()
    {
        IReadOnlyList<RecipeRecord> recipes = CraftingCatalog.ToRecords();

        byte[] empty = SelfRecordCodec.ToArray(new SelfRecord());
        byte[] filled = SelfRecordCodec.ToArray(new SelfRecord { Recipes = recipes });

        // ListLength includes the i32 count, which the empty record already wrote.
        int expected = SelfRecordCodec.MinimalLength + RecipeRecord.ListLength(recipes) - 4;
        Assert.Equal(expected, filled.Length);
        Assert.Equal(empty.Length + RecipeRecord.ListLength(recipes) - 4, filled.Length);
    }

    /// <summary>
    /// The retail six, sized to the byte. 45 fixed + 44 per ingredient, in the wire order D256
    /// decoded: satchel 1 → 89, flaming arrow 4 → 221, armour 3 → 177, explosive arrow 4 → 221,
    /// procoagulant 2 → 133, bandage 1 → 89; plus the i32 count. That is <b>934</b>, which is the
    /// captured <c>26 09</c> packet's 936 minus its two-byte header - the same arithmetic, checked
    /// from the other end. A change to the catalogue that changes this number is a change to the
    /// most dangerous packet in the server, and should be seen.
    /// </summary>
    [Fact]
    public void TheShippedCatalogueIsNineHundredAndThirtyFourBytes()
    {
        IReadOnlyList<RecipeRecord> recipes = CraftingCatalog.ToRecords();

        Assert.Equal(4 + 89 + 221 + 177 + 221 + 133 + 89, RecipeRecord.ListLength(recipes));
        Assert.Equal(934, RecipeRecord.ListLength(recipes));
        Assert.Equal(839 + 934 - 4, SelfRecordCodec.ToArray(new SelfRecord { Recipes = recipes }).Length);

        // The wave-6 four are still 536, which is what makes CRANBERRY_CRAFT_RECIPES=0 exact.
        Assert.Equal(
            536,
            RecipeRecord.ListLength(
                CraftingCatalog.ToRecords(retailRecipes: false, sentinelFields: false)));
    }

    /// <summary>
    /// The list the self record writes and the body of <c>0x26 09 Recipe.List</c> are the same
    /// bytes, because both are <c>FUN_140a54bc0</c>. This is what lets stage 1 prove stage 2.
    /// </summary>
    [Fact]
    public void TheSelfRecordListMatchesTheRecipeListPacketBody()
    {
        IReadOnlyList<RecipeRecord> recipes = CraftingCatalog.ToRecords();

        byte[] filled = SelfRecordCodec.ToArray(new SelfRecord { Recipes = recipes });
        byte[] empty = SelfRecordCodec.ToArray(new SelfRecord());

        using var w = new PacketWriter(1024);
        new RecipeList(recipes).WriteTo(w);
        byte[] packetBody = w.Written[2..].ToArray();

        // Everything before the list is unchanged, and the list's first byte is the low byte of
        // its count - 4 in the filled record, 0 in the empty one - so the first difference between
        // the two records IS the start of the list.
        int listStart = 0;
        while (filled[listStart] == empty[listStart])
        {
            listStart++;
        }

        // 282 = 0x11a. docs/10's flat row 1303 puts the recipe list at blob offset 0x11a from the
        // client's own read sequence; Cranberry's writer lands it at exactly that byte. Two
        // independent derivations agreeing is the strongest evidence this lane has that the writer
        // and the loader are in step - so the number is pinned rather than merely observed.
        Assert.Equal(0x11a, listStart);
        Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(filled.AsSpan(listStart)));
        Assert.Equal(packetBody, filled[listStart..(listStart + packetBody.Length)]);

        // And the tail after the list is untouched too, which is what the loader's length
        // assertion actually checks.
        Assert.Equal(
            empty[(listStart + 4)..],
            filled[(listStart + packetBody.Length)..]);
    }

    /// <summary>
    /// The record is written whole every time, so a recipe list with a mistake in it can only ever
    /// be too long or too short by a multiple of a field - never subtly misaligned in a way the
    /// length assertion would miss. This checks the arithmetic for every recipe individually.
    /// </summary>
    [Fact]
    public void EveryRecipeSizesItself()
    {
        foreach (RecipeRecord recipe in CraftingCatalog.ToRecords())
        {
            using var w = new PacketWriter(256);
            recipe.WriteTo(w);
            Assert.Equal(recipe.Length, w.Written.Length);
            Assert.Equal(45 + (44 * recipe.Components.Count), w.Written.Length);
        }
    }
}
