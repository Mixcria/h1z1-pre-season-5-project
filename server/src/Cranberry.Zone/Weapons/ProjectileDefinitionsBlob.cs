using Cranberry.Protocol;

namespace Cranberry.Zone.Weapons;

/// <summary>
/// One <c>ProjectileDefinitions</c> record, as the August client reads it.
/// <para>
/// <b>Layout [P, 2026-09-02 overhaul lane 2D].</b> The list reader <c>FUN_140a4fef0</c> takes a
/// <c>u32</c> id into <c>rec+0x158</c> (the hash key) and the body reader <c>FUN_140a40410</c>
/// reads, in this exact order:
/// </para>
/// <code>
/// u32   -&gt; rec+0x018     the record's own id (mirrors the key, as list 0's def+0x18 does)
/// u8[2] -&gt; rec+0x020     the bit-packed booleans (11 client columns, LOS_FLAG .. DO_NOT_HOLD_ACTOR)
/// str   -&gt; rec+0x028     SoeUtil::StringFixed&lt;32&gt; (u32 length + bytes)
/// str   -&gt; rec+0x040     SoeUtil::StringFixed&lt;32&gt;
/// u32 x4  -&gt; +0x58 +0x5c +0x60 +0x64
/// u8      -&gt; +0x68      *** ONE BYTE, zero-extended - FLIGHT_TYPE (docs/107 3) ***
/// u32 x6  -&gt; +0x6c +0x70 +0x74 +0x78 +0x7c +0x80
/// str   -&gt; rec+0x088     SoeUtil::StringFixed&lt;32&gt;
/// u32 x13 -&gt; +0xa0 .. +0xd0
/// i8    -&gt; rec+0x0d4
/// u32 x7  -&gt; +0xd8 .. +0xf0
/// i32 n; u32[n] -&gt; rec+0x120   (FUN_140a4caf0)
/// u32 x9  -&gt; +0xf4 +0xf8 +0x104 +0x108 +0x10c +0x110 +0x114 +0x118 +0x11c
/// </code>
/// <para>
/// With three empty strings and an empty array that is <b>180 body bytes</b> and
/// <see cref="MinimalLength"/> = 184 with the key.
/// </para>
/// <para>
/// <b>WAVE 13 CORRECTION - the run between the second and third strings is ELEVEN fields, not ten,
/// and the fifth of them is one byte (docs/107 3, D199).</b> <c>FUN_140a40410:66-76</c> reads a
/// single <c>byte</c>, zero-extends it into <c>rec+0x68</c> and advances the cursor by 1; every
/// other field in that run is a <c>u32</c>. Waves 9-12 wrote ten <c>u32</c>s (40 bytes) where the
/// client reads 41, so <b>every field from <c>rec+0x88</c> onward was one byte out of phase</b> -
/// the third string's length would have been read across a boundary and the record's whole tail
/// misaligned. The table has never been on a wire (the switch defaults off), so this corrected
/// nothing that shipped; it is exactly why it must not be turned on before this fix.
/// </para>
/// <para>
/// <b>Column mapping [P] for every field the spawn path reads (docs/107 3).</b> The map did not
/// come from the stat-getter pattern - there is no <c>Projectile.*</c> key in the stat-name pool at
/// all; projectile columns are read straight off the record by the spawn. The bindings that matter
/// come from <c>FUN_142224ef0</c> (the definition-to-spawn-params copier), <c>FUN_140c7bb60</c> /
/// <c>FUN_140c7b280</c> (the local and remote spawns) and <c>FUN_140f09e30</c>
/// (<c>SpawnProjectile</c>): <b><c>+0x5c</c> SPEED, <c>+0x68</c> FLIGHT_TYPE (u8), <c>+0x78</c>
/// LIFESPAN, <c>+0x7c</c> MAX_SPEED, <c>+0x80</c> TURN_RATE, <c>+0x6c</c> PROJECTILE_EFFECT_ID,
/// <c>+0xec</c> SCALE</b>, the six angular-velocity words at <c>+0x104..+0x118</c>, and the tracer
/// block <c>+0xac..+0xbc</c>. <c>GRAVITY</c> (<c>+0xa8</c>) and <c>DRAG</c> (<c>+0xa0</c>) stay
/// <b>[I]</b> - placed only by the constructor's two non-unit presets (<c>10.0f</c>,
/// <c>0.005f</c>) and by elimination, because <b>no decompiled function anywhere reads them</b>.
/// They gate nothing on the spawn path, so they are not what blocks the table;
/// <see cref="WeaponStageOptions.SendProjectileDefinitions"/> says what does.
/// </para>
/// </summary>
/// <param name="ProjectileId">
/// <c>rec+0x158</c> (the hash key) and, unless <paramref name="WriteBodyId"/> is false,
/// <c>rec+0x18</c> too. The ids Cranberry ships are the client's own, from
/// <c>ProjectileToPenTypes.txt</c>.
/// </param>
/// <param name="ModelFileName">
/// <c>rec+0x28</c> [P offset] <c>MODEL_FILE_NAME</c>. <b>Empty by default, and that is only safe
/// for the LOCAL player's own shot:</b> <c>FUN_140c7bb60:703-704</c> substitutes the client's own
/// <c>"InvisibleTriangle.adr"</c> when the name is empty, but the remote path has no such fallback
/// and <c>FUN_140f09e30:33-38</c> returns <b>no projectile at all</b> for another player's shot
/// when this string is empty AND <see cref="ProjectileEffectId"/> is 0. The August client ships no
/// sheet naming a per-projectile model, so filling it would be an invented value (docs/107 3).
/// </param>
/// <param name="AttachmentOverride">
/// <c>rec+0x40</c> [I]. <b>The label is probably backwards</b>: <c>FUN_140c7bb60:699-700</c> copies
/// this string OVER the model name when the local-player and first-person flags are both set, which
/// reads as <c>FP_MODEL_FILE_NAME</c> and leaves <c>+0x88</c> as the attachment override. Both are
/// empty, so nothing turns on it today. Empty by default.
/// </param>
/// <param name="FirstPersonModelFileName"><c>rec+0x88</c> [I]. See <paramref name="AttachmentOverride"/>. Empty by default.</param>
public sealed record ProjectileDefinitionRecord(
    uint ProjectileId,
    string ModelFileName = "",
    string AttachmentOverride = "",
    string FirstPersonModelFileName = "")
{
    /// <summary>
    /// <b>[P] <c>rec+0x5c</c> - <c>SPEED</c>, the muzzle velocity.</b> <c>FUN_142224ef0:8</c> copies
    /// it to <c>params+0x6c</c>, which <c>FUN_140c7bb60:390-413</c> multiplies the normalised aim
    /// vector by to make the projectile's velocity; independently <c>FUN_140e74b30:170-172</c>
    /// computes the weapon's range as <c>rec+0x5c * rec+0x78</c>. Zero is a projectile that never
    /// leaves the muzzle.
    /// </summary>
    public float Speed { get; init; }

    /// <summary>
    /// <b>[P] <c>rec+0x68</c> - <c>FLIGHT_TYPE</c>, ONE BYTE, and the single most load-bearing field
    /// in the record.</b> <c>FUN_140f09e30:116-134</c> switches on it to choose the flight model:
    /// <c>1</c> -&gt; <c>FUN_1415aff80</c> (24-byte ballistic), <c>9</c> -&gt; <c>FUN_1415b00b0</c>
    /// (336-byte physics, <c>ProjectileCallbackPhysics</c> - the grenade arc), <c>10</c> -&gt;
    /// <c>FUN_1415affd0</c>. <b>Any other value, 0 included, leaves the flight-model pointer null
    /// and installs no flight model at all</b> - a projectile object that exists and does not move.
    /// </summary>
    public byte FlightType { get; init; }

    /// <summary>
    /// <b>[P] <c>rec+0x78</c> - <c>LIFESPAN</c>, in seconds.</b> Copied to <c>params+0xec</c> and on
    /// to <c>actor+0x47c</c>; <c>FUN_140ef15d0:1470-1471</c> expires the projectile when the elapsed
    /// time passes it, and <c>:1441</c> takes an entirely different, non-flying path when it is
    /// <c>&lt;= 0</c>. <c>FUN_140f09e30:66</c> also requires <c>LIFESPAN &gt; 0</c> (or a
    /// <see cref="FlightType"/> other than 1) before it will build the visual model actor at all.
    /// <b>Must be &gt; 0.</b>
    /// </summary>
    public float Lifespan { get; init; }

    /// <summary>
    /// <b>[P] <c>rec+0x6c</c> - <c>PROJECTILE_EFFECT_ID</c>.</b> <c>FUN_140f052a0:50</c> spawns the
    /// effect when it is non-zero; together with <see cref="ModelFileName"/> it is the pair
    /// <c>FUN_140f09e30:33-38</c> tests before refusing to create a remote projectile at all. No
    /// August sheet names one, so it ships 0.
    /// </summary>
    public uint ProjectileEffectId { get; init; }

    /// <summary>
    /// <b>[I] <c>rec+0xa0</c> - <c>DRAG</c>.</b> The client's own constructor preset
    /// (<c>FUN_142224d50:24</c>, <c>0x3ba3d70a</c> = <c>0.005f</c>). <b>Nothing in any dump reads
    /// it</b>, so its identity rests on being one of only two non-unit presets in the record; the
    /// ballistic integrator that would consume it was not found (docs/107 3, still open).
    /// </summary>
    public float Drag { get; init; } = 0.005f;

    /// <summary>
    /// <b>[I] <c>rec+0xa8</c> - an UNNAMED scalar.</b> The client's own constructor preset
    /// (<c>0x41200000</c> = <c>10.0f</c>). <b>docs/120 §3.2 correction:</b> this word was carried
    /// as <c>GRAVITY</c> from wave 9 to wave 17, but the client's own <c>ProjectileDefinitions</c>
    /// row loader (<c>0x142225280..0x142225ee2</c>) stores no column at <c>+0xa8</c> at all - its
    /// <c>GRAVITY</c> cell goes to <c>+0xa4</c> (<see cref="GravityScale"/>). The preset ships
    /// unchanged, so every bullet record is byte-identical to before.
    /// </summary>
    public float Gravity { get; init; } = 10.0f;

    /// <summary>
    /// <b>[P] <c>rec+0xa4</c> - <c>GRAVITY</c></b>, the loader's own binding (<c>MOVSS [RDI+0xa4]</c>
    /// at <c>142225730</c>, column string <c>0x1435e8...</c>, docs/120 §3.2) and the constructor's
    /// preset <c>1.0f</c>. Copied to <c>params+0x124</c> by <c>FUN_142224ef0</c>. A MULTIPLIER on
    /// the flight model's gravity, not an acceleration - which is why the client's default is one.
    /// </summary>
    public float GravityScale { get; init; } = 1.0f;

    /// <summary>
    /// <b>[P] <c>rec+0x20</c> bit <c>0x40</c> - <c>LIFESPAN_DETONATE</c></b> (loader
    /// <c>142225312</c>). Copied to <c>params+0x1f8</c> bit <c>0x10</c> = <c>actor+0x588</c>, which
    /// the actor tick tests when the lifespan runs out: set, it calls <c>FUN_140ef51b0</c> and
    /// the client SENDS <c>82 19 GuidedExplode</c> with the detonation position
    /// (<c>FUN_140ef15d0:1470-1486</c>, docs/120 §2.5). This is the grenade fuse.
    /// </summary>
    public bool DetonatesOnLifespan { get; init; }

    /// <summary>
    /// <b>[P] <c>rec+0x20</c> bit <c>0x04</c> - <c>DETONATE_ON_CONTACT</c></b> (loader
    /// <c>1422253fe</c>), copied to <c>params+0x1f9</c> bit <c>0x04</c>. The molotov.
    /// </summary>
    public bool DetonatesOnContact { get; init; }

    /// <summary>
    /// <b>[P] <c>rec+0xc8</c> - <c>DAMAGE_RADIUS</c></b> (loader <c>14222581f</c>, copied to
    /// <c>params+0x16c</c>). Declared to the client for completeness; the server applies its own
    /// radius (<c>AugustThrowables</c>), because damage is server-authoritative.
    /// </summary>
    public float DamageRadius { get; init; }

    /// <summary>
    /// <b>[P] <c>rec+0x104..+0x118</c> - <c>ANGULAR_VELOCITY_{X,Y,Z}_{MIN,MAX}</c></b>, six floats
    /// the physics flight model lerps between at spawn (<c>FUN_1415b17a0:42-55</c>). One (min, max)
    /// pair is written to all three axes: a grenade tumbles the same way about every axis.
    /// </summary>
    public float AngularVelocityMin { get; init; }

    /// <inheritdoc cref="AngularVelocityMin"/>
    public float AngularVelocityMax { get; init; }

    /// <summary>The <c>rec+0x20</c> flag byte, built from the two named bits above. [P bits]</summary>
    public byte Flags0 =>
        (byte)((DetonatesOnLifespan ? LifespanDetonateFlag : 0)
            | (DetonatesOnContact ? DetonateOnContactFlag : 0));

    /// <summary><c>rec+0x20</c> bit <c>0x40</c>, <c>LIFESPAN_DETONATE</c> (loader <c>142225312</c>).</summary>
    public const byte LifespanDetonateFlag = 0x40;

    /// <summary><c>rec+0x20</c> bit <c>0x04</c>, <c>DETONATE_ON_CONTACT</c> (loader <c>1422253fe</c>).</summary>
    public const byte DetonateOnContactFlag = 0x04;
    /// <summary>
    /// True (the default) writes <see cref="ProjectileId"/> at <c>rec+0x18</c> as well as in the
    /// key, which is what list 0 does (<c>WeaponDefinitionRecord.WriteBodyId</c>) and what the
    /// 2026-09-02 refute-1 finding says a record's own id field is for.
    /// </summary>
    public bool WriteBodyId { get; init; } = true;

    /// <summary>
    /// <b>[P] <c>rec+0xa0</c> = <c>0.005f</c></b> - the client's own record constructor
    /// <c>FUN_142224d50</c> presets <c>0x3ba3d70a</c> here. One of only four non-zero presets in
    /// the whole record.
    /// </summary>
    public const short DefaultSmallScalarOffset = 0x0a0;

    /// <summary><b>[P] <c>rec+0xa4</c> = <c>1.0f</c></b> (<c>0x3f800000</c>) - a scalar, D143.</summary>
    public const short UnitScalarOffsetA = 0x0a4;

    /// <summary><b>[P] <c>rec+0xa8</c> = <c>10.0f</c></b> (<c>0x41200000</c>).</summary>
    public const short DefaultTenScalarOffset = 0x0a8;

    /// <summary><b>[P] <c>rec+0xec</c> = <c>1.0f</c></b> - a scalar, D143.</summary>
    public const short UnitScalarOffsetB = 0x0ec;

    /// <summary>
    /// Bytes with three empty strings and an empty <c>rec+0x120</c> array: <b>184</b> - the 4-byte
    /// key plus a 180-byte body. It was 183 until wave 13 found the one-byte <c>FLIGHT_TYPE</c>
    /// read the writer was missing (see the correction on this type).
    /// </summary>
    public const int MinimalLength = 184;

    /// <summary>
    /// <b>D314 (docs/123 section 6): the 43 non-string fields of a Z1 <c>projectileDefinitions.json</c>
    /// row</b>, in <see cref="Z1ProjectileFacts.Columns"/> order - which is field for field the
    /// order this record already writes - each encoded for its own wire kind. Null (the default,
    /// and every August record) leaves the writer on its named parameters, byte for byte.
    /// <para>
    /// The 1087 row has no <c>FADE_OUT_MS</c>; 1148 reads one more word at <c>rec+0x11c</c>, so the
    /// raw branch writes the client's own zero there. That one word is the whole of the projectile
    /// record's 1087 -> 1148 difference.
    /// </para>
    /// </summary>
    public IReadOnlyList<uint>? RawWords { get; init; }

    /// <summary>
    /// The <c>rec+0x120</c> counted array (<c>PLAYER_BULLET_RADIUS_LIST</c>). Empty on every August
    /// record; a Z1 row carries the radii its own server sends.
    /// </summary>
    public IReadOnlyList<uint>? BulletRadii { get; init; }

    /// <summary>How many words <see cref="RawWords"/> must hold: <b>43</b>.</summary>
    public const int RawWordCount = 43;

    /// <summary><see cref="MinimalLength"/> plus each string's own bytes and the radius array.</summary>
    public int Length =>
        MinimalLength
        + System.Text.Encoding.UTF8.GetByteCount(ModelFileName)
        + System.Text.Encoding.UTF8.GetByteCount(AttachmentOverride)
        + System.Text.Encoding.UTF8.GetByteCount(FirstPersonModelFileName)
        + (4 * (BulletRadii?.Count ?? 0));

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (RawWords is not null)
        {
            WriteRawTo(writer);
            return;
        }

        writer.WriteUInt32(ProjectileId);                             // FUN_140a4fef0 -> rec+0x158 (key)
        writer.WriteUInt32(WriteBodyId ? ProjectileId : 0);           // -> rec+0x18
        writer.WriteByte(Flags0);                                     // -> rec+0x20  LOS_FLAG 80, LIFESPAN_DETONATE 40, CLICK_DETONATE 20, STICKY 10, STICKS_TO_PLAYERS 08, DETONATE_ON_CONTACT 04, SPAWN_AT_LOCK_ON_LOCATION 02, HIGH_PRIORITY 01 [P, docs/120 s3.2]
        writer.WriteByte(0);                                          // -> rec+0x21  PREDICTIVE_TRACKING 80, CREATE_NETWORK_OBJECT 40, DO_NOT_HOLD_ACTOR 20 [P]
        writer.WriteString(ModelFileName);                            // -> rec+0x28
        writer.WriteString(AttachmentOverride);                       // -> rec+0x40

        // ELEVEN fields, and the fifth is ONE BYTE (FUN_140a40410:66-76). Waves 9-12 wrote ten
        // u32s here and every field past rec+0x88 was a byte out of phase - docs/107 3, D199.
        writer.WriteUInt32(0);                                        // -> +0x58  an asset handle [U]
        writer.WriteSingle(Speed);                                    // -> +0x5c  SPEED            [P]
        writer.WriteUInt32(0);                                        // -> +0x60  VELOCITY_INHERIT_SCALER
        writer.WriteUInt32(0);                                        // -> +0x64  ACCELERATION
        writer.WriteByte(FlightType);                                 // -> +0x68  FLIGHT_TYPE, u8  [P]
        writer.WriteUInt32(ProjectileEffectId);                       // -> +0x6c  PROJECTILE_EFFECT_ID
        writer.WriteUInt32(0);                                        // -> +0x70  LAND_EFFECT_ID   [I]
        writer.WriteUInt32(0);                                        // -> +0x74  INDIRECT_DAMAGE_EFFECT_ID [I]
        writer.WriteSingle(Lifespan);                                 // -> +0x78  LIFESPAN         [P]
        writer.WriteUInt32(0);                                        // -> +0x7c  MAX_SPEED (0 => SPEED wins)
        writer.WriteUInt32(0);                                        // -> +0x80  TURN_RATE (>0 makes it a tracker)
        writer.WriteString(FirstPersonModelFileName);                 // -> rec+0x88

        // +0xa0 .. +0xd0: thirteen words, four of which the client's own constructor presets.
        writer.WriteSingle(Drag);          // -> +0xa0  DRAG    [P name, docs/120 s3.2]; FUN_142224d50 0x3ba3d70a = 0.005f
        writer.WriteSingle(GravityScale);  // -> +0xa4  GRAVITY [P name]; FUN_142224d50 0x3f800000 = 1.0f
        writer.WriteSingle(Gravity);       // -> +0xa8  unnamed [I]; FUN_142224d50 0x41200000 = 10.0f
        WriteZeroWords(writer, 7);         // -> +0xac TRACER_FREQUENCY +0xb0 TRACER_EFFECT_ID +0xb4 FP_TRACER_FREQUENCY +0xb8 FP_TRACER_EFFECT_ID +0xbc FP_TRACER_HIDE_RANGE +0xc0 NPC_DEFINITION_ID +0xc4 MAX_COUNT
        writer.WriteSingle(DamageRadius);  // -> +0xc8  DAMAGE_RADIUS [P name]
        WriteZeroWords(writer, 2);         // -> +0xcc DROP_OFF_RADIUS +0xd0 DROP_OFF_MODIFIER

        writer.WriteByte(0);               // -> +0xd4  TRIGGER_DETONATE_REQUIREMENT (i8) [P name]

        WriteZeroWords(writer, 5);         // -> +0xd8 ARM_DISTANCE +0xdc DETONATE_DISTANCE +0xe0 TETHER_DISTANCE +0xe4 LOCKON_ACCELERATION +0xe8 LOCKON_LIFESPAN
        writer.WriteSingle(1.0f);          // -> +0xec  SCALE; FUN_142224d50 0x3f800000  (D143)
        writer.WriteUInt32(0);             // -> +0xf0  LOSE_LOCKON_ANGLE

        IReadOnlyList<uint> radii = BulletRadii ?? [];
        writer.WriteInt32(radii.Count);    // -> +0x120 array (FUN_140a4caf0)
        foreach (uint radius in radii)
        {
            writer.WriteUInt32(radius);
        }

        writer.WriteUInt32(0);             // -> +0xf4  ITEM_PICKUP_ID
        writer.WriteUInt32(0);             // -> +0xf8  BOUNCE_MODEL_ID
        for (int axis = 0; axis < 3; axis++)
        {
            writer.WriteSingle(AngularVelocityMin);   // -> +0x104 / +0x10c / +0x114  ANGULAR_VELOCITY_{X,Y,Z}_MIN [P]
            writer.WriteSingle(AngularVelocityMax);   // -> +0x108 / +0x110 / +0x118  ANGULAR_VELOCITY_{X,Y,Z}_MAX [P]
        }

        writer.WriteUInt32(0);             // -> +0x11c  FADE_OUT_MS
    }

    /// <summary>
    /// The same [P] 1148 record, written straight from <see cref="RawWords"/> - the Z1 row's own
    /// values, in the order <see cref="Z1ProjectileFacts.Columns"/> declares. Every write below is
    /// the same write, at the same offset and the same width, as the named branch above; only the
    /// source of the value changes. The record's LENGTH is identical for identical strings.
    /// </summary>
    private void WriteRawTo(PacketWriter writer)
    {
        IReadOnlyList<uint> w = RawWords!;
        ArgumentOutOfRangeException.ThrowIfNotEqual(w.Count, RawWordCount, nameof(RawWords));
        IReadOnlyList<uint> radii = BulletRadii ?? [];

        writer.WriteUInt32(ProjectileId);        // FUN_140a4fef0 -> rec+0x158 (key)
        writer.WriteUInt32(w[0]);                // -> rec+0x18  ID
        writer.WriteByte((byte)w[1]);            // -> rec+0x20  FLAGS1
        writer.WriteByte((byte)w[2]);            // -> rec+0x21  FLAGS2
        writer.WriteString(ModelFileName);       // -> rec+0x28  MODEL_FILE_NAME
        writer.WriteString(AttachmentOverride);  // -> rec+0x40  FP_MODEL_FILE_NAME
        writer.WriteUInt32(w[3]);                // -> +0x58  AUDIO_GAME_OBJECT
        writer.WriteUInt32(w[4]);                // -> +0x5c  SPEED
        writer.WriteUInt32(w[5]);                // -> +0x60  VELOCITY_INHERIT_SCALER
        writer.WriteUInt32(w[6]);                // -> +0x64  ACCELERATION
        writer.WriteByte((byte)w[7]);            // -> +0x68  FLIGHT_TYPE, one byte
        writer.WriteUInt32(w[8]);                // -> +0x6c  PROJECTILE_EFFECT_ID
        writer.WriteUInt32(w[9]);                // -> +0x70  LAND_EFFECT_ID
        writer.WriteUInt32(w[10]);               // -> +0x74  INDIRECT_DAMAGE_EFFECT_ID
        writer.WriteUInt32(w[11]);               // -> +0x78  LIFESPAN
        writer.WriteUInt32(w[12]);               // -> +0x7c  MAX_SPEED
        writer.WriteUInt32(w[13]);               // -> +0x80  TURN_RATE
        writer.WriteString(FirstPersonModelFileName);   // -> rec+0x88  BONE_ATTACHMENT_OVERRIDE

        for (int i = 14; i <= 26; i++)
        {
            writer.WriteUInt32(w[i]);            // -> +0xa0 DRAG .. +0xd0 DROP_OFF_MODIFIER
        }

        writer.WriteByte((byte)w[27]);           // -> +0xd4  TRIGGER_DETONATE_REQUIREMENT (i8)

        for (int i = 28; i <= 34; i++)
        {
            writer.WriteUInt32(w[i]);            // -> +0xd8 ARM_DISTANCE .. +0xf0 LOSE_LOCKON_ANGLE
        }

        writer.WriteInt32(radii.Count);          // -> +0x120  PLAYER_BULLET_RADIUS_LIST
        foreach (uint radius in radii)
        {
            writer.WriteUInt32(radius);
        }

        for (int i = 35; i <= 42; i++)
        {
            writer.WriteUInt32(w[i]);            // -> +0xf4 ITEM_PICKUP_ID .. +0x118 ANGULAR_VELOCITY_Z_MAX
        }

        writer.WriteUInt32(0);                   // -> +0x11c  FADE_OUT_MS - 1148 only, the client's own 0
    }

    private static void WriteZeroWords(PacketWriter writer, int count)
    {
        for (int i = 0; i < count; i++)
        {
            writer.WriteUInt32(0);
        }
    }
}

