namespace Cranberry.Zone.Combat;

/// <summary>The <c>0x82</c> family header, shared by every sub.</summary>
/// <param name="Sub">The sub-id byte at offset 5.</param>
/// <param name="GameTime">
/// The u32 at <c>[1..5]</c>. docs/20 §2 proved the client's own receiver reads it and never uses
/// it, and left its meaning open (open question 2). The owner's recovered 1087 <b>sender</b>
/// (<c>FUN_14063dac0</c>) writes the game time there, so this is read as the client's clock and
/// logged. Cranberry keeps writing 0 outbound, because no receive case reads it back.
/// </param>
public readonly record struct WeaponBaseHeader(byte Sub, uint GameTime);

/// <summary><c>82 01 FireStateUpdate</c> - the trigger going down or coming up.</summary>
/// <param name="WeaponGuid">The item instance, not a character.</param>
/// <param name="FireState">64 means the magazine is dry.</param>
/// <param name="Unknown">The trailing byte, carried and logged, never acted on.</param>
public readonly record struct FireStateUpdate(ulong WeaponGuid, byte FireState, byte Unknown);

/// <summary>
/// <c>82 28 AmmoCountAcknowledge</c> - the client telling this server that the magazine it was just
/// handed in <c>82 08</c> is not the one the client itself has.
/// </summary>
/// <param name="WeaponGuid">The item instance whose magazine disagrees.</param>
/// <param name="ClientAmmo">What the client counts. This is what the player can see, so it wins.</param>
/// <param name="ServerAmmo">The <c>ammoCount</c> field of the <c>82 08</c>, echoed back.</param>
public readonly record struct AmmoCountAcknowledge(ulong WeaponGuid, int ClientAmmo, uint ServerAmmo);

/// <summary>
/// <c>82 0c SwitchFireModeRequest</c> - <b>the ADS packet</b>, and the busiest c2s weapon packet
/// after the trigger itself: 145 of them in the owner's three 2026-09-02 sessions, one on every
/// right-click press and one on every release (FIRE-PATH-DIAGNOSIS §1.3, §1.3a).
/// </summary>
/// <param name="WeaponGuid">The wielded item instance - matched to the draw the server logged.</param>
/// <param name="FireGroupIndex">Observed 0 in all 145 packets.</param>
/// <param name="FireModeIndex">
/// <b>1 = aim down sights, 0 = hip.</b> Observed only as 0 or 1; session B's 14/15 split is a
/// press/release pairing. The owner's Z1 names mode 1 as the ADS mode in
/// <c>ZoneCombat.cs:1786-1793</c>.
/// </param>
/// <param name="Unknown">
/// The trailing byte 1148 adds to 1087's 10-byte body. <b>Not a constant by construction</b>: the
/// client's own writer <c>FUN_14148c1c0:48</c> fills it from <c>comp-&gt;vtable+0x70()</c>, a weapon
/// component predicate (the same one the reload gate <c>FUN_1411c74b0</c> consults). It happened to
/// be 0 in all 145 captured packets, which is why it is carried and logged rather than asserted -
/// the first non-zero one is evidence, not a decode failure.
/// </param>
public readonly record struct SwitchFireModeRequest(
    ulong WeaponGuid, byte FireGroupIndex, byte FireModeIndex, byte Unknown);

/// <summary><c>82 03 Fire</c> - one trigger pull, and one projectile id per pellet.</summary>
/// <param name="WeaponGuid">The item instance that fired.</param>
/// <param name="X">Muzzle point.</param>
/// <param name="Y">Muzzle point.</param>
/// <param name="Z">Muzzle point.</param>
/// <param name="ProjectileIds">
/// <b>Every</b> id in the array, one per pellet. Reading the array's first three u32s as three
/// scalars is byte-identical whenever the count is 1 and silently truncates a shotgun - the owner's
/// own note, and the part a naive port gets wrong.
/// </param>
public readonly record struct WeaponFire(ulong WeaponGuid, float X, float Y, float Z, uint[] ProjectileIds);

/// <summary>
/// <c>82 19 GuidedExplode</c>, c2s - <b>the client's own detonation report</b> (docs/120 §2.5).
/// The projectile actor sends it from <c>FUN_140ef51b0</c> when a <c>LIFESPAN_DETONATE</c>
/// projectile's lifespan runs out (the actor tick <c>FUN_140ef15d0:1470-1486</c>) or a
/// <c>DETONATE_ON_CONTACT</c> one lands (<c>:1057</c>), once per projectile (<c>actor+0x652</c>
/// bit <c>0x04</c>). Serializer <c>FUN_140eec1f0</c>: header, then <c>packed u32; packed u32;
/// u32 projectileId; packed u32; f32 x,y,z; f32 x,y,z; u8</c>.
/// </summary>
/// <param name="OwnerNetworkId"><c>obj+0x20</c>: <c>FUN_140b2a410(actor+0x4c0)</c>, an entity network id (<c>entity+0x220</c>). [P shape, I meaning]</param>
/// <param name="SecondNetworkId"><c>obj+0x24</c>: the same conversion of <c>actor+0x4c8</c>. [U meaning]</param>
/// <param name="ProjectileId"><c>obj+0x2c</c>: <c>actor+0x3b0</c>, the id the paired <c>82 03 Fire</c> named. [P]</param>
/// <param name="TargetNetworkId"><c>obj+0x28</c>: the conversion of <c>actor+0x4e0</c>, the guid the tick compares with the null guid. [U meaning]</param>
/// <param name="X">The detonation position (<c>actor+0x590</c>). [P: the second float3 is a direction, this one is where the actor IS]</param>
/// <param name="Y"><inheritdoc cref="X"/></param>
/// <param name="Z"><inheritdoc cref="X"/></param>
/// <param name="DirectionX">The second float3 (<c>actor+0x5d0</c>). [U meaning]</param>
/// <param name="DirectionY"><inheritdoc cref="DirectionX"/></param>
/// <param name="DirectionZ"><inheritdoc cref="DirectionX"/></param>
/// <param name="Trailer">The closing <c>u8</c>; the builder writes 1.</param>
public readonly record struct GuidedExplode(
    uint OwnerNetworkId,
    uint SecondNetworkId,
    uint ProjectileId,
    uint TargetNetworkId,
    float X,
    float Y,
    float Z,
    float DirectionX,
    float DirectionY,
    float DirectionZ,
    byte Trailer);

/// <summary>
/// <c>82 26 GrenadeBounceReport</c>, c2s - one per bounce loud enough to hear (docs/120 §2.4).
/// The projectile manager tick <c>FUN_140f034a0:1000-1066</c> sends it when a physics
/// projectile's contact speed passes <c>Audio.MinGrenadeBounceVelocity</c>, with
/// <c>Audio.GrenadeBounceEffectId</c>; serializer <c>FUN_140ef6680</c>: header, then
/// <c>u32 projectileId; u32 effectId; u64 characterGuid</c>. The order matches the owner's own
/// 1087 read (<c>ZoneCombatThrowables.OnBounceReport</c>), now proved from the August binary.
/// </summary>
public readonly record struct GrenadeBounceReport(uint ProjectileId, uint EffectId, ulong CharacterGuid);

