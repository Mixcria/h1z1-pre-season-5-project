using Cranberry.Protocol;

namespace Cranberry.Zone.Weapons;

// ReferenceData "WeaponDefinitions" (0x17) - docs/58 §4, the table the August client needs before a
// weapon can be wielded without the docs/45 null dereference.
//
// WHY THIS TABLE EXISTS AT ALL. The client builds a weapon's fire groups ITSELF, at ItemAdd time,
// from a weapon-definition table the server ships under this type name. The crash site
// (FUN_1411cef14, reading +0x38 with rax = 0) is two dereferences past a SUCCESSFUL slot-7 guid
// lookup: what it reads is FUN_14228d840(item + 0xa8), which returns
//     comp->vtable[1](comp, fireGroupArray[comp + 0x64].id)
// i.e. a lookup of the CURRENT fire group's id in list 1 of THIS table. So a body-slot-7 row is
// survivable only when both halves hold: the weapon component owns a non-empty fire-group array
// (the ItemAdd tail, WeaponItemAddTail.cs), AND the id in it resolves inside this table.
//
// Every field below is commented with the decompiled function that reads it. Addresses are August
// build 0.0.118.208059. LABEL: DERIVED from the binary; no byte of this table has ever been on a
// wire (docs/58: "Nothing here has been on a wire").

