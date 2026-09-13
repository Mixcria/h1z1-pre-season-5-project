using Cranberry.Protocol;

namespace Cranberry.Zone.Weapons;

/// <summary>
/// One <c>FireModes</c> record - <b>list 2</b> of the <see cref="WeaponDefinitionsBlob"/>, the list
/// the weapon component's vtable slot 2 (<c>FUN_14147f040</c>, key <c>rec+0x378</c>) looks a fire
/// group's mode ids up in.
/// <para>
/// <b>Layout [P, 2026-09-02 overhaul lane 2D - docs/58 U2 CLOSED]</b>. The list reader
/// <c>FUN_140a4f0e0</c> takes the id; <c>FUN_140a39b80</c> takes one <c>u32</c> into
/// <c>rec+0x18</c> and hands the rest to <c>FUN_140a422d0(rec+0x20, cursor)</c>, which is 180
/// straight-line reads totalling <b>631 bytes</b> (<see cref="WeaponListLayouts.FireModeBody"/>).
/// A record is therefore <b>639 bytes</b>, fixed - no counted array, no string.
/// </para>
/// <code>
/// u32   fireModeId   -&gt; rec+0x378   FUN_140a4f0e0 (hash key)
/// u32   triggerCharge-&gt; rec+0x18    FUN_140a39b80
/// 631 B body        -&gt; rec+0x20..0x2e8   FUN_140a422d0
/// </code>
/// <para>
/// <b>Why 631 is not a guess.</b> <c>FUN_140a39b80</c> guards the body with a locally computed XOR
/// over <c>0x59</c> whole qwords starting at <c>rec+0x20</c>, plus one more masked to its high
/// half. That last, half-covered qword begins at <c>rec+0x20 + 0x59*8 = rec+0x2e8</c> - exactly
/// the offset of the last field <c>FUN_140a422d0</c> writes. Two functions read independently
/// agree on where the body ends, and the checksum is computed, never sent (docs/58 section 4d).
/// </para>
/// <para>
/// <b>What Cranberry puts in it: the client's own defaults, plus the trigger gate and the sheet's
/// own times.</b> <c>FUN_1422267e0</c>, the client's record constructor, presets 34 of those words
/// to <c>1.0f</c> and every other word to <c>0</c>. Cranberry writes exactly that, so a populated
/// list 2 cannot change any behaviour the client would not already have had - except that the
/// modes now <em>resolve</em>. That is the entire point of the list: <c>FUN_142290f60</c>
/// blank-constructs a mode whose id does not resolve, so today every mode the client owns is a
/// default with no server behind it.
/// </para>
/// </summary>
/// <param name="FireModeId">
/// The hash key at <c>rec+0x378</c>, and the value a <see cref="FireGroupRecord.FireModeIds"/>
/// entry has to match. Cranberry's numbering is its own - see
/// <c>AugustWeaponTable.FireModeIdFor</c> - because the August client ships no fire-mode table for
/// this build and nothing in its data names one.
/// </param>
/// <param name="DefinitionId">
/// <c>rec+0x18</c> - the one list-2 word the datasheet-row loader does NOT name, because it sits
/// before the loader's own object base <c>rec+0x20</c>.
/// <b>The fire mode's OWN id, and wave 14 corrects what this field is</b> - docs/99
/// section 2.4 and docs/107 called it "the trigger charge". It is an identifier, and the client uses
/// it for three things:
/// <list type="number">
/// <item>the <b>scope key of every stat override</b> on this mode: the getter pattern is
/// <c>f(comp, <b>def+0x18</b>, &amp;DAT_key, def+OFFSET)</c> - <c>FUN_1422a5150</c> passes it with
/// <c>FireMode.ReloadTime</c> (<c>def+0x60</c>) and <c>FUN_1422a50a0</c> with
/// <c>FireMode.ReloadLoopStartTime</c> (<c>def+0x6c</c>), which also promotes both of those two
/// offsets from [I] to <b>[P]</b>;</item>
/// <item>the <b>primary key of list 4</b>, the fire-mode to projectile mapping
/// (<see cref="FireModeProjectileRecord"/>): <c>FUN_14228fcf0:29-35</c> - the function the local
/// spawn <c>FUN_140c7bb60</c> depends on - resolves the mode's record, reads <c>def+0x18</c> and
/// hands it to the list-4 lookup;</item>
/// <item>the <c>&gt; 0</c> gate <c>FUN_142291b90:20</c> applies before a mode may arm, which is the
/// only part of the old reading that survives.</item>
/// </list>
/// <b>Cranberry writes the fire-mode id itself</b> (the same value as <paramref name="FireModeId"/>,
/// <c>rec+0x378</c>), exactly as list 0 mirrors its own id into <c>def+0x18</c> and the projectile
/// record into <c>rec+0x18</c>. The old value - <c>CLIP_SIZE</c> for mode 0 and <c>1</c> for mode 1 -
/// gave 60 of the 120 modes the same "id", which would have collided in both the override container
/// and list 4.
/// </param>
/// <param name="AmmoSlot">
/// <c>rec+0x30</c> = <c>AMMO_SLOT</c> [P] (<see cref="WeaponListLayouts.FireModeAmmoSlot"/>), an
/// <c>i8</c> on the wire. The index into the weapon definition's own ammo-slot array
/// (<see cref="WeaponAmmoSlotRow"/>) and, with the same value, into the item's magazine vector.
/// Cranberry declares one slot per gun, so this is 0.
/// <para>
/// <b>Named a second way, wave 14 doc lane.</b> The client's own <c>FireModes</c> datasheet-row
/// loader stores the schema column <c>AMMO_SLOT</c> at exactly this offset (loader
/// <c>142227723</c>) - docs/99 CORRECTION 3. Cranberry's value is unchanged.
/// </para>
/// </param>
/// <param name="IronSights">
/// <c>rec+0x20</c> bit <c>0x04</c> = <c>IRON_SIGHTS</c> [P]
/// (<see cref="WeaponListLayouts.FireModeIronSightsFlag"/>). Set, the client treats entering this
/// mode as aiming down sights: <c>FUN_1422935d0:68-77</c> takes the transition time from
/// <c>Weapon.ToIronSightsTime</c> instead of <c>Weapon.FromIronSightsTime</c>. The datasheet-row
/// loader writes the same bit for the schema column <c>IRON_SIGHTS</c> (loader <c>142227309</c>,
/// <c>OR byte ptr [RBX],0x4</c>), which is the second, independent binding.
/// </param>
/// <param name="RefireTimeMs">
/// <c>rec+0x40</c> = <c>FireMode.RefireTime</c> [P]: <c>FUN_14228d2d0</c> - the weapon component's
/// own fire tick - reads <c>def+0x40</c> and hands it to the int-valued stat override
/// <c>FUN_14228f4e0</c> under the key <c>DAT_145592b80</c>, whose initialiser string is
/// <c>FireMode.RefireTime</c>. Filled from the client's own
/// <c>ClientItemDatasheetData.REFIRE_TIME_MS</c>. The datasheet-row loader names the same offset
/// from the other end: schema column <c>REFIRE_TIME_MS</c>, loader <c>1422278d1</c>.
/// </param>
/// <param name="AutoFireTimeMs">
/// <c>rec+0x48</c> = <c>FireMode.AutoFireTime</c> [P], same function, key <c>DAT_1455928d8</c>;
/// schema column <c>AUTO_FIRE_TIME_MS</c>, loader <c>1422278a9</c>.
/// Cranberry ships <c>0</c>: no client sheet carries it and no ruling has set it.
/// </param>
/// <param name="ReloadTimeMs">
/// <c>rec+0x60</c> = <c>FireMode.ReloadTime</c> [I]. The five reload columns of the client's own
/// <c>FireModes</c> schema (<c>RELOAD_TIME_MS</c>, <c>RELOAD_CHAMBER_TIME_MS</c>,
/// <c>RELOAD_AMMO_FILL_TIME_MS</c>, <c>RELOAD_LOOP_START_TIME_MS</c>,
/// <c>RELOAD_LOOP_END_TIME_MS</c>) land on the contiguous <c>i16</c> run
/// <c>0x60 / 0x64 / 0x68 / 0x6c / 0x70</c>, in that order; two of the five have an independent
/// getter (<c>FUN_1422a4f30</c>, <c>FUN_1422a4ff0</c>). Filled from
/// <c>ClientItemDatasheetData.RELOAD_TIME_MS</c>.
/// <para>
/// <b>All five are [P] since the wave-14 doc lane</b> (docs/99 CORRECTION 3): the client's own
/// datasheet-row loader stores the five schema columns at exactly the run docs/99 section 2.3
/// inferred - <c>RELOAD_TIME_MS</c> <c>142227971</c>, <c>RELOAD_CHAMBER_TIME_MS</c>
/// <c>142227999</c>, <c>RELOAD_AMMO_FILL_TIME_MS</c> <c>1422279c1</c>,
/// <c>RELOAD_LOOP_START_TIME_MS</c> <c>1422279e9</c>, <c>RELOAD_LOOP_END_TIME_MS</c>
/// <c>142227a11</c>. No value moves.
/// </para>
/// </param>
/// <param name="ReloadChamberTimeMs">
/// <c>rec+0x64</c> = <c>RELOAD_CHAMBER_TIME_MS</c> [P]. 0 unless a sheet or ruling says otherwise.
/// </param>
/// <param name="ReloadAmmoFillTimeMs"><c>rec+0x68</c> = <c>RELOAD_AMMO_FILL_TIME_MS</c> [P]. 0.</param>
/// <param name="ReloadLoopStartTimeMs"><c>rec+0x6c</c> = <c>RELOAD_LOOP_START_TIME_MS</c> [P]. 0.</param>
/// <param name="ReloadLoopEndTimeMs"><c>rec+0x70</c> = <c>RELOAD_LOOP_END_TIME_MS</c> [P]. 0.</param>
/// <param name="PelletsPerShot">
/// <c>rec+0x74</c> = <c>FireMode.PelletsPerShot</c> / schema column <c>PELLETS_PER_SHOT</c>
/// (loader <c>142227a39</c>, getter <c>FUN_1422a4c80</c>), an <c>i8</c>. <b>[I] to [P]</b>, wave-14
/// doc lane. 0 = the client's own default of one projectile.
/// </param>
/// <param name="MovementModifier">
/// <c>rec+0x10c</c> = <c>FireMode.MovementModifier</c> / schema column <c>MOVEMENT_MODIFIER</c>
/// (loader <c>1422277cb</c>) [P] - <c>FUN_1422a4a60</c> reads exactly
/// this offset and falls back to <c>DAT_1430ef088</c> = <c>1.0f</c> when no definition resolves.
/// <b>D143: it ships <c>1.0f</c>.</b> Zero here is "cannot move while this mode is current".
/// </param>
/// <param name="TurnModifier">
/// <c>rec+0x110</c> = <c>FireMode.TurnModifier</c> / schema column <c>TURN_MODIFIER</c> (loader
/// <c>1422277fe</c>) [P] - <c>FUN_1422a5760</c>, same shape. D143: <c>1.0f</c>.
/// </param>
/// <param name="DefaultZoom">
/// <c>rec+0x128</c> = <c>DEFAULT_ZOOM</c> / <c>FireMode.DefaultZoom</c> [P] - <b>the ADS zoom</b>,
/// a multiplier whose <c>1.0f</c> is the client's own constructor preset and means "no zoom".
/// <c>FUN_141488490</c> reads this exact offset, <c>FUN_1411c8740</c> interpolates the character's
/// <c>+0x55a4</c> toward it, and the client's own debug HUD prints that word as <c>Zoom</c>
/// (<c>FUN_140f786f0</c>). The datasheet-row loader stores the schema column <c>DEFAULT_ZOOM</c>
/// here (loader <c>142227ab9</c>). See <see cref="WeaponListLayouts.FireModeDefaultZoom"/> for the
/// two independent bindings, and <c>AugustWeaponTable.AdsZoom</c> for the value and its ruling.
/// </param>
/// <param name="ForceFpScope">
/// <c>rec+0x2dc</c> = <c>FORCE_FP_SCOPE</c> [P] - <b>the ADS first-person switch</b>.
/// <c>FUN_14158ad50:17</c> reads this byte off the LIVE fire-mode record and
/// <c>FUN_14158b700</c> - the <c>Infantry</c> / <c>SecondaryFire</c> handler, which
/// <c>InputProfile_Default.xml:518</c> binds to <c>Mouse_1</c> as <c>UI.WeaponOptic</c> -
/// branches on it. Set, right-click calls <c>FUN_14158ab50</c>, which puts the camera in
/// <c>0x1e</c> (first person) while the button is held and back in <c>0x24</c> on release, and
/// persists the choice through <c>FUN_1413ac350</c>, the writer of <c>[General] FirstPerson</c>
/// in <c>UserOptions.ini</c>. Clear, right-click prepares the third-person aim camera through
/// virtual slot <c>+0x370</c>. These camera branches are exclusive
/// (<c>14158bb2d JZ 14158bc1f</c>), but a separate input path in the caller
/// <c>14158ef20</c> invokes <c>1411ceca0</c> and can select ADS mode 1 through
/// <c>1411b3500</c>. This flag does not itself suppress <see cref="DefaultZoom"/>.
/// </param>
/// <param name="FpCameraFovDegrees">
/// The absolute first-person field of view in DEGREES for <c>rec+0x2d0</c> / <c>+0x2d4</c> /
/// <c>+0x2d8</c> (<c>FP_CAMERA_FOV</c>, <c>FP_CR_</c>, <c>FP_PR_</c>), behind the gate
/// <c>rec+0x2cd</c> (<c>FP_FORCE_CAMERA_OVERRIDES</c>) that <c>FUN_140e3fff0</c> tests. Zero
/// clears the gate, which is what every wave before this one shipped: the client then keeps the
/// player's own <c>[Rendering] VerticalFOV</c>. See <c>AugustFireModeFacts.AdsFpCameraFov</c>.
/// </param>
/// <param name="MeleeAbilityId">
/// <c>rec+0x194</c> = <c>MELEE_ABILITY_ID</c> [P] - <b>the field that makes a fire mode a SWING
/// rather than a shot</b> (<see cref="WeaponListLayouts.FireModeMeleeAbilityId"/>). The client's
/// own <c>FireModes</c> row loader stores the schema column <c>MELEE_ABILITY_ID</c> at exactly
/// this displacement (<c>142228759</c>, <c>LEA RDX,[RBX+0x174]</c> over the <c>rec+0x20</c> base -
/// docs/119 section 2, wave 16). Cranberry shipped <c>0</c> in it from wave 9 to wave 15, which
/// is why <b>every</b> shipped fire mode was a firearm mode and the fists produced a trigger-down
/// and nothing else.
/// </param>
/// <param name="MeleeCompositeEffectId">
/// <c>rec+0x190</c> = <c>MELEE_COMPOSITE_EFFECT_ID</c> [P] (loader <c>142228730</c>), the
/// composite effect the swing plays. Non-zero also sets the <c>rec+0x22</c> bit
/// <see cref="WeaponListLayouts.FireModeMeleeCompositeEffectIdFlag"/>, which is the SAME schema
/// column's second binding (loader <c>142227659</c>, <c>OR byte ptr [RBX+0x2],0x4</c>) - the
/// client writes the presence flag and the id from one cell, so a server that writes one without
/// the other is describing a mode the client's own loader could never have produced.
/// </param>
/// <param name="Automatic">
/// <c>rec+0x20</c> bit <c>0x20</c> = <c>AUTOMATIC</c> [P] (loader <c>142227259</c>, read by
/// <c>FUN_1422a5860</c> as the arms / animation predicate). Set on the fire modes of the fire
/// groups the client's own text calls automatic - the AK-47 family, locale description 11959
/// (docs/121 §3, D331). The fire GROUP's own automatic bit (<c>FireGroupRecord.AutomaticFlag</c>)
/// is the one <c>FUN_1411ceca0</c> branches on; this one is what the datasheet loader would have
/// stored from the same <c>AUTOMATIC</c> cell, and the two are written together.
/// </param>
/// <param name="PelletSpread">
/// <c>rec+0x7c</c> = <c>PELLET_SPREAD</c> [P] (loader <c>142227a89</c>), in <b>degrees</b>: the
/// spawn's pattern branch multiplies it by <c>pi/180</c> (<c>FUN_140c7bb60:523-524</c>). Read only
/// when <see cref="PelletsPerShot"/> is 2 or more (<c>:507-511</c>). 0 is the client's own preset.
/// </param>
/// <param name="EffectGroup">
/// <c>rec+0x104</c> = <c>EFFECT_GROUP</c> (<see cref="WeaponListLayouts.FireModeEffectGroup"/>) -
/// <b>the <c>FireModeEffectGroups</c> GROUP_ID the client resolves to a fire (muzzle flash + gunshot
/// audio) composite effect</b>. When a shot resolves the client reads this group off the live fire
/// mode, looks the <c>(group, "fire")</c> pair up in <c>FireModeEffectGroups.txt</c> itself, and
/// queues the resulting composite effect, which is where the gunshot sound lives
/// (<c>HasAudioOnCompositeEffect</c>). Cranberry shipped <c>0</c> from wave 9 to wave 16, so the
/// client logged <c>"Failed to QueueCompositeEffectAtLocation due to missing effect definition for
/// Id #0!"</c> and played no fire sound. The value is the fire-group id itself
/// (<c>AugustFireModeFacts.EffectGroup</c>), shipped only for a group with a non-zero <c>fire</c>
/// row; a group with no row (the AK-47's 51 included) is a gap and stays 0.
/// </param>
/// <param name="Type">
/// <c>rec+0x24</c> = <c>TYPE</c> (<see cref="WeaponListLayouts.FireModeType"/>), an <c>i8</c> -
/// <b>what a trigger pull on this mode DOES</b>. Read by the client's fire executor
/// <c>FUN_14228d1d0</c> (<c>TYPE == 3</c> raises the entity event <c>hash("MELEE_ATTACK")</c>
/// through vtable slot <c>+0x1e0</c> = <c>FUN_141480d50</c> / <c>DAT_145235750</c> instead of
/// <c>OnFireBegin</c>; <c>TYPE != 0xc</c> runs <c>OnFireBegin</c>), by <c>FUN_14228d2d0</c>
/// (<c>TYPE == 0xc</c> fires <c>OnThrowEnd</c>, slot <c>+0x298</c>; <c>TYPE == 8</c> fires
/// <c>OnTriggerItemAbility</c>, slot <c>+0x1c8</c>), and by the hotbar's own data-source getters
/// <c>FUN_1414f31c0</c> / <c>FUN_1414a6660</c>, which SKIP the <c>WeaponShouldShowAmmo</c> /
/// <c>LoadoutWeaponShouldShowAmmo</c> cell when <c>TYPE == 3</c> and otherwise write it from the
/// magazine and <c>AMMO_ITEM_ID</c>. So <see cref="WeaponListLayouts.FireModeTypeMelee"/> (3) is
/// what makes a fist swing and hides its "0-0"; <see cref="WeaponListLayouts.FireModeTypeThrowable"/>
/// (12) is a grenade; <see cref="WeaponListLayouts.FireModeTypeTriggerItemAbility"/> (8) runs the
/// item's own activatable ability on the trigger. The client constructor's 0 is a projectile shot.
/// </param>
public sealed record FireModeRecord(
    uint FireModeId,
    uint DefinitionId = 1,
    int RefireTimeMs = 0,
    int AutoFireTimeMs = 0,
    int ReloadTimeMs = 0,
    int ReloadChamberTimeMs = 0,
    int ReloadAmmoFillTimeMs = 0,
    int ReloadLoopStartTimeMs = 0,
    int ReloadLoopEndTimeMs = 0,
    int PelletsPerShot = 0,
    float MovementModifier = 1.0f,
    float TurnModifier = 1.0f,
    int AmmoSlot = 0,
    bool IronSights = false,
    float DefaultZoom = 1.0f,
    bool ForceFpScope = false,
    float FpCameraFovDegrees = 0.0f,
    uint MeleeAbilityId = 0,
    uint MeleeCompositeEffectId = 0,
    uint EffectGroup = 0,
    int Type = 0,
    bool Automatic = false,
    float PelletSpread = 0.0f,
    int FireDurationMs = 0)
{
    /// <summary>
    /// <b>D313 (docs/123): the captured 1087 table's own words for this mode</b>, keyed by the 1148
    /// record offset they belong at and already encoded for that offset's wire kind (a float field
    /// carries its IEEE-754 bits, an integer field its value). Null - the default and what every
    /// generated record uses - changes nothing.
    /// <para>
    /// An entry wins over every other rule in <see cref="WriteTo"/>, including the flag byte and
    /// the named parameters, because the captured value IS the ruling for that field when
    /// <c>CRANBERRY_WEAPON_TABLE=captured</c>. It can never move the record's LENGTH: the word is
    /// written at the field's own size, so a 639-byte record stays 639 bytes.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<short, uint>? Overrides { get; init; }

    public byte ReticleId { get; init; }

    /// <summary>Run the local component's ammo/heat guards before entering the fire state.</summary>
    public bool CheckEnterFireState { get; init; }

    /// <summary>Rounds required by the fire guard; an optic has no slot that can satisfy it.</summary>
    public int AmmoPerShot { get; init; }

    /// <summary>
    /// <c>RANGE</c> (rec+0x58). For TYPE 12 the client aims at camera position plus
    /// forward times this distance (FUN_1411be900), so zero aims back at the camera.
    /// </summary>
    public float Range { get; init; }

    /// <summary><c>LAUNCH_PITCH_ADDITIVE_DEGREES</c> (rec+0x198), applied by FUN_1411bd8b0.</summary>
    public float LaunchPitchAdditiveDegrees { get; init; }

    /// <summary><c>4</c> (id) + <c>4</c> (<c>rec+0x18</c>) + <c>631</c> (body) = <b>639</b>.</summary>
    public const int RecordLength = 8 + WeaponListLayouts.FireModeBodyLength;

    /// <summary>Always <see cref="RecordLength"/> - the record is fixed-size.</summary>
    public int Length => RecordLength;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt32(FireModeId);       // FUN_140a4f0e0 -> rec+0x378 (hash key)
        writer.WriteUInt32(DefinitionId);     // FUN_140a39b80 -> rec+0x18  (the mode's own id)

        foreach (WeaponListField field in WeaponListLayouts.FireModeBody)
        {
            // D313: the captured table's own word for this offset, at this offset's own width.
            if (Overrides is not null
                && Overrides.TryGetValue(field.RecordOffset, out uint captured))
            {
                switch (field.Size)
                {
                    case 1:
                        writer.WriteByte((byte)captured);
                        break;
                    case 2:
                        writer.WriteUInt16((ushort)captured);
                        break;
                    default:
                        writer.WriteUInt32(captured);
                        break;
                }

                continue;
            }

            switch (field.RecordOffset)
            {
                case WeaponListLayouts.FireModeReticleId:
                    writer.WriteByte(ReticleId);
                    continue;
                case WeaponListLayouts.FireModeFlags0:
                    // rec+0x20 bit 2 = IRON_SIGHTS (FUN_1422935d0:68); bit 5 = AUTOMATIC (loader
                    // 142227259, read back by FUN_1422a5860 - docs/121 §3). Every other bit stays 0.
                    writer.WriteByte((byte)(
                        (IronSights ? WeaponListLayouts.FireModeIronSightsFlag : (byte)0)
                        | (Automatic ? WeaponListLayouts.FireModeAutomaticFlag : (byte)0)));
                    continue;
                case WeaponListLayouts.FireModeFlags1:
                    writer.WriteByte(CheckEnterFireState ? WeaponListLayouts.FireModeCheckEnterFireStateFlag : (byte)0);
                    continue;
                case WeaponListLayouts.FireModeAmmoPerShot:
                    writer.WriteByte((byte)Math.Clamp(AmmoPerShot, 0, sbyte.MaxValue));
                    continue;
                case WeaponListLayouts.FireModePelletSpread:
                    // rec+0x7c PELLET_SPREAD (loader 142227a89, getter FUN_1422a4bd0 through
                    // FUN_14228fb90), in DEGREES: the local spawn's pattern branch multiplies it by
                    // DAT_14311d670 = pi/180 (FUN_140c7bb60:523-524). 0 is the client's own preset.
                    writer.WriteSingle(PelletSpread);
                    continue;
                case WeaponListLayouts.FireModeFlags2:
                    // rec+0x22. Only MELEE_COMPOSITE_EFFECT_ID's presence bit is ever set, and
                    // only when the id beside it is non-zero: loader 142227659 sets the bit from
                    // the same cell loader 142228730 stores at rec+0x190.
                    writer.WriteByte(MeleeCompositeEffectId != 0
                        ? WeaponListLayouts.FireModeMeleeCompositeEffectIdFlag
                        : (byte)0);
                    continue;
                case WeaponListLayouts.FireModeMeleeCompositeEffectId:
                    writer.WriteUInt32(MeleeCompositeEffectId);   // rec+0x190, loader 142228730
                    continue;
                case WeaponListLayouts.FireModeMeleeAbilityId:
                    writer.WriteUInt32(MeleeAbilityId);           // rec+0x194, loader 142228759
                    continue;
                case WeaponListLayouts.FireModeEffectGroup:
                    // rec+0x104 EFFECT_GROUP. A FireModeEffectGroups GROUP_ID the client resolves to
                    // the fire composite effect (muzzle flash + gunshot audio) on a shot; handed 0
                    // it logs "missing effect definition for Id #0" and plays no sound. This is the
                    // fire-group id itself; 0 restores every wave before this.
                    writer.WriteUInt32(EffectGroup);              // rec+0x104, loader 142227b83
                    continue;
                case WeaponListLayouts.FireModeType:
                    // rec+0x24 TYPE, an i8 (loader 1422276d3). 3 = melee, 12 = throwable,
                    // 8 = trigger item ability; the constructor's 0 is a projectile shot.
                    writer.WriteByte((byte)(sbyte)Math.Clamp(Type, sbyte.MinValue, sbyte.MaxValue));
                    continue;
                case WeaponListLayouts.FireModeAmmoSlot:
                    // rec+0x30, an i8 (FUN_14228fcf0:31, FUN_141154450:165, FUN_1414887e0).
                    writer.WriteByte((byte)(sbyte)Math.Clamp(AmmoSlot, sbyte.MinValue, sbyte.MaxValue));
                    continue;
                case WeaponListLayouts.FireModeRefireTime:
                    WriteInt16(writer, RefireTimeMs);
                    continue;
                case WeaponListLayouts.FireModeRange:
                    writer.WriteSingle(Range);
                    continue;
                case WeaponListLayouts.FireModeLaunchPitchAdditiveDegrees:
                    writer.WriteSingle(LaunchPitchAdditiveDegrees);
                    continue;
                case WeaponListLayouts.FireModeFireDurationMs:
                    // rec+0x38 FIRE_DURATION_MS (loader 142227859). On a TYPE-12 (throwable) mode
                    // FUN_142293af0 reads it on the trigger press and refuses to enter the throw
                    // state (0x10) when it is < 1 - docs/120 s2.2. 0 everywhere else, as before.
                    WriteInt16(writer, FireDurationMs);
                    continue;
                case WeaponListLayouts.FireModeAutoFireTime:
                    WriteInt16(writer, AutoFireTimeMs);
                    continue;
                case WeaponListLayouts.FireModeReloadTime:
                    WriteInt16(writer, ReloadTimeMs);
                    continue;
                case WeaponListLayouts.FireModeReloadChamberTime:
                    WriteInt16(writer, ReloadChamberTimeMs);
                    continue;
                case WeaponListLayouts.FireModeReloadAmmoFillTime:
                    WriteInt16(writer, ReloadAmmoFillTimeMs);
                    continue;
                case WeaponListLayouts.FireModeReloadLoopStartTime:
                    WriteInt16(writer, ReloadLoopStartTimeMs);
                    continue;
                case WeaponListLayouts.FireModeReloadLoopEndTime:
                    WriteInt16(writer, ReloadLoopEndTimeMs);
                    continue;
                case WeaponListLayouts.FireModePelletsPerShot:
                    writer.WriteByte((byte)Math.Clamp(PelletsPerShot, 0, sbyte.MaxValue));
                    continue;
                case WeaponListLayouts.FireModeMovementModifier:
                    writer.WriteSingle(MovementModifier);
                    continue;
                case WeaponListLayouts.FireModeTurnModifier:
                    writer.WriteSingle(TurnModifier);
                    continue;
                case WeaponListLayouts.FireModeDefaultZoom:
                    // rec+0x128 DEFAULT_ZOOM. Still one of the 34 FireModeUnitScalars, so the
                    // multiplier guard covers it; 1.0f here is byte-identical to the fallback.
                    writer.WriteSingle(DefaultZoom);
                    continue;
                case WeaponListLayouts.FireModeForceFpScope:
                    // rec+0x2dc FORCE_FP_SCOPE. FUN_14158ad50:17 reads this byte off the LIVE
                    // fire-mode record, so it only ever fires on the mode that is live when the
                    // right mouse button goes down - the PRIMARY mode. Set, FUN_14158b700 swaps
                    // the camera (FUN_14158ab50 -> FUN_140b7a770 0x1e / 0x24) INSTEAD OF calling
                    // its aim vtable slot +0x370, so it also suppresses 82 0c while it is on.
                    writer.WriteByte(ForceFpScope ? (byte)1 : (byte)0);
                    continue;
                case WeaponListLayouts.FireModeFpForceCameraOverrides:
                    // rec+0x2cd. FUN_140e3fff0 leaves the caller's own FOV standing unless this
                    // byte is set, so a zero FpCameraFovDegrees is "keep the player's verticalFOV"
                    // and is byte-identical to every wave before this one.
                    writer.WriteByte(FpCameraFovDegrees > 0.0f ? (byte)1 : (byte)0);
                    continue;
                case WeaponListLayouts.FireModeFpCameraFov:
                case WeaponListLayouts.FireModeFpCrouchedCameraFov:
                case WeaponListLayouts.FireModeFpProneCameraFov:
                    // rec+0x2d0 / +0x2d4 / +0x2d8, the standing / crouched / prone words
                    // FUN_140e3fff0 picks between on player+0x338 +0x37e7. Cranberry has one
                    // number for all three stances: the client's own per-stance values are [U]
                    // and inventing three would be D143's exact failure.
                    writer.WriteSingle(FpCameraFovDegrees > 0.0f ? FpCameraFovDegrees : 0.0f);
                    continue;
            }

            if (WeaponListLayouts.FireModeUnitScalars.Contains(field.RecordOffset))
            {
                // FUN_1422267e0 presets this word to 0x3f800000. D143: a scalar ships 1.0f.
                writer.WriteSingle(1.0f);
                continue;
            }

            // Everything else is the client constructor's own zero (docs/99 section 2.3 [U]).
            switch (field.Size)
            {
                case 1:
                    writer.WriteByte(0);
                    break;
                case 2:
                    writer.WriteUInt16(0);
                    break;
                default:
                    writer.WriteUInt32(0);
                    break;
            }
        }
    }

    private static void WriteInt16(PacketWriter writer, int value) =>
        writer.WriteUInt16((ushort)(short)Math.Clamp(value, short.MinValue, short.MaxValue));
}

