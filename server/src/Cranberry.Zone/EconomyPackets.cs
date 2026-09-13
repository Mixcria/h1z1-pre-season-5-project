using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>August FUN_140d0eb20: ac13 + owned instance u64 + removed count u32.</summary>
public sealed record RemoveAccountItem(ulong ItemInstanceId, uint Count = 1)
{
    public void WriteTo(PacketWriter writer)
    {
        if (ItemInstanceId == 0 || Count == 0 || Count > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(Count));
        writer.WriteByte(ZoneOpcodes.ItemsBase);
        writer.WriteByte(0x13);
        writer.WriteUInt64(ItemInstanceId);
        writer.WriteUInt32(Count);
    }
}

/// <summary>August FUN_140d0f940 / FUN_140a2b830: the account record plus one flag byte.</summary>
public sealed record UpdateAccountItem(ulong ItemInstanceId, uint AccountItemId,
    uint StackCount, uint ItemType = 0, byte Flags = 0)
{
    public void WriteTo(PacketWriter writer)
    {
        if (ItemInstanceId == 0 || AccountItemId == 0 || StackCount > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(StackCount));
        writer.WriteByte(ZoneOpcodes.ItemsBase);
        writer.WriteByte(0x14);
        writer.WriteUInt64(ItemInstanceId);
        writer.WriteUInt32(AccountItemId);
        writer.WriteUInt32(ItemType);
        writer.WriteUInt32(StackCount);
        writer.WriteByte(Flags);
    }
}

/// <summary>August FUN_140d21240: a positive increment animates EVENT_REWARD_SCRAP.</summary>
public sealed record ReportRewardScrap(uint AmountGained)
{
    public void WriteTo(PacketWriter writer)
    {
        if (AmountGained == 0 || AmountGained > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(AmountGained));
        writer.WriteByte(ZoneOpcodes.ItemsBase);
        writer.WriteByte(0x20);
        writer.WriteUInt32(AmountGained);
    }
}

public readonly record struct AccountRewardRow(uint AccountItemId, uint Count = 1);

/// <summary>
/// August FUN_140d21070 calls FUN_140d1c4b0 twice: winning rewards followed by possible
/// rewards, each an i32 count and pairs of item-definition id/count. A preview has no winner.
/// </summary>
public sealed record ReportRewardCrateContents(IReadOnlyList<AccountRewardRow> WinningRewards,
    IReadOnlyList<AccountRewardRow> PossibleRewards)
{
    public void WriteTo(PacketWriter writer)
    {
        if (WinningRewards.Count + PossibleRewards.Count == 0)
            throw new ArgumentException("The native reward panel ignores two empty lists.");
        Validate(WinningRewards);
        Validate(PossibleRewards);
        writer.WriteByte(ZoneOpcodes.ItemsBase);
        writer.WriteByte(0x1f);
        WriteRows(writer, WinningRewards);
        WriteRows(writer, PossibleRewards);
    }

    private static void Validate(IReadOnlyList<AccountRewardRow> rows)
    {
        if (rows.Count > 2048 || rows.Any(r => r.AccountItemId == 0 || r.Count == 0 || r.Count > int.MaxValue))
            throw new ArgumentException("Invalid native account reward list.");
    }

    private static void WriteRows(PacketWriter writer, IReadOnlyList<AccountRewardRow> rows)
    {
        writer.WriteInt32(rows.Count);
        foreach (AccountRewardRow row in rows)
        {
            writer.WriteUInt32(row.AccountItemId);
            writer.WriteUInt32(row.Count);
        }
    }
}

