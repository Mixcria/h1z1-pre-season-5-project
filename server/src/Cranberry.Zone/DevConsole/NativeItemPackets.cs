using Cranberry.Protocol;

namespace Cranberry.Zone.DevConsole;

/// <summary>
/// The original August /item executor FUN_14128c280 sends dedicated packets.
/// Add uses FUN_141267110 -> FUN_14126ba40; list uses FUN_141267770 -> FUN_14126c3f0.
/// These layouts come from the client's serializers, not an ExecuteCommand alias.
/// </summary>
public static class NativeItemPackets
{
    public const ushort AddSubOpcode = 0x03ea;
    public const ushort ListSubOpcode = 0x043c;
    public const ushort DeleteSubOpcode = 0x03eb;

    public static bool Matches(ReadOnlySpan<byte> payload, ushort subOpcode) => payload.Length >= 3
        && payload[0] is ConsoleOpcodes.CommandBase or ConsoleOpcodes.AdminBase
        && payload[1] == (byte)subOpcode && payload[2] == (byte)(subOpcode >> 8);
}

/// <summary>09 EA 03 | u32 definition | u32 tint | u32 count | u64 target | u32 rental term.</summary>
public readonly record struct NativeItemAdd(uint DefinitionId, uint TintId, uint Count, ulong TargetGuid, uint RentalTerm)
{
    public const int Length = 27;

    public static NativeItemAdd Parse(ReadOnlySpan<byte> payload)
    {
        if (!NativeItemPackets.Matches(payload, NativeItemPackets.AddSubOpcode) || payload.Length != Length)
            throw new PacketFormatException($"Native /item add requires {Length} bytes, received {payload.Length}.");
        var reader = new PacketReader(payload);
        reader.Skip(3);
        return new(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt64(), reader.ReadUInt32());
    }
}

/// <summary>09 3C 04 | u64 target | u8 mode. The original /item list executor sends mode zero.</summary>
public readonly record struct NativeItemList(ulong TargetGuid, byte Mode)
{
    public const int Length = 12;

    public static NativeItemList Parse(ReadOnlySpan<byte> payload)
    {
        if (!NativeItemPackets.Matches(payload, NativeItemPackets.ListSubOpcode) || payload.Length != Length)
            throw new PacketFormatException($"Native /item list requires {Length} bytes, received {payload.Length}.");
        var reader = new PacketReader(payload);
        reader.Skip(3);
        return new(reader.ReadUInt64(), reader.ReadByte());
    }
}

/// <summary>09 EB 03 | u64 instance | u64 target. FUN_141267330 -> FUN_14126bd90.</summary>
public readonly record struct NativeItemDelete(ulong ItemGuid, ulong TargetGuid)
{
    public const int Length = 19;

    public static NativeItemDelete Parse(ReadOnlySpan<byte> payload)
    {
        if (!NativeItemPackets.Matches(payload, NativeItemPackets.DeleteSubOpcode) || payload.Length != Length)
            throw new PacketFormatException($"Native /item delete requires {Length} bytes, received {payload.Length}.");
        var reader = new PacketReader(payload);
        reader.Skip(3);
        return new(reader.ReadUInt64(), reader.ReadUInt64());
    }
}

/// <summary>
/// 15 11 00 | u64 instance | u32 count. FUN_140ba5dd0 -> FUN_140ba6160 writes a u16 sub-opcode,
/// unlike the ordinary inventory UI's 0xac namespace. Verified from the August PE serializer.
/// </summary>
public readonly record struct NativeItemDrop(ulong ItemGuid, uint Count)
{
    public const byte BaseOpcode = 0x15;
    public const int Length = 15;

    public static bool Matches(ReadOnlySpan<byte> payload) => payload.Length >= 3
        && payload[0] == BaseOpcode && payload[1] == 0x11 && payload[2] == 0;

    public static NativeItemDrop Parse(ReadOnlySpan<byte> payload)
    {
        if (!Matches(payload) || payload.Length != Length)
            throw new PacketFormatException($"Native /item drop requires {Length} bytes, received {payload.Length}.");
        var reader = new PacketReader(payload);
        reader.Skip(3);
        return new(reader.ReadUInt64(), reader.ReadUInt32());
    }
}
