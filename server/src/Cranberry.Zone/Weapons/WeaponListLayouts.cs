namespace Cranberry.Zone.Weapons;

/// <summary>
/// The primitive a <c>WeaponDefinitions</c> body reader takes off the cursor for one field. Every
/// list-2..7 body in the August client is built out of exactly these six, by the sub-readers
/// <c>FUN_140a130f0</c> / <c>FUN_140a13170</c> (4 bytes), <c>FUN_140a25a60</c> (1 signed byte),
/// <c>FUN_140a4c660</c> (1 byte, coerced to 0/1), <c>FUN_140a51490</c> (two 4-byte words) and
/// <c>FUN_140a514f0</c> (three 4-byte words), plus the same reads written out inline.
/// </summary>
public enum WeaponListFieldKind : byte
{
    /// <summary>One raw byte (the inline <c>undefined1</c> reads: the record's bit-packed flags).</summary>
    UInt8 = 0,

    /// <summary>One signed byte widened into an <c>int</c> field (<c>FUN_140a25a60</c>).</summary>
    Int8 = 1,

    /// <summary>Two bytes widened into an <c>int</c> field (the inline <c>short</c> reads).</summary>
    Int16 = 2,

    /// <summary>Four bytes, used as an id / count (<c>FUN_140a130f0</c>).</summary>
    UInt32 = 3,

    /// <summary>Four bytes, used as an IEEE-754 float (<c>FUN_140a13170</c>).</summary>
    Single = 4,

    /// <summary>One byte, stored as <c>value != 0</c> (<c>FUN_140a4c660</c>).</summary>
    Boolean = 5,
}

/// <summary>
/// One field of a <c>WeaponDefinitions</c> record body: <b>where the client parks it</b>
/// (<paramref name="RecordOffset"/>, an offset from the record's own base) and <b>how many bytes it
/// eats off the wire</b>. The ORDER of the array these live in is the wire order, and the wire order
/// is the only thing that fixes a record's length - see <see cref="WeaponListLayouts"/>.
/// </summary>
/// <param name="RecordOffset">Byte offset from the record base (<c>rec+0x…</c>).</param>
/// <param name="Kind">Which primitive reader consumes it.</param>
public readonly record struct WeaponListField(short RecordOffset, WeaponListFieldKind Kind)
{
    /// <summary>Bytes this field takes off the wire.</summary>
    public int Size => Kind switch
    {
        WeaponListFieldKind.UInt8 or WeaponListFieldKind.Int8 or WeaponListFieldKind.Boolean => 1,
        WeaponListFieldKind.Int16 => 2,
        _ => 4,
    };
}

/// <summary>
/// How the client's own <c>FireModes</c> datasheet-row loader
/// (<c>H1Z1.exe 0x142226260..0x14222a800</c>) stores one schema column into the fire-mode object.
/// This is the LOADER's view of the column, not the wire's: the wire widths are
/// <see cref="WeaponListFieldKind"/> and they narrow some of these (<c>REFIRE_TIME_MS</c> is an
/// <see cref="Int"/> column the body reader carries as an <c>i16</c>).
/// </summary>
public enum FireModeColumnKind : byte
{
    /// <summary><c>MOVSS dword ptr [RBX + off],XMM1</c> after a <c>CVTSD2SS</c> - a float.</summary>
    Float = 0,

    /// <summary>
    /// <c>LEA RDX,[RBX + off]</c> then the loader's own string-to-integer helper
    /// <c>0x14007022f</c> - an id, a count or a millisecond duration.
    /// </summary>
    Int = 1,

    /// <summary><c>MOV byte ptr [RBX + off],0x1</c> - a standalone boolean byte.</summary>
    Byte = 2,

    /// <summary>
    /// <c>OR</c> / <c>AND byte ptr [RBX + off],mask</c> - one bit of a packed flag byte
    /// (<c>rec+0x20</c>, <c>rec+0x21</c> or <c>rec+0x22</c>). The mask is
    /// <see cref="FireModeColumn.FlagBit"/>.
    /// </summary>
    FlagBit = 3,
}

/// <summary>
/// One <c>FireModes</c> datasheet column, as the client's own row loader names and places it.
/// See <see cref="WeaponListLayouts.FireModeColumns"/>.
/// </summary>
/// <param name="Name">The column name, exactly as the string the loader's <c>LEA RDX</c> points at.</param>
/// <param name="RecordOffset">
/// <c>rec+</c> offset: the loader's displacement plus <c>0x20</c>, because the loader's object base
/// is the network record's <c>rec+0x20</c>.
/// </param>
/// <param name="Kind">How the loader stores it.</param>
/// <param name="FlagBit">
/// The bit mask when <paramref name="Kind"/> is <see cref="FireModeColumnKind.FlagBit"/>; 0 otherwise.
/// </param>
public readonly record struct FireModeColumn(
    string Name, short RecordOffset, FireModeColumnKind Kind, byte FlagBit = 0);

/// <summary>
/// <b>The wire layouts of <c>WeaponDefinitions</c> lists 2-7, recovered 2026-09-02 (overhaul lane
/// 2D).</b> docs/58 §10 U2 ("<c>FUN_140a422d0</c> — the whole fire-mode record body … not decoded")
/// and U7 ("lists 3-7 … roles") are CLOSED here; the derivations are in docs/99 and docs/02.
/// <para>
/// Dumps: <c>out\firegroup\gh-lists2to7\_140a422d0_140a50040_140a39ca0_140a2c1b0_140a4fbf0_140a4cbf0\</c>
/// (the six body readers, this wave) and
/// <c>out\firegroup\gh-weapondefs\_14220e860_140a4ef30_140a4f0e0_140a51070_14147f4c0_1421e5f20_140a2b4d0\</c>
/// (the eight list readers, 2026-08-30).
/// </para>
/// <para>
/// <b>Nothing in this file has been on a wire.</b> The label is DERIVED. A record whose length is
/// wrong by one byte misaligns every record after it, which is why every list has a length theory
/// test and a hex snapshot beside it, and why each list has its own revert switch.
/// </para>
/// </summary>
public static class WeaponListLayouts
{
    private const WeaponListFieldKind U8 = WeaponListFieldKind.UInt8;
    private const WeaponListFieldKind I8 = WeaponListFieldKind.Int8;
    private const WeaponListFieldKind I16 = WeaponListFieldKind.Int16;
    private const WeaponListFieldKind U32 = WeaponListFieldKind.UInt32;
    private const WeaponListFieldKind F32 = WeaponListFieldKind.Single;
    private const WeaponListFieldKind B = WeaponListFieldKind.Boolean;

    /// <summary>
    /// <b>[P] The <c>FireModes</c> record body — <c>FUN_140a422d0</c>, in its exact read order.</b>
    /// 180 fields, <b>631 bytes</b>, no counted array and no string, so the body is fixed-size.
    /// <para>
    /// The reader takes <c>param_1 = rec+0x20</c>, so every offset here is <c>0x20 +</c> the
    /// decompiled displacement. It ends at <c>rec+0x2e8</c>, which is exactly where
    /// <c>FUN_140a39b80</c>'s tamper checksum stops (it XORs <c>0x59</c> qwords from <c>rec+0x20</c>
    /// and masks the top half of the last one — see docs/99 §2.1). That agreement is the strongest
    /// independent check on the 631 that exists without a wire.
    /// </para>
    /// <para>
    /// <b>Field names are NOT in this table on purpose.</b> The client's own 194 <c>FireModes</c>
    /// column names live in the datasheet-schema string run at file offset <c>0x35e79d8</c>, and the
    /// class layout is provably not in column order (<c>FireMode.MovementModifier</c> is column 26
    /// and sits at <c>rec+0x10c</c>). docs/99 §2.3 carries the per-offset name evidence with its
    /// [P] / [I] / [U] marks; only the offsets marked [P] there are named in code.
    /// </para>
    /// </summary>
    public static IReadOnlyList<WeaponListField> FireModeBody { get; } =
    [
        new(0x020, U8), new(0x021, U8), new(0x022, U8), new(0x024, I8),
        new(0x02c, U32), new(0x030, I8), new(0x034, I8), new(0x038, I16),
        new(0x03c, I16), new(0x040, I16), new(0x044, I16), new(0x048, I16),
        new(0x050, I16), new(0x058, U32), new(0x05c, I8), new(0x060, I16),
        new(0x064, I16), new(0x068, I16), new(0x06c, I16), new(0x070, I16),
        new(0x074, I8), new(0x078, I8), new(0x07c, U32), new(0x080, U32),
        new(0x084, U32), new(0x088, U32), new(0x090, U32), new(0x08c, U32),
        new(0x094, U32), new(0x098, U32), new(0x09c, U32), new(0x0a0, U32),
        new(0x0a4, U32), new(0x0a8, U32), new(0x0ac, U32), new(0x0b0, I16),
        new(0x0b4, U32), new(0x0b8, U32), new(0x0bc, I8), new(0x0c0, U32),
        new(0x0c4, U32), new(0x0c8, U32), new(0x0cc, U32), new(0x0d0, U32),
        new(0x0d4, U32), new(0x100, I16), new(0x104, U32), new(0x108, U32),
        new(0x10c, U32), new(0x110, U32), new(0x0d8, U32), new(0x114, U32),
        new(0x118, U32), new(0x11c, U32), new(0x120, I16), new(0x124, I16),
        new(0x128, U32), new(0x12c, U32), new(0x130, U32), new(0x134, U32),
        new(0x138, I8), new(0x140, I8), new(0x144, U32), new(0x148, U32),
        new(0x14c, I16), new(0x150, U32), new(0x154, U32), new(0x158, U32),
        new(0x15c, U32), new(0x160, U32), new(0x16c, U32), new(0x170, U32),
        new(0x174, U32), new(0x178, U32), new(0x17c, U32), new(0x180, F32),
        new(0x184, F32), new(0x188, U32), new(0x18c, F32), new(0x0dc, U32),
        new(0x0e0, U32), new(0x0e4, U32), new(0x0e8, I8), new(0x0ec, I8),
        new(0x0f0, F32), new(0x0f4, F32), new(0x0f8, F32), new(0x0fc, F32),
        new(0x190, U32), new(0x194, U32), new(0x164, F32), new(0x168, F32),
        new(0x198, F32), new(0x19c, B), new(0x1b0, F32), new(0x1b4, F32),
        new(0x1b8, F32), new(0x1bc, F32), new(0x1c0, F32), new(0x1c4, F32),
        new(0x1c8, F32), new(0x2cd, B), new(0x1dc, F32), new(0x1e0, F32),
        new(0x1e4, F32), new(0x1e8, F32), new(0x1ec, F32), new(0x1f0, F32),
        new(0x1f4, F32), new(0x1f8, F32), new(0x2d0, F32), new(0x21c, F32),
        new(0x220, F32), new(0x224, F32), new(0x228, F32), new(0x22c, F32),
        new(0x230, F32), new(0x288, F32), new(0x28c, F32), new(0x290, F32),
        new(0x294, F32), new(0x298, F32), new(0x29c, F32), new(0x248, F32),
        new(0x24c, F32), new(0x250, F32), new(0x254, F32), new(0x258, F32),
        new(0x25c, F32), new(0x260, F32), new(0x264, F32), new(0x2ac, F32),
        new(0x2b0, F32), new(0x2b4, F32), new(0x2b8, F32), new(0x2bc, F32),
        new(0x2c0, F32), new(0x2c4, F32), new(0x2c8, F32), new(0x1a0, F32),
        new(0x20c, F32), new(0x278, F32), new(0x234, F32), new(0x2a0, F32),
        new(0x2d4, F32), new(0x2d8, F32), new(0x2dc, B), new(0x2e0, U32),
        new(0x2dd, B), new(0x1fc, F32), new(0x200, F32), new(0x204, F32),
        new(0x208, F32), new(0x268, F32), new(0x26c, F32), new(0x270, F32),
        new(0x274, F32), new(0x1cc, F32), new(0x1d0, F32), new(0x238, F32),
        new(0x23c, F32), new(0x2cc, B), new(0x13c, U32), new(0x1a8, F32),
        new(0x214, F32), new(0x280, F32), new(0x1d4, F32), new(0x240, F32),
        new(0x2a4, F32), new(0x1d8, F32), new(0x244, F32), new(0x2a8, F32),
        new(0x2e4, F32), new(0x1ac, F32), new(0x218, F32), new(0x284, F32),
        new(0x1a4, F32), new(0x210, F32), new(0x27c, F32), new(0x2e8, F32),
    ];

    /// <summary>Bytes one <c>FireModes</c> body takes: <b>631</b> (the sum of <see cref="FireModeBody"/>).</summary>
    public const int FireModeBodyLength = 631;