/// <summary>
/// One <c>FireGroups</c> record - list 1 of the <see cref="WeaponDefinitionsBlob"/>, the list the
/// crash site's lookup (<c>FUN_14147ef50</c>, weapon-component vtable slot 1) reads.
/// <para>
/// <b>Layout [DERIVED, docs/58 §4c]</b> - the id is read by the list reader
/// <c>FUN_140a4ef30</c> (which stores it at <c>rec+0x78</c>, the hash key, and links the record with
/// <c>FUN_140b18d70</c>), the rest by the body reader <c>FUN_140a398c0</c>:
/// <code>
/// u32   fireGroupId          -&gt; rec+0x78   FUN_140a4ef30 (hash key)
/// u32   unknown18            -&gt; rec+0x18   FUN_140a398c0
/// i32 m; u32 fireModeIds[m]  -&gt; rec+0x20   FUN_140a4caf0 (base +0x28, count +0x30)
/// u8    flags                -&gt; rec+0x38   *** THE BYTE THE CRASH SITE READS ***
/// u32   x10                  -&gt; rec+0x3c .. +0x60
/// </code>
/// <b>53 + 4m bytes.</b> Re-verified this wave against
/// <c>out\firegroup\gh-weapondefs\…\FUN_140a398c0_140a398c0.c</c> and
/// <c>FUN_140a4ef30_140a4ef30.c</c>: one <c>u32</c>, the array sub-reader, one <c>u8</c>, then
/// exactly ten <c>u32</c>s.
/// </para>
/// </summary>
/// <param name="FireGroupId">
/// <c>ClientItemDatasheetData.FIRE_GROUP_ID</c> - EXTRACTED, see <see cref="AugustWeaponFacts"/>.
/// </param>
/// <param name="Flags">
/// <c>rec+0x38</c>. <b>Bit 6 (0x40) selects the automatic branch</b> of the local attack path
/// <c>FUN_1411ceca0</c>; clear selects single-shot / melee. Bit 4 (0x10) is tested by
/// <c>FUN_14148cca0</c> at the end of the <c>ItemAdd</c> tail to set an item flag at
/// <c>item+0x230</c>. Every other bit is docs/58 U4 - unrecovered, so Cranberry writes zero.
/// </param>
/// <param name="FireModeIds">
/// The <c>rec+0x28</c>/<c>+0x30</c> array. Cranberry sends it <b>empty</b>: nothing on the wielding
/// path reads it (the modes that matter come from the <c>ItemAdd</c> tail, which blank-constructs
/// them through <c>FUN_1422a3f40</c>), and list 2 <c>FireModes</c> is not populated because its
/// record body <c>FUN_140a422d0</c> is undecoded (docs/58 U2).
/// </param>
/// <param name="SpinUpMovementModifier">
/// <c>rec+0x54</c> (trailing word index 6) = <c>FireGroup.SpinUpMovementModifier</c>, a MULTIPLIER
/// the client applies to movement speed only while the weapon component is in state 2 (spin-up):
/// <c>FUN_14228ec30</c> defaults to <c>groupDef+0x54</c>. The client's own "no modifier" value is
/// <c>1.0f</c>; <c>0</c> means "cannot move". (2026-09-02 overhaul, S5b / DIAGNOSIS-wield-freeze §6.1.)
/// </param>
/// <param name="SpinUpTurnRateModifier">
/// <c>rec+0x58</c> (trailing word index 7) = <c>FireGroup.SpinUpTurnRateModifier</c>, the turn-rate
/// twin of <paramref name="SpinUpMovementModifier"/>; same state-2 gate, same <c>1.0f</c> default.
/// </param>
public sealed record FireGroupRecord(
    uint FireGroupId,
    byte Flags = 0,
    IReadOnlyList<uint>? FireModeIds = null,
    float SpinUpMovementModifier = 1.0f,
    float SpinUpTurnRateModifier = 1.0f)
{
    /// <summary>Trailing word index of <c>rec+0x54</c> <c>FireGroup.SpinUpMovementModifier</c>.</summary>
    public const int SpinUpMovementModifierIndex = 6;

    /// <summary>Trailing word index of <c>rec+0x58</c> <c>FireGroup.SpinUpTurnRateModifier</c>.</summary>
    public const int SpinUpTurnRateModifierIndex = 7;

    /// <summary><c>rec+0x38</c> bit 6 - the automatic-fire branch of <c>FUN_1411ceca0</c>.</summary>
    public const byte AutomaticFlag = 0x40;

    /// <summary>Bytes with an empty fire-mode id array: <c>53 + 4m</c> at <c>m = 0</c>.</summary>
    public const int MinimalLength = 53;

    /// <summary>The ten trailing <c>u32</c>s of <c>FUN_140a398c0</c> (docs/58 U3: meanings unknown).</summary>
    public const int TrailingWordCount = 10;

    /// <summary><c>53 + 4m</c>.</summary>
    public int Length => MinimalLength + (4 * (FireModeIds?.Count ?? 0));

    /// <summary>True when this record selects <c>FUN_1411ceca0</c>'s automatic branch.</summary>
    public bool IsAutomatic => (Flags & AutomaticFlag) != 0;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        IReadOnlyList<uint> modes = FireModeIds ?? [];

        writer.WriteUInt32(FireGroupId);        // FUN_140a4ef30  -> rec+0x78  (hash key)
        writer.WriteUInt32(0);                  // FUN_140a398c0  -> rec+0x18  (docs/58 U3)
        writer.WriteInt32(modes.Count);         // FUN_140a4caf0  -> rec+0x30  (count)
        foreach (uint modeId in modes)
        {
            writer.WriteUInt32(modeId);         // FUN_140a4caf0  -> rec+0x28  (elements)
        }

        writer.WriteByte(Flags);                // FUN_140a398c0  -> rec+0x38  <-- the crash byte
        for (int i = 0; i < TrailingWordCount; i++)
        {
            switch (i)
            {
                case SpinUpMovementModifierIndex:
                    writer.WriteSingle(SpinUpMovementModifier);   // -> rec+0x54  MULTIPLIER (FUN_14228ec30, state 2)
                    break;
                case SpinUpTurnRateModifierIndex:
                    writer.WriteSingle(SpinUpTurnRateModifier);   // -> rec+0x58  MULTIPLIER (state 2)
                    break;
                default:
                    writer.WriteUInt32(0);      // FUN_140a398c0  -> rec+0x3c .. +0x60 (docs/58 U3, still unnamed)
                    break;
            }
        }
    }
}

