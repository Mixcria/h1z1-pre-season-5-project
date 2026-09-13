using Cranberry.Protocol;

namespace Cranberry.Zone.World.Doors;

/// <summary>
/// s2c <c>Character.SetCollidable</c> (base <c>0x0f</c>, u8 sub <c>0x1e</c>) — <b>exactly 11
/// bytes</b>: <c>u8 0x0f; u8 0x1e; u64 characterGuid; u8 collidable</c>. The same two-byte header
/// shape as <see cref="DoorStateUpdate"/>.
/// <para>
/// <b>Ported from the owner's Z1 server</b> (<c>C:\Z1\Server\Zone\ZoneDoors.cs</c>,
/// <c>SetCollidable</c>) under D53, re-expressed against 1148. The opcode pair needs no transform:
/// the wave-9 bridge measured that <c>0x0f 0x1e</c> is byte-identical between 1087 and 1148, and
/// <c>out/registrations-1148.md</c> gives <c>0x1e = cCharacterPacketIdSetCollidable</c> on this
/// build. His layout was derived from the 1087 reader <c>FUN_1404f0510</c>, which re-reads the base
/// at <c>buf[0]</c> and the sub at <c>buf[1]</c>, takes the u64 at <c>buf[2]</c> and the bool at
/// <c>buf[10]</c>, and is called with <c>allowTrailing = 0</c> — so the length is EXACT and a
/// twelfth byte would make the client drop the packet without a word.
/// </para>
/// <para>
/// <b>This is the same mechanism as the spawn record's collision bit, said a second time.</b>
/// docs/85 §2b: the only thunk to the handler <c>FUN_140c75ef0</c> has four call sites, and two of
/// them are the <c>0xd6</c> apply (<c>+0x1b1 &gt;&gt; 4 &amp; 1</c>, i.e.
/// <see cref="LightweightEntityBody.CollidableFlag"/>) and <c>0x140c4f905</c> inside the
/// <c>0x0f</c> sub-dispatcher <c>FUN_140c4d240</c>, which feeds it this packet's bool from
/// <c>[rbp+0xc68]</c>. So this packet is not a second lever on a second system — it is the same
/// call, made later, when the entity is complete.
/// </para>
/// <para>
/// <b>Why it must be sent as a <c>false</c> then <c>true</c> PAIR</b> (and why the owner never hit
/// this: his copy is parked). <c>FUN_140c75ef0</c> opens with
/// <c>movzx eax,[rcx+0x37e6] / shr al,3 / and al,1 / cmp dl,al / je done</c> — it is idempotent. Once
/// the spawn record's bit 4 has already set <c>entity+0x37e6</c> bit 3, a lone
/// <c>SetCollidable(true)</c> returns at that first branch and never reaches the actor. Only the
/// <c>false</c> → <c>true</c> pair guarantees the actor half runs.
/// </para>
/// <para>
/// <b>Off by default.</b> See <c>ZoneOptions.DoorSetCollidable</c>: the owner ran exactly this
/// packet at 1087 in his round 23, and turned it off again in round 27 because his click-proven
/// reference server sends zero of them and its doors are solid on the spawn flag alone, while his
/// own copy — arriving in the spawn's own datagram train, at a half-built actor — was his prime
/// suspect for three filmed door <em>rendering</em> faults. Porting the writer costs nothing while
/// it is parked; turning it on is experiment B.
/// </para>
/// </summary>
public sealed record SetCollidablePacket(ulong CharacterGuid, bool Collidable)
{
    public const byte Opcode = ZoneOpcodes.CharacterBase;
    public const byte SubOpcode = 0x1e;

    /// <summary>2 header + 8 guid + 1 bool. The 1087 reader runs with <c>allowTrailing = 0</c>, and
    /// the August handler is reached through the same fixed-length sub-dispatcher, so this is a hard
    /// length rather than a minimum.</summary>
    public const int Length = 11;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);           // buf[0]  — the base, re-read by the packet's own reader
        writer.WriteByte(SubOpcode);        // buf[1]  — cCharacterPacketIdSetCollidable
        writer.WriteUInt64(CharacterGuid);  // buf[2]  — the entity to make solid
        writer.WriteByte(Collidable ? (byte)1 : (byte)0);   // buf[10] — handed to FUN_140c75ef0
    }
}
