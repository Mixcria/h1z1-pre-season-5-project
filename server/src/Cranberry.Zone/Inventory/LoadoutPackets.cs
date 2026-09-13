using System.Buffers.Binary;

using Cranberry.Protocol;

namespace Cranberry.Zone.Inventory;

// Loadouts family 0x86 - the packets that bind an inventory item guid to a UI slot, i.e. the thing
// that decides whether a picked-up helmet appears on the head box or nowhere.
// Derivation: docs/41-inventory-slots.md §3c. DERIVED from the August binary; not LIVE-VERIFIED.
//
// Dispatcher FUN_140af3950 case 0x86 -> FUN_140b03170 -> FUN_140d37650(player+0xf548, ...), which
// reads u8 opcode; u8 sub and requires sub-1 < 7. Note the sub is a **u8** here, unlike the
// container family's u16 - sub width is per family in 1148 (docs/13 §4a).
//
//   sub 2  FUN_140d378c0                      loadout-definition set
//   sub 3  FUN_140d37bd0 -> FUN_140d37a00     SetCurrentLoadout
//   sub 4  FUN_140d37fa0 -> FUN_140d37d30     SetLoadoutSlots      (reader FUN_140d32b70)
//   sub 5  FUN_140d38390 -> FUN_140d38100     SetLoadoutSlot       (reader FUN_140d32ca0)
//   sub 6  FUN_140d394a0                      SelectSlot request   (c2s)
//   sub 7  FUN_140d37760                      SelectSlot

/// <summary>Sub-opcodes of the Loadouts family (<c>ZoneOpcodes.LoadoutsBase</c> = 0x86).</summary>
public static class LoadoutOpcodes
{
    /// <summary><c>FUN_140d37a00</c>: <c>u8 0x86; u8 3; u64 characterGuid; i32 loadoutId</c>.</summary>
    public const byte SetCurrentLoadoutSub = 0x03;

    /// <summary><c>FUN_140d32b70</c>: the whole slot list.</summary>
    public const byte SetLoadoutSlotsSub = 0x04;

    /// <summary><c>FUN_140d32ca0</c>: one slot.</summary>
    public const byte SetLoadoutSlotSub = 0x05;

    /// <summary><c>FUN_140d394a0</c>: client request to select one hotbar slot.</summary>
    public const byte SelectSlotRequestSub = 0x06;

    /// <summary><c>FUN_140d37760</c>: <c>u8 0x86; u8 7; i32; i32</c>.</summary>
    public const byte SelectSlotSub = 0x07;
}