/// <summary>
/// One element of a <b>list 3</b> record - <c>FUN_140a41f00</c>'s 17 words behind the element key
/// <c>FUN_140a50040</c> reads. <b>Role [I]: one <c>ConeOfFire</c> player-state row</b>
/// (<see cref="WeaponListLayouts.List3ElementBodyLength"/>).
/// </summary>
/// <param name="StateId">The element's own key (<c>FUN_140b10260</c>).</param>
/// <param name="Words">
/// The 17 body words, in wire order: <c>Words[0]</c> is <c>elem+0x18</c>, <c>Words[1..16]</c> are
/// <c>+0x24 .. +0x60</c>. Fewer than 17 is padded with zeros; more is refused, because the element
/// is fixed-size and a long one would misalign every record after it.
/// </param>
/// <param name="Flags">
/// The one byte at <c>elem+0x20</c>, written between <c>Words[0]</c> and <c>Words[1]</c>
/// (<c>FUN_140a41f00</c>). The 1087 element's <c>FLAGS</c>; the capture carries it equal to the
/// state id. Leaving it out is the 2026-09-04 20:12 client crash (docs/123 §7).
/// </param>
public sealed record ConeOfFireStateRow(uint StateId, IReadOnlyList<uint>? Words = null, byte Flags = 0)
{
    /// <summary>Body words after the key: <b>17</b> (plus the one <see cref="Flags"/> byte).</summary>
    public const int WordCount = 17;

