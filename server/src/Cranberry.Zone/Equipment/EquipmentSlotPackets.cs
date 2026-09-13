using Cranberry.Protocol;
using Cranberry.Zone.Weapons;

namespace Cranberry.Zone.Equipment;

// The two Equipment subs Cranberry has never written (docs/94, docs/95).
//
// Cranberry has only ever sent 0x94 sub 1, SetCharacterEquipment - the WHOLE character, every
// attachment and every equipment row, in one packet. The owner's Z1 server never uses that packet
// to change a hand: it sends the per-slot pair 95 02 / 95 03 (1148: 94 02 / 94 03), sixty times in
// one reference session, and the whole-character form only for a dress.
//
// Both bodies are PROVEN from the August binary and independently corroborated, byte for byte, by a
// working 1087 writer. That is the strongest evidence standard this project has. Only sub 2 is
// DEFINED here; sub 3's type already existed and this wave only proved it (see the note below):
//
//   dispatcher  FUN_140cd8480     sub 1 -> FUN_140cd85e0   SetCharacterEquipment      (already sent)
//                                 sub 2 -> FUN_140cd8be0   SetCharacterEquipmentSlot  (this file)
//                                 sub 3 -> FUN_140cd97c0   UnsetCharacterEquipmentSlot(CharacterPackets.cs)
//                                 sub 4 -> FUN_140cd9020   SetCharacterEquipmentSlots (not needed)
//   out\ghidra-aug\wave10-equip-dispatch, wave10-equipslot, wave10-unsetslot

