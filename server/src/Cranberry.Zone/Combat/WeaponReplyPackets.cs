using Cranberry.Protocol;

namespace Cranberry.Zone.Combat;

/// <summary>
/// <b>The s2c half of the <c>0x82</c> family</b> - the replies Cranberry owed the client and had
/// never sent. Ported from the owner's <c>ZoneCombatWire.cs</c> under D53 and re-expressed against
/// <c>ClientProtocol_1148</c>.
///
/// <para>
/// <b>The one shape difference a naive port gets wrong.</b> He writes the family header as
/// <c>83 00 | u32 gameTime | u8 sub</c> - two base bytes. 1148 writes <c>82 | u32 gameTime | u8
/// sub</c> - one. Two bytes collapse into one and the rest of the arrangement is identical
/// (<see cref="WeaponBaseDecoder.HeaderLength"/> = 6). Carrying his <c>00</c> across would
/// desynchronise every weapon packet by a byte, so it is deliberately absent here.
/// </para>
/// </summary>
public static class WeaponReplyPackets
{
    /// <summary><c>WeaponPacket::cIdReload</c> - the server's answer to a reload request.</summary>
    public const byte SubReload = 0x08;

    /// <summary><c>WeaponPacket::cIdReloadRejected</c> - the answer when there is nothing to load.</summary>
    public const byte SubReloadRejected = 0x0b;

    /// <summary><c>WeaponPacket::cIdFireRejected</c> - the answer to a shot this server refuses.</summary>
    public const byte SubFireRejected = 0x1e;

    /// <summary>
    /// <b>The game time every s2c <c>0x82</c> this server writes carries, and why it is zero.</b>
    /// The <c>u32</c> after the base byte is an <em>apply time</em> on the client's own game clock:
    /// <c>FUN_140b81830</c> case <c>0x82</c> dispatches immediately for subs <c>08 0f 11 12 24</c>
    /// and otherwise returns <c>(gameTime − (now − entityTimeBase) &lt; 1)</c>, queueing the packet
    /// on the timed list at <c>world+0x35238</c> when that is false. <b>Zero is therefore "now",
    /// always</b> - and the two rejection packets below are outside the immediate set, so a value
    /// echoed from the client's own request could defer a refusal by however far the two clocks
    /// have drifted (S5c §3.1, §6.4 recommendation 4).
    /// </summary>
    public const uint ImmediateGameTime = 0;

    /// <summary>
    /// <c>Weapon.Reload</c> (<c>82 08</c>):
    /// <c>u8 0x82; u32 gameTime; u8 0x08; u64 weaponGuid; u32 projectileCount; u32 ammoCount;
    /// u32 inventoryAmmoCount; u64 reloadCount</c>.
    /// <para>
    /// <b>Cranberry answered a reload with nothing at all</b> until wave 9: <c>OnReload</c> refilled
    /// a server-side counter and sent no packet, so a reload could only ever appear to do nothing.
    /// </para>
    /// </summary>
    public static byte[] Reload(
        uint gameTime,
        ulong weaponGuid,
        uint projectileCount,
        uint ammoCount,
        uint inventoryAmmoCount,
        ulong reloadCount)
    {
        using var writer = new PacketWriter(32);
        writer.WriteByte(ZoneOpcodes.WeaponBase);
        writer.WriteUInt32(gameTime);
        writer.WriteByte(SubReload);
        writer.WriteUInt64(weaponGuid);
        writer.WriteUInt32(projectileCount);
        writer.WriteUInt32(ammoCount);
        writer.WriteUInt32(inventoryAmmoCount);
        WriteReloadCount(writer, reloadCount);
        return writer.Written.ToArray();
    }

    /// <summary>
    /// <c>Weapon.ReloadRejected</c> (<c>82 0b</c>): <c>u8 0x82; u32 0; u8 0x0b; u64 weaponGuid</c> -
    /// <b>14 bytes</b>.
    /// <para>
    /// <b>This is the packet a refusal owes the client</b>, and until lane 1F nothing sent it,
    /// because nothing could refuse. The client's handler (<c>FUN_140a67190</c> →
    /// <c>FUN_140dc0880</c>) finds the item by guid or among the held items and, when its state is
    /// one of <c>{10, 0xb, 0xc}</c> - i.e. a reload really is in flight -, calls
    /// <c>FUN_142291750(comp, "RejectedByZone", 0)</c>, which drives the weapon back to state 1.
    /// Without it, a client that started a reload animation this server will not honour stays in
    /// the reload state until something else knocks it out: the "reloads but won't shoot" shape
    /// (S5c §5.3, §5.4 step 6).
    /// </para>
    /// </summary>
    public static byte[] ReloadRejected(ulong weaponGuid)
    {
        using var writer = new PacketWriter(16);
        writer.WriteByte(ZoneOpcodes.WeaponBase);
        writer.WriteUInt32(ImmediateGameTime);
        writer.WriteByte(SubReloadRejected);
        writer.WriteUInt64(weaponGuid);
        return writer.Written.ToArray();
    }

