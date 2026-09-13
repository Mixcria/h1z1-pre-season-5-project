using Cranberry.Protocol;

namespace Cranberry.Zone.Inventory;

// Container family 0xc8 (200) - the packets that give a granted item somewhere to live.
// Derivation: docs/41-inventory-slots.md §3a. Everything below is DERIVED from the August binary
// (C:\Aug2017\ghidra\aug.gpr, build 0.0.118.208059); nothing has been on the wire yet, so nothing
// here is LIVE-VERIFIED (D29, docs/32).
//
// Why this family is not in ZoneOpcodes.g.cs: that file is generated from the client's own packet
// id registration table (registrations-1148.json), and 0xc8 is ABSENT from it - the table jumps
// 0xc7 ResourcesBase -> 0xc9 ConstructionBase and no cPacketIdContainerBase string exists in the
// binary. The family is nevertheless live and dispatched, which makes the registration table an
// incomplete list of live families rather than an authority (docs/41 §3a, a standing correction
// for every lane):
//
//   FUN_140af3950 (the zone dispatcher, docs/06), line 1938 of the lightweight-dispatch decompile:
//       case 200:
//         thunk_FUN_140d7f450(DAT_143f69f60 + 0x10320, param_3, param_4);
//
// player+0x10320 is the same container manager the self record's step-112 list loads
// (docs/10 step 112: call FUN_140a55df0 -> base+0x10320), so this family and SelfRecord.cs:532
// write the same object.

/// <summary>
/// Base opcode of the container family. <b>Not</b> in <c>ZoneOpcodes.g.cs</c> because the client's
/// registration table omits it; see the file header for the dispatcher proof.
/// </summary>
public static class ContainerOpcodes
{
    /// <summary><c>FUN_140af3950</c> case 200.</summary>
    public const byte ContainerBase = 0xc8;

    /// <summary>Move one item, client to server.</summary>
    public const ushort MoveItemSub = 0x0001;

    /// <summary>Replace the character's whole container set (<c>FUN_140d7ed90</c>).</summary>
    public const ushort InitContainersSub = 0x0002;

    /// <summary>One error code, printed to the client console (<c>FUN_140d7eb60</c>).</summary>
    public const ushort ErrorSub = 0x0003;

    /// <summary>Replace one container by guid (<c>FUN_140d7f580</c>).</summary>
    public const ushort UpdateContainerSub = 0x0006;

    /// <summary>
    /// The sub-dispatcher <c>FUN_140d7f450</c> reads <c>u8 opcode; u16 sub</c> and rejects anything
    /// shorter than this. The sub is a <b>u16</b> for this family, unlike the Loadouts family's u8.
    /// </summary>
    public const int MinimumLength = 11;
}

/// <summary>
/// <c>Container.MoveItem</c>, the client's inventory/proximity drag request. The August family is
/// shifted from the March server's <c>0xc9</c> to <c>0xc8</c>; the u16 sub and 40-byte body are the
/// same oracle-backed shape:
/// <code>
/// c8 01 00 | u64 destinationContainer | u64 sourceCharacter | u64 itemGuid |
///             u64 targetCharacter | u32 count | i32 destinationSlot
/// </code>
/// A proximity row names the ground world object as <see cref="SourceCharacterGuid"/>.
/// </summary>
public sealed record MoveItemRequest(
    ulong ContainerGuid,
    ulong SourceCharacterGuid,
    ulong ItemGuid,
    ulong TargetCharacterGuid,
    uint Count,
    int NewSlotId)
{
    public const byte Opcode = ContainerOpcodes.ContainerBase;
    public const ushort SubOpcode = ContainerOpcodes.MoveItemSub;
    public const int Length = 43;

    public static bool Matches(ReadOnlySpan<byte> payload) =>
        payload.Length >= Length
        && payload[0] == Opcode
        && payload[1] == 0x01
        && payload[2] == 0x00;

    public static MoveItemRequest Parse(ReadOnlySpan<byte> payload)
    {
        if (!Matches(payload))
        {
            throw new PacketFormatException(
                $"Container.MoveItem needs {Length} bytes and c8/0001, got {payload.Length} bytes.");
        }

        var reader = new PacketReader(payload[3..]);
        return new MoveItemRequest(
            reader.ReadUInt64(),
            reader.ReadUInt64(),
            reader.ReadUInt64(),
            reader.ReadUInt64(),
            reader.ReadUInt32(),
            reader.ReadInt32());
    }
}

/// <summary>
/// The client's own container error enum, printed as <c>Container Error: %s</c> by
/// <c>FUN_140d7eb60</c>. Cranberry answers a refused pickup or move with one of these rather than
/// dropping it silently - they are the client's vocabulary for exactly the failures the server
/// produces (docs/41 §3a).
/// </summary>
public enum ContainerErrorCode : uint
{
    None = 0,
    ContainerInUse = 1,
    WrongItemType = 2,
    UnknownContainer = 3,
    UnknownContainerSlot = 4,
    SlotDoesNotContainItem = 5,
    InteractionValidationFailed = 6,
}