/// <summary>
/// The loadout-slot record - the 25 bytes that bind an item instance to a loadout slot. Reader
/// <c>FUN_140a3bbb0</c> plus its hidden hand-off <c>FUN_140a3bda0</c>:
/// <code>
/// u32 loadoutId       -> record +0x00
/// u32 slotId          -> record +0x04   (LoadoutSlots.SLOT_ID; mirrored to +0x38 as the hash key)
/// u32 itemDefinitionId -> record +0x08  (docs/46 §3a - a HARD GATE; 0 makes it inert)
/// u64 itemGuid        -> record +0x10   (the instance guid granted by ClientUpdate.ItemAdd)
/// u8  flag            -> record +0x18   [LEAD - docs/41 L3]
/// u32 unknownC        -> record +0x20   [LEAD - docs/41 L3]
/// </code>
/// <c>FUN_140d38100</c>'s copy-out of the previous value touches exactly <c>+0x00, +0x04, +0x08,
/// +0x10 (u64), +0x18 (u8), +0x20</c> and keys the hash on <c>+0x04</c>, which corroborates every
/// field width above.
/// <para>
/// <b>Correction to docs/10.</b> Rows 1642-1644 of the original self-record layout reference record
/// <c>FUN_140a3bbb0</c> as three <c>u32</c>s (12 bytes). That is the extractor's known "no dump for
/// the callee" failure mode - <c>FUN_140a3bda0</c> had no decompile - and it misses 13 bytes per
/// element. Populating <c>SelfRecord.cs:509</c> with the 12-byte shape would abort the self-record
/// loader; this is the shape to use.
/// </para>
/// <para>
/// <b><see cref="ItemDefinitionId"/> is the field that made wave 3's bindings inert</b> (docs/46
/// §3a, closing docs/41 lead L3). It is the item definition id, and it is a hard gate that two
/// independent consumers apply before they do anything at all:
/// <c>FUN_140dc0150</c> - the <c>LoadoutSlotChanged</c> consumer reached from <c>FUN_140d38100</c> -
/// opens with <c>if (0 &lt; *(int *)(record + 8))</c> and then passes that value as the
/// definition-id key to <c>FUN_140da7b10</c>; <c>FUN_140d35250</c>, the weapon-wheel rebuild called
/// by both sub 4 and sub 5, applies the same test before it will put the slot on the wheel. A
/// record with 0 here contributes no occupied tile. It must still be present for an empty
/// destination: CanMoveItem checks the slot map before validating a drag (140d7d650).
/// Every <c>86 04</c>/<c>86 05</c> in <c>captures\wire-20260829-220829.txt</c> carried 0.
/// </para>
/// <para>
/// The record's <see cref="ItemGuid"/> is <b>not</b> the key the client looks the item up by. It
/// resolves the pair (definition id, slot id) against the item collection and requires the item to
/// carry <c>ContainerGuid = 0xFFFFFFFFFFFFFFFF</c> and <c>SlotId = this slot</c>; see
/// <see cref="PlayerInventory.EquippedContainerGuid"/>. <b>This supersedes docs/41 §3c.</b>
/// </para>
/// </summary>
public sealed record LoadoutSlotRecord(
    uint LoadoutId,
    uint SlotId,
    ulong ItemGuid,
    uint ItemDefinitionId = 0,
    bool Flag = false,
    uint UnknownC = 0)
{
    /// <summary><c>4 + 4 + 4 + 8 + 1 + 4</c>.</summary>
    public const int Length = 25;

    /// <summary>The <c>FUN_140a55fa0</c> list element: <c>u32 key</c> plus the record.</summary>
    public const int ListElementLength = 4 + Length;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt32(LoadoutId);      // +0x00
        writer.WriteUInt32(SlotId);         // +0x04
        writer.WriteUInt32(ItemDefinitionId);   // +0x08  the gate: 0 makes the record inert
        writer.WriteUInt64(ItemGuid);       // +0x10
        writer.WriteBool(Flag);             // +0x18  [lead]
        writer.WriteUInt32(UnknownC);       // +0x20  [lead]
    }
}

/// <summary>
/// <c>Loadouts.SetCurrentLoadout</c> (<c>0x86</c>, u8 sub 3; <c>FUN_140d37a00</c>):
/// <c>u8 0x86; u8 0x03; u64 characterGuid; i32 loadoutId</c> - 14 bytes. The survivor loadout is
/// Cranberry's active KOTK survivor loadout is <see cref="SurvivorLoadout.Id"/> = 17.
/// </summary>
public sealed record SetCurrentLoadout(ulong CharacterGuid, uint LoadoutId)
{
    public const byte Opcode = ZoneOpcodes.LoadoutsBase;
    public const byte SubOpcode = LoadoutOpcodes.SetCurrentLoadoutSub;
    public const int Length = 14;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
        writer.WriteInt32((int)LoadoutId);
    }
}

/// <summary>
/// The August client's hotbar request: <c>86 06 | u32 0 | u32 slotId | u32 clientGameTime</c>.
/// <para>
/// The first field is zero in <c>FUN_140d394a0</c>; the second is the selected loadout slot; and
/// the final value is formed from the client's game clock. The same three-dword shape is used by
/// the March server under family 0x87, independently confirming the packet object layout.
/// </para>
/// </summary>
public sealed record SelectLoadoutSlotRequest(uint Unknown, uint SlotId, uint ClientGameTime)
{
    public const byte Opcode = ZoneOpcodes.LoadoutsBase;
    public const byte SubOpcode = LoadoutOpcodes.SelectSlotRequestSub;
    public const int Length = 14;

    public static bool TryParse(ReadOnlySpan<byte> payload, out SelectLoadoutSlotRequest? request)
    {
        request = null;
        if (payload.Length < Length || payload[0] != Opcode || payload[1] != SubOpcode)
        {
            return false;
        }

        request = new SelectLoadoutSlotRequest(
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(2, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(6, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(10, 4)));
        return true;
    }
}

/// <summary>
/// Server-directed hotbar selection: <c>86 07 | u32 slotId | u32 loadoutId</c>. The client's
/// <c>FUN_140d37760</c> passes the first field to its local selection apply path with echo disabled.
/// <para>
/// <b>NEVER SENT (2026-09-02, S6 §7.4).</b> The type is kept because it is the layout of a packet
/// the client still accepts and because the byte test pins it, but no send site exists any more.
/// Two reasons: the owner's live A/B froze mouse-look and translation for as long as the gun was in
/// hand when this was published on a draw (docs/95), which is what proved that the client's
/// selection path is wired into locomotion; and <see cref="SetLoadoutSlots"/> / <see
/// cref="SetLoadoutSlot"/> now carry the trailing <c>currentSlotId</c> (§7.1), so the selection
/// travels with the table exactly as Z1 does it - Z1 has no <c>87 07</c> writer at all.
/// </para>
/// </summary>
public sealed record SelectLoadoutSlot(uint SlotId, uint LoadoutId)
{
    public const byte Opcode = ZoneOpcodes.LoadoutsBase;
    public const byte SubOpcode = LoadoutOpcodes.SelectSlotSub;
    public const int Length = 10;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(SlotId);
        writer.WriteUInt32(LoadoutId);
    }
}

