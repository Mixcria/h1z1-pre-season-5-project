using Cranberry.Protocol;
using Cranberry.Zone.Weapons;

namespace Cranberry.Zone;

// Weapon in hand — the combat-capture enabler of docs/36-loot-pickup-activation.md §W.
//
// The August client has never emitted a 0x82 WeaponBase packet in 96 captured sessions, because
// nothing has ever put a weapon in the player's hands: Cranberry's slot 7 (RHand) has carried
// "Weapon_Empty.adr" since CharacterVisuals.FromSelection was written (item definition 85, "Fists").
// The c2s fire layout cannot be ported from anywhere (docs/20, docs/16 §5 — the send site was never
// located and the Z1 audit put every combat file there out of bounds), so the cheapest route to the
// Fire / ProjectileHitReport bytes is to make the client speak them: equip a real weapon and pull
// the trigger once.
//
// This file adds only the two things the existing packet set is missing for that:
//   1. EquipmentSlotRow — the equipment-slot row of Equipment.SetCharacterEquipment, which
//      SetCharacterEquipment in CharacterPackets.cs hard-codes as an EMPTY list. That list is the
//      only place on the wire where a slot id is bound to an inventory item guid.
//   2. AugustHeldWeapon — the item-definition choice, its two meshes and its ground model, all read
//      out of the client's own datasheets.
//
// Both are new types in a new file for the same reason CreateComponentWithRepData is separate from
// CreateComponent: CharacterPackets.cs and ZoneService.cs belong to other lanes this wave. The
// integration steps are written out in docs/36 §W3.

/// <summary>
/// One row of <c>Equipment.SetCharacterEquipment</c>'s equipment-slot list — the list
/// <see cref="SetCharacterEquipment"/> currently writes as <c>i32 0</c>.
/// <para>
/// <b>Layout [P-bin].</b> The list reader is <c>FUN_140a558a0</c>, reached from the equipment body
/// <c>FUN_140cd4d40</c> as <c>thunk_FUN_140a558a0(&amp;cursor, body + 0x22)</c> — i.e. it sits
/// between the two alias strings and the attachment list, exactly where
/// <see cref="SetCharacterEquipment"/> writes its zero. <c>FUN_140a558a0</c> reads
/// <c>i32 count</c>, then per row a <c>u32</c> which it uses as the collection's own hash key
/// (<c>rowKey % 0x1f</c> selects the bucket at <c>list + 5 + bucket</c>) before handing the cursor
/// to the row reader <b><c>FUN_140a39250</c></b>:
/// <code>
/// u32 slotId      → row +0x20
/// u64 itemGuid    → row +0x28
/// str tintAlias   → row +0x30   (SoeUtil::StringFixed&lt;32&gt;, FUN_140b78f60)
/// str decalAlias  → row +0x70   (SoeUtil::StringFixed&lt;32&gt;)
/// </code>
/// So a row is <c>u32 key; u32 slotId; u64 itemGuid; str; str</c> — <b>24 bytes</b> with two empty
/// strings. The key and the slot id are written as the same value: the key is only a bucket index
/// and the client never compares the two, but keying by slot id is what makes "one row per slot"
/// collide correctly on a re-send.
/// </para>
/// <b>UNVERIFIED:</b> that the item guid in this row is what binds the weapon to the RHand slot for
/// the purposes of firing. The row is proven to exist and to be read; that it is <em>sufficient</em>
/// (rather than needing <c>Character.UpdateActiveWieldType 0f 28</c> as well, docs/36 §W4) has not
/// been tested against the client. Sending it is harmless either way — the field is consumed.
/// </summary>
public sealed record EquipmentSlotRow(
    uint SlotId,
    ulong ItemGuid,
    string TintAlias = "",
    string DecalAlias = "")
{
    /// <summary>Row length with two empty strings: <c>u32; u32; u64; i32; i32</c>.</summary>
    public const int MinimalLength = 24;

    public int Length => MinimalLength
        + System.Text.Encoding.UTF8.GetByteCount(TintAlias)
        + System.Text.Encoding.UTF8.GetByteCount(DecalAlias);

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteUInt32(SlotId);          // FUN_140a558a0: the collection hash key (key % 0x1f)
        w.WriteUInt32(SlotId);          // FUN_140a39250 → row +0x20
        w.WriteUInt64(ItemGuid);        // FUN_140a39250 → row +0x28
        w.WriteString(TintAlias);       // FUN_140b78f60 → row +0x30
        w.WriteString(DecalAlias);      // FUN_140b78f60 → row +0x70
    }
}