/// <summary>
/// <c>82 20 WeaponFireHint</c> - <b>the direction half of a shot</b>, sent bare beside the
/// <c>82 1f</c>-wrapped <c>82 03 Fire</c> that carries the same muzzle point and the same projectile
/// ids (docs/107 §10, 2026-09-03 addendum). It is not a second trigger pull and must never spend a
/// second round; it is the only c2s packet in this family that says which way the shot went, which
/// is why it is decoded rather than dropped.
/// <para>
/// <b>The client builds both from one function.</b> <c>FUN_140e74b30</c> fills a <c>sub 0x03</c>
/// object and a <c>sub 0x20</c> object from the same origin vector on one trigger pull, pushes the
/// <c>0x03</c> into the <c>MultiWeapon</c> batch and sends the <c>0x20</c> standalone through
/// <c>FUN_140e5ca70</c>. That is why the pair always arrives as "wrapped Fire, then bare hint".
/// </para>
/// </summary>
/// <param name="WeaponGuid">The item instance that fired - identical to the paired <c>82 03</c>.</param>
/// <param name="Marker">The byte between the guid and the muzzle point; 255 in all 53 captured hints.</param>
/// <param name="X">Muzzle point, identical to the paired <c>82 03</c>'s.</param>
/// <param name="Y">Muzzle point.</param>
/// <param name="Z">Muzzle point.</param>
/// <param name="Hints">
/// <b>One entry per projectile</b>, in the order the client pushed them. A single trigger pull of a
/// rifle carries one; a shotgun blast carries one per pellet. Reading the first entry's fields as
/// flat packet fields is byte-identical while the count is 1 and silently truncates a shotgun - the
/// same trap <see cref="WeaponFire"/>'s array carries, in the same family.
/// </param>
public readonly record struct WeaponFireHint(
    ulong WeaponGuid,
    byte Marker,
    float X,
    float Y,
    float Z,
    IReadOnlyList<WeaponFireHintEntry> Hints);

/// <summary>
/// One projectile inside a <c>82 20 WeaponFireHint</c>: which way it went, and who the client
/// thinks it might be going at.
/// </summary>
/// <param name="ProjectileId">
/// The client's own projectile id - the SAME value the paired <c>82 03 Fire</c> put in its array and
/// the one a later <c>82 06 ProjectileHitReport</c> names. Ascending from 1 across a session.
/// </param>
/// <param name="DirectionX">Flight direction. A unit vector in 52 of 53 captured hints; all-zero in
/// the 53rd, which is a real value the reader accepts rather than a decode failure.</param>
/// <param name="DirectionY">Flight direction.</param>
/// <param name="DirectionZ">Flight direction.</param>
/// <param name="CandidateTargets">
/// <b>Characters the client thinks this projectile may be flying at</b>, as world guids - the nested
/// counted list the sender fills from the shot's own candidate walk. Empty in every captured hint
/// of the 17:51 session, because the owner was alone in the match.
/// </param>
public readonly record struct WeaponFireHintEntry(
    uint ProjectileId,
    float DirectionX,
    float DirectionY,
    float DirectionZ,
    IReadOnlyList<ulong> CandidateTargets)
{
    /// <summary>The direction's length, so a caller can say whether it was a real aim.</summary>
    public float DirectionLength =>
        MathF.Sqrt((DirectionX * DirectionX) + (DirectionY * DirectionY) + (DirectionZ * DirectionZ));

    /// <summary>True when the client sent no direction at all.</summary>
    public bool DirectionIsZero => DirectionX == 0f && DirectionY == 0f && DirectionZ == 0f;
}

/// <summary><c>82 06 ProjectileHitReport</c> - the client claiming one projectile hit something.</summary>
/// <param name="ProjectileId">Matches an id from the <see cref="WeaponFire"/> array.</param>
/// <param name="CharacterId">Who was hit.</param>
/// <param name="X">Impact point.</param>
/// <param name="Y">Impact point.</param>
/// <param name="Z">Impact point.</param>
/// <param name="HitLocation">The literal bone name, or empty in the string-table form.</param>
/// <param name="HitLocationHeader">The raw <c>SoeUtil::IString</c> u16, so the log can show the form.</param>
/// <param name="UnknownDword">Carried and logged; its meaning is not established.</param>
/// <param name="HitEntryCount">Entries in the trailing 20-byte array.</param>
/// <param name="TotalShotCount">The sender's own shot counter.</param>
/// <param name="Flags">Bit 0x80 is set by the sender.</param>
public readonly record struct ProjectileHitReport(
    uint ProjectileId,
    ulong CharacterId,
    float X,
    float Y,
    float Z,
    string HitLocation,
    ushort HitLocationHeader,
    uint UnknownDword,
    int HitEntryCount,
    byte TotalShotCount,
    byte Flags)
{
    /// <summary>True when the client sent a string-table id instead of text; then
    /// <see cref="HitLocation"/> is empty and <see cref="HitLocationId"/> carries the id.</summary>
    public bool HitLocationIsDictionaryId => (HitLocationHeader & 0x8000) != 0;

    /// <summary>The string-table id, when <see cref="HitLocationIsDictionaryId"/>.</summary>
    public int HitLocationId => HitLocationHeader & 0x7fff;

    /// <summary>
    /// D315: the report names no entity - the projectile hit the WORLD. The position is still the
    /// client's own impact point, which is what a thrown grenade needs and what nothing may take
    /// damage from.
    /// </summary>
    public bool HitTheWorld => CharacterId == 0;
}

