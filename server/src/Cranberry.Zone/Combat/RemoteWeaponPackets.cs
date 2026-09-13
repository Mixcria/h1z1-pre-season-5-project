using Cranberry.Protocol;
using Cranberry.Zone.World;

namespace Cranberry.Zone.Combat;

/// <summary>
/// The <c>82 15 RemoteWeaponBase</c> family — <b>the only way another player's gun is ever seen</b>.
/// Derived byte for byte in <c>out\overhaul-20260901\S5c-remote-weapon-82-15.md</c> §3.1–§3.8 from
/// the August readers; docs/100 §4 carries the field table.
///
/// <para>
/// <b>This family can never touch the local player.</b> S5c §0 finding 2: the dispatcher
/// <c>FUN_140b07010</c> case <c>0x15</c> (L389-397) drops the packet when its varint owner id equals
/// <c>world+0x324f8</c>, the local player's own transient id, and the entity lookup
/// <c>FUN_140aeb750</c> only returns entities carrying the <c>"ProxiedCharacter"</c> descriptor. So
/// <c>ownerTransientId</c> is always <em>the viewer's</em> transient id for <em>another</em>
/// character, and <see cref="GuardOwner"/> refuses the two ids that would make it a self-echo.
/// </para>
///
/// <para>
/// <b><c>gameTime</c> is fixed at 0 and is deliberately not a parameter.</b> S5c §3.1: the
/// pre-dispatch filter <c>FUN_140b81830</c> case <c>0x82</c> (L199-267) treats the header
/// <c>u32</c> as an apply time on the client's game clock — remote subs <c>01/02/05</c> bypass the
/// gate, every other <c>82 15</c> is queued at <c>world+0x35238</c> until the proxied entity's
/// interpolated clock reaches it. Zero always means "apply now". A writer that put a plain
/// server timestamp there would stall the whole arsenal behind a clock the server does not own.
/// </para>
/// </summary>
public static class RemoteWeaponPackets
{
    /// <summary><c>cPacketIdWeaponBase</c>.</summary>
    public const byte Family = ZoneOpcodes.WeaponBase;

    /// <summary><c>WeaponPacket::cIdRemoteWeapon</c> — the sub every packet in this file carries.</summary>
    public const byte Sub = 0x15;

    /// <summary>The header <c>u32</c>. Always 0: see the class remarks. S5c §3.1.</summary>
    public const uint ImmediateGameTime = 0;

    /// <summary><c>82 | u32 gameTime | 15 | u8 remoteSub</c> — everything before the owner varint.</summary>
    public const int PrefixLength = 7;

    /// <summary>
    /// <c>FUN_141498890</c>'s remote-sub table (S5c §3.4). Names are the client's own registrar
    /// strings.
    /// </summary>
    public enum RemoteSub : byte
    {
        /// <summary><c>Remote::cIdReset</c> — this character's whole arsenal, replacing it.</summary>
        Reset = 0x01,

        /// <summary><c>Remote::cIdAddWeapon</c>.</summary>
        AddWeapon = 0x02,

        /// <summary><c>Remote::cIdRemoveWeapon</c>.</summary>
        RemoveWeapon = 0x03,

        /// <summary><c>Remote::cIdUpdateBase</c>.</summary>
        UpdateBase = 0x04,

        /// <summary><c>Remote::cIdProjectileLaunchHint</c>.</summary>
        ProjectileLaunchHint = 0x05,

        /// <summary><c>Remote::cIdProjectileDetonateHint</c>.</summary>
        ProjectileDetonateHint = 0x06,

        /// <summary><c>Remote::cIdProjectileRemoteContactReport</c>.</summary>
        ProjectileRemoteContactReport = 0x07,
    }

    /// <summary>
    /// <c>UpdateBase</c>'s <c>updateType</c> byte, from <c>FUN_141499850</c>'s switch (S5c §3.7).
    /// <c>0x0d Throw</c> and <c>0x0e Trigger</c> are <b>absent on purpose</b>: the client logs
    /// <c>"unhandled update type %d"</c> for both, so there is nothing to write.
    /// </summary>
    public enum WeaponUpdateType : byte
    {
        FireState = 0x01,
        Reload = 0x03,
        ReloadLoopEnd = 0x04,
        ReloadInterrupt = 0x05,
        SwitchFireMode = 0x06,
        StatUpdate = 0x07,
        ProjectileLaunch = 0x0b,
        Chamber = 0x0c,
        ChamberInterrupt = 0x0f,
        AimBlocked = 0x10,
    }