    /// <summary><see cref="WeaponListLayouts.List3ElementLength"/> - always 73.</summary>
    public int Length => WeaponListLayouts.List3ElementLength;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        IReadOnlyList<uint> words = Words ?? [];
        ArgumentOutOfRangeException.ThrowIfGreaterThan(words.Count, WordCount);

        writer.WriteUInt32(StateId);
        writer.WriteUInt32(words.Count > 0 ? words[0] : 0u);   // elem+0x18
        writer.WriteByte(Flags);                                // elem+0x20 - ONE byte
        for (int i = 1; i < WordCount; i++)                     // elem+0x24 .. +0x60
        {
            writer.WriteUInt32(i < words.Count ? words[i] : 0u);
        }
    }
}

/// <summary>
/// One <b>list 3</b> record - <c>FUN_140a4f230</c>: <c>u32 id</c> (to <c>rec+0x60</c>, the hash
/// key), <c>u32</c> (to <c>rec+0x00</c>), then <c>FUN_140a50040</c>'s counted list of
/// <see cref="ConeOfFireStateRow"/>. <b>Role [I]: <c>ConeOfFire</c>, keyed by cone group.</b>
/// Cranberry ships list 3 empty - docs/99 section 3.
/// </summary>
public sealed record ConeOfFireRecord(
    uint ConeOfFireId,
    uint Unknown00 = 0,
    IReadOnlyList<ConeOfFireStateRow>? States = null)
{
    /// <summary><c>4 + 4 + 4</c> with no states.</summary>
    public const int MinimalLength = 12;

    /// <summary><c>12 + 72n</c>.</summary>
    public int Length => MinimalLength + (WeaponListLayouts.List3ElementLength * (States?.Count ?? 0));

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        IReadOnlyList<ConeOfFireStateRow> states = States ?? [];

        writer.WriteUInt32(ConeOfFireId);   // FUN_140a4f230 -> rec+0x60 (hash key)
        writer.WriteUInt32(Unknown00);      // FUN_140a4f230 -> rec+0x00 [U]
        writer.WriteInt32(states.Count);    // FUN_140a50040 count
        foreach (ConeOfFireStateRow state in states)
        {
            state.WriteTo(writer);
        }
    }
}

