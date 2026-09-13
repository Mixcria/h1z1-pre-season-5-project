using System.Buffers.Binary;
using Cranberry.Protocol;
using Cranberry.Zone.Crafting;

namespace Cranberry.Tests.Zone.Crafting;

/// <summary>
/// The recipe record and the 0x26 family, byte for byte (docs/62 §2).
/// <para>
/// Every arm of <c>FUN_141518250</c> rejects trailing bytes, so these lengths are exact rather than
/// minimums - a packet one byte long is dropped in silence, which is the failure mode these tests
/// exist to make impossible.
/// </para>
/// </summary>
public sealed class RecipeRecordTests
{
    private static byte[] Write(Action<PacketWriter> write)
    {
        using var w = new PacketWriter(256);
        write(w);
        return w.Written.ToArray();
    }

    /// <summary>
    /// The fixed part is 45 bytes: nine u32s, one bool, the i32 component count and the trailing
    /// u32. docs/59 §1.5.4 derives it; <c>FUN_140a44390</c> confirms it read by read.
    /// </summary>
    [Fact]
    public void AnIngredientlessRecordIs45Bytes()
    {
        byte[] bytes = Write(w => new RecipeRecord { RecipeId = 7 }.WriteTo(w));

        Assert.Equal(RecipeRecord.FixedLength, bytes.Length);
        Assert.Equal(45, bytes.Length);
    }

    /// <summary>Each ingredient is 44 bytes: the list reader's key plus eight u32s and one u64.</summary>
    [Fact]
    public void EachIngredientAdds44Bytes()
    {
        var record = new RecipeRecord
        {
            RecipeId = 7,
            Components = [new RecipeComponentRecord { Key = 1 }, new RecipeComponentRecord { Key = 2 }],
        };

        byte[] bytes = Write(w => record.WriteTo(w));

        Assert.Equal(45 + (2 * RecipeComponentRecord.Length), bytes.Length);
        Assert.Equal(133, bytes.Length);
        Assert.Equal(record.Length, bytes.Length);
    }