/// <summary>
/// <c>82 21 ProjectileContactReport</c> - <b>the client's own contact</b>, and the packet that
/// says where a grenade actually landed when no <c>82 19 GuidedExplode</c> follows (docs/125 §2).
/// <para>
/// 75 body bytes at 1148, derived from the 2026-09-04 19:09:13.259 molotov (host log
/// <c>host-20260904-190216.log</c>): <c>u32 projectileId; u64 characterId; f32 qx,qy,qz,qw;
/// f32 x,y,z; f32 nx,ny,nz; f32 ax,ay,az; u32 material; u16 IString header; u32; u8</c>, consumed
/// exactly. The owner's 1087 server reads the impact position at body <c>[28..40]</c>
/// (<c>ZoneCombatThrowables.OnContactReport</c>) and 1148 puts it at exactly the same offset, which
/// is the independent check on the framing.
/// </para>
/// </summary>
/// <param name="ProjectileId">
/// <c>+0</c>. 63 in the evidence packet - the same id the paired <c>82 03 Fire</c> named and the
/// same the <c>82 06</c> two milliseconds later repeats. [P from the evidence]
/// </param>
/// <param name="CharacterId">
/// <c>+4</c>. The entity contacted, 0 for the world. Zero in the evidence packet, and the molotov
/// did land on terrain. [I]
/// </param>
/// <param name="RotationX">
/// <c>+12</c>, the first component of a UNIT QUATERNION: the evidence's
/// <c>(0.525862, -0.018579, 0.140904, 0.838612)</c> has a norm of 1.0000 to seven digits, which is
/// what fixes this boundary. The contact's orientation. [I meaning]
/// </param>
/// <param name="RotationY"><inheritdoc cref="RotationX"/></param>
/// <param name="RotationZ"><inheritdoc cref="RotationX"/></param>
/// <param name="RotationW"><inheritdoc cref="RotationX"/></param>
/// <param name="X">
/// <c>+28</c>. <b>The contact position</b> - <c>(-2048.8, -4.6, 1340.3)</c> in the evidence,
/// 8.3 u from the hand the molotov left, and the identical triple the following <c>82 06</c> and
/// <c>82 1a</c> carry. The one field this server acts on.
/// </param>
/// <param name="Y"><inheritdoc cref="X"/></param>
/// <param name="Z"><inheritdoc cref="X"/></param>
/// <param name="NormalX">
/// <c>+40</c>, a unit vector: <c>(-0.965926, 0, -0.258819)</c> = <c>(-cos 15°, 0, -sin 15°)</c>,
/// norm 1.0000. A surface normal or a contact direction; which of the two is [U].
/// </param>
/// <param name="NormalY"><inheritdoc cref="NormalX"/></param>
/// <param name="NormalZ"><inheritdoc cref="NormalX"/></param>
/// <param name="SecondX">
/// <c>+52</c>, a second unit vector: <c>(0.216788, -0.407231, -0.887224)</c>, norm 1.0000. [U meaning]
/// </param>
/// <param name="SecondY"><inheritdoc cref="SecondX"/></param>
/// <param name="SecondZ"><inheritdoc cref="SecondX"/></param>
/// <param name="Material">
/// <c>+64</c>. <c>0xffffffff</c> in the evidence - the -1 "no material / nothing" sentinel this
/// client writes elsewhere. [I]
/// </param>
/// <param name="LocationHeader">
/// <c>+68</c>. The same <c>SoeUtil::IString</c> u16 <c>82 06</c> carries, and the same value:
/// <c>0xa000</c>, the dictionary form with id 0 and no text. That two different subs put the same
/// header in the same relative place is what makes the tail framing more than arithmetic.
/// </param>
/// <param name="UnknownDword"><c>+70</c>. 0 in the evidence. [U]</param>
/// <param name="Trailer"><c>+74</c>. 1 in the evidence - the same closing <c>u8</c> shape as <c>82 19</c>.</param>
public readonly record struct ProjectileContactReport(
    uint ProjectileId,
    ulong CharacterId,
    float RotationX,
    float RotationY,
    float RotationZ,
    float RotationW,
    float X,
    float Y,
    float Z,
    float NormalX,
    float NormalY,
    float NormalZ,
    float SecondX,
    float SecondY,
    float SecondZ,
    uint Material,
    ushort LocationHeader,
    uint UnknownDword,
    byte Trailer)
{
    /// <summary>The contact named no entity - the projectile met the world.</summary>
    public bool HitTheWorld => CharacterId == 0;

    /// <summary>The client's own <c>-1</c> sentinel for "no material".</summary>
    public bool NoMaterial => Material == uint.MaxValue;
}

/// <summary>
/// <c>82 1a DestroyNpcProjectile</c> - the client's own end-of-life notice for a projectile actor
/// (docs/125 §3). 28 body bytes: <c>u64 characterGuid; u32 projectileId; f32 x,y,z; f32</c>.
/// <para>
/// The 2026-09-04 19:09:17.772 packet is the whole derivation: projectile <b>63</b>, the molotov's
/// own, at the <b>identical</b> position its <c>82 21</c> and <c>82 06</c> reported five seconds
/// earlier, sent 4.998 s after the throw - i.e. at the record's <c>LIFESPAN</c> of 5.0 s, when the
/// actor tick expires a projectile that did not detonate. It is the client saying "this projectile
/// is gone", not that anything went off, so this server logs it and never bangs on it.
/// </para>
/// </summary>
/// <param name="CharacterGuid"><c>+0</c>. 0 in the evidence. The spawned NPC actor's guid. [I]</param>
/// <param name="ProjectileId"><c>+8</c>. 63 - the id the <c>82 03</c> named. [P from the evidence]</param>
/// <param name="X">The position the actor died at, identical to the contact's. [P]</param>
/// <param name="Y"><inheritdoc cref="X"/></param>
/// <param name="Z"><inheritdoc cref="X"/></param>
/// <param name="Trailer">The closing f32; <c>1.0</c> in the evidence. [U]</param>
public readonly record struct DestroyNpcProjectile(
    ulong CharacterGuid, uint ProjectileId, float X, float Y, float Z, float Trailer);

/// <summary>
/// <b>The validated reader for the three c2s subs docs/20 §6 marked BLOCKED.</b>
/// <para>
/// <b>Where the layouts come from, and why they are candidates rather than guesses.</b> docs/20 §6
/// spent a whole Ghidra budget hunting the August client's <c>0x82</c> <em>sender</em> and found
/// nothing: the composed id occurs exactly once in the image, and the receive record classes have no
/// virtual serialise. The owner, on <c>ClientProtocol_1087</c>, recovered the sender side from his
/// client's own writers. Those two builds turn out to number this family <b>identically</b> for
/// every sub the loop touches (<c>01</c>, <c>03</c>, <c>05</c>, <c>06</c>, <c>07</c>, <c>0c</c>,
/// <c>15</c>, <c>1f</c>, <c>20</c>, <c>22</c>, <c>27</c>; 1148 only appends <c>0x28</c> and renames
/// <c>0x0a</c>), which is what makes his layouts the first real candidates this project has had.
/// The one shape difference is the family base: two bytes (<c>83 00</c>) at 1087, one (<c>82</c>) at
/// 1148, with the same <c>[base][u32 gameTime][u8 sub]</c> arrangement either side.
/// </para>
/// <para>
/// <b>Every reader is a gate.</b> A packet that fails any plausibility check returns false and the
/// caller logs it with untruncated hex and does nothing. That is the owner's own rule and it is why
/// his server never paid damage out of a misread; here it also means the very first <c>0x82</c> the
/// August client sends is readable evidence of whether the candidate is right.
/// </para>
/// </summary>
public static class WeaponBaseDecoder
{
    /// <summary><c>u8 0x82; u32 gameTime; u8 sub</c>.</summary>
    public const int HeaderLength = 6;

    /// <summary><c>WeaponPacket::cIdFireStateUpdate</c>.</summary>
    public const byte SubFireStateUpdate = 0x01;

    /// <summary><c>WeaponPacket::cIdFire</c>.</summary>
    public const byte SubFire = 0x03;

