using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone;

// Ground-loot pickup packets (docs/13-loot-protocol.md): a lootable object is spawned as a
// lightweight world entity, the player's F key sends Command.InteractRequest, and the server
// answers with ClientUpdate.ItemAdd plus Character.RemovePlayer. Every layout below is the August
// client's own parser read sequence; anything not recovered from the binary is marked as a lead in
// the member's comment and named in docs/13's blocker list.
//
// ---------------------------------------------------------------------------------------------
// THE PICKUP BURST, AS THE OWNER'S OWN SERVER SENDS IT (docs/78 §4.3). Cranberry's grant path is a
// different protocol and is not written against this yet; it is recorded here because this is the
// file the next lane to write it will open, and because both of its rules are non-obvious.
//
//   1. ORDER: effect -> reward -> grant -> interaction -> removal -> panel. The composite EFFECT is
//      FIRST and the REMOVAL is LAST, and the reason is that both are addressed to the ITEM'S guid:
//      once the object is destroyed there is nothing left for the effect to play on, so an effect
//      sent after the removal plays nowhere. Cranberry's own order today is grant (ItemAdd) then
//      removal then panel, which is the same rule for the same reason (docs/13 §5) — it simply has
//      no effect or reward step yet.
//
//   2. FastSelfPickup (his r41): the PICKER must NOT receive a self-addressed
//      CharacterState.InteractionStart/Stop pair, because that pair delays the client's next F
//      request and turns a run down a row of loot into one pickup per animation. Other players
//      still get it, so the animation is still seen. If Cranberry ever adds the interaction
//      animation, it is addressed to everyone EXCEPT the picker.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// <c>AddLightweightNpc</c> (0xd6; dispatcher <c>FUN_140af3950</c> → parser <c>FUN_140a2d040</c>).
/// The August parser for 0xd6 is the shared lightweight-entity body <em>alone</em> — the vehicle
/// record 0xd7 is the same body plus the <c>FUN_140a2dd10</c> tail — so a ground item is that body
/// with <c>vehicleId (+0x110) = 0</c>, a static position update type and the item's
/// <em>ground</em> actor model id at +0x40 (docs/13 §3a).
/// <para>
/// The model id is a numeric row of the client's own <c>Models.txt</c>, never the item definition's
/// <c>MODEL_NAME</c> string: <c>ClientItemDefinitions</c> row 2423 (Field Bandage) names the held
/// mesh <c>Common_Props_Bandages_BandageRoll_3P.adr</c>, whose ground counterpart without the
/// <c>_3P</c> suffix is <c>Models.txt</c> row <b>9066</b>. The item definition id itself never
/// appears in this packet; identity is bound by <see cref="ItemAdd"/> when the item is granted.
/// </para>
/// Minimal length (one-byte transient varint): 200 bytes.
/// </summary>
public sealed record AddLightweightItem(
    ulong Guid,
    uint TransientId,
    uint GroundModelId,
    Vector3 Position,
    uint NameId = 0,
    Vector4? Rotation = null,
    uint ShaderGroupId = 0,
    bool Collidable = false)
{
    public const byte Opcode = ZoneOpcodes.AddLightweightNpc;
    public const int MinimalLength = LightweightEntityBody.MinimalLength;

    /// <summary>
    /// The shared <c>FUN_140a2d040</c> body with the ground-item field choices.
    /// <see cref="ShaderGroupId"/> (D328) rides at <c>+0x1a4</c>, the vehicle tint field: the
    /// item's base appearance group, or 0 for the white composite.
    /// </summary>
    public LightweightEntityBody Body => new(
        Opcode,
        Guid,
        TransientId,
        GroundModelId,
        Position,
        Rotation ?? new Vector4(0, 0, 0, 1),
        VehicleId: 0,               // +0x110: any non-zero value would select a vehicle actor class
        NameId: NameId,
        PositionUpdateType: 0,      // +0x11c: 0 = static, the item never moves
        ProfileId: 0,
        NpcDefinitionId: 0,
        SpawnFlags1: Collidable ? LightweightEntityBody.CollidableFlag : (byte)0,
        ShaderGroupId: ShaderGroupId);

    public int Length => Body.Length;

    public void WriteTo(PacketWriter writer) => Body.WriteTo(writer);
}

