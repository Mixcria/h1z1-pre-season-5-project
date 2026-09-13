using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>
/// The lightweight-entity body read by <c>FUN_140a2d040</c> — the record the August dispatcher
/// <c>FUN_140af3950</c> shares between <c>AddLightweightNpc</c> (0xd6, the body alone) and
/// <c>AddLightweightVehicle</c> (0xd7, this body plus the <c>FUN_140a2dd10</c> tail). Field offsets
/// in the comments are the client object offsets the parser stores to; helpers are the varint
/// <c>FUN_140a190f0</c>, the string <c>FUN_140b78f60</c> and the target block
/// <c>FUN_140a3d220</c>, whose block ends after its u64 when that guid is the null sentinel
/// <c>DAT_143f67f78</c> (0). Leftover bytes or a stream error reject the packet.
/// docs/12 §1 (vehicle) and docs/13 §3a (ground item) derive the same sequence.
/// </summary>
public sealed record LightweightEntityBody(
    byte Opcode,
    ulong Guid,
    uint TransientId,
    uint ModelId,
    Vector3 Position,
    Vector4 Rotation,
    uint VehicleId = 0,
    uint NameId = 0,
    byte PositionUpdateType = 0,
    uint ProfileId = 0,
    uint NpcDefinitionId = 0,
    byte SpawnFlags1 = 0,
    uint ShaderGroupId = 0,
    float RenderDistance = 0f)
{
    /// <summary>Body length when the transient id fits a one-byte client varint.</summary>
    public const int MinimalLength = 200;

    /// <summary>
    /// <c>+0x1b1</c> bit <c>0x10</c> — <b>THE COLLISION SWITCH.</b> docs/85 §2a: inside the
    /// <c>0xd6</c> apply <c>FUN_140c51c90</c>, at <c>0x140c51ddd</c>, the client runs
    /// <c>movzx edx,[rbp+0x1b1] / mov rcx,r14 / shr dl,4 / and dl,1 / call 0x14001cba2</c>, and
    /// <c>0x14001cba2</c> holds <c>e9 49 93 c5 00</c> = <c>jmp 0x140c75ef0</c>.
    /// <c>FUN_140c75ef0(entity, bool)</c> flips <c>entity+0x37e6</c> bit 3, fetches the actor through
    /// entity vtable <c>[0x68]</c> and calls <c>FUN_141fed190(actor, bool)</c>, which registers or
    /// unregisters <c>actor+0x408</c> with the physics manager and flips <c>actor+0x4e1 &amp; 0x20</c>.
    /// That is the client's collision enable.
    /// <para>
    /// <b>The same function the <c>0f 1e Character.SetCollidable</c> packet reaches.</b> The only
    /// thunk to <c>FUN_140c75ef0</c> has four call sites, and one of them is <c>0x140c4f905</c>
    /// inside the <c>0x0f</c> Character sub-dispatcher <c>FUN_140c4d240</c>, feeding it the packet's
    /// own bool from <c>[rbp+0xc68]</c>; <c>registrations-1148.md</c> gives
    /// <c>0x1e = cCharacterPacketIdSetCollidable</c>. So the spawn flag bit and the packet are one
    /// mechanism — see <c>Cranberry.Zone.World.Doors.SetCollidablePacket</c>.
    /// </para>
    /// <para>
    /// <b>The owner proved the identical bit of the identical byte at 1087.</b> His
    /// <c>ZoneNpcs.Collidable = 1 &lt;&lt; 4</c> on <c>flags2</c> (<c>pkt+0x1a9</c> there, this byte
    /// here) is derived from <c>FUN_140515c80 → FUN_140534520(entity, pkt+0x1a9 &gt;&gt; 4 &amp; 1)</c>
    /// and identified through <c>case 0x1d</c> of his Character sub-dispatcher. His doors are solid,
    /// and the click-proven reference server he measured carries <c>flags2 = 16</c> on 47 of 47 doors
    /// while sending no <c>SetCollidable</c> at all. Two builds, two independent derivations, same
    /// byte, same bit, same function (docs/85 §3; D53).
    /// </para>
    /// <para>
    /// <b>Default 0 on purpose.</b> Ground loot must NOT carry it: an item that stops a player is an
    /// obstacle you cannot step over, and the owner's server deliberately withholds the bit from loot
    /// for the same reason. Doors opt in, via <c>ZoneOptions.DoorSpawnFlags1</c>.
    /// </para>
    /// </summary>
    public const byte CollidableFlag = 0x10;

    /// <summary>
    /// <c>+0x1b1</c> bit <c>0x20</c> — the physics-body <b>rebuild</b> gate, and
    /// <b>FALSIFIED as the collision lever</b> (docs/85 §1, §2c; supersedes docs/68 F1).
    /// <para>
    /// Wave 8 shipped this bit believing it was the switch. It reached the client — 7 of 7 door
    /// records in <c>captures/wire-20260830-203615.txt</c> carry <c>(0, 32, 0)</c> — and the owner
    /// walked through the doors anyway. It could never have worked alone: the bit sets
    /// <c>entity+0x37e5 |= 0x40</c>, whose only consumer is <c>FUN_140c5c210 → FUN_141fea440</c>,
    /// which passes <c>body ? body-&gt;+0x168 : 0</c> into <c>FUN_141fed340</c> — and
    /// <c>FUN_141fed340</c> returns on its first line when that geom is null. It can rebuild an
    /// existing body; it cannot create one and cannot enable a disabled one.
    /// </para>
    /// <para>
    /// <b>Kept, not deleted.</b> It is already on the wire, it is harmless, and
    /// <c>CRANBERRY_DOOR_SPAWN_FLAGS1=0x20</c> has to stay reachable as the control that reproduces
    /// wave 8 byte for byte.
    /// </para>
    /// </summary>
    public const byte PhysicsBodyFlag = 0x20;

    /// <summary>
    /// The <c>+0x1b1</c> byte a <b>door</b> ships with: <see cref="CollidableFlag"/>, the switch,
    /// plus <see cref="PhysicsBodyFlag"/>, which is inert but already on the wire — dropping it in
    /// the same change that adds bit 4 would move two things at once. docs/85 §0.4.
    /// </summary>
    public const byte DoorSpawnFlagsDefault = CollidableFlag | PhysicsBodyFlag;   // 0x30

    /// <summary>
    /// The <c>+0x1b1</c> byte a door ships with <b>since 2026-09-03</b>: <see cref="CollidableFlag"/>
    /// and nothing else (docs/114 §5, AUDIT-doors §7).
    /// <para>
    /// The friend's live server writes <c>0x10</c> in that island and Z1's own
    /// <c>ZoneNpcs.Collidable = 1 &lt;&lt; 4</c> is the same bit; adopted under D53. Bit 5 is
    /// <see cref="PhysicsBodyFlag"/>, which docs/85 §2c FALSIFIED as a collision lever — it can
    /// rebuild an existing physics body and cannot create one — so it was never doing anything, and
    /// dropping it is the one byte that moves Cranberry's door record toward his.
    /// <c>CRANBERRY_DOOR_RETAIL_INTERACT=0</c> restores <see cref="DoorSpawnFlagsDefault"/>, and
    /// <c>CRANBERRY_DOOR_SPAWN_FLAGS1=0x30</c> restores it for one run without moving the range.
    /// </para>
    /// </summary>
    public const byte DoorSpawnFlagsRetail = CollidableFlag;                     // 0x10

    /// <summary>
    /// Bytes the body writes <em>after</em> the <c>+0x1b1</c> flag byte: <c>+0x1b2</c> and
    /// <c>+0x124</c> (a byte each), <c>+0x128</c> (u32), <c>+0x130</c> and the target guid (u64
    /// each), <c>+0x180</c>/<c>+0x184</c> (u32), <c>+0x190</c> (u64), then
    /// <c>+0x198</c>/<c>+0x19c</c>/<c>+0x1a0</c>/<c>+0x1a4</c>/<c>+0x188</c> (u32) and
    /// <c>+0x1a8</c> (u64) — 66. Measured back from the end for the same reason
    /// <see cref="AddLightweightDoor.TrailingBytesAfterDoorId"/> is: a field added earlier moves
    /// the offset with it instead of silently misplacing it.
    /// </summary>
    public const int TrailingBytesAfterSpawnFlags1 = 66;

    /// <summary>
    /// Byte offset of the <c>+0x1b1</c> flag byte in this record. docs/68 §1 read <b>133</b> from a
    /// one-byte transient varint; a door's three-byte varint (ids from 1,000,000) puts it at
    /// <b>135</b>, which is the offset that document's hexdump annotates.
    /// </summary>
    public int SpawnFlags1Offset => Length - TrailingBytesAfterSpawnFlags1 - sizeof(byte);

    public int Length => MinimalLength + ClientVarInt.Length(TransientId) - 1;

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte(Opcode);                        // +8
        w.WriteUInt64(Guid);                        // +0x10
        ClientVarInt.Write(w, TransientId);         // +0x18
        w.WriteString(string.Empty);                // +0x20
        w.WriteUInt32(NameId);                      // +0x38 display NAME_ID
        w.WriteByte(0);                             // +0x3c
        w.WriteUInt32(ModelId);                     // +0x40 (model table FUN_14220c720)
        WriteVector4(w, new Vector4(1, 1, 1, 1));   // +0x50 scale
        w.WriteString(string.Empty);                // +0x60
        w.WriteString(string.Empty);                // +0x78
        w.WriteUInt32(0);                           // +0x90
        w.WriteSingle(Position.X);                  // +0x94 (w forced to 1 by the filler)
        w.WriteSingle(Position.Y);
        w.WriteSingle(Position.Z);
        WriteVector4(w, Rotation);                  // +0xa0
        WriteVector4(w, new Vector4(1, 1, 1, 1));   // +0xb0
        w.WriteUInt32(0);                           // +0xc0
        w.WriteUInt32(0);                           // +0xc4
        w.WriteString(string.Empty);                // +0xc8
        w.WriteString(string.Empty);                // +0xe0
        w.WriteString(string.Empty);                // +0xf8
        w.WriteUInt32(VehicleId);                   // +0x110 (> 0 selects the vehicle class)
        w.WriteUInt32(0);                           // +0x114
        w.WriteUInt32(NpcDefinitionId);             // +0x118 npc definition id
        w.WriteByte(PositionUpdateType);            // +0x11c (0 = static)
        w.WriteUInt32(ProfileId);                   // +0x120
        // +0x1b0 / +0x1b1 / +0x1b2 — the record's THREE FLAG BYTES. docs/85 §2 is the map:
        // +0x1b1 & 0x10 is THE COLLISION SWITCH (CollidableFlag → FUN_140c75ef0 → FUN_141fed190);
        // +0x1b1 & 0x20 is only the body-REBUILD gate (PhysicsBodyFlag), falsified as the lever by
        // the owner's 20:36 session; +0x1b1 & 0x08 → entity+0x37e5 |= 0x10, +0x1b1 & 0x80 →
        // entity+0x37e1 |= 0x02, and +0x1b0 & 0x04 → entity+0x37e5 |= 0x01. Cranberry wrote
        // 00 00 00 here on all 823 records of the owner's capture, which is why nothing this server
        // spawned was ever solid.
        w.WriteByte(0);                             // +0x1b0
        w.WriteByte(SpawnFlags1);                   // +0x1b1 (bit 0x10 = collidable; docs/85 §2a)
        w.WriteByte(0);                             // +0x1b2
        w.WriteByte(0);                             // +0x124
        w.WriteUInt32(0);                           // +0x128
        w.WriteUInt64(0);                           // +0x130
        w.WriteUInt64(0);                           // FUN_140a3d220: target object id; null guid → block ends
        w.WriteUInt32(0);                           // +0x180
        w.WriteUInt32(0);                           // +0x184
        w.WriteUInt64(0);                           // +0x190
        w.WriteUInt32(0);                           // +0x198
        w.WriteUInt32(0);                           // +0x19c door id (Doors.txt row; > 0 installs
                                                    //   the door controller). NOT a collision flag
                                                    //   — docs/42 §2b, docs/68 §3 R2, docs/85 §4.
        w.WriteSingle(RenderDistance);              // +0x1a0 -> entity +0xb30 / actor +0x5a8
        w.WriteUInt32(ShaderGroupId);               // +0x1a4 vehicle shader-parameter group (docs/117
                                                    //   §B step 3): 838 tints the OffRoader; 0 (the
                                                    //   default for every other entity) is untouched.
        w.WriteUInt32(0);                           // +0x188
        w.WriteUInt64(0);                           // +0x1a8 (new in 1148)
    }

    private static void WriteVector4(PacketWriter w, Vector4 v)
    {
        w.WriteSingle(v.X);
        w.WriteSingle(v.Y);
        w.WriteSingle(v.Z);
        w.WriteSingle(v.W);
    }
}