/// <summary>
/// One <b>list 4</b> record - <c>FUN_140a4d5d0</c> reads an <c>i32</c> key into <c>rec+0x20</c> and
/// <c>FUN_140a39ca0</c> reads three words into <c>rec+0x00 / +0x04 / +0x08</c>. Flat, <b>16
/// bytes</b>, no nested list.
///
/// <para>
/// <b>Role [P], wave 14: this is the fire mode to projectile mapping</b>, and it is the only path
/// from a trigger pull to a <c>ProjectileDefinitions</c> id. docs/99 section 3.2's "Role [U]" and
/// docs/107 section 6 S1's "<c>rec+0x04</c> is an ammo-derived key [U]" are both CLOSED.
/// </para>
/// <code>
/// FUN_1411542f0(weaponDefsMgr, fireModeDefinitionId, ammoItemId)
///     buckets mgr+0x120, count mgr+0x100          (list 4's header is mgr+0xf8)
///     walk the chain (rec+0x28) for rec+0x20 == fireModeDefinitionId
///     then walk it again for rec+0x04 == ammoItemId
///     return rec+0x08                              the projectile definition id
/// </code>
/// <para>
/// <b>Where each key comes from - three functions, agreeing.</b>
/// </para>
/// <list type="number">
/// <item><b>The shot itself.</b> <c>FUN_140c7bb60</c> (the local spawn) opens with
/// <c>FUN_14228da90(comp)</c> and returns immediately if it yields 0.
/// <c>FUN_14228da90:9-18</c> resolves the live fire-mode record, reads <b><c>def+0x18</c></b> and
/// calls <c>FUN_14228fcf0(comp, thatId)</c>, which looks <c>FireMode.ProjectileOverride</c> up in
/// the stat container first, finds nothing, and then at <c>:29-35</c> takes the mode's
/// <c>def+0x30</c> <c>AMMO_SLOT</c>, resolves <c>FUN_14228de50(comp, slot)</c> = the definition's
/// <b><c>AmmoSlot.AmmoId</c></b>, and hands both to the list-4 lookup (the component vtable's
/// slot <c>+0x28</c>).
/// Dump: <c>out\firegroup\gh-projspawn\</c>.</item>
/// <item><b>The effect preloader.</b> <c>FUN_141154450:164-178</c> inlines the same walk with the
/// same two keys, then at <c>:180-212</c> looks the result up in the projectile manager
/// <c>DAT_143f6a000</c> (key <c>rec+0x158</c>) and preloads that record's <c>+0x6c</c>,
/// <c>+0x70</c>, <c>+0xb0</c> and <c>+0xb8</c> effect ids - independent proof that
/// <c>rec+0x08</c> is a <c>ProjectileDefinitions</c> id.</item>
/// <item><b>The UI stat panel.</b> <c>FUN_141617460:137-153</c> calls <c>FUN_1411542f0</c> with the
/// same pair and feeds the answer to <c>FUN_140c474e0(DAT_143f6a000, id)</c>, then reports
/// <c>max(rec+0x7c, rec+0x5c)</c> - <c>MAX_SPEED</c> and <c>SPEED</c>.
/// Dump: <c>out\firegroup\gh-projlookup\callers_140c474e0\</c>.</item>
/// </list>
/// <para>
/// <c>AmmoSlot.AmmoId</c> and <c>AmmoSlot.ClipSize</c> are the client's own names for the first two
/// words of the slot descriptor - the string-hash initialisers <c>FUN_14091eb20:10</c> and
/// <c>FUN_14091f060:10</c> (<c>out\firegroup\gh-ammoslotkeys\refs_145592b10_145592c18\</c>).
/// </para>
/// </summary>
/// <param name="FireModeDefinitionId">
/// <c>rec+0x20</c>, the hash key: the <see cref="FireModeRecord.DefinitionId"/> of the mode that
/// fires this projectile.
/// </param>
/// <param name="AmmoItemId">
/// <c>rec+0x04</c>: the <see cref="WeaponAmmoSlotRow.AmmoId"/> of the slot the mode names. The
/// pair, not the mode alone, is the key - which is how one fire mode fires different projectiles
/// for different rounds.
/// </param>
/// <param name="ProjectileDefinitionId">
/// <c>rec+0x08</c>: the <c>ReferenceData "ProjectileDefinitions"</c> record to spawn.
/// </param>
/// <param name="Unknown00"><c>rec+0x00</c>. Neither consumer reads it; Cranberry writes 0.</param>
public sealed record FireModeProjectileRecord(
    uint FireModeDefinitionId,
    uint AmmoItemId,
    uint ProjectileDefinitionId,
    uint Unknown00 = 0)
{
    /// <summary><see cref="WeaponListLayouts.List4RecordLength"/> - always 16.</summary>
    public int Length => WeaponListLayouts.List4RecordLength;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt32(FireModeDefinitionId);   // FUN_140a4d5d0 -> rec+0x20 (hash key)
        writer.WriteUInt32(Unknown00);              // FUN_140a39ca0 -> rec+0x00 [U]
        writer.WriteUInt32(AmmoItemId);             // FUN_140a39ca0 -> rec+0x04  AmmoSlot.AmmoId
        writer.WriteUInt32(ProjectileDefinitionId); // FUN_140a39ca0 -> rec+0x08  the projectile id
    }
}