    /// <summary><c>WeaponPacket::cIdFireNoProjectile</c>.</summary>
    public const byte SubFireNoProjectile = 0x05;

    /// <summary><c>WeaponPacket::cIdProjectileHitReport</c>.</summary>
    public const byte SubProjectileHitReport = 0x06;

    /// <summary><c>WeaponPacket::cIdReloadRequest</c>.</summary>
    public const byte SubReloadRequest = 0x07;

    /// <summary>
    /// <c>WeaponPacket::cIdSwitchFireModeRequest</c> - ADS on the August client (docs/107 §2).
    /// </summary>
    public const byte SubSwitchFireModeRequest = 0x0c;

    /// <summary><c>WeaponPacket::cIdMultiWeapon</c> - the wrapper most of the traffic arrives in.</summary>
    public const byte SubMultiWeapon = 0x1f;

    /// <summary>
    /// <c>WeaponPacket::cIdWeaponFireHint</c> - <b>sent bare, never wrapped, one per accepted
    /// trigger pull</b>, and the only c2s sub of this family that carries the shot's DIRECTION.
    /// Confirmed on the August wire: 53 of them in the 2026-09-03 17:51 session, one for every
    /// <c>82 03 Fire</c>, always outside <c>82 1f</c> (docs/107 §10).
    /// </summary>
    public const byte SubWeaponFireHint = 0x20;

    /// <summary>
    /// The one byte the August client writes between the guid and the muzzle point. 255 in all 53
    /// captured hints; carried, never asserted. <b>The builder disagrees with the wire and the
    /// serializer is not in any dump</b>: <c>FUN_140e74b30:134</c> stores <c>1</c> into the object's
    /// <c>+0x28</c>, and every captured packet has <c>0xff</c> in that position, so how the byte
    /// reaches the wire is open (docs/107 §10).
    /// </summary>
    public const byte WeaponFireHintMarkerByte = 0xff;

    /// <summary>Bytes before the hint list: <c>u64 guid; u8 marker; f32 x,y,z; u32 count</c>.</summary>
    public const int WeaponFireHintFixedLength = 8 + 1 + 12 + 4;

    /// <summary>One hint entry's fixed part: <c>u32 projectileId; f32 dx,dy,dz; u32 targetCount</c>.</summary>
    public const int WeaponFireHintEntryLength = 4 + 12 + 4;

    /// <summary>
    /// <c>WeaponPacket::cIdAmmoCountAcknowledge</c> - <b>the one sub 1148 added and 1087 never
    /// had</b> (docs/02, 2026-08-27 diff row), and a packet only the CLIENT sends.
    /// </summary>
    public const byte SubAmmoCountAcknowledge = 0x28;

    /// <summary>
    /// The owner's 1087 <c>EmptyFireState</c>. <b>The August client never writes it</b>, and that is
    /// a finding rather than a detail (docs/107 §10, 2026-09-03).
    /// <para>
    /// The only two c2s writers for this sub, <c>FUN_1414897f0</c> (fire start) and
    /// <c>FUN_14148a1a0</c> (fire stop), build the state byte from a fixed bitfield that has no bit
    /// 6 in it at all, so <b>there is no value of <c>82 01</c> that means "my magazine is dry" at
    /// 1148</b>. Dryness is a purely local transition to weapon-component state <c>0x0e</c>
    /// (<c>FUN_1422946d0</c> case 8) and never reaches the wire. The constant is kept so the log can
    /// name the value if a build ever sends it, and so the magazine resync's gate reads as the
    /// deliberate belt-and-braces it is - but nothing may depend on ever seeing it.
    /// </para>
    /// </summary>
    public const byte EmptyFireState = 64;

    /// <summary>
    /// A sanity ceiling on the <c>Fire</c> array. The widest spread this game has is a 12-pellet
    /// shotgun, so anything past this is a misread, not a weapon. The owner's own number.
    /// </summary>
    public const uint MaxProjectilesPerShot = 64;

    /// <summary>A misread hit-entry count would walk off the end of the datagram.</summary>
    public const uint MaxHitEntries = 64;

    /// <summary>20 bytes: <c>u64 guid | u32 | u32 | u32</c>.</summary>
    public const int HitEntryBytes = 20;

    /// <summary>
    /// The coordinate gate, in world units. Z2 is a 256x256-tile map and every position Cranberry
    /// has ever sent or received is inside a few thousand units of the origin; a value outside this
    /// box is a misread field, not a place.
    /// </summary>
    public const float CoordinateLimit = 16_384f;

    /// <summary>
    /// Bit 0 of a <c>82 01 FireStateUpdate</c>'s state byte: <b>the trigger is down</b>.
    /// <para>
    /// The two c2s writers hard-code the whole byte. <c>FUN_1414897f0</c> (fire START) ORs in
    /// <c>0x11</c>; <c>FUN_14148a1a0</c> (fire STOP) writes the same bitfield without it. So 17 is
    /// "firing" and 0 is "not firing", and bits 1, 2 and 3 are three component predicates that were
    /// clear in all 112 packets of the 17:51 session (docs/107 §10). Bit 3 also selects the tail:
    /// clear, the serializer writes a <c>u32</c>; set, a position vector.
    /// </para>
    /// </summary>
    public const byte FireStateTriggerDown = 0x01;

    /// <summary>
    /// <b>The August client's own name for every sub of this family</b>, read out of its
    /// registration table <c>FUN_1413d3520</c>, which calls the name registrar once per sub with
    /// the composed id <c>sub &lt;&lt; 24 | 0x008200</c>. This is the 1148 sub table as the binary
    /// gives it, and it replaces every name this project had carried across from 1087 by analogy.
    /// <para>
    /// It agrees with the 1087 numbering everywhere the loop touches and adds the four names no
    /// 1087 list had: <c>0x02 FireStateTargetedUpdate</c>, <c>0x04 FireWithDefinitionMapping</c>,
    /// <c>0x0a _UNUSED_10</c> and <c>0x0b ReloadRejected</c>.
    /// </para>
    /// </summary>
    public static string NameOfSub(byte sub) => sub switch
    {
        0x01 => "FireStateUpdate",
        0x02 => "FireStateTargetedUpdate",
        0x03 => "Fire",
        0x04 => "FireWithDefinitionMapping",
        0x05 => "FireNoProjectile",
        0x06 => "ProjectileHitReport",
        0x07 => "ReloadRequest",
        0x08 => "Reload",
        0x09 => "ReloadInterrupt",
        0x0a => "_UNUSED_10",
        0x0b => "ReloadRejected",
        0x0c => "SwitchFireModeRequest",
        0x0d => "LockOnGuidUpdate",
        0x0e => "LockOnLocationUpdate",
        0x0f => "StatUpdate",
        0x10 => "DebugProjectile",
        0x11 => "AddFireGroup",
        0x12 => "RemoveFireGroup",
        0x13 => "ReplaceFireGroup",
        0x14 => "GuidedUpdate",
        0x15 => "RemoteWeaponBase",
        0x16 => "ChamberRound",
        0x17 => "GuidedSetNonSeeking",
        0x18 => "ChamberInterrupt",
        0x19 => "GuidedExplode",
        0x1a => "DestroyNpcProjectile",
        0x1b => "WeaponToggleEffects",
        0x1c => "Reset",
        0x1d => "ProjectileSpawnNpc",
        0x1e => "FireRejected",
        0x1f => "MultiWeapon",
        0x20 => "WeaponFireHint",
        0x21 => "ProjectileContactReport",
        0x22 => "MeleeHitMaterial",
        0x23 => "ProjectileSpawnAttachedNpc",
        0x24 => "AddDebugLogEntry",
        0x25 => "DebugZoneState",
        0x26 => "GrenadeBounceReport",
        0x27 => "AimBlockedNotify",
        0x28 => "AmmoCountAcknowledge",
        _ => "unnamed",
    };