    /// <summary>
    /// <b>[P] Every <c>FireModes</c> offset the client's own record constructor presets to
    /// <c>1.0f</c></b> - <c>FUN_1422267e0</c> (called from <c>FUN_142226700</c> with
    /// <c>rec+0x20</c>), which writes <c>0x3f800000</c> into exactly these 34 words and <c>0</c>
    /// into every other one.
    /// <para>
    /// <b>This is the whole of D143 for list 2, recovered rather than guessed.</b> D143 says every
    /// multiplier in a server-authored definition table ships <c>1.0f</c> because <c>0</c> means
    /// "cannot"; for list 0 and list 1 the multipliers were found one at a time by chasing
    /// consumers. Here the constructor names them all at once: a field the client itself defaults
    /// to <c>1.0f</c> is a scalar, and shipping <c>0</c> in it is the 2026-08-31 wield freeze
    /// again. The ones a consumer independently confirms are
    /// <c>FireMode.MovementModifier</c> (<c>0x10c</c>, <c>FUN_1422a4a60</c>),
    /// <c>FireMode.TurnModifier</c> (<c>0x110</c>, <c>FUN_1422a5760</c>),
    /// <c>FireMode.CofScalar</c> / <c>CofScalarMoving</c> (<c>0x84</c> / <c>0x88</c>,
    /// <c>FUN_14228e540</c>), <c>FireMode.CylofScalar</c> / <c>CylofScalarMoving</c>
    /// (<c>0xf4</c> / <c>0xf8</c>, <c>FUN_14228e7e0</c>), <c>FireMode.SwayCrouchScalar</c>
    /// (<c>0x164</c>, <c>FUN_1422a5380</c>) and <c>FireMode.SwayProneScalar</c> (<c>0x168</c>,
    /// <c>FUN_1422a5660</c>).
    /// </para>
    /// <para>
    /// Every one of the 34 is a four-byte field that <see cref="FireModeBody"/> writes, which is
    /// itself a check on both decodes: a constructor default the wire could not reach would mean
    /// one of the two is wrong.
    /// </para>
    /// </summary>
    public static IReadOnlySet<short> FireModeUnitScalars { get; } = new HashSet<short>
    {
        0x084, 0x088, 0x090, 0x0f4, 0x0f8, 0x10c, 0x110, 0x128, 0x164, 0x168, 0x16c,
        0x1a0, 0x1a4, 0x1a8, 0x1ac, 0x1cc, 0x1d0, 0x1d4, 0x1d8, 0x20c, 0x210, 0x214,
        0x218, 0x238, 0x23c, 0x240, 0x244, 0x278, 0x27c, 0x280, 0x284, 0x2a4, 0x2a8, 0x2e8,
    };

    // ---------------------------------------------------------------- the named list-2 offsets
    //
    // Every constant below is [P] and comes from ONE piece of evidence: the client's own
    // FireModes datasheet-row loader, H1Z1.exe 0x142226260..0x14222a800. For each schema column
    // the loader does
    //
    //     LEA RDX,[s_<COLUMN_NAME>] ; MOV RCX,RDI ; CALL qword ptr [RAX + 0x18]   fetch the cell
    //     ... convert ... ; MOVSS/MOV [RBX + loaderOff]                           store it
    //
    // and the object it stores into is the network record's rec+0x20, so
    // "loaderOff + 0x20" is the rec+ offset FireModeBody writes. That makes the map a DIRECT
    // name-to-offset binding that needs no getter and no reference scan - see docs/99 section
    // 2.3 and docs/107 section 9.3. The three source tables are
    // tools/data/firemodes-columns.tsv (the 134 float/byte columns),
    // tools/data/firemodes-columns-ids.tsv (the 45 integer/id columns the loader stores through
    // the helper at 0x14007022f) and tools/data/firemodes-columns-full.tsv (the same plus the
    // flag bits); FireModeColumns below is pinned against all three by WeaponListColumnTableTests.
    //
    // NAMING. A constant that existed before this wave keeps its name and its value; the
    // datasheet column it corresponds to is named in its doc comment. Everything else is
    // FireMode<PascalCase(COLUMN)>, and a flag bit takes a "Flag" suffix because its value is a
    // bit mask, not an offset.
    //
    // NO VALUE MOVES HERE. Naming a column does not fill it: every field Cranberry does not
    // explicitly write still ships the client's own constructor default (docs/99 section 2.4,
    // D143), and this wave changed no byte of the blob.

    /// <summary>
    /// <b>[P] <c>rec+0x20</c> - the first of the record's three bit-packed flag bytes</b>
    /// (<see cref="FireModeBody"/> fields 1-3, the only three <c>u8</c> reads in the body).
    /// Its eight bits are <see cref="FireModeHideUnavailableFlag"/> down to
    /// <see cref="FireModeLaserGuidedFlag"/>; <see cref="FireModeIronSightsFlag"/> is the one
    /// this project writes.
    /// </summary>
    public const short FireModeFlags0 = 0x020;

    /// <summary>
    /// <b>[P] <c>rec+0x21</c> - the second flag byte</b>: <see cref="FireModeCanSteadySwayFlag"/>
    /// down to <see cref="FireModeCanLockonWhileBusyFlag"/>, all eight bits used. Cranberry
    /// writes 0.
    /// </summary>
    public const short FireModeFlags1 = 0x021;

    /// <summary>
    /// <b>[P] <c>rec+0x22</c> - the third flag byte</b>. The loader does not name bits
    /// <c>0x40</c>/<c>0x20</c>, but August FUN_141485bf0 consumes them as the master and
    /// horizontal recoil gates. Captured firearm modes retain those two bits.
    /// </summary>
    public const short FireModeFlags2 = 0x022;

    // --- the 21 flag bits of rec+0x20..rec+0x22, in loader order

    /// <summary>
    /// <c>HIDE_UNAVAILABLE</c> - <c>rec+0x020</c> bit <c>0x80</c>, loader <c>1422271e7</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeHideUnavailableFlag = 0x80;

    /// <summary>
    /// <c>SPRINT_FIRE</c> - <c>rec+0x020</c> bit <c>0x40</c>, loader <c>142227220</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeSprintFireFlag = 0x40;

    /// <summary>
    /// <c>AUTOMATIC</c> - <c>rec+0x020</c> bit <c>0x20</c>, loader <c>142227259</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeAutomaticFlag = 0x20;

    /// <summary>
    /// <c>GRIEF_IMMUNE</c> - <c>rec+0x020</c> bit <c>0x10</c>, loader <c>142227293</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeGriefImmuneFlag = 0x10;

    /// <summary>
    /// <c>USE_IN_WATER</c> - <c>rec+0x020</c> bit <c>0x08</c>, loader <c>1422272ce</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeUseInWaterFlag = 0x08;

    /// <summary>
    /// <b>[P] <c>rec+0x20</c> bit 2 (<c>0x04</c>) = <c>IRON_SIGHTS</c>.</b> The local fire-mode
    /// setter <c>FUN_1422935d0:68-77</c> branches on exactly this bit when the switch is not
    /// silent: set, it takes the mode's aim-in time from <c>Weapon.ToIronSightsTime</c>
    /// (<c>def+0x38</c>, key <c>DAT_145592b70</c>); clear, the aim-out time from
    /// <c>Weapon.FromIronSightsTime</c> (<c>def+0x3c</c>, key <c>DAT_1455928b0</c>). The result
    /// becomes the weapon component's transition timer at <c>comp+0x60</c>.
    /// <para>
    /// Dump: <c>out\ghidra-aug\w82-ads\callers_1422935d0_142290640_14228f5d0_142294c30\FUN_1422935d0_1422935d0.c</c>.
    /// Two more bits of the same byte are read by <c>FUN_140c3b330</c> (<c>0x10</c> against the
    /// camera mode, <c>0x40</c> against the player state) and one by <c>FUN_1422a5860</c>
    /// (<c>0x20</c>); Cranberry writes none of them.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Datasheet column <c>IRON_SIGHTS</c>; loader <c>142227309</c>, <c>rec+0x020</c> bit <c>0x04</c>. [P]
    /// </remarks>
    public const byte FireModeIronSightsFlag = 0x04;

    /// <summary>
    /// <c>HIDE_WEAPON</c> - <c>rec+0x020</c> bit <c>0x02</c>, loader <c>142227344</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeHideWeaponFlag = 0x02;

    /// <summary>
    /// <c>LASER_GUIDED</c> - <c>rec+0x020</c> bit <c>0x01</c>, loader <c>14222737f</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeLaserGuidedFlag = 0x01;

    /// <summary>
    /// <c>CAN_STEADY_SWAY</c> - <c>rec+0x021</c> bit <c>0x80</c>, loader <c>1422273ba</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeCanSteadySwayFlag = 0x80;

    /// <summary>
    /// <c>HOLD_PLAYER_STATE_WHILE_FIRING</c> - <c>rec+0x021</c> bit <c>0x40</c>, loader <c>1422273f7</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeHoldPlayerStateWhileFiringFlag = 0x40;

    /// <summary>
    /// <c>BLOCK_AUTO_RELOAD</c> - <c>rec+0x021</c> bit <c>0x20</c>, loader <c>142227434</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeBlockAutoReloadFlag = 0x20;

    /// <summary>
    /// <c>SPAWN_PROJECTILE_AT_EYE</c> - <c>rec+0x021</c> bit <c>0x10</c>, loader <c>142227471</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeSpawnProjectileAtEyeFlag = 0x10;

    /// <summary>
    /// <c>FIRE_NEEDS_LOCK</c> - <c>rec+0x021</c> bit <c>0x08</c>, loader <c>1422274ae</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeFireNeedsLockFlag = 0x08;

    /// <summary>
    /// <c>CHECK_ENTER_FIRE_STATE</c> - <c>rec+0x021</c> bit <c>0x04</c>, loader <c>1422274eb</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeCheckEnterFireStateFlag = 0x04;

    /// <summary>
    /// <c>CONTINUOUS_RELOAD</c> - <c>rec+0x021</c> bit <c>0x02</c>, loader <c>142227528</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeContinuousReloadFlag = 0x02;

    /// <summary>
    /// <c>CAN_LOCKON_WHILE_BUSY</c> - <c>rec+0x021</c> bit <c>0x01</c>, loader <c>142227565</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeCanLockonWhileBusyFlag = 0x01;

    /// <summary>
    /// <c>MAINTAIN_LOCK</c> - <c>rec+0x022</c> bit <c>0x80</c>, loader <c>1422275a2</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeMaintainLockFlag = 0x80;

    /// <summary>
    /// <c>UNDEFINED1</c> - <c>rec+0x022</c> bit <c>0x10</c>, loader <c>1422275df</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeUndefined1Flag = 0x10;

    /// <summary>
    /// <c>UNDEFINED2</c> - <c>rec+0x022</c> bit <c>0x08</c>, loader <c>14222761c</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeUndefined2Flag = 0x08;

    /// <summary>
    /// <c>MELEE_COMPOSITE_EFFECT_ID</c> - <c>rec+0x022</c> bit <c>0x04</c>, loader <c>142227659</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeMeleeCompositeEffectIdFlag = 0x04;

    /// <summary>
    /// <c>USE_CONE_INSTEAD_OF_CYLINDER</c> - <c>rec+0x022</c> bit <c>0x02</c>, loader <c>142227696</c>. [P]
    /// Cranberry writes 0.
    /// </summary>
    public const byte FireModeUseConeInsteadOfCylinderFlag = 0x02;

    // --- the 179 value columns, by record offset

    /// <summary>
    /// <c>TYPE</c> - <c>rec+0x024</c>, loader <c>1422276d3</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeType = 0x024;

    /// <summary>
    /// <c>TYPE == 3</c>: a MELEE mode. The fire executor <c>FUN_14228d1d0</c> raises the entity
    /// event <c>hash("MELEE_ATTACK")</c> (<c>FUN_141480d50</c>, <c>DAT_145235750</c> written by
    /// <c>FUN_1406518d0</c> from the literal <c>"MELEE_ATTACK"</c>) instead of <c>OnFireBegin</c>;
    /// the hotbar getters <c>FUN_1414f31c0</c> / <c>FUN_1414a6660</c> skip the ammo readout; the
    /// input gate <c>FUN_14158b700</c> refuses the aim for it; <c>FUN_142291930</c> /
    /// <c>FUN_142291b00</c> test for it. [P]
    /// </summary>
    public const int FireModeTypeMelee = 3;