/// <summary>
/// The body of <c>ReferenceData "ProjectileDefinitions"</c>.
/// <para>
/// <b>It is NOT the same shape as <c>WeaponDefinitions</c>.</b> <c>FUN_140a20370</c> decompresses
/// the ReferenceData envelope the ordinary way (<c>FUN_14220e860</c>, which is a plain memcpy when
/// <c>uncompressedLength == blobLength</c> - the form Cranberry already sends) and then hands the
/// result to <c>FUN_140a40e40</c>, which reads <b>two more <c>u32</c>s</b> and decompresses
/// <em>again</em>:
/// </para>
/// <code>
/// u32 compressedLength      the LZ4 block's own size
/// u32 uncompressedLength    the size to allocate for the decompressed table
/// u8[compressedLength]      an LZ4 block  (FUN_14212a7b0 -> thunk_FUN_14212b6a0)
///
///   decompressed:  i32 count; ProjectileDefinitionRecord[count]     (FUN_140a4fef0)
/// </code>
/// <para>
/// <b>There is no raw escape hatch.</b> <c>FUN_14220e860</c> has one (equal lengths mean memcpy);
/// <c>FUN_14212a7b0</c> does not - with <c>uncompressedLength &gt; 0</c> it always calls the LZ4
/// decoder. Cranberry therefore emits a real, if deliberately dumb, LZ4 block:
/// <see cref="Lz4LiteralBlock"/> writes the whole table as one literal run, which is a valid block
/// under the LZ4 format (the final sequence may be literals only) and costs about 0.4% overhead.
/// Writing a compressor is not the point of this lane; writing a block the client can decode is.
/// </para>
/// <para>
/// <b>The failure mode is quiet.</b> If the block does not decode, <c>FUN_140a40e40</c> skips the
/// list and returns without touching the OUTER cursor's error byte, so <c>FUN_140b055c0</c>'s
/// <c>_DAT_00000000 = 1</c> store - the access violation that a malformed
/// <c>WeaponDefinitions</c> would cause - cannot be reached from this table.
/// </para>
/// </summary>
public sealed record ProjectileDefinitionsBlob(IReadOnlyList<ProjectileDefinitionRecord>? Projectiles = null)
{
    /// <summary>
    /// The type name <c>FUN_140b055c0</c> dispatches on, hashed with <c>FUN_140981390</c> exactly
    /// as <c>"WeaponDefinitions"</c> is; the branch loads <c>DAT_143f6a010</c> and calls
    /// <c>FUN_140a20370</c>.
    /// </summary>
    public const string TypeName = "ProjectileDefinitions";