/// <summary>
/// <c>Equipment.SetCharacterEquipment</c> (0x94 / u8 sub 1) <em>with</em> a populated equipment-slot
/// list. Identical on the wire to <see cref="SetCharacterEquipment"/> except that the
/// <c>FUN_140a558a0</c> list carries rows instead of a bare zero; the head
/// (<c>FUN_140a2cda0</c>: <c>u32 profileId; u64 characterId</c>), the <c>u32</c>, the two alias
/// strings, the attachment list (<c>FUN_140a2ed70</c> elements) and the trailing <c>u8</c> are
/// unchanged, so the live-proven 726-byte wardrobe packet of docs/32 is exactly this type with
/// <see cref="Slots"/> empty.
/// <para>
/// It is a separate record rather than a change to <see cref="SetCharacterEquipment"/> because that
/// type is the one every already-live path uses and the zoning regression of docs/32 was caused by
/// exactly this kind of widening. Once §W has been proven live the two should be merged.
/// </para>
/// </summary>
public sealed record SetCharacterEquipmentWithSlots(
    ulong CharacterId,
    uint ProfileId = 5,
    IReadOnlyList<EquipmentSlotRow>? Slots = null,
    IReadOnlyList<CharacterEquipmentAttachment>? Attachments = null,
    IActiveHandClearance? Clearance = null)
{
    public const byte Opcode = ZoneOpcodes.EquipmentBase;
    public const byte SubOpcode = SetCharacterEquipment.SubOpcode;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(ProfileId);                  // FUN_140a2cda0 head
        writer.WriteUInt64(CharacterId);                // FUN_140a2cda0 head
        writer.WriteUInt32(0);
        writer.WriteString(string.Empty);               // tint alias
        writer.WriteString(string.Empty);               // decal alias

        IReadOnlyList<EquipmentSlotRow> slots = ActiveHandRowGuard.Filter(Slots, Clearance);
        writer.WriteInt32(slots.Count);                 // FUN_140a558a0
        foreach (EquipmentSlotRow slot in slots)
        {
            slot.WriteTo(writer);
        }

        IReadOnlyList<CharacterEquipmentAttachment> attachments = Attachments ?? [];
        writer.WriteInt32(attachments.Count);           // FUN_140a2ed70 elements
        foreach (CharacterEquipmentAttachment attachment in attachments)
        {
            attachment.WriteTo(writer);
        }

        writer.WriteBool(true);
    }
}