    /// <summary>
    /// <c>TYPE == 8</c>: a TRIGGER-ITEM-ABILITY mode. <c>FUN_14228d2d0</c> fires vtable slot
    /// <c>+0x1d0</c> once and <c>+0x1c8</c> (<c>"&gt; OnTriggerItemAbility"</c>,
    /// <c>FUN_1414933e0</c>) on every pull, and <c>FUN_1422a5b30</c> groups 8 with 9. The trigger
    /// runs the item's own <c>ACTIVATABLE_ABILITY_ID</c>. [P] for the dispatch; that the
    /// binoculars are such a mode is a RULING (docs/119 addendum).
    /// </summary>
    public const int FireModeTypeTriggerItemAbility = 8;

    /// <summary>
    /// <c>TYPE == 12</c> (<c>0xc</c>): a THROWABLE mode. <c>FUN_14228d1d0</c> skips
    /// <c>OnFireBegin</c> for it and <c>FUN_14228d2d0</c> fires vtable slot <c>+0x298</c>
    /// (<c>"&gt; OnThrowEnd"</c>, <c>FUN_1414933b0</c>) when the throw is not yet flagged. [P]
    /// </summary>
    public const int FireModeTypeThrowable = 12;

    /// <summary>
    /// <c>AMMO_ITEM_ID</c> - <c>rec+0x02c</c>, loader <c>1422276fb</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeAmmoItemId = 0x02c;

    /// <summary>
    /// <b>[P] <c>rec+0x30</c> = <c>AMMO_SLOT</c></b> - the index into the <em>weapon
    /// definition's</em> ammo-slot array (<c>def+0xd0</c>, base <c>+0xd8</c>, count <c>+0xe0</c>,
    /// stride <c>0x68</c>), which <c>FUN_1421e5e90(def, index)</c> bounds-checks and returns.
    /// An <c>i8</c> on the wire, an <c>int</c> in the record.
    /// <para>
    /// Three independent readers: <c>FUN_14228fcf0:31</c> (the projectile resolve, through
    /// <c>FUN_14228de50</c>), <c>FUN_141154450:165</c> (the effect preloader) and
    /// <c>FUN_1414887e0</c> (the <c>82 08 Reload</c> applier, docs/107 section 1.4, which passes it
    /// to <c>FUN_1422938d0(comp, slot, value)</c> as the component's ammo-slot index). The same
    /// index therefore addresses BOTH the definition's slot descriptor and the item's own
    /// magazine vector, which is why the two arrays have to be the same length.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Datasheet column <c>AMMO_SLOT</c>; loader <c>142227723</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </remarks>
    public const short FireModeAmmoSlot = 0x030;

    /// <summary>
    /// <c>BURST_COUNT</c> - <c>rec+0x034</c>, loader <c>142227773</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeBurstCount = 0x034;

    /// <summary>
    /// <c>FIRE_DURATION_MS</c> - <c>rec+0x038</c>, loader <c>142227859</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeFireDurationMs = 0x038;

    /// <summary>
    /// <c>FIRE_COOLDOWN_DURATION_MS</c> - <c>rec+0x03c</c>, loader <c>142227881</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeFireCooldownDurationMs = 0x03c;

    /// <summary><c>FireMode.RefireTime</c> - <c>FUN_14228d2d0</c> (<c>DAT_145592b80</c>, <c>def+0x40</c>). [P]</summary>
    /// <remarks>
    /// Datasheet column <c>REFIRE_TIME_MS</c>; loader <c>1422278d1</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </remarks>
    public const short FireModeRefireTime = 0x040;

    /// <summary>
    /// <c>FIRE_DELAY_MS</c> - <c>rec+0x044</c>, loader <c>142227831</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeFireDelayMs = 0x044;

    /// <summary><c>FireMode.AutoFireTime</c> - <c>FUN_14228d2d0</c> (<c>DAT_1455928d8</c>, <c>def+0x48</c>). [P]</summary>
    /// <remarks>
    /// Datasheet column <c>AUTO_FIRE_TIME_MS</c>; loader <c>1422278a9</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </remarks>
    public const short FireModeAutoFireTime = 0x048;

    /// <summary>
    /// <c>CHARGE_UP_TIME_MS</c> - <c>rec+0x04c</c>, loader <c>1422278f9</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeChargeUpTimeMs = 0x04c;

    /// <summary>
    /// <c>COOK_TIME_MS</c> - <c>rec+0x050</c>, loader <c>142227921</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeCookTimeMs = 0x050;

    /// <summary>
    /// <c>SPIN_UP_TIME_MS</c> - <c>rec+0x054</c>, loader <c>142227949</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeSpinUpTimeMs = 0x054;

    /// <summary><c>FireMode.Range</c> - <c>FUN_142290010</c> and <c>FUN_1422a4dd0</c>. [P]</summary>
    /// <remarks>
    /// Datasheet column <c>RANGE</c>; loader <c>14222779b</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </remarks>
    public const short FireModeRange = 0x058;

    /// <summary>
    /// <c>AMMO_PER_SHOT</c> - <c>rec+0x05c</c>, loader <c>14222774b</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeAmmoPerShot = 0x05c;

    /// <summary><c>FireMode.ReloadTime</c> - the <c>0x60..0x70</c> reload run, docs/99 section 2.3. [I]</summary>
    /// <remarks>
    /// Datasheet column <c>RELOAD_TIME_MS</c>; loader <c>142227971</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </remarks>
    public const short FireModeReloadTime = 0x060;

    /// <summary><c>FireMode.ReloadChamberTime</c> (getter <c>FUN_1422a4f30</c>). [I]</summary>
    /// <remarks>
    /// Datasheet column <c>RELOAD_CHAMBER_TIME_MS</c>; loader <c>142227999</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </remarks>
    public const short FireModeReloadChamberTime = 0x064;

    /// <summary><c>FireMode.ReloadAmmoFillTime</c>. [I]</summary>
    /// <remarks>
    /// Datasheet column <c>RELOAD_AMMO_FILL_TIME_MS</c>; loader <c>1422279c1</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </remarks>
    public const short FireModeReloadAmmoFillTime = 0x068;

    /// <summary><c>FireMode.ReloadLoopStartTime</c>. [I]</summary>
    /// <remarks>
    /// Datasheet column <c>RELOAD_LOOP_START_TIME_MS</c>; loader <c>1422279e9</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </remarks>
    public const short FireModeReloadLoopStartTime = 0x06c;

    /// <summary><c>FireMode.ReloadLoopEndTime</c> (getter <c>FUN_1422a4ff0</c>). [I]</summary>
    /// <remarks>
    /// Datasheet column <c>RELOAD_LOOP_END_TIME_MS</c>; loader <c>142227a11</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </remarks>
    public const short FireModeReloadLoopEndTime = 0x070;

    /// <summary><c>FireMode.PelletsPerShot</c> (getter <c>FUN_1422a4c80</c>) - an <c>i8</c>. [I]</summary>
    /// <remarks>
    /// Datasheet column <c>PELLETS_PER_SHOT</c>; loader <c>142227a39</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </remarks>
    public const short FireModePelletsPerShot = 0x074;

    /// <summary>
    /// <c>PELLET_PATTERN_GROUP_ID</c> - <c>rec+0x078</c>, loader <c>142227a61</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModePelletPatternGroupId = 0x078;

    /// <summary><c>FireMode.PelletSpread</c> - <c>FUN_1422a4bd0</c>. [P]</summary>
    /// <remarks>
    /// Datasheet column <c>PELLET_SPREAD</c>; loader <c>142227a89</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </remarks>
    public const short FireModePelletSpread = 0x07c;

    /// <summary><c>FireMode.CofRecoil</c> - <c>FUN_14228d2d0</c> (<c>DAT_145592a78</c>, <c>def+0x80</c>). [P]</summary>
    /// <remarks>
    /// Datasheet column <c>COF_RECOIL</c>; loader <c>142227c0c</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </remarks>
    public const short FireModeCofRecoil = 0x080;

    /// <summary><c>FireMode.CofScalar</c> - <c>FUN_14228e540</c>, standing branch. [P] (unit scalar)</summary>
    /// <remarks>
    /// Datasheet column <c>COF_SCALAR</c>; loader <c>142227c3c</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </remarks>
    public const short FireModeCofScalar = 0x084;

    /// <summary><c>FireMode.CofScalarMoving</c> - <c>FUN_14228e540</c>, moving branch. [P] (unit scalar)</summary>
    /// <remarks>
    /// Datasheet column <c>COF_SCALAR_MOVING</c>; loader <c>142227c6c</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </remarks>
    public const short FireModeCofScalarMoving = 0x088;

    /// <summary>
    /// <c>COF_OVERRIDE</c> - <c>rec+0x08c</c>, loader <c>142227c9c</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeCofOverride = 0x08c;

    /// <summary><c>FireMode.CofScalarJumping</c> - the third branch of <c>FUN_14228e540</c>. [I] (unit scalar)</summary>
    /// <remarks>
    /// Datasheet column <c>COF_SCALAR_JUMPING</c>; loader <c>142227ccc</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </remarks>
    public const short FireModeCofScalarJumping = 0x090;

    /// <summary>
    /// <c>RECOIL_ANGLE_MIN</c> - <c>rec+0x094</c>, loader <c>142227cfc</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeRecoilAngleMin = 0x094;

    /// <summary>
    /// <c>RECOIL_ANGLE_MAX</c> - <c>rec+0x098</c>, loader <c>142227d2c</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeRecoilAngleMax = 0x098;

    /// <summary>
    /// <c>RECOIL_HORIZONTAL_TOLERANCE</c> - <c>rec+0x09c</c>, loader <c>142227d5c</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeRecoilHorizontalTolerance = 0x09c;

    /// <summary>
    /// <c>RECOIL_HORIZONTAL_MIN</c> - <c>rec+0x0a0</c>, loader <c>142227d8c</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeRecoilHorizontalMin = 0x0a0;

    /// <summary>
    /// <c>RECOIL_HORIZONTAL_MAX</c> - <c>rec+0x0a4</c>, loader <c>142227dbf</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeRecoilHorizontalMax = 0x0a4;

    /// <summary>
    /// <c>RECOIL_MAGNITUDE_MIN</c> - <c>rec+0x0a8</c>, loader <c>142227df2</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeRecoilMagnitudeMin = 0x0a8;

    /// <summary>
    /// <c>RECOIL_MAGNITUDE_MAX</c> - <c>rec+0x0ac</c>, loader <c>142227e25</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeRecoilMagnitudeMax = 0x0ac;

    /// <summary>
    /// <c>RECOIL_RECOVERY_DELAY_MS</c> - <c>rec+0x0b0</c>, loader <c>142227e58</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeRecoilRecoveryDelayMs = 0x0b0;

    /// <summary>
    /// <c>RECOIL_RECOVERY_RATE</c> - <c>rec+0x0b4</c>, loader <c>142227e83</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeRecoilRecoveryRate = 0x0b4;

    /// <summary>
    /// <c>RECOIL_RECOVERY_ACCELERATION</c> - <c>rec+0x0b8</c>, loader <c>142227eb6</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeRecoilRecoveryAcceleration = 0x0b8;

    /// <summary>
    /// <c>RECOIL_SHOTS_AT_MIN_MAGNITUDE</c> - <c>rec+0x0bc</c>, loader <c>142227ee9</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeRecoilShotsAtMinMagnitude = 0x0bc;

    /// <summary>
    /// <c>RECOIL_MAX_TOTAL_MAGNITUDE</c> - <c>rec+0x0c0</c>, loader <c>142227f14</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeRecoilMaxTotalMagnitude = 0x0c0;

    /// <summary>
    /// <c>RECOIL_INCREASE</c> - <c>rec+0x0c4</c>, loader <c>142227f47</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeRecoilIncrease = 0x0c4;

    /// <summary>
    /// <c>RECOIL_INCREASE_CROUCHED</c> - <c>rec+0x0c8</c>, loader <c>142227f7a</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeRecoilIncreaseCrouched = 0x0c8;

    /// <summary>
    /// <c>RECOIL_FIRST_SHOT_MODIFIER</c> - <c>rec+0x0cc</c>, loader <c>142228013</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeRecoilFirstShotModifier = 0x0cc;

    /// <summary>
    /// <c>RECOIL_HORIZONTAL_MIN_INCREASE</c> - <c>rec+0x0d0</c>, loader <c>142227fad</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeRecoilHorizontalMinIncrease = 0x0d0;

    /// <summary>
    /// <c>RECOIL_HORIZONTAL_MAX_INCREASE</c> - <c>rec+0x0d4</c>, loader <c>142227fe0</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeRecoilHorizontalMaxIncrease = 0x0d4;

    /// <summary>
    /// <c>LOCK_ON_ICON_ID</c> - <c>rec+0x0d8</c>, loader <c>14222811d</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeLockOnIconId = 0x0d8;