    /// <summary>The two lengths in front of the LZ4 block.</summary>
    public const int HeaderLength = 8;

    /// <summary>The decompressed table: <c>i32 count</c> then the records.</summary>
    public byte[] Table()
    {
        IReadOnlyList<ProjectileDefinitionRecord> projectiles = Projectiles ?? [];
        int length = 4;
        foreach (ProjectileDefinitionRecord projectile in projectiles)
        {
            length += projectile.Length;
        }

        using var writer = new PacketWriter(length);
        writer.WriteInt32(projectiles.Count);     // FUN_140a4fef0's count
        foreach (ProjectileDefinitionRecord projectile in projectiles)
        {
            projectile.WriteTo(writer);
        }

        return writer.Written.ToArray();
    }

    /// <summary>The raw blob - what goes inside the <c>ReferenceData</c> envelope's counted bytes.</summary>
    public byte[] ToArray()
    {
        byte[] table = Table();
        byte[] block = Lz4LiteralBlock.Encode(table);

        using var writer = new PacketWriter(HeaderLength + block.Length);
        writer.WriteUInt32((uint)block.Length);   // FUN_140a40e40's first read  -> FUN_14212a7b0 srcLen
        writer.WriteUInt32((uint)table.Length);   // FUN_140a40e40's second read -> the output size
        writer.WriteRaw(block);
        return writer.Written.ToArray();
    }

