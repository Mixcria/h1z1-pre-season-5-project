namespace Cranberry.Zone.Weapons;

/// <summary>
/// The capture side of this wave: a formatter for an <b>inbound</b> <c>0x82 WeaponBase</c> packet
/// that prints its sub-id and its <b>complete</b> hex, so that the first time the owner wields a
/// weapon and pulls the trigger, the c2s <c>Fire</c> (<c>82 03</c>) and <c>ProjectileHitReport</c>
/// (<c>82 06</c>) bytes are on disk and decodable.
/// <para>
/// <b>Why this exists.</b> docs/16 §5 and docs/20 established that the c2s combat layouts have
/// <em>never been captured</em>, because no client has ever held a weapon: docs/56 §1.3 counted
/// <b>zero</b> <c>0x82</c> packets of any sub across all three 2026-08-30 sessions. Their layouts
/// are therefore NOT guessed anywhere in this tree - the deliberate alternative is to make the
/// client speak them and record what it says. <b>Any <c>0x82</c> at all is the proof that the attack
/// path came alive.</b>
/// </para>
/// <para>
/// <b>Why the existing default log line is not enough.</b> <c>ZoneService</c>'s catch-all branch
/// prints <c>Convert.ToHexString(payload[..min(48, len)])</c> - truncated at 48 bytes. A
/// <c>ProjectileHitReport</c> carrying a position, a normal and a target guid will be longer than
/// that, and a half-recorded packet cannot be decoded afterwards. <see cref="Format"/> never
/// truncates.
/// </para>
/// <para>
/// <b>This type only formats.</b> It never answers a <c>0x82</c>: replying to a packet whose layout
/// is unknown is how a client gets desynchronised, and the owner's session is worth more as a clean
/// recording than as a guess.
/// </para>
/// </summary>
public static class WeaponBasePacketTrace
{
    /// <summary><c>WeaponBase</c>, 0x82.</summary>
    public const byte Opcode = ZoneOpcodes.WeaponBase;

    /// <summary>
    /// The tag every traced line carries. <b>This is the string to grep for</b> - it is deliberately
    /// unique in the tree and in the log, so <c>docs/60 §6</c>'s extraction command is a single
    /// <c>findstr</c>.
    /// </summary>
    public const string Tag = "WEAPONFIRE";

    /// <summary>
    /// The sub-id byte's position. The <c>0x82</c> family header is <c>u8 opcode; u32 (read and
    /// never used); u8 sub</c> - docs/20 §2, re-confirmed by <c>FUN_140a2e9b0</c>, which every
    /// sub-reader uses to re-consume the header from wire offset 0.
    /// </summary>
    public const int SubOpcodeOffset = 5;

    /// <summary>Bytes before a sub's own body: <c>u8; u32; u8</c>.</summary>
    public const int FamilyHeaderLength = 6;

    /// <summary>
    /// The sub-ids docs/16 §1a named for the local player's attack path. Naming them costs nothing
    /// and makes the recorded line self-describing; an unnamed sub is still printed in full.
    /// </summary>
    public static string SubOpcodeName(byte sub) => sub switch
    {
        0x03 => "Fire?",
        0x06 => "ProjectileHitReport?",
        0x11 => "s2c group-add",
        0x13 => "s2c group-replace",
        0x15 => "s2c remote-weapon-update",
        _ => "unknown",
    };

    /// <summary>
    /// True when this inbound payload is a <c>0x82</c> the trace should record.
    /// </summary>
    public static bool IsWeaponBase(ReadOnlySpan<byte> payload) =>
        payload.Length >= 1 && payload[0] == Opcode;

    /// <summary>
    /// The line to log, verbatim and untruncated. Shape:
    /// <code>
    /// WEAPONFIRE 0x82 sub=0x03 (Fire?) len=NN body=MM hex=8203000000...
    /// </code>
    /// A payload too short to carry the family header still prints, with <c>sub=?</c> - a runt
    /// <c>0x82</c> would itself be a finding.
    /// </summary>
    public static string Format(ReadOnlySpan<byte> payload)
    {
        string hex = Convert.ToHexString(payload);
        if (payload.Length <= SubOpcodeOffset)
        {
            return $"{Tag} 0x82 sub=? (runt) len={payload.Length} body=0 hex={hex}";
        }

        byte sub = payload[SubOpcodeOffset];
        int body = payload.Length - FamilyHeaderLength;
        return $"{Tag} 0x82 sub=0x{sub:x2} ({SubOpcodeName(sub)}) len={payload.Length} body={body} hex={hex}";
    }
}