/// <summary>
/// <c>Loadouts.SetLoadoutSlot</c> (<c>0x86</c>, u8 sub 5; reader <c>FUN_140d32ca0</c>, applier
/// <c>FUN_140d38100</c>): <c>u8 0x86; u8 0x05; u64 characterGuid; &lt;25-byte record&gt;;
/// u32 currentSlotId</c> - <b>39 bytes</b>. This is the single-slot delta a pickup sends.
/// <para>
/// <b>The trailing <c>u32 currentSlotId</c></b> (2026-09-02, S6 §7.1). <c>FUN_140d32ca0</c> is built
/// exactly like the sub-4 reader: after the 25-byte record it tests the cursor for four more bytes,
/// stores them at packet <c>+0x28</c>, and when they are missing writes 0 there and
/// <b><c>return 1</c></b> - which the applier reads as "do not apply". <c>FUN_140d38100</c> requires
/// <c>(int)DAT_143f8dbdc &lt; value</c> before <c>FUN_1421e90e0</c> / <c>FUN_140dc0150</c> /
/// <c>FUN_1421e8f90</c> (which sets <c>loadout+0x38</c>, the current slot, and fires the listener's
/// <c>vtable+0x30</c>) / <c>FUN_140d35250(loadout, 0)</c> run. Dumps:
/// <c>out\inventory-research\loadout-slot-body</c>.
/// </para>
/// <para>
/// The reader stores the record's first <c>u32</c> a second time into the packet struct's
/// <c>+0x28</c> and the caller uses that as the loadout id: it reads the same four bytes twice, it
/// is not an extra field.
/// </para>
/// <para>
/// <b>Ordering.</b> The record names an item guid, so the <c>ClientUpdate.ItemAdd</c> that creates
/// that instance must already have been sent - the same rule docs/36 §W3 proved for the RHand
/// equipment-slot row.
/// </para>
/// </summary>
public sealed record SetLoadoutSlot(
    ulong CharacterGuid,
    LoadoutSlotRecord Slot,
    uint CurrentSlotId)
{
    public const byte Opcode = ZoneOpcodes.LoadoutsBase;
    public const byte SubOpcode = LoadoutOpcodes.SetLoadoutSlotSub;

    /// <summary><c>1 + 1 + 8 + 25 + 4</c> - the trailing current slot included.</summary>
    public const int Length = 14 + LoadoutSlotRecord.Length;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
        Slot.WriteTo(writer);
        writer.WriteUInt32(CurrentSlotId);      // FUN_140d32ca0 -> packet+0x28; FUN_140d38100 applies it
    }
}

