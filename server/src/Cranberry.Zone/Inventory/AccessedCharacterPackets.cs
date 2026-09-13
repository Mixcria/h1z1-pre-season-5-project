using Cranberry.Protocol;

namespace Cranberry.Zone.Inventory;

/// <summary>
/// <c>AccessedCharacter.BeginCharacterAccess</c> for the August 2017 protocol. The August client
/// registers this family at <c>0xf0</c> (the March/reference build used <c>0xf1</c>).
/// <para>
/// For a self grant both guids must be the player's own guid. The client's self-apply path writes
/// its mutable-inventory access flag to <c>objectCharacterGuid == mutatorCharacterGuid</c>; without
/// this packet, equips, moves and drags are rejected locally and never reach the server.
/// </para>
/// </summary>
public sealed record BeginCharacterAccess(
    ulong ObjectCharacterGuid,
    ulong MutatorCharacterGuid,
    bool DontOpenInventory = true,
    IReadOnlyCollection<InventoryItem>? Items = null)
{
    public const byte Opcode = ZoneOpcodes.AccessedCharacterBase;
    public const ushort SubOpcode = 0x0001;
    public const int Length = 32;

    /// <summary>Create the self-access grant used when the inventory window opens.</summary>
    public BeginCharacterAccess(ulong characterGuid)
        : this(characterGuid, characterGuid)
    {
    }

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteUInt64(ObjectCharacterGuid);
        writer.WriteUInt64(MutatorCharacterGuid);
        writer.WriteBool(DontOpenInventory);

        // itemsData is a bytes-with-length block. Its empty schema is still eight bytes: an empty
        // item array followed by unknownDword1. A zero-length block is malformed for this parser.
        writer.WriteUInt32((uint)(8 + (Items?.Count ?? 0) * (InventoryItem.BaseLength + 1)));
        writer.WriteUInt32((uint)(Items?.Count ?? 0));
        foreach (var item in Items ?? [])
        {
            item.WriteTo(writer);
            writer.WriteBool(false); // itemsData entry trailer, separate from ItemAdd's class tail.
        }
        writer.WriteUInt32(Items is null ? 0u : 92u);
    }
}

/// <summary>
/// <c>AccessedCharacter.EndCharacterAccess</c> (<c>f0 02 00</c>) for a window close.
/// The client also sends this shape, so <see cref="Matches"/> and <see cref="Parse"/> are shared by
/// the inbound lifecycle handler.
/// </summary>
public sealed record EndCharacterAccess(ulong CharacterGuid)
{
    public const byte Opcode = ZoneOpcodes.AccessedCharacterBase;
    public const ushort SubOpcode = 0x0002;
    public const int Length = 11;

    public static bool Matches(ReadOnlySpan<byte> payload) =>
        payload.Length >= Length
        && payload[0] == Opcode
        && payload[1] == (byte)SubOpcode
        && payload[2] == 0;

    public static EndCharacterAccess Parse(ReadOnlySpan<byte> payload)
    {
        if (!Matches(payload))
        {
            throw new ArgumentException("Not an August EndCharacterAccess packet.", nameof(payload));
        }

        var reader = new PacketReader(payload[3..]);
        return new EndCharacterAccess(reader.ReadUInt64());
    }

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
    }
}