    /// <summary>Total blob length, including the eight-byte header.</summary>
    public int Length => ToArray().Length;
}

/// <summary>
/// <b>An LZ4 block encoder that never matches anything</b> - it writes its input as one run of
/// literals. That is a well-formed LZ4 block (the format's last sequence carries literals and no
/// match), so any conforming decoder, including the August client's
/// <c>thunk_FUN_14212b6a0</c>, reproduces the input exactly.
/// <para>
/// This exists because <c>ProjectileDefinitions</c> has no uncompressed path
/// (<see cref="ProjectileDefinitionsBlob"/>), and because a table Cranberry can decode in a unit
/// test is worth more here than a compression ratio: the whole point of the lane is that a wrong
/// byte in this blob must be findable. The overhead is one token per 255 bytes.
/// </para>
/// </summary>
public static class Lz4LiteralBlock
{
    /// <summary>Literal lengths of 15 or more are continued in 255-valued bytes.</summary>
    private const int LiteralLengthEscape = 15;

    /// <summary>Encodes <paramref name="data"/> as a single literal-only LZ4 sequence.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length + (data.Length / 255) + 2);
        int literals = data.Length;
        if (literals < LiteralLengthEscape)
        {
            output.Add((byte)(literals << 4));
        }
        else
        {
            output.Add(LiteralLengthEscape << 4);
            int remainder = literals - LiteralLengthEscape;
            while (remainder >= 255)
            {
                output.Add(255);
                remainder -= 255;
            }

            output.Add((byte)remainder);
        }

