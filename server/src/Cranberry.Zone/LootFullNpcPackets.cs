using System.Buffers.Binary;
using Cranberry.Protocol;

namespace Cranberry.Zone;

// The second half of the ground-loot spawn (docs/19-loot-full-npc-findings.md): the August client
// answers every AddLightweightNpc (0xd6) with Character.FullCharacterDataRequest (0F 45) because the
// 0xd6 apply FUN_140af2900 marks the new entity "full data pending" (entity+0x37ec |= 0x40, line 58
// of that decompile). The answer is LightweightToFullNpc (0xda). Separately, the [F] prompt is bound
// only when Replication.CreateComponent (ea 04) carries at least one rep-data entry — the client
// enrols an object in its interaction manager exclusively inside the per-entry callback
// FUN_14146d0a0 → FUN_14146d230 (docs/19 §4b), so the empty-list form in LootPackets.cs creates an
// inert component.
//
// These types live in their own file because LootPackets.cs, LootWorld.cs and ZoneService.cs are
// owned by another lane this wave; the wiring is written out in docs/24-loot-integration-note.md.
// Everything reused from LootPackets.cs (ClientPackedName, CreateComponent's opcode constants,
// ItemAdd.GenericItemClassTail) is referenced, never duplicated.

/// <summary>
/// c2s <c>Character.FullCharacterDataRequest</c> (base 0x0f, <b>u8</b> sub 0x45; registration id
/// <c>0x45000f00</c> = <c>(sub &lt;&lt; 24) | (base &lt;&lt; 8)</c>,
/// <c>cCharacterPacketFullCharacterDataRequest</c>, registered by <c>FUN_1413c1ba0</c> —
/// <c>out/registrations-1148.md:1146</c>). Layout is the ordinary 0x0f family shape the receive
/// dispatcher <c>FUN_140af9ca0</c> uses in both directions: <c>u8 op; u8 sub; u64 characterGuid</c>.
/// <para>
/// <b>Live-proven, 10 bytes</b> (docs/19 §2): after three <c>0xd6</c> spawns carrying world guids
/// <c>0x2000000000000001/2/3</c> the client sent <c>0F450100000000000020</c>,
/// <c>0F450300000000000020</c> and <c>0F450200000000000020</c>
/// (<c>logs/host-20260829-151220.log</c> 15:12:48.228) — one request per spawned object, ~150 ms
/// after the burst, in arbitrary order, and never retried when unanswered.
/// </para>
/// The guid is the <em>world-object</em> guid the spawn packet put at <c>+0x10</c>, but the
/// <c>0xda</c> reply is matched by <b>transient id</b> (<c>FUN_140b02060</c>), so the server has to
/// map guid → transient through its own <see cref="LootWorld"/> registry.
/// </summary>
public sealed record FullCharacterDataRequest(ulong CharacterGuid)
{
    public const byte Opcode = ZoneOpcodes.CharacterBase;
    public const byte SubOpcode = 0x45;

    /// <summary>Exact wire length: <c>u8; u8; u64</c> (docs/19 §2, live).</summary>
    public const int Length = 10;

    public static FullCharacterDataRequest Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        byte opcode = reader.ReadByte();
        byte sub = reader.ReadByte();
        if (opcode != Opcode || sub != SubOpcode)
        {
            throw new PacketFormatException(
                $"Expected Character.FullCharacterDataRequest 0f 45, got {opcode:x2} {sub:x2}.");
        }

        var request = new FullCharacterDataRequest(reader.ReadUInt64());
        if (!reader.AtEnd)
        {
            throw new PacketFormatException(
                $"Character.FullCharacterDataRequest has {reader.Remaining} trailing byte(s).");
        }

        return request;
    }
}