/// <summary>
/// One inventory item record, the blob parsed by <c>FUN_140a3aa60</c> and shared by
/// <c>ClientUpdate.ItemAdd</c> and <c>ClientUpdate.ItemUpdate</c>. The machine-extracted read
/// sequence in <c>FUN_140a3aa60</c> (13 inline reads = 61
/// bytes, plus the one detail-block byte the extractor could not see) gives the 62-byte base:
/// <c>u32 definitionId; u32 tint; u64 itemGuid; u32 count;</c> the detail block
/// <c>FUN_140a496c0</c> (<c>u8 hasDetail; if set { u32 kind; if kind == 1 { FUN_140a3ad60 } }</c>);
/// <c>u64 containerGuid; u32 containerDefinitionId; u32 slotId; u32 baseDurability;
/// u32 currentDurability; u32 maxDurability; u8 flag; u64 ownerGuid; u32</c>.
/// </summary>
public sealed record InventoryItem(
    uint DefinitionId,
    ulong ItemGuid,
    uint Count,
    ulong OwnerGuid,
    ulong ContainerGuid,
    uint ContainerDefinitionId,
    uint SlotId,
    uint Tint = 0,
    uint BaseDurability = 0,
    uint CurrentDurability = 0,
    uint MaxDurability = 0,
    bool Flag = true,
    uint Trailer = 0)
{
    /// <summary>Length of the base record with an absent detail block (<c>hasDetail = 0</c>).</summary>
    public const int BaseLength = 62;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt32(DefinitionId);           // +8
        writer.WriteUInt32(Tint);                   // +0xc
        writer.WriteUInt64(ItemGuid);               // +0x10
        writer.WriteUInt32(Count);                  // +0x18
        writer.WriteBool(false);                    // FUN_140a496c0: no detail block
        writer.WriteUInt64(ContainerGuid);          // +0x20
        writer.WriteUInt32(ContainerDefinitionId);  // +0x28
        writer.WriteUInt32(SlotId);                 // +0x2c (1-based)
        writer.WriteUInt32(BaseDurability);         // +0x30
        writer.WriteUInt32(CurrentDurability);      // +0x34
        writer.WriteUInt32(MaxDurability);          // +0x38
        writer.WriteBool(Flag);                     // +0x3c
        writer.WriteUInt64(OwnerGuid);              // +0x40
        writer.WriteUInt32(Trailer);                // +0x48
    }
}

/// <summary>
/// <c>ClientUpdate.ItemAdd</c> (base 0x11, u16 sub 0x0002; dispatcher <c>FUN_140afc660</c> case 2 →
/// envelope <c>FUN_140a358c0</c>): <c>u8 0x11; u16 0x0002; u64 targetCharacterGuid; i32 length +
/// blob</c>. When the target guid is the local player the blob is applied by <c>FUN_140dbf5b0</c>,
/// otherwise by <c>FUN_140c50ba0</c>; both reach <c>FUN_140c35400</c>, which parses the item record
/// and then fires the container-change notification itself — so no separate container-repaint
/// packet is needed after a ground pickup (docs/13 §7; the 1087 <c>Container 0xc9</c> family does
/// not exist in 1148).
/// <para>
/// <b>Item-class tail (lead, docs/13 blocker 2).</b> After <c>FUN_140a3aa60</c>,
/// <c>FUN_140c35400</c> calls the item object's own <c>vtable+0x50</c>, which reads a further
/// per-item-class tail from the same cursor; the length prefix covers base + tail. The reader for
/// the <b>Generic</b> class (item 2423, ITEM_CLASS 16053) was not decompiled. The behavioural lead
/// is that a non-weapon tail on the 1087 client was a single <c>0x00</c> terminator, so that is
/// what <see cref="GenericItemClassTail"/> writes. Unconfirmed against the August binary: if a live
/// grant is rejected or leaves trailing bytes, this is the first thing to change.
/// </para>
/// </summary>
public sealed record ItemAdd(ulong TargetCharacterGuid, InventoryItem Item)
{
    public const byte Opcode = ZoneOpcodes.ClientUpdateBase;
    public const ushort SubOpcode = 0x0002;

    /// <summary>
    /// Lead-based tail for the Generic item class: one <c>0x00</c> terminator (docs/13 §4b).
    /// </summary>
    public static ReadOnlySpan<byte> GenericItemClassTail => [0x00];

    /// <summary>Envelope (15 bytes) + the length-prefixed blob.</summary>
    public int Length => 15 + InventoryItem.BaseLength + GenericItemClassTail.Length;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteUInt64(TargetCharacterGuid);

        int lengthSlot = writer.ReserveUInt32();
        int start = writer.Position;
        Item.WriteTo(writer);
        writer.WriteRaw(GenericItemClassTail);
        writer.PatchUInt32(lengthSlot, (uint)(writer.Position - start));
    }
}

/// <summary>
/// <c>ClientUpdate.ItemDelete</c> (base 0x11, u16 sub 0x0004; parser <c>FUN_140a359e0</c>):
/// <c>u8 0x11; u16 0x0004; u64 targetCharacterGuid; u64 itemGuid</c> — 19 bytes (docs/13 §4b).
/// </summary>
public sealed record ItemDelete(ulong TargetCharacterGuid, ulong ItemGuid)
{
    public const byte Opcode = ZoneOpcodes.ClientUpdateBase;
    public const ushort SubOpcode = 0x0004;
    public const int Length = 19;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteUInt64(TargetCharacterGuid);
        writer.WriteUInt64(ItemGuid);
    }
}