        output.AddRange(data.ToArray());
        return [.. output];
    }

    /// <summary>
    /// Decodes a block this class produced, so a test can prove the round trip without pulling in a
    /// decompressor. It understands literal-only blocks and nothing else - a block with a match is
    /// rejected rather than half-decoded.
    /// </summary>
    public static byte[] Decode(ReadOnlySpan<byte> block)
    {
        if (block.Length == 0)
        {
            return [];
        }

        int cursor = 0;
        int literals = block[cursor++] >> 4;
        if (literals == LiteralLengthEscape)
        {
            while (true)
            {
                byte more = block[cursor++];
                literals += more;
                if (more != 255)
                {
                    break;
                }
            }
        }

        if (cursor + literals != block.Length)
        {
            throw new InvalidOperationException(
                $"not a literal-only LZ4 block: {literals} literals at {cursor} of {block.Length} bytes");
        }

        return block.Slice(cursor, literals).ToArray();
    }
}

/// <summary>
/// The <c>ProjectileDefinitions</c> table Cranberry would send, and the one place its values are
/// decided.
/// <para>
/// <b>Ids: the client's own.</b> One record per <c>PROJECTILE_ID</c> in the August client's
/// <c>ProjectileToPenTypes.txt</c> (14 rows, joined in <c>out\data_aug\derived\weapons.json</c>
/// and generated into <see cref="AugustProjectileFacts"/>). Nothing here is invented: those are the
/// projectile ids the client's own data names.
/// </para>
/// <para>
/// <b>Values: the client's own record-constructor defaults, and nothing else.</b>
/// <c>FUN_142224d50</c> presets exactly four words (<c>0.005f</c>, <c>1.0f</c>, <c>10.0f</c>,
/// <c>1.0f</c>) and zeroes the rest; <see cref="ProjectileDefinitionRecord"/> writes that.
/// Filling <c>SPEED</c> / <c>GRAVITY</c> / <c>DRAG</c> would need the column-to-offset map, which
/// is [I] (docs/99 section 4.3), and the only complete 55-column value set that exists is a
/// third-party artefact docs/00 forbids. <b>So the table ships OFF by default</b> - it exists so
/// the wire is written, tested and revertible the day the map is closed.
/// </para>
/// </summary>
public static class AugustProjectileTable
{
    /// <summary>
    /// <b><c>FLIGHT_TYPE</c> 1 - the plain ballistic flight model.</b> The client offers exactly
    /// three (<c>FUN_140f09e30:116-134</c>): 1 is the 24-byte model every bullet wants, 9 is the
    /// 336-byte <c>ProjectileCallbackPhysics</c> a grenade bounces on, 10 is a third. A CRANBERRY
    /// RULING, not a client value - no August sheet names a flight type - but the alternatives are
    /// enumerable and only one of them is a bullet.
    /// </summary>
    public const byte BallisticFlightType = 1;