/// <summary>
/// <c>LightweightToFullNpc</c> (0xda; router <c>FUN_140af3950</c> case 0xda → parser
/// <c>FUN_140a2ded0</c>, apply <c>FUN_140b02060</c>). This is the record the client asks for with
/// <see cref="FullCharacterDataRequest"/>; without it a ground item stays half-created forever
/// (docs/19 §0, §1).
/// <para>
/// <b>Why the bytes are trustworthy without new guessing.</b> The vehicle form <c>0xdb</c> is
/// <c>FUN_140a2f2f0</c>, whose first statement is <c>thunk_FUN_140a2ded0()</c>: <c>0xdb</c> is this
/// record plus a 62-byte vehicle tail, exactly as <c>0xd7</c> is <c>0xd6</c> plus the
/// <c>FUN_140a2dd10</c> tail. So this writer is the live-proven 271-byte
/// <see cref="LightweightToFullVehicle"/> with the opcode changed and the tail removed:
/// <c>271 − 62 = 209</c>, and the vehicle record's guid offset 90 lands exactly on the
/// <c>+0x120</c> u64 below (docs/19 §1 cross-check). <c>LootFullNpcPacketTests</c> asserts that
/// identity byte for byte.
/// </para>
/// <para>
/// <b>Apply rules the server must respect</b> (<c>FUN_140b02060</c>, docs/19 §1a): the record is
/// matched to the world object by <c>transientId (+0x10)</c>, <em>not</em> by guid; the object must
/// still carry the pending bit, so <c>0xda</c> is accepted only after its own <c>0xd6</c> and only
/// once (the apply clears <c>+0x37ec &amp; 0x40</c>, and a second <c>0xda</c> returns 0 harmlessly);
/// and the dispatcher applies the record only when it is consumed exactly
/// (<c>(int)end - (int)cursor &lt; 1</c>, <c>FUN_140af3950</c> line 2110), so trailing bytes are
/// fatal. Every field <c>FUN_140b80960</c> actually consumes is zero-safe for an inert prop.
/// </para>
/// Minimal length: <b>208 bytes + the transient varint</b> — 209 with a one-byte varint, 210 for the
/// transient ids <see cref="LootWorld"/> issues (1000+, a two-byte varint).
/// </summary>
public sealed record LightweightToFullNpc(uint TransientId, ulong ObjectGuid)
{
    public const byte Opcode = ZoneOpcodes.LightweightToFullNpc;

    /// <summary>Length with a one-byte transient varint (docs/19 §1: 208 + varint).</summary>
    public const int MinimalLength = 209;

    /// <summary>
    /// Byte offset of the <c>+0x120</c> object guid for a one-byte transient varint — the same
    /// offset <see cref="LightweightToFullVehicle.VehicleGuidOffset"/> carries, which is the
    /// arithmetic cross-check of docs/19 §1. Wider varints push it out by
    /// <c>varintLength − 1</c>; use <see cref="GuidOffset"/>.
    /// </summary>
    public const int MinimalGuidOffset = 90;

    public int Length => MinimalLength + ClientVarInt.Length(TransientId) - 1;

    /// <summary>Byte offset of the object guid in this record.</summary>
    public int GuidOffset => MinimalGuidOffset + ClientVarInt.Length(TransientId) - 1;

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);

        // --- FUN_140a2ded0, the whole record (docs/19 §1 table). Offsets in the comments are the
        // client object offsets the parser stores to; the parser re-reads the opcode itself because
        // the dispatcher hands it the unadvanced buffer.
        w.WriteByte(Opcode);                        // +0x08 (record +0x09 = 1 is a constant, not on the wire)
        ClientVarInt.Write(w, TransientId);         // +0x10 varint FUN_140a190f0 — ** the match key **
        WriteZeroUInt32s(w, 3);                     // +0x14, +0x18, +0x1c
        w.WriteInt32(0);                            // attachment count (each element FUN_140a2ed70)
        w.WriteString(string.Empty);                // +0x40 str (FUN_140b78f60)
        w.WriteString(string.Empty);                // +0x58 str
        WriteZeroUInt32s(w, 5);                     // +0x70, +0x74, +0x78, +0x7c, +0x80
        w.WriteInt32(0);                            // effect-tag count (each element FUN_140a383c0)
        w.WriteUInt32(0);                           // +0xc8  \ inline FUN_140a377d0 block
        w.WriteString(string.Empty);                // +0xd0   |
        w.WriteString(string.Empty);                // +0xe8   |
        w.WriteUInt32(0);                           // +0x100  |
        w.WriteString(string.Empty);                // +0x108 /
        WriteZeroUInt32s(w, 4);                     // +0xb0 f32×4
        w.WriteUInt32(0);                           // +0xc0
        w.WriteUInt64(ObjectGuid);                  // +0x120 ** the 0xd6's guid; offset 89 + varint **
        w.WriteByte(0);                             // +0x128 virtual vt+8 = FUN_140a14ca0: u8 type, 0 = end
        w.WriteInt32(0);                            // +0x138 list count (FUN_140a4e980)
        w.WriteInt32(0);                            // +0x168 list count (FUN_140a4e6c0)
        w.WriteUInt32(0);                           // +0x198
        w.WriteUInt32(0);                           // +0x19c
        WriteZeroUInt32s(w, 4);                     // +0x1a0 f32×4
        WriteZeroUInt32s(w, 7);                     // +0x1b0 .. +0x1c8
        w.WriteByte(0);                             // +0x1cc
        w.WriteByte(0);                             // +0x1d0
        WriteZeroUInt32s(w, 3);                     // +0x1d4, +0x1d8, +0x1dc
        w.WriteUInt64(0);                           // +0x1e0
        for (int index = 0; index < 6; index++)
        {
            w.WriteInt32(0);                        // counted bytes ×6 (FUN_1400eda8b), +0x1f0/+0x1f8 … +0x240/+0x248
        }

        w.WriteUInt32(0);                           // +0x1e8 — read last, stored before the six blocks
    }

    private static void WriteZeroUInt32s(PacketWriter w, int count)
    {
        for (int index = 0; index < count; index++)
        {
            w.WriteUInt32(0);
        }
    }
}