    /// <summary>Reads the six-byte family header. False for anything that is not a <c>0x82</c>.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> packet, out WeaponBaseHeader header)
    {
        header = default;

        if (packet.Length < HeaderLength || packet[0] != ZoneOpcodes.WeaponBase)
        {
            return false;
        }

        header = new WeaponBaseHeader(packet[5], BitConverter.ToUInt32(packet[1..5]));
        return true;
    }

    /// <summary>The body after the family header.</summary>
    public static ReadOnlySpan<byte> Body(ReadOnlySpan<byte> packet) =>
        packet.Length <= HeaderLength ? [] : packet[HeaderLength..];

    /// <summary>
    /// Splits an <c>82 1f MultiWeapon</c> into the complete packets it carries. Its body is
    /// <c>u32 count</c> then <c>count</c> x (<c>u32 size</c>, <c>u8[size]</c>), and <b>each embedded
    /// body is already a whole <c>82 | gameTime | sub | fields</c> packet</b>.
    /// <para>
    /// <b>Do not re-frame it.</b> The owner's own code carries the scar: "Round 24 and earlier
    /// prepended a second <c>83 00</c> here and shifted every field two bytes right." Each element
    /// returned here is a span into <paramref name="packet"/>, dispatched exactly as it stands.
    /// </para>
    /// <para>
    /// Without this unwrap a <c>Fire</c> is logged as an unreadable <c>sub=0x1f</c>: the owner
    /// counted <b>274 of 428</b> c2s weapon packets in one eight-minute session arriving inside
    /// <c>MultiWeapon</c>, with only <c>WeaponFireHint</c>, <c>MeleeHitMaterial</c> and
    /// <c>AimBlockedNotify</c> sent bare.
    /// </para>
    /// </summary>
    /// <param name="packet">The whole inbound <c>82 1f</c> packet, header included.</param>
    /// <param name="into">Receives one entry per embedded packet, in order.</param>
    /// <returns>False when the wrapper is malformed; <paramref name="into"/> then holds whatever
    /// was readable before the break, which is itself evidence.</returns>
    public static bool TryReadMultiWeapon(ReadOnlySpan<byte> packet, List<Range> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();

        ReadOnlySpan<byte> body = Body(packet);

        if (body.Length < 4)
        {
            return false;
        }

        uint count = BitConverter.ToUInt32(body[..4]);
        int offset = 4;

        if (count > MaxHitEntries)
        {
            return false;
        }

        for (uint i = 0; i < count; i++)
        {
            if (offset + 4 > body.Length)
            {
                return false;
            }

            uint size = BitConverter.ToUInt32(body[offset..(offset + 4)]);
            offset += 4;

            if (size < HeaderLength || size > int.MaxValue || offset + (int)size > body.Length)
            {
                return false;
            }

            int start = HeaderLength + offset;
            offset += (int)size;

            if (packet[start] != ZoneOpcodes.WeaponBase)
            {
                return false;
            }

            into.Add(new Range(start, HeaderLength + offset));
        }

        return offset == body.Length;
    }

    /// <summary><c>82 01</c>. 10 body bytes: <c>u64 weaponGuid; u8 fireState; u8</c>.</summary>
    public static bool TryReadFireStateUpdate(ReadOnlySpan<byte> packet, out FireStateUpdate value)
    {
        value = default;
        ReadOnlySpan<byte> body = Body(packet);

        if (body.Length != 10)
        {
            return false;
        }

        ulong guid = BitConverter.ToUInt64(body[..8]);

        if (guid == 0)
        {
            return false;
        }

        value = new FireStateUpdate(guid, body[8], body[9]);
        return true;
    }

    /// <summary>
    /// <c>82 28 AmmoCountAcknowledge</c>: <c>u64 itemGuid; i32 clientAmmo; u32 serverAmmo</c> -
    /// <b>22 bytes</b> with the header.
    /// <para>
    /// <b>The client sends this, and it is the only proven-layout c2s weapon packet this project
    /// has.</b> Its writer is <c>FUN_141482a60</c>, reached from the <c>82 08 Reload</c> applier
    /// <c>FUN_1414887e0</c>: after the applier has refilled the magazine it compares its own count
    /// <c>FUN_14228e710(comp, item+0x110)</c> against the <c>ammoCount</c> field this server put in
    /// the reply, and when they differ it tells the server both numbers - its own first, then the
    /// one it was given back verbatim (S5c §5.3). So it is a <em>disagreement report</em>, not a
    /// request, and the right answer is to move the server's magazine to the client's number: that
    /// is the count the player can see.
    /// </para>
    /// </summary>
    public static bool TryReadAmmoCountAcknowledge(
        ReadOnlySpan<byte> packet, out AmmoCountAcknowledge value)
    {
        value = default;
        ReadOnlySpan<byte> body = Body(packet);

        if (body.Length != 16)
        {
            return false;
        }

        ulong guid = BitConverter.ToUInt64(body[..8]);
        int client = BitConverter.ToInt32(body[8..12]);
        uint server = BitConverter.ToUInt32(body[12..16]);

        // A magazine is a small number in this build - the largest CLIP_SIZE in the whole August
        // sheet is 30. Anything outside that band is a misread field, not a count.
        if (guid == 0 || client < 0 || client > MaxMagazine || server > MaxMagazine)
        {
            return false;
        }

        value = new AmmoCountAcknowledge(guid, client, server);
        return true;
    }

    /// <summary>
    /// The plausibility ceiling on a magazine count. The August sheet's largest <c>CLIP_SIZE</c> is
    /// 30 (AR-15, AK-47); this is that with room for a build that adds a drum.
    /// </summary>
    public const int MaxMagazine = 256;