    /// <summary>
    /// <b>The distance a projectile is allowed to cover before it expires</b>, in the same world
    /// units <c>CombatOptions.MaxHitDistance</c> is measured in. <see cref="LifespanFor"/> turns it
    /// into <c>LIFESPAN</c> seconds by dividing by the muzzle speed.
    /// <para>
    /// <b>A Cranberry design value with one virtue: it is not invented.</b> 350 is this server's own
    /// hit-registration limit, so the rule is "the projectile dies exactly where this server stops
    /// accepting hits from it" - the two numbers cannot drift apart. The August client ships no
    /// lifespan sheet (<c>RANGE_STRING_ID</c> is a UI string, not a distance), so the alternative
    /// was a feel number. <b>[U] against the client, and it carries the unit assumption</b> that
    /// the owner's m/s speeds and the server's world units are the same length - which is exactly
    /// what one live shot with the table on would settle.
    /// </para>
    /// </summary>
    public const float RangeUnits = 350f;

    /// <summary>
    /// <c>LIFESPAN</c> for a projectile of this muzzle speed: <see cref="RangeUnits"/> over the
    /// speed, and <b>0 for a projectile whose speed this server does not know</b> - which correctly
    /// leaves that projectile unable to spawn rather than giving it an invented flight.
    /// </summary>
    public static float LifespanFor(float speed) => speed > 0f ? RangeUnits / speed : 0f;