/// <summary>
/// The client's packed name (<c>FUN_140a12af0</c>): a <c>u16</c> tag whose low 13 bits are a
/// length, bit 15 marking a pre-registered 13-bit id instead of an inline string, bit 14 an extra
/// trailing <c>u32</c> and bit 13 the high bit of the stored value. In the inline-string form the
/// tag is the string length and a NUL-terminated string of that length follows; the client hashes
/// it with <c>FUN_140981390</c> (that hash of <c>"ClientInteractComponent"</c> is the registered
/// <c>0xc6a64e3e</c> of <c>FUN_140c1a940</c>). Cranberry only ever writes the inline-string form.
/// </summary>
public static class ClientPackedName
{
    public const ushort LengthMask = 0x1fff;

    public static void Write(PacketWriter w, string name)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(name);
        int length = System.Text.Encoding.UTF8.GetByteCount(name);
        if (length > LengthMask)
        {
            throw new ArgumentOutOfRangeException(nameof(name), "A packed name carries at most 13 length bits.");
        }

        w.WriteUInt16((ushort)length);
        w.WriteRaw(System.Text.Encoding.UTF8.GetBytes(name));
        w.WriteByte(0);     // the reader advances strlen + 1
    }

    public static int Length(string name) => 3 + System.Text.Encoding.UTF8.GetByteCount(name);
}

/// <summary>
/// <c>Replication.CreateComponent</c> (base 0xea, <b>u8</b> sub 0x04; dispatcher
/// <c>FUN_1413e3f40</c>, envelope reader <c>FUN_1413e1980</c>): <c>u8 0xea; u8 0x04;
/// varint ownerTransientId; packedName componentClass; i32 nameTable {packedName; str};
/// i32 repData {u32 id; u32 classHash; u8 flag; i32 length + bytes}</c>. The dispatcher's own log
/// string — "Client told to create Component class (%s) but owner object (transientId=%u) doesn't
/// exist!" — names the first two fields, so the owner transient id must be the one the item's
/// <see cref="AddLightweightItem"/> carried.
/// <para>
/// <b>Lead (docs/13 blocker 3).</b> Each rep-data blob is parsed later by its own class's
/// <c>vtable+0x20</c> deserializer, and none of the three registered classes
/// (<c>0x50d51c9d</c>, <c>0xf68bb709</c>, <c>0xb927f3ef</c>) was decompiled, so the
/// <c>InteractReplicationData</c> field map — including the interaction distance — is still
/// unknown. Cranberry therefore sends the component with an <em>empty</em> rep-data list and
/// relies on the client's own defaults; whether that is enough to bind the <c>[F]</c> prompt is
/// exactly the sufficiency question docs/13 §3d leaves to a live test.
/// </para>
/// </summary>
public sealed record CreateComponent(uint OwnerTransientId, string ComponentClass = "ClientInteractComponent")
{
    public const byte Opcode = ZoneOpcodes.ReplicationBase;
    public const byte SubOpcode = 0x04;

    /// <summary>Registered client type <c>0xc6a64e3e</c> (<c>FUN_140c1a940</c>) — the [F] binding.</summary>
    public const string InteractComponentClass = "ClientInteractComponent";

    /// <summary>Registered client type at <c>0x14312cd30</c> — the world-object presentation.</summary>
    public const string NpcComponentClass = "ClientNpcComponent";

    public int Length => 2 + ClientVarInt.Length(OwnerTransientId) + ClientPackedName.Length(ComponentClass) + 8;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        ClientVarInt.Write(writer, OwnerTransientId);           // FUN_140a190f0 → +0x48
        ClientPackedName.Write(writer, ComponentClass);         // FUN_140a12af0 → +0x4c
        writer.WriteInt32(0);                                   // FUN_1413e1610 name table
        writer.WriteInt32(0);                                   // FUN_1413e0380 rep-data blobs
    }
}

/// <summary>
/// <c>ProximateItems</c> list (base 0xf8, <b>u8</b> sub 0x01; dispatcher <c>FUN_140d8b840</c> →
/// <c>FUN_140d8b8f0</c>, element parser <c>FUN_140d8a760</c>): <c>u8 0xf8; u8 0x01; i32 count;</c>
/// then per element <c>u32 key; &lt;62-byte item record&gt;; u64 worldObjectGuid</c> — 74 bytes each,
/// the same <c>FUN_140a3aa60</c> record as <see cref="ItemAdd"/> but <em>without</em> an item-class
/// tail (the element is a plain record, not a class-dispatched item object). The list drives the
/// client's <c>BaseClient.Inventory.ProximateItems</c> data source: the nearby-pickable panel and
/// QuickLoot.
/// <para>
/// The leading <c>u32</c> is the collection's 128-bucket hash key (<c>elem[(key &amp; 0x7f) + 5]</c>,
/// re-hashed on removal by <c>FUN_140d8bdc0</c>); its semantic name is a lead — Cranberry writes
/// the world object's transient id, which is unique per object and is not the item definition id
/// (that already rides inside the record).
/// </para>
/// </summary>
public sealed record ProximateItems(IReadOnlyList<ProximateItem> Items)
{
    public const byte Opcode = ZoneOpcodes.ProximateItemBase;
    public const byte SubOpcode = 0x01;
    public const int ElementLength = 74;