    /// <summary>
    /// <c>82 0c SwitchFireModeRequest</c>: <c>u64 weaponGuid; u8 fireGroupIndex; u8 fireModeIndex;
    /// u8</c> - <b>17 bytes</b> with the 6-byte header.
    /// <para>
    /// <b>The layout is the owner's, and the August wire agrees with it in 145 packets.</b> His
    /// reader is <c>TryReadSwitchFireMode(payload, out ulong guid, out byte firegroupIndex, out byte
    /// firemodeIndex)</c> (<c>C:\Z1\Server\Zone\ZoneCombatWire.cs:924-940</c>) over a 10-byte 1087
    /// body; 1148 adds one trailing zero. Decoding every August packet against it yields two clean
    /// enum-valued fields and a constant - <c>fireGroupIndex</c> 0 throughout, <c>fireModeIndex</c>
    /// only ever 0 or 1, trailing byte only ever 0 (FIRE-PATH-DIAGNOSIS §1.3). A layout that
    /// produced garbage in any of the three would not do that.
    /// </para>
    /// <para>
    /// The two index fields are <b>bounded</b> here rather than merely read: a fire group carries at
    /// most <see cref="WeaponItemAddTailMaximumIndex"/> modes on anything this server ships, and an
    /// index outside that band is a misread body, not a mode the player selected.
    /// </para>
    /// </summary>
    public static bool TryReadSwitchFireModeRequest(
        ReadOnlySpan<byte> packet, out SwitchFireModeRequest value)
    {
        value = default;
        ReadOnlySpan<byte> body = Body(packet);

        if (body.Length != 11)
        {
            return false;
        }

        ulong guid = BitConverter.ToUInt64(body[..8]);

        if (guid == 0
            || body[8] > WeaponItemAddTailMaximumIndex
            || body[9] > WeaponItemAddTailMaximumIndex)
        {
            return false;
        }

        value = new SwitchFireModeRequest(guid, body[8], body[9], body[10]);
        return true;
    }

    /// <summary>
    /// The plausibility ceiling on a fire-group or fire-mode index. Both are written as signed bytes
    /// in the <c>ItemAdd</c> tail (<c>WeaponItemAddTail.MaximumGroups</c>), so anything past
    /// <c>sbyte.MaxValue</c> could not have been sent in the first place; the observed values are 0
    /// and 1.
    /// </summary>
    public const byte WeaponItemAddTailMaximumIndex = 127;

    /// <summary>
    /// <c>82 03</c>. <c>u64 weaponGuid; f32 x,y,z; u32 projectileCount; {u32 id, u32}[count]</c>.
    /// The array must consume the body exactly.
    /// </summary>
    public static bool TryReadFire(ReadOnlySpan<byte> packet, out WeaponFire value)
    {
        value = default;
        ReadOnlySpan<byte> body = Body(packet);

        if (body.Length < 24)
        {
            return false;
        }

        ulong guid = BitConverter.ToUInt64(body[..8]);
        float x = BitConverter.ToSingle(body[8..12]);
        float y = BitConverter.ToSingle(body[12..16]);
        float z = BitConverter.ToSingle(body[16..20]);
        uint count = BitConverter.ToUInt32(body[20..24]);

        if (guid == 0 || !PlausiblePoint(x, y, z) || count == 0 || count > MaxProjectilesPerShot)
        {
            return false;
        }

        if (body.Length != 24 + ((int)count * 8))
        {
            return false;
        }

        var ids = new uint[count];

        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = BitConverter.ToUInt32(body[(24 + (i * 8))..(28 + (i * 8))]);
        }

