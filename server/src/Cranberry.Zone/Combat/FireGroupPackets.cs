using Cranberry.Protocol;

namespace Cranberry.Zone.Combat;

// WeaponBase.AddFireGroup (0x82 / u8 sub 0x11) - docs/56 §1.6, which closed the payload docs/45 §3b
// had to leave BLOCKED.
//
// WHY THIS FILE EXISTS AND WHY NOTHING CALLS IT.
//
// The owner cannot punch because the August client's ONLY local attack entry point (FUN_1411ceca0,
// reached from the input controller FUN_14158ef20+0x3887) resolves the ACTIVE-HAND equipment slot,
// maps it to an item, and reads the weapon component at item+0xa8. With nothing bound to body
// slot 7 the whole block is skipped - no fallback, no built-in fist - and the client never even
// tries: across all three of the owner's 2026-08-30 captures it sent zero 0x82 packets of any sub
// (docs/56 §1.3, LIVE-NEGATIVE).
//
// Fists are therefore NOT a special case. Item 85 "Fists" is an ordinary CODE_FACTORY_NAME = Weapon
// row with ACTIVE_EQUIP_SLOT_ID 7 and PASSIVE_EQUIP_SLOT_ID 0 - it cannot even be stowed - so
// making the owner punch needs exactly the body-slot-7 row REGRESSION GUARD 5 forbids, and sending
// that row before the client has fire-group data reproduces the docs/45 minidump
// (EXCEPTION_ACCESS_VIOLATION reading 0x38 under FUN_14228d840). docs/56 §1.5 is unambiguous: do
// not lift ActiveHandRowGuard to test fists.
//
// So this file ships docs/56 §1.9 STEP 1 and nothing else: the packet type, built to the recovered
// layout, tested, and DELIBERATELY UNREFERENCED. It cannot crash anything because no call site
// sends it. Step 2 (send it for the fists behind an option defaulting off, still with no slot-7
// row) and step 3 (narrow - never remove - the guard) belong to a later wave and to the lane that
// owns the landing burst. (EquipmentGuardCoverageTests, which used to grep this file, was deleted
// under D150 on 2026-09-02; the guard itself is unchanged.)
//
// LABEL: DERIVED from decompilation of the August binary. NOT TESTED against the client - no byte
// of this layout has ever been on a wire.

/// <summary>
/// One fire mode inside an <see cref="AddFireGroup"/> payload - <b>13 bytes</b>, the record read by
/// <c>FUN_141483ec0</c> (docs/56 §1.6):
/// <code>
/// u8  -> mode + 0x10
/// u32 -> mode + 0x14
/// i32 a; i32 b;                 // both always read
/// if (a &gt; 0) mode + 0x18 = a;
/// if (b &gt; 0) mode + 0x18 = b;   // b wins when positive
/// </code>
/// <para>
/// <b><c>mode + 0x18</c> is the field the local trigger gate tests.</b>
/// <c>FUN_142291b90(weapon, group, modeIndex)</c> returns 0 unless
/// <c>modeIndex &lt; group.modeCount</c> <em>and</em> <c>mode-&gt;+0x18 &gt; 0</c>, so a mode whose
/// <see cref="EffectiveCharge"/> is 0 can never swing or fire (docs/56 §1.7).
/// </para>
/// <para>
/// <b>OPEN (docs/56 open question 2):</b> which of <see cref="Flags"/> and <see cref="EffectId"/> is
/// the fire-mode id and which the composite-effect id is <em>not</em> asserted here - both are read
/// into the mode struct and neither is interpreted by anything Cranberry sends.
/// </para>
/// </summary>
/// <param name="Flags">The <c>u8</c> the client stores at <c>mode + 0x10</c>.</param>
/// <param name="EffectId">The <c>u32</c> the client stores at <c>mode + 0x14</c>.</param>
/// <param name="Charge">The first <c>i32</c>; written to <c>mode + 0x18</c> when positive.</param>
/// <param name="ChargeAlt">The second <c>i32</c>; overrides <paramref name="Charge"/> when positive.</param>
public sealed record FireModeDefinition(byte Flags, uint EffectId, int Charge, int ChargeAlt = 0)
{
    /// <summary>Bytes one fire mode occupies: <c>u8; u32; i32; i32</c>.</summary>
    public const int Length = 13;