    /// <summary>
    /// <c>HUD_IMAGE_ID</c> - <c>rec+0x0dc</c>, loader <c>142228046</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeHudImageId = 0x0dc;

    /// <summary>
    /// <c>TARGET_REQUIREMENT</c> - <c>rec+0x0e0</c>, loader <c>142228071</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeTargetRequirement = 0x0e0;

    /// <summary>
    /// <c>FIRE_ANIM_DURATION_MS</c> - <c>rec+0x0e4</c>, loader <c>14222809c</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeFireAnimDurationMs = 0x0e4;

    /// <summary>
    /// <c>SEQUENTIAL_FIRE_ANIM_START</c> - <c>rec+0x0e8</c>, loader <c>1422280c7</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeSequentialFireAnimStart = 0x0e8;

    /// <summary>
    /// <c>SEQUENTIAL_FIRE_ANIM_COUNT</c> - <c>rec+0x0ec</c>, loader <c>1422280f2</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeSequentialFireAnimCount = 0x0ec;

    /// <summary><c>FireMode.CylofRecoil</c> - <c>FUN_14228d2d0</c> (<c>DAT_145592b48</c>). [P]</summary>
    /// <remarks>
    /// Datasheet column <c>CYLOF_RECOIL</c>; loader <c>142228643</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </remarks>
    public const short FireModeCylofRecoil = 0x0f0;

    /// <summary><c>FireMode.CylofScalar</c> - <c>FUN_14228e7e0</c>. [P] (unit scalar)</summary>
    /// <remarks>
    /// Datasheet column <c>CYLOF_SCALAR</c>; loader <c>142228674</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </remarks>
    public const short FireModeCylofScalar = 0x0f4;

    /// <summary><c>FireMode.CylofScalarMoving</c> - <c>FUN_14228e7e0</c>. [P] (unit scalar)</summary>
    /// <remarks>
    /// Datasheet column <c>CYLOF_SCALAR_MOVING</c>; loader <c>1422286a5</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </remarks>
    public const short FireModeCylofScalarMoving = 0x0f8;

    /// <summary>
    /// <c>CYLOF_OVERRIDE</c> - <c>rec+0x0fc</c>, loader <c>1422286d6</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeCylofOverride = 0x0fc;

    /// <summary>
    /// <c>FIRE_DETECT_RANGE</c> - <c>rec+0x100</c>, loader <c>142227bae</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeFireDetectRange = 0x100;

    /// <summary>
    /// <c>EFFECT_GROUP</c> - <c>rec+0x104</c>, loader <c>142227b83</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeEffectGroup = 0x104;

    /// <summary>
    /// <c>PLAYER_STATE_GROUP_ID</c> - <c>rec+0x108</c>, loader <c>142227b02</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModePlayerStateGroupId = 0x108;

    /// <summary><c>FireMode.MovementModifier</c> - <c>FUN_1422a4a60</c>. [P] (unit scalar)</summary>
    /// <remarks>
    /// Datasheet column <c>MOVEMENT_MODIFIER</c>; loader <c>1422277cb</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </remarks>
    public const short FireModeMovementModifier = 0x10c;

    /// <summary><c>FireMode.TurnModifier</c> - <c>FUN_1422a5760</c>. [P] (unit scalar)</summary>
    /// <remarks>
    /// Datasheet column <c>TURN_MODIFIER</c>; loader <c>1422277fe</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </remarks>
    public const short FireModeTurnModifier = 0x110;

    /// <summary>
    /// <c>LOCK_ON_ANGLE</c> - <c>rec+0x114</c>, loader <c>142228148</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeLockOnAngle = 0x114;

    /// <summary>
    /// <c>LOCK_ON_RADIUS</c> - <c>rec+0x118</c>, loader <c>14222817b</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeLockOnRadius = 0x118;

    /// <summary>
    /// <c>LOCK_ON_RANGE</c> - <c>rec+0x11c</c>, loader <c>1422281ae</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeLockOnRange = 0x11c;

    /// <summary>
    /// <c>LOCK_ON_ACQUIRE_TIME_MS</c> - <c>rec+0x120</c>, loader <c>1422281e1</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeLockOnAcquireTimeMs = 0x120;

    /// <summary>
    /// <c>LOCK_ON_LOSE_TIME_MS</c> - <c>rec+0x124</c>, loader <c>14222820c</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeLockOnLoseTimeMs = 0x124;

    /// <summary>
    /// <b>[P] <c>rec+0x128</c> = <c>DEFAULT_ZOOM</c> (<c>FireMode.DefaultZoom</c>) - THE ADS ZOOM.</b>
    /// A multiplier, <c>1.0f</c> = no zoom.
    /// <para>
    /// Two independent bindings. The <b>datasheet loader</b> stores the <c>DEFAULT_ZOOM</c> column
    /// at <c>obj+0x108</c> (<c>142227ab9</c> <c>LEA RDX,[s_DEFAULT_ZOOM_1435e93a8]</c> then
    /// <c>142227ae2</c> <c>MOVSS [RBX + 0x108],XMM1</c>), and the loader's object base is the
    /// network record's <c>rec+0x20</c>, so the column is <c>rec+0x128</c>. The <b>getter</b>
    /// <c>FUN_141488490</c> reads exactly <c>rec+0x128</c> and hands it to the stat-override helper
    /// under the key <c>DAT_1452362c8</c>, whose initialiser string is <c>"FireMode.DefaultZoom"</c>
    /// (<c>FUN_140655760</c>).
    /// </para>
    /// <para>
    /// <b>Where the number lands.</b> <c>FUN_1411c8740</c> interpolates the character's own
    /// <c>+0x55a4</c> toward <c>FUN_141488490</c> and <c>+0x55ac</c> toward
    /// <see cref="FireModeArmsFovScalar"/>; <c>FUN_1411c2ce0</c> / <c>FUN_1411ca9e0</c> snap both on
    /// a weapon change and write <c>1.0f</c> when there is no weapon. The client's own debug HUD
    /// prints those two words as <c>"Zoom:  %f  First Person FOV: %f"</c> (<c>FUN_140f786f0</c>,
    /// string <c>0x14317a4a0</c>). The mouse path bands the same word: <c>FUN_140e40090</c> and
    /// <c>FUN_140e4a010</c> pick <c>scopedMouseSensitivity</c> above <c>_DAT_143275150 = 2.0f</c>,
    /// <c>ADSMouseSensitivity</c> above <c>1.0f</c>, and the hip profile otherwise - so the client
    /// itself defines <c>(1.0, 2.0]</c> as the iron-sights band.
    /// </para>
    /// <para>
    /// This <b>corrects docs/107 section 8.6</b>, which recorded a negative proof for
    /// <c>DEFAULT_ZOOM</c>. That scan was scoped to <c>DAT_145592838</c> / <c>DAT_1455937f8</c>,
    /// which are a different pair of statics built from the same string; the pair the camera uses is
    /// <c>DAT_1452362c8</c>. Dumps: <c>out\w14-adsfov\disasm-firemodes-loader.txt</c>,
    /// <c>out\w14-adsfov\gh-zoomgetters</c>, <c>out\w14-adsfov\gh-statnames</c>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Datasheet column <c>DEFAULT_ZOOM</c>; loader <c>142227ab9</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </remarks>
    public const short FireModeDefaultZoom = 0x128;

    /// <summary>
    /// <c>FIRST_PERSON_OFFSET_X</c> - <c>rec+0x12c</c>, loader <c>142228237</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeFirstPersonOffsetX = 0x12c;

    /// <summary>
    /// <c>FIRST_PERSON_OFFSET_Y</c> - <c>rec+0x130</c>, loader <c>14222826a</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeFirstPersonOffsetY = 0x130;

    /// <summary>
    /// <c>FIRST_PERSON_OFFSET_Z</c> - <c>rec+0x134</c>, loader <c>14222829d</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeFirstPersonOffsetZ = 0x134;

    /// <summary>
    /// <c>RETICLE_ID</c> - <c>rec+0x138</c>, loader <c>142227b2d</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeReticleId = 0x138;

    /// <summary>
    /// <c>RETICLE_ID_SECONDARY</c> - <c>rec+0x13c</c>, loader <c>142228707</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeReticleIdSecondary = 0x13c;

    /// <summary>
    /// <c>FULL_SCREEN_EFFECT</c> - <c>rec+0x140</c>, loader <c>142227b58</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeFullScreenEffect = 0x140;

    /// <summary>
    /// <c>HEAT_PER_SHOT</c> - <c>rec+0x144</c>, loader <c>1422282d0</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeHeatPerShot = 0x144;

    /// <summary>
    /// <c>HEAT_THRESHOLD</c> - <c>rec+0x148</c>, loader <c>1422282fb</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeHeatThreshold = 0x148;

    /// <summary>
    /// <c>HEAT_RECOVERY_DELAY_MS</c> - <c>rec+0x14c</c>, loader <c>142228326</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeHeatRecoveryDelayMs = 0x14c;

    /// <summary>
    /// <c>SWAY_AMPLITUDE_X</c> - <c>rec+0x150</c>, loader <c>142228351</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeSwayAmplitudeX = 0x150;

    /// <summary>
    /// <c>SWAY_AMPLITUDE_Y</c> - <c>rec+0x154</c>, loader <c>142228384</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeSwayAmplitudeY = 0x154;

    /// <summary>
    /// <c>SWAY_PERIOD_X</c> - <c>rec+0x158</c>, loader <c>1422283b7</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeSwayPeriodX = 0x158;

    /// <summary>
    /// <c>SWAY_PERIOD_Y</c> - <c>rec+0x15c</c>, loader <c>1422283ea</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeSwayPeriodY = 0x15c;

    /// <summary>
    /// <c>SWAY_INITIAL_Y_OFFSET</c> - <c>rec+0x160</c>, loader <c>14222841d</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeSwayInitialYOffset = 0x160;

    /// <summary>
    /// <c>SWAY_CROUCH_SCALAR</c> - <c>rec+0x164</c>, loader <c>14222846c</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeSwayCrouchScalar = 0x164;

    /// <summary>
    /// <c>SWAY_PRONE_SCALAR</c> - <c>rec+0x168</c>, loader <c>14222849f</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeSwayProneScalar = 0x168;

    /// <summary>
    /// <b>[P] <c>rec+0x16c</c> = <c>ARMS_FOV_SCALAR</c> (<c>FireMode.ArmsFovScalar</c>)</b> - the
    /// first-person <em>viewmodel</em> FOV scalar, not the view's. Loader <c>142227bd9</c> /
    /// <c>MOVSS [RBX + 0x14c]</c>; getter <c>FUN_141488410</c>, key <c>DAT_145236278</c> =
    /// <c>"FireMode.ArmsFovScalar"</c> (<c>FUN_140654a30</c>), and the getter first asks the camera
    /// manager's <c>vtable+0x188</c> whether the view is first-person at all. It ships <c>1.0f</c>:
    /// it is one of the 34 <see cref="FireModeUnitScalars"/>, and a value other than the client's
    /// own default would distort the held weapon rather than zoom the view.
    /// </summary>
    /// <remarks>
    /// Datasheet column <c>ARMS_FOV_SCALAR</c>; loader <c>142227bd9</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </remarks>
    public const short FireModeArmsFovScalar = 0x16c;

    /// <summary>
    /// <c>ANIM_KICK_MAGNITUDE</c> - <c>rec+0x170</c>, loader <c>1422284d2</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeAnimKickMagnitude = 0x170;

    /// <summary>
    /// <c>ANIM_RECOIL_MAGNITUDE</c> - <c>rec+0x174</c>, loader <c>142228504</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeAnimRecoilMagnitude = 0x174;

    /// <summary>
    /// <c>DESCRIPTION_ID</c> - <c>rec+0x178</c>, loader <c>142228535</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeDescriptionId = 0x178;

    /// <summary>
    /// <c>INDIRECT_EFFECT</c> - <c>rec+0x17c</c>, loader <c>14222855e</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeIndirectEffect = 0x17c;

    /// <summary>
    /// <c>BULLET_ARC_KICK_ANGLE</c> - <c>rec+0x180</c>, loader <c>142228587</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeBulletArcKickAngle = 0x180;

    /// <summary>
    /// <c>PROJECTILE_SPEED_OVERRIDE</c> - <c>rec+0x184</c>, loader <c>1422285b8</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeProjectileSpeedOverride = 0x184;

    /// <summary>
    /// <c>INHERIT_FROM_ID</c> - <c>rec+0x188</c>, loader <c>1422285e9</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeInheritFromId = 0x188;

    /// <summary>
    /// <c>INHERIT_FROM_CHARGE_POWER</c> - <c>rec+0x18c</c>, loader <c>142228612</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeInheritFromChargePower = 0x18c;