/// <summary>
/// <b>REGRESSION GUARD 5 (docs/45).</b> An <c>Equipment.SetCharacterEquipment (0x94/01)</c>
/// equipment-slot row whose slot id is <b>7 (RHand, the active hand)</b> hard-crashes the August
/// client, whatever item it names. This type filters such a row out of every packet it writes, so
/// no call site can put one on the wire.
/// <para>
/// <b>Evidence (docs/45 §2, §3).</b> In <c>logs\host-20260829-220829.log</c> /
/// <c>captures\wire-20260829-220829.txt</c>, across four sessions, <b>5 of 5</b> <c>94 01</c>
/// packets whose row list named only body slots 1/5/10/11/100 were accepted and the client kept
/// playing; <b>4 of 4</b> that also carried a slot-7 row killed the client within a millisecond -
/// over three unrelated item definitions (83 Machete class 4098, 1991 R380 class 4096, 10 AR-15
/// class 25036) and with the slot-7 <em>attachment</em> both changed and unchanged. The 22:34:02.648
/// crash packet is the 22:33:54.678 accepted packet byte for byte with the row count bumped
/// <c>1 -&gt; 2</c> and 24 bytes inserted at offset <c>0x37</c>
/// (<c>07000000 07000000 0400000000000031 00000000 00000000</c>); reconstruction reproduces it
/// exactly (648 == 648). So neither the item, nor its class, nor the mesh substitution is the cause
/// - only the slot id is.
/// </para>
/// <para>
/// <b>Mechanism.</b> <c>H1Z1.exe_08292026_223402.dmp</c> gives
/// <c>EXCEPTION_ACCESS_VIOLATION</c> reading <c>0x38</c> at <c>0x1411cef14</c>
/// (<c>FUN_1411cee73+0xa1</c>), reached from the local input controller
/// <c>FUN_14158ef20+0x3887</c> via <c>FUN_1411cf8a0+0x66</c>. That block resolves the item bound to
/// the <em>active-hand</em> equipment slot, calls <c>item-&gt;vtable[0x40]()</c>, then reads
/// <c>FUN_14228d840(item + 0xa8)-&gt;+0x38</c> <b>without a null check</b>.
/// <c>FUN_14228d840</c> returns the weapon component's current fire-group descriptor and returns
/// <b>0</b> whenever the fire-group array (<c>+0x30</c> base, <c>+0x38</c> count) is empty - the
/// state of every weapon Cranberry grants, because no fire-group data has ever been sent
/// (<c>ItemAdd</c>'s per-item-class tail is Cranberry's 1-byte Generic tail for
/// <c>CODE_FACTORY_NAME = Weapon</c> rows too, docs/13 blocker 2; the alternative supplier
/// <c>82 11 AddFireGroup</c> has an undecoded payload). <c>rax = 0</c> at the fault is that return
/// value. Because the fault is two dereferences <em>past</em> a successful guid lookup, the row's
/// binding provably works - what is missing is the weapon's runtime fire-group state.
/// </para>
/// <para>
/// <b>This is a hard crash with a minidump, not a refusal</b> - a different failure class from the
/// three docs/32 regressions. The guard is therefore enforced here, at the single serialisation
/// choke point every caller passes through (<c>ZoneService.SendCharacterAppearance</c>'s inventory
/// projection <em>and</em> its dormant <c>GiveStarterWeapon</c> branch), rather than at each call
/// site.
/// </para>
/// <para>
/// <b>When to lift it.</b> Only together with the wielding sequence of docs/45 §5b, i.e. once the
/// client is given real fire-group data ahead of the row. Removing the guard on its own reproduces
/// this crash exactly.
/// </para>
/// <para>
/// <b>NARROWED, NOT LIFTED (docs/58 §11 stage 3, docs/60).</b> <see cref="Filter(IReadOnlyList{EquipmentSlotRow}, IActiveHandClearance)"/>
/// takes an <see cref="IActiveHandClearance"/> that may permit a slot-7 row <em>for one specific
/// item guid</em>, and only when that item has had both halves of docs/58 delivered to it: the
/// Weapon-class <c>ItemAdd</c> tail that builds the fire-group array, and a
/// <c>ReferenceData "WeaponDefinitions"</c> list-1 record that makes the array's current id resolve.
/// There is no way to ask this type for "slot 7 in general". With no clearance object - the shipped
/// default, because <c>WeaponSession.Clearance</c> is null unless <c>CRANBERRY_WIELD=1</c> - every
/// slot-7 row is still dropped, exactly as in wave 5.
/// </para>
/// <para>
/// The slot-7 <b>attachment</b> is not affected and is deliberately untouched: the attachment list
/// is a separate list, it carried the unchanged <c>Weapon_Empty.adr</c> in 3 of the 4 crashes, and
/// it is what draws a mesh in the hand.
/// </para>
/// </summary>
public static class ActiveHandRowGuard
{
    /// <summary>
    /// <c>EquipmentSlotDefinitions.txt</c> row 7, <c>SLOT_NAME = RHand</c> - the active hand. This
    /// is the same value as <see cref="AugustHeldWeapon.RightHandSlotId"/>, restated here so the
    /// guard does not depend on the weapon-choice type.
    /// </summary>
    public const uint ActiveHandSlotId = 7;

