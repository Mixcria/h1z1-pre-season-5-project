using Cranberry.Zone;
using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone.Combat;

/// <summary>
/// Builds the c2s <c>0x82</c> packets the decoder reads, so a test can pin a layout without a client.
/// <para>
/// <b>This is the candidate layout under test, not an authority.</b> It writes exactly what
/// <c>docs/81</c> §2c says the owner's <c>ClientProtocol_1087</c> client writes, on the 1148 header
/// shape (<c>u8 0x82</c> rather than <c>u16 0x83 0x00</c>). If the August client turns out to
/// disagree, these builders are what change - and the decoder's plausibility gate is what makes the
/// disagreement visible in a log instead of paying damage out of a misread.
/// </para>
/// </summary>
internal static class ShootingPacketBuilder
{
    public static byte[] Header(byte sub, uint gameTime = 0)
    {
        var bytes = new List<byte> { ZoneOpcodes.WeaponBase };
        bytes.AddRange(BitConverter.GetBytes(gameTime));
        bytes.Add(sub);
        return [.. bytes];
    }

    /// <summary><c>82 01</c>: <c>u64 weaponGuid; u8 fireState; u8</c>.</summary>
    public static byte[] FireStateUpdate(ulong weaponGuid, byte fireState, byte trailer = 0, uint gameTime = 0)
    {
        var bytes = new List<byte>(Header(WeaponBaseDecoder.SubFireStateUpdate, gameTime));
        bytes.AddRange(BitConverter.GetBytes(weaponGuid));
        bytes.Add(fireState);
        bytes.Add(trailer);
        return [.. bytes];
    }

    /// <summary><c>82 03</c>: <c>u64 guid; f32 x,y,z; u32 count; {u32 id, u32}[count]</c>.</summary>
    public static byte[] Fire(
        ulong weaponGuid, float x, float y, float z, uint[] projectileIds, uint gameTime = 0)
    {
        ArgumentNullException.ThrowIfNull(projectileIds);
        var bytes = new List<byte>(Header(WeaponBaseDecoder.SubFire, gameTime));
        bytes.AddRange(BitConverter.GetBytes(weaponGuid));
        bytes.AddRange(BitConverter.GetBytes(x));
        bytes.AddRange(BitConverter.GetBytes(y));
        bytes.AddRange(BitConverter.GetBytes(z));
        bytes.AddRange(BitConverter.GetBytes((uint)projectileIds.Length));

        foreach (uint id in projectileIds)
        {
            bytes.AddRange(BitConverter.GetBytes(id));
            bytes.AddRange(BitConverter.GetBytes(0u));
        }

        return [.. bytes];
    }

    /// <summary>
    /// <c>82 06</c>, literal string form: <c>u32 projectileId; u64 characterId; f32 x,y,z;
    /// u16 header; text + NUL; u32; u32 entryCount; entry[20]{n}; u8 shots; u8 flags</c>.
    /// </summary>
    public static byte[] HitReport(
        uint projectileId,
        ulong characterId,
        string hitLocation,
        float x = 0,
        float y = 0,
        float z = 0,
        int hitEntries = 0,
        byte totalShots = 1,
        byte flags = 0x80,
        uint gameTime = 0)
    {
        ArgumentNullException.ThrowIfNull(hitLocation);
        var bytes = new List<byte>(Header(WeaponBaseDecoder.SubProjectileHitReport, gameTime));
        bytes.AddRange(BitConverter.GetBytes(projectileId));
        bytes.AddRange(BitConverter.GetBytes(characterId));
        bytes.AddRange(BitConverter.GetBytes(x));
        bytes.AddRange(BitConverter.GetBytes(y));
        bytes.AddRange(BitConverter.GetBytes(z));
        bytes.AddRange(BitConverter.GetBytes((ushort)hitLocation.Length));
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(hitLocation));
        bytes.Add(0);
        bytes.AddRange(BitConverter.GetBytes(0u));
        bytes.AddRange(BitConverter.GetBytes((uint)hitEntries));
        bytes.AddRange(new byte[hitEntries * WeaponBaseDecoder.HitEntryBytes]);
        bytes.Add(totalShots);
        bytes.Add(flags);
        return [.. bytes];
    }

    /// <summary>
    /// <c>82 06</c>, string-table form: bit 0x8000 set, so no text follows at all - the case a
    /// "read the low byte as a length" reader cannot see.
    /// </summary>
    public static byte[] HitReportByStringId(
        uint projectileId, ulong characterId, int stringId, byte totalShots = 1, byte flags = 0x80)
    {
        var bytes = new List<byte>(Header(WeaponBaseDecoder.SubProjectileHitReport));
        bytes.AddRange(BitConverter.GetBytes(projectileId));
        bytes.AddRange(BitConverter.GetBytes(characterId));
        bytes.AddRange(BitConverter.GetBytes(0f));
        bytes.AddRange(BitConverter.GetBytes(0f));
        bytes.AddRange(BitConverter.GetBytes(0f));
        bytes.AddRange(BitConverter.GetBytes((ushort)(0x8000 | stringId)));
        bytes.AddRange(BitConverter.GetBytes(0u));
        bytes.AddRange(BitConverter.GetBytes(0u));
        bytes.Add(totalShots);
        bytes.Add(flags);
        return [.. bytes];
    }

    /// <summary><c>82 07</c>: one u64, and it names an ITEM guid.</summary>
    public static byte[] ReloadRequest(ulong weaponGuid)
    {
        var bytes = new List<byte>(Header(WeaponBaseDecoder.SubReloadRequest));
        bytes.AddRange(BitConverter.GetBytes(weaponGuid));
        return [.. bytes];
    }

    /// <summary>
    /// <c>82 0c SwitchFireModeRequest</c>: <c>u64 weaponGuid; u8 fireGroupIndex; u8 fireModeIndex;
    /// u8</c> - the August client's ADS packet, 17 bytes with the header (docs/107 §2). Unlike the
    /// rest of this class the layout is not a candidate: 145 of these were decoded off the owner's
    /// own 2026-09-02 wire and every field came out in range.
    /// </summary>
    public static byte[] SwitchFireModeRequest(
        ulong weaponGuid,
        byte fireGroupIndex = 0,
        byte fireModeIndex = WeaponFireArm.AimDownSightsFireModeIndex,
        byte trailer = 0,
        uint gameTime = 0)
    {
        var bytes = new List<byte>(
            Header(WeaponBaseDecoder.SubSwitchFireModeRequest, gameTime));
        bytes.AddRange(BitConverter.GetBytes(weaponGuid));
        bytes.Add(fireGroupIndex);
        bytes.Add(fireModeIndex);
        bytes.Add(trailer);
        return [.. bytes];
    }

    /// <summary>
    /// <c>82 1f</c>: <c>u32 count</c> then <c>count</c> x (<c>u32 size</c>, <c>u8[size]</c>). Each
    /// member is a complete packet and is NOT re-framed.
    /// </summary>
    public static byte[] MultiWeapon(params byte[][] members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var bytes = new List<byte>(Header(WeaponBaseDecoder.SubMultiWeapon));
        bytes.AddRange(BitConverter.GetBytes((uint)members.Length));

        foreach (byte[] member in members)
        {
            bytes.AddRange(BitConverter.GetBytes((uint)member.Length));
            bytes.AddRange(member);
        }

        return [.. bytes];
    }
}