    /// <summary>
    /// <c>MELEE_COMPOSITE_EFFECT_ID</c> - <c>rec+0x190</c>, loader <c>142228730</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeMeleeCompositeEffectId = 0x190;

    /// <summary>
    /// <c>MELEE_ABILITY_ID</c> - <c>rec+0x194</c>, loader <c>142228759</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeMeleeAbilityId = 0x194;

    /// <summary>
    /// <c>LAUNCH_PITCH_ADDITIVE_DEGREES</c> - <c>rec+0x198</c>, loader <c>142228782</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeLaunchPitchAdditiveDegrees = 0x198;

    /// <summary>
    /// <b>[P] <c>rec+0x19c</c> = <c>TP_FORCE_CAMERA_OVERRIDES</c></b> (loader <c>1422287b3</c>,
    /// <c>MOV byte ptr [RBX + 0x17c]</c>). <b>This corrects docs/107 section 8.6</b>, which called
    /// it "the first-person camera block's gate": it gates the <em>third</em>-person camera. The
    /// five functions that test it (<c>FUN_140e45f50</c>, <c>FUN_140e467a0</c>,
    /// <c>FUN_140e49670</c> / <c>760</c> / <c>870</c>) read the per-stance pitch-lerp quads
    /// <c>TP_EXTRA_LEAD_*</c> (<c>+0x1dc</c>, <c>+0x248</c>, <c>+0x2ac</c>),
    /// <c>TP_EXTRA_HEIGHT_*</c> (<c>+0x1ec</c>, <c>+0x258</c>, <c>+0x2bc</c>) and
    /// <c>TP_EXTRA_DRAW_*</c> (<c>+0x1fc</c>, <c>+0x268</c>; prone has no draw quad, which is why
    /// the draw lerp is skipped when the prone bit is set). Each quad is
    /// <c>(fromPitchA, fromPitchB, pitchA, pitchB)</c> in degrees.
    /// </summary>
    /// <remarks>
    /// Datasheet column <c>TP_FORCE_CAMERA_OVERRIDES</c>; loader <c>1422287b3</c>,
    /// one byte (<c>MOV byte ptr [RBX+off],0x1</c>). [P]
    /// </remarks>
    public const short FireModeTpForceCameraOverrides = 0x19c;

    /// <summary>
    /// <c>TP_CAMERA_DISTANCE</c> - <c>rec+0x1a0</c>, loader <c>142228bef</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCameraDistance = 0x1a0;

    /// <summary>
    /// <c>TP_CAMERA_INDOOR_DISTANCE</c> - <c>rec+0x1a4</c>, loader <c>142228c20</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCameraIndoorDistance = 0x1a4;

    /// <summary>
    /// <c>TP_CAMERA_MOVE_DISTANCE</c> - <c>rec+0x1a8</c>, loader <c>142228c51</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCameraMoveDistance = 0x1a8;

    /// <summary>
    /// <c>TP_CAMERA_MOVING_INDOOR_DISTANCE</c> - <c>rec+0x1ac</c>, loader <c>142228c82</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCameraMovingIndoorDistance = 0x1ac;

    /// <summary>
    /// <c>TP_CAMERA_LOOK_OFFSET_X</c> - <c>rec+0x1b0</c>, loader <c>1422287ea</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCameraLookOffsetX = 0x1b0;

    /// <summary>
    /// <c>TP_CAMERA_LOOK_OFFSET_Y</c> - <c>rec+0x1b4</c>, loader <c>14222881b</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCameraLookOffsetY = 0x1b4;

    /// <summary>
    /// <c>TP_CAMERA_LOOK_OFFSET_Z</c> - <c>rec+0x1b8</c>, loader <c>14222884c</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCameraLookOffsetZ = 0x1b8;

    /// <summary>
    /// <c>TP_CAMERA_POSITION_OFFSET_X</c> - <c>rec+0x1bc</c>, loader <c>14222887d</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCameraPositionOffsetX = 0x1bc;

    /// <summary>
    /// <c>TP_CAMERA_POSITION_OFFSET_Y</c> - <c>rec+0x1c0</c>, loader <c>1422288ae</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCameraPositionOffsetY = 0x1c0;

    /// <summary>
    /// <c>TP_CAMERA_POSITION_OFFSET_Z</c> - <c>rec+0x1c4</c>, loader <c>1422288df</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCameraPositionOffsetZ = 0x1c4;

    /// <summary>
    /// <c>TP_CAMERA_FOV</c> - <c>rec+0x1c8</c>, loader <c>142228910</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCameraFov = 0x1c8;

    /// <summary>
    /// <c>TP_CAMERA_POS_OFFSET_Y_MOV</c> - <c>rec+0x1cc</c>, loader <c>142228941</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCameraPosOffsetYMov = 0x1cc;

    /// <summary>
    /// <c>TP_CAMERA_LOOK_OFFSET_Y_MOV</c> - <c>rec+0x1d0</c>, loader <c>142228972</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCameraLookOffsetYMov = 0x1d0;

    /// <summary>
    /// <c>TP_CAMERA_MOVE_SPEED</c> - <c>rec+0x1d4</c>, loader <c>142228cb3</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCameraMoveSpeed = 0x1d4;

    /// <summary>
    /// <c>TP_CAMERA_RETURN_SPEED</c> - <c>rec+0x1d8</c>, loader <c>142228ce4</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCameraReturnSpeed = 0x1d8;

    /// <summary>
    /// <c>TP_EXTRA_LEAD_FROM_PITCH_A</c> - <c>rec+0x1dc</c>, loader <c>1422289a3</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpExtraLeadFromPitchA = 0x1dc;

    /// <summary>
    /// <c>TP_EXTRA_LEAD_FROM_PITCH_B</c> - <c>rec+0x1e0</c>, loader <c>1422289d4</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpExtraLeadFromPitchB = 0x1e0;

    /// <summary>
    /// <c>TP_EXTRA_LEAD_PITCH_A</c> - <c>rec+0x1e4</c>, loader <c>142228a05</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpExtraLeadPitchA = 0x1e4;

    /// <summary>
    /// <c>TP_EXTRA_LEAD_PITCH_B</c> - <c>rec+0x1e8</c>, loader <c>142228a36</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpExtraLeadPitchB = 0x1e8;

    /// <summary>
    /// <c>TP_EXTRA_HEIGHT_FROM_PITCH_A</c> - <c>rec+0x1ec</c>, loader <c>142228a67</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpExtraHeightFromPitchA = 0x1ec;

    /// <summary>
    /// <c>TP_EXTRA_HEIGHT_FROM_PITCH_B</c> - <c>rec+0x1f0</c>, loader <c>142228a98</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpExtraHeightFromPitchB = 0x1f0;

    /// <summary>
    /// <c>TP_EXTRA_HEIGHT_PITCH_A</c> - <c>rec+0x1f4</c>, loader <c>142228ac9</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpExtraHeightPitchA = 0x1f4;

    /// <summary>
    /// <c>TP_EXTRA_HEIGHT_PITCH_B</c> - <c>rec+0x1f8</c>, loader <c>142228afa</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpExtraHeightPitchB = 0x1f8;

    /// <summary>
    /// <c>TP_EXTRA_DRAW_FROM_PITCH_A</c> - <c>rec+0x1fc</c>, loader <c>142228b2b</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpExtraDrawFromPitchA = 0x1fc;

    /// <summary>
    /// <c>TP_EXTRA_DRAW_FROM_PITCH_B</c> - <c>rec+0x200</c>, loader <c>142228b5c</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpExtraDrawFromPitchB = 0x200;

    /// <summary>
    /// <c>TP_EXTRA_DRAW_PITCH_A</c> - <c>rec+0x204</c>, loader <c>142228b8d</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpExtraDrawPitchA = 0x204;

    /// <summary>
    /// <c>TP_EXTRA_DRAW_PITCH_B</c> - <c>rec+0x208</c>, loader <c>142228bbe</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpExtraDrawPitchB = 0x208;

    /// <summary>
    /// <c>TP_CR_CAMERA_DISTANCE</c> - <c>rec+0x20c</c>, loader <c>14222911a</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrCameraDistance = 0x20c;

    /// <summary>
    /// <c>TP_CR_CAMERA_INDOOR_DISTANCE</c> - <c>rec+0x210</c>, loader <c>14222914b</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrCameraIndoorDistance = 0x210;

    /// <summary>
    /// <c>TP_CR_CAMERA_MOVE_DISTANCE</c> - <c>rec+0x214</c>, loader <c>14222917c</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrCameraMoveDistance = 0x214;

    /// <summary>
    /// <c>TP_CR_CAMERA_MOVING_INDOOR_DISTANCE</c> - <c>rec+0x218</c>, loader <c>1422291ad</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrCameraMovingIndoorDistance = 0x218;

    /// <summary>
    /// <c>TP_CR_CAMERA_LOOK_OFFSET_X</c> - <c>rec+0x21c</c>, loader <c>142228d15</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrCameraLookOffsetX = 0x21c;

    /// <summary>
    /// <c>TP_CR_CAMERA_LOOK_OFFSET_Y</c> - <c>rec+0x220</c>, loader <c>142228d46</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrCameraLookOffsetY = 0x220;

    /// <summary>
    /// <c>TP_CR_CAMERA_LOOK_OFFSET_Z</c> - <c>rec+0x224</c>, loader <c>142228d77</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrCameraLookOffsetZ = 0x224;

    /// <summary>
    /// <c>TP_CR_CAMERA_POSITION_OFFSET_X</c> - <c>rec+0x228</c>, loader <c>142228da8</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrCameraPositionOffsetX = 0x228;

    /// <summary>
    /// <c>TP_CR_CAMERA_POSITION_OFFSET_Y</c> - <c>rec+0x22c</c>, loader <c>142228dd9</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrCameraPositionOffsetY = 0x22c;

    /// <summary>
    /// <c>TP_CR_CAMERA_POSITION_OFFSET_Z</c> - <c>rec+0x230</c>, loader <c>142228e0a</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrCameraPositionOffsetZ = 0x230;

    /// <summary>
    /// <c>TP_CR_CAMERA_FOV</c> - <c>rec+0x234</c>, loader <c>142228e3b</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrCameraFov = 0x234;

    /// <summary>
    /// <c>TP_CR_CAMERA_POS_OFFSET_Y_MOV</c> - <c>rec+0x238</c>, loader <c>142228e6c</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrCameraPosOffsetYMov = 0x238;

    /// <summary>
    /// <c>TP_CR_CAMERA_LOOK_OFFSET_Y_MOV</c> - <c>rec+0x23c</c>, loader <c>142228e9d</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrCameraLookOffsetYMov = 0x23c;

    /// <summary>
    /// <c>TP_CR_CAMERA_MOVE_SPEED</c> - <c>rec+0x240</c>, loader <c>1422291de</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrCameraMoveSpeed = 0x240;

    /// <summary>
    /// <c>TP_CR_CAMERA_RETURN_SPEED</c> - <c>rec+0x244</c>, loader <c>14222920f</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrCameraReturnSpeed = 0x244;

    /// <summary>
    /// <c>TP_CR_EXTRA_LEAD_FROM_PITCH_A</c> - <c>rec+0x248</c>, loader <c>142228ece</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrExtraLeadFromPitchA = 0x248;

    /// <summary>
    /// <c>TP_CR_EXTRA_LEAD_FROM_PITCH_B</c> - <c>rec+0x24c</c>, loader <c>142228eff</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrExtraLeadFromPitchB = 0x24c;

    /// <summary>
    /// <c>TP_CR_EXTRA_LEAD_PITCH_A</c> - <c>rec+0x250</c>, loader <c>142228f30</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrExtraLeadPitchA = 0x250;

    /// <summary>
    /// <c>TP_CR_EXTRA_LEAD_PITCH_B</c> - <c>rec+0x254</c>, loader <c>142228f61</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrExtraLeadPitchB = 0x254;

    /// <summary>
    /// <c>TP_CR_EXTRA_HEIGHT_FROM_PITCH_A</c> - <c>rec+0x258</c>, loader <c>142228f92</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrExtraHeightFromPitchA = 0x258;

    /// <summary>
    /// <c>TP_CR_EXTRA_HEIGHT_FROM_PITCH_B</c> - <c>rec+0x25c</c>, loader <c>142228fc3</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrExtraHeightFromPitchB = 0x25c;

    /// <summary>
    /// <c>TP_CR_EXTRA_HEIGHT_PITCH_A</c> - <c>rec+0x260</c>, loader <c>142228ff4</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrExtraHeightPitchA = 0x260;

    /// <summary>
    /// <c>TP_CR_EXTRA_HEIGHT_PITCH_B</c> - <c>rec+0x264</c>, loader <c>142229025</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrExtraHeightPitchB = 0x264;