/// <summary>
/// One element of a <c>WeaponDefinitions</c> list-0 record's <b>ammo-slot array</b> - the
/// <c>def+0xd0</c> array (base <c>+0xd8</c>, count <c>+0xe0</c>, stride <c>0x68</c>) that
/// <c>FUN_140a515d0</c> reads and <c>FUN_140a2c690</c> fills, and that
/// <c>FUN_1421e5e90(def, index)</c> bounds-checks and returns.
///
/// <para>
/// <b>Wave 14 [P]: this array is what a shot walks through.</b> A fire mode names a slot index
/// (<c>rec+0x30</c> <c>AMMO_SLOT</c>); the slot names an ammunition item; and the
/// <c>(fireModeDefinitionId, ammoItemId)</c> pair is the key of list 4, the projectile mapping
/// (<see cref="FireModeProjectileRecord"/>). Cranberry sent this array EMPTY from wave 9 to
/// wave 13, so <c>FUN_1421e5e90</c> returned 0 for every index and <c>FUN_14228fcf0</c> could
/// never reach a projectile id however good the mapping was.
/// </para>
/// <para>
/// It is also what the client's own <c>CreateItem</c> self-init sizes the item's magazine vector
/// from (<c>FUN_142291180</c> uses <c>def+0xe0</c>), so the count here and the <c>ItemAdd</c>
/// tail's leading counted array (docs/107 section 1) describe the same thing and must agree.
/// </para>
/// <code>
/// u32 ammoId    -&gt; slot+0x00   AmmoSlot.AmmoId    [P]  FUN_14228de50 / FUN_14091eb20
/// u32 clipSize  -&gt; slot+0x04   AmmoSlot.ClipSize  [P]  FUN_14228df60 / FUN_14091f060
/// u32           -&gt; slot+0x08   [U]
/// u8            -&gt; slot+0x0c   [U] (stored as value != 0)
/// u32 x3        -&gt; slot+0x10 +0x14 +0x18   [U]
/// str x3        -&gt; slot+0x20 +0x38 +0x50   SoeUtil::StringFixed&lt;32&gt;
/// </code>
/// <para>
/// With three empty strings that is <b>37 wire bytes</b> (<see cref="MinimalLength"/>).
/// </para>
/// </summary>
/// <param name="AmmoId">
/// <c>slot+0x00</c> = <c>AmmoSlot.AmmoId</c> [P]. <c>FUN_14228de50</c> reads exactly this word and
/// passes it to the stat-override helper under <c>DAT_145592b10</c>, whose initialiser string is
/// <c>"AmmoSlot.AmmoId"</c> (<c>FUN_14091eb20:10</c>). Cranberry writes the
/// <c>ClientItemDefinitions</c> row of the round the gun eats
/// (<c>Cranberry.Zone.Combat.AmmoTypes</c>).
/// </param>
/// <param name="ClipSize">
/// <c>slot+0x04</c> = <c>AmmoSlot.ClipSize</c> [P] - the magazine's capacity.
/// <c>FUN_14228df60:15</c> reads it under <c>DAT_145592c18</c> (<c>"AmmoSlot.ClipSize"</c>,
/// <c>FUN_14091f060:10</c>) and, for the slot the current fire mode uses, adds the component's own
/// bonus from vtable slot <c>+0x88</c>. Cranberry writes the client's own <c>CLIP_SIZE</c>.
/// </param>
public sealed record WeaponAmmoSlotRow(uint AmmoId, uint ClipSize)
{
    /// <summary>Bytes with three empty strings: <c>3*4 + 1 + 3*4 + 3*4</c> = <b>37</b>.</summary>
    public const int MinimalLength = 37;

    /// <summary>Always <see cref="MinimalLength"/> - Cranberry writes no strings.</summary>
    public int Length => MinimalLength;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt32(AmmoId);          // -> slot+0x00  AmmoSlot.AmmoId    [P]
        writer.WriteUInt32(ClipSize);        // -> slot+0x04  AmmoSlot.ClipSize  [P]
        writer.WriteUInt32(0);               // -> slot+0x08  [U]
        writer.WriteByte(0);                 // -> slot+0x0c  [U] (FUN_140a2c690 stores != 0)
        writer.WriteUInt32(0);               // -> slot+0x10  [U]
        writer.WriteUInt32(0);               // -> slot+0x14  [U]
        writer.WriteUInt32(0);               // -> slot+0x18  [U]
        writer.WriteString(string.Empty);    // -> slot+0x20  StringFixed<32>
        writer.WriteString(string.Empty);    // -> slot+0x38
        writer.WriteString(string.Empty);    // -> slot+0x50
    }
}