/// <summary>
/// Native FUN_140d1d420 and UIBindingItem.RequestUseAccountItem (FUN_14121c5c0).
/// Phase 1 is a request, 2 a response; the first dword is not an item count. The five typed
/// maps have u32 keys and respectively u32/u32/u64/vec4/string values. Preserve the original
/// bytes for a response so unconsumed presentation parameters retain their native shape.
/// </summary>
public sealed record RequestUseAccountItem(uint ItemUseOptionId, uint AccountItemId,
    IReadOnlyDictionary<uint, uint> UInt32Parameters, IReadOnlyDictionary<uint, ulong> UInt64Parameters,
    byte[] Payload)
{
    public const byte SubOpcode = 0x2d;
    public const int MinimumLength = 19;
    public uint StackAtClick => UInt32Parameters.GetValueOrDefault(1u);

    public static RequestUseAccountItem Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        EconomyPacketReader.ReadRequestHeader(ref reader, SubOpcode);
        uint useOption = reader.ReadUInt32();
        uint accountItem = reader.ReadUInt32();
        if (useOption == 0 || accountItem == 0)
            throw new PacketFormatException("Missing account use option or item.");
        byte simple = reader.ReadByte();
        var integers = new Dictionary<uint, uint>();
        var guids = new Dictionary<uint, ulong>();
        if (simple == 0)
        {
            int count = EconomyPacketReader.ReadMapCount(ref reader, 8);
            for (int i = 0; i < count; i++)
                if (!integers.TryAdd(reader.ReadUInt32(), reader.ReadUInt32()))
                    throw new PacketFormatException("Duplicate account-use integer parameter.");
            count = EconomyPacketReader.ReadMapCount(ref reader, 8);
            for (int i = 0; i < count; i++)
            {
                reader.ReadUInt32();
                reader.ReadUInt32();
            }
            count = EconomyPacketReader.ReadMapCount(ref reader, 12);
            for (int i = 0; i < count; i++)
                if (!guids.TryAdd(reader.ReadUInt32(), reader.ReadUInt64()))
                    throw new PacketFormatException("Duplicate account-use guid parameter.");
            count = EconomyPacketReader.ReadMapCount(ref reader, 20);
            reader.ReadBytes(count * 20);
            count = EconomyPacketReader.ReadMapCount(ref reader, 8);
            for (int i = 0; i < count; i++)
            {
                reader.ReadUInt32();
                int length = reader.ReadInt32();
                if (length < 0 || length > 1024)
                    throw new PacketFormatException("Invalid account-use string parameter length.");
                reader.ReadBytes(length);
            }
        }
        else if (simple != 1)
            throw new PacketFormatException("Invalid account-use simple flag.");
        EconomyPacketReader.RequireEnd(ref reader);
        return new(useOption, accountItem, integers, guids, payload.ToArray());
    }

    public void WriteResponse(PacketWriter writer, uint result = 0) =>
        EconomyPacketReader.WriteResponse(writer, Payload, result);
}

/// <summary>FUN_140d1d270/FUN_140d1c270: phase/result followed by keyed (item,count) rows.</summary>
public sealed record RequestOpenAccountCrate(IReadOnlyList<AccountRewardRow> Crates, byte[] Payload)
{
    public const byte SubOpcode = 0x39;
    public const int MaximumCrates = 100; // Server request limit, shared with the atomic operation.
    public static RequestOpenAccountCrate Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        EconomyPacketReader.ReadRequestHeader(ref reader, SubOpcode);
        int count = EconomyPacketReader.ReadMapCount(ref reader, 12, MaximumCrates);
        if (count == 0)
            throw new PacketFormatException("Invalid number of crate types.");
        var crates = new List<AccountRewardRow>(count);
        var keys = new HashSet<uint>();
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            uint key = reader.ReadUInt32();
            uint item = reader.ReadUInt32();
            uint quantity = reader.ReadUInt32();
            total += quantity;
            if (key == 0 || key != item || quantity == 0 || total > MaximumCrates || !keys.Add(key))
                throw new PacketFormatException("Invalid, duplicate or excessive crate request.");
            crates.Add(new(item, quantity));
        }
        EconomyPacketReader.RequireEnd(ref reader);
        return new(crates.AsReadOnly(), payload.ToArray());
    }

    public void WriteResponse(PacketWriter writer, uint result = 0) =>
        EconomyPacketReader.WriteResponse(writer, Payload, result);
}

/// <summary>FUN_140d1ab00/FUN_140d21500: exactly phase/result/crate-item-id, 14 bytes.</summary>
public sealed record RequestPreviewAccountCrateRewards(uint CrateItemId, byte[] Payload)
{
    public const byte SubOpcode = 0x3a;
    public static RequestPreviewAccountCrateRewards Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        EconomyPacketReader.ReadRequestHeader(ref reader, SubOpcode);
        uint item = reader.ReadUInt32();
        if (item == 0)
            throw new PacketFormatException("Missing preview crate.");
        EconomyPacketReader.RequireEnd(ref reader);
        return new(item, payload.ToArray());
    }

    public void WriteResponse(PacketWriter writer, uint result = 0) =>
        EconomyPacketReader.WriteResponse(writer, Payload, result);
}

internal static class EconomyPacketReader
{
    internal static void ReadRequestHeader(ref PacketReader reader, byte sub)
    {
        if (reader.ReadByte() != ZoneOpcodes.ItemsBase || reader.ReadByte() != sub
            || reader.ReadUInt32() != 1 || reader.ReadUInt32() != 0)
            throw new PacketFormatException("Invalid account economy request header.");
    }

    internal static int ReadMapCount(ref PacketReader reader, int minimumRowBytes, int maximumCount = 64)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximumCount || count > reader.Remaining / minimumRowBytes)
            throw new PacketFormatException("Invalid account economy map length.");
        return count;
    }

    internal static void RequireEnd(ref PacketReader reader)
    {
        if (!reader.AtEnd)
            throw new PacketFormatException("Trailing account economy request data.");
    }

    internal static void WriteResponse(PacketWriter writer, ReadOnlySpan<byte> request, uint result)
    {
        if (request.Length < 10 || result >= 0x33)
            throw new ArgumentOutOfRangeException(nameof(result));
        writer.WriteByte(request[0]);
        writer.WriteByte(request[1]);
        writer.WriteUInt32(2);
        writer.WriteUInt32(result);
        writer.WriteRaw(request[10..]);
    }
}