    /// <summary>
    /// <c>TP_CR_EXTRA_DRAW_FROM_PITCH_A</c> - <c>rec+0x268</c>, loader <c>142229056</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrExtraDrawFromPitchA = 0x268;

    /// <summary>
    /// <c>TP_CR_EXTRA_DRAW_FROM_PITCH_B</c> - <c>rec+0x26c</c>, loader <c>142229087</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrExtraDrawFromPitchB = 0x26c;

    /// <summary>
    /// <c>TP_CR_EXTRA_DRAW_PITCH_A</c> - <c>rec+0x270</c>, loader <c>1422290b8</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrExtraDrawPitchA = 0x270;

    /// <summary>
    /// <c>TP_CR_EXTRA_DRAW_PITCH_B</c> - <c>rec+0x274</c>, loader <c>1422290e9</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCrExtraDrawPitchB = 0x274;

    /// <summary>
    /// <c>TP_PR_CAMERA_DISTANCE</c> - <c>rec+0x278</c>, loader <c>14222951f</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrCameraDistance = 0x278;

    /// <summary>
    /// <c>TP_PR_CAMERA_INDOOR_DISTANCE</c> - <c>rec+0x27c</c>, loader <c>142229550</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrCameraIndoorDistance = 0x27c;

    /// <summary>
    /// <c>TP_PR_CAMERA_MOVE_DISTANCE</c> - <c>rec+0x280</c>, loader <c>142229581</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrCameraMoveDistance = 0x280;

    /// <summary>
    /// <c>TP_PR_CAMERA_MOVING_INDOOR_DISTANCE</c> - <c>rec+0x284</c>, loader <c>1422295b2</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrCameraMovingIndoorDistance = 0x284;

    /// <summary>
    /// <c>TP_PR_CAMERA_LOOK_OFFSET_X</c> - <c>rec+0x288</c>, loader <c>142229240</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrCameraLookOffsetX = 0x288;

    /// <summary>
    /// <c>TP_PR_CAMERA_LOOK_OFFSET_Y</c> - <c>rec+0x28c</c>, loader <c>142229271</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrCameraLookOffsetY = 0x28c;

    /// <summary>
    /// <c>TP_PR_CAMERA_LOOK_OFFSET_Z</c> - <c>rec+0x290</c>, loader <c>1422292a2</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrCameraLookOffsetZ = 0x290;

    /// <summary>
    /// <c>TP_PR_CAMERA_POSITION_OFFSET_X</c> - <c>rec+0x294</c>, loader <c>1422292d3</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrCameraPositionOffsetX = 0x294;

    /// <summary>
    /// <c>TP_PR_CAMERA_POSITION_OFFSET_Y</c> - <c>rec+0x298</c>, loader <c>142229304</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrCameraPositionOffsetY = 0x298;

    /// <summary>
    /// <c>TP_PR_CAMERA_POSITION_OFFSET_Z</c> - <c>rec+0x29c</c>, loader <c>142229335</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrCameraPositionOffsetZ = 0x29c;

    /// <summary>
    /// <c>TP_PR_CAMERA_FOV</c> - <c>rec+0x2a0</c>, loader <c>142229366</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrCameraFov = 0x2a0;

    /// <summary>
    /// <c>TP_PR_CAMERA_MOVE_SPEED</c> - <c>rec+0x2a4</c>, loader <c>1422295e3</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrCameraMoveSpeed = 0x2a4;

    /// <summary>
    /// <c>TP_PR_CAMERA_RETURN_SPEED</c> - <c>rec+0x2a8</c>, loader <c>142229614</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrCameraReturnSpeed = 0x2a8;

    /// <summary>
    /// <c>TP_PR_EXTRA_LEAD_FROM_PITCH_A</c> - <c>rec+0x2ac</c>, loader <c>142229397</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrExtraLeadFromPitchA = 0x2ac;

    /// <summary>
    /// <c>TP_PR_EXTRA_LEAD_FROM_PITCH_B</c> - <c>rec+0x2b0</c>, loader <c>1422293c8</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrExtraLeadFromPitchB = 0x2b0;

    /// <summary>
    /// <c>TP_PR_EXTRA_LEAD_PITCH_A</c> - <c>rec+0x2b4</c>, loader <c>1422293f9</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrExtraLeadPitchA = 0x2b4;

    /// <summary>
    /// <c>TP_PR_EXTRA_LEAD_PITCH_B</c> - <c>rec+0x2b8</c>, loader <c>14222942a</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrExtraLeadPitchB = 0x2b8;

    /// <summary>
    /// <c>TP_PR_EXTRA_HEIGHT_FROM_PITCH_A</c> - <c>rec+0x2bc</c>, loader <c>14222945b</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrExtraHeightFromPitchA = 0x2bc;

    /// <summary>
    /// <c>TP_PR_EXTRA_HEIGHT_FROM_PITCH_B</c> - <c>rec+0x2c0</c>, loader <c>14222948c</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrExtraHeightFromPitchB = 0x2c0;

    /// <summary>
    /// <c>TP_PR_EXTRA_HEIGHT_PITCH_A</c> - <c>rec+0x2c4</c>, loader <c>1422294bd</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrExtraHeightPitchA = 0x2c4;

    /// <summary>
    /// <c>TP_PR_EXTRA_HEIGHT_PITCH_B</c> - <c>rec+0x2c8</c>, loader <c>1422294ee</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpPrExtraHeightPitchB = 0x2c8;

    /// <summary>
    /// <c>TP_ALLOW_MOVE_HEIGHTS</c> - <c>rec+0x2cc</c>, loader <c>142229645</c>,
    /// one byte (<c>MOV byte ptr [RBX+off],0x1</c>). [P]
    /// </summary>
    public const short FireModeTpAllowMoveHeights = 0x2cc;

    /// <summary>
    /// <b>[P] <c>rec+0x2cd</c> = <c>FP_FORCE_CAMERA_OVERRIDES</c></b> (loader <c>14222967c</c>,
    /// <c>MOV byte ptr [RBX + 0x2ad]</c>), the gate on the <em>absolute</em> first-person FOV
    /// override: <c>FUN_140e3fff0</c> writes its out-parameter only when this byte is set, taking
    /// <see cref="FireModeFpProneCameraFov"/> for the prone bit <c>0x04</c>,
    /// <see cref="FireModeFpCrouchedCameraFov"/> for <c>0x0a</c> and
    /// <see cref="FireModeFpCameraFov"/> otherwise (<c>player+0x338 +0x37e7</c>).
    /// <para>
    /// <b>Cranberry writes 0 here deliberately.</b> These three are an absolute FOV in degrees and
    /// would replace the player's own <c>verticalFOV</c> option; <see cref="FireModeDefaultZoom"/>
    /// is a multiplier and composes with it. The offsets are named so a later lane can use them.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Datasheet column <c>FP_FORCE_CAMERA_OVERRIDES</c>; loader <c>14222967c</c>,
    /// one byte (<c>MOV byte ptr [RBX+off],0x1</c>). [P]
    /// </remarks>
    public const short FireModeFpForceCameraOverrides = 0x2cd;

    /// <summary><c>rec+0x2d0</c> = <c>FP_CAMERA_FOV</c>, standing (<c>FUN_140e3fff0</c>). [P]</summary>
    /// <remarks>
    /// Datasheet column <c>FP_CAMERA_FOV</c>; loader <c>1422296b3</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </remarks>
    public const short FireModeFpCameraFov = 0x2d0;

    /// <summary><c>rec+0x2d4</c> = <c>FP_CR_CAMERA_FOV</c>, crouched (<c>FUN_140e3fff0</c>). [P]</summary>
    /// <remarks>
    /// Datasheet column <c>FP_CR_CAMERA_FOV</c>; loader <c>1422296e4</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </remarks>
    public const short FireModeFpCrouchedCameraFov = 0x2d4;

    /// <summary><c>rec+0x2d8</c> = <c>FP_PR_CAMERA_FOV</c>, prone (<c>FUN_140e3fff0</c>). [P]</summary>
    /// <remarks>
    /// Datasheet column <c>FP_PR_CAMERA_FOV</c>; loader <c>142229715</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </remarks>
    public const short FireModeFpProneCameraFov = 0x2d8;

    /// <summary>
    /// <c>FORCE_FP_SCOPE</c> - <c>rec+0x2dc</c>, loader <c>142229746</c>,
    /// one byte (<c>MOV byte ptr [RBX+off],0x1</c>). [P]
    /// </summary>
    public const short FireModeForceFpScope = 0x2dc;

    /// <summary>
    /// <c>ALLOW_DEPTH_ADJUSTMENT</c> - <c>rec+0x2dd</c>, loader <c>14222977d</c>,
    /// one byte (<c>MOV byte ptr [RBX+off],0x1</c>). [P]
    /// </summary>
    public const short FireModeAllowDepthAdjustment = 0x2dd;

    /// <summary>
    /// <c>AIM_ASSIST_CONFIG</c> - <c>rec+0x2e0</c>, loader <c>1422297b4</c>,
    /// an integer (<c>LEA RDX,[RBX+off]</c> then the string-to-int helper <c>0x14007022f</c>). [P]
    /// </summary>
    public const short FireModeAimAssistConfig = 0x2e0;

    /// <summary>
    /// <c>RETICLE_MAX_DISTANCE</c> - <c>rec+0x2e4</c>, loader <c>1422297dd</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeReticleMaxDistance = 0x2e4;

    /// <summary>
    /// <c>TP_CAMERA_INDOOR_OUTDOOR_SPEED</c> - <c>rec+0x2e8</c>, loader <c>14222980e</c>,
    /// a four-byte IEEE-754 float (<c>MOVSS</c>). [P]
    /// </summary>
    public const short FireModeTpCameraIndoorOutdoorSpeed = 0x2e8;