/// <summary>
/// One <c>WeaponDefinitions</c> record - list 0, keyed by the item row's <c>PARAM1</c>
/// (<c>FUN_14147f4c0</c>, weapon-component vtable slot 8). This is the record
/// <c>ClientPlayerItemManager::CreateItem</c> fails to find today, which is what makes the client
/// write <c>weapon definition not found for weapon ID %d</c> into its own
/// <c>C:\Aug2017\Client\Logs\WeaponErrors.log</c>.
/// <para>
/// <b>docs/58 U1 was OPEN when docs/58 was written and is CLOSED here.</b> docs/58 §4e said "the
/// four sub-readers are not decoded … until they are, list 0 must be sent empty". This wave ran
/// <c>dump.ps1 -Targets '@140a52a50+140a515d0+140a51490'</c>
/// (<c>out\firegroup\gh-weapondef-subs\_140a52a50_140a515d0_140a51490\</c>) and all four are now
/// decoded:
/// </para>
/// <list type="bullet">
/// <item><c>FUN_140a51490</c> (<c>+0x84</c>) - <b>two u32s</b>, a fixed-size pair, no count.</item>
/// <item><c>FUN_140b78f60</c> (<c>+0x90</c>) - <c>SoeUtil::StringFixed&lt;32&gt;</c>, i.e. the same
/// <c>u32 length; bytes</c> string <c>EquipmentSlotRow</c> already writes.</item>
/// <item><c>FUN_140a515d0</c> (<c>+0xd0</c>) - <c>i32 n</c> then <c>n</c> bodies of
/// <c>FUN_140a2c690</c> (<c>u32; u32; u32; u8; u32; u32; u32; str; str; str</c>), stride 0x68.</item>
/// <item><c>FUN_140a52a50</c> (<c>+0xe8</c>) - <c>i32 m; u32 fireGroupIds[m]</c>. <b>This is the
/// fire-group id list</b> <c>FUN_142291180</c> walks through <c>FUN_1421e5f20(def, i)</c>.</item>
/// </list>
/// <para>
/// <b>From wave 9 it is ON by default</b> (<see cref="WeaponStageOptions.PopulateWeaponDefinitions"/>),
/// because an empty list 0 keeping "exactly today's behaviour" turned out to BE the failure: today's
/// behaviour is <c>CreateItem</c> early-returning and writing
/// <c>weapon definition not found for weapon ID 1374</c> into the client's own
/// <c>WeaponErrors.log</c>, which is why the owner has never held a gun. The risk is unchanged and
/// is why <c>CRANBERRY_WEAPON_DEFS_LIST0=0</c> exists: the decode has never been on a wire, and a
/// record whose <em>length</em> is wrong by one byte misaligns every following record, which would
/// hand <c>FUN_142291180</c> a garbage <c>def+0xf8</c> count and a garbage allocation at gun-pickup
/// time.
/// </para>
/// </summary>
/// <param name="WeaponDefinitionId">
/// The item row's <c>PARAM1</c> = <c>ClientItemDatasheetData.WEAPON_ID</c> (docs/58 §2 step 10).
/// </param>
/// <param name="FireGroupIds">
/// <c>FUN_140a52a50</c> -&gt; <c>def+0xe8</c> (base <c>+0xf0</c>, count <c>+0xf8</c>): the ids
/// <c>FUN_142291180</c> materialises the weapon's fire-group array from.
/// </param>
/// <param name="TurnModifier">
/// <c>def+0x58</c> = <c>Weapon.TurnModifier</c>, a MULTIPLIER on the local player's turn rate while
/// this weapon is in the active hand (<c>FUN_142290810</c> → <c>FUN_140c4c440</c>). The client's own
/// record constructor <c>FUN_1421e5b70</c> presets <c>0x3f800000</c> (<c>1.0f</c>); <c>0</c> means
/// "cannot turn" (yaw pinned in all 12 freeze windows, 2026-09-02 overhaul refute-2 §3.2).
/// </param>
/// <param name="MovementModifier">
/// <c>def+0x5c</c> = <c>Weapon.MovementModifier</c>, a MULTIPLIER on the local player's movement
/// speed while this weapon is in the active hand: <c>FUN_14228fa20</c> → <c>FUN_1411acfa0</c>
/// (returns "no speed" when the product is <c>&lt;= 0</c>) → <c>FUN_141593e80</c>. The client's
/// own default is <c>1.0f</c> (<c>FUN_1421e5b70</c>, <c>DAT_1430ef088</c>); Cranberry shipped
/// <c>0</c> here from wave 9 to 2026-09-02, which is the 08-31 wield freeze docs/49 §5.3 predicted
/// (DIAGNOSIS-wield-freeze.md, overhaul-20260901).
/// </param>
/// <param name="AmmoSlots">
/// The <c>def+0xd0</c> ammo-slot array (<see cref="WeaponAmmoSlotRow"/>). <b>Wave 14: no longer
/// always empty.</b> A gun gets one slot naming its round and its clip size; a melee row, a
/// throwable and the fists keep the empty array they have always had, which leaves their record
/// length unchanged at <see cref="MinimalLength"/> + its fire-group ids.
/// </param>
public sealed record WeaponDefinitionRecord(
    uint WeaponDefinitionId,
    IReadOnlyList<uint> FireGroupIds,
    float TurnModifier = 1.0f,
    float MovementModifier = 1.0f,
    IReadOnlyList<WeaponAmmoSlotRow>? AmmoSlots = null,
    int ToIronSightsTimeMs = 0,
    int FromIronSightsTimeMs = 0)
{
    /// <summary>
    /// When true (the default) the record's body "ID" at <c>def+0x18</c> carries
    /// <see cref="WeaponDefinitionId"/>, as the client expects: the record's vtable <c>+0x20</c>
    /// getter (<c>0x1421e5f50</c> = <c>mov eax,[rcx+0x18]</c>) is what <c>FUN_14228bba0</c> stores
    /// at <c>comp+0x08</c>, and <c>FUN_14147f4c0</c> then looks list 0 up by that value. Shipping
    /// <c>0</c> there (waves 9-10) made the local weapon key its definition lookup on 0 (refute-1).
    /// False exists only as the bisect lever for the owner's click.
    /// </summary>
    public bool WriteBodyId { get; init; } = true;

    /// <summary>
    /// Weapon audio object name hash at def+0xc4. FUN_1421e63f0 hashes AUDIO_GAME_OBJECT;
    /// FUN_140a46ef0 reads it before the August-only sprint-resume word at def+0xc8.
    /// See docs/weapon-audio-20260904.md for the capture-to-August asset verification.
    /// </summary>
    public uint AudioGameObject { get; init; }
    public uint WeaponGroupId { get; init; }
    public uint EquipTimeMs { get; init; }
    public uint UnequipTimeMs { get; init; }
    public uint AimInAnimationTimeMs { get; init; }
    public uint AimOutAnimationTimeMs { get; init; }
    public uint SprintRecoveryTimeMs { get; init; }
    public string AnimationSetName { get; init; } = string.Empty;

    /// <summary>
    /// Bytes with an empty <c>+0x90</c> string, an empty <c>+0xd0</c> array and no fire-group ids:
    /// <c>4</c> (id) + <c>81</c> (twenty <c>u32</c> + one <c>u8</c>) + <c>8</c>
    /// (<c>FUN_140a51490</c>) + <c>4</c> (empty string) + <c>28</c> (seven <c>u32</c>) + <c>4</c>
    /// (empty <c>+0xd0</c> array) + <c>4</c> (the fire-group count) = <b>133</b>, then <c>4</c> per
    /// fire-group id.
    /// </summary>
    public const int MinimalLength = 133;

    /// <summary><c>133 + 4m + 37n</c>.</summary>
    public int Length =>
        MinimalLength
        + System.Text.Encoding.UTF8.GetByteCount(AnimationSetName)
        + (4 * FireGroupIds.Count)
        + (WeaponAmmoSlotRow.MinimalLength * (AmmoSlots?.Count ?? 0));

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(FireGroupIds);

        // FUN_140a51070 - the list reader; the id is the hash key at rec+0x110.
        writer.WriteUInt32(WeaponDefinitionId);

        // FUN_140a46ef0 - the body, in its exact decompiled read order. The ORDER fixes the record's
        // length. Until 2026-09-02 every value here was 0 on the theory that "0 is the only value
        // that claims nothing" - WRONG for three fields: +0x18 is the record's own ID (the local
        // weapon's lookup key), and +0x58 / +0x5c are MULTIPLIERS whose client default is 1.0f, so
        // 0 there means "cannot turn / cannot move" - the 2026-08-31 wield freeze that docs/49 §5.3
        // predicted (out\overhaul-20260901\DIAGNOSIS-wield-freeze.md §6). Every other field keeps 0,
        // which IS the client constructor's own default for them (FUN_1421e5b70 L7-15).
        writer.WriteUInt32(WriteBodyId ? WeaponDefinitionId : 0);   // -> def+0x18  the body "ID" (getter 0x1421e5f50)
        writer.WriteUInt32(WeaponGroupId); // -> def+0x20
        writer.WriteByte(0);        // -> def+0x24   (the single u8 of the record)
        writer.WriteUInt32(EquipTimeMs); // -> def+0x28
        writer.WriteUInt32(UnequipTimeMs); // -> def+0x2c
        writer.WriteUInt32(0);      // -> def+0x30
        writer.WriteUInt32(0);      // -> def+0x34
        writer.WriteUInt32(0);      // -> def+0x50   (out of offset order in the binary; wire order wins)
        // def+0x38 Weapon.ToIronSightsTime and def+0x3c Weapon.FromIronSightsTime, the two
        // durations FUN_1422935d0:70-76 takes on a non-silent fire-mode switch and parks in
        // comp+0x60 - the aim-in / aim-out RAMP. 0 is the client constructor's own default
        // and means "instant"; AugustFireModeFacts.IronSightsTimeMs is the owner's own Z1
        // value, adopted under D53. Both are u32s already on the wire, so the record's
        // LENGTH does not move either way.
        writer.WriteUInt32((uint)Math.Max(0, ToIronSightsTimeMs));    // -> def+0x38
        writer.WriteUInt32((uint)Math.Max(0, FromIronSightsTimeMs));  // -> def+0x3c
        writer.WriteUInt32(AimInAnimationTimeMs); // -> def+0x40
        writer.WriteUInt32(AimOutAnimationTimeMs); // -> def+0x44
        writer.WriteUInt32(SprintRecoveryTimeMs); // -> def+0x48
        writer.WriteUInt32(0);      // -> def+0x4c
        writer.WriteSingle(TurnModifier);       // -> def+0x58  Weapon.TurnModifier      MULTIPLIER, 0 = cannot turn
        writer.WriteSingle(MovementModifier);   // -> def+0x5c  Weapon.MovementModifier  MULTIPLIER, 0 = cannot move
        writer.WriteUInt32(0);      // -> def+0x70
        writer.WriteUInt32(0);      // -> def+0x74
        writer.WriteUInt32(0);      // -> def+0x78
        writer.WriteUInt32(0);      // -> def+0x7c
        writer.WriteUInt32(0);      // -> def+0x80

        // thunk_FUN_140a51490(cursor, def + 0x84): a bare do { u32 } while (i < 2) - two u32s.
        writer.WriteUInt32(0);      // -> def+0x84
        writer.WriteUInt32(0);      // -> def+0x88

        // thunk_FUN_140b78f60(cursor, def + 0x90): SoeUtil::StringFixed<32>, u32 length + bytes.
        writer.WriteString(AnimationSetName); // -> def+0x90

        writer.WriteUInt32(0);      // -> def+0xa8
        writer.WriteUInt32(0);      // -> def+0xac
        writer.WriteUInt32(0);      // -> def+0xb8
        writer.WriteUInt32(0);      // -> def+0xbc
        writer.WriteUInt32(0);      // -> def+0xc0
        writer.WriteUInt32(AudioGameObject); // -> def+0xc4 AUDIO_GAME_OBJECT
        writer.WriteUInt32(0);      // -> def+0xc8 (Ghidra prints this offset in decimal, as 200)

        // thunk_FUN_140a515d0(cursor, def + 0xd0): i32 n, then n FUN_140a2c690 bodies (stride 0x68).
        // THE AMMO-SLOT ARRAY. FUN_142291180 sizes the item's own magazine vector from its COUNT
        // (def+0xe0), and FUN_1421e5e90(def, fireMode->+0x30) returns one of its elements - which
        // is where the shot path gets AmmoSlot.AmmoId, the second key of list 4 (wave 14).
        IReadOnlyList<WeaponAmmoSlotRow> ammoSlots = AmmoSlots ?? [];
        writer.WriteInt32(ammoSlots.Count);   // -> def+0xd0 array (base +0xd8, count +0xe0)
        foreach (WeaponAmmoSlotRow slot in ammoSlots)
        {
            slot.WriteTo(writer);
        }

        // thunk_FUN_140a52a50(cursor, def + 0xe8): i32 m; u32 ids[m] - THE FIRE-GROUP ID LIST.
        writer.WriteInt32(FireGroupIds.Count);   // -> def+0xf8 (count)
        foreach (uint fireGroupId in FireGroupIds)
        {
            writer.WriteUInt32(fireGroupId);     // -> def+0xf0 (base), read by FUN_1421e5f20
        }
    }
}