    /// <summary>
    /// <c>Weapon.FireRejected</c> (<c>82 1e</c>):
    /// <c>u8 0x82; u32 0; u8 0x1e; u64 weaponGuid; u8 flag; i8 fireGroupIndex; i8 fireModeIndex;
    /// u32 n; u32 projectileId[n]</c>.
    /// <para>
    /// <b>The client's own "that shot did not happen" reaction, and the answer to a dry trigger.</b>
    /// <c>FUN_140dc0770</c> resolves the fire mode with <c>FUN_14228f030(comp, group, mode)</c>,
    /// asks it for a refund with <c>FUN_1422a41f0</c> and, when that is positive, puts the rounds
    /// back into the client's own ammo slot (<c>FUN_14228bd80(comp, modeDef+0x5c, refund, 0)</c>) -
    /// which is exactly right for a refusal, because the client had already decremented its local
    /// magazine when it pulled the trigger. Every projectile id named here is then looked up and
    /// flagged <c>+0x651 |= 0x40; +0x652 |= 0x10</c> (rejected / hide), so the tracer the client
    /// drew is taken back off the screen (S5c §5.3).
    /// </para>
    /// <para>
    /// <b>Grade [I] on the field order</b>: docs/20 §2b recovered it from the owner's 1087 client,
    /// and 1148 numbers this sub identically, but no August capture of one exists - it has never
    /// been sent by anything. <see cref="CombatOptions.Enabled"/> is its off switch.
    /// </para>
    /// </summary>
    /// <param name="weaponGuid">The instance that refused.</param>
    /// <param name="fireGroupIndex">The group the client named; <c>WeaponSession</c>'s current is 0.</param>
    /// <param name="fireModeIndex">The mode the client named.</param>
    /// <param name="projectileIds">The ids from the refused <c>82 03 Fire</c>, so the client hides
    /// exactly the tracers it drew. May be empty.</param>
    public static byte[] FireRejected(
        ulong weaponGuid,
        sbyte fireGroupIndex,
        sbyte fireModeIndex,
        ReadOnlySpan<uint> projectileIds)
    {
        using var writer = new PacketWriter(32 + (projectileIds.Length * 4));
        writer.WriteByte(ZoneOpcodes.WeaponBase);
        writer.WriteUInt32(ImmediateGameTime);
        writer.WriteByte(SubFireRejected);
        writer.WriteUInt64(weaponGuid);
        writer.WriteByte(0);                                    // flag = 0: look the item up by guid
        writer.WriteByte(unchecked((byte)fireGroupIndex));
        writer.WriteByte(unchecked((byte)fireModeIndex));
        writer.WriteUInt32((uint)projectileIds.Length);

        foreach (uint id in projectileIds)
        {
            writer.WriteUInt32(id);
        }

        return writer.Written.ToArray();
    }

    /// <summary>
    /// Retire one local projectile after its authoritative contact detonation. The August
    /// FireRejected applier's projectile cleanup runs independently of its weapon refund:
    /// FUN_140b07010 case 0x1e finds (local character guid, projectile id) and marks the actor
    /// for removal; FUN_140f034a0 releases its model and physics callback on the next tick.
    /// No item is selected and both indices are invalid, so FUN_140dc0770 cannot refund ammo.
    /// This also works after the last bottle left the inventory. See docs/molotov-contact-20260906.md.
    /// </summary>
    public static byte[] RetireProjectile(uint projectileId) =>
        FireRejected(weaponGuid: 0, fireGroupIndex: -1, fireModeIndex: -1, [projectileId]);

    /// <summary>
    /// The August applier reads a native little-endian u64. Its matching-counter branch stages
    /// the pump's reserve for the continuing shell loop (FUN_1414887e0 / FUN_141487990).
    /// D133 copied a big-endian reference-server workaround that made 1 arrive as 2^56. That
    /// forced direct magazine reconciliation but could never match the client's expected 1,
    /// leaving its cached reserve empty and ending the pump animation after its first shell.
    /// Counters must match at acknowledgement and advance for later authoritative snapshots.
    /// See docs/shotgun-reload-counter-20260906.md, which supersedes that workaround.
    /// </summary>
    internal static void WriteReloadCount(PacketWriter writer, ulong count)
    {
        writer.WriteUInt64(count);
    }
}