    /// <summary>
    /// <b>Muzzle speed per calibre, adopted from the owner's own retail research under D53.</b>
    /// <c>C:\Z1\Server\Zone\ZoneRetailBalance.cs:180-211</c> carries a speed on every retail weapon
    /// row, sourced from Daybreak's own published Combat Update notes (its source S19, the
    /// "X m/s (up from Y)" lines) - <b>the file's <c>RetailPs3Damage = true</c> arm, which is
    /// explicitly the NON-reference branch</b>; the h1emu table is the other arm
    /// (<c>ZoneCombatWeapons.DamageIsReferenceBalance</c>) and is not read here. His own comment at
    /// <c>:225-232</c> records that Z1 never applies the number; Cranberry is the first server in
    /// either tree to put it on a wire.
    /// <para>
    /// The key is the <b>calibre</b>, because that is what a projectile is: the id's own
    /// <c>PenTypes.DESCRIPTION</c> from the client's <c>ProjectileToPenTypes.txt</c>. The four arrow
    /// and spear rows have no published speed and are deliberately absent - see
    /// <see cref="LifespanFor"/> for what that means for them.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, float> MuzzleSpeedByPenType { get; } =
        new Dictionary<string, float>(StringComparer.Ordinal)
        {
            [".223"] = 375f,                        // AR-15            ZoneRetailBalance.cs:181-183
            ["Rifle.AK47.762Bullet"] = 375f,        // AK-47            :186-187
            [".308 FMJ"] = 659f,                    // .308 Hunting     :190-191
            [".44 Magnum"] = 375f,                  // .44 Magnum       :194
            [".45 ACP"] = 375f,                     // M1911A1          :197
            [".9mm"] = 375f,                        // M9               :200-201
            [".380"] = 375f,                        // R380             :205
            ["12GA shotgun"] = 250f,                // 12GA Pump        :209-210
        };

