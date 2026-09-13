using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>August 140d85d90/140d849f0/140d84cc0: df + u16 1 + keyed item/count map.</summary>
public sealed record GrinderExchangeRequest(IReadOnlyList<AccountRewardRow> Items)
{
    public const int MaximumItems = 100;

    public static GrinderExchangeRequest Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (reader.ReadByte() != ZoneOpcodes.GrinderBase || reader.ReadUInt16() != 1)
            throw new PacketFormatException("Invalid Grinder exchange opcode.");
        int count = reader.ReadInt32();
        if (count <= 0 || count > MaximumItems || reader.Remaining != count * 8)
            throw new PacketFormatException("Invalid Grinder item map.");
        var items = new List<AccountRewardRow>(count);
        var keys = new HashSet<uint>();
        ulong total = 0;
        for (int i = 0; i < count; i++)
        {
            uint item = reader.ReadUInt32(), quantity = reader.ReadUInt32();
            total += quantity;
            if (item == 0 || quantity == 0 || total > MaximumItems || !keys.Add(item))
                throw new PacketFormatException("Invalid Grinder item or quantity.");
            items.Add(new(item, quantity));
        }
        return new(items);
    }
}

/// <summary>140b015d0 strictly reads one u32; 140d85f30 publishes it to the Grinder reward datasource.</summary>
public sealed record GrinderExchangeResponse(uint Scrap)
{
    public void WriteTo(PacketWriter writer)
    {
        if (Scrap > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(Scrap));
        writer.WriteByte(ZoneOpcodes.GrinderBase);
        writer.WriteUInt16(2);
        writer.WriteUInt32(Scrap);
    }
}