    /// <summary>
    /// <b>[P] Every <c>FireModes</c> datasheet column the client's own row loader names, with
    /// the record offset it stores it at.</b> 200 entries: 21 flag bits of
    /// <c>rec+0x20..0x22</c> and 179 value columns.
    /// <para>
    /// This is the table form of the constants above, for a reader that wants to walk the
    /// record rather than name one field. It is <b>documentation, not behaviour</b>: nothing on
    /// the wire path reads it, and <see cref="FireModeBody"/> - not this - is what
    /// <c>FireModeRecord.WriteTo</c> walks.
    /// </para>
    /// <para>
    /// Two entries share a name: <c>MELEE_COMPOSITE_EFFECT_ID</c> appears once as a flag bit
    /// (<c>rec+0x22</c> bit <c>0x04</c>, loader <c>142227659</c>) and once as an id
    /// (<c>rec+0x190</c>, loader <c>142228730</c>). Both reads are in the binary and both take
    /// the same string at <c>0x1435e9178</c>; the second is the one docs/99 section 2.3 row 89
    /// already carried as [I].
    /// </para>
    /// <para>
    /// Pinned by <c>WeaponListColumnTableTests</c> against
    /// <c>tools/data/firemodes-columns.tsv</c>, <c>firemodes-columns-ids.tsv</c> and
    /// <c>firemodes-columns-full.tsv</c>, so the TSVs under <c>C:\Aug2017\out</c> are never
    /// load-bearing at runtime.
    /// </para>
    /// </summary>
    public static IReadOnlyList<FireModeColumn> FireModeColumns { get; } =
    [
        new("HIDE_UNAVAILABLE", 0x020, FireModeColumnKind.FlagBit, 0x80),
        new("SPRINT_FIRE", 0x020, FireModeColumnKind.FlagBit, 0x40),
        new("AUTOMATIC", 0x020, FireModeColumnKind.FlagBit, 0x20),
        new("GRIEF_IMMUNE", 0x020, FireModeColumnKind.FlagBit, 0x10),
        new("USE_IN_WATER", 0x020, FireModeColumnKind.FlagBit, 0x08),
        new("IRON_SIGHTS", 0x020, FireModeColumnKind.FlagBit, 0x04),
        new("HIDE_WEAPON", 0x020, FireModeColumnKind.FlagBit, 0x02),
        new("LASER_GUIDED", 0x020, FireModeColumnKind.FlagBit, 0x01),
        new("CAN_STEADY_SWAY", 0x021, FireModeColumnKind.FlagBit, 0x80),
        new("HOLD_PLAYER_STATE_WHILE_FIRING", 0x021, FireModeColumnKind.FlagBit, 0x40),
        new("BLOCK_AUTO_RELOAD", 0x021, FireModeColumnKind.FlagBit, 0x20),
        new("SPAWN_PROJECTILE_AT_EYE", 0x021, FireModeColumnKind.FlagBit, 0x10),
        new("FIRE_NEEDS_LOCK", 0x021, FireModeColumnKind.FlagBit, 0x08),
        new("CHECK_ENTER_FIRE_STATE", 0x021, FireModeColumnKind.FlagBit, 0x04),
        new("CONTINUOUS_RELOAD", 0x021, FireModeColumnKind.FlagBit, 0x02),
        new("CAN_LOCKON_WHILE_BUSY", 0x021, FireModeColumnKind.FlagBit, 0x01),
        new("MAINTAIN_LOCK", 0x022, FireModeColumnKind.FlagBit, 0x80),
        new("UNDEFINED1", 0x022, FireModeColumnKind.FlagBit, 0x10),
        new("UNDEFINED2", 0x022, FireModeColumnKind.FlagBit, 0x08),
        new("MELEE_COMPOSITE_EFFECT_ID", 0x022, FireModeColumnKind.FlagBit, 0x04),
        new("USE_CONE_INSTEAD_OF_CYLINDER", 0x022, FireModeColumnKind.FlagBit, 0x02),
        new("TYPE", 0x024, FireModeColumnKind.Int),
        new("AMMO_ITEM_ID", 0x02c, FireModeColumnKind.Int),
        new("AMMO_SLOT", 0x030, FireModeColumnKind.Int),
        new("BURST_COUNT", 0x034, FireModeColumnKind.Int),
        new("FIRE_DURATION_MS", 0x038, FireModeColumnKind.Int),
        new("FIRE_COOLDOWN_DURATION_MS", 0x03c, FireModeColumnKind.Int),
        new("REFIRE_TIME_MS", 0x040, FireModeColumnKind.Int),
        new("FIRE_DELAY_MS", 0x044, FireModeColumnKind.Int),
        new("AUTO_FIRE_TIME_MS", 0x048, FireModeColumnKind.Int),
        new("CHARGE_UP_TIME_MS", 0x04c, FireModeColumnKind.Int),
        new("COOK_TIME_MS", 0x050, FireModeColumnKind.Int),
        new("SPIN_UP_TIME_MS", 0x054, FireModeColumnKind.Int),
        new("RANGE", 0x058, FireModeColumnKind.Float),
        new("AMMO_PER_SHOT", 0x05c, FireModeColumnKind.Int),
        new("RELOAD_TIME_MS", 0x060, FireModeColumnKind.Int),
        new("RELOAD_CHAMBER_TIME_MS", 0x064, FireModeColumnKind.Int),
        new("RELOAD_AMMO_FILL_TIME_MS", 0x068, FireModeColumnKind.Int),
        new("RELOAD_LOOP_START_TIME_MS", 0x06c, FireModeColumnKind.Int),
        new("RELOAD_LOOP_END_TIME_MS", 0x070, FireModeColumnKind.Int),
        new("PELLETS_PER_SHOT", 0x074, FireModeColumnKind.Int),
        new("PELLET_PATTERN_GROUP_ID", 0x078, FireModeColumnKind.Int),
        new("PELLET_SPREAD", 0x07c, FireModeColumnKind.Float),
        new("COF_RECOIL", 0x080, FireModeColumnKind.Float),
        new("COF_SCALAR", 0x084, FireModeColumnKind.Float),
        new("COF_SCALAR_MOVING", 0x088, FireModeColumnKind.Float),
        new("COF_OVERRIDE", 0x08c, FireModeColumnKind.Float),
        new("COF_SCALAR_JUMPING", 0x090, FireModeColumnKind.Float),
        new("RECOIL_ANGLE_MIN", 0x094, FireModeColumnKind.Float),
        new("RECOIL_ANGLE_MAX", 0x098, FireModeColumnKind.Float),
        new("RECOIL_HORIZONTAL_TOLERANCE", 0x09c, FireModeColumnKind.Float),
        new("RECOIL_HORIZONTAL_MIN", 0x0a0, FireModeColumnKind.Float),
        new("RECOIL_HORIZONTAL_MAX", 0x0a4, FireModeColumnKind.Float),
        new("RECOIL_MAGNITUDE_MIN", 0x0a8, FireModeColumnKind.Float),
        new("RECOIL_MAGNITUDE_MAX", 0x0ac, FireModeColumnKind.Float),
        new("RECOIL_RECOVERY_DELAY_MS", 0x0b0, FireModeColumnKind.Int),
        new("RECOIL_RECOVERY_RATE", 0x0b4, FireModeColumnKind.Float),
        new("RECOIL_RECOVERY_ACCELERATION", 0x0b8, FireModeColumnKind.Float),
        new("RECOIL_SHOTS_AT_MIN_MAGNITUDE", 0x0bc, FireModeColumnKind.Int),
        new("RECOIL_MAX_TOTAL_MAGNITUDE", 0x0c0, FireModeColumnKind.Float),
        new("RECOIL_INCREASE", 0x0c4, FireModeColumnKind.Float),
        new("RECOIL_INCREASE_CROUCHED", 0x0c8, FireModeColumnKind.Float),
        new("RECOIL_FIRST_SHOT_MODIFIER", 0x0cc, FireModeColumnKind.Float),
        new("RECOIL_HORIZONTAL_MIN_INCREASE", 0x0d0, FireModeColumnKind.Float),
        new("RECOIL_HORIZONTAL_MAX_INCREASE", 0x0d4, FireModeColumnKind.Float),
        new("LOCK_ON_ICON_ID", 0x0d8, FireModeColumnKind.Int),
        new("HUD_IMAGE_ID", 0x0dc, FireModeColumnKind.Int),
        new("TARGET_REQUIREMENT", 0x0e0, FireModeColumnKind.Int),
        new("FIRE_ANIM_DURATION_MS", 0x0e4, FireModeColumnKind.Int),
        new("SEQUENTIAL_FIRE_ANIM_START", 0x0e8, FireModeColumnKind.Int),
        new("SEQUENTIAL_FIRE_ANIM_COUNT", 0x0ec, FireModeColumnKind.Int),
        new("CYLOF_RECOIL", 0x0f0, FireModeColumnKind.Float),
        new("CYLOF_SCALAR", 0x0f4, FireModeColumnKind.Float),
        new("CYLOF_SCALAR_MOVING", 0x0f8, FireModeColumnKind.Float),
        new("CYLOF_OVERRIDE", 0x0fc, FireModeColumnKind.Float),
        new("FIRE_DETECT_RANGE", 0x100, FireModeColumnKind.Int),
        new("EFFECT_GROUP", 0x104, FireModeColumnKind.Int),
        new("PLAYER_STATE_GROUP_ID", 0x108, FireModeColumnKind.Int),
        new("MOVEMENT_MODIFIER", 0x10c, FireModeColumnKind.Float),
        new("TURN_MODIFIER", 0x110, FireModeColumnKind.Float),
        new("LOCK_ON_ANGLE", 0x114, FireModeColumnKind.Float),
        new("LOCK_ON_RADIUS", 0x118, FireModeColumnKind.Float),
        new("LOCK_ON_RANGE", 0x11c, FireModeColumnKind.Float),
        new("LOCK_ON_ACQUIRE_TIME_MS", 0x120, FireModeColumnKind.Int),
        new("LOCK_ON_LOSE_TIME_MS", 0x124, FireModeColumnKind.Int),
        new("DEFAULT_ZOOM", 0x128, FireModeColumnKind.Float),
        new("FIRST_PERSON_OFFSET_X", 0x12c, FireModeColumnKind.Float),
        new("FIRST_PERSON_OFFSET_Y", 0x130, FireModeColumnKind.Float),
        new("FIRST_PERSON_OFFSET_Z", 0x134, FireModeColumnKind.Float),
        new("RETICLE_ID", 0x138, FireModeColumnKind.Int),
        new("RETICLE_ID_SECONDARY", 0x13c, FireModeColumnKind.Int),
        new("FULL_SCREEN_EFFECT", 0x140, FireModeColumnKind.Int),
        new("HEAT_PER_SHOT", 0x144, FireModeColumnKind.Int),
        new("HEAT_THRESHOLD", 0x148, FireModeColumnKind.Int),
        new("HEAT_RECOVERY_DELAY_MS", 0x14c, FireModeColumnKind.Int),
        new("SWAY_AMPLITUDE_X", 0x150, FireModeColumnKind.Float),
        new("SWAY_AMPLITUDE_Y", 0x154, FireModeColumnKind.Float),
        new("SWAY_PERIOD_X", 0x158, FireModeColumnKind.Float),
        new("SWAY_PERIOD_Y", 0x15c, FireModeColumnKind.Float),
        new("SWAY_INITIAL_Y_OFFSET", 0x160, FireModeColumnKind.Float),
        new("SWAY_CROUCH_SCALAR", 0x164, FireModeColumnKind.Float),
        new("SWAY_PRONE_SCALAR", 0x168, FireModeColumnKind.Float),
        new("ARMS_FOV_SCALAR", 0x16c, FireModeColumnKind.Float),
        new("ANIM_KICK_MAGNITUDE", 0x170, FireModeColumnKind.Float),
        new("ANIM_RECOIL_MAGNITUDE", 0x174, FireModeColumnKind.Float),
        new("DESCRIPTION_ID", 0x178, FireModeColumnKind.Int),
        new("INDIRECT_EFFECT", 0x17c, FireModeColumnKind.Int),
        new("BULLET_ARC_KICK_ANGLE", 0x180, FireModeColumnKind.Float),
        new("PROJECTILE_SPEED_OVERRIDE", 0x184, FireModeColumnKind.Float),
        new("INHERIT_FROM_ID", 0x188, FireModeColumnKind.Int),
        new("INHERIT_FROM_CHARGE_POWER", 0x18c, FireModeColumnKind.Float),
        new("MELEE_COMPOSITE_EFFECT_ID", 0x190, FireModeColumnKind.Int),
        new("MELEE_ABILITY_ID", 0x194, FireModeColumnKind.Int),
        new("LAUNCH_PITCH_ADDITIVE_DEGREES", 0x198, FireModeColumnKind.Float),
        new("TP_FORCE_CAMERA_OVERRIDES", 0x19c, FireModeColumnKind.Byte),
        new("TP_CAMERA_DISTANCE", 0x1a0, FireModeColumnKind.Float),
        new("TP_CAMERA_INDOOR_DISTANCE", 0x1a4, FireModeColumnKind.Float),
        new("TP_CAMERA_MOVE_DISTANCE", 0x1a8, FireModeColumnKind.Float),
        new("TP_CAMERA_MOVING_INDOOR_DISTANCE", 0x1ac, FireModeColumnKind.Float),
        new("TP_CAMERA_LOOK_OFFSET_X", 0x1b0, FireModeColumnKind.Float),
        new("TP_CAMERA_LOOK_OFFSET_Y", 0x1b4, FireModeColumnKind.Float),
        new("TP_CAMERA_LOOK_OFFSET_Z", 0x1b8, FireModeColumnKind.Float),
        new("TP_CAMERA_POSITION_OFFSET_X", 0x1bc, FireModeColumnKind.Float),
        new("TP_CAMERA_POSITION_OFFSET_Y", 0x1c0, FireModeColumnKind.Float),
        new("TP_CAMERA_POSITION_OFFSET_Z", 0x1c4, FireModeColumnKind.Float),
        new("TP_CAMERA_FOV", 0x1c8, FireModeColumnKind.Float),
        new("TP_CAMERA_POS_OFFSET_Y_MOV", 0x1cc, FireModeColumnKind.Float),
        new("TP_CAMERA_LOOK_OFFSET_Y_MOV", 0x1d0, FireModeColumnKind.Float),
        new("TP_CAMERA_MOVE_SPEED", 0x1d4, FireModeColumnKind.Float),
        new("TP_CAMERA_RETURN_SPEED", 0x1d8, FireModeColumnKind.Float),
        new("TP_EXTRA_LEAD_FROM_PITCH_A", 0x1dc, FireModeColumnKind.Float),
        new("TP_EXTRA_LEAD_FROM_PITCH_B", 0x1e0, FireModeColumnKind.Float),
        new("TP_EXTRA_LEAD_PITCH_A", 0x1e4, FireModeColumnKind.Float),
        new("TP_EXTRA_LEAD_PITCH_B", 0x1e8, FireModeColumnKind.Float),
        new("TP_EXTRA_HEIGHT_FROM_PITCH_A", 0x1ec, FireModeColumnKind.Float),
        new("TP_EXTRA_HEIGHT_FROM_PITCH_B", 0x1f0, FireModeColumnKind.Float),
        new("TP_EXTRA_HEIGHT_PITCH_A", 0x1f4, FireModeColumnKind.Float),
        new("TP_EXTRA_HEIGHT_PITCH_B", 0x1f8, FireModeColumnKind.Float),
        new("TP_EXTRA_DRAW_FROM_PITCH_A", 0x1fc, FireModeColumnKind.Float),
        new("TP_EXTRA_DRAW_FROM_PITCH_B", 0x200, FireModeColumnKind.Float),
        new("TP_EXTRA_DRAW_PITCH_A", 0x204, FireModeColumnKind.Float),
        new("TP_EXTRA_DRAW_PITCH_B", 0x208, FireModeColumnKind.Float),
        new("TP_CR_CAMERA_DISTANCE", 0x20c, FireModeColumnKind.Float),
        new("TP_CR_CAMERA_INDOOR_DISTANCE", 0x210, FireModeColumnKind.Float),
        new("TP_CR_CAMERA_MOVE_DISTANCE", 0x214, FireModeColumnKind.Float),
        new("TP_CR_CAMERA_MOVING_INDOOR_DISTANCE", 0x218, FireModeColumnKind.Float),
        new("TP_CR_CAMERA_LOOK_OFFSET_X", 0x21c, FireModeColumnKind.Float),
        new("TP_CR_CAMERA_LOOK_OFFSET_Y", 0x220, FireModeColumnKind.Float),
        new("TP_CR_CAMERA_LOOK_OFFSET_Z", 0x224, FireModeColumnKind.Float),
        new("TP_CR_CAMERA_POSITION_OFFSET_X", 0x228, FireModeColumnKind.Float),
        new("TP_CR_CAMERA_POSITION_OFFSET_Y", 0x22c, FireModeColumnKind.Float),
        new("TP_CR_CAMERA_POSITION_OFFSET_Z", 0x230, FireModeColumnKind.Float),
        new("TP_CR_CAMERA_FOV", 0x234, FireModeColumnKind.Float),
        new("TP_CR_CAMERA_POS_OFFSET_Y_MOV", 0x238, FireModeColumnKind.Float),
        new("TP_CR_CAMERA_LOOK_OFFSET_Y_MOV", 0x23c, FireModeColumnKind.Float),
        new("TP_CR_CAMERA_MOVE_SPEED", 0x240, FireModeColumnKind.Float),
        new("TP_CR_CAMERA_RETURN_SPEED", 0x244, FireModeColumnKind.Float),
        new("TP_CR_EXTRA_LEAD_FROM_PITCH_A", 0x248, FireModeColumnKind.Float),
        new("TP_CR_EXTRA_LEAD_FROM_PITCH_B", 0x24c, FireModeColumnKind.Float),
        new("TP_CR_EXTRA_LEAD_PITCH_A", 0x250, FireModeColumnKind.Float),
        new("TP_CR_EXTRA_LEAD_PITCH_B", 0x254, FireModeColumnKind.Float),
        new("TP_CR_EXTRA_HEIGHT_FROM_PITCH_A", 0x258, FireModeColumnKind.Float),
        new("TP_CR_EXTRA_HEIGHT_FROM_PITCH_B", 0x25c, FireModeColumnKind.Float),
        new("TP_CR_EXTRA_HEIGHT_PITCH_A", 0x260, FireModeColumnKind.Float),
        new("TP_CR_EXTRA_HEIGHT_PITCH_B", 0x264, FireModeColumnKind.Float),
        new("TP_CR_EXTRA_DRAW_FROM_PITCH_A", 0x268, FireModeColumnKind.Float),
        new("TP_CR_EXTRA_DRAW_FROM_PITCH_B", 0x26c, FireModeColumnKind.Float),
        new("TP_CR_EXTRA_DRAW_PITCH_A", 0x270, FireModeColumnKind.Float),
        new("TP_CR_EXTRA_DRAW_PITCH_B", 0x274, FireModeColumnKind.Float),
        new("TP_PR_CAMERA_DISTANCE", 0x278, FireModeColumnKind.Float),
        new("TP_PR_CAMERA_INDOOR_DISTANCE", 0x27c, FireModeColumnKind.Float),
        new("TP_PR_CAMERA_MOVE_DISTANCE", 0x280, FireModeColumnKind.Float),
        new("TP_PR_CAMERA_MOVING_INDOOR_DISTANCE", 0x284, FireModeColumnKind.Float),
        new("TP_PR_CAMERA_LOOK_OFFSET_X", 0x288, FireModeColumnKind.Float),
        new("TP_PR_CAMERA_LOOK_OFFSET_Y", 0x28c, FireModeColumnKind.Float),
        new("TP_PR_CAMERA_LOOK_OFFSET_Z", 0x290, FireModeColumnKind.Float),
        new("TP_PR_CAMERA_POSITION_OFFSET_X", 0x294, FireModeColumnKind.Float),
        new("TP_PR_CAMERA_POSITION_OFFSET_Y", 0x298, FireModeColumnKind.Float),
        new("TP_PR_CAMERA_POSITION_OFFSET_Z", 0x29c, FireModeColumnKind.Float),
        new("TP_PR_CAMERA_FOV", 0x2a0, FireModeColumnKind.Float),
        new("TP_PR_CAMERA_MOVE_SPEED", 0x2a4, FireModeColumnKind.Float),
        new("TP_PR_CAMERA_RETURN_SPEED", 0x2a8, FireModeColumnKind.Float),
        new("TP_PR_EXTRA_LEAD_FROM_PITCH_A", 0x2ac, FireModeColumnKind.Float),
        new("TP_PR_EXTRA_LEAD_FROM_PITCH_B", 0x2b0, FireModeColumnKind.Float),
        new("TP_PR_EXTRA_LEAD_PITCH_A", 0x2b4, FireModeColumnKind.Float),
        new("TP_PR_EXTRA_LEAD_PITCH_B", 0x2b8, FireModeColumnKind.Float),
        new("TP_PR_EXTRA_HEIGHT_FROM_PITCH_A", 0x2bc, FireModeColumnKind.Float),
        new("TP_PR_EXTRA_HEIGHT_FROM_PITCH_B", 0x2c0, FireModeColumnKind.Float),
        new("TP_PR_EXTRA_HEIGHT_PITCH_A", 0x2c4, FireModeColumnKind.Float),
        new("TP_PR_EXTRA_HEIGHT_PITCH_B", 0x2c8, FireModeColumnKind.Float),
        new("TP_ALLOW_MOVE_HEIGHTS", 0x2cc, FireModeColumnKind.Byte),
        new("FP_FORCE_CAMERA_OVERRIDES", 0x2cd, FireModeColumnKind.Byte),
        new("FP_CAMERA_FOV", 0x2d0, FireModeColumnKind.Float),
        new("FP_CR_CAMERA_FOV", 0x2d4, FireModeColumnKind.Float),
        new("FP_PR_CAMERA_FOV", 0x2d8, FireModeColumnKind.Float),
        new("FORCE_FP_SCOPE", 0x2dc, FireModeColumnKind.Byte),
        new("ALLOW_DEPTH_ADJUSTMENT", 0x2dd, FireModeColumnKind.Byte),
        new("AIM_ASSIST_CONFIG", 0x2e0, FireModeColumnKind.Int),
        new("RETICLE_MAX_DISTANCE", 0x2e4, FireModeColumnKind.Float),
        new("TP_CAMERA_INDOOR_OUTDOOR_SPEED", 0x2e8, FireModeColumnKind.Float),
    ];