    /// <summary>
    /// The value the client ends up with at <c>mode + 0x18</c>, following <c>FUN_141483ec0</c>'s two
    /// independent <c>&gt; 0</c> tests in order. 0 means "the trigger gate will refuse this mode".
    /// </summary>
    public int EffectiveCharge => ChargeAlt > 0 ? ChargeAlt : Charge > 0 ? Charge : 0;

    /// <summary>Writes the 13-byte mode record in <c>FUN_141483ec0</c>'s read order.</summary>
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Flags);        // -> mode + 0x10
        writer.WriteUInt32(EffectId);   // -> mode + 0x14
        writer.WriteInt32(Charge);      // -> mode + 0x18 when > 0
        writer.WriteInt32(ChargeAlt);   // -> mode + 0x18 when > 0, overriding the previous
    }
}

/// <summary>
/// The body of an <see cref="AddFireGroup"/> - one fire group and its fire modes, i.e. the raw
/// sub-blob whose byte count the envelope carries.
/// <para>
/// <b>Layout</b> (docs/56 §1.6, from <c>FUN_1414859b0</c> -> <c>FUN_141484300</c> ->
/// <c>FUN_141483ec0</c>): <c>u32 fireGroupId; i8 fireModeCount;</c> then <c>fireModeCount</c> x
/// <see cref="FireModeDefinition"/>, so the payload length is always <c>5 + 13 * modeCount</c>.
/// <c>FUN_1414859b0</c> reads the id, <em>rewinds the cursor</em>, appends the group with
/// <c>FUN_14228be00(item + 0xa8, id)</c> and then re-reads the whole body into the entry at index
/// <c>*(int*)(item + 0xe0) - 1</c>; <c>FUN_142290f60</c> writes <c>entry + 0x18 = id</c>,
/// <c>entry + 0x10 = modeCount</c> and <c>entry + 0x08 = mode array</c> (stride <c>0x28</c>) - the
/// exact struct docs/45 §3a derived independently from the <em>readers</em>, which is what makes
/// this layout two-route corroborated.
/// </para>
/// </summary>
public sealed record FireGroupDefinition
{
    /// <summary>Bytes before the first mode: <c>u32 fireGroupId; i8 fireModeCount</c>.</summary>
    public const int HeaderLength = 5;

    /// <summary>
    /// The fire-mode index the client's single-shot/melee branch hard-codes:
    /// <c>FUN_142291b90(weapon, *(u32*)(item + 0x10c), <b>1</b>)</c>. The automatic branch asks for
    /// modes 0 <em>and</em> 1. So a one-mode group can never attack (docs/56 §1.7).
    /// </summary>
    public const int TriggerFireModeIndex = 1;

    /// <summary>
    /// The smallest mode count that can satisfy <see cref="TriggerFireModeIndex"/>. Not a rail on
    /// what may be built - a group with fewer modes is a legal packet, it simply cannot swing.
    /// </summary>
    public const int MinimumModesForLocalAttack = TriggerFireModeIndex + 1;

    /// <summary>
    /// <c>fireModeCount</c> is read as a <b>signed</b> byte (<c>FUN_141484300</c>), so a count above
    /// this would arrive negative: the client would skip every mode while the envelope still
    /// claimed their bytes, leaving the group empty and the crash back on the table.
    /// </summary>
    public const int MaximumModes = sbyte.MaxValue;

