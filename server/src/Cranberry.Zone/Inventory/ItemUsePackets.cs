using Cranberry.Protocol;

namespace Cranberry.Zone.Inventory;

// Items family 0xac sub 0x2c - Items::cItemPacketIdRequestUseItem, the CLIENT-TO-SERVER half of the
// inventory that docs/41 §7 lead L5 left open ("how does a manual drag reach the server?").
//
// It is not a lead any more. The August client has been sending this packet at Cranberry for four
// waves and the server has answered none of them; every one is in this project's own captures and
// in the host log, written by ZoneService's Items default arm as
//
//     zone Items sub=0x2c 47 bytes: AC2C...  (unanswered)
//
// 21 of them across captures\wire-20260829-201025.txt, wire-20260829-220829.txt,
// wire-20260830-085410.txt and wire-20260830-090725.txt. Because these are CLIENT-ORIGINATED bytes,
// the field map below is LIVE-VERIFIED in the D29 sense - it is not a decompile reading, it is what
// the client actually wrote, cross-checked field by field against the server's own state at that
// instant. Full derivation: docs/63-inventory-complete.md §1.

/// <summary>
/// One entry of <c>Items.RequestUseItem</c>'s typed parameter block: <c>u32 paramId</c> then
/// <c>u32 value</c>. Ported from the owner's <c>ZoneItemUse.TryReadStackSize</c> under D53;
/// <c>paramId 1</c> is the tile's stack size at click.
/// </summary>
public readonly record struct ItemUseParameter(uint ParameterId, uint Value);

/// <summary>Opcode constants for the item-action channel.</summary>
public static class ItemUseOpcodes
{
    /// <summary><c>ItemsBase</c>. Repeated here so this file reads on its own.</summary>
    public const byte ItemsBase = 0xac;

    /// <summary>
    /// <c>Items::cItemPacketIdRequestUseItem</c> - <c>out/registrations-1148.md</c>, the 0xac table,
    /// sub 0x2c. The sub is a <b>u8</b> for this family, the same one-byte sub the skin requests
    /// 0x31 / 0x32 / 0x34 use, which <c>ZoneService.HandleSkinItemRequest</c> has read since wave 1.
    /// </summary>
    public const byte RequestUseItemSub = 0x2c;
}

