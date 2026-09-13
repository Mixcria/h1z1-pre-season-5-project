using Cranberry.Protocol;
using Cranberry.Zone.Combat;

namespace Cranberry.Zone.Weapons;

// The Weapon-class ClientUpdate.ItemAdd tail - docs/58 §5, the SECOND independent cause of the
// docs/45 crash.
//
// FUN_140c35400 reads the 62-byte base item record with FUN_140a3aa60, builds the item through
// CreateItem, and then calls the created object's vtable+0x50 ON THE SAME CURSOR. For the base item
// class that is FUN_140bcdc70, which reads exactly ONE byte - so Cranberry's 1-byte
// ItemAdd.GenericItemClassTail is CORRECT for Generic / EquippableContainer / InfantryEquipment
// (this closes docs/13 blocker 2). For the Weapon class it is FUN_14148c3c0, which reads that same
// byte and then keeps going for another 67. Cranberry stops after one, so the cursor is exhausted,
// the i8 fireGroupCount reads 0, and FUN_1414819a0 DESTRUCTIVELY SHRINKS the fire-group array to
// nothing - even if CreateItem had just built it. That destructor is not guarded by the cursor's
// overrun flag, which is why a truncated weapon tail is fatal rather than merely incomplete.
//
// Re-verified this wave against out\firegroup\gh-itemtail\…: FUN_14148c3c0, FUN_141483fa0,
// FUN_141484a20, FUN_141484b70, FUN_141484740, FUN_141484300. LABEL: DERIVED. Never on a wire.

