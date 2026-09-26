using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Cranberry.Zone.Vehicles;

/// <summary>
/// The latest reported native vehicle animation values. Missing delta keys retain their values;
/// these maps do not change authoritative health, fuel, parts, or control ownership.
/// </summary>
public sealed class VehicleAnimationSnapshot
{
    public static VehicleAnimationSnapshot Empty { get; } = new(0, [], []);

    private VehicleAnimationSnapshot(uint value, IReadOnlyList<VehicleStateEntry> listA,
        IReadOnlyList<VehicleStateEntry> listB)
    {
        Value = value;
        ListA = listA;
        ListB = listB;
    }

    /// <summary>Opaque native state field; the observed client sender writes zero.</summary>
    public uint Value { get; }
    public IReadOnlyList<VehicleStateEntry> ListA { get; }
    public IReadOnlyList<VehicleStateEntry> ListB { get; }

    /// <summary>Validate and copy the whole delta before producing a new immutable snapshot.</summary>
    public bool TryApply(VehicleStateData delta, out VehicleAnimationSnapshot next)
    {
        ArgumentNullException.ThrowIfNull(delta);
        next = this;
        if (!VehicleAnimationCodec.TryCopyEntries(delta.ListA, flagList: true, out var flags)
            || !VehicleAnimationCodec.TryCopyEntries(delta.ListB, flagList: false, out var values))
            return false;

        next = new VehicleAnimationSnapshot(delta.Value, Merge(ListA, flags), Merge(ListB, values));
        return true;
    }

    public VehicleStateData ToPacket(ulong vehicleGuid) => new(vehicleGuid, Value, ListA, ListB);

    private static IReadOnlyList<VehicleStateEntry> Merge(IReadOnlyList<VehicleStateEntry> previous,
        IReadOnlyList<VehicleStateEntry> delta)
    {
        var entries = new SortedDictionary<uint, byte>();
        foreach (var entry in previous) entries[entry.Key] = entry.Value;
        foreach (var entry in delta) entries[entry.Key] = entry.Value;
        return Array.AsReadOnly(entries.Select(entry => new VehicleStateEntry(entry.Key, entry.Value)).ToArray());
    }
}

/// <summary>
/// August native sender/consumer bounds, not original server acceptance rules.
/// See docs/vehicle-animation-state-20260926.md for addresses and compatibility limits.
/// </summary>
internal static class VehicleAnimationCodec
{
    private const int FlagCount = 30;
    private const int ValueCount = 15;

    internal static bool TryParse(ReadOnlySpan<byte> payload,
        [NotNullWhen(true)] out VehicleStateData? state)
    {
        state = null;
        if (payload.Length < VehicleStateData.MinimumLength
            || payload[0] != VehicleStateData.Opcode || payload[1] != VehicleStateData.SubOpcode)
            return false;

        int offset = 14;
        if (!TryReadEntries(payload, ref offset, flagList: true, out var flags)
            || !TryReadEntries(payload, ref offset, flagList: false, out var values))
            return false;

        // The native dispatcher checks stream errors, not exhaustion. Unknown trailers are
        // accepted but never copied into a relay packet.
        state = new VehicleStateData(BinaryPrimitives.ReadUInt64LittleEndian(payload[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[10..]),
            Array.AsReadOnly(flags), Array.AsReadOnly(values));
        return true;
    }

    internal static bool TryCopyEntries(IReadOnlyList<VehicleStateEntry>? source, bool flagList,
        out VehicleStateEntry[] entries)
    {
        entries = [];
        int count = source?.Count ?? 0;
        if (count > (flagList ? FlagCount : ValueCount)) return false;
        var copy = new VehicleStateEntry[count];
        uint seen = 0;
        for (int index = 0; index < count; index++)
        {
            var entry = source![index];
            if (!ValidEntry(entry, flagList, ref seen)) return false;
            copy[index] = entry;
        }
        entries = copy;
        return true;
    }

    private static bool TryReadEntries(ReadOnlySpan<byte> payload, ref int offset, bool flagList,
        out VehicleStateEntry[] entries)
    {
        entries = [];
        if (payload.Length - offset < sizeof(int)) return false;
        int count = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
        offset += sizeof(int);
        if (count <= 0) return true; // Native signed counts iterate only when positive.
        if (count > (flagList ? FlagCount : ValueCount)
            || count > (payload.Length - offset) / VehicleStateEntry.Length)
            return false;

        var parsed = new VehicleStateEntry[count];
        uint seen = 0;
        for (int index = 0; index < count; index++)
        {
            var entry = new VehicleStateEntry(BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]),
                payload[offset + sizeof(uint)]);
            if (!ValidEntry(entry, flagList, ref seen)) return false;
            parsed[index] = entry;
            offset += VehicleStateEntry.Length;
        }
        entries = parsed;
        return true;
    }

    private static bool ValidEntry(VehicleStateEntry entry, bool flagList, ref uint seen)
    {
        if (entry.Key >= (uint)(flagList ? FlagCount : ValueCount)) return false;
        int value = flagList ? entry.Value : unchecked((sbyte)entry.Value);
        if (flagList ? value > 1 : value is < -100 or > 100) return false;
        uint bit = 1u << (int)entry.Key;
        if ((seen & bit) != 0) return false; // Native sender maps contain unique keys.
        seen |= bit;
        return true;
    }
}
