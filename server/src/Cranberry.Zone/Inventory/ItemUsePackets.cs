using Cranberry.Protocol;

namespace Cranberry.Zone.Inventory;

/// <summary>One first-list property: a u32 ID followed by four value bytes.</summary>
public readonly record struct ItemUseParameter(uint ParameterId, uint Value);

/// <summary>Opcode constants for the item-action channel.</summary>
public static class ItemUseOpcodes
{
    public const byte ItemsBase = 0xac;
    public const byte RequestUseItemSub = 0x2c;
}

/// <summary>
/// August 2017 Items.RequestUseItem (ac/2c). Header reader: 0x140d1b330;
/// property reader: 0x140d1afa0; property writer: 0x140d1a850.
/// See docs/item-use-properties-20260926.md for binary identity and evidence.
/// <code>
/// +2/+6: two u32 header fields; +10: u32 option; +14/+22/+30/+38: four u64 GUIDs;
/// +46: u8 simple flag (any nonzero value means no property lists).
/// Zero flag: five signed i32 counts, followed by their respective entries:
///   1: u32 ID + 4 bytes; 2: u32 ID + 4 bytes; 3: u32 ID + 8 bytes;
///   4: u32 ID + 16 bytes; 5: u32 ID + signed i32 byte length + raw bytes.
/// </code>
/// Negative list counts iterate zero times in the native reader. Negative string lengths and
/// truncated fields are errors. Unsupported properties are validated and skipped; remaining bytes
/// are reported because native exact-exhaustion requirements have not been established.
/// Native manager 0x140d24300 and sender 0x14162a270 establish target at +22 and owner/source at +30.
/// </summary>
/// <param name="ItemCount">
/// Legacy name for the u32 at +2. Sender 0x14162a270 writes 1 here and 0 at +6; this is not quantity.
/// </param>
/// <param name="Count">The first first-list property with ID 1, or zero when absent.</param>
/// <param name="SourceCharacterGuid">Owner of the source item/container, wire offset +30.</param>
/// <param name="TargetCharacterGuid">Resolved action target, wire offset +22.</param>
/// <param name="Parameters">First-list properties in wire order; empty for the simple form.</param>
public sealed record RequestUseItem(
    ulong UnknownA,
    uint ItemUseOptionId,
    ulong CharacterGuid,
    ulong SourceCharacterGuid,
    ulong TargetCharacterGuid,
    ulong ItemGuid,
    bool Simple,
    uint Count,
    int TrailingBytes,
    uint ItemCount = 0,
    uint ReservedA = 0,
    IReadOnlyList<ItemUseParameter>? Parameters = null)
{
    public const int MinimumLength = 47;

    /// <summary>Size with one first-list quantity property and four empty lists.</summary>
    public const int QuantityFormLength = 75;

    /// <summary>Quantity offset only when the first list has one entry with ID 1.</summary>
    public const int CountOffset = 55;

    /// <summary>UI-requested quantity; it need not equal the whole inventory stack.</summary>
    public const uint QuantityParameterId = 1;

    /// <summary>Legacy alias for <see cref="QuantityParameterId"/>.</summary>
    public const uint StackSizeParameterId = QuantityParameterId;

    public ItemUseOptionKind Kind => ItemUseOptionTable.KindOf(ItemUseOptionId);

    /// <summary>Legacy alias: true when the first quantity property is nonzero.</summary>
    public bool HasStackAtClick => StackAtClick != 0;

    /// <summary>Legacy name for the first quantity property, or zero when absent.</summary>
    public uint StackAtClick
    {
        get
        {
            IReadOnlyList<ItemUseParameter> parameters = Parameters ?? [];
            for (int i = 0; i < parameters.Count; i++)
            {
                if (parameters[i].ParameterId == QuantityParameterId)
                {
                    return parameters[i].Value;
                }
            }

            return 0;
        }
    }

    public static bool Matches(ReadOnlySpan<byte> payload) =>
        payload.Length >= MinimumLength
        && payload[0] == ItemUseOpcodes.ItemsBase
        && payload[1] == ItemUseOpcodes.RequestUseItemSub;

    public static RequestUseItem Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < MinimumLength)
        {
            throw new PacketFormatException(
                $"Items.RequestUseItem needs at least {MinimumLength} bytes, got {payload.Length}.");
        }

        var reader = new PacketReader(payload[2..]);
        uint itemCount = reader.ReadUInt32();
        uint reservedA = reader.ReadUInt32();
        uint optionId = reader.ReadUInt32();
        ulong character = reader.ReadUInt64();
        ulong target = reader.ReadUInt64();
        ulong source = reader.ReadUInt64();
        ulong item = reader.ReadUInt64();
        bool simple = reader.ReadBool();

        var parameters = new List<ItemUseParameter>();
        if (!simple)
        {
            int entries = ReadListCount(ref reader, entryBytes: 8);
            for (int i = 0; i < entries; i++)
            {
                parameters.Add(new ItemUseParameter(reader.ReadUInt32(), reader.ReadUInt32()));
            }

            SkipFixedProperties(ref reader, entryBytes: 8);
            SkipFixedProperties(ref reader, entryBytes: 12);
            SkipFixedProperties(ref reader, entryBytes: 20);

            int strings = ReadListCount(ref reader, entryBytes: 8);
            for (int i = 0; i < strings; i++)
            {
                _ = reader.ReadUInt32(); // property ID
                int byteLength = reader.ReadInt32();
                reader.Skip(byteLength); // Rejects negative/truncated lengths without decoding bytes.
            }
        }

        // Keep the existing first-match rule; other lists do not supply this parameter.
        uint count = 0;
        for (int i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].ParameterId == QuantityParameterId)
            {
                count = parameters[i].Value;
                break;
            }
        }

        return new RequestUseItem(
            ((ulong)reservedA << 32) | itemCount,
            optionId,
            character,
            source,
            target,
            item,
            simple,
            count,
            reader.Remaining,
            itemCount,
            reservedA,
            parameters);
    }

    private static int ReadListCount(ref PacketReader reader, int entryBytes)
    {
        int count = reader.ReadInt32();
        if (count <= 0) return 0;
        // Bound every loop/skip by bytes actually present, without overflowing count * width.
        if (count > reader.Remaining / entryBytes)
        {
            throw new PacketFormatException(
                $"Items.RequestUseItem property count {count} exceeds {reader.Remaining} remaining bytes.");
        }

        return count;
    }

    private static void SkipFixedProperties(ref PacketReader reader, int entryBytes)
    {
        int count = ReadListCount(ref reader, entryBytes);
        reader.Skip(count * entryBytes);
    }
}