/// <summary>
/// One container as the client reads it - <c>FUN_140a37290</c>, cross-checked field for field
/// against the client's own debug format string at <c>0x143149490</c>:
/// <c>"Container GUID: %llu, DefinitionId: %d, Associated Character: %llu[%s], Slots: %d%s,
/// Bulk(Used/Max): %d/%d"</c> (consumed by <c>FUN_140d7ef80</c>).
/// <code>
/// u64  containerGuid              -> +0x00
/// u32  containerDefinitionId      -> +0x08   (a row of ContainerDefinitions.txt)
/// u64  associatedCharacterGuid    -> +0x10
/// u32  slotCount                  -> +0x2c   (the debug's "Slots: %d")
/// i32  n + n x { u32 key; 62-byte item record }   -> +0x48   (FUN_140a4f410)
/// u8   flagA                      -> +0x29   [LEAD - docs/41 L1, meaning unknown]
/// u32  maxBulk                    -> +0x30   (the debug's "Max")
/// u32  unknown                    -> +0x34   [LEAD - docs/41 L1; MAX_SLOT_BULK or a weight]
/// u32  bulkUsed                   -> +0x38   (the debug's "Used")
/// u8   flagB                      -> +0x3c   [LEAD - docs/41 L1]
/// </code>
/// The per-item element is the <em>same</em> <c>FUN_140a3aa60</c> record
/// (<see cref="InventoryItem"/>) that <c>ClientUpdate.ItemAdd</c> and <c>ProximateItems</c> carry,
/// and like <c>ProximateItems</c> it has <b>no item-class tail</b> - the element is a plain record,
/// not a class-dispatched item object (docs/13 §3c, docs/41 §3a).
/// </summary>
public sealed record ContainerRecord(
    ulong ContainerGuid,
    uint ContainerDefinitionId,
    ulong AssociatedCharacterGuid,
    uint SlotCount,
    IReadOnlyList<ContainerItemEntry> Items,
    uint MaxBulk,
    uint BulkUsed,
    bool FlagA = true,
    uint UnknownBulkField = 0,
    bool FlagB = true)
{
    /// <summary>
    /// Bytes written when the item list is empty: <c>8 + 4 + 8 + 4</c> head, <c>4</c> list count,
    /// then <c>1 + 4 + 4 + 4 + 1</c> tail.
    /// </summary>
    public const int EmptyLength = 42;

    /// <summary>One item element: the hash key plus the shared 62-byte record.</summary>
    public const int ItemElementLength = 4 + InventoryItem.BaseLength;

    public int Length => EmptyLength + (ItemElementLength * Items.Count);

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt64(ContainerGuid);              // +0x00
        writer.WriteUInt32(ContainerDefinitionId);      // +0x08
        writer.WriteUInt64(AssociatedCharacterGuid);    // +0x10
        writer.WriteUInt32(SlotCount);                  // +0x2c

        writer.WriteInt32(Items.Count);                 // FUN_140a4f410 -> +0x48
        foreach (ContainerItemEntry entry in Items)
        {
            entry.WriteTo(writer);
        }

        writer.WriteBool(FlagA);                        // +0x29  [lead]
        writer.WriteUInt32(MaxBulk);                    // +0x30
        writer.WriteUInt32(UnknownBulkField);           // +0x34  [lead]
        writer.WriteUInt32(BulkUsed);                   // +0x38
        writer.WriteBool(FlagB);                        // +0x3c  [lead]
    }
}

/// <summary>
/// One element of a container's item list (<c>FUN_140a4f410</c>): <c>u32 key</c> followed by the
/// shared inventory-item record.
/// <para>
/// The leading <c>u32</c> is the collection's own hash key (it lands at <c>item+0x68</c>), the same
/// role as the leading <c>u32</c> of a <c>ProximateItem</c>. The capture-backed reference writer uses
/// the item's definition id here; the item's actual per-container slot remains in the shared
/// <see cref="InventoryItem"/> record that follows it.
/// </para>
/// </summary>
public sealed record ContainerItemEntry(uint Key, InventoryItem Item)
{
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt32(Key);
        Item.WriteTo(writer);       // FUN_140a3aa60, no item-class tail in a container list
    }
}