/// <summary>
/// <c>Equipment.SetCharacterEquipmentSlot</c> (<c>0x94</c> / sub <c>0x02</c>) — bind ONE item to
/// ONE body slot and attach ONE mesh, without re-sending the whole character.
///
/// <para>
/// <b>Layout — proven.</b> The August handler <c>FUN_140cd8be0</c> declares exactly two
/// <c>SoeUtil::StringFixed&lt;32&gt;</c> and four <c>SoeUtil::StringFixed&lt;64&gt;</c> locals — the
/// slot row's two aliases and the attachment's four — and hands the cursor to
/// <c>FUN_140cd4f90</c>, which reads, in this order:
/// </para>
/// <code>
/// thunk_FUN_140a2cda0(...)              u32 profileId; u64 characterGuid   (the SAME head 94 01 uses)
/// *(int *)(param_3 + 0x20)              u32 key
/// *(int *)(row + 0x20)                  u32 slotId
/// *(u64 *)(row + 0x28)                  u64 itemGuid
/// thunk_FUN_140980540(row + 0x30, ...)  str tintAlias
/// thunk_FUN_140980540(row + 0x70, ...)  str decalAlias
/// thunk_FUN_140a2ed70(...)              ONE attachment    (the SAME element reader 94 01 uses)
/// </code>
/// <para>
/// Those row offsets — <c>+0x20 slotId, +0x28 itemGuid, +0x30 tint, +0x70 decal</c> — are the very
/// offsets <see cref="EquipmentSlotRow"/> already cites for <c>FUN_140a39250</c>. It is the same
/// record, so this packet is built from two sub-structures the August client has already accepted
/// thousands of times inside <c>94 01</c>. There is <b>no</b> <c>u32 0</c>, <b>no</b> pair of alias
/// strings and <b>no</b> trailing <c>u8</c> here: those belong to <c>94 01</c> only.
/// </para>
/// <para>
/// <b>Corroboration.</b> Z1's own 21-field bytes for an AK-47 into RHand decode against this layout
/// with zero bytes left over, at exactly 128:
/// <c>9502 03000000 6cfcb9a7b430ab61 07000000 07000000 5ff8230000000030 "Default" "#"
/// "Weapon_AK47_3P.adr" "Default" "Default" "#" 0 0 0 7 0 [2: 97, 98] 00</c>.
/// </para>
/// <para>
/// <b>Guard 5 applies here exactly as it does to <c>94 01</c>.</b> This packet carries a single row,
/// so a refusal means the packet must not be written at all — <see cref="IsPermitted"/> is what the
/// caller asks first. Writing it anyway with the row stripped would produce a <c>94 02</c> with a
/// slot but no binding, which is not a smaller version of this packet, it is a malformed one.
/// </para>
/// </summary>
public sealed record SetCharacterEquipmentSlot(
    ulong CharacterId,
    EquipmentSlotRow Row,
    CharacterEquipmentAttachment Attachment,
    uint ProfileId = 3,
    IActiveHandClearance? Clearance = null)
{
    public const byte Opcode = ZoneOpcodes.EquipmentBase;

    /// <summary>Sub 2 in the August dispatcher <c>FUN_140cd8480</c>.</summary>
    public const byte SubOpcode = 0x02;

    /// <summary>
    /// False when <see cref="ActiveHandRowGuard"/> would drop this packet's only row — the caller
    /// must then send nothing at all rather than an empty one.
    /// </summary>
    public bool IsPermitted => !ActiveHandRowGuard.IsCrashingRow(Row, Clearance);

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(Row);
        ArgumentNullException.ThrowIfNull(Attachment);
        if (!IsPermitted)
        {
            throw new InvalidOperationException(
                $"{nameof(SetCharacterEquipmentSlot)} for body slot {Row.SlotId} item {Row.ItemGuid} "
                + "is refused by ActiveHandRowGuard; ask IsPermitted before writing.");
        }

        WriteBody(writer);
    }

    /// <summary>
    /// The explicit remote-character branch of FUN_140cd8be0 skips the local inventory /
    /// fire-group path and updates the identified actor's mesh and slot binding instead.
    /// A remote hand needs its own update: the owner's separately sent wield packet never
    /// reaches this observer. The self-addressed writer above retains its clearance guard.
    /// </summary>
    public void WriteForObserverTo(PacketWriter writer, ulong observerCharacterId)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (CharacterId == 0 || observerCharacterId == 0 || CharacterId == observerCharacterId
            || Row.ItemGuid == 0 || Row.SlotId == 0 || Row.SlotId != Attachment.SlotId)
            throw new ArgumentException("Observer equipment must identify another character and a bound body slot.");
        WriteBody(writer);
    }

    private void WriteBody(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(ProfileId);          // FUN_140a2cda0 head
        writer.WriteUInt64(CharacterId);        // FUN_140a2cda0 head
        Row.WriteTo(writer);                    // FUN_140cd4f90 -> the FUN_140a39250 record
        Attachment.WriteTo(writer);             // thunk_FUN_140a2ed70
    }
}

// NOTE - UnsetCharacterEquipmentSlot is NOT redefined here. Cranberry already had the type, in
// CharacterPackets.cs, left behind when docs/32 removed its burst from the dress path. This wave
// derived the August reader independently (FUN_140cd97c0, below) and the existing type turned out
// to be byte-for-byte correct, so it is reused rather than duplicated:
//
//   u8 base; u8 sub; u32 profileId; u64 characterGuid; u32 unknown; u32 slotId      = 22 bytes
//
//   FUN_140cd97c0 reads exactly those six, in that order; local_28 (the guid) is checked against
//   DAT_143f85728 (the local player) and local_18[0] (the slot id) is the value every downstream
//   slot-clearing call is handed. local_20 (the unknown u32) is read and never used; Z1 writes 0.
//   Z1's own bytes: 9503 03000000 6cfcb9a7b430ab61 00000000 4c000000 - same fields, same order,
//   and the only value that varies across its 25 sends is the trailing slot id (76, 77).
//
// docs/32 still stands and is about the CALLER, not the packet: an unset BURST on the dress path is
// what hung the client after ClientBeginZoning. One unset, for one slot, at the moment an item
// leaves it - which is what Z1 does - is a different use. It must never go back into the dress.