    /// <summary>
    /// Length of the <c>82 15</c> prefix plus the owner varint, for a given owner id.
    /// The varint is <c>FUN_140a190f0</c>'s (S5c §3.2), so an id below 64 costs one byte.
    /// </summary>
    public static int HeaderLength(uint ownerTransientId) =>
        PrefixLength + ClientVarInt.Length(ownerTransientId);

    /// <summary>
    /// <b>The invariant, enforced rather than documented.</b> An <c>82 15</c> whose owner id is the
    /// receiving client's own transient id is discarded before any weapon code runs (S5c §0 finding
    /// 2), so a caller that passed the viewer's own id would produce a packet that silently does
    /// nothing. Both ids that can mean "me" are refused:
    /// <see cref="TransientIdTable.LocalPlayer"/> (1, what the self record will carry after the
    /// docs/100 §7 patch) and 0 (what <c>SelfRecord.cs:315</c> writes today).
    /// </summary>
    public static void GuardOwner(uint ownerTransientId)
    {
        if (ownerTransientId is 0 or TransientIdTable.LocalPlayer)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ownerTransientId),
                ownerTransientId,
                "82 15 addressed to the viewer's own transient id is dropped by FUN_140b07010 case 0x15 "
                + "(S5c section 0 finding 2). Pass the viewer's transient id for the OTHER character.");
        }
    }

    /// <summary>
    /// <b>The <c>weaponItemInstanceId</c> invariant.</b> S5c §4: the proxied weapon is keyed by this
    /// u64 in <c>mgr+0xd0</c>, and every <c>UpdateBase</c> looks the weapon up by it —
    /// <c>FUN_141499850</c> L45-62 logs <c>"received update type %d for weapon that was not found!"</c>
    /// and drops the packet otherwise. It must be the same guid the viewer's
    /// <c>94 01 SetCharacterEquipment</c> row for that character carries, or the gun is registered
    /// against an id nothing else names.
    /// </summary>
    public static void GuardEquipmentRowGuid(ulong equipmentRowGuid)
    {
        if (equipmentRowGuid == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(equipmentRowGuid),
                "weaponItemInstanceId must be the equipment-row guid the viewer already has for this "
                + "character (S5c section 4); 0 is the client's 'no item instance id' sentinel.");
        }
    }

    // -------------------------------------------------------------------------------------
    // Sub 0x02 — AddWeapon
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// <c>82 15 02</c> — reader <c>FUN_141494a60</c>, handler <c>FUN_141498200</c> →
    /// <c>FUN_141497950</c> (S5c §3.6). Body after the owner varint:
    /// <c>u64 weaponItemInstanceId; i32 blobLen; u8 blob[blobLen]</c>.
    /// <para>
    /// The handler is strict: a stream overrun or a single trailing byte drops the packet in
    /// silence. It also <b>never de-duplicates</b> — a second <c>AddWeapon</c> for the same id
    /// leaves two records and every lookup finds the newest, so an arsenal is replaced with
    /// <see cref="Reset"/>, not repaired with repeats.
    /// </para>
    /// </summary>
    public static byte[] AddWeapon(uint ownerTransientId, ulong equipmentRowGuid, RemoteWeaponBlob weapon)
    {
        ArgumentNullException.ThrowIfNull(weapon);
        GuardOwner(ownerTransientId);
        GuardEquipmentRowGuid(equipmentRowGuid);

        using var w = new PacketWriter(96);
        WriteHeader(w, RemoteSub.AddWeapon, ownerTransientId);
        w.WriteUInt64(equipmentRowGuid);
        w.WriteInt32(weapon.Length);
        weapon.WriteTo(w);
        return w.Written.ToArray();
    }

    /// <summary>Byte length of <see cref="AddWeapon"/>.</summary>
    public static int AddWeaponLength(uint ownerTransientId, RemoteWeaponBlob weapon)
    {
        ArgumentNullException.ThrowIfNull(weapon);
        return HeaderLength(ownerTransientId) + sizeof(ulong) + sizeof(int) + weapon.Length;
    }

    // -------------------------------------------------------------------------------------
    // Sub 0x01 — Reset
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// <c>82 15 01</c> — reader <c>FUN_141494f50</c>, handler <c>FUN_141499430</c> →
    /// <c>FUN_14149b0c0</c> (S5c §3.6). Body after the owner varint:
    /// <c>i32 len; u8 body[len]</c>, where the body is
    /// <c>u32 count; count x { u64 itemInstanceId; &lt;AddWeapon blob&gt; }; i32 stateCount</c>.
    /// <para>
    /// <b>The arsenal packet.</b> Before reading a byte the handler fires the "weapon leaving hand"
    /// listener for whatever sits in the two hand slots, clears the by-slot list and <em>destroys
    /// every registered weapon</em>. So this is "here is everything this character carries" — the
    /// weapon entering the hand carries <see cref="RemoteWeaponBlob.RightHandSlot"/>, the rest 0 —
    /// and it is what a viewer gets on peer spawn and on every draw.
    /// </para>
    /// <para>
    /// <c>stateCount</c> is written as 0. Its per-entry record (current group/mode, the state byte,
    /// then a <c>u32</c> and two bools for every mode of every group) is derived in S5c §3.6, but the
    /// inner loop is sized by the groups the blob just built, so an entry that disagrees with its
    /// blob abandons the rest of the packet. A zero state count skips
    /// <c>FUN_141499d40</c>'s state-loading loop; it does not initialize the active mode.
    /// Callers must select a valid mode using <see cref="SwitchFireMode"/> after adding
    /// a known weapon. See the September 10 friend-capture comparison.
    /// </para>
    /// </summary>
    public static byte[] Reset(uint ownerTransientId, IReadOnlyList<RemoteWeaponEntry> arsenal)
    {
        ArgumentNullException.ThrowIfNull(arsenal);
        GuardOwner(ownerTransientId);

        int bodyLength = BodyLength(arsenal);
        using var w = new PacketWriter(128 + bodyLength);
        WriteHeader(w, RemoteSub.Reset, ownerTransientId);
        w.WriteInt32(bodyLength);
        w.WriteUInt32((uint)arsenal.Count);
        foreach (RemoteWeaponEntry entry in arsenal)
        {
            GuardEquipmentRowGuid(entry.EquipmentRowGuid);
            w.WriteUInt64(entry.EquipmentRowGuid);
            entry.Weapon.WriteTo(w);
        }

        w.WriteInt32(0);            // stateCount — see the remarks
        return w.Written.ToArray();
    }

    /// <summary>Byte length of <see cref="Reset"/>.</summary>
    public static int ResetLength(uint ownerTransientId, IReadOnlyList<RemoteWeaponEntry> arsenal)
    {
        ArgumentNullException.ThrowIfNull(arsenal);
        return HeaderLength(ownerTransientId) + sizeof(int) + BodyLength(arsenal);
    }

    // -------------------------------------------------------------------------------------
    // Sub 0x03 — RemoveWeapon
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// <c>82 15 03</c> — reader <c>FUN_1414966b0</c>, handler <c>FUN_141499250</c> (S5c §3.6):
    /// <c>u64 weaponItemInstanceId</c>, and nothing else. The handler posts entity event
    /// <c>0x11 {u64 id}</c> through <c>FUN_140c52b30</c>.
    /// </summary>
    public static byte[] RemoveWeapon(uint ownerTransientId, ulong equipmentRowGuid)
    {
        GuardOwner(ownerTransientId);
        GuardEquipmentRowGuid(equipmentRowGuid);

        using var w = new PacketWriter(24);
        WriteHeader(w, RemoteSub.RemoveWeapon, ownerTransientId);
        w.WriteUInt64(equipmentRowGuid);
        return w.Written.ToArray();
    }

    /// <summary>Byte length of <see cref="RemoveWeapon"/>.</summary>
    public static int RemoveWeaponLength(uint ownerTransientId) =>
        HeaderLength(ownerTransientId) + sizeof(ulong);

    // -------------------------------------------------------------------------------------
    // Sub 0x04 — UpdateBase
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The <c>UpdateBase</c> header, reader <c>FUN_141493fe0</c> (S5c §3.7):
    /// <c>82 | u32 0 | 15 | 04 | varint owner | u8 updateType | u64 weaponItemInstanceId</c>,
    /// then the per-type payload.
    /// </summary>
    public static int UpdateHeaderLength(uint ownerTransientId) =>
        HeaderLength(ownerTransientId) + sizeof(byte) + sizeof(ulong);

    /// <summary>
    /// Type <c>0x01 FireState</c>, <b>point form</b> — handler <c>FUN_1414986c0</c>, reader
    /// <c>FUN_1414963e0</c>. Payload <c>u8 flags; 4 x f32 aimPoint</c>.
    /// <para>
    /// Bit 1 selects the point form over a <c>varint targetNetworkId</c>; the point form is used
    /// because the "no target network id" default <c>DAT_1452391d4</c> is an undecoded
    /// <c>.data</c> global (S5c §3.1), and a wrong sentinel silently clears the target instead of
    /// setting it. Bit 0 clear starts firing (entity event <b>10</b> <c>{slot}</c>), bit 0 set stops
    /// it (event <b>9</b>); bit 2 rides through to <c>FUN_142293ad0</c> → <c>weapon+0xec</c> bit
    /// <c>0x04</c>.
    /// </para>
    /// </summary>
    /// <param name="firing">false ⇒ bit 0 set ⇒ stop firing.</param>
    public static byte[] FireState(
        uint ownerTransientId,
        ulong equipmentRowGuid,
        bool firing,
        System.Numerics.Vector4 aimPoint,
        bool auxiliaryFlag = false)
    {
        GuardOwner(ownerTransientId);
        GuardEquipmentRowGuid(equipmentRowGuid);

        byte flags = 0x02;                              // bit 1 = an aim point follows
        if (!firing)
        {
            flags |= 0x01;
        }

        if (auxiliaryFlag)
        {
            flags |= 0x04;
        }

        using var w = new PacketWriter(48);
        WriteUpdateHeader(w, ownerTransientId, WeaponUpdateType.FireState, equipmentRowGuid);
        w.WriteByte(flags);
        w.WriteSingle(aimPoint.X);
        w.WriteSingle(aimPoint.Y);
        w.WriteSingle(aimPoint.Z);
        w.WriteSingle(aimPoint.W);
        return w.Written.ToArray();
    }

    /// <summary>Byte length of <see cref="FireState"/> in its point form.</summary>
    public static int FireStateLength(uint ownerTransientId) =>
        UpdateHeaderLength(ownerTransientId) + sizeof(byte) + (4 * sizeof(float));

    /// <summary>
    /// Type <c>0x03 Reload</c> — handler <c>FUN_141498fa0</c>, no payload, not strict. Posts entity
    /// event <b>7</b> <c>{slot}</c>: the reload animation on the proxied character.
    /// </summary>
    public static byte[] Reload(uint ownerTransientId, ulong equipmentRowGuid) =>
        Update(ownerTransientId, WeaponUpdateType.Reload, equipmentRowGuid);

    /// <summary>
    /// Type <c>0x04 ReloadLoopEnd</c> — handler <c>FUN_141499040</c>, payload <c>u8 bool</c>,
    /// strict. Posts event <b>8</b> <c>{slot, bool}</c>. The pump/shell loop's exit.
    /// </summary>
    public static byte[] ReloadLoopEnd(uint ownerTransientId, ulong equipmentRowGuid, bool value) =>
        Update(ownerTransientId, WeaponUpdateType.ReloadLoopEnd, equipmentRowGuid, w => w.WriteBool(value));

    /// <summary>
    /// Type <c>0x05 ReloadInterrupt</c> — <c>FUN_142291750(weapon, "HandleReloadInterrupt", 0)</c>,
    /// no payload, not strict. State <c>+0x44</c>: 10 → 1 (idle) with the pending count cleared;
    /// 0x0b/0x0c → 0x0d with a recomputed count.
    /// </summary>
    public static byte[] ReloadInterrupt(uint ownerTransientId, ulong equipmentRowGuid) =>
        Update(ownerTransientId, WeaponUpdateType.ReloadInterrupt, equipmentRowGuid);

    /// <summary>
    /// Type <c>0x06 SwitchFireMode</c> — handler <c>FUN_1414996a0</c>, payload
    /// <c>i8 fireGroupIndex; i8 fireModeIndex</c>, strict. <c>FUN_142291b90</c> refuses an index out
    /// of range or a mode whose <c>+0x18</c> charge is 0, logging
    /// <c>"attempted to switch to invalid fire mode"</c>; the modes therefore have to have been
    /// built by an <see cref="AddWeapon"/> or <see cref="Reset"/> blob first.
    /// <para>
    /// This is the packet S5c §0 finding 3 decodes out of the owner's own 21-byte
    /// <c>Weapon.Weapon</c>, byte for byte, once the 1087 family header loses its extra <c>00</c>.
    /// </para>
    /// </summary>
    public static byte[] SwitchFireMode(
        uint ownerTransientId,
        ulong equipmentRowGuid,
        sbyte fireGroupIndex,
        sbyte fireModeIndex) =>
        Update(ownerTransientId, WeaponUpdateType.SwitchFireMode, equipmentRowGuid, w =>
        {
            w.WriteByte(unchecked((byte)fireGroupIndex));
            w.WriteByte(unchecked((byte)fireModeIndex));
        });

    /// <summary>
    /// Type <c>0x07 StatUpdate</c> — handler <c>FUN_141499510</c>, payload
    /// <c>u8 kind; u32 statId; hashedString; statValue</c>, strict.
    /// <c>FUN_141482700</c> routes kind 1 into the by-hash table at <c>weapon+0xf0</c> and any other
    /// kind into <c>weapon+0x120[kind][statId]</c>.
    /// <para>
    /// Only the <b>inline</b> hashed-string form is written: <c>FUN_140a12af0</c> takes the hash from
    /// the 13 low bits plus <c>(h &amp; 0xe000) &lt;&lt; 18</c> when bit <c>0x8000</c> is set, and
    /// reads a NUL-terminated string to hash otherwise. Bit <c>0x4000</c> (a trailing <c>u32</c>) is
    /// never set. The value is <c>FUN_140a30280</c>'s
    /// <c>u32 a; u8 type; if type in {0,1}: u32 b; u32 c</c> — type 2 is the short, three-field form.
    /// </para>
    /// </summary>
    public static byte[] StatUpdate(
        uint ownerTransientId,
        ulong equipmentRowGuid,
        byte kind,
        uint statId,
        ushort inlineHash,
        uint value) =>
        Update(ownerTransientId, WeaponUpdateType.StatUpdate, equipmentRowGuid, w =>
        {
            w.WriteByte(kind);
            w.WriteUInt32(statId);
            w.WriteUInt16((ushort)(inlineHash | 0x8000));   // inline hash, no trailing u32
            w.WriteUInt32(value);                           // statValue.a
            w.WriteByte(2);                                 // statValue.type 2 => no b/c
        });

    /// <summary>
    /// Type <c>0x0b ProjectileLaunch</c> — handler <c>FUN_141498cf0</c>, payload
    /// <c>u32 projectileId; u64 launchParams</c>, strict.
    /// <c>FUN_140c7bb60(character, weapon, projectileId, &amp;params)</c> spawns the visible
    /// projectile. The 799-line spawner is not decoded, so <c>launchParameters</c> is <b>[U]</b>:
    /// pass 0 until a capture says otherwise.
    /// </summary>
    public static byte[] ProjectileLaunch(
        uint ownerTransientId,
        ulong equipmentRowGuid,
        uint projectileId,
        ulong launchParameters = 0) =>
        Update(ownerTransientId, WeaponUpdateType.ProjectileLaunch, equipmentRowGuid, w =>
        {
            w.WriteUInt32(projectileId);
            w.WriteUInt64(launchParameters);
        });

    /// <summary>
    /// Type <c>0x0c Chamber</c> — handler <c>FUN_141498440</c>, no payload, not strict. Posts event
    /// <b>0xe</b> <c>{slot}</c>, and only when the weapon's equipment slot is non-zero: a stowed
    /// weapon cannot chamber.
    /// </summary>
    public static byte[] Chamber(uint ownerTransientId, ulong equipmentRowGuid) =>
        Update(ownerTransientId, WeaponUpdateType.Chamber, equipmentRowGuid);

    /// <summary>
    /// Type <c>0x0f ChamberInterrupt</c> — <c>FUN_142291680</c>, no payload, not strict. Only acts
    /// when the state machine is in <c>0x0f</c> (chambering); it moves to <c>0x0e</c>.
    /// </summary>
    public static byte[] ChamberInterrupt(uint ownerTransientId, ulong equipmentRowGuid) =>
        Update(ownerTransientId, WeaponUpdateType.ChamberInterrupt, equipmentRowGuid);

    /// <summary>
    /// Type <c>0x10 AimBlocked</c> — handler <c>FUN_141498320</c>, payload <c>u8 bool</c>, strict.
    /// Posts event <b>0x15</b> <c>{slot, bool}</c>: the proxied character's muzzle is against
    /// geometry.
    /// </summary>
    public static byte[] AimBlocked(uint ownerTransientId, ulong equipmentRowGuid, bool blocked) =>
        Update(ownerTransientId, WeaponUpdateType.AimBlocked, equipmentRowGuid, w => w.WriteBool(blocked));

    /// <summary>An <c>UpdateBase</c> with an arbitrary payload; the typed writers above call it.</summary>
    public static byte[] Update(
        uint ownerTransientId,
        WeaponUpdateType updateType,
        ulong equipmentRowGuid,
        Action<PacketWriter>? payload = null)
    {
        GuardOwner(ownerTransientId);
        GuardEquipmentRowGuid(equipmentRowGuid);

        using var w = new PacketWriter(48);
        WriteUpdateHeader(w, ownerTransientId, updateType, equipmentRowGuid);
        payload?.Invoke(w);
        return w.Written.ToArray();
    }

    // -------------------------------------------------------------------------------------
    // Subs 0x05 / 0x06 / 0x07 — the projectile hints
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// <c>82 15 05 ProjectileLaunchHint</c> — reader <c>FUN_1414947b0</c>, handler
    /// <c>FUN_141498e50</c> (S5c §3.6):
    /// <c>u64 weaponItemInstanceId; u32 projectileId; f32 x3 position; f32 x3 direction</c>.
    /// <para>
    /// <b>The one sub whose handler consumes the header <c>u32</c>.</b>
    /// <c>FUN_141497880</c> appends <c>{gameTime, projectileId, vec3a, vec3b}</c> to the ring at
    /// <c>weapon+0x1c8</c>, so the launch time is a real field here — and, like every other
    /// <c>82 15</c> in this file, this writer sends 0 for it, meaning "now". A relay that wants the
    /// shot played back in step with the shooter's own movement stream must carry the shooter's
    /// stream time instead; that is a Phase 3C question and is deliberately not exposed yet.
    /// </para>
    /// <para>Remote subs <c>01</c>, <c>02</c> and <c>05</c> bypass the timed queue entirely
    /// (S5c §3.1), so this hint is never deferred whatever the header says.</para>
    /// </summary>
    public static byte[] ProjectileLaunchHint(
        uint ownerTransientId,
        ulong equipmentRowGuid,
        uint projectileId,
        System.Numerics.Vector3 position,
        System.Numerics.Vector3 direction)
    {
        GuardOwner(ownerTransientId);
        GuardEquipmentRowGuid(equipmentRowGuid);

        using var w = new PacketWriter(64);
        WriteHeader(w, RemoteSub.ProjectileLaunchHint, ownerTransientId);
        w.WriteUInt64(equipmentRowGuid);
        w.WriteUInt32(projectileId);
        WriteVector3(w, position);
        WriteVector3(w, direction);
        return w.Written.ToArray();
    }

    /// <summary>Byte length of <see cref="ProjectileLaunchHint"/>.</summary>
    public static int ProjectileLaunchHintLength(uint ownerTransientId) =>
        HeaderLength(ownerTransientId) + sizeof(ulong) + sizeof(uint) + (6 * sizeof(float));

    /// <summary>
    /// <c>82 15 06 ProjectileDetonateHint</c> — reader <c>FUN_1414946a0</c>, handler
    /// <c>FUN_141498bb0</c>: <c>u32 projectileId; f32 x3 position</c>. The client finds the
    /// projectile by <c>(ownerHandle, projectileId)</c> in the manager at <c>world+0x38530</c> and
    /// detonates it there. <b>No <c>weaponItemInstanceId</c>:</b> the projectile is addressed by the
    /// character, not by the gun that fired it.
    /// </summary>
    public static byte[] ProjectileDetonateHint(
        uint ownerTransientId,
        uint projectileId,
        System.Numerics.Vector3 position)
    {
        GuardOwner(ownerTransientId);

        using var w = new PacketWriter(32);
        WriteHeader(w, RemoteSub.ProjectileDetonateHint, ownerTransientId);
        w.WriteUInt32(projectileId);
        WriteVector3(w, position);
        return w.Written.ToArray();
    }

    /// <summary>Byte length of <see cref="ProjectileDetonateHint"/>.</summary>
    public static int ProjectileDetonateHintLength(uint ownerTransientId) =>
        HeaderLength(ownerTransientId) + sizeof(uint) + (3 * sizeof(float));

    /// <summary>
    /// <b>Sub <c>0x07 ProjectileRemoteContactReport</c> is refused, not guessed.</b> S5c §3.6 gives
    /// its layout as <b>[P]</b> and its names as <b>[U]</b>:
    /// <c>u32 projectileId; u64 ?; f32 x4 quaternion; f32 x3 position; u32 x3; u32 x3; u32;
    /// hashedString; u32; u8</c> — reader <c>FUN_141494180</c>, handler <c>FUN_141498940</c>.
    /// <para>
    /// Six of its thirteen fields have no meaning attached and one is a <c>hashedString</c> whose
    /// namespace is unknown (a surface-material id would come from a table this project has not
    /// extracted). Zeros would tell the client a bullet struck the world origin on an unnamed
    /// surface: a visible artefact for no gain. <see cref="ContactReportPrefix"/> emits the derived
    /// prefix and there is no full writer until the fields are named.
    /// </para>
    /// </summary>
    public const bool ContactReportNotYetDerived = true;

    /// <summary>
    /// The proven prefix of sub <c>0x07</c>: <c>82 | u32 0 | 15 | 07 | varint owner | u32
    /// projectileId</c>. Everything after it is <see cref="ContactReportNotYetDerived"/>.
    /// </summary>
    public static byte[] ContactReportPrefix(uint ownerTransientId, uint projectileId)
    {
        GuardOwner(ownerTransientId);

        using var w = new PacketWriter(16);
        WriteHeader(w, RemoteSub.ProjectileRemoteContactReport, ownerTransientId);
        w.WriteUInt32(projectileId);
        return w.Written.ToArray();
    }

    private static int BodyLength(IReadOnlyList<RemoteWeaponEntry> arsenal)
    {
        int bodyLength = sizeof(uint) + sizeof(int);
        foreach (RemoteWeaponEntry entry in arsenal)
        {
            bodyLength += sizeof(ulong) + entry.Weapon.Length;
        }

        return bodyLength;
    }

    private static void WriteHeader(PacketWriter w, RemoteSub sub, uint ownerTransientId)
    {
        w.WriteByte(Family);
        w.WriteUInt32(ImmediateGameTime);
        w.WriteByte(Sub);
        w.WriteByte((byte)sub);
        ClientVarInt.Write(w, ownerTransientId);
    }

    private static void WriteUpdateHeader(
        PacketWriter w,
        uint ownerTransientId,
        WeaponUpdateType updateType,
        ulong equipmentRowGuid)
    {
        WriteHeader(w, RemoteSub.UpdateBase, ownerTransientId);
        w.WriteByte((byte)updateType);
        w.WriteUInt64(equipmentRowGuid);
    }

    private static void WriteVector3(PacketWriter w, System.Numerics.Vector3 v)
    {
        w.WriteSingle(v.X);
        w.WriteSingle(v.Y);
        w.WriteSingle(v.Z);
    }
}