/// <summary>
/// <c>WeaponComponent::Deserialize</c> (<c>FUN_141483fa0</c>) plus the two trailing lists of
/// <c>FUN_14148c3c0</c> - i.e. the whole per-item-class tail a <c>Weapon</c> row needs.
/// <para>
/// <b>Wire order [DERIVED, docs/58 §5]:</b>
/// <code>
/// u8   baseFlag                 FUN_140bcdc70   -&gt; item+0x59   (the byte Cranberry already writes)
/// i32  n0; u32 v[n0]            FUN_141484a20   -&gt; comp+0x10   THE AMMO-SLOT ARRAY (magazine)
/// i8   fireGroupCount           FUN_141483fa0   -&gt; comp+0x38   *** GROWS or DESTRUCTIVELY SHRINKS ***
///   repeat fireGroupCount:      FUN_141484300 - the SAME reader 82 11 uses
///     u32 fireGroupId           -&gt; entry+0x18
///     i8  fireModeCount         -&gt; entry+0x10
///     repeat fireModeCount:     FUN_141483ec0 - 13 bytes each
///       u8 flags; u32 effectId; i32 a; i32 b       (mode+0x18 = b &gt; 0 ? b : a &gt; 0 ? a : 0)
/// i8   -&gt; comp+0x40   u8  -&gt; comp+0x44   u32 -&gt; comp+0x48
/// i8   -&gt; comp+0x64   CURRENT FIRE-GROUP INDEX (== item+0x10c; what FUN_14228d840 indexes)
/// i8   -&gt; comp+0x6c   i8  -&gt; comp+0x70   u32 -&gt; comp+0x74
/// u8   -&gt; comp+0xa8   u32 -&gt; comp+0xac   i8  -&gt; comp+0x7c (stored as a float)  u32 -&gt; comp+0xe8
/// i32  n1                       FUN_141484b70   -&gt; item+0x198  (hashed-string entries)
/// i32  n2                       FUN_141484740   -&gt; item+0x1c8  (u32-keyed entries)
/// </code>
/// The fixed cost after the fire groups is <b>23 bytes</b>; with <c>n0 = n1 = n2 = 0</c> and one
/// two-mode group the tail is <b>68 bytes</b>, so the <c>ItemAdd</c> blob is <c>62 + 68 = 130</c>.
/// With <see cref="AmmoSlot"/> on the tail is <b>72</b> and the blob <b>134</b>.
/// </para>
/// <para>
/// <b>Wave 13 (docs/107 §1) - <c>n0</c> is the MAGAZINE and it was shipped empty.</b> The leading
/// counted array at <c>comp+0x10</c> is the weapon's ammo-slot array: the owner's Z1 writes
/// <c>ammoSlotsCount = 1</c> followed by <c>u32 ammoInMagazine</c> in exactly this position
/// (<c>C:\Z1\Server\Zone\ZoneInventory.cs:3140-3160</c>, gated on
/// <c>AmmoItemId != 0 &amp;&amp; ClipSize &gt; 0</c>; S6 §2.4 reads his tail against this one), and
/// Cranberry wrote <c>n0 = 0</c> from wave 5 to wave 12 - so the client was handed a weapon with a
/// magazine CAPACITY (fire mode 0's charge) and no CONTENTS. <b>The hotbar counter had exactly one
/// input for the whole session and that input was "empty".</b> That is the whole of
/// <c>FIRE-PATH-DIAGNOSIS.md</c> §1.6 / rank 2b: "shows bullets" and "shows 0 forever" are the same
/// never-written field. <see cref="Magazine"/> fills it; <see cref="AmmoSlot"/> is the revert.
/// </para>
/// <para>
/// <b>Wave 12 correction.</b> This block used to read "everything Cranberry writes here that is not
/// a fire group is zero, and deliberately". Four of those zeros were wrong, not merely unknown:
/// <c>comp+0x44</c> is the weapon state machine and <b>0 is not in the client's own "not busy" set
/// <c>{1,3,9,0xb,0xc,0xe}</c></b> (<c>FUN_142291930</c>), and <c>+0x6c</c>/<c>+0x70</c>/<c>+0xe8</c>
/// are the fields the client's own <c>82 1c Reset</c> (<c>FUN_142293450</c>) and Z1's tail both put
/// -1 in. <see cref="IdleState"/> ships Z1's values there (S6 §2.4); the remaining scalars
/// (<c>+0x48/0x74/0xa8/0xac/0x7c</c>) really are docs/58 U6, and the <c>n1</c>/<c>n2</c> lists are
/// docs/58 U5 (attachments and mods; empty is correct for a weapon with none).
/// </para>
/// </summary>
/// <param name="Groups">
/// The fire groups the weapon component ends up owning. <b>The count is what saves the client</b>:
/// an empty list reproduces exactly the wave-5 failure mode, so <see cref="IsSafeForActiveHand"/>
/// refuses to clear it.
/// </param>
/// <param name="CurrentFireGroupIndex">
/// <c>comp+0x64</c> = <c>item+0x10c</c> - the index <c>FUN_14228d840</c> uses to pick the group the
/// crash site dereferences. It must address a real entry, which is why 0 is the only value Cranberry
/// sends. docs/58 §2b: this field and <c>item+0x10c</c> are the same field (<c>0xa8 + 0x64</c>).
/// </param>
/// <param name="BaseFlag">
/// <c>FUN_140bcdc70</c> -&gt; <c>item+0x59</c>. Cranberry's existing 1-byte tail is exactly this byte
/// with the value 0, so keeping 0 here changes nothing that is already live.
/// </param>
/// <param name="IdleState">
/// <b>Wave 12 (S6 §2.4 / §7.2), default true</b> - write Z1's idle scalars instead of the block of
/// zeros. <c>WeaponStageOptions.TailIdleState</c> (<c>CRANBERRY_WEAPON_TAIL_STATE=0</c>) is the
/// revert, applied in <c>WeaponSession.CreateTail</c>. See <see cref="IdleStateScalars"/> for what
/// each value is and where it came from. The tail's <em>length</em> is identical either way.
/// </param>
/// <param name="Magazine">
/// <b>Wave 13, docs/107 §1.</b> The rounds currently in the weapon, written as the single element
/// of the <c>comp+0x10</c> ammo-slot array when <see cref="AmmoSlot"/> is on. <b>0 is a truthful
/// value</b>, not a placeholder: under <c>AmmoOptions.GunsSpawnEmpty</c> a looted gun really does
/// arrive empty, and the difference from wave 12 is that the client is now TOLD 0 in a field it can
/// later see change, instead of being handed an array with no element in it at all. Negative values
/// clamp to 0. The caller is <c>WeaponSession.CreateItemAdd</c>, which asks the session's own
/// <c>ShooterCombatState</c> for the live count so a re-announce after a reload carries the reload.
/// </param>
/// <param name="AmmoSlot">
/// <b>Wave 13, default true</b> - write the ammo-slot array as <c>n0 = 1</c> plus
/// <see cref="Magazine"/>. <c>WeaponStageOptions.WriteWeaponTailMagazine</c>
/// (<c>CRANBERRY_WEAPON_TAIL_AMMO=0</c>) is the revert, applied in
/// <c>WeaponSession.CreateTail</c>; off, this writes the wave-5..12 <c>n0 = 0</c> and the tail is
/// byte-identical to what the owner's three 2026-09-02 sessions received.
/// <b>This switch changes the tail's LENGTH</b> (68 -> 72 for a one-group weapon), unlike
/// <see cref="IdleState"/>, so the two are never interchangeable in a byte test.
/// <para>
/// <b>It defaults to FALSE on the record and is turned on by the table</b>
/// (<c>AugustWeaponTable.CreateTail</c>), which is the only place that knows whether the item HAS an
/// ammo slot - a weapon with no calibre and no magazine (the fists, a melee row, a throwable) must
/// keep the empty array, because the client's own <c>CreateItem</c> self-init sizes this vector from
/// the weapon definition's <c>def+0xd0</c> array (<c>FUN_142291180</c>) and a slot we declare for a
/// weapon whose definition has none is a wire disagreeing with the table sent beside it. Z1 gates it
/// the same way and in the same place (<c>ZoneInventory.cs:3140</c>).
/// </para>
/// </param>
public sealed record WeaponItemAddTail(
    IReadOnlyList<FireGroupDefinition> Groups,
    sbyte CurrentFireGroupIndex = 0,
    byte BaseFlag = 0,
    bool IdleState = true,
    int Magazine = 0,
    bool AmmoSlot = false)
{
    /// <summary>
    /// The bytes that are always present: <c>u8 baseFlag</c>, <c>i32 n0</c>, <c>i8 groupCount</c>,
    /// the 23-byte scalar block, <c>i32 n1</c>, <c>i32 n2</c>. The ammo-slot ELEMENT is not in here
    /// - it is present only with <see cref="AmmoSlot"/> (see <see cref="AmmoSlotElementLength"/>).
    /// </summary>
    public const int FixedLength = 1 + 4 + 1 + ScalarBlockLength + 4 + 4;

    /// <summary>
    /// One element of the <c>comp+0x10</c> ammo-slot array on the wire: a single <c>u32</c>, the
    /// rounds in the magazine. <c>FUN_141484a20</c> keeps its entries at a 0x10-byte stride in
    /// memory, but reads one <c>u32</c> per entry off the cursor (docs/58 §5); Z1's 1087 tail writes
    /// the same one <c>u32</c> per slot (S6 §2.4).
    /// </summary>
    public const int AmmoSlotElementLength = 4;

    /// <summary>
    /// The 23 bytes <c>FUN_141483fa0</c> reads after the fire groups:
    /// <c>i8; u8; u32; i8; i8; i8; u32; u8; u32; i8; u32</c>.
    /// </summary>
    public const int ScalarBlockLength = 23;

    /// <summary>
    /// The length docs/58 §5 works: one fire group with two fire modes, all three lists empty.
    /// <c>1 + 4 + 1 + (4 + 1 + 26) + 23 + 4 + 4</c>. This is the <see cref="AmmoSlot"/>-OFF length.
    /// </summary>
    public const int SingleGroupLength = 68;

    /// <summary>
    /// The shipped length from wave 13: <see cref="SingleGroupLength"/> plus the one ammo-slot
    /// element. The <c>ItemAdd</c> blob for a one-group weapon is <c>62 + 72 = 134</c>.
    /// </summary>
    public const int SingleGroupLengthWithMagazine = SingleGroupLength + AmmoSlotElementLength;

    /// <summary>
    /// <c>fireGroupCount</c> is read as a <b>signed</b> byte, so a larger count arrives negative and
    /// the client would skip every group while the envelope still claimed their bytes.
    /// </summary>
    public const int MaximumGroups = sbyte.MaxValue;

    /// <summary>Total tail length.</summary>
    public int Length
    {
        get
        {
            int total = FixedLength + (AmmoSlot ? AmmoSlotElementLength : 0);
            foreach (FireGroupDefinition group in Groups)
            {
                total += group.PayloadLength;    // u32 id; i8 modeCount; 13 * modeCount
            }

            return total;
        }
    }

    /// <summary>
    /// <b>The predicate that decides whether an item may ever be cleared for body slot 7.</b> True
    /// only when this tail leaves the weapon component in a state <c>FUN_14228d840</c> survives:
    /// at least one fire group, <see cref="CurrentFireGroupIndex"/> addressing a real one, and that
    /// group carrying enough fire modes for the trigger gate <c>FUN_142291b90</c> (which the
    /// single-shot / melee branch calls with the mode index hard-coded to 1).
    /// <para>
    /// It does <b>not</b> - and cannot - check that the group's id resolves inside
    /// <c>ReferenceData "WeaponDefinitions"</c>; that half is <c>WeaponFireGroupLedger</c>'s job,
    /// because it depends on what this session actually sent.
    /// </para>
    /// </summary>
    public bool IsSafeForActiveHand =>
        Groups.Count > 0
        && CurrentFireGroupIndex >= 0
        && CurrentFireGroupIndex < Groups.Count
        && Groups[CurrentFireGroupIndex].SupportsLocalAttack;

    /// <summary>The fire group <see cref="CurrentFireGroupIndex"/> selects, or null if it does not select one.</summary>
    public FireGroupDefinition? CurrentGroup =>
        CurrentFireGroupIndex >= 0 && CurrentFireGroupIndex < Groups.Count
            ? Groups[CurrentFireGroupIndex]
            : null;

    /// <summary>
    /// The four values <see cref="IdleState"/> puts into the 23-byte scalar block, and where each
    /// one comes from. Adopted from Z1 under D53 (its 1087 tail is click-proven and byte-identical
    /// to the friend's captured 149-byte weapon <c>ItemAdd</c>); the block is the same 23 bytes wide
    /// on both builds, so only the values cross - S6 §2.4.
    /// </summary>
    public static class IdleStateScalars
    {
        /// <summary>
        /// <c>comp+0x40</c>, the equipment slot the item is drawn into. <b>Cranberry writes 0.</b>
        /// Z1 writes 7 for the drawn weapon (76/77/80 slung, 0 in a bag), but nothing on this build
        /// knows the draw slot when the tail is built: the only constructor is
        /// <c>AugustWeaponTable.CreateTail(uint itemDefinitionId)</c>, reached from
        /// <c>WeaponSession.CreateItemAdd</c>, and an <c>ItemAdd</c> is written before the
        /// <c>94 02</c> that decides the slot - the same packet whose applier
        /// (<c>FUN_142293a50</c>, S5c §5.2) sets this field itself moments later. Sending 7 for a
        /// weapon that then goes to the back would be a lie the client has no reason to correct, so
        /// the field stays 0 until a caller can pass the real slot.
        /// </summary>
        public const byte EquipmentSlot = 0;

        /// <summary>
        /// <c>comp+0x44</c>, the weapon state machine. <b>1 = idle.</b> The client's own
        /// <c>FUN_142291930</c> counts <c>{1, 3, 9, 0xb, 0xc, 0xe}</c> as "not busy" and <b>0 is not
        /// in that set</b>, so a tail full of zeros hands the client a weapon that is permanently
        /// busy. 1 is also exactly what the client's own <c>82 1c Reset</c> handler
        /// (<c>FUN_142293450</c>, S5c §5.3) writes here.
        /// </summary>
        public const byte WeaponState = 1;

        /// <summary>
        /// <c>comp+0x6c</c> and <c>comp+0x70</c>: <b>-1</b>, the value the client's own
        /// <c>82 1c Reset</c> restores to both (S5c §5.3). Z1 writes -1 to both as well.
        /// </summary>
        public const sbyte ResetSentinel = -1;

        /// <summary>
        /// <c>comp+0xe8</c>: <c>0xffffffff</c>, Z1's value. The field's meaning is unrecovered on
        /// both builds (S6 §10 Q5), so this is adopted, not derived.
        /// </summary>
        public const uint UnknownE8 = 0xffff_ffffu;
    }

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(Groups);
        if (Groups.Count > MaximumGroups)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Groups),
                Groups.Count,
                $"A weapon tail carries at most {MaximumGroups} fire groups; the client reads the count as a signed byte.");
        }

        writer.WriteByte(BaseFlag);                 // FUN_140bcdc70  -> item+0x59

        // FUN_141484a20 -> comp+0x10: THE AMMO-SLOT ARRAY. One element, carrying the rounds now in
        // the weapon - the only field in the whole 11 02 that can ever say how full the magazine is
        // (docs/107 §1; the capacity is fire mode 0's charge and is a different number). Off, the
        // wave-5..12 empty array, which is what left the hotbar reading 0 for three whole sessions.
        if (AmmoSlot)
        {
            writer.WriteInt32(1);
            writer.WriteUInt32((uint)Math.Max(Magazine, 0));
        }
        else
        {
            writer.WriteInt32(0);
        }

        writer.WriteByte((byte)(sbyte)Groups.Count);// FUN_141483fa0  -> comp+0x38  (i8; grows/shrinks)
        foreach (FireGroupDefinition group in Groups)
        {
            group.WritePayloadTo(writer);           // FUN_141484300 + FUN_141483ec0
        }

        // The 23-byte scalar block. With IdleState off every byte is 0, which is what wave 5
        // through wave 11 sent; with it on, the four fields IdleStateScalars names carry Z1's
        // values. Nothing else in the block moves and the length never does.
        writer.WriteByte(IdleState ? IdleStateScalars.EquipmentSlot : (byte)0);
                                                    // i8  -> comp+0x40   equipment slot [see IdleStateScalars]
        writer.WriteByte(IdleState ? IdleStateScalars.WeaponState : (byte)0);
                                                    // u8  -> comp+0x44   weapon state machine; 1 = idle
        writer.WriteUInt32(0);                      // u32 -> comp+0x48   (docs/58 U6)
        writer.WriteByte((byte)CurrentFireGroupIndex); // i8 -> comp+0x64 == item+0x10c  <-- indexed by FUN_14228d840
        writer.WriteByte(IdleState ? unchecked((byte)IdleStateScalars.ResetSentinel) : (byte)0);
                                                    // i8  -> comp+0x6c   what 82 1c Reset restores
        writer.WriteByte(IdleState ? unchecked((byte)IdleStateScalars.ResetSentinel) : (byte)0);
                                                    // i8  -> comp+0x70   what 82 1c Reset restores
        writer.WriteUInt32(0);                      // u32 -> comp+0x74   (docs/58 U6)
        writer.WriteByte(0);                        // u8  -> comp+0xa8   (docs/58 U6)
        writer.WriteUInt32(0);                      // u32 -> comp+0xac   (docs/58 U6)
        writer.WriteByte(0);                        // i8  -> comp+0x7c, stored as a float (docs/58 U6)
        writer.WriteUInt32(IdleState ? IdleStateScalars.UnknownE8 : 0u);
                                                    // u32 -> comp+0xe8   Z1 writes -1 (S6 §10 Q5: [U])

        writer.WriteInt32(0);                       // FUN_141484b70  -> item+0x198, n1 = 0 (docs/58 U5)
        writer.WriteInt32(0);                       // FUN_141484740  -> item+0x1c8, n2 = 0 (docs/58 U5)
    }
}