    // ---------------------------------------------------------------- lists 3-7

    /// <summary>
    /// <b>[P] List 3 - the per-element body <c>FUN_140a41f00</c>: one four-byte word at
    /// <c>elem+0x18</c>, ONE BYTE at <c>elem+0x20</c>
    /// (<c>*(uint*)(param_1+0x20) = (uint)**(byte**)(param_2+0x10)</c>, cursor += 1), then sixteen
    /// four-byte words at <c>elem+0x24 .. +0x60</c>: <b>69 bytes</b>.</b> The element also carries a
    /// four-byte key read by <c>FUN_140a50040</c> before it, so an element is <b>73</b> bytes and a
    /// list-3 record is <c>4 (id) + 4 + 4 (n) + 73n</c>. The byte is the 1087 element's
    /// <c>FLAGS</c>. Until 2026-09-04 this constant was 68 ("seventeen words") and the first ever
    /// populated list 3 killed the client at the zoning table send: the cursor ran six bytes ahead
    /// after the first record, read a bogus count, tripped the cursor error, and
    /// <c>FUN_140b055c0</c>'s failure sink stored to address 0 (docs/123 §7,
    /// <c>out\ghidra-aug\w19-list3</c>, <c>w19-verify\prove_fix.py</c>).
    /// <para>
    /// <b>Role [I]: <c>ConeOfFire</c>.</b> The client's own datasheet schema names 18
    /// <c>ConeOfFire</c> columns (string run <c>0x35e9398</c>: <c>ROTATION</c>,
    /// <c>RADIAL_SCALAR</c>, <c>MIN_CONE_OF_FIRE</c> ... <c>CYLOF_GROW_RATE</c>), and a keyed
    /// element of 18 four-byte fields is the only shape in the blob that matches. Cranberry ships
    /// the list EMPTY - docs/99 section 3.
    /// </para>
    /// </summary>
    public const int List3ElementBodyLength = 69;

    /// <summary><c>4</c> key plus <see cref="List3ElementBodyLength"/>.</summary>
    public const int List3ElementLength = 4 + List3ElementBodyLength;

    /// <summary>
    /// <b>[P] List 4 - body <c>FUN_140a39ca0</c>: three four-byte words</b>, after the reader's own
    /// <c>i32</c> key (<c>FUN_140a4d5d0</c> to <c>rec+0x20</c>). A record is <b>16 bytes</b> flat,
    /// with no nested list at all. docs/58 section 4b's "reads two <c>i32</c>s (nested list)" is
    /// corrected by this decode.
    /// <para>
    /// <b>Role, wave 14 [P]: <c>FireModeProjectileMapping</c>.</b> docs/99 section 3.2's "Role [U]"
    /// is CLOSED. The record is
    /// <c>(fireModeDefinitionId, ammoItemId) -&gt; projectileDefinitionId</c>: the hash key at
    /// <c>rec+0x20</c> is the fire mode's own definition id (<c>fireModeRec+0x18</c>),
    /// <c>rec+0x04</c> is <c>AmmoSlot.AmmoId</c> and <c>rec+0x08</c> is the id of a
    /// <c>ReferenceData "ProjectileDefinitions"</c> record. <c>rec+0x00</c> is read by nothing.
    /// See <c>WeaponBlobList4Record</c> for the three dumps that prove it.
    /// </para>
    /// </summary>
    public const int List4RecordLength = 16;

    /// <summary>
    /// <b>[P] List 5 - body <c>FUN_140a2c1b0</c>: twenty-three four-byte words</b> at
    /// <c>rec+0x20 .. +0x78</c>, after the reader's key (<c>FUN_140a4e570</c> to <c>rec+0x90</c>).
    /// A record is <b>96 bytes</b>. <b>Role [I]: <c>AimAssist</c></b> - the client's schema run at
    /// <c>0x35e9098</c> names 21 <c>AimAssist</c> columns (<c>CONE_ANGLE</c> ...
    /// <c>MIN_INPUT_STRAFE_ARRIVE_TIME</c>), the closest of the unattributed runs; the two-column
    /// excess is [U]. Cranberry ships the list EMPTY.
    /// </summary>
    public const int List5RecordLength = 4 + (23 * 4);

    /// <summary>
    /// <b>[P] List 6 - <c>FUN_140a4fa40</c>: <c>u32 id</c> (to <c>rec+0x60</c>), <c>u32</c>
    /// (to <c>rec+0x18</c>), then <c>FUN_140a4fbf0</c>'s nested list</b>: <c>i32 n</c> and <c>n</c>
    /// elements of <c>u32 key</c> (to <c>sub+0x38</c>) plus <c>FUN_140a3f970</c>'s three words
    /// (<c>sub+0x18</c>, <c>+0x20</c>, <c>+0x24</c>). An element is <b>16 bytes</b>; a record is
    /// <c>12 + 16n</c>. Role [U].
    /// </summary>
    public const int List6ElementLength = 16;

    /// <summary>
    /// <b>[P] List 7 - <c>FUN_140a4fd40</c>: <c>u32 id</c> (to <c>rec+0x78</c>), <c>u32</c>
    /// (to <c>rec+0x00</c>), then <c>FUN_140a4cbf0</c>'s list</b>: <c>i32 n</c> and <c>n</c> bare
    /// <c>u32</c>s, each linked into a singly-linked chain. A record is <c>12 + 4n</c>. Role [U] -
    /// the lookup <c>FUN_14147f370</c> exists but no consumer of it is in any dump.
    /// </summary>
    public const int List7ElementLength = 4;
}