/// <summary>
/// <c>Items.RequestUseItem</c> (<c>0xac</c>, u8 sub <c>0x2c</c>) - the client asking the server to
/// perform one <c>ItemUseOptions</c> action on one inventory item.
///
/// <code>
///  +0   u8   0xac
///  +1   u8   0x2c
///  +2   u32  itemCount                always 1 in 21 of 21 captured packets
///  +6   u32  reservedA                always 0
///  +10  u32  itemUseOptionId          ItemUseOptions.ITEM_USE_OPTION_ID
///  +14  u64  characterGuid            the requesting player
///  +22  u64  sourceCharacterGuid      == characterGuid in 21 of 21
///  +30  u64  targetCharacterGuid      == characterGuid in 21 of 21
///  +38  u64  itemGuid                 the inventory instance guid
///  +46  u8   noParams                 1 -> the packet ends here (47 bytes)
///                                     0 -> a typed parameter block follows (75 bytes)
///  +47  u32  entries                  how many {paramId, value} pairs follow
///  +51  u32  paramId                  1 = the tile's stack size at click time
///  +55  u32  value                    that parameter's value
///  +59  4 x u32                       four further empty typed lists, all 0
/// </code>
///
/// <para>
/// <b>WAVE 9 - the four <c>[LEAD]</c>s in this block are closed, and the tail is not opaque.</b>
/// The owner's own parser (<c>C:\Z1\Server\Zone\ZoneItemUse.cs</c>
/// <c>TryReadStackSize</c>) reads the same packet at the same offsets and gives the tail a shape:
/// <c>u32 entries</c> then <c>entries x {u32 paramId, u32 value}</c>, with <c>paramId 1</c> the
/// tile's stack count at the moment of the click. His own boot self-check builds the buffer
/// explicitly and labels the rest - <c>intParamsA</c> (the block above), then <b>four more empty
/// lists</b> <c>intParamsB</c>, <c>qwordParams</c>, <c>vectorParams</c> and <c>stringParams</c>,
/// which is exactly the sixteen zero bytes at +59. Every one of Cranberry's own 21 captured packets
/// stays consistent under this reading, and it is strictly more general: Cranberry's fixed
/// <c>CountOffset = 55</c> is the <c>entries = 1, paramId = 1</c> special case of it. Ported under
/// D53; docs/86 §2.5.
/// </para>
/// <para>
/// One field order differs between the two and is <b>unobservable</b>: he reads target at +22 and
/// source at +30, this file the other way round. All 21 samples carry the same value in both, since
/// every action the owner performed was on his own inventory. Neither reading can be preferred from
/// the bytes, and <see cref="CharacterGuid"/> is the only one the server acts on.
/// </para>
///
/// <para><b>How each field was pinned - all of it from client-originated bytes (D29).</b></para>
/// <list type="bullet">
/// <item><b><c>itemUseOptionId</c></b>: the ten distinct values ever seen are 2, 3, 4, 5, 6, 7, 9,
/// 12, 63 and 99, and every one of them is a real <c>ItemUseOptions.ITEM_USE_OPTION_ID</c> -
/// 2/3/9/99 <c>ConsumeItem</c>, 4 <c>DropItem</c>, 5 <c>PlaceItem</c>, 6/63 <c>SalvageItem</c>,
/// 7 <c>UnloadWeapon</c>, 12 <c>RemoveItem</c>. A u32 that lands on a live sheet row ten times out
/// of ten is not a coincidence, and each value matches what the player was doing.</item>
/// <item><b><c>characterGuid</c></b>: 4099 in every packet, which is the guid Cranberry issued that
/// session (<c>logs\host-20260830-090725.log</c> - 27 lines carry <c>guid=4099</c>).</item>
/// <item><b><c>itemGuid</c></b>: every value is in Cranberry's own inventory guid space,
/// <c>LootWorld.DefaultItemGuidBase = 0x3100_0000_0000_0001</c>, and names an instance the server
/// had granted minutes earlier. <c>0x3100000000000009</c> is item 1429 (7.62mm ammunition), granted
/// at 09:10:05.965 and stacked at 09:10:10.476 / 09:10:30.584 / 09:10:31.280.</item>
/// <item><b><c>count</c></b>: the two 75-byte packets carry <b>120</b> and <b>90</b> for exactly
/// that ammunition stack, and the host log says the stack held 4 x 30 = 120 rounds at 09:11:15 and
/// 3 x 30 = 90 rounds at 09:20:49. Both are the WHOLE stack, which is why wave 9 reads the value as
/// the stack size at click (<see cref="StackAtClick"/>) rather than as a chosen quantity: no
/// quantity dialog was open, and a drop of a stack drops the stack.</item>
/// </list>
///
/// <para>
/// The three <c>[LEAD]</c> guid fields are all the same value in every sample because every action
/// the owner performed was on his own inventory. They are named source/target on the model that a
/// loot-a-corpse or give-to-player action would differ there; nothing decompiled says so, and the
/// server must not depend on it. <see cref="CharacterGuid"/> is the only one this lane reads.
/// </para>
/// </summary>
/// <param name="ItemCount">
/// <c>u32</c> at +2 - 1 in all 21 captured packets. His parser's own name for it, and his count
/// resolution reads it as "how many did the UI ask for", falling back to 1.
/// </param>
/// <param name="ReservedA"><c>u32</c> at +6 - 0 in all 21.</param>
/// <param name="Parameters">
/// The typed parameter block of the 75-byte form, in packet order. Empty for the 47-byte form.
/// </param>
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
    /// <summary>The shortest legal packet: the header through <see cref="Simple"/>.</summary>
    public const int MinimumLength = 47;

    /// <summary>The form that carries a chosen quantity.</summary>
    public const int QuantityFormLength = 75;

    /// <summary>Offset of the quantity, inside the 28-byte block that follows a zero <c>simple</c>.</summary>
    public const int CountOffset = 55;

    /// <summary>
    /// <c>paramId 1</c> - the stack size the tile showed when the player clicked it. His
    /// <c>TryReadStackSize</c>.
    /// </summary>
    public const uint StackSizeParameterId = 1;

    /// <summary>The action the client asked for, resolved through <c>ItemUseOptions.txt</c>.</summary>
    public ItemUseOptionKind Kind => ItemUseOptionTable.KindOf(ItemUseOptionId);

    /// <summary>True when the typed block carried <see cref="StackSizeParameterId"/>.</summary>
    public bool HasStackAtClick => StackAtClick != 0;

    /// <summary>
    /// The tile's stack size at click, or 0 when the packet carried no such parameter (the 47-byte
    /// form). <b>Not</b> necessarily <see cref="Count"/>: they are the same value today because
    /// every captured 75-byte packet has <c>entries = 1</c> and <c>paramId = 1</c>, and they would
    /// differ the moment a packet carries a parameter this server has not seen.
    /// </summary>
    public uint StackAtClick
    {
        get
        {
            IReadOnlyList<ItemUseParameter> parameters = Parameters ?? [];
            for (int i = 0; i < parameters.Count; i++)
            {
                if (parameters[i].ParameterId == StackSizeParameterId)
                {
                    return parameters[i].Value;
                }
            }

            return 0;
        }
    }

    /// <summary>True when <paramref name="payload"/> is an <c>0xac / 0x2c</c> of usable length.</summary>
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
        ulong source = reader.ReadUInt64();
        ulong target = reader.ReadUInt64();
        ulong item = reader.ReadUInt64();
        bool simple = reader.ReadBool();

        // The typed parameter block (docs/86 §2.5, ported from his TryReadStackSize). A short or
        // absent block is not an error - it is the 47-byte form, and the count then means "all of
        // it", which is what the caller does with Count == 0. A truncated or absurd entry count is
        // not an error either: read what is there and stop.
        var parameters = new List<ItemUseParameter>();
        if (!simple && payload.Length >= MinimumLength + 4)
        {
            uint entries = reader.ReadUInt32();
            int offset = MinimumLength + 4;
            for (uint i = 0; i < entries && offset + 8 <= payload.Length; i++, offset += 8)
            {
                parameters.Add(new ItemUseParameter(reader.ReadUInt32(), reader.ReadUInt32()));
            }
        }

        // Count stays exactly what wave 8 read, so nothing downstream changes: the fixed
        // CountOffset 55 IS the value of the entries = 1 / paramId = 1 block, which is every 75-byte
        // packet ever captured.
        uint count = 0;
        for (int i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].ParameterId == StackSizeParameterId)
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
            payload.Length - (simple ? MinimumLength : QuantityFormLength),
            itemCount,
            reservedA,
            parameters);
    }
}
