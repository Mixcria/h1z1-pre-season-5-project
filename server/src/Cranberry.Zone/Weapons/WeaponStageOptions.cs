namespace Cranberry.Zone.Weapons;

/// <summary>
/// The three independently toggleable stages of docs/58 §11's wielding plan, plus the one extra
/// toggle this wave's Ghidra run made possible.
/// <para>
/// <b>Why stages at all.</b> The owner is away and cannot play-test. Every previous attempt to put
/// an item in body slot 7 produced a minidump (docs/45: 4 of 4 packets carrying a slot-7 row killed
/// the client within a millisecond). So each half of the fix ships as its own switch, defaulting to
/// the safest value, and each one is observable in a <em>client-side</em> log before the next is
/// turned on - which is the only evidence standard docs/32 accepts.
/// </para>
/// <para>
/// <b>Order matters and is enforced.</b> <see cref="AllowWielding"/> without
/// <see cref="WriteWeaponItemAddTail"/> would put a weapon in the hand whose fire-group array is
/// still empty - the exact docs/45 crash. <see cref="Effective"/> refuses that combination rather
/// than trusting a call site to remember.
/// </para>
/// </summary>
public sealed record WeaponStageOptions
{
    /// <summary>Environment switch for <see cref="SendWeaponDefinitions"/> (stage 1). Default OFF.</summary>
    public const string SendDefinitionsVariable = "CRANBERRY_WEAPON_DEFINITIONS";

    /// <summary>Environment switch for <see cref="PopulateWeaponDefinitions"/> (stage 1b). Default off.</summary>
    public const string PopulateDefinitionsVariable = "CRANBERRY_WEAPON_DEFS_LIST0";

    /// <summary>
    /// Environment switch for <see cref="PopulateFireGroups"/> (list 1 of the stage-1 blob). Default
    /// on, so <c>CRANBERRY_WEAPON_DEFINITIONS=1</c> alone ships the real table;
    /// <c>CRANBERRY_WEAPON_DEFS_LIST1=0</c> is the 32-byte empty-envelope control.
    /// </summary>
    public const string PopulateFireGroupsVariable = "CRANBERRY_WEAPON_DEFS_LIST1";

    /// <summary>
    /// Bisect lever for <see cref="WeaponMovementModifier"/> (2026-09-02, wield-freeze fix E0). A
    /// float; unset = <c>1.0</c>, the client's own default. <c>0.5</c> is the owner's "Run B": with
    /// a gun in hand the player must move at half speed, which proves the field is the multiplier.
    /// </summary>
    public const string MovementModifierVariable = "CRANBERRY_WEAPON_MOVEMENT_MODIFIER";

    /// <summary>
    /// Bisect lever for <see cref="WriteWeaponDefinitionBodyId"/> (2026-09-02, E0). <c>0</c> ships
    /// the pre-fix zero at <c>def+0x18</c>; anything else keeps the record's own id there.
    /// </summary>
    public const string BodyIdVariable = "CRANBERRY_WEAPON_DEF_BODY_ID";

    /// <summary>
    /// Environment switch for <see cref="PopulateFireModes"/> (list 2 of the stage-1 blob, overhaul
    /// lane 2D). Default ON; <c>CRANBERRY_WEAPON_DEFS_LIST2=0</c> is the revert to the six zero
    /// counts waves 9-16 shipped.
    /// </summary>
    public const string PopulateFireModesVariable = "CRANBERRY_WEAPON_DEFS_LIST2";

    /// <summary>
    /// Environment switch for <see cref="SendProjectileDefinitions"/>.
    /// <b>Default ON since wave 14</b> - see that property for what changed.
    /// </summary>
    public const string ProjectileDefinitionsVariable = "CRANBERRY_PROJECTILE_DEFINITIONS";

    /// <summary>
    /// Environment switch for <see cref="PopulateAmmoSlots"/> (the list-0 <c>def+0xd0</c> array,
    /// wave 14). Default ON; <c>CRANBERRY_WEAPON_DEFS_AMMOSLOTS=0</c> restores the empty array and
    /// every list-0 record's wave-9..13 length.
    /// </summary>
    public const string PopulateAmmoSlotsVariable = "CRANBERRY_WEAPON_DEFS_AMMOSLOTS";

    /// <summary>
    /// Environment switch for <see cref="PopulateFireModeProjectiles"/> (list 4, wave 14). Default
    /// ON; <c>CRANBERRY_WEAPON_DEFS_LIST4=0</c> ships the empty list waves 9-13 shipped.
    /// </summary>
    public const string PopulateFireModeProjectilesVariable = "CRANBERRY_WEAPON_DEFS_LIST4";

    /// <summary>
    /// Environment switch for <see cref="WriteAdsZoom"/> (docs/107 wave-14 addendum, D212).
    /// <b>Default ON</b>; <c>CRANBERRY_WEAPON_ADS_FOV=0</c> writes <c>1.0f</c> into every mode's
    /// <c>rec+0x128</c> again, which is byte-identical to every wave before this one.
    /// </summary>
    public const string AdsFovVariable = "CRANBERRY_WEAPON_ADS_FOV";

    /// <summary>
    /// Float lever for <see cref="WeaponAdsZoom"/>: the <c>DEFAULT_ZOOM</c> multiplier the
    /// iron-sights mode carries. Unset = <c>AugustFireModeFacts.AdsZoom</c>. The client bands this
    /// number itself - above <c>2.0</c> it switches to <c>scopedMouseSensitivity</c> - so a value
    /// in <c>(1.0, 2.0]</c> is an iron sight and one above <c>2.0</c> is a scope.
    /// </summary>
    public const string AdsZoomVariable = "CRANBERRY_WEAPON_ADS_ZOOM";

    /// <summary>
    /// Environment switch for <see cref="WriteAdsFirstPerson"/> (docs/107 wave-15 addendum).
    /// <b>Default ON</b>; <c>CRANBERRY_WEAPON_ADS_FP=0</c> writes <c>0</c> into every mode's
    /// <c>rec+0x2dc</c> and <c>rec+0x2cd</c> again, which restores the wave-14 third-person
    /// ADS - the <c>82 0c</c> fire-mode switch with <see cref="WeaponAdsZoom"/> - byte for byte.
    /// </summary>
    public const string AdsFirstPersonVariable = "CRANBERRY_WEAPON_ADS_FP";

    /// <summary>
    /// Float lever for <see cref="WeaponAdsFpCameraFov"/>: the aimed first-person field of view
    /// in DEGREES. Unset = <c>AugustFireModeFacts.AdsFpCameraFov</c>;
    /// <c>CRANBERRY_WEAPON_ADS_FP_FOV=0</c> clears the <c>rec+0x2cd</c> gate and leaves the
    /// player's own <c>[Rendering] VerticalFOV</c> standing while aiming.
    /// </summary>
    public const string AdsFpFovVariable = "CRANBERRY_WEAPON_ADS_FP_FOV";

    /// <summary>
    /// Environment switch for <see cref="IronSightsArmedOnly"/>. <b>Default ON</b>;
    /// <c>CRANBERRY_WEAPON_IRONSIGHTS_ARMED=0</c> restores wave 15, where mode index 1 of
    /// <em>every</em> fire group carried <c>IRON_SIGHTS</c> - the fists and the binoculars
    /// included.
    /// </summary>
    public const string IronSightsArmedOnlyVariable = "CRANBERRY_WEAPON_IRONSIGHTS_ARMED";

    /// <summary>
    /// Environment switch for <see cref="WriteMeleeAbilityIds"/>. <b>Default ON</b>;
    /// <c>CRANBERRY_WEAPON_MELEE_ABILITY=0</c> restores the all-zero <c>rec+0x194</c> every wave
    /// before 16 shipped.
    /// </summary>
    public const string MeleeAbilityVariable = "CRANBERRY_WEAPON_MELEE_ABILITY";

    /// <summary>
    /// Environment switch for <see cref="WriteFireEffect"/> (report 1, the fire sound).
    /// <b>Default ON</b>; <c>CRANBERRY_WEAPON_FIRE_SOUND=0</c> writes <c>0</c> into <c>rec+0x104</c>
    /// again, the wave-9..16 silence.
    /// </summary>
    public const string FireEffectVariable = "CRANBERRY_WEAPON_FIRE_SOUND";

    /// <summary>
    /// Environment switch for <see cref="PlainWieldNoMagazine"/> (report 3). <b>Default ON</b>;
    /// <c>CRANBERRY_WEAPON_NO_MELEE_MAG=0</c> restores the mode-0 trigger-charge sentinel that made
    /// the fists and the binoculars show a 1-round magazine.
    /// </summary>
    public const string PlainWieldNoMagazineVariable = "CRANBERRY_WEAPON_NO_MELEE_MAG";

    /// <summary>
    /// Environment switch for <see cref="BinocularsOptic"/> (report 3). <b>Default ON</b>;
    /// <c>CRANBERRY_BINOCULARS_OPTIC=0</c> stops shipping <c>FORCE_FP_SCOPE</c> on the binoculars.
    /// </summary>
    public const string BinocularsOpticVariable = "CRANBERRY_BINOCULARS_OPTIC";

    /// <summary>
    /// Float lever for <see cref="BinocularsOpticFovDegrees"/>: the aimed first-person field of view,
    /// in DEGREES, when the binoculars are raised. Unset = the D235 ruling; <c>0</c> clears the gate
    /// and keeps the player's own <c>VerticalFOV</c> (raised to first person but not zoomed).
    /// </summary>
    public const string BinocularsOpticFovVariable = "CRANBERRY_BINOCULARS_OPTIC_FOV";

    /// <summary>
    /// Environment switch for <see cref="WriteFireModeTypes"/> (D291-D293, wave 17).
    /// <b>Default ON</b>; <c>CRANBERRY_WEAPON_FIRE_MODE_TYPES=0</c> restores the all-zero
    /// <c>rec+0x24</c> every wave before 17 shipped - the fists, the blades and the grenades all
    /// declared as projectile shots.
    /// </summary>
    public const string FireModeTypesVariable = "CRANBERRY_WEAPON_FIRE_MODE_TYPES";