/// <summary>
/// One <b>list 5</b> record - <c>FUN_140a4e570</c>: <c>u32 id</c> (to <c>rec+0x90</c>), then
/// <c>FUN_140a2c1b0</c>'s 23 words at <c>rec+0x20 .. +0x78</c>. <b>96 bytes</b>.
/// <b>Role [I]: <c>AimAssist</c></b> (21 client columns at <c>0x35e9098</c>). Cranberry ships list
/// 5 empty.
/// </summary>
public sealed record AimAssistRecord(uint AimAssistId, IReadOnlyList<uint>? Words = null)
{
    /// <summary>Body words after the id: <b>23</b>.</summary>
    public const int WordCount = 23;

    /// <summary><see cref="WeaponListLayouts.List5RecordLength"/> - always 96.</summary>
    public int Length => WeaponListLayouts.List5RecordLength;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        IReadOnlyList<uint> words = Words ?? [];
        ArgumentOutOfRangeException.ThrowIfGreaterThan(words.Count, WordCount);

        writer.WriteUInt32(AimAssistId);   // FUN_140a4e570 -> rec+0x90 (hash key)
        for (int i = 0; i < WordCount; i++)
        {
            writer.WriteUInt32(i < words.Count ? words[i] : 0u);   // FUN_140a2c1b0 -> rec+0x20..+0x78
        }
    }
}