/// <summary>
/// <c>Loadouts.SetLoadoutSlots</c> (<c>0x86</c>, u8 sub 4; reader <c>FUN_140d32b70</c>, applier
/// <c>FUN_140d37d30</c>):
/// <c>u8 0x86; u8 0x04; u64 characterGuid; u32 loadoutId; i32 n + n x { u32 key; 25-byte record };
/// u32 currentSlotId</c>.
/// <para>
/// <c>FUN_140a55fa0</c> is the <em>same</em> list reader the self record uses at
/// <c>SelfRecord.cs:509</c>, so the self record's loadout list element is this 29-byte element too.
/// </para>
/// <para>
/// <b>The trailing <c>u32 currentSlotId</c> is not optional</b> (2026-09-02, S6 §7.1; dumps under
/// <c>out\inventory-research\loadout-slot-body</c>). <c>FUN_140d32b70</c>'s stack cursor is
/// <c>{start, len, cur, end, error}</c>; <c>FUN_140a55fa0</c> advances <c>cur</c> past the whole
/// list, and the reader then does
/// <c>if (end &lt; cur + 4) { packet+0x28 = 0; return 1; } packet+0x28 = *(u32 *)cur; return error;</c>
/// - i.e. a packet that stops after the last record makes the reader <b>return 1</b>. The applier
/// <c>FUN_140d37d30</c> only runs its tail when the reader returned 0, so <em>the slot records still
/// land</em> (they were read straight into the live loadout) while the current slot, the wheel
/// rebuild <c>FUN_140d35250(loadout, 1)</c> - which selects the wheel index whose slot equals
/// <c>loadout+0x38</c> - and five listener refreshes are all skipped. Every <c>86 04</c> Cranberry
/// sent before 2026-09-02 was short by these four bytes.
/// </para>
/// <para>
/// The value follows Z1's rule (<c>ZoneLoadout.cs:309, 556-559</c>, adopted under D53):
/// <b>never 0</b> - "0 is not a slot of any loadout" - so an empty hand sends
/// <see cref="SurvivorLoadout.Fists"/> (7). See <see cref="LoadoutSelectionRule"/>.
/// </para>
/// </summary>
public sealed record SetLoadoutSlots(
    ulong CharacterGuid,
    uint LoadoutId,
    IReadOnlyList<LoadoutSlotEntry> Slots,
    uint CurrentSlotId)
{
    public const byte Opcode = ZoneOpcodes.LoadoutsBase;
    public const byte SubOpcode = LoadoutOpcodes.SetLoadoutSlotsSub;

    /// <summary>
    /// Bytes before the first list element: <c>1 + 1 + 8 + 4 + 4</c>. The trailing
    /// <c>currentSlotId</c> comes <em>after</em> the list, so this is not
    /// <see cref="EmptyLength"/> any more.
    /// </summary>
    public const int HeaderLength = 18;

    /// <summary>
    /// Envelope with an empty list: <see cref="HeaderLength"/> plus the trailing
    /// <c>u32 currentSlotId</c> - <c>1 + 1 + 8 + 4 + 4 + 4</c>.
    /// </summary>
    public const int EmptyLength = HeaderLength + 4;

    public int Length => EmptyLength + (LoadoutSlotRecord.ListElementLength * Slots.Count);

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
        writer.WriteUInt32(LoadoutId);
        writer.WriteInt32(Slots.Count);
        foreach (LoadoutSlotEntry entry in Slots)
        {
            entry.WriteTo(writer);
        }

        writer.WriteUInt32(CurrentSlotId);      // FUN_140d32b70 -> packet+0x28, read AFTER FUN_140a55fa0
    }
}

/// <summary>
/// One element of <see cref="SetLoadoutSlots"/>'s list (<c>FUN_140a55fa0</c>): <c>u32 key</c> then
/// the 25-byte record. Cranberry writes the slot id as the key, which is what the applier keys the
/// record's own hash on (<c>record+0x38</c> mirrors <c>+0x04</c>), so a re-send of the same slot
/// collides on the same bucket.
/// </summary>
public sealed record LoadoutSlotEntry(uint Key, LoadoutSlotRecord Slot)
{
    public const int Length = LoadoutSlotRecord.ListElementLength;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt32(Key);
        Slot.WriteTo(writer);
    }
}

/// <summary>
/// The one place the "what goes in the trailing <c>currentSlotId</c>" rule lives, for
/// <see cref="SetLoadoutSlots"/> and <see cref="SetLoadoutSlot"/>.
/// <para>
/// Z1's <c>CurrentSlotFor</c> (<c>ZoneLoadout.cs:556-559</c>, adopted under D53): the field is
/// <b>never 0</b>, because 0 is not a slot of any loadout - an empty hand reports the required Fists
/// slot 7. The August applier corroborates the shape of the rule: <c>FUN_140d37d30</c> /
/// <c>FUN_140d38100</c> compare the value against <c>DAT_143f8dbdc</c> before applying it, so a
/// below-sentinel value is dropped exactly like a missing trailer (S6 §10 Q1 - the sentinel's own
/// value is still [U], which is another reason not to send 0).
/// </para>
/// </summary>
public static class LoadoutSelectionRule
{
    /// <summary>The trailing <c>currentSlotId</c> for a raw selection value.</summary>
    public static uint CurrentSlotFor(uint currentLoadoutSlotId) =>
        currentLoadoutSlotId != 0 ? currentLoadoutSlotId : SurvivorLoadout.Fists;

    /// <summary>The trailing <c>currentSlotId</c> for a live inventory.</summary>
    public static uint CurrentSlotFor(PlayerInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return CurrentSlotFor(inventory.CurrentLoadoutSlotId);
    }
}