    /// <summary>
    /// Environment switch for <see cref="BinocularsTriggerAbility"/> (D293). <b>Default ON</b>;
    /// <c>CRANBERRY_BINOCULARS_ABILITY=0</c> leaves the binoculars' modes at <c>TYPE 0</c> while
    /// the melee and throwable types still ship.
    /// </summary>
    public const string BinocularsTriggerAbilityVariable = "CRANBERRY_BINOCULARS_ABILITY";

    /// <summary>
    /// Environment switch for <see cref="WriteIronSightsTimes"/>. <b>Default ON</b>;
    /// <c>CRANBERRY_WEAPON_ADS_TIMES=0</c> writes <c>0</c> into <c>def+0x38</c> / <c>def+0x3c</c>
    /// again, which is the client's own "instant" and every wave before this one.
    /// </summary>
    public const string IronSightsTimesVariable = "CRANBERRY_WEAPON_ADS_TIMES";

    /// <summary>
    /// Environment switch for <see cref="RetailAutomatic"/> (docs/121 §3, D331). <b>Default ON</b>;
    /// <c>CRANBERRY_WEAPON_AUTO_RETAIL=0</c> restores the wave-9 diagnostic - the AR-15's group 6
    /// automatic, the AK-47 single-shot, and no <c>AUTO_FIRE_TIME_MS</c> anywhere.
    /// </summary>
    public const string RetailAutomaticVariable = "CRANBERRY_WEAPON_AUTO_RETAIL";

    /// <summary>
    /// Integer lever for <see cref="ShotgunPellets"/>. Unset = 17, the early-August
    /// shell; <c>0</c> restores the client's own default, which its spawn reads as ONE pellet.
    /// </summary>
    public const string ShotgunPelletsVariable = "CRANBERRY_SHOTGUN_PELLETS";

    /// <summary>
    /// Float lever for <see cref="ShotgunSpreadDegrees"/> (docs/121 §4, D332). Unset = 4.0
    /// degrees, a ruling; <c>0</c> restores the client's own preset (every pellet on one line).
    /// </summary>
    public const string ShotgunSpreadVariable = "CRANBERRY_SHOTGUN_SPREAD_DEG";

    /// <summary>
    /// Environment switch for <see cref="WeaponTable"/> (D313, docs/123): <c>captured</c> (the
    /// default) or <c>generated</c>. Anything else leaves the default, as every other lever here does.
    /// </summary>
    public const string WeaponTableVariable = "CRANBERRY_WEAPON_TABLE";
    public const string FastLongGunDrawVariable = "CRANBERRY_FAST_LONG_GUN_DRAW";
    public const string Z1LiveGunplayVariable = "CRANBERRY_Z1_LIVE_GUNPLAY";

    /// <summary>
    /// Environment switch for <see cref="ProjectileTable"/> (D314, docs/123): <c>z1</c> (the
    /// default) or <c>august</c>.
    /// </summary>
    public const string ProjectileTableVariable = "CRANBERRY_PROJECTILE_TABLE";

    /// <summary>Environment switch for <see cref="WriteWeaponItemAddTail"/> (stage 2). Default off.</summary>
    public const string TailVariable = "CRANBERRY_WEAPON_TAIL";

    /// <summary>Environment switch for <see cref="AllowWielding"/> (stage 3). Default off.</summary>
    public const string WieldVariable = "CRANBERRY_WIELD";

    /// <summary>Environment switch for <see cref="MarkFireGroupsAutomatic"/> (docs/95, D186). Default on.</summary>
    public const string AutomaticVariable = "CRANBERRY_WEAPON_AUTOMATIC";

    /// <summary>Environment switch for <see cref="TailIdleState"/> (S6 §7.2). Default ON.</summary>
    public const string TailIdleStateVariable = "CRANBERRY_WEAPON_TAIL_STATE";

    /// <summary>Environment switch for <see cref="WriteWeaponTailMagazine"/> (docs/107 §1). Default ON.</summary>
    public const string TailAmmoVariable = "CRANBERRY_WEAPON_TAIL_AMMO";

    /// <summary>Environment switch for <see cref="SendWeaponStance"/> (S6 §7.3). Default ON.</summary>
    public const string WeaponStanceVariable = "CRANBERRY_WEAPON_STANCE";

    /// <summary>
    /// <b>STAGE 1 - DEFAULT ON</b> (<c>CRANBERRY_WEAPON_DEFINITIONS=0</c> is the revert). Send
    /// <c>ReferenceData "WeaponDefinitions"</c> once per session, before any weapon <c>ItemAdd</c>.
    /// <para>
    /// <b>Wave 9 turned this on, and the August client is what turned it on.</b> The owner's
    /// 20:36-20:41 session on 2026-08-30 wrote two lines and only two lines into
    /// <c>C:\Aug2017\Client\Logs\WeaponErrors.log</c>:
    /// <c>ClientPlayerItemManager::CreateItem - weapon definition not found for weapon ID 1374</c>
    /// at 20:38:08 and <c>... for weapon ID 1405</c> at 20:38:19 - the two instants he picked a gun
    /// up - and <c>Client\Logs\FirstPersonArms.log</c> line 3 reads <c>ERROR - player does not
    /// have anything equipped in the primary weapon slot!</c>. 1374 and 1405 are exactly the
    /// <c>WEAPON_ID</c>s <see cref="AugustWeaponFact"/> holds for the two items he picked up. The
    /// table the client asked for by number is built in this tree and had never been sent. That is
    /// LIVE-VERIFIED, client-side, and it outranks every caution below.
    /// </para>
    /// <para>
    /// <b>The cautions are still true and are why the revert exists</b> (wave-6 verify; this
    /// comment once claimed "zero crash risk" and every clause of that claim was wrong):
    /// </para>
    /// <list type="number">
    /// <item><b>The packet cannot be ignored.</b> <c>FUN_140b055c0</c> tests
    /// <c>"WeaponDefinitions"</c> by name and, on a match, calls <c>thunk_FUN_140a20570</c>. The
    /// <c>Received ReferenceData type=%s, but no handler!</c> line is emitted <em>only</em> from the
    /// no-name-matched branch, which this type name can never reach in this build.</item>
    /// <item><b>A parse failure is an access violation, not a no-op.</b> <c>FUN_140a20570</c>
    /// returns the cursor's error byte at <c>+0x20</c> - set by <c>FUN_140a398c0</c>,
    /// <c>FUN_140a4caf0</c> and <c>FUN_140a4ef30</c> whenever a read would pass the blob end - and
    /// <c>FUN_140b055c0</c> answers a false return with <c>_DAT_00000000 = 1</c>, a store to address
    /// 0. Any blob that consumes fewer bytes than the true layout kills the client on the spot.
    /// Over-writing is harmless; under-writing is fatal.</item>
    /// <item><b>Its send site is the login bootstrap</b> (<c>ZoneService.SendBootstrap</c>), i.e. the
    /// path to PLAY that is otherwise byte-identical to the known-good
    /// <c>captures\wire-20260829-150206.txt</c>. docs/32 is the cautionary tale about new packets on
    /// a proven-safe path, and the owner is away and could not attribute a hang there.</item>
    /// <item><b>FALSIFIED by the client's own log.</b> This item used to read "at the shipped
    /// stage set it buys nothing ... an ON default was full parse exposure for no behaviour
    /// change". <c>CreateItem</c> early-returning <em>is</em> the failure the client logs, so the
    /// stage set that "bought nothing" bought exactly the two <c>WeaponErrors.log</c> lines above.
    /// Stages 1, 1b, 2 and 3 are one feature and ship together.</item>
    /// </list>
    /// <para>
    /// <b>The acceptance check is "the client reaches character select at all"</b> - NOT the absence
    /// of a <c>no handler!</c> line in <c>C:\Aug2017\Client\Logs\ClientBadData.log</c>, which is
    /// unfalsifiable for this type name (point 1). Run 1 is
    /// <c>CRANBERRY_WEAPON_DEFINITIONS=1 CRANBERRY_WEAPON_DEFS_LIST1=0</c>: 32 zero bytes, the
    /// well-formed empty table, which proves the envelope and the dispatch with zero record-layout
    /// exposure. Run 1a drops <c>LIST1=0</c> and adds the 60 real fire groups. Those staged runs
    /// are now the <em>diagnosis</em> ladder for a client that dies at login, not the ship order.
    /// </para>
    /// </summary>
    public bool SendWeaponDefinitions { get; init; } = true;

    /// <summary>
    /// <b>LIST 1 OF STAGE 1 - DEFAULT ON</b> (<c>CRANBERRY_WEAPON_DEFS_LIST1=0</c> turns it off).
    /// Fill list 1 with the 60 <see cref="FireGroupRecord"/>s instead of leaving it empty.
    /// <para>
    /// This exists so the "32 zero bytes is a well-formed, completely empty table" argument in
    /// <see cref="WeaponDefinitionsBlob"/> names a configuration that can actually be shipped -
    /// before this switch, list 1 was populated unconditionally and the only thing stage 1 could
    /// send was 3,212 bytes of a 53-byte record layout that is DERIVED and has never been on a wire.
    /// </para>
    /// <para>
    /// Turning it off also correctly leaves stage 3 unclearable:
    /// <c>WeaponSession.MarkWeaponDefinitionsSent</c> declares the ids the blob actually carried, so
    /// an empty list 1 leaves <c>WeaponFireGroupLedger.WeaponDefinitionsSent</c> false - and
    /// <see cref="Effective"/> refuses <see cref="AllowWielding"/> outright.
    /// </para>
    /// </summary>
    public bool PopulateFireGroups { get; init; } = true;

