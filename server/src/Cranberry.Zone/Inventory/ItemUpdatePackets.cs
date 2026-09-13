using Cranberry.Protocol;

namespace Cranberry.Zone.Inventory;

/// <summary>
/// <c>ClientUpdate.ItemUpdate</c> (base <c>0x11</c>, <c>u16</c> sub <c>0x0003</c>) - <b>73 bytes</b>,
/// the cheap "this item changed" packet Cranberry has never sent.
///
/// <para>
/// <b>The body is DERIVED, not guessed.</b> The registrar names the id
/// (<c>cClientUpdatePacketIdItemUpdate = 0x3001100</c>, <c>FUN_1413c2b50</c> sub 3). The dispatcher
/// <c>FUN_140afc660</c> case 3 builds a record stamped <c>{0x11, 3}</c> and calls the reader
/// <c>FUN_140a65960(rec, bytes, length, 0)</c>; that reader takes
/// <c>u8 → rec+8; u16 → rec+0x10; u64 → rec+0x18</c> and then hands the same cursor to
/// <c>FUN_140a3aa60</c>, the 62-byte item record shared with <c>ItemAdd</c>
/// (<see cref="InventoryItem"/>). Its fourth argument is <b>0</b>, and the success test is
/// <c>no read error &amp;&amp; end − cursor &lt; 1</c>: the packet must end exactly where the record
/// ends, so there is <b>no length prefix and no item-class tail</b> - the two things
/// <c>ItemAdd</c> has and this does not. <c>11 + 62 = 73</c>.
/// </para>
///
/// <para>
/// <b>What the client does with it.</b> When the target guid is the local player's,
/// <c>FUN_140dbfcb0</c> looks the item up by guid and, if it is already known, calls
/// <c>FUN_141479ae0</c>, which copies exactly seven fields onto the live item -
/// <c>count</c> (<c>rec+0x18</c>, via <c>FUN_1421dba20</c> and the item's <c>vtable+0x18</c>),
/// <c>containerGuid</c> (<c>+0x20</c>), <c>containerDefinitionId</c> (<c>+0x28</c>),
/// <c>slotId</c> (<c>+0x2c</c>), <c>currentDurability</c> (<c>+0x34</c>, via
/// <c>FUN_1421dbad0</c>), <c>baseDurability</c> (<c>+0x30</c>) and <c>maxDurability</c>
/// (<c>+0x38</c>) - then repaints the recipe components, three UI panels and the loadout.
/// <b>An update for a guid the client does not hold returns without doing anything</b>, so this
/// packet can never create an item and can never crash on a stale guid. The remaining record fields
/// (definition id, tint, flag, owner guid, trailer) are read and ignored, but they still have to be
/// on the wire because of the exact-length rule.
/// </para>
///
/// <para>
/// <b>D29.</b> BUILT and byte-TESTED. Never LIVE-VERIFIED: no <c>11 03</c> has ever left this server
/// or any other, so the first one the August client receives is the experiment. It is gated by
/// <c>CRANBERRY_ITEM_UPDATE</c> (<see cref="Combat.AmmoOptions.SendItemUpdate"/>) for exactly that
/// reason.
/// </para>
/// </summary>
public sealed record ItemUpdate(ulong TargetCharacterGuid, InventoryItem Item)
{
    /// <summary><c>cPacketIdClientUpdateBase</c>.</summary>
    public const byte Opcode = ZoneOpcodes.ClientUpdateBase;

    /// <summary><c>cClientUpdatePacketIdItemUpdate</c>.</summary>
    public const ushort SubOpcode = 0x0003;

    /// <summary>Envelope: <c>u8 base; u16 sub; u64 targetCharacterGuid</c>.</summary>
    public const int EnvelopeLength = 11;

    /// <summary>The whole packet: 11 + 62. There is no tail and no length prefix.</summary>
    public const int Length = EnvelopeLength + InventoryItem.BaseLength;

    /// <summary>
    /// The packet for one item instance whose stack count moved - the ammunition case.
    /// </summary>
    public static ItemUpdate ForStack(ulong ownerGuid, InventoryItemInstance item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new ItemUpdate(ownerGuid, item.ToRecord(ownerGuid));
    }

    /// <summary>
    /// The packet for a weapon whose durability moved - the per-shot case. The record is the item's
    /// own, with the three durability fields filled in; everything else is exactly what
    /// <c>ItemAdd</c> would have carried, so the client's copy cannot drift.
    /// </summary>
    public static ItemUpdate ForDurability(
        ulong ownerGuid,
        InventoryItemInstance item,
        int currentDurability,
        int maxDurability)
    {
        ArgumentNullException.ThrowIfNull(item);
        uint max = (uint)Math.Max(0, maxDurability);
        return new ItemUpdate(
            ownerGuid,
            item.ToRecord(ownerGuid) with
            {
                BaseDurability = max,
                CurrentDurability = (uint)Math.Clamp(currentDurability, 0, (int)max),
                MaxDurability = max,
            });
    }

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(Item);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteUInt64(TargetCharacterGuid);
        Item.WriteTo(writer);
    }

    /// <summary>The serialised packet, for the arms that hand whole byte arrays back.</summary>
    public byte[] ToBytes()
    {
        using var writer = new PacketWriter(Length);
        WriteTo(writer);
        return writer.Written.ToArray();
    }
}