    /// <summary>True when this row would bind an item into the active hand and crash the client.</summary>
    public static bool IsActiveHandRow(EquipmentSlotRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.SlotId == ActiveHandSlotId;
    }

    /// <summary>
    /// True when this row is one the client cannot survive: an active-hand row for an item
    /// <paramref name="clearance"/> has no positive fire-group evidence for. A null clearance - the
    /// shipped default - makes every active-hand row crashing, which is the wave-5 behaviour.
    /// </summary>
    public static bool IsCrashingRow(EquipmentSlotRow row, IActiveHandClearance? clearance)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!IsActiveHandRow(row))
        {
            return false;
        }

        // docs/58 §11 stage 3. The clearance answers only for ONE guid at a time, and only on the
        // strength of packets this session has already written (WeaponFireGroupLedger).
        return clearance?.IsClearedForActiveHand(row.ItemGuid) != true;
    }

    /// <summary>
    /// <paramref name="rows"/> with every active-hand row removed. Returns the input list unchanged
    /// (no allocation) in the normal case where there is nothing to remove - which, with
    /// <c>InventoryOptions.WieldFirstWeapon = false</c>, is every call.
    /// </summary>
    public static IReadOnlyList<EquipmentSlotRow> Filter(IReadOnlyList<EquipmentSlotRow>? rows) =>
        Filter(rows, clearance: null);

    /// <summary>
    /// <paramref name="rows"/> with every active-hand row removed <em>except</em> those
    /// <paramref name="clearance"/> vouches for by item guid (docs/58 §11 stage 3). Pass null - or
    /// use the single-argument overload - for the unconditional wave-5 behaviour.
    /// </summary>
    public static IReadOnlyList<EquipmentSlotRow> Filter(
        IReadOnlyList<EquipmentSlotRow>? rows,
        IActiveHandClearance? clearance)
    {
        if (rows is null || rows.Count == 0)
        {
            return [];
        }

        int crashing = 0;
        foreach (EquipmentSlotRow row in rows)
        {
            if (IsCrashingRow(row, clearance))
            {
                crashing++;
            }
        }

        if (crashing == 0)
        {
            return rows;
        }

        var kept = new List<EquipmentSlotRow>(rows.Count - crashing);
        foreach (EquipmentSlotRow row in rows)
        {
            if (!IsCrashingRow(row, clearance))
            {
                kept.Add(row);
            }
        }

        return kept;
    }
}

