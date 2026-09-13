using Cranberry.Protocol;

namespace Cranberry.Zone.Movement;

/// <summary>
/// The two stat packets that deliver movement numbers to a client (docs/40 §7).
/// <para>
/// <b>Who owns what.</b> The server owns the numbers, the client owns the arithmetic. Speed is not
/// in the movement packet and not in a client datasheet: it is a per-character <c>StatId</c> map at
/// <c>entity+0x3dd0</c> populated **only** from the wire, and <c>GetStat</c> returns the caller's
/// literal default when a stat is absent — <c>1.0f</c> at every speed-modifier call site. A server
/// that never sends a stat therefore makes all six movement modes run at exactly the base speed,
/// which is the symptom this lane exists to fix (docs/40 §0).
/// </para>
/// </summary>
public static class CharacterStatPackets
{
    /// <summary>Base opcode of the character family (<c>0x0f</c>), whose sub id is a <b>u8</b>.</summary>
    public const byte CharacterOpcode = ZoneOpcodes.CharacterBase;

    /// <summary><c>Character.UpdateStat</c> sub <c>0x40</c> — entity dispatcher <c>FUN_140c4d240</c> case <c>0x3f</c>.</summary>
    public const byte UpdateStatSubOpcode = 0x40;

    /// <summary>Base opcode of the ClientUpdate family (<c>0x11</c>), whose sub id is a <b>u16 LE</b>.</summary>
    public const byte ClientUpdateOpcode = ZoneOpcodes.ClientUpdateBase;

    /// <summary><c>ClientUpdate.UpdateStat</c> sub <c>0x0005</c> — dispatcher <c>FUN_140afc660</c> case 5.</summary>
    public const ushort ClientUpdateStatSubOpcode = 0x0005;

    /// <summary>Header bytes of <c>0f 40</c> before the entries: <c>u8; u8; u64 guid; u32 count</c>.</summary>
    public const int UpdateStatHeaderLength = 14;

    /// <summary>Header bytes of <c>11 05</c> before the entries: <c>u8; u16; u32 count</c>.</summary>
    public const int ClientUpdateStatHeaderLength = 7;

    /// <summary>
    /// <c>Character.UpdateStat</c> — <c>0f 40</c>, s2c. <b>The only channel that writes a character's
    /// stat map</b>, and therefore the only channel every speed in docs/40 §2 can see.
    /// <para>
    /// Layout (reader <c>FUN_140c260c0</c>): <c>u8 0x0f; u8 0x40; u64 guid; u32 count;
    /// count × Stat</c>. For each parsed entry the client calls <c>entity-&gt;vtable[0x7b8]</c>
    /// (<c>FUN_140c871a0</c>, the map insert) and then, once, <c>entity-&gt;vtable[0x7c0]</c>
    /// (<c>FUN_140c60380</c>) which refreshes the eight animation blend parameters (docs/40 §3).
    /// </para>
    /// <para>
    /// Like every <c>0x0f</c> sub it is <b>silently dropped when the guid is not in the entity
    /// manager</b> (docs/21 §1b), so it must be sent after the character record — never before. It
    /// works against the local player's own guid and any remote character alike. Re-sending a
    /// partial list is fine: the map is keyed and entries update in place.
    /// </para>
    /// </summary>
    /// <param name="CharacterGuid">The character the stats belong to.</param>
    /// <param name="Stats">The entries; the reader rejects a declared count that overruns the buffer.</param>
    public sealed record UpdateStat(ulong CharacterGuid, IReadOnlyList<CharacterStat> Stats)
    {
        public const byte Opcode = CharacterOpcode;
        public const byte SubOpcode = UpdateStatSubOpcode;

        /// <summary>Bytes this packet writes.</summary>
        public int Length => UpdateStatHeaderLength + (Stats.Count * CharacterStat.Length);

        /// <summary>The full movement burst of docs/40 §8.1 — 18 entries, 248 bytes.</summary>
        public static UpdateStat ForProfile(ulong characterGuid, MovementProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return new UpdateStat(characterGuid, profile.ToStats());
        }

        public void WriteTo(PacketWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(Stats);
            writer.WriteByte(Opcode);
            writer.WriteByte(SubOpcode);
            writer.WriteUInt64(CharacterGuid);
            CharacterStat.WriteList(writer, Stats);
        }
    }

    /// <summary>
    /// <c>ClientUpdate.UpdateStat</c> — <c>11 05</c>, s2c, owning client only. Layout (reader
    /// <c>FUN_140a61520</c>): <c>u8 0x11; u16 LE 0x0005; u32 count; count × Stat</c> — note the
    /// ClientUpdate family's sub id is 16-bit, unlike <c>0x0f</c>'s.
    /// <para>
    /// <b>This is not a substitute for <see cref="UpdateStat"/>.</b> The handler never touches
    /// <c>entity+0x3dd0</c>: it is gated on a UI object (<c>client[0x62b7]+0x1688</c>), pushes each
    /// entry into a UI stat panel through <c>FUN_140dc9070</c>, and only for
    /// <c>statId == 2</c> (<c>StatId.MaxMovementSpeed</c>, matched by the inline hash
    /// <c>0x85c3f4d2</c>) writes <c>base + modifier</c> to <c>localPlayer+0xb48</c>, the base-speed
    /// cache. Send it alongside the <c>0f 40</c> burst so the UI and the cache agree with the map;
    /// whether it is needed at all is docs/40 §9 open question 5, answerable by one live test
    /// (docs/40 §7.3, §8.1).
    /// </para>
    /// </summary>
    public sealed record ClientUpdateStat(IReadOnlyList<CharacterStat> Stats)
    {
        public const byte Opcode = ClientUpdateOpcode;
        public const ushort SubOpcode = ClientUpdateStatSubOpcode;

        /// <summary>Bytes this packet writes.</summary>
        public int Length => ClientUpdateStatHeaderLength + (Stats.Count * CharacterStat.Length);

        /// <summary>
        /// The single <c>MaxMovementSpeed</c> entry of docs/40 §8.1 step 2 — 20 bytes. It is the one
        /// stat this packet does anything numeric with.
        /// </summary>
        public static ClientUpdateStat BaseSpeed(MovementProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return new ClientUpdateStat(
                [CharacterStat.Float(CharacterStatId.MaxMovementSpeed, profile.MaxMovementSpeed)]);
        }

        public void WriteTo(PacketWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(Stats);
            writer.WriteByte(Opcode);
            writer.WriteUInt16(SubOpcode);
            CharacterStat.WriteList(writer, Stats);
        }
    }
}