/// <summary>
/// <c>ClientUpdate.ItemAdd</c> (<c>11 00 02</c>) for a <c>CODE_FACTORY_NAME = Weapon</c> row: the
/// same envelope and the same 62-byte base record as <see cref="ItemAdd"/>, with the Weapon-class
/// tail of <see cref="WeaponItemAddTail"/> in place of the 1-byte Generic tail.
/// <para>
/// <b>A separate record rather than a change to <see cref="ItemAdd"/></b>, for the same reason
/// <c>SetCharacterEquipmentWithSlots</c> is separate from <c>SetCharacterEquipment</c>: every
/// already-live grant path uses <see cref="ItemAdd"/>, this tail has never been on a wire, and the
/// docs/32 regressions were all caused by widening a live-proven writer. With
/// <see cref="WeaponStageOptions.WriteWeaponItemAddTail"/> off, nothing constructs this type and the
/// wire is byte-identical to wave 5.
/// </para>
/// </summary>
public sealed record WeaponItemAdd(ulong TargetCharacterGuid, InventoryItem Item, WeaponItemAddTail Tail)
{
    /// <summary><c>ClientUpdate</c>, 0x11.</summary>
    public const byte Opcode = ZoneOpcodes.ClientUpdateBase;

    /// <summary><c>ItemAdd</c>, u16 sub 0x0002 - identical to <see cref="ItemAdd.SubOpcode"/>.</summary>
    public const ushort SubOpcode = ItemAdd.SubOpcode;

    /// <summary>Envelope (15 bytes) + the length-prefixed blob (62-byte base + tail).</summary>
    public int Length => 15 + InventoryItem.BaseLength + Tail.Length;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(Item);
        ArgumentNullException.ThrowIfNull(Tail);

        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteUInt64(TargetCharacterGuid);

        int lengthSlot = writer.ReserveUInt32();
        int start = writer.Position;
        Item.WriteTo(writer);       // FUN_140a3aa60, unchanged - 62 bytes
        Tail.WriteTo(writer);       // FUN_14148c3c0, the Weapon class's vtable+0x50
        writer.PatchUInt32(lengthSlot, (uint)(writer.Position - start));
    }
}