    /// <summary>
    /// One record per client projectile id, in ascending id order, carrying everything the spawn
    /// path reads that this project can source (docs/107 §3):
    /// <see cref="ProjectileDefinitionRecord.FlightType"/> = 1,
    /// <see cref="ProjectileDefinitionRecord.Speed"/> from
    /// <see cref="MuzzleSpeedByPenType"/> and <see cref="ProjectileDefinitionRecord.Lifespan"/>
    /// derived from it, plus the client's own constructor presets everywhere else.
    /// <para>
    /// <b>A projectile with no published speed gets speed 0, lifespan 0 and flight type 0</b> - the
    /// five arrow rows and the spear. That is a record the client can resolve and will not fly,
    /// which is the honest shape for a value nobody has: the alternative is an invented arrow.
    /// </para>
    /// <para>
    /// <b>Wave 14: these records are now REACHABLE.</b> Until list 4 was filled
    /// (<see cref="FireModeProjectileRecord"/>) nothing in the client could get from a fire mode to
    /// one of these ids, which is why the table shipped off. It ships ON from wave 14.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ProjectileDefinitionRecord> Records { get; } =
    [
        .. AugustProjectileFacts.All.Select(fact =>
        {
            float speed = MuzzleSpeedByPenType.TryGetValue(fact.PenTypeName, out float known)
                ? known
                : 0f;

            return new ProjectileDefinitionRecord(fact.ProjectileId)
            {
                Speed = speed,
                Lifespan = LifespanFor(speed),
                FlightType = speed > 0f ? BallisticFlightType : (byte)0,
            };
        }),
    ];

    /// <summary>
    /// <b>Which calibre a round IS</b> - the ammunition item's own
    /// <c>ClientItemDefinitions</c> name matched to the <c>PenTypes.DESCRIPTION</c> that carries
    /// the same calibre. Both halves are the client's own files; the pairing is the obvious one and
    /// is written out here rather than pattern-matched, so it can be read and argued with.
    /// <code>
    ///  112  "Wooden Arrow"              -> MetalTippedHandmadeArrow
    /// 1428  ".45 Round"                 -> .45 ACP
    /// 1429  ".223 Round"                -> .223
    /// 1469  ".308 Round"                -> .308 FMJ
    /// 1511  "12 Gauge Buckshot Shell"   -> 12GA shotgun
    /// 1719  ".44 Round"                 -> .44 Magnum
    /// 1992  ".380 Round"                -> .380
    /// 1998  "9mm Round"                 -> .9mm
    /// 2325  "7.62x39 Round"             -> Rifle.AK47.762Bullet
    /// </code>
    /// <para>
    /// The ninth <c>PenTypes</c> row, <c>MetalTippedLargeWoodSpear</c>, has no ammunition item in
    /// this build and no weapon that fires it, so nothing maps to it.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<uint, string> PenTypeByAmmoItemId { get; } =
        new Dictionary<uint, string>
        {
            [112] = "MetalTippedHandmadeArrow",
            [1428] = ".45 ACP",
            [1429] = ".223",
            [1469] = ".308 FMJ",
            [1511] = "12GA shotgun",
            [1719] = ".44 Magnum",
            [1992] = ".380",
            [1998] = ".9mm",
            [2325] = "Rifle.AK47.762Bullet",
        };

    /// <summary>
    /// The <c>PROJECTILE_ID</c> a round produces, or <b>0</b> when this build names none - which is
    /// the honest answer for a melee weapon, a throwable and the fists, and which correctly leaves
    /// them without a list-4 row.
    /// <para>
    /// <b>Where the ambiguity is, and how it is resolved.</b> Five of the client's fourteen
    /// <c>ProjectileToPenTypes</c> rows share pen type 2 (<c>MetalTippedHandmadeArrow</c>): 70038,
    /// 70046, 70054, 70061 and 70075 - five arrows, presumably wooden / metal / explosive and so
    /// on, which no August sheet tells apart. The <b>lowest id wins</b>, deterministically, and the
    /// choice is recorded rather than hidden. Every other calibre is one row and one id.
    /// </para>
    /// </summary>
    public static uint ProjectileForAmmoItem(uint ammoItemDefinitionId)
    {
        if (!PenTypeByAmmoItemId.TryGetValue(ammoItemDefinitionId, out string? penType))
        {
            return 0u;
        }

        uint best = 0u;
        foreach (AugustProjectileFact fact in AugustProjectileFacts.All)
        {
            if (string.Equals(fact.PenTypeName, penType, StringComparison.Ordinal)
                && (best == 0u || fact.ProjectileId < best))
            {
                best = fact.ProjectileId;
            }
        }

        return best;
    }

    /// <summary>
    /// The bullet records plus, when <paramref name="throwables"/> is on, the five grenade records
    /// of <see cref="AugustThrowables"/> (docs/120 §3) at <paramref name="throwSpeed"/>.
    /// </summary>
    public static IReadOnlyList<ProjectileDefinitionRecord> RecordsFor(bool throwables, float throwSpeed) =>
        throwables
            ? [.. Records, .. AugustThrowables.ProjectileRecords(throwSpeed)]
            : Records;

    /// <summary>The blob for a session.</summary>
    public static ProjectileDefinitionsBlob CreateBlob(bool populate = true) =>
        new(populate ? Records : []);

    /// <summary>The blob for a session, with or without the throwable records (docs/120).</summary>
    public static ProjectileDefinitionsBlob CreateBlob(bool populate, bool throwables, float throwSpeed) =>
        new(populate ? RecordsFor(throwables, throwSpeed) : []);
}