/// <summary>
/// <c>Container.InitContainers</c> (<c>0xc8</c>, u16 sub 2; reader <c>FUN_140d7b810</c>, applier
/// <c>FUN_140d7ed90</c>):
/// <code>
/// u8   0xc8
/// u16  0x0002
/// u64  unknownA          [LEAD - docs/41 L2: the client seeds it from DAT_143f91640 and never
///                         compares it; Cranberry sends 0]
/// u64  characterGuid     (compared against player+0xd8)
/// i32  n + n x { u32 key; container record }     (FUN_140a55df0)
/// u8   flag              [LEAD - parser +0xc8 -> container manager +0xb8]
/// </code>
/// When the character guid is the local player, <c>FUN_140d7ed90</c> <b>clears the whole existing
/// container list</b>, re-inserts every container in this packet, and re-indexes the player's item
/// collection (<c>player+0xbdb8</c>) and loadout manager (<c>player+0xf548</c>) against them
/// (<c>FUN_140d7bcb0</c>). A guid that is not the local player is routed to the proxied-character
/// manager at <c>player+0xf7d0</c> - that is how another player's or a corpse's containers appear.
/// <para>
/// Because it is destructive, this is the <em>bootstrap</em> packet: send it once, before any
/// <c>ItemAdd</c> that names one of these container guids. Deltas go through
/// <see cref="UpdateContainer"/>.
/// </para>
/// </summary>
public sealed record InitContainers(
    ulong CharacterGuid,
    IReadOnlyList<ContainerEntry> Containers,
    ulong UnknownA = 0,
    bool Flag = false)
{
    public const byte Opcode = ContainerOpcodes.ContainerBase;
    public const ushort SubOpcode = ContainerOpcodes.InitContainersSub;

    /// <summary>Envelope with an empty container list: <c>1 + 2 + 8 + 8 + 4 + 1</c>.</summary>
    public const int EmptyLength = 24;

    public int Length
    {
        get
        {
            int total = EmptyLength;
            foreach (ContainerEntry entry in Containers)
            {
                total += entry.Length;
            }

            return total;
        }
    }

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteUInt64(UnknownA);           // parser +0x18  [lead]
        writer.WriteUInt64(CharacterGuid);      // parser +0x20
        writer.WriteInt32(Containers.Count);
        foreach (ContainerEntry entry in Containers)
        {
            entry.WriteTo(writer);
        }

        writer.WriteBool(Flag);                 // parser +0xc8  [lead]
    }
}

/// <summary>
/// One element of <see cref="InitContainers"/>'s list (<c>FUN_140a55df0</c>): <c>u32 key</c> then a
/// <see cref="ContainerRecord"/>. The key is the loadout slot occupied by the item that provides
/// the container. The August re-indexer hashes the loadout slot and compares it with this wrapper
/// key; a guid-derived key leaves the record stored but prevents it being associated with the item.
/// </summary>
public sealed record ContainerEntry(uint Key, ContainerRecord Container)
{
    public int Length => 4 + Container.Length;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt32(Key);
        Container.WriteTo(writer);
    }
}

/// <summary>
/// <c>Container.UpdateContainer</c> (<c>0xc8</c>, u16 sub 6; reader <c>FUN_140d7bb80</c>, applier
/// <c>FUN_140d7f580</c>):
/// <code>
/// u8   0xc8
/// u16  0x0006
/// u64  unknownA          [LEAD - the same field as sub 2; Cranberry sends 0]
/// u64  characterGuid
///      container record
/// </code>
/// <c>FUN_140d7f580</c> finds the container with the matching guid in the player's manager, copies
/// the record over it (<c>FUN_140d7caf0</c>), pokes the loadout manager
/// (<c>FUN_140d37250(player+0xf548)</c>) and raises the container-changed event. A guid the client
/// has never been told about is the <see cref="ContainerErrorCode.UnknownContainer"/> case, which
/// is exactly what Cranberry produces today by sending <c>ContainerGuid = 0</c> on every
/// <c>ItemAdd</c> (docs/41 §0).
/// </summary>
public sealed record UpdateContainer(
    ulong CharacterGuid,
    ContainerRecord Container,
    ulong UnknownA = 0)
{
    public const byte Opcode = ContainerOpcodes.ContainerBase;
    public const ushort SubOpcode = ContainerOpcodes.UpdateContainerSub;

    /// <summary>Envelope alone: <c>1 + 2 + 8 + 8</c>.</summary>
    public const int EnvelopeLength = 19;

    public int Length => EnvelopeLength + Container.Length;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteUInt64(UnknownA);
        writer.WriteUInt64(CharacterGuid);
        Container.WriteTo(writer);
    }
}

/// <summary>
/// <c>Container.Error</c> (<c>0xc8</c>, u16 sub 3; <c>FUN_140d7eb60</c>): <c>u8 0xc8; u16 0x0003;
/// u64 characterGuid; u32 errorCode</c> - <b>15 bytes</b>. The client prints
/// <c>Container Error: &lt;name&gt;</c> to its own console and touches no state.
/// <para>
/// This is the recommended <b>first live probe</b> for the whole family (docs/41 §6c): it is one
/// 15-byte packet, it cannot corrupt anything, and a screenshot of that console line is a
/// client-originated proof that <c>0xc8</c> reaches the handler at all - which the registration
/// table alone cannot establish.
/// </para>
/// </summary>
public sealed record ContainerError(ulong CharacterGuid, ContainerErrorCode Code)
{
    public const byte Opcode = ContainerOpcodes.ContainerBase;
    public const ushort SubOpcode = ContainerOpcodes.ErrorSub;
    public const int Length = 15;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
        writer.WriteUInt32((uint)Code);
    }
}