/// <summary>
/// The weapon Cranberry hands the player to make the client emit its own combat packets, chosen
/// entirely from the August client's own datasheets under <c>out/data_aug</c>.
/// <para>
/// <b>Why definition 2425 ("AR-15")</b> and not definition 68 ("Rifle"), which carries the same
/// mesh family:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>item-names-en_us.json</c> (the client's own locale, docs/28) names <c>2425</c> <b>"AR-15"</b>
/// — a shipped KOTK weapon — and names <c>68</c> "Rifle", a legacy row.
/// </description></item>
/// <item><description>
/// <c>2425</c>'s <c>MODEL_NAME</c> is <c>Weapon_M16A4_3p.adr</c>, which is a <b>key of
/// <c>FirstPersonAttachments.txt</c></b> (<c>Weapon_M16A4_3P.adr ^ Weapon_M16A4.adr</c>): the
/// client resolves the first-person view model from the third-person attachment mesh itself. Row
/// <c>68</c>'s <c>MODEL_NAME</c> is the <em>first-person</em> mesh <c>Weapon_M16A4.adr</c>, which is
/// not a key of that table, so attaching it would put a view model on the character's back and
/// leave first person with no remap. This is the same convention docs/13 §3a recorded for the
/// bandage (item 2423 names the held mesh <c>…_3P.adr</c>).
/// </description></item>
/// <item><description>
/// <c>2425</c> has <c>ACTIVE_EQUIP_SLOT_ID = 7</c> (<c>EquipmentSlotDefinitions.txt</c> row
/// <c>7^…^RHand^…^WeaponSwitch^</c>), <c>PASSIVE_EQUIP_SLOT_ID = 76</c> (stowed on the back),
/// <c>FLAG_CAN_EQUIP = 1</c>, <c>MAX_STACK_SIZE = 1</c>, <c>ITEM_CLASS = 25036</c> whose
/// <c>ItemClasses.txt</c> <c>WIELD_TYPE</c> is <b>2</b> — the two-handed wield type shared by every
/// rifle row. Definition 85 ("Fists", class 25006) has <c>WIELD_TYPE 0</c>, which is why the empty
/// hand renders the relaxed idle.
/// </description></item>
/// <item><description>
/// It exists in the world: <c>Models.txt</c> has <c>9591 Weapon_M16A4_3P.adr</c> (attachment),
/// <c>9087 Weapon_M16A4.adr</c> (first person), <c>23 Weapon_M16A4_OnGround.adr</c> (the ground
/// prop) and <c>9268 ItemSpawner_Weapon_M16A4.adr</c> (the Z2 placement marker of the
/// <c>Weapons01</c> spawner category, docs/29).
/// </description></item>
/// </list>
/// <para>
/// Definitions <c>2239</c> and <c>2425</c> are byte-identical on every column that matters here
/// (same name id 32, class, model, slots, bulk 1500, image set 7); either works and <c>2425</c> is
/// the later row. <c>1889</c> and <c>2230</c> share the mesh but differ in <c>PARAM1</c> (the field
/// that groups <c>68</c>/<c>2239</c>/<c>2425</c> as <c>6</c>) and <c>2230</c> is named "Steyr Aug",
/// so they are deliberately not used.
/// </para>
/// <b>No ballistics are asserted here</b> (docs/16 §2, clean-room): damage, fire rate, magazine size
/// and spread are not read from any table by this type. It only makes the weapon appear and be
/// held.
/// </summary>
public static class AugustHeldWeapon
{
    /// <summary><c>ClientItemDefinitions.txt</c> row 2425, locale name "AR-15".</summary>
    public const uint ItemDefinitionId = 2425;

    /// <summary>The byte-identical alternate row, kept so a live A/B needs no new derivation.</summary>
    public const uint AlternateItemDefinitionId = 2239;

    /// <summary><c>NAME_ID</c> of row 2425 — the display string id for a name plate or prompt.</summary>
    public const uint NameId = 32;

    /// <summary><c>ITEM_CLASS</c> of row 2425; <c>ItemClasses.txt</c> gives it <c>WIELD_TYPE</c> 2.</summary>
    public const uint ItemClass = 25036;

    /// <summary><c>ItemClasses.txt</c> <c>WIELD_TYPE</c> for <see cref="ItemClass"/> — two-handed.</summary>
    public const uint WieldType = 2;

    /// <summary>
    /// <c>EquipmentSlotDefinitions.txt</c> row 7, <c>SLOT_NAME = RHand</c>. An
    /// <c>EquipmentSlotRow</c> naming this slot crashes the client - see
    /// <see cref="ActiveHandRowGuard"/>. The <em>attachment</em> for this slot is unaffected.
    /// </summary>
    public const uint RightHandSlotId = 7;

    /// <summary><c>PASSIVE_EQUIP_SLOT_ID</c> of row 2425 — where the client stows it when unheld.</summary>
    public const uint StowedSlotId = 76;