/// <summary>
/// One entry of the <c>Replication.CreateComponent</c> rep-data list, read by
/// <c>FUN_1413e0380</c>: <c>u32 repId (entry+0x00); u32 repDataClassHash (entry+0x04);
/// u8 sequence (entry+0x14); i32 payloadLength + payload bytes (entry+0x08 ptr / +0x10 len)</c>.
/// <para>
/// The field order is fixed by the dispatcher's own log string — "Failed to create RepData(%08x)
/// id(%u) for guid(%lld)'s %s during component creation" — whose <c>%08x</c> argument is
/// <c>entry+0x04</c> and whose <c>%u</c> is <c>entry+0x00</c> (docs/19 §3). <c>repId</c> must be
/// unique per rep-data instance for the session: the client keys its own cache by it and logs
/// "type already exists" on a collision (docs/19 §7 rule 3).
/// </para>
/// </summary>
public sealed record ReplicationDataEntry(uint RepId, uint ClassHash, byte Sequence, byte[] Payload)
{
    /// <summary><c>u32 + u32 + u8 + i32</c> before the payload.</summary>
    public const int HeaderLength = 13;

    public int Length => HeaderLength + Payload.Length;

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(Payload);
        w.WriteUInt32(RepId);                       // entry +0x00
        w.WriteUInt32(ClassHash);                   // entry +0x04
        w.WriteByte(Sequence);                      // entry +0x14
        w.WriteInt32(Payload.Length);               // entry +0x10
        w.WriteRaw(Payload);                        // entry +0x08
    }
}

/// <summary>
/// <c>InteractReplicationData</c> — the 11-byte payload that actually binds the <c>[F]</c> prompt
/// (docs/19 §4a). Class hash <b><c>0x50d51c9d</c></b>, one of exactly three classes the rep-data
/// factory <c>FUN_1413e3820</c> knows; the hash↔name pairing is proven by the class vtable at
/// <c>0x143236d88</c> (slot <c>+0x10</c> returns the hash, slot <c>+0x18</c> the name string at
/// <c>0x143236cc8</c>). Deserializer <c>FUN_1413e6890</c> → <b><c>FUN_1413dfe80</c></b>, which reads
/// from object <c>+0x20</c>:
/// <code>
/// f32 interactionRange   → +0x20
/// u32                    → +0x24     UNVERIFIED (docs/19 §8 item 3)
/// u8  bool               → +0x28     UNVERIFIED
/// u8  bool               → +0x29     UNVERIFIED
/// u8  bool               → +0x2a     UNVERIFIED
/// </code>
/// <para>
/// <c>+0x20</c> is a float, proven by the component callback <c>FUN_14146d0a0</c>, which assigns
/// <c>*(float *)(repData + 4) = (float)*(int *)(DAT_143f696a0 + 0x326b8)</c> at that offset whenever
/// the component's own range getter returns ≤ 0 — i.e. <b>a range of 0 is safe</b>, the client
/// substitutes its global default. Writing a positive range is the belt-and-braces choice; the
/// constructor zeroes every field, so an all-zero payload is also valid.
/// </para>
/// </summary>
public sealed record InteractReplicationData(
    float InteractionRange = 3f,        // = DefaultRange; a record parameter default cannot name it
    uint Unknown24 = 0,
    bool Flag28 = false,
    bool Flag29 = false,
    bool Flag2A = false)
{
    /// <summary>Registered class hash of <c>InteractReplicationData</c> (<c>FUN_1413e3820</c>).</summary>
    public const uint ClassHash = 0x50d51c9d;

    /// <summary>Payload length read by <c>FUN_1413dfe80</c>: <c>f32; u32; u8; u8; u8</c>.</summary>
    public const int PayloadLength = 11;

    /// <summary>
    /// This project's own choice of interaction radius, in the client's range units. Any value ≤ 0
    /// makes the client fall back to its global default at <c>DAT_143f696a0 + 0x326b8</c>.
    /// </summary>
    public const float DefaultRange = 3f;

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteSingle(InteractionRange);            // +0x20 f32 interaction range
        w.WriteUInt32(Unknown24);                   // +0x24 UNVERIFIED — purpose unnamed, 0 is the ctor default
        w.WriteBool(Flag28);                        // +0x28 UNVERIFIED
        w.WriteBool(Flag29);                        // +0x29 UNVERIFIED
        w.WriteBool(Flag2A);                        // +0x2a UNVERIFIED
    }

    public byte[] ToPayload()
    {
        using var writer = new PacketWriter(PayloadLength);
        WriteTo(writer);
        return writer.Written.ToArray();
    }

    /// <summary>The rep-data list entry that carries this payload under its own class hash.</summary>
    public ReplicationDataEntry ToEntry(uint repId, byte sequence = 0) =>
        new(repId, ClassHash, sequence, ToPayload());
}