    public int Length => 6 + (ElementLength * Items.Count);

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteInt32(Items.Count);
        foreach (ProximateItem item in Items)
        {
            item.WriteTo(writer);
        }
    }
}

/// <summary>One 74-byte element of <see cref="ProximateItems"/> (<c>FUN_140d8a760</c>).</summary>
public sealed record ProximateItem(uint Key, InventoryItem Item, ulong WorldObjectGuid)
{
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt32(Key);            // → element +0x70, the hash bucket key
        Item.WriteTo(writer);               // FUN_140a3aa60, no item-class tail here
        writer.WriteUInt64(WorldObjectGuid);// → element +0x58
    }
}

/// <summary>
/// c2s <c>Command.InteractRequest</c> (base 0x09, u16 sub 0x0007; registration id
/// <c>0x07000900</c> = <c>(sub &lt;&lt; 24) | (base &lt;&lt; 8)</c>,
/// <c>cCommandPacketIdInteractRequest</c>, registered by <c>FUN_1413c37a0</c>). This is the F-key
/// pickup request; the guid names the world object spawned by <see cref="AddLightweightItem"/>.
/// <para>
/// The u16 sub width is proven, not assumed: the registration decoder <c>FUN_1413d1df0</c> walks a
/// packet id in 16-bit chunks, and the Command family registers subs as high as <c>0x0527</c>
/// (<c>cAdminCommandPacketIdNpcLocsRequest</c> at <c>0x3f7000900</c>), which a one-byte sub could
/// not carry. Sub width is per family in 1148 — <c>0xf8</c> and <c>0xea</c> use a <b>u8</b> sub,
/// <c>0x11</c> and <c>0x09</c> a <b>u16</b>. The guid's byte offset is still a lead (docs/13 §4a:
/// all ~1624 Command registrations share the generic descriptor <c>DAT_1430f2620</c> and the family
/// is dispatched through <c>zoneClient-&gt;vtbl[0x2d0]</c>, which is not decompiled), so
/// <see cref="Parse"/> accepts the <c>09 07 00 &lt;u64&gt;</c> form and reports trailing bytes
/// instead of rejecting the packet.
/// </para>
/// </summary>
public sealed record InteractRequest(ulong TargetGuid, int TrailingBytes = 0)
{
    public const byte Opcode = ZoneOpcodes.CommandBase;
    public const ushort SubOpcode = 0x0007;
    public const int MinimumLength = 11;

    public static InteractRequest Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        byte opcode = reader.ReadByte();
        ushort sub = reader.ReadUInt16();
        if (opcode != Opcode || sub != SubOpcode)
        {
            throw new PacketFormatException(
                $"Expected Command.InteractRequest 09 07 00, got {opcode:x2} {sub & 0xff:x2} {sub >> 8:x2}.");
        }

        ulong guid = reader.ReadUInt64();
        return new InteractRequest(guid, reader.Remaining);
    }
}

/// <summary>
/// c2s <c>Command.PlayerSelect</c> (base 0x09, u16 sub 0x0015): <c>u64 selectingCharacterGuid;
/// u64 targetGuid</c>. The August client emits this <em>and</em> <see cref="InteractRequest"/> for
/// a single F press, so the server claims the object on whichever arrives first and no-ops the
/// other (docs/13 §8). Field order within the two guids is a lead from the behavioural reference;
/// <see cref="LootWorld"/> is keyed by world guid, so a swapped pair simply fails the lookup.
/// </summary>
public sealed record PlayerSelect(ulong SelectingCharacterGuid, ulong TargetGuid)
{
    public const byte Opcode = ZoneOpcodes.CommandBase;
    public const ushort SubOpcode = 0x0015;
    public const int MinimumLength = 19;

    public static PlayerSelect Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        byte opcode = reader.ReadByte();
        ushort sub = reader.ReadUInt16();
        if (opcode != Opcode || sub != SubOpcode)
        {
            throw new PacketFormatException(
                $"Expected Command.PlayerSelect 09 15 00, got {opcode:x2} {sub & 0xff:x2} {sub >> 8:x2}.");
        }

        return new PlayerSelect(reader.ReadUInt64(), reader.ReadUInt64());
    }
}