/// <summary>
/// One weapon in a <see cref="RemoteWeaponPackets.Reset"/> arsenal: the equipment-row guid the
/// viewer already knows the item by, and the component state blob.
/// </summary>
public sealed record RemoteWeaponEntry(ulong EquipmentRowGuid, RemoteWeaponBlob Weapon);

/// <summary>
/// One fire group inside a <see cref="RemoteWeaponBlob"/>. Read by <c>FUN_141495190</c> then
/// resolved by <c>FUN_142290f60</c> against <c>WeaponDefinitions</c> list 1: when the group id is
/// known and <see cref="Modes"/> is empty the client uses the <em>definition's</em> mode count.
/// However, its constructors <c>1422a3f10/1422a3f40</c> zero runtime charge at +0x18;
/// firing peers must carry explicit mode charges or the trigger gate rejects them.
/// </summary>
/// <param name="FireGroupId">u32 → <c>entry+0x18</c>.</param>
/// <param name="Modes">
/// The wire mode list. Each mode is <c>u32 → mode+0x18</c> (the charge/ammo count
/// <c>FUN_142291b90</c> requires to be greater than 0 before it will select the mode) and
/// <c>u32 → mode+0x1c</c> ([U]). <b>Eight bytes per mode, not the local component's thirteen</b> —
/// S5c §3.6 flags the difference explicitly.
/// </param>
public sealed record RemoteFireGroup(uint FireGroupId, IReadOnlyList<RemoteFireMode> Modes)
{
    public int Length => sizeof(uint) + sizeof(byte) + (Modes.Count * RemoteFireMode.WireLength);
}