/// <summary>
/// One element of a <b>list 6</b> record - <c>FUN_140a4fbf0</c>'s key (to <c>sub+0x38</c>) plus
/// <c>FUN_140a3f970</c>'s three words (<c>sub+0x18</c>, <c>+0x20</c>, <c>+0x24</c>). 16 bytes.
/// </summary>
public sealed record WeaponBlobList6Element(uint Key, uint Word0 = 0, uint Word1 = 0, uint Word2 = 0)
{
    /// <summary><see cref="WeaponListLayouts.List6ElementLength"/> - always 16.</summary>
    public int Length => WeaponListLayouts.List6ElementLength;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt32(Key);
        writer.WriteUInt32(Word0);
        writer.WriteUInt32(Word1);
        writer.WriteUInt32(Word2);
    }
}

/// <summary>
/// One <b>list 6</b> record - <c>FUN_140a4fa40</c>: <c>u32 id</c> (to <c>rec+0x60</c>), <c>u32</c>
/// (to <c>rec+0x18</c>), then <c>FUN_140a4fbf0</c>'s counted list. Role [U]. Cranberry ships list
/// 6 empty.
/// </summary>
public sealed record WeaponBlobList6Record(
    uint Id,
    uint Unknown18 = 0,
    IReadOnlyList<WeaponBlobList6Element>? Elements = null)
{
    /// <summary><c>4 + 4 + 4</c> with no elements.</summary>
    public const int MinimalLength = 12;

    /// <summary><c>12 + 16n</c>.</summary>
    public int Length => MinimalLength + (WeaponListLayouts.List6ElementLength * (Elements?.Count ?? 0));

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        IReadOnlyList<WeaponBlobList6Element> elements = Elements ?? [];

        writer.WriteUInt32(Id);          // FUN_140a4fa40 -> rec+0x60 (hash key)
        writer.WriteUInt32(Unknown18);   // FUN_140a4fa40 -> rec+0x18 [U]
        writer.WriteInt32(elements.Count);
        foreach (WeaponBlobList6Element element in elements)
        {
            element.WriteTo(writer);
        }
    }
}