    /// <summary>
    /// <c>MODEL_NAME</c> of row 2425 and a key of <c>FirstPersonAttachments.txt</c>; the client maps
    /// it to <see cref="FirstPersonModelName"/> by itself. This is the string that goes in the
    /// slot-7 <see cref="CharacterEquipmentAttachment"/>.
    /// </summary>
    public const string AttachmentModelName = "Weapon_M16A4_3p.adr";

    /// <summary>
    /// The first-person mesh the client resolves for <see cref="AttachmentModelName"/>
    /// (<c>FirstPersonAttachments.txt</c>, <c>Models.txt</c> row 9087). Recorded, never sent.
    /// </summary>
    public const string FirstPersonModelName = "Weapon_M16A4.adr";

    /// <summary>
    /// <c>Models.txt</c> row <b>23</b>, <c>Weapon_M16A4_OnGround.adr</c> — the ground prop, i.e. the
    /// <c>AddLightweightItem.GroundModelId</c> to use when this weapon is spawned as loot instead of
    /// granted directly. (<c>ZoneOptions.GroundLootModelId</c>'s current 9066 is the bandage roll.)
    /// </summary>
    public const uint GroundModelId = 23;

    /// <summary>
    /// The mesh Cranberry attaches to slot 7 today — <c>ClientItemDefinitions.txt</c> row 85,
    /// "Fists", class 25006, <c>WIELD_TYPE</c> 0. Replacing this attachment is the whole visible
    /// half of the change (<c>CharacterVisuals.FromSelection</c>).
    /// </summary>
    public const string EmptyHandModelName = "Weapon_Empty.adr";

    /// <summary>Definition id of <see cref="EmptyHandModelName"/>.</summary>
    public const uint EmptyHandItemDefinitionId = 85;

    /// <summary>
    /// The slot-7 attachment that renders the weapon in the character's hand. <c>TextureAlias</c> is
    /// the datasheet default (<c>TEXTURE_ALIAS</c> on row 2425 is empty, and every live Cranberry
    /// attachment uses "Default"); no appearance rows are attached, because row 2425 is a base item
    /// rather than a wardrobe skin and <c>AugustDynamicAppearanceTable</c> has no map for it.
    /// </summary>
    public static CharacterEquipmentAttachment Attachment() =>
        new(AttachmentModelName, RightHandSlotId);

    /// <summary>
    /// The equipment-slot row that binds this weapon's inventory instance to RHand. See
    /// <see cref="EquipmentSlotRow"/> for what is proven and what is not.
    /// <para>
    /// <b>This row is dropped by <see cref="ActiveHandRowGuard"/> before it reaches the wire</b>
    /// (docs/45): a slot-7 row hard-crashes the August client until the weapon has fire-group data.
    /// The method is kept - rather than deleted - so the shape stays derived and citable, and so
    /// the guard has something to be tested against.
    /// </para>
    /// </summary>
    public static EquipmentSlotRow SlotRow(ulong itemGuid) =>
        new(RightHandSlotId, itemGuid);

    /// <summary>
    /// The inventory record for one granted AR-15, shaped for <see cref="ItemAdd"/>. Durability is
    /// left at zero: no client-shipped table read in this lane names a durability value for row
    /// 2425, and inventing one would be a fact without a citation.
    /// </summary>
    public static InventoryItem InventoryRecord(
        ulong itemGuid,
        ulong ownerGuid,
        uint slotId,
        ulong containerGuid = 0,
        uint containerDefinitionId = 0) =>
        new(
            DefinitionId: ItemDefinitionId,
            ItemGuid: itemGuid,
            Count: 1,                   // MAX_STACK_SIZE = 1 on row 2425
            OwnerGuid: ownerGuid,
            ContainerGuid: containerGuid,
            ContainerDefinitionId: containerDefinitionId,
            SlotId: slotId);
}