        value = new WeaponFire(guid, x, y, z, ids);
        return true;
    }

    /// <summary><c>82 19 GuidedExplode</c> (docs/120 §2.5). Sub only; no receive case at 1148 was needed to name it - the client's own registrar does.</summary>
    public const byte SubGuidedExplode = 0x19;

    /// <summary><c>82 26 GrenadeBounceReport</c> (docs/120 §2.4).</summary>
    public const byte SubGrenadeBounceReport = 0x26;

    /// <summary>
    /// <c>82 21</c> - <c>WeaponPacket::cIdProjectileContactReport</c>, the client's own name for it
    /// in its registrar <c>FUN_1413d3520</c> (<c>thunk_FUN_1413d1df0(0x21008200, …)</c>). docs/125 §2.
    /// </summary>
    public const byte SubProjectileContactReport = 0x21;

    /// <summary>
    /// <c>82 1a</c> - <c>WeaponPacket::cIdDestroyNpcProjectile</c>, from the same registrar
    /// (<c>0x1a008200</c>). Not an unknown: the client names it. docs/125 §3.
    /// </summary>
    public const byte SubDestroyNpcProjectile = 0x1a;

    /// <summary>The <c>82 21</c> body, which is fixed: 75 bytes, consumed exactly.</summary>
    public const int ProjectileContactReportBodyLength = 75;

    /// <summary>The <c>82 1a</c> body, fixed at 28 bytes.</summary>
    public const int DestroyNpcProjectileBodyLength = 28;

    /// <summary>
    /// <c>82 21 ProjectileContactReport</c> (docs/125 §2): <c>u32 projectileId; u64 characterId;
    /// f32 quat x,y,z,w; f32 pos x,y,z; f32 n x,y,z; f32 x,y,z; u32 material; u16 IString header;
    /// u32; u8</c> - 75 body bytes, consumed exactly.
    /// <para>
    /// <b>No serializer for this sub exists in any dump</b> (the registrar binds only names; the
    /// sub-to-serializer slot lives in an unexported packet-class vtable), so the framing rests on
    /// four independent checks against the 2026-09-04 19:09:13.259 packet: the declared length is
    /// consumed to the byte; the field at <c>+0</c> is the projectile id the paired <c>82 03</c>
    /// named; all three vector fields have a norm of 1.0000 in the quaternion's four components and
    /// in each unit triple; and the position at <c>[28..40]</c> is the same offset the owner's own
    /// 1087 server reads it at (<c>ZoneCombatThrowables.OnContactReport</c>), with a value 8.3 u
    /// from the hand that threw and identical to the following <c>82 06</c>'s and <c>82 1a</c>'s.
    /// </para>
    /// </summary>
    public static bool TryReadProjectileContactReport(
        ReadOnlySpan<byte> packet, out ProjectileContactReport value)
    {
        value = default;
        ReadOnlySpan<byte> body = Body(packet);

        if (body.Length != ProjectileContactReportBodyLength)
        {
            return false;
        }

        float x = F(body, 28);
        float y = F(body, 32);
        float z = F(body, 36);

        if (!PlausiblePoint(x, y, z))
        {
            return false;
        }

        // The two unit vectors are the boundary check: a field that is not a direction means the
        // framing moved, and a moved framing must never place a detonation.
        if (!PlausibleDirection(F(body, 40), F(body, 44), F(body, 48))
            || !PlausibleDirection(F(body, 52), F(body, 56), F(body, 60)))
        {
            return false;
        }

        value = new ProjectileContactReport(
            BitConverter.ToUInt32(body[..4]),
            BitConverter.ToUInt64(body[4..12]),
            F(body, 12), F(body, 16), F(body, 20), F(body, 24),
            x, y, z,
            F(body, 40), F(body, 44), F(body, 48),
            F(body, 52), F(body, 56), F(body, 60),
            BitConverter.ToUInt32(body[64..68]),
            BitConverter.ToUInt16(body[68..70]),
            BitConverter.ToUInt32(body[70..74]),
            body[74]);
        return true;
    }

    /// <summary>One little-endian <c>f32</c> out of a body at a fixed offset.</summary>
    private static float F(ReadOnlySpan<byte> body, int at) => BitConverter.ToSingle(body[at..(at + 4)]);

    /// <summary>
    /// <c>82 1a DestroyNpcProjectile</c> (docs/125 §3): <c>u64 characterGuid; u32 projectileId;
    /// f32 x,y,z; f32</c> - 28 body bytes, consumed exactly. The client's end-of-life notice for a
    /// projectile actor, not a detonation.
    /// </summary>
    public static bool TryReadDestroyNpcProjectile(
        ReadOnlySpan<byte> packet, out DestroyNpcProjectile value)
    {
        value = default;
        ReadOnlySpan<byte> body = Body(packet);

        if (body.Length != DestroyNpcProjectileBodyLength)
        {
            return false;
        }

        float x = BitConverter.ToSingle(body[12..16]);
        float y = BitConverter.ToSingle(body[16..20]);
        float z = BitConverter.ToSingle(body[20..24]);

        if (!PlausiblePoint(x, y, z))
        {
            return false;
        }

        value = new DestroyNpcProjectile(
            BitConverter.ToUInt64(body[..8]),
            BitConverter.ToUInt32(body[8..12]),
            x, y, z,
            BitConverter.ToSingle(body[24..28]));
        return true;
    }

    /// <summary>The fixed part of a bounce report's body: <c>u32; u32; u64</c>.</summary>
    public const int GrenadeBounceReportBodyLength = 16;

    /// <summary>
    /// Reads the client's packed u32 (<c>FUN_140a18d80</c> writes it: the low two bits of the
    /// first byte are the number of extra little-endian bytes, the value is the whole quantity
    /// shifted right by two - the same form <c>ClientVarInt</c> writes). False when the body ends
    /// inside it.
    /// </summary>
    public static bool TryReadPackedUInt32(ReadOnlySpan<byte> body, ref int offset, out uint value)
    {
        value = 0;
        if (offset >= body.Length)
        {
            return false;
        }

        int extra = body[offset] & 0x03;
        if (offset + 1 + extra > body.Length)
        {
            return false;
        }

        uint packed = 0;
        for (int i = 0; i <= extra; i++)
        {
            packed |= (uint)body[offset + i] << (8 * i);
        }

        value = packed >> 2;
        offset += 1 + extra;
        return true;
    }

    /// <summary>
    /// <c>82 19 GuidedExplode</c>: <c>packed u32; packed u32; u32 projectileId; packed u32;
    /// f32 x,y,z; f32 dx,dy,dz; u8</c> after the six-byte header (serializer <c>FUN_140eec1f0</c>,
    /// docs/120 §2.5). Gated on a plausible position and an exactly-consumed body.
    /// </summary>
    public static bool TryReadGuidedExplode(ReadOnlySpan<byte> packet, out GuidedExplode value)
    {
        value = default;
        ReadOnlySpan<byte> body = Body(packet);
        int offset = 0;

        if (!TryReadPackedUInt32(body, ref offset, out uint owner)
            || !TryReadPackedUInt32(body, ref offset, out uint second))
        {
            return false;
        }

        if (offset + 4 > body.Length)
        {
            return false;
        }

        uint projectileId = BitConverter.ToUInt32(body[offset..(offset + 4)]);
        offset += 4;

        if (!TryReadPackedUInt32(body, ref offset, out uint target))
        {
            return false;
        }

        if (body.Length != offset + 12 + 12 + 1)
        {
            return false;
        }

        float x = BitConverter.ToSingle(body[offset..(offset + 4)]);
        float y = BitConverter.ToSingle(body[(offset + 4)..(offset + 8)]);
        float z = BitConverter.ToSingle(body[(offset + 8)..(offset + 12)]);
        float dx = BitConverter.ToSingle(body[(offset + 12)..(offset + 16)]);
        float dy = BitConverter.ToSingle(body[(offset + 16)..(offset + 20)]);
        float dz = BitConverter.ToSingle(body[(offset + 20)..(offset + 24)]);
        byte trailer = body[offset + 24];

        if (!PlausiblePoint(x, y, z) || !float.IsFinite(dx) || !float.IsFinite(dy) || !float.IsFinite(dz))
        {
            return false;
        }

        value = new GuidedExplode(owner, second, projectileId, target, x, y, z, dx, dy, dz, trailer);
        return true;
    }

    /// <summary>
    /// <c>82 26 GrenadeBounceReport</c>: <c>u32 projectileId; u32 effectId; u64 characterGuid</c>
    /// (serializer <c>FUN_140ef6680</c>, docs/120 §2.4). Exactly 16 body bytes.
    /// </summary>
    public static bool TryReadGrenadeBounceReport(ReadOnlySpan<byte> packet, out GrenadeBounceReport value)
    {
        value = default;
        ReadOnlySpan<byte> body = Body(packet);

        if (body.Length != GrenadeBounceReportBodyLength)
        {
            return false;
        }

        value = new GrenadeBounceReport(
            BitConverter.ToUInt32(body[..4]),
            BitConverter.ToUInt32(body[4..8]),
            BitConverter.ToUInt64(body[8..16]));
        return true;
    }

    /// <summary>
    /// <c>82 20 WeaponFireHint</c>: <c>u64 weaponGuid; u8 marker; f32 x,y,z; u32; u32 projectileId;
    /// f32 dx,dy,dz; u32</c> - <b>51 bytes</b> with the six-byte header, 45 of body.
    /// <para>
    /// <b>Every field is read off the August wire, not ported.</b> The five bodies quoted in
    /// docs/107 §10 fix each boundary: the leading <c>u64</c> is byte-identical to the wielded
    /// instance the server itself handed the client in <c>94 02</c>
    /// (<c>0x310000000000000C</c> for the AK-47, <c>0x310000000000000E</c> for the shotgun, and it
    /// changes with the hotbar), the three floats after the marker equal the paired
    /// <c>82 03 Fire</c>'s muzzle point to the bit, the <c>u32</c> after them equals that
    /// <c>Fire</c>'s single projectile id, and the last three floats form a unit vector in 52 of 53
    /// hints - the arithmetic that could not survive a wrong boundary.
    /// </para>
    /// <para>
    /// The direction is gated, not asserted: finite, and either all-zero or within a generous band
    /// of unit length. An all-zero direction is a real captured value (the shotgun's 0x21 hint) and
    /// is accepted; a direction that is neither is a field boundary that moved, and a moved boundary
    /// must never reach arbitration.
    /// </para>
    /// </summary>
    public static bool TryReadWeaponFireHint(ReadOnlySpan<byte> packet, out WeaponFireHint value)
    {
        value = default;
        ReadOnlySpan<byte> body = Body(packet);

        if (body.Length < WeaponFireHintFixedLength)
        {
            return false;
        }

        ulong guid = BitConverter.ToUInt64(body[..8]);
        byte marker = body[8];
        float x = BitConverter.ToSingle(body[9..13]);
        float y = BitConverter.ToSingle(body[13..17]);
        float z = BitConverter.ToSingle(body[17..21]);
        uint count = BitConverter.ToUInt32(body[21..25]);

        if (guid == 0 || !PlausiblePoint(x, y, z) || count == 0 || count > MaxProjectilesPerShot)
        {
            return false;
        }

        var entries = new WeaponFireHintEntry[count];
        int offset = WeaponFireHintFixedLength;

        for (uint i = 0; i < count; i++)
        {
            if (offset + WeaponFireHintEntryLength > body.Length)
            {
                return false;
            }

            uint projectileId = BitConverter.ToUInt32(body[offset..(offset + 4)]);
            float dx = BitConverter.ToSingle(body[(offset + 4)..(offset + 8)]);
            float dy = BitConverter.ToSingle(body[(offset + 8)..(offset + 12)]);
            float dz = BitConverter.ToSingle(body[(offset + 12)..(offset + 16)]);
            uint targets = BitConverter.ToUInt32(body[(offset + 16)..(offset + 20)]);
            offset += WeaponFireHintEntryLength;

            if (!PlausibleDirection(dx, dy, dz) || targets > MaxHitEntries)
            {
                return false;
            }

            if (offset + ((int)targets * 8) > body.Length)
            {
                return false;
            }

            var candidates = new ulong[targets];

            for (uint t = 0; t < targets; t++)
            {
                candidates[t] = BitConverter.ToUInt64(body[offset..(offset + 8)]);
                offset += 8;
            }

            entries[i] = new WeaponFireHintEntry(projectileId, dx, dy, dz, candidates);
        }

        // Every declared length must consume the body exactly - the same closing rule
        // TryReadFire and TryReadProjectileHitReport apply, and the one that makes a wrong
        // boundary a refusal instead of damage.
        if (offset != body.Length)
        {
            return false;
        }

        value = new WeaponFireHint(guid, marker, x, y, z, entries);
        return true;
    }

    /// <summary>
    /// A barrel direction is finite and either all-zero or of roughly unit length. The band is wide
    /// (0.9 - 1.1) because the client writes single-precision components it normalised itself; a
    /// value outside it means the three floats are not a direction and the layout is wrong.
    /// </summary>
    public static bool PlausibleDirection(float x, float y, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            return false;
        }

        if (x == 0f && y == 0f && z == 0f)
        {
            return true;
        }

        float squared = (x * x) + (y * y) + (z * z);
        return squared is >= 0.81f and <= 1.21f;
    }

    /// <summary>
    /// <c>82 06</c>. <c>u32 projectileId; u64 characterId; f32 x,y,z;</c> a
    /// <c>SoeUtil::IString</c> hit location, <c>u32; u32 hitEntryCount; entry[20]{count};
    /// u8 totalShotCount; u8 flags</c>. Every declared length must consume the body exactly.
    /// </summary>
    public static bool TryReadProjectileHitReport(ReadOnlySpan<byte> packet, out ProjectileHitReport value)
    {
        value = default;
        ReadOnlySpan<byte> body = Body(packet);

        // 26 fixed + u32 + u32 + u8 + u8 is the shortest legal report, and only in the dictionary
        // form, where no text follows the string header.
        if (body.Length < 36)
        {
            return false;
        }

        uint projectileId = BitConverter.ToUInt32(body[..4]);
        ulong characterId = BitConverter.ToUInt64(body[4..12]);
        float x = BitConverter.ToSingle(body[12..16]);
        float y = BitConverter.ToSingle(body[16..20]);
        float z = BitConverter.ToSingle(body[20..24]);
        ushort header = BitConverter.ToUInt16(body[24..26]);
        int offset = 26;
        string hitLocation = string.Empty;

        // D315 (docs/125). A ZERO characterId is a REAL report, not a misread: the client sends
        // 82 06 for a projectile that hit the WORLD as well as one that hit an entity, and the
        // 2026-09-04 19:09:13.261 molotov proves it - guid 0, a good position, and the two trailing
        // bytes (0x3f totalShotCount, 0x80 flags, the sender's own bit) landing exactly on the end
        // of the body. Refusing a zero guid here was why that packet printed "could not be read ...
        // NO DAMAGE". Decoding is not damage: a report that names no entity carries a POSITION,
        // which is what a thrown grenade needs, and the damage path still refuses it below.
        if (!PlausiblePoint(x, y, z))
        {
            return false;
        }

        if ((header & 0x8000) == 0)
        {
            // Literal form: the client writes strlen + 1 bytes, the NUL included - the byte a
            // "constant 8 or 9 trailing bytes" reading quietly absorbs.
            int length = header & 0xff;

            if (offset + length + 1 > body.Length)
            {
                return false;
            }

            if (length > 0)
            {
                ReadOnlySpan<byte> text = body.Slice(offset, length);

                if (!PrintableAscii(text))
                {
                    return false;
                }

                hitLocation = System.Text.Encoding.ASCII.GetString(text);
            }

            offset += length + 1;
        }

        if ((header & 0x4000) != 0)
        {
            offset += 4;
        }

        if (offset + 8 > body.Length)
        {
            return false;
        }

        uint unknown = BitConverter.ToUInt32(body[offset..(offset + 4)]);
        offset += 4;

        uint entries = BitConverter.ToUInt32(body[offset..(offset + 4)]);
        offset += 4;

        if (entries > MaxHitEntries)
        {
            return false;
        }

        offset += (int)entries * HitEntryBytes;

        if (offset + 2 != body.Length)
        {
            return false;
        }

        value = new ProjectileHitReport(
            projectileId,
            characterId,
            x,
            y,
            z,
            hitLocation,
            header,
            unknown,
            (int)entries,
            body[offset],
            body[offset + 1]);

        return true;
    }

    /// <summary>
    /// Finite and inside the play area. An infinity, a NaN or a coordinate past
    /// <see cref="CoordinateLimit"/> means a field boundary is wrong, and a wrong field boundary
    /// must never pay damage.
    /// </summary>
    public static bool PlausiblePoint(float x, float y, float z) =>
        Plausible(x) && Plausible(y) && Plausible(z);

    private static bool Plausible(float value) =>
        float.IsFinite(value) && Math.Abs(value) <= CoordinateLimit;

    /// <summary>
    /// The owner's <c>PlausibleHitLocation</c> gate. A literal bone name is printable ASCII; if it
    /// is not, the decompiled layout is wrong somewhere before the string, and the hex is the
    /// finding.
    /// </summary>
    public static bool PrintableAscii(ReadOnlySpan<byte> text)
    {
        foreach (byte b in text)
        {
            if (b is < 0x20 or > 0x7e)
            {
                return false;
            }
        }

        return true;
    }
}