    /// <summary>
    /// <b>LIST 2 OF STAGE 1 - DEFAULT ON</b> (<c>CRANBERRY_WEAPON_DEFS_LIST2=0</c> turns it off).
    /// Fill list 2 (<c>FireModes</c>) with one <see cref="FireModeRecord"/> per fire mode the
    /// shipped fire groups name, and give every list-1 record the matching
    /// <see cref="FireGroupRecord.FireModeIds"/> - which are empty without this switch.
    /// <para>
    /// <b>What turning it on actually changes for the client.</b> Cranberry writes the client's own
    /// record-constructor defaults into every field of the body (<c>FUN_1422267e0</c>: 34 words at
    /// <c>1.0f</c>, every other word <c>0</c>) except three: the trigger charge at
    /// <c>rec+0x18</c>, which <c>FUN_142291b90</c> requires to be <c>&gt; 0</c> before a mode can
    /// arm, and <c>FireMode.RefireTime</c> / <c>FireMode.ReloadTime</c>, which come from the
    /// client's own <c>ClientItemDatasheetData</c> row. So the behavioural delta is exactly "the
    /// mode ids now RESOLVE" - today <c>FUN_142290f60</c> blank-constructs every mode because
    /// nothing in list 2 answers, and <c>FUN_14147f040</c> (weapon-component vtable slot 2) has
    /// never had a record to find.
    /// </para>
    /// <para>
    /// The risk is the one every record in this blob carries and is why the revert exists: the
    /// 639-byte record length is DERIVED and has never been on a wire, and a length that is wrong
    /// by one byte misaligns every following list. Lists 3-7 stay empty either way.
    /// </para>
    /// </summary>
    public bool PopulateFireModes { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON as of wave 14</b> (<c>CRANBERRY_PROJECTILE_DEFINITIONS=0</c> is the revert).
    /// Send <c>ReferenceData "ProjectileDefinitions"</c> once per session, immediately before
    /// <c>WeaponDefinitions</c>.
    /// <para>
    /// <b>What changed, and why the default moved.</b> Wave 13 (docs/107 section 3) bound every
    /// field the client's spawn path reads and gates on - <c>+0x5c</c> <c>SPEED</c>, <c>+0x68</c>
    /// <c>FLIGHT_TYPE</c> (one byte), <c>+0x78</c> <c>LIFESPAN</c>, <c>+0x6c</c>
    /// <c>PROJECTILE_EFFECT_ID</c>, <c>+0x7c</c> / <c>+0x80</c>, <c>+0xec</c> <c>SCALE</c> - and
    /// fixed the one-byte read at <c>+0x68</c> the writer was missing, but left the table OFF for a
    /// single reason: <b>nothing could reach it</b>. The fire-mode to projectile link is list 4 of
    /// <c>WeaponDefinitions</c>, its role was [U], and Cranberry shipped it empty.
    /// </para>
    /// <para>
    /// <b>Wave 14 closed that.</b> <c>FUN_14228da90</c> - the accessor the local spawn
    /// <c>FUN_140c7bb60</c> opens with, and returns immediately without - reads the live fire mode's
    /// own id (<c>def+0x18</c>) and calls <c>FUN_14228fcf0</c>, which resolves the mode's
    /// <c>AMMO_SLOT</c> (<c>def+0x30</c>) to the weapon definition's <c>AmmoSlot.AmmoId</c> and
    /// looks the pair up in list 4, returning a <c>ProjectileDefinitions</c> id. Cranberry now ships
    /// all three pieces: the ammo slot (<see cref="PopulateAmmoSlots"/>), the mapping
    /// (<see cref="PopulateFireModeProjectiles"/>) and this table. Every link in the chain is [P]
    /// from the binary and every id in it is the client's own.
    /// </para>
    /// <para>
    /// <b>What is still not sourced, stated plainly.</b> <c>MODEL_FILE_NAME</c> is empty - the local
    /// spawn substitutes the client's own <c>"InvisibleTriangle.adr"</c>, so that costs OTHER
    /// players' projectiles, not the owner's - and <c>LIFESPAN</c> is derived from this server's own
    /// 350-unit hit limit rather than from any sheet. Those are value gaps in a table that resolves,
    /// not the structural gap that kept the switch off.
    /// </para>
    /// <para>
    /// <b>The failure mode is quiet, which is why the default can move at all.</b> A body the inner
    /// LZ4 cannot decode makes <c>FUN_140a40e40</c> skip its list without touching the OUTER
    /// cursor's error byte, so <c>FUN_140b055c0</c>'s <c>_DAT_00000000 = 1</c> store - the access
    /// violation a malformed <c>WeaponDefinitions</c> causes - cannot be reached from here. The
    /// worst case is the pre-wave-14 behaviour: no projectile.
    /// </para>
    /// </summary>
    public bool SendProjectileDefinitions { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON (wave 14)</b> - <c>CRANBERRY_WEAPON_DEFS_AMMOSLOTS=0</c> is the revert. Give
    /// each list-0 weapon definition its <c>def+0xd0</c> ammo-slot array
    /// (<see cref="WeaponAmmoSlotRow"/>): one slot per gun, carrying <c>AmmoSlot.AmmoId</c> and
    /// <c>AmmoSlot.ClipSize</c>.
    /// <para>
    /// <b>Why it matters beyond the projectile.</b> <c>FUN_1421e5e90(def, index)</c> bounds-checks
    /// against the array's count, so with the array empty every ammo-slot lookup in the client
    /// returned 0 - the projectile resolve, the clip-capacity getter <c>FUN_14228df60</c> and the
    /// effect preloader alike. <c>FUN_142291180</c> also sizes the item's own magazine vector from
    /// this count, so the array and the <c>ItemAdd</c> tail's leading counted array (docs/107
    /// section 1) now describe the same one slot instead of disagreeing.
    /// </para>
    /// <para>
    /// <b>The risk, and it is the usual one.</b> A gun's list-0 record grows by 37 bytes, and a
    /// record whose length is wrong misaligns every record after it. Melee rows, throwables and the
    /// fists keep the empty array and their exact previous length.
    /// </para>
    /// </summary>
    public bool PopulateAmmoSlots { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON (wave 14)</b> - <c>CRANBERRY_WEAPON_DEFS_LIST4=0</c> is the revert. Fill list 4
    /// with the fire-mode to projectile mapping (<see cref="FireModeProjectileRecord"/>): one
    /// 16-byte row per <c>(fireModeDefinitionId, ammoItemId)</c> pair the shipped tables can
    /// produce.
    /// <para>
    /// Without it a fire mode cannot reach a projectile id however good the projectile table is;
    /// with it and without <see cref="SendProjectileDefinitions"/> the client resolves an id it has
    /// no record for and the local spawn returns, which is exactly the pre-wave-14 behaviour. The
    /// two switches are therefore independent bisect levers on the same chain.
    /// </para>
    /// </summary>
    public bool PopulateFireModeProjectiles { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON (docs/107 wave-14 addendum, D212)</b> -
    /// <c>CRANBERRY_WEAPON_ADS_FOV=0</c> is the revert. Write
    /// <see cref="WeaponAdsZoom"/> into <c>rec+0x128</c> (<c>DEFAULT_ZOOM</c> /
    /// <c>FireMode.DefaultZoom</c>) on the iron-sights mode of every fire group an <em>armed</em>
    /// weapon names, instead of the <c>1.0f</c> every mode has carried since list 2 went on.
    /// <para>
    /// <b>This is the field the aimed camera zooms by, and it is the whole of the owner's
    /// "right-click enters iron sights but nothing zooms".</b> <c>FUN_141488490</c> reads
    /// <c>rec+0x128</c> through the stat-override helper under the key <c>DAT_1452362c8</c>, whose
    /// initialiser string is <c>"FireMode.DefaultZoom"</c> (<c>FUN_140655760</c>);
    /// <c>FUN_1411c2ce0</c> / <c>FUN_1411ca9e0</c> snap the character's own <c>+0x55a4</c> to it on
    /// a weapon change and <c>FUN_1411c8740</c> interpolates toward it on a mode change; the
    /// client's debug HUD prints that word as <c>Zoom</c> (<c>FUN_140f786f0</c>). Cranberry shipped
    /// <c>1.0f</c> there - the client's own constructor preset, i.e. "no zoom" - so the aim-in
    /// transition ran and the FOV never moved. docs/107 section 8.6's negative proof was scoped to
    /// <c>DAT_145592838</c>, a different static built from the same string.
    /// </para>
    /// <para>
    /// <b>Armed groups only.</b> D205 sets <c>IRON_SIGHTS</c> on mode index 1 of every group,
    /// including the fists and every melee row, because that is the index the client's own
    /// right-click sends. A hatchet's alternate swing is not aim-down-sights, so the zoom is
    /// applied only where <c>AmmoTypes.ByWeaponDefinitionId</c> pairs one of the group's weapons
    /// with a round - the same predicate that decides the list-0 ammo slot and the list-4 mapping.
    /// </para>
    /// <para>
    /// The wire length does not move: <c>rec+0x128</c> is one of the 34
    /// <see cref="WeaponListLayouts.FireModeUnitScalars"/> and was already being written, so this
    /// changes four bytes of one record per armed group and nothing else. The multiplier guard is
    /// unchanged and still covers the offset, because the value is <c>&gt; 0</c> either way.
    /// </para>
    /// </summary>
    public bool WriteAdsZoom { get; init; } = true;

    /// <summary>
    /// The <c>DEFAULT_ZOOM</c> multiplier <see cref="WriteAdsZoom"/> writes.
    /// <b>RULING D212</b>: the mechanism is [P] but no August sheet carries a zoom column, so the
    /// number is Cranberry's - <c>AugustFireModeFacts.AdsZoom</c>, the midpoint of the
    /// <c>(1.0, 2.0]</c> band the client itself uses to pick <c>ADSMouseSensitivity</c> over
    /// <c>scopedMouseSensitivity</c> (<c>_DAT_143275150 = 2.0f</c>, <c>FUN_140e40090</c> and
    /// <c>FUN_140e4a010</c>). <c>CRANBERRY_WEAPON_ADS_ZOOM</c> retunes it without a rebuild.
    /// </summary>
    public float WeaponAdsZoom { get; init; } = AugustFireModeFacts.AdsZoom;

    /// <summary>
    /// <b>DEFAULT ON (docs/107 wave-15 addendum)</b> - <c>CRANBERRY_WEAPON_ADS_FP=0</c> is the
    /// revert. Write <c>FORCE_FP_SCOPE</c> (<c>rec+0x2dc</c>) on the <b>primary</b> (index 0) mode
    /// of every fire group an <em>armed</em> weapon names, so that right-click takes the client to
    /// the FIRST-PERSON camera over the weapon instead of zooming the third-person one.
    /// <para>
    /// <b>This is the owner's "it is kind of a zoom-in feeling".</b> The whole path is [P]:
    /// <c>FUN_14158ad50:17</c> reads <c>rec+0x2dc</c> off the LIVE fire-mode record;
    /// <c>FUN_14158b700</c> - the <c>Infantry</c> / <c>SecondaryFire</c> handler that
    /// <c>InputProfile_Default.xml:518</c> binds to <c>Mouse_1</c> as <c>UI.WeaponOptic</c> -
    /// branches on it and calls <c>FUN_14158ab50</c>, which sets camera <c>0x1e</c> while the
    /// button is held and <c>0x24</c> on release through <c>FUN_140b7a770</c> and records the
    /// choice with <c>FUN_1413ac350</c>, the writer of <c>[General] FirstPerson</c> in
    /// <c>UserOptions.ini</c>. It is the ONLY gameplay path into first person in this build; every
    /// other caller of <c>FUN_140b7a770(mgr, 0x1e)</c> is construction placement or the
    /// <c>ChangeCamera</c> key.
    /// </para>
    /// <para>
    /// <b>Two consequences, both deliberate.</b> (1) The flag is read off the LIVE mode, so it has
    /// to sit on mode 0 - the mode that is live when the button goes down - not on the
    /// <c>IRON_SIGHTS</c> mode. (2) The camera branch and the aim branch are EXCLUSIVE
    /// (<c>14158bb2d JZ 14158bc1f</c>): while this is on the client no longer calls its aim vtable
    /// slot <c>+0x370</c>, so it sends no <c>82 0c SwitchFireModeRequest</c>, never enters the
    /// iron-sights mode, and <see cref="WeaponAdsZoom"/> is dormant. The aimed field of view is
    /// <see cref="WeaponAdsFpCameraFov"/> instead.
    /// </para>
    /// <para>
    /// <b>Armed groups only</b>, on the same <c>AmmoTypes.ByWeaponDefinitionId</c> predicate the
    /// ammo slot, list 4 and the zoom already use - a hatchet's alternate swing is not ADS. And
    /// <b>the wire length does not move</b>: <c>rec+0x2dc</c> and <c>rec+0x2cd</c> are bytes the
    /// record already writes as 0.
    /// </para>
    /// </summary>
    public bool WriteAdsFirstPerson { get; init; } = AugustFireModeFacts.AdsFirstPerson;

    /// <summary>
    /// The absolute aimed first-person field of view, in DEGREES, that
    /// <see cref="WriteAdsFirstPerson"/> writes into <c>FP_CAMERA_FOV</c> / <c>FP_CR_</c> /
    /// <c>FP_PR_</c> (<c>rec+0x2d0</c> / <c>+0x2d4</c> / <c>+0x2d8</c>) behind the
    /// <c>FP_FORCE_CAMERA_OVERRIDES</c> gate at <c>rec+0x2cd</c> that <c>FUN_140e3fff0</c> tests.
    /// <b>RULING</b>: the mechanism is [P] and no August sheet carries a field-of-view column, so
    /// the number is Cranberry's - the client's own default vertical FOV (<c>65</c>, the default
    /// <c>FUN_1413a3e30:233-236</c> hands the <c>[Rendering] VerticalFOV</c> ini read, whose clamp
    /// maximum there is <c>74</c>) divided by <c>AugustFireModeFacts.AdsZoom</c>.
    /// <c>CRANBERRY_WEAPON_ADS_FP_FOV=0</c> clears the gate; any other value retunes it without a
    /// rebuild. Because it is absolute it also applies when the player is in first person for
    /// another reason - the <c>ChangeCamera</c> key, or <c>[General] FirstPerson=1</c>.
    /// </summary>
    public float WeaponAdsFpCameraFov { get; init; } = AugustFireModeFacts.AdsFpCameraFov;

    /// <summary>
    /// <b>DEFAULT ON</b> - <c>CRANBERRY_WEAPON_ADS_TIMES=0</c> is the revert. Write
    /// <c>AugustFireModeFacts.IronSightsTimeMs</c> into <c>Weapon.ToIronSightsTime</c>
    /// (<c>def+0x38</c>) and <c>Weapon.FromIronSightsTime</c> (<c>def+0x3c</c>), the two durations
    /// <c>FUN_1422935d0:70-76</c> takes on a non-silent fire-mode switch and parks in
    /// <c>comp+0x60</c>, so the switch RAMPS instead of calling <c>FUN_14228d700</c> (instant).
    /// The value is <b>ADOPTED under D53</b> from the owner's own Z1
    /// (<c>C:\Z1\Server\Zone\ZoneWeaponDefinitions.cs:1358-1361</c>). Both fields are u32s the
    /// list-0 record already writes, so no length moves. It is dormant while
    /// <see cref="WriteAdsFirstPerson"/> is on, because that path never switches the mode.
    /// </summary>
    public bool WriteIronSightsTimes { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON (D287, wave 16)</b> - <c>CRANBERRY_WEAPON_IRONSIGHTS_ARMED=0</c> is the
    /// revert. Set <c>IRON_SIGHTS</c> (<c>rec+0x20</c> bit <c>0x04</c>) on mode index 1 of the fire
    /// groups an <em>armed</em> weapon names <b>only</b>, instead of on all 60.
    /// <para>
    /// <b>This is half of the owner's 2026-09-03 report</b> (<i>"unable to melee / use binoculars;
    /// both are shown as a reloadable object"</i>). D205 set the flag on every group because mode
    /// index 1 is the index the client's right-click sends; but the ADS zoom (D212) and
    /// <c>FORCE_FP_SCOPE</c> (D230) are both already armed-only on the same
    /// <c>AmmoTypes.ByWeaponDefinitionId</c> predicate, so D205 was the odd one out - and the
    /// consequence is visible on the wire: at 19:50:35 the owner right-clicked the BINOCULARS and
    /// the client sent <c>82 0c SwitchFireModeRequest ... mode=1 (AIM DOWN SIGHTS)</c>, and at
    /// 19:51:22 it sent the same for the FISTS (<c>logs\host-20260903-194804.log</c>). A fist and
    /// a pair of binoculars have no sights to aim down.
    /// </para>
    /// <para>
    /// One byte per unarmed mode; <b>no wire length moves</b>.
    /// </para>
    /// </summary>
    public bool IronSightsArmedOnly { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON (D288, wave 16)</b> - <c>CRANBERRY_WEAPON_MELEE_ABILITY=0</c> is the revert.
    /// Write <c>AugustWeaponTable.MeleeAbilityIdFor</c> into <c>MELEE_ABILITY_ID</c>
    /// (<c>rec+0x194</c>) on both modes of every fire group no armed weapon names.
    /// <para>
    /// <b>This is the other half of the report.</b> Cranberry has shipped <c>0</c> in
    /// <c>rec+0x194</c> and <c>rec+0x190</c> since wave 9, so not one of the 120 fire modes it
    /// declares has ever been a melee mode - the client has no way to tell a fist from a firearm
    /// with an empty magazine, which is why a fists trigger-pull produces a <c>82 01</c> pair and
    /// no attack at all. See <see cref="AugustWeaponTable.MeleeAbilityIdFor"/> for where each
    /// value comes from and for the single ruling (the fists).
    /// </para>
    /// <para>
    /// Four bytes per unarmed mode, in a <c>u32</c> the record already writes as 0; <b>no wire
    /// length moves</b>.
    /// </para>
    /// </summary>
    public bool WriteMeleeAbilityIds { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON (report 1, the fire sound)</b> - <c>CRANBERRY_WEAPON_FIRE_SOUND=0</c> is the
    /// revert. Write the client's own <c>EFFECT_GROUP</c> (<c>AugustFireModeFacts.EffectGroup</c>)
    /// into <c>rec+0x104</c> on every fire mode, instead of the <c>0</c> waves 9-16 shipped.
    /// <para>
    /// <b>This is the whole of the owner's "no noise of being shot".</b> <c>rec+0x104</c> is the
    /// composite-effect id the client queues when a weapon FIRES - the muzzle flash and the gunshot
    /// audio (<c>HasAudioOnCompositeEffect</c>). The client reads it off the live fire mode
    /// (<c>FUN_140e4a010</c> gates on it being non-zero) and, handed <c>0</c>, writes
    /// <c>"Failed to QueueCompositeEffectAtLocation due to missing effect definition for Id #0!"</c>
    /// into its own <c>PlayClient (Live).log</c> and plays nothing. The value is the client's own
    /// per-fire-group number, from <c>FireModeEffectGroups.txt</c>'s <c>GROUP_ID^fire^EFFECT_ID</c>
    /// row (the AR-15's fire group 6 is effect 105).
    /// </para>
    /// <para>
    /// Four bytes per fire mode, in a <c>u32</c> the record already writes as 0; <b>no wire length
    /// moves</b>. The RELOAD sound has no equivalent field - there is no reload composite effect in
    /// this build and the client plays reload audio off its own animation state - so it is not
    /// server-shippable and is not part of this switch.
    /// </para>
    /// </summary>
    public bool WriteFireEffect { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON (report 3)</b> - <c>CRANBERRY_WEAPON_NO_MELEE_MAG=0</c> is the revert. Ship
    /// mode 0's trigger charge as <b>0</b>, not the <c>TriggerChargeSentinel</c> (1), in the Weapon
    /// <c>ItemAdd</c> tail of a magazine-less weapon (<c>CLIP_SIZE == 0</c>: the fists, the
    /// binoculars, every melee row).
    /// <para>
    /// <b>This is the owner's "fists AND binoculars are shown as a reloadable object (they show a
    /// magazine)".</b> The client renders mode 0's charge (<c>mode+0x18</c>) as the hotbar's
    /// magazine CAPACITY, so the sentinel Cranberry forced in for a clip-0 item made each show a
    /// 1-round magazine. Zeroing it removes the display; the owner's own Z1 writes 0 there for every
    /// entry (<c>ZoneInventory.cs:3166-3172</c>). Wielding is unaffected - the trigger/wield gate
    /// reads mode <b>1</b>, whose sentinel is untouched - and a real gun (<c>CLIP_SIZE &gt; 0</c>)
    /// still ships its clip in mode 0. It changes the tail bytes of those items only, and the tail
    /// LENGTH does not move.
    /// </para>
    /// </summary>
    public bool PlainWieldNoMagazine { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON (report 3)</b> - <c>CRANBERRY_BINOCULARS_OPTIC=0</c> is the revert. Ship
    /// <c>FORCE_FP_SCOPE</c> (<c>rec+0x2dc</c>) on the PRIMARY mode of the binoculars' fire group
    /// (21), so raising them / right-clicking takes the client to the FIRST-PERSON camera and zooms
    /// - which is how a viewing optic works in retail, and the owner's "unable to use binoculars".
    /// <para>
    /// <b>This is not the same as the weapon ADS (report 4), and deliberately so.</b> Right-click on
    /// a GUN is a third-person over-the-shoulder aim (<see cref="WriteAdsFirstPerson"/> off); the
    /// binoculars are a viewing optic you look THROUGH, which is inherently first person. The
    /// mechanism is the same field the wave-15 FP ADS used (<c>FUN_14158b700</c> reads
    /// <c>FORCE_FP_SCOPE</c> off the live mode and swaps the camera via <c>FUN_14158ab50</c>), so it
    /// is [P]; that binoculars are the optic that uses it is inferred from their being the only
    /// magazine-less viewing item, and the aimed FOV is a ruling (D235). The wire length does not
    /// move - <c>rec+0x2dc</c> and the FP FOV words are bytes the record already writes as 0.
    /// </para>
    /// </summary>
    public bool BinocularsOptic { get; init; } = true;

    /// <summary>
    /// The aimed first-person field of view for the binoculars, in DEGREES.
    /// <b>RULING D235</b>: no August sheet carries a binocular zoom, so the number is Cranberry's -
    /// <c>20</c> degrees, a strong zoom (the client's own default vertical FOV is 65, so this is a
    /// roughly 3x optic, clearly tighter than the 54 the iron sights use). The lever retunes it and
    /// <c>0</c> raises the binoculars to first person without zooming.
    /// </summary>
    public float BinocularsOpticFovDegrees { get; init; } = 20.0f;

    /// <summary>
    /// <b>DEFAULT ON (D291-D293, wave 17)</b> - <c>CRANBERRY_WEAPON_FIRE_MODE_TYPES=0</c> is the
    /// revert. Write <c>TYPE</c> (<c>rec+0x24</c>, <see cref="AugustWeaponTable.FireModeTypeFor"/>)
    /// on every list-2 record: 3 (melee) on the fire groups only unarmed, non-optic, non-throwable
    /// items name, 12 (throwable) on the groups of the five <c>ITEM_CLASS 25078</c> grenades, 8
    /// (trigger item ability) on the binoculars' group when
    /// <see cref="BinocularsTriggerAbility"/> is on, and the constructor's 0 on every armed gun.
    /// <para>
    /// <b>This is the owner's 2026-09-04 report</b> (<i>"it should not say 0-0 in the hotbar for
    /// fists or binoculars as they are not reloadable"</i>) and the missing half of D288: the
    /// client's hotbar data-source getters <c>FUN_1414f31c0</c> / <c>FUN_1414a6660</c> write the
    /// <c>WeaponShouldShowAmmo</c> cell for every mode whose <c>TYPE != 3</c>, and its fire
    /// executor <c>FUN_14228d1d0</c> only reaches the <c>MELEE_ATTACK</c> event for
    /// <c>TYPE == 3</c>. A <c>MELEE_ABILITY_ID</c> on a <c>TYPE 0</c> mode is a swing the client
    /// never dispatches.
    /// </para>
    /// <para>One i8 per mode, in a byte the record already wrote as 0; <b>no wire length moves</b>.</para>
    /// </summary>
    public bool WriteFireModeTypes { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON (D293, wave 17)</b> - <c>CRANBERRY_BINOCULARS_ABILITY=0</c> is the revert.
    /// The binoculars' fire group (21) carries <c>TYPE 8</c>, the trigger-item-ability type, so a
    /// trigger pull runs the item's own <c>ACTIVATABLE_ABILITY_ID 1111157</c> (the 9-stage
    /// <c>StageMaintainOnGuid</c> row the client also gives the bows) instead of a projectile
    /// shot. The dispatch is [P]; that the binoculars are such a mode is a RULING, and the one
    /// click in docs/119's addendum settles it. Requires <see cref="WriteFireModeTypes"/>.
    /// </summary>
    public bool BinocularsTriggerAbility { get; init; } = true;

    /// <summary>Environment switch for <see cref="Throwables"/> (docs/120, D300-D304). <b>Default ON</b>.</summary>
    public const string ThrowablesVariable = "CRANBERRY_THROWABLES";

    /// <summary>Environment lever for <see cref="ThrowableSpeed"/> (D301).</summary>
    public const string ThrowableSpeedVariable = "CRANBERRY_THROWABLE_SPEED";

    /// <summary>Environment lever for <see cref="ThrowableWindupMs"/> (D304).</summary>
    public const string ThrowableWindupVariable = "CRANBERRY_THROWABLE_WINDUP_MS";

    /// <summary>
    /// <b>DEFAULT ON (docs/120, D300-D304)</b> - <c>CRANBERRY_THROWABLES=0</c> is the revert. Ship
    /// everything a grenade needs to leave the hand: the frag's ruled fire group 1404 with its two
    /// modes and its list-0 record (D300), <c>FIRE_DURATION_MS</c> on every throwable mode (D304),
    /// a list-4 row per throwable mode with the ammo key 0 (D303) and the five physics
    /// <c>ProjectileDefinitions</c> records of <see cref="AugustThrowables"/> (D301). Off restores
    /// the wave-17 blob byte for byte: the frag stays groupless and no throwable mode can reach a
    /// projectile, so a trigger pull on one produces the <c>82 01</c> pair and nothing else -
    /// the owner's 2026-09-03 21:35 session exactly.
    /// </summary>
    public bool Throwables { get; init; } = true;

    /// <summary>
    /// <c>SPEED</c> (<c>rec+0x5c</c>) of the five throwable projectiles, world units per second -
    /// <see cref="Generated.Rulings.Throwables.ThrowSpeed"/> (D301). A retune lever, not a switch.
    /// </summary>
    public float ThrowableSpeed { get; init; } = Generated.Rulings.Throwables.ThrowSpeed;

    /// <summary>
    /// <c>FIRE_DURATION_MS</c> (<c>rec+0x38</c>) on every throwable mode -
    /// <see cref="Generated.Rulings.Throwables.ThrowWindupMs"/> (D304). Must be at least 1 or the
    /// client never enters its throw state (<c>FUN_142293af0</c>); the lever floors it there.
    /// </summary>
    public int ThrowableWindupMs { get; init; } = Generated.Rulings.Throwables.ThrowWindupMs;

    /// <summary>
    /// <b>STAGE 1b - DEFAULT ON</b> (<c>CRANBERRY_WEAPON_DEFS_LIST0=0</c> is the revert). Fill
    /// list 0 of the blob with real <see cref="WeaponDefinitionRecord"/>s instead of leaving it
    /// empty.
    /// <para>
    /// <b>List 0 is THE list, and every previous plan missed it.</b>
    /// <c>ClientPlayerItemManager::CreateItem</c> hashes the item row's <c>PARAM1</c> - the
    /// <c>WEAPON_ID</c> - against list 0. Stage 1 alone therefore ships an envelope carrying 60
    /// fire groups and <em>no weapon definitions</em>, and the hash of 1374 / 1405 still misses:
    /// the client logs the identical <c>weapon definition not found for weapon ID 1374</c> it
    /// logged for the owner at 20:38:08 on 2026-08-30. docs/81's "Run 1" recipe omitted this
    /// switch and could never have worked for that reason.
    /// </para>
    /// <para>
    /// docs/58 U1 required list 0 to stay empty until <c>FUN_140a52a50</c> was decoded. It <b>is</b>
    /// decoded now (see <see cref="WeaponDefinitionRecord"/>), so the writer exists and is
    /// byte-pinned. The residual risk is unchanged and is the reason the revert exists: a record
    /// whose length is wrong misaligns the rest of the list, and <c>FUN_142291180</c> would then
    /// size a fire-group array from a garbage <c>def+0xf8</c> at gun-pickup time. With it off,
    /// <c>CreateItem</c> early-returns exactly as it did all wave 8 and writes that log line again.
    /// </para>
    /// </summary>
    public bool PopulateWeaponDefinitions { get; init; } = true;

    /// <summary>
    /// <c>Weapon.MovementModifier</c> (<c>def+0x5c</c>) written into every list-0 record. The client
    /// multiplies the local player's movement speed by it while the weapon is in the active hand
    /// (<c>FUN_14228fa20</c> → <c>FUN_1411acfa0</c>); <c>1.0</c> is its own default and <c>0</c>
    /// was the 2026-08-31 wield freeze. Not a stage - a measurement lever
    /// (<see cref="MovementModifierVariable"/>).
    /// </summary>
    public float WeaponMovementModifier { get; init; } = 1.0f;

    /// <summary>
    /// Whether list-0 records carry their own id in the body "ID" field (<c>def+0x18</c>), the
    /// value the local weapon keys its definition lookup on. Default true; false reproduces the
    /// waves-9/10 zero for one bisect run (<see cref="BodyIdVariable"/>).
    /// </summary>
    public bool WriteWeaponDefinitionBodyId { get; init; } = true;

    /// <summary>
    /// <b>STAGE 2 - DEFAULT ON</b> (<c>CRANBERRY_WEAPON_TAIL=0</c> is the revert). Write the real 68-byte Weapon
    /// <c>ItemAdd</c> tail (<see cref="WeaponItemAddTail"/>) instead of the 1-byte Generic tail, for
    /// items whose <c>CODE_FACTORY_NAME</c> is <c>Weapon</c> <em>and</em> whose fire group resolves.
    /// <para>
    /// Guard 5 stays fully on while this is the only stage enabled, so nothing is wielded and the
    /// change is invisible except as bytes: run it alone to prove the tail's field order without
    /// ever binding body slot 7. Shipped, it runs with stage 3, because the tail is what builds
    /// the fire-group array a wielded row dereferences - the two cannot be separated in a session
    /// that is meant to work.
    /// </para>
    /// </summary>
    public bool WriteWeaponItemAddTail { get; init; } = true;

    /// <summary>
    /// <b>STAGE 3 - DEFAULT ON</b> (<c>CRANBERRY_WIELD=0</c> is the revert). Narrow - never remove -
    /// <c>ActiveHandRowGuard</c> so a body-slot-7 row is permitted for an item this session has
    /// demonstrably delivered fire-group data for (<see cref="WeaponFireGroupLedger"/>).
    /// <para>
    /// This is the only stage that can crash the client, and it is the last one for that reason.
    /// It is nevertheless ON from wave 9, because with body slot 7 empty the August client's only
    /// local attack entry point (<c>FUN_1411ceca0</c>, from the input controller
    /// <c>FUN_14158ef20+0x3887</c>) resolves the active-hand slot, finds nothing and skips the
    /// whole block: no fallback, no built-in fist. Trigger, melee swing and hit report all hang
    /// off that one resolve, so an empty hand is a server that cannot be shot, swung or tested.
    /// <c>ActiveHandRowGuard</c> and <c>WeaponFireGroupLedger</c> are narrowed, never removed - a
    /// row is still refused for an item this session has not delivered fire-group data for.
    /// </para>
    /// </summary>
    public bool AllowWielding { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON (D331, docs/121 §3)</b> - <c>CRANBERRY_WEAPON_AUTO_RETAIL=0</c> is the revert.
    /// With <see cref="MarkFireGroupsAutomatic"/> on, mark the fire groups the client's own text
    /// calls automatic - <c>AugustWeaponTable.RetailAutomaticFireGroupIds</c>, the AK-47 family
    /// (locale 11959: <i>"automatic weapon"</i>) - instead of the AR-15's group 6, whose own text
    /// says <i>"pinpoint accurate assault rifle"</i> and whose retail behaviour is one round per
    /// click. Those groups' fire modes also carry the <c>AUTOMATIC</c> bit (<c>rec+0x20</c> bit
    /// <c>0x20</c>) and an <c>AUTO_FIRE_TIME_MS</c> (<c>rec+0x48</c>) equal to their
    /// <c>REFIRE_TIME_MS</c>, because <c>FUN_14228d2d0</c> reads <c>rec+0x48</c> for every shot of
    /// a hold after the second and clamps it to a 1 ms floor - an automatic group with a 0 there
    /// cycles at 1000 rounds a second from the third round on, and this server refuses every one.
    /// <para>
    /// Off restores the wave-9 diagnostic (D186) byte for byte: the AR-15 automatic, the AK-47
    /// single-shot, no auto-fire time anywhere.
    /// </para>
    /// </summary>
    public bool RetailAutomatic { get; init; } = true;

    /// <summary>
    /// Generated-table diagnostic pellet count. Ordinary sessions use
    /// <see cref="AugustShotgunPattern"/>'s 17-pellet pattern after source overlays;
    /// its reference rows, rather than the legacy random-pellet field, drive the native loop.
    /// </summary>
    public int ShotgunPellets { get; init; } = Combat.RetailBalance.RetailShotgunPellets;

    /// <summary>
    /// <b>D332, RULING.</b> <c>PELLET_SPREAD</c> (<c>rec+0x7c</c>) on the same modes, in
    /// <b>degrees</b> - the unit is [P] from the spawn's pattern branch, which multiplies the same
    /// word by <c>pi/180</c> (<c>FUN_140c7bb60:523-524</c>). No August sheet and no Z1 value
    /// carries a spread (his hand-written writer ships 0), so the number is Cranberry's: <b>4.0</b>,
    /// a half-angle that puts every pellet on a torso inside the 5 m full-damage band and spreads
    /// them over ~1.4 m at 20 m. Captured-table sessions retain their 2.35/1.35 degree hip/ADS
    /// values. With systematic patterns enabled, zero spread collapses the pattern to one line.
    /// </summary>
    public float ShotgunSpreadDegrees { get; init; } = 4.0f;

    /// <summary>
    /// <b>DEFAULT ON, and it is a DIAGNOSTIC, not a feature</b>
    /// (<c>CRANBERRY_WEAPON_AUTOMATIC=0</c> clears it). Mark fire group 6 automatic, as
    /// <c>AugustWeaponTable.AutomaticFireGroupIds</c> always has.
    /// <para>
    /// docs/95 (the wave-10 follow-up, D186): this single bit is what the client's per-frame
    /// active-hand block branches on
    /// (<c>FUN_1411ceca0</c>: <c>FUN_14228d840(item+0xa8)-&gt;+0x38 &amp; 0x40</c>), and the two
    /// arms of that branch share almost no code. Group 6 belongs to the AR-15 (item 10, WEAPON_ID
    /// 6) — the exact gun that froze the owner's input on 2026-08-31 with two different binding
    /// packets. Running one match with this off says which arm of the branch the freeze lives in,
    /// which is worth more than another hypothesis.
    /// </para>
    /// <para>
    /// It is not a fix either way: with it off, an AR-15 that does work will fire single shots.
    /// </para>
    /// </summary>
    public bool MarkFireGroupsAutomatic { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON</b> (<c>CRANBERRY_WEAPON_TAIL_STATE=0</c> is the revert). Write Z1's idle
    /// scalars in the Weapon <c>ItemAdd</c> tail instead of the block of zeros
    /// (<see cref="WeaponItemAddTail.IdleState"/>).
    /// <para>
    /// S6 §2.4 / §7.2: Cranberry has always left <c>comp+0x44</c> - the weapon state machine - at
    /// <b>0</b>, and 0 is not in the client's own "not busy" set <c>{1,3,9,0xb,0xc,0xe}</c>
    /// (<c>FUN_142291930</c>). <c>+0x6c</c> and <c>+0x70</c> stayed at 0 where the client's own
    /// <c>82 1c Reset</c> handler (<c>FUN_142293450</c>, S5c §5.3) writes <b>-1</b>, and
    /// <c>+0xe8</c> at 0 where Z1 writes <c>0xffffffff</c>. Z1's tail carries 1 / -1 / -1 / -1 and
    /// is click-proven on 1087; the 23-byte scalar block is the same width on both builds, so this
    /// is a value cross under D53, not a layout change. The wire length does not move.
    /// </para>
    /// </summary>
    public bool TailIdleState { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON</b> (<c>CRANBERRY_WEAPON_TAIL_AMMO=0</c> is the revert). Write the Weapon
    /// <c>ItemAdd</c> tail's ammo-slot array as <c>n0 = 1</c> plus the rounds in the magazine
    /// (<see cref="WeaponItemAddTail.AmmoSlot"/> / <see cref="WeaponItemAddTail.Magazine"/>)
    /// instead of the empty <c>n0 = 0</c> waves 5-12 shipped.
    /// <para>
    /// <b>This is the field the hotbar magazine reads, and it is the only one.</b> The owner's three
    /// 2026-09-02 sessions contain zero <c>11 03 ItemUpdate</c> and zero s2c <c>0x82</c>, so the one
    /// number the client ever had for "rounds in this gun" was the empty array in the <c>11 02</c> -
    /// hence "the counter reads 0 forever" (FIRE-PATH-DIAGNOSIS §1.6, rank 2b). The owner's Z1
    /// writes <c>ammoSlotsCount = 1</c> + <c>ammoInMagazine</c> in exactly this position
    /// (<c>ZoneInventory.cs:3140-3160</c>, S6 §2.4), so the shape crosses under D53 and the reader
    /// is the client's own <c>FUN_141484a20</c>.
    /// </para>
    /// <para>
    /// <b>Unlike every other tail switch this one changes the tail's LENGTH</b> (68 -> 72 bytes for
    /// a one-fire-group weapon). Over-writing a tail is harmless and under-writing is fatal
    /// (<see cref="SendWeaponDefinitions"/> caution 2), and this adds four bytes the client's own
    /// reader asks for by count, so the direction is the safe one - but it is a wire-length change
    /// on the pickup path, which is why it has its own revert.
    /// </para>
    /// </summary>
    public bool WriteWeaponTailMagazine { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON</b> (<c>CRANBERRY_WEAPON_STANCE=0</c> is the revert). Send
    /// <c>0f 20 Character.WeaponStance {selfGuid, 1}</c> once after the world session's first
    /// <c>a0 05</c> and after every weapon draw, until the client sends a <c>0f 20</c> of its own.
    /// <para>
    /// S6 §7.3 and the owner's own round-26 finding (<c>C:\Z1\Server\Zone\ZoneAbilities.cs</c>
    /// :770-856): a client that is never given a stance never leaves stance 0, never enters a fire
    /// state, and reports "cannot shoot / reload". The id is
    /// <c>cCharacterPacketIdWeaponStance</c> at levels <c>[15, 32]</c> in
    /// <c>out/registrations-1148.json</c> and the bridge marks the 1087 row <c>identical</c>, so
    /// the 14-byte body ports unchanged. Self-limiting by construction: the client answering once
    /// stops the re-assert for the session.
    /// </para>
    /// </summary>
    public bool SendWeaponStance { get; init; } = true;

    /// <summary>
    /// <b>D313 - DEFAULT <see cref="WeaponTableSource.Captured"/></b>
    /// (<c>CRANBERRY_WEAPON_TABLE=generated</c> is the whole-feature revert). The 2026-09-04 20:12
    /// crash at the zoning table send was the 72-byte list-3 element (73 on the client: one byte
    /// of <c>FLAGS</c> at <c>elem+0x20</c>), fixed the same evening - docs/123 §7. Which
    /// <c>WeaponDefinitions</c> table goes on the wire.
    /// <para>
    /// <c>captured</c> keeps every generated list 0, 1 and 4 record - August's own weapon ids, fire
    /// groups, ammo slots and projectile mappings - and crosses the friend's captured 1087 numbers
    /// into list 2, then fills lists 3 and 5 from the capture (<see cref="CapturedWeaponTable"/>,
    /// docs/123). Recoil, cone of fire, the reload clock and the pellet pattern become his.
    /// </para>
    /// <para>
    /// <b>The generated-only rulings that the crossing supersedes, on the modes it reaches:</b>
    /// D331 (the automatic set - the capture's own <c>AUTOMATIC</c> bit ships instead), D332 (the
    /// 12GA's pellets and spread), and any list-2 flag the capture carries in <c>rec+0x20</c> or
    /// <c>rec+0x21</c> including <c>IRON_SIGHTS</c> (D287). <b>Not superseded</b>, because they are
    /// not crossed: D291-D293 (<c>TYPE</c>: the 1087 <c>TYPE</c> enum is not this build's), the
    /// binoculars' ability and optic (D235/D293), the ADS zoom and first-person camera
    /// (D212/D230), <c>EFFECT_GROUP</c> (D234), and the ammo item ids (D196). On a fire mode the
    /// capture has nothing for - every unarmed group, the throwables, the fists - <b>nothing
    /// changes at all</b>.
    /// </para>
    /// </summary>
    public WeaponTableSource WeaponTable { get; init; } = WeaponTableSource.Captured;

    /// <summary>Use 150 ms native equip transitions for the AR-15, AK-47 and shotgun.
    /// The captured baseline remains available with CRANBERRY_FAST_LONG_GUN_DRAW=0.</summary>
    public bool FastLongGunDraw { get; init; } = true;

    /// <summary>Adapt the recorded Z1BR firearm profile, excluding both shotgun identities.</summary>
    public bool Z1LiveGunplay { get; init; } = true;

    /// <summary>
    /// <b>D314 - DEFAULT <see cref="ProjectileTableSource.Z1"/></b>
    /// (<c>CRANBERRY_PROJECTILE_TABLE=august</c> is the revert). <c>z1</c> ships all 131 rows of the
    /// owner's own <c>projectileDefinitions.json</c> - models, speeds, flight types, tracers,
    /// gravity and drag - in place of Cranberry's 14 constructor-default records with no model and
    /// no effect. The five throwable records ride either table unchanged (docs/123 §6).
    /// </summary>
    public ProjectileTableSource ProjectileTable { get; init; } = ProjectileTableSource.Z1;

    /// <summary>
    /// All stages off - the wave-5 behaviour, byte for byte. <b>No longer equal to
    /// <see cref="Default"/></b>: wave 9 flipped the four stage defaults on, so every field is
    /// written out here or this record silently stops being the control it was built to be.
    /// </summary>
    public static WeaponStageOptions AllOff { get; } = new()
    {
        SendWeaponDefinitions = false,
        PopulateFireGroups = false,
        PopulateWeaponDefinitions = false,
        PopulateFireModes = false,
        SendProjectileDefinitions = false,
        WriteWeaponItemAddTail = false,
        AllowWielding = false,
        // Not a stage: the diagnostic bit keeps its shipped value so AllOff stays the wave-5
        // CONTROL rather than silently becoming a second experiment.
        MarkFireGroupsAutomatic = true,
        // Wave 12: both of these change bytes on the wire, so the wave-5 control has to clear them
        // explicitly or it stops being a control.
        TailIdleState = false,
        SendWeaponStance = false,
        // Wave 13: this one changes the tail's LENGTH, so the wave-5 control has to clear it or it
        // stops being a byte-for-byte control.
        WriteWeaponTailMagazine = false,
        // Wave 14: the ammo slot changes a list-0 record's LENGTH and list 4 adds records, so both
        // have to be cleared here for the same reason.
        PopulateAmmoSlots = false,
        PopulateFireModeProjectiles = false,
        // Wave 14 addendum: the ADS zoom changes four bytes of a shipped record, so the wave-5
        // control has to clear it or it stops being a byte-for-byte control.
        WriteAdsZoom = false,
        // Wave 15: FORCE_FP_SCOPE, the FP camera FOV gate and the iron-sights ramp each change
        // shipped bytes, so the wave-5 control has to clear all three for the same reason.
        WriteAdsFirstPerson = false,
        WriteIronSightsTimes = false,
        // Wave 16: both change shipped bytes of list 2, so the wave-5 control clears them too.
        IronSightsArmedOnly = false,
        WriteMeleeAbilityIds = false,
        // Report 1: the fire effect changes four bytes of every armed fire mode, so the wave-5
        // control clears it or it stops being a byte-for-byte control.
        WriteFireEffect = false,
        // Report 3: moot with the tail off (wave 5 sent no tail), but set to the pre-report-3
        // sentinel behaviour for clarity - the tail switch above is what makes it byte-for-byte.
        PlainWieldNoMagazine = false,
        // Report 3 (binoculars optic): FORCE_FP_SCOPE on group 21 changes shipped bytes, so the
        // wave-5 control clears it too.
        BinocularsOptic = false,
        // Wave 17: TYPE changes one byte of every melee, throwable and optic mode, so the wave-5
        // control clears both TYPE switches or it stops being a byte-for-byte control.
        WriteFireModeTypes = false,
        BinocularsTriggerAbility = false,
        // docs/121: the retail automatic set moves list-1 flags and list-2 words, and the shotgun
        // pellets move two list-2 words, so the wave-5 control clears all three.
        RetailAutomatic = false,
        ShotgunPellets = 0,
        ShotgunSpreadDegrees = 0.0f,
        // docs/120: the throwables add a fire group, a weapon record, two modes, ten list-4 rows
        // and five projectile records, so the wave-5 control clears them too.
        Throwables = false,
        // docs/123: both tables change shipped bytes (list 2's words, lists 3 and 5 going from
        // empty to populated, and every projectile record's body), so the wave-5 control clears
        // both or it stops being a byte-for-byte control.
        WeaponTable = WeaponTableSource.Generated,
        ProjectileTable = ProjectileTableSource.August,
    };

    /// <summary>
    /// The shipped default: every stage <b>on</b>, i.e. a weapon the client can build, hold and
    /// fire. <c>CRANBERRY_WEAPON_DEFINITIONS=0</c> is the whole-feature revert.
    /// </summary>
    public static WeaponStageOptions Default { get; } = new();

    /// <summary>
    /// The options after the two ordering rules are applied. Nothing else is altered.
    /// <list type="number">
    /// <item><see cref="AllowWielding"/> is dropped without <see cref="WriteWeaponItemAddTail"/>,
    /// because a slot-7 row without the tail that builds the fire-group array is the docs/45
    /// minidump.</item>
    /// <item><see cref="AllowWielding"/> is dropped without a stage 1 that actually carries fire
    /// groups (<see cref="SendWeaponDefinitions"/> <em>and</em> <see cref="PopulateFireGroups"/>),
    /// because the client resolves a tail's group id against that list and <c>FUN_14228d840</c>
    /// returns 0 for an id it was never sent - the same null deref. <c>WeaponFireGroupLedger</c>
    /// already refuses to clear anything in that case, so this rule makes the invariant explicit
    /// rather than implicit, and stops the boot log claiming stage 3 is live when nothing can ever
    /// be wielded. It became load-bearing the moment stage 1 stopped defaulting on.</item>
    /// </list>
    /// </summary>
    public WeaponStageOptions Effective =>
        WieldingRefusedForMissingTail || WieldingRefusedForMissingTable
            ? this with { AllowWielding = false }
            : this;

    /// <summary>
    /// True when <see cref="Effective"/> had to drop <see cref="AllowWielding"/> - the caller should
    /// say so on the boot log rather than let the owner think stage 3 is live.
    /// </summary>
    public bool WieldingRefusedForMissingTail => AllowWielding && !WriteWeaponItemAddTail;

    /// <summary>
    /// True when <see cref="Effective"/> had to drop <see cref="AllowWielding"/> because no
    /// non-empty fire-group table is being sent for a tail's group id to resolve against.
    /// </summary>
    public bool WieldingRefusedForMissingTable =>
        AllowWielding && (!SendWeaponDefinitions || !PopulateFireGroups);

    /// <summary>
    /// Reads the four switches from the environment. Only the exact string <c>"1"</c> flips a
    /// switch; anything else (including <c>"true"</c>) leaves the default, so a typo can never
    /// silently enable a stage that can crash the client.
    /// </summary>
    public static WeaponStageOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= System.Environment.GetEnvironmentVariable;
        return new WeaponStageOptions
        {
            SendWeaponDefinitions = Switch(read, SendDefinitionsVariable, @default: true),
            PopulateWeaponDefinitions = Switch(read, PopulateDefinitionsVariable, @default: true),
            PopulateFireGroups = Switch(read, PopulateFireGroupsVariable, @default: true),
            PopulateFireModes = Switch(read, PopulateFireModesVariable, @default: true),
            SendProjectileDefinitions = Switch(read, ProjectileDefinitionsVariable, @default: true),
            PopulateAmmoSlots = Switch(read, PopulateAmmoSlotsVariable, @default: true),
            PopulateFireModeProjectiles =
                Switch(read, PopulateFireModeProjectilesVariable, @default: true),
            WriteAdsZoom = Switch(read, AdsFovVariable, @default: true),
            WeaponAdsZoom = Number(read, AdsZoomVariable, @default: AugustFireModeFacts.AdsZoom),
            WriteAdsFirstPerson =
                Switch(read, AdsFirstPersonVariable, @default: AugustFireModeFacts.AdsFirstPerson),
            WeaponAdsFpCameraFov =
                Number(read, AdsFpFovVariable, @default: AugustFireModeFacts.AdsFpCameraFov),
            WriteIronSightsTimes = Switch(read, IronSightsTimesVariable, @default: true),
            IronSightsArmedOnly = Switch(read, IronSightsArmedOnlyVariable, @default: true),
            WriteMeleeAbilityIds = Switch(read, MeleeAbilityVariable, @default: true),
            WriteFireEffect = Switch(read, FireEffectVariable, @default: true),
            PlainWieldNoMagazine = Switch(read, PlainWieldNoMagazineVariable, @default: true),
            BinocularsOptic = Switch(read, BinocularsOpticVariable, @default: true),
            BinocularsOpticFovDegrees = Number(read, BinocularsOpticFovVariable, @default: 20.0f),
            WriteFireModeTypes = Switch(read, FireModeTypesVariable, @default: true),
            BinocularsTriggerAbility = Switch(read, BinocularsTriggerAbilityVariable, @default: true),
            RetailAutomatic = Switch(read, RetailAutomaticVariable, @default: true),
            ShotgunPellets = (int)Number(
                read, ShotgunPelletsVariable, @default: Combat.RetailBalance.RetailShotgunPellets),
            ShotgunSpreadDegrees = Number(read, ShotgunSpreadVariable, @default: 4.0f),
            Throwables = Switch(read, ThrowablesVariable, @default: true),
            ThrowableSpeed = Number(read, ThrowableSpeedVariable, @default: Generated.Rulings.Throwables.ThrowSpeed),
            ThrowableWindupMs = Math.Max(1, (int)Number(
                read, ThrowableWindupVariable, @default: Generated.Rulings.Throwables.ThrowWindupMs)),
            WriteWeaponItemAddTail = Switch(read, TailVariable, @default: true),
            AllowWielding = Switch(read, WieldVariable, @default: true),
            MarkFireGroupsAutomatic = Switch(read, AutomaticVariable, @default: true),
            TailIdleState = Switch(read, TailIdleStateVariable, @default: true),
            WriteWeaponTailMagazine = Switch(read, TailAmmoVariable, @default: true),
            SendWeaponStance = Switch(read, WeaponStanceVariable, @default: true),
            WeaponMovementModifier = Number(read, MovementModifierVariable, @default: 1.0f),
            WriteWeaponDefinitionBodyId = Switch(read, BodyIdVariable, @default: true),
            FastLongGunDraw = Switch(read, FastLongGunDrawVariable, true),
            Z1LiveGunplay = Switch(read, Z1LiveGunplayVariable, true),
            WeaponTable = read(WeaponTableVariable) switch
            {
                "generated" => WeaponTableSource.Generated,
                "captured" => WeaponTableSource.Captured,
                _ => WeaponTableSource.Captured,
            },
            ProjectileTable = read(ProjectileTableVariable) switch
            {
                "august" => ProjectileTableSource.August,
                "z1" => ProjectileTableSource.Z1,
                _ => ProjectileTableSource.Z1,
            },
        };
    }

    /// <summary>
    /// A float lever: only a value that parses as an invariant-culture float and is finite is
    /// accepted; anything else leaves the default, for the same reason <c>Switch</c> ignores typos.
    /// </summary>
    private static float Number(Func<string, string?> read, string name, float @default) =>
        float.TryParse(
            read(name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float value) && float.IsFinite(value)
            ? value
            : @default;

    /// <summary>A one-line boot-log description, so a play-test can never guess which stages ran.</summary>
    public string Describe()
    {
        WeaponStageOptions effective = Effective;
        string refused = WieldingRefusedForMissingTail
            ? $" — {WieldVariable}=1 IGNORED: stage 3 needs {TailVariable}=1, which supplies the fire groups"
            : WieldingRefusedForMissingTable
                ? $" — {WieldVariable}=1 IGNORED: stage 3 needs {SendDefinitionsVariable}=1 with a non-empty "
                    + $"list 1 ({PopulateFireGroupsVariable}), which is what a tail's group id resolves against"
                : string.Empty;
        // docs/123: the table line says which table shipped and, when it is the capture, which
        // generated-only rulings it supersedes - so a play-test can never guess.
        string table = effective.WeaponTable == WeaponTableSource.Captured
            ? $"table=captured ({CapturedWeaponFacts.WeaponRowCount} defs / "
                + $"{CapturedWeaponFacts.FireGroupRowCount} groups / "
                + $"{CapturedWeaponFacts.FireModeRowCount} modes / "
                + $"{CapturedWeaponFacts.ConeOfFireRowCount} player-state / "
                + $"{CapturedWeaponFacts.AimAssistRowCount} aim-assist"
                + $" crossed into lists 2/3/5, +{CapturedWeaponTable.AugustOnlyWeaponDefinitionIds.Count}"
                + " August fills; lists 0/1/4 generated;"
                + " D331 automatic and D332 pellets NOT applied to a crossed mode)"
            : "table=generated (D291-D293 TYPE, binoculars ability, D331 automatic, D332 pellets applied)";
        string projectiles = effective.ProjectileTable == ProjectileTableSource.Z1
            ? $"projectileTable=z1 ({Z1ProjectileFacts.RowCount} rows, "
                + $"{Z1ProjectileTable.ZeroedEffectIds.Count} unknown effect id(s) zeroed, "
                + $"{Z1ProjectileTable.BlankedModelNames.Count} model(s) August lacks blanked)"
            : "projectileTable=august (14 constructor-default records)";

        return $"weapon stages: {table}, {projectiles}, "
            + $"definitions={On(effective.SendWeaponDefinitions)} "
            + $"(list0={On(effective.PopulateWeaponDefinitions)}, "
            + $"list1={On(effective.PopulateFireGroups)}, "
            + $"list2={On(effective.PopulateFireModes)}, "
            + $"list4={On(effective.PopulateFireModeProjectiles)}, "
            + $"ammoSlots={On(effective.PopulateAmmoSlots)}), "
            + $"projectiles={On(effective.SendProjectileDefinitions)}, "
            + $"itemAddTail={On(effective.WriteWeaponItemAddTail)}, "
            + $"wielding={On(effective.AllowWielding)}, "
            + $"automatic={On(effective.MarkFireGroupsAutomatic)}, "
            + $"autoRetail={(effective.MarkFireGroupsAutomatic && effective.RetailAutomatic ? "AK-47" : effective.MarkFireGroupsAutomatic ? "AR-15(diag)" : "off")}, "
            + $"shotgunPellets={effective.ShotgunPellets}, "
            + $"shotgunSpread={effective.ShotgunSpreadDegrees.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}deg, "
            + $"fastLongGunDraw={On(effective.FastLongGunDraw && effective.WeaponTable == WeaponTableSource.Captured)}, "
            + $"tailIdleState={On(effective.TailIdleState)}, "
            + $"tailMagazine={On(effective.WriteWeaponTailMagazine)}, "
            + $"weaponStance={On(effective.SendWeaponStance)}, "
            + $"movementModifier={effective.WeaponMovementModifier.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, "
            + $"adsZoom={(effective.WriteAdsZoom ? effective.WeaponAdsZoom : 1.0f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, "
            + $"adsFirstPerson={On(effective.WriteAdsFirstPerson)}, "
            + $"adsFpFov={(effective.WriteAdsFirstPerson ? effective.WeaponAdsFpCameraFov : 0.0f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, "
            + $"ironSightsMs={(effective.WriteIronSightsTimes ? AugustFireModeFacts.IronSightsTimeMs : 0)}, "
            + $"ironSightsArmedOnly={On(effective.IronSightsArmedOnly)}, "
            + $"meleeAbility={On(effective.WriteMeleeAbilityIds)}, "
            + $"fireSound={On(effective.WriteFireEffect)}, "
            + $"noMeleeMag={On(effective.PlainWieldNoMagazine)}, "
            + $"binocularsOptic={On(effective.BinocularsOptic)}, "
            + $"fireModeTypes={On(effective.WriteFireModeTypes)}, "
            + $"binocularsAbility={On(effective.WriteFireModeTypes && effective.BinocularsTriggerAbility)}, "
            + $"throwables={On(effective.Throwables)}"
            + (effective.Throwables
                ? $" (speed={effective.ThrowableSpeed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, windupMs={effective.ThrowableWindupMs})"
                : string.Empty)
            + $", bodyId={On(effective.WriteWeaponDefinitionBodyId)}{refused}";

        static string On(bool value) => value ? "ON" : "off";
    }

    private static bool Switch(Func<string, string?> read, string name, bool @default) =>
        read(name) switch
        {
            "1" => true,
            "0" => false,
            _ => @default,
        };
}