/// <summary>
/// <c>NpcReplicationData</c> — the second rep-data class of <c>FUN_1413e3820</c>, hash
/// <c>0xf68bb709</c> (name at <c>0x143236ce8</c>, vtable <c>0x143236dd0</c>), deserializer
/// <c>FUN_1413e68e0</c> → <c>FUN_1413dffa0</c>: from object <c>+0x20</c>, 15 × u32, one u64,
/// two u32, one u8 expanded to a native u32, one u32 and one u8 = <b>82 bytes</b>.
/// The older 85-byte reading confused the expanded native field with its one-byte wire value.
/// The final bool is <c>IsWorldItem</c> at native <c>+0x78</c>: <c>FUN_142296c90</c> reads it
/// through <c>ClientNpcComponent</c>, and <c>FUN_14150cef0</c> publishes it to the proximity UI.
/// A false value disables both clicking and dragging that row. See docs/loot-proximity-20260906.md.
/// </summary>
public static class NpcReplicationData
{
    public const uint ClassHash = 0xf68bb709;

    /// <summary><c>15 × u32 + u64 + 2 × u32 + u8 + u32 + u8</c> (<c>FUN_1413dffa0</c>).</summary>
    public const int PayloadLength = 82;

    public const int IsWorldItemOffset = 81;

    /// <summary>
    /// Fourth u32: owner's ZoneReplication.NpcComponentPayload nameId field, with the August
    /// position verified by FUN_1413dffa0 and the native +0x2c getter FUN_142296aa0.
    /// </summary>
    public const int NameIdOffset = 12;

    /// <summary>An all-zero payload of the derived length.</summary>
    public static byte[] ZeroPayload() => new byte[PayloadLength];

    public static ReplicationDataEntry ToEntry(
        uint repId, byte sequence = 0, bool isWorldItem = false, uint nameId = 0)
    {
        byte[] payload = ZeroPayload();
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(NameIdOffset), nameId);
        payload[IsWorldItemOffset] = isWorldItem ? (byte)1 : (byte)0;
        return new(repId, ClassHash, sequence, payload);
    }
}