/// <summary>
/// The <c>WeaponDefinitions</c> blob: <b>eight <c>i32</c>-counted lists in a fixed order</b>
/// (docs/58 §4b - <c>FUN_140a20570</c> calls eight readers against one cursor). Because every
/// reader starts with an <c>i32 count</c>, <b>32 zero bytes is a well-formed, completely empty
/// table</b> - which is what makes stage 1 cheap and safe to try.
/// <para>
/// Cranberry populates list 1 (<c>FireGroups</c>) behind
/// <see cref="WeaponStageOptions.PopulateFireGroups"/> (default on) and list 0
/// (<c>WeaponDefinitions</c>) behind <see cref="WeaponStageOptions.PopulateWeaponDefinitions"/>
/// (default off), so the 32-zero-byte empty table above is a configuration that can actually be
/// shipped - <c>CRANBERRY_WEAPON_DEFS_LIST1=0</c> - and not just a hypothetical.
/// </para>
/// <para>
/// <b>2026-09-02 (overhaul lane 2D): lists 2-7 are no longer six hard-coded zero counts.</b>
/// Every one of the six body readers is decoded (<see cref="WeaponListLayouts"/>, docs/99), so
/// each list is now a real collection this record writes. List 2 (<c>FireModes</c>) is populated
/// behind <see cref="WeaponStageOptions.PopulateFireModes"/>; lists 3-7 ship empty because their
/// layouts are [P] but their <em>roles</em> are [I] or [U] and no client sheet supplies values for
/// them - an empty list leaves the client on its own record-constructor defaults, which is exactly
/// where it is today.
/// </para>
/// </summary>
public sealed record WeaponDefinitionsBlob(
    IReadOnlyList<WeaponDefinitionRecord>? WeaponDefinitions = null,
    IReadOnlyList<FireGroupRecord>? FireGroups = null,
    IReadOnlyList<FireModeRecord>? FireModes = null,
    IReadOnlyList<ConeOfFireRecord>? ConeOfFire = null,
    IReadOnlyList<FireModeProjectileRecord>? FireModeProjectiles = null,
    IReadOnlyList<AimAssistRecord>? AimAssist = null,
    IReadOnlyList<WeaponBlobList6Record>? List6 = null,
    IReadOnlyList<WeaponBlobList7Record>? List7 = null)
{
    /// <summary>The type name the handler <c>FUN_140b055c0</c> dispatches on (string 0x143118bf0).</summary>
    public const string TypeName = "WeaponDefinitions";

    /// <summary>Lists in the blob - <c>FUN_140a20570</c> calls exactly eight readers.</summary>
    public const int ListCount = 8;

    /// <summary>An entirely empty table: eight <c>i32 0</c>s.</summary>
    public const int EmptyLength = ListCount * 4;

    /// <summary>List index of <c>WeaponDefinitions</c> (<c>FUN_140a51070</c>, header +0x38).</summary>
    public const int WeaponDefinitionsList = 0;

    /// <summary>List index of <c>FireGroups</c> (<c>FUN_140a4ef30</c>, header +0x68).</summary>
    public const int FireGroupsList = 1;

    /// <summary>List index of <c>FireModes</c> (<c>FUN_140a4f0e0</c>, header +0x98).</summary>
    public const int FireModesList = 2;

    /// <summary>List index of the [I] <c>ConeOfFire</c> list (<c>FUN_140a4f230</c>, header +0xc8).</summary>
    public const int ConeOfFireList = 3;

    /// <summary>
    /// List index of the fire-mode to projectile mapping (<c>FUN_140a4d5d0</c>, header +0xf8) -
    /// <see cref="FireModeProjectileRecord"/>. Role recovered wave 14; it was "[U] list 4".
    /// </summary>
    public const int List4Index = 4;

    /// <summary>List index of the [I] <c>AimAssist</c> list (<c>FUN_140a4e570</c>, header +0x128).</summary>
    public const int AimAssistList = 5;

    /// <summary>List index of the [U] list 6 (<c>FUN_140a4fa40</c>, header +0x158).</summary>
    public const int List6Index = 6;

    /// <summary>List index of the [U] list 7 (<c>FUN_140a4fd40</c>, header +0x188).</summary>
    public const int List7Index = 7;

    /// <summary>Total blob length.</summary>
    public int Length
    {
        get
        {
            int total = EmptyLength;
            foreach (WeaponDefinitionRecord definition in WeaponDefinitions ?? [])
            {
                total += definition.Length;
            }

            foreach (FireGroupRecord group in FireGroups ?? [])
            {
                total += group.Length;
            }

            foreach (FireModeRecord mode in FireModes ?? [])
            {
                total += mode.Length;
            }

            foreach (ConeOfFireRecord cone in ConeOfFire ?? [])
            {
                total += cone.Length;
            }

            foreach (FireModeProjectileRecord record in FireModeProjectiles ?? [])
            {
                total += record.Length;
            }

            foreach (AimAssistRecord assist in AimAssist ?? [])
            {
                total += assist.Length;
            }

            foreach (WeaponBlobList6Record record in List6 ?? [])
            {
                total += record.Length;
            }

            foreach (WeaponBlobList7Record record in List7 ?? [])
            {
                total += record.Length;
            }

            return total;
        }
    }

    /// <summary>The raw blob - what goes inside the <c>ReferenceData</c> envelope's counted bytes.</summary>
    public byte[] ToArray()
    {
        using var writer = new PacketWriter(Length);
        WriteTo(writer);
        return writer.Written.ToArray();
    }

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        IReadOnlyList<WeaponDefinitionRecord> definitions = WeaponDefinitions ?? [];
        IReadOnlyList<FireGroupRecord> groups = FireGroups ?? [];

        // List 0 - WeaponDefinitions (FUN_140a51070 -> manager+0x38, count +0x40, buckets +0x60).
        writer.WriteInt32(definitions.Count);
        foreach (WeaponDefinitionRecord definition in definitions)
        {
            definition.WriteTo(writer);
        }

        // List 1 - FireGroups (FUN_140a4ef30 -> manager+0x68, count +0x70, buckets +0x90). This is
        // the list the crash site's lookup FUN_14147ef50 reads.
        writer.WriteInt32(groups.Count);
        foreach (FireGroupRecord group in groups)
        {
            group.WriteTo(writer);
        }

        // List 2 - FireModes (FUN_140a4f0e0 -> manager+0x98, count +0xa0, buckets +0xc0), the list
        // the weapon component's vtable slot 2 (FUN_14147f040, key rec+0x378) resolves a fire
        // group's mode ids against. 639 bytes per record; docs/99 section 2.
        IReadOnlyList<FireModeRecord> modes = FireModes ?? [];
        writer.WriteInt32(modes.Count);
        foreach (FireModeRecord mode in modes)
        {
            mode.WriteTo(writer);
        }

        // List 3 - FUN_140a4f230 (header +0xc8), [I] ConeOfFire. docs/99 section 3.1.
        IReadOnlyList<ConeOfFireRecord> cones = ConeOfFire ?? [];
        writer.WriteInt32(cones.Count);
        foreach (ConeOfFireRecord cone in cones)
        {
            cone.WriteTo(writer);
        }

        // List 4 - FUN_140a4d5d0 (header +0xf8): the FIRE-MODE TO PROJECTILE MAPPING, role [P]
        // as of wave 14 (docs/107 addendum). (fireModeDefinitionId, ammoItemId) -> projectileId.
        IReadOnlyList<FireModeProjectileRecord> list4 = FireModeProjectiles ?? [];
        writer.WriteInt32(list4.Count);
        foreach (FireModeProjectileRecord record in list4)
        {
            record.WriteTo(writer);
        }

        // List 5 - FUN_140a4e570 (header +0x128), [I] AimAssist. docs/99 section 3.3.
        IReadOnlyList<AimAssistRecord> assists = AimAssist ?? [];
        writer.WriteInt32(assists.Count);
        foreach (AimAssistRecord assist in assists)
        {
            assist.WriteTo(writer);
        }

        // List 6 - FUN_140a4fa40 (header +0x158), role [U]. docs/99 section 3.4.
        IReadOnlyList<WeaponBlobList6Record> list6 = List6 ?? [];
        writer.WriteInt32(list6.Count);
        foreach (WeaponBlobList6Record record in list6)
        {
            record.WriteTo(writer);
        }

        // List 7 - FUN_140a4fd40 (header +0x188), role [U]. docs/99 section 3.5.
        IReadOnlyList<WeaponBlobList7Record> list7 = List7 ?? [];
        writer.WriteInt32(list7.Count);
        foreach (WeaponBlobList7Record record in list7)
        {
            record.WriteTo(writer);
        }
    }
}