    /// <summary>Builds a fire group. <paramref name="modes"/> may be empty but not null.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// More than <see cref="MaximumModes"/> modes - see that constant for why.
    /// </exception>
    public FireGroupDefinition(uint fireGroupId, IReadOnlyList<FireModeDefinition> modes)
    {
        ArgumentNullException.ThrowIfNull(modes);
        if (modes.Count > MaximumModes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(modes),
                modes.Count,
                $"A fire group carries at most {MaximumModes} modes; the client reads the count as a signed byte.");
        }

        foreach (FireModeDefinition mode in modes)
        {
            ArgumentNullException.ThrowIfNull(mode, nameof(modes));
        }

        FireGroupId = fireGroupId;
        Modes = modes;
    }

    /// <summary>The group id, written to <c>entry + 0x18</c> and looked up through <c>weapon-&gt;vtable[1]</c>.</summary>
    public uint FireGroupId { get; }

    /// <summary>The group's fire modes, in wire order.</summary>
    public IReadOnlyList<FireModeDefinition> Modes { get; }

    /// <summary>Always <c>5 + 13 * <see cref="Modes"/>.Count</c> - the <c>i32 n</c> of the envelope.</summary>
    public int PayloadLength => HeaderLength + (FireModeDefinition.Length * Modes.Count);

    /// <summary>
    /// True when this group could actually let the local player attack: at least
    /// <see cref="MinimumModesForLocalAttack"/> modes, and mode <see cref="TriggerFireModeIndex"/>
    /// carrying a positive <see cref="FireModeDefinition.EffectiveCharge"/>. A group that fails this
    /// is still a valid packet - it just does nothing (docs/56 §1.7).
    /// </summary>
    public bool SupportsLocalAttack =>
        Modes.Count > TriggerFireModeIndex && Modes[TriggerFireModeIndex].EffectiveCharge > 0;

    /// <summary>Writes the payload alone, without the <see cref="AddFireGroup"/> envelope.</summary>
    public void WritePayloadTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt32(FireGroupId);        // FUN_141484300: u32 fireGroupId
        writer.WriteByte((byte)Modes.Count);    // FUN_141484300: i8 fireModeCount
        foreach (FireModeDefinition mode in Modes)
        {
            mode.WriteTo(writer);
        }
    }
}

/// <summary>
/// <c>WeaponBase.AddFireGroup</c> (<c>0x82</c>, u8 sub <c>0x11</c>) - the packet that gives an item's
/// weapon component the runtime fire-group state the client's attack path dereferences.
/// <para>
/// <b>Envelope</b> (docs/45 §3b for the envelope, docs/56 §1.6 for the payload), on top of the
/// 6-byte <c>0x82</c> family header of docs/20 §2 - <c>u8 opcode; u32 read-but-never-used; u8 sub</c>,
/// which every sub-reader re-consumes from wire offset 0 via <c>FUN_140a2e9b0</c>:
/// <code>
/// u8  0x82
/// u32 0
/// u8  0x11
/// u64 itemGuid          -> rec + 0x20
/// u8  flag              -> rec + 0x28   [meaning still OPEN - docs/56 open question 1]
/// i32 n                 -> rec + 0x38   the payload's BYTE count
/// u8  payload[n]        -> rec + 0x30
/// </code>
/// </para>
/// <para>
/// <b>This type has no call site, on purpose.</b> It is docs/56 §1.9 step 1: pure, tested, inert
/// code. Sending it is <em>by itself</em> harmless - the docs/45 crash needs an item in the active
/// hand, and <c>ActiveHandRowGuard</c> still makes that impossible - but the ordering rule must be
/// obeyed the day it does ship: <b>fire-group data first, body-slot-7 row second, never the other
/// way round</b>, and only ever with a group whose
/// <see cref="FireGroupDefinition.SupportsLocalAttack"/> is true.
/// </para>
/// </summary>
/// <param name="ItemGuid">The inventory item guid whose weapon component receives the group.</param>
/// <param name="Group">The fire group and its modes.</param>
/// <param name="Flag">
/// The <c>u8</c> at <c>rec + 0x28</c>, forwarded to <c>FUN_140db57b0</c> as its third argument. Read
/// and passed on by the client; <b>meaning unknown</b>, so it defaults to 0 and is exposed rather
/// than guessed at.
/// </param>
public sealed record AddFireGroup(ulong ItemGuid, FireGroupDefinition Group, byte Flag = 0)
{
    /// <summary><c>WeaponBase</c>, 0x82.</summary>
    public const byte Opcode = ZoneOpcodes.WeaponBase;