    /// <summary>
    /// The single most surprising thing about this record, and the one a re-reader is most likely
    /// to "fix": <b>the output item id is written last</b>, after the component list.
    /// <c>FUN_140a44390</c> reads <c>param_1[1]</c> as its final action, after
    /// <c>thunk_FUN_140a4da70</c>. Moving it to the front would misalign every ingredient.
    /// </summary>
    [Fact]
    public void TheOutputItemIdIsTheLastFieldOnTheWire()
    {
        var record = new RecipeRecord
        {
            RecipeId = 0x1111_1111,
            OutputItemDefinitionId = 0x2222_2222,
            Components = [new RecipeComponentRecord { Key = 0x3333_3333 }],
        };

        byte[] bytes = Write(w => record.WriteTo(w));

        Assert.Equal(0x1111_1111u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0)));
        Assert.Equal(0x2222_2222u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4)));
    }

    /// <summary>
    /// The wire order is the client's read order, not the record's memory order. Field 2 lands at
    /// <c>rec+0x08</c>, field 3 at <c>rec+0x18</c>: the gap at <c>rec+0x0c..+0x17</c> holds the
    /// embedded vtable and is never written, which is why the wire looks scrambled.
    /// </summary>
    [Fact]
    public void TheFixedFieldsAreInTheClientsReadOrder()
    {
        var record = new RecipeRecord
        {
            RecipeId = 1,
            ItemNameStringId = 2,
            ItemImageSetId = 3,
            ItemTintValue = 4,
            ItemDescriptionStringId = 5,
            Reserved = 6,
            BundleCount = 7,
            SortOrdinal = 8,
            MembersOnly = true,
            FilterType = 9,
            OutputItemDefinitionId = 10,
        };

        byte[] bytes = Write(w => record.WriteTo(w));

        for (int i = 0; i < 8; i++)
        {
            Assert.Equal((uint)(i + 1), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4)));
        }

        Assert.Equal(1, bytes[32]);                                                     // MembersOnly
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(33)));    // FilterType
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(37)));      // component count
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(41)));   // ItemId, last
    }

    /// <summary>
    /// The ingredient's <c>RequiredCount</c> is <c>elem+0x20</c>, which is the <b>seventh</b> u32 on
    /// the wire, and its item id is <c>elem+0x04</c>, which is the <b>last</b>. Both come from
    /// <c>FUN_1415119f0</c>'s <c>puVar10[8]</c> and <c>puVar10[1]</c>.
    /// </summary>
    [Fact]
    public void TheIngredientsRequiredCountAndItemIdLandWhereTheRowBuilderReadsThem()
    {
        var component = new RecipeComponentRecord
        {
            Key = 3499,
            ItemNameStringId = 0x11,
            ItemImageSetId = 0x22,
            Reserved = 0x33,
            ItemDescriptionStringId = 0x44,
            ItemLocateDescriptionStringId = 0x55,
            RequiredCount = 2,
            LiveCounts = RecipeComponentRecord.PackLiveCounts(9, 4),
            RecipeType = 0x66,
            ItemDefinitionId = 3499,
        };

        byte[] bytes = Write(component.WriteTo);

        Assert.Equal(RecipeComponentRecord.Length, bytes.Length);
        Assert.Equal(3499u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0)));    // key
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(6 * 4)));   // RequiredCount
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(7 * 4)));   // countInInventory
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32)));      // quantityInProximity
        Assert.Equal(0x66u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(36)));   // RecipeType
        Assert.Equal(3499u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));   // ItemId, last
    }

    /// <summary><c>26 01 Recipe.Add</c> is the two-byte header plus the bare record - the arm
    /// rejects a single trailing byte.</summary>
    [Fact]
    public void RecipeAddIsTwoBytesPlusTheBareRecord()
    {
        var packet = new RecipeAdd(new RecipeRecord { RecipeId = 2423 });

        byte[] bytes = Write(packet.WriteTo);

        Assert.Equal(47, bytes.Length);
        Assert.Equal(packet.Length, bytes.Length);
        Assert.Equal(0x26, bytes[0]);
        Assert.Equal(0x01, bytes[1]);
        Assert.Equal(2423u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(2)));
    }

    /// <summary>
    /// <c>26 09 Recipe.List</c> carries the identical bytes the self record's <c>0x11a</c> field
    /// carries, because both are <c>FUN_140a54bc0</c>. This equality is the whole reason the two
    /// delivery stages of <see cref="CraftingOptions"/> can be ordered safely: what stage 1 proves,
    /// stage 2 reuses unchanged.
    /// </summary>
    [Fact]
    public void RecipeListIsTheSelfRecordListBehindATwoByteHeader()
    {
        IReadOnlyList<RecipeRecord> recipes = CraftingCatalog.ToRecords();

        byte[] packet = Write(new RecipeList(recipes).WriteTo);
        byte[] selfList = Write(w => RecipeRecord.WriteList(w, recipes));

        Assert.Equal(0x26, packet[0]);
        Assert.Equal(0x09, packet[1]);
        Assert.Equal(selfList, packet[2..]);
        Assert.Equal(RecipeRecord.ListLength(recipes), selfList.Length);
    }

    /// <summary>The three short arms are fixed-length and the lengths are exact.</summary>
    [Fact]
    public void TheShortArmsHaveTheExactLengthsTheirReadersConsume()
    {
        Assert.Equal(18, Write(new RecipeComponentUpdate(2423, 0, 5).WriteTo).Length);
        Assert.Equal(7, Write(new RecipeRemove(2423).WriteTo).Length);
        Assert.Equal(7, Write(new RecipeCraftingStatus(2423, RecipeCraftingState.Crafting).WriteTo).Length);

        byte[] status = Write(new RecipeCraftingStatus(3378, RecipeCraftingState.Crafting).WriteTo);
        Assert.Equal([0x26, 0x0a], status[..2]);
        Assert.Equal(3378u, BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(2)));
        // RecipeManager.as: isCrafting = int(event.data[1]) == 0.
        Assert.Equal(0, status[6]);
        Assert.Equal(1, Write(new RecipeCraftingStatus(3378, RecipeCraftingState.Ready).WriteTo)[6]);
    }

    /// <summary>
    /// <c>26 02</c>'s u64 is the two live counts packed low/high - the one [INF] left in the record.
    /// Pinning the packing here means a live correction is a one-line change with a failing test to
    /// point at it.
    /// </summary>
    [Fact]
    public void ComponentUpdatePacksCountInInventoryLow()
    {
        byte[] bytes = Write(RecipeComponentUpdate.Counts(2423, 1, countInInventory: 7, quantityInProximity: 3).WriteTo);

        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(10)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(14)));
    }
}