/// <summary>
/// One <b>list 7</b> record - <c>FUN_140a4fd40</c>: <c>u32 id</c> (to <c>rec+0x78</c>), <c>u32</c>
/// (to <c>rec+0x00</c>), then <c>FUN_140a4cbf0</c>'s counted list of bare <c>u32</c>s. Role [U] -
/// the lookup <c>FUN_14147f370</c> exists, no consumer is in any dump. Cranberry ships list 7
/// empty.
/// </summary>
public sealed record WeaponBlobList7Record(
    uint Id,
    uint Unknown00 = 0,
    IReadOnlyList<uint>? Values = null)
{
    /// <summary><c>4 + 4 + 4</c> with no values.</summary>
    public const int MinimalLength = 12;

    /// <summary><c>12 + 4n</c>.</summary>
    public int Length => MinimalLength + (WeaponListLayouts.List7ElementLength * (Values?.Count ?? 0));

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        IReadOnlyList<uint> values = Values ?? [];

        writer.WriteUInt32(Id);          // FUN_140a4fd40 -> rec+0x78 (hash key)
        writer.WriteUInt32(Unknown00);   // FUN_140a4fd40 -> rec+0x00 [U]
        writer.WriteInt32(values.Count);
        foreach (uint value in values)
        {
            writer.WriteUInt32(value);
        }
    }
}