/// <summary>
/// <c>Replication.CreateComponent</c> (base 0xea, <b>u8</b> sub 0x04) <em>with</em> a rep-data list
/// — the form that binds <c>[F]</c>. Envelope reader <c>FUN_1413e1980</c> (docs/19 §3):
/// <code>
/// u8  0xea                                     → +0x08
/// u8  0x04                                     → +0x10
/// varint ownerTransientId   FUN_140a190f0      → +0x48   (must name an existing object)
/// packedName componentClass FUN_140a12af0      → +0x4c
/// i32 nameTableCount        FUN_1413e1610      → +0x58   each { packedName; str }
/// i32 repDataCount          FUN_1413e0380      → +0x68/+0x70
/// </code>
/// <para>
/// This is deliberately a separate type from <see cref="CreateComponent"/> in
/// <c>LootPackets.cs</c>, which writes the same envelope with a hard-coded <em>empty</em> rep-data
/// list. That empty list is exactly the defect docs/19 §4b identifies: the client enrols an object
/// in its interaction manager (<c>DAT_143f696a0 + 0x322b8</c>) only inside
/// <c>ClientInteractComponent</c>'s per-entry rep-data callback <c>FUN_14146d0a0</c> →
/// <c>FUN_14146d230</c>, which the dispatcher <c>FUN_1413e3f40</c> case 4 invokes once per rep-data
/// entry. Zero entries → the callback never runs → the component exists but is inert, which is what
/// the live <c>09 08 00 InteractCancel</c> bursts (three bytes, no target) proved. The opcode
/// constants and the class names are reused from <see cref="CreateComponent"/>.
/// </para>
/// </summary>
/// <summary>
/// <b>THE ORDER OF A GROUND-LOOT SPAWN BURST IS LOAD-BEARING. Do not reorder it.</b>
/// Cranberry sends <c>d6 AddLightweightNpc</c> → <c>da LightweightToFullNpc</c> →
/// <c>ea 04</c> component(s), and has the promotion first by accident of derivation rather than by
/// a written rule. The owner's server settled the same order the hard way over three rounds, and
/// this is that ruling recorded so nobody re-derives it (docs/78 §4.1):
/// <list type="number">
/// <item><b>The promotion is not optional.</b> A ground item spawned as a bare lightweight lives its
/// whole life on the client's ~100 ms entity-update throttle — his r39 decompiled the gate
/// (<c>FUN_140502d20</c> via vtable <c>+0x6d0</c>: while <c>entity+0x8d4 &amp; 0x40</c> is set the
/// entity is updated once per <c>maxIntervalMs</c>, and only the full-data handlers clear it) and
/// filmed the symptom, a glow that lands immediately and a pickup 300-800 ms later. Removing the
/// <c>da</c> restores that throttle.</item>
/// <item><b>The promotion must come BEFORE the components.</b> His r40 found that a promotion sent
/// <i>after</i> <c>ClientNpcComponent</c> overwrites the state the native proximity validation
/// reads; r42 settled on promotion, then npc, then interact, so the loot-specific state is the final
/// write.</item>
/// <item>The NPC component carries <c>IsWorldItem = true</c>, which the August proximity UI
/// requires for its row to be usable. It always precedes the interact component.</item>
/// </list>
/// </summary>
public sealed record CreateComponentWithRepData(
    uint OwnerTransientId,
    IReadOnlyList<ReplicationDataEntry> RepData,
    string ComponentClass = CreateComponent.InteractComponentClass)
{
    public const byte Opcode = CreateComponent.Opcode;
    public const byte SubOpcode = CreateComponent.SubOpcode;

    /// <summary>
    /// The ground-item form: one <c>ClientInteractComponent</c> carrying a single
    /// <c>InteractReplicationData</c> entry. The owner transient id must be the one the object's
    /// <see cref="AddLightweightItem"/> carried — the dispatcher refuses a component whose owner is
    /// unknown ("Client told to create Component class (%s) but owner object (transientId=%u)
    /// doesn't exist!").
    /// </summary>
    public static CreateComponentWithRepData ForGroundItem(
        uint ownerTransientId,
        uint repId,
        float interactionRange = InteractReplicationData.DefaultRange) =>
        new(ownerTransientId, [new InteractReplicationData(interactionRange).ToEntry(repId)]);

    /// <summary>
    /// Marks an existing ground entity as a world item before its proximity row is published.
    /// Loot transient ids are below 1,000,000; the high bit gives this second component a rep-data
    /// id distinct from every existing loot/door interaction entry in the session's shared cache.
    /// </summary>
    public static CreateComponentWithRepData ForWorldItemNpc(uint ownerTransientId, uint nameId = 0) =>
        new(ownerTransientId,
            [NpcReplicationData.ToEntry(ownerTransientId | 0x80000000u, isWorldItem: true, nameId: nameId)],
            CreateComponent.NpcComponentClass);

    public int Length
    {
        get
        {
            int length = 2 + ClientVarInt.Length(OwnerTransientId)
                + ClientPackedName.Length(ComponentClass) + 4 + 4;
            foreach (ReplicationDataEntry entry in RepData)
            {
                length += entry.Length;
            }

            return length;
        }
    }

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(RepData);
        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        ClientVarInt.Write(w, OwnerTransientId);            // FUN_140a190f0 → +0x48
        ClientPackedName.Write(w, ComponentClass);          // FUN_140a12af0 → +0x4c
        w.WriteInt32(0);                                    // FUN_1413e1610 name table: none
        w.WriteInt32(RepData.Count);                        // FUN_1413e0380 rep-data list
        foreach (ReplicationDataEntry entry in RepData)
        {
            entry.WriteTo(w);
        }
    }
}