/// <summary>One mode of a <see cref="RemoteFireGroup"/>.</summary>
/// <param name="Charge">
/// <c>mode+0x18</c>. Must be greater than 0 or <c>FUN_142291b90</c> refuses to select the mode and
/// logs <c>"attempted to switch to invalid fire mode"</c>.
/// </param>
/// <param name="Reserved"><c>mode+0x1c</c> — <b>[U]</b>, no consumer found. Zero.</param>
public readonly record struct RemoteFireMode(uint Charge, uint Reserved = 0)
{
    public const int WireLength = 2 * sizeof(uint);
}

/// <summary>
/// The nested <c>WeaponComponent</c> state blob shared by <c>AddWeapon</c> and every
/// <c>Reset</c> entry — reader <c>FUN_141495340</c> (S5c §3.6):
/// <c>u32 weaponDefinitionId; i8 equipmentSlotId; i8 fireGroupCount;
/// per group { u32 fireGroupId; i8 fireModeCount; per mode { u32; u32 } };
/// i32 statCount; i32 statTableCount</c>.
/// <para>
/// The proxied weapon object is a <c>WeaponComponent</c> subclass (ctor <c>FUN_141491da0</c>, vtable
/// <c>0x143255928</c>), so these are the same offsets the local component uses — but the per-mode
/// record is <b>not</b> the local one (S5c §3.6).
/// </para>
/// </summary>
/// <param name="WeaponDefinitionId">
/// <c>weapon+0x08</c>. The key <c>FUN_14147f4c0</c> looks up in <c>ReferenceData
/// "WeaponDefinitions"</c> list 0 — the same id the local player's own weapon uses, so a peer's gun
/// and yours resolve through one table.
/// </param>
/// <param name="EquipmentSlotId">
/// <c>weapon+0x40</c>, and the hash key of the by-slot list. <b>0 = stowed.</b> A non-zero slot
/// links the weapon into <c>mgr+0xf0</c>; the two hand slots (<c>cfg+0x228</c>/<c>+0x22c</c>,
/// inferred 7 and 8) additionally fire the "weapon entered this character's hand" listeners through
/// <c>FUN_140dc9bb0</c> — which is what actually puts the gun in a peer's hands.
/// </param>
/// <param name="FireGroups">See <see cref="RemoteFireGroup"/>.</param>
public sealed record RemoteWeaponBlob(
    uint WeaponDefinitionId,
    sbyte EquipmentSlotId,
    IReadOnlyList<RemoteFireGroup> FireGroups)
{
    /// <summary>
    /// <c>cfg+0x228</c>, the primary hand. <b>[I]</b> — S5c §3.5: <c>FUN_140cd8be0</c> treats a slot
    /// as a hand when <c>slot - 7 &lt; 2</c> and <c>FUN_1411ceca0</c> resolves the held weapon
    /// through <c>+0x228</c>; the loader that fills the two globals was not found.
    /// </summary>
    public const sbyte RightHandSlot = 7;

    /// <summary><c>cfg+0x22c</c>, the second hand slot. [I], same evidence.</summary>
    public const sbyte SecondHandSlot = 8;

    /// <summary>Slot 0 — registered against the character but in no hand.</summary>
    public const sbyte StowedSlot = 0;

    /// <summary>
    /// Blob length. The two trailing <c>i32</c>s are the stat list and the nested stat-table list
    /// (<c>weapon+0xf0</c> / <c>weapon+0x120</c>, readers <c>FUN_141484b70</c> /
    /// <c>FUN_141484740</c>); both are written empty — a peer's HUD reads no weapon stats.
    /// </summary>
    public int Length
    {
        get
        {
            int length = sizeof(uint) + sizeof(byte) + sizeof(byte) + sizeof(int) + sizeof(int);
            foreach (RemoteFireGroup group in FireGroups)
            {
                length += group.Length;
            }

            return length;
        }
    }

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteUInt32(WeaponDefinitionId);                      // weapon+0x08
        w.WriteByte(unchecked((byte)EquipmentSlotId));          // weapon+0x40
        w.WriteByte(checked((byte)FireGroups.Count));           // resizes weapon+0x28
        foreach (RemoteFireGroup group in FireGroups)
        {
            w.WriteUInt32(group.FireGroupId);                   // entry+0x18
            w.WriteByte(checked((byte)group.Modes.Count));
            foreach (RemoteFireMode mode in group.Modes)
            {
                w.WriteUInt32(mode.Charge);                     // mode+0x18
                w.WriteUInt32(mode.Reserved);                   // mode+0x1c [U]
            }
        }

        w.WriteInt32(0);                                        // weapon+0xf0  stat list
        w.WriteInt32(0);                                        // weapon+0x120 stat tables
    }

    public byte[] ToArray()
    {
        using var w = new PacketWriter(64);
        WriteTo(w);
        return w.Written.ToArray();
    }
}