    /// <summary><c>AddFireGroup</c>, sub 0x11 (<c>FUN_140a46dd0</c>).</summary>
    public const byte SubOpcode = 0x11;

    /// <summary>
    /// Bytes before the payload: the 6-byte family header, <c>u64 itemGuid</c>, <c>u8 flag</c> and
    /// the <c>i32</c> byte count.
    /// </summary>
    public const int EnvelopeLength = 6 + 8 + 1 + 4;

    /// <summary>Total wire length.</summary>
    public int Length => EnvelopeLength + Group.PayloadLength;

    /// <summary>Serialises the whole packet.</summary>
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(Group);

        writer.WriteByte(Opcode);
        writer.WriteUInt32(0);                      // docs/20 §2: read by FUN_140a2e9b0, never used
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(ItemGuid);               // -> rec + 0x20
        writer.WriteByte(Flag);                     // -> rec + 0x28
        writer.WriteInt32(Group.PayloadLength);     // -> rec + 0x38, a BYTE count
        Group.WritePayloadTo(writer);               // -> rec + 0x30
    }

    /// <summary>
    /// The packet docs/56 §1.9 step 2 would send for item 85 ("Fists") - built here so the values
    /// are reviewed and pinned now rather than invented at a call site later. See
    /// <see cref="UnarmedFireGroup"/> for where each number comes from.
    /// </summary>
    public static AddFireGroup ForUnarmed(ulong fistsItemGuid) =>
        new(fistsItemGuid, UnarmedFireGroup.Group);
}

/// <summary>
/// The fire group Cranberry would give the fists, with the provenance of every number.
/// <b>Nothing sends this.</b>
/// </summary>
public static class UnarmedFireGroup
{
    /// <summary>
    /// <b>DERIVED.</b> <c>out\data_aug\ClientItemDatasheetData.txt</c> row 85 is
    /// <c>85^2^12^12^0^0^0^500^3200^0^674^0^</c>, i.e. <c>WEAPON_ID 12</c> and
    /// <c>FIRE_GROUP_ID <b>12</b></c> - and <c>ClientItemDefinitions.txt</c> row 85's <c>PARAM1</c>
    /// is <c>12</c> as well. The client's own data names this group for the fists.
    /// </summary>
    public const uint FireGroupId = 12;

    /// <summary>
    /// <b>DESIGN, and it must stay labelled as such.</b> Fists have <c>CLIP_SIZE 0</c> in the
    /// client's datasheet, so there is no extracted value for <c>mode + 0x18</c> - yet the trigger
    /// gate refuses any mode whose <c>+0x18</c> is not positive (docs/56 §1.7). 1 is the smallest
    /// sentinel that opens the gate; nothing in the August client says 1.
    /// </summary>
    public const int TriggerCharge = 1;

    /// <summary>
    /// <b>DESIGN.</b> Two modes, because <c>FUN_1411ceca0</c>'s melee branch hard-codes fire-mode
    /// index 1 and its automatic branch reads modes 0 <em>and</em> 1 - one mode can never swing.
    /// <see cref="FireModeDefinition.Flags"/> and <see cref="FireModeDefinition.EffectId"/> are left
    /// 0 rather than guessed: docs/56 open question 2 has not established what they mean, and 0 is
    /// the only value that claims nothing.
    /// </summary>
    public static FireGroupDefinition Group { get; } = new(
        FireGroupId,
        [
            new FireModeDefinition(Flags: 0, EffectId: 0, Charge: TriggerCharge),
            new FireModeDefinition(Flags: 0, EffectId: 0, Charge: TriggerCharge),
        ]);
}
