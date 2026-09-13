using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone.World;

/// <summary>
/// The packets that make a <b>second player</b> appear, move and disappear in the August client.
/// Derived in docs/100 from the client's own readers; nothing here is ported from another server.
///
/// <para>
/// The family is four packets and one request:
/// <list type="bullet">
/// <item><c>d5 AddLightweightPc</c> — the spawn. Fully derived, <see cref="AddLightweightPc"/>.</item>
/// <item><c>94 01 SetCharacterEquipment</c> for the peer's guid — the dress. Already written by
/// <c>Cranberry.Zone.SetCharacterEquipment</c>; this lane adds nothing to it.</item>
/// <item><c>0x78 PlayerUpdatePosition</c> — the pose relay, in either of the two proven framings
/// (<see cref="RelayBody"/> / <see cref="PlayerUpdatePosition"/>).</item>
/// <item><c>0f 01 RemovePlayer</c> — the despawn, <see cref="Despawn"/>.</item>
/// <item><c>0f 45 FullCharacterDataRequest</c> — what the client asks when it meets a character it
/// does not know (<see cref="TryParseFullCharacterDataRequest"/>); the answer is
/// <c>d9 LightweightToFullPc</c>, which initializes the remote weapon manager.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Pure by design.</b> Nothing in this file touches a session, a sink or the world; it turns a
/// record into bytes. Wiring it into the tick is lane 3C.
/// </para>
/// </summary>
public static class PeerSpawnWriter
{
    /// <summary>
    /// Gateway channel the client streams its own movement on, and the channel a relayed pose goes
    /// back out on when it is sent in the opcode-free form. <c>FUN_140dd1400</c> L23-35 handles
    /// <c>channel == 2</c> by <em>synthesising</em> the opcode <c>0x78</c> from the channel number
    /// (<c>local_1b0 = 0x78</c>) and parsing the body with no opcode byte at all.
    /// </summary>
    public const byte MovementChannel = 2;

    // =======================================================================================
    // d5 AddLightweightPc
    // =======================================================================================

    /// <summary>
    /// <c>d5 AddLightweightPc</c> — reader <c>FUN_140a2d880</c>, handler <c>FUN_140af2ba0</c>,
    /// dispatched from <c>FUN_140af3950</c> case <c>0xd5</c> (L2015-2030). docs/100 §2 is the field
    /// table.
    ///
    /// <para>
    /// <b>Exact-length gated.</b> The dispatcher applies the record only when the reader set no error
    /// byte <em>and</em> <c>end - cursor &lt; 1</c> — one trailing byte and the spawn is dropped in
    /// silence, exactly like <c>0f 48</c> (docs/02, 2026-09-02). <see cref="AddLightweightPcLength"/>
    /// is therefore a contract, not a convenience.
    /// </para>
    ///
    /// <para>
    /// <b>This is the packet the whole peer wire hangs off.</b> Its handler sets
    /// <c>entity+0x37ec |= 0x40</c>, and both <c>d9 LightweightToFullPc</c>
    /// (<c>FUN_140b02170</c>: the entity is refused unless that bit is set) and every
    /// <c>82 15</c> require the entity this packet creates. The remote weapon router additionally
    /// requires d9 to clear that bit and create the weapon manager before any arsenal or action.
    /// </para>
    /// </summary>
    public static byte[] AddLightweightPc(PeerCharacterRecord peer)
    {
        ArgumentNullException.ThrowIfNull(peer);

        using var w = new PacketWriter(256);
        WriteAddLightweightPc(w, peer);
        return w.Written.ToArray();
    }

    /// <summary>
    /// <see cref="AddLightweightPc"/> into a caller's writer — the form a sink's
    /// <c>Begin</c>/<c>End</c> pair wants.
    /// </summary>
    public static void WriteAddLightweightPc(PacketWriter w, PeerCharacterRecord peer)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(peer);

        w.WriteByte(ZoneOpcodes.AddLightweightPc);          // rec+0x08 (the reader stores the opcode)
        w.WriteUInt64(peer.Guid);                           // rec+0x10  character guid
        ClientVarInt.Write(w, peer.TransientId);            // rec+0x18  the id this viewer knows it by

        // FUN_140a40000 -> rec+0x20 .. rec+0x137. Byte for byte the SelfRecord's own identity
        // sub-record (SelfRecord.cs:334-341 writes the same eight fields through the same reader),
        // which is what lets a peer be dressed from the character data the server already has.
        w.WriteUInt32(peer.Identity.Value0);                // rec+0x128
        w.WriteUInt32(peer.Identity.Value1);                // rec+0x12c
        w.WriteUInt32(peer.Identity.Value2);                // rec+0x130
        w.WriteString(peer.Identity.Name);                  // rec+0x20
        w.WriteString(peer.Identity.Text1);                 // rec+0x50
        w.WriteString(peer.Identity.Text2);                 // rec+0xc0
        w.WriteString(peer.Identity.Text3);                 // rec+0xf0
        w.WriteUInt64(peer.Identity.Value3);                // rec+0x120

        w.WriteByte(unchecked((byte)peer.Field138));        // rec+0x138  i8, sign-extended [U]
        w.WriteUInt32(peer.ModelId);                        // rec+0x13c  model table FUN_14220c720
        w.WriteUInt32(peer.Field140);                       // rec+0x140  -> entity vtable[0x110] [U]

        w.WriteSingle(peer.Position.X);                     // rec+0x144  position; the handler
        w.WriteSingle(peer.Position.Y);                     //            supplies w = DAT_1430ef088
        w.WriteSingle(peer.Position.Z);                     // rec+0x14c; no W float on the wire

        w.WriteSingle(peer.Rotation.X);                     // rec+0x150  rotation, a real vec4
        w.WriteSingle(peer.Rotation.Y);
        w.WriteSingle(peer.Rotation.Z);
        w.WriteSingle(peer.Rotation.W);

        w.WriteUInt32(peer.Field160);                       // rec+0x160  -> entity+0xb48 [U]
        w.WriteUInt64(peer.MountGuid);                      // rec+0x168  SetPendingMountInfo "mount"
        w.WriteUInt32(peer.MountField170);                  // rec+0x170  -> entity+0x3748 [I seat]
        w.WriteUInt32(peer.MountField174);                  // rec+0x174  -> entity+0x374c [U]
        w.WriteByte(peer.Field178);                         // rec+0x178  -> entity+0x5a0 and the
                                                            //            initial motion baseline version
        w.WriteUInt32(peer.Field17C);                       // rec+0x17c  -> entity+0x9e8 [U]
        w.WriteUInt32(peer.Field180);                       // rec+0x180  -> entity+0x9ec [U]
        w.WriteUInt64(peer.Field188);                       // rec+0x188 [U]
        w.WriteUInt32(peer.Field190);                       // rec+0x190 [U]
        w.WriteByte(peer.SpawnFlags);                       // rec+0x194  see PeerSpawnFlags
    }

    /// <summary>
    /// Exact byte length of <see cref="AddLightweightPc"/>. The record is exact-length gated, so this
    /// has to agree with the writer for the packet to be applied at all.
    /// </summary>
    public static int AddLightweightPcLength(PeerCharacterRecord peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        // MinimalLength already counts the four u32 string-length prefixes, so only the text bytes
        // themselves are added here.
        return PeerCharacterRecord.MinimalLength
            + (ClientVarInt.Length(peer.TransientId) - 1)
            + TextBytes(peer.Identity.Name)
            + TextBytes(peer.Identity.Text1)
            + TextBytes(peer.Identity.Text2)
            + TextBytes(peer.Identity.Text3);
    }

    // =======================================================================================
    // d9 LightweightToFullPc
    // =======================================================================================

    /// <summary>Compatibility marker: the August promotion is now derived and implemented.</summary>
    public const bool FullPcNotYetDerived = false;

    public static byte[] LightweightToFullPcPrefix(int blobLength, bool compressed = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blobLength);
        using var w = new PacketWriter(8);
        w.WriteByte(ZoneOpcodes.LightweightToFullPc);
        w.WriteBool(compressed);
        w.WriteInt32(blobLength);
        return w.Written.ToArray();
    }

    /// <summary>Creates the weapon manager and enables remote events after AddLightweightPc.</summary>
    public static byte[] LightweightToFullPc(PeerCharacterRecord peer) => FullCharacterPackets.Promote(peer);

    // =======================================================================================
    // 0x78 PlayerUpdatePosition — the pose relay
    // =======================================================================================

    /// <summary>
    /// The relay body in its <b>opcode-free</b> form: <c>varint transientId | movement record</c>,
    /// to be sent on <see cref="MovementChannel"/>.
    ///
    /// <para>
    /// <b>[P], and it is the shape this server already emits.</b> <c>FUN_140dd1400</c> L23-35 is the
    /// client's inbound channel router: for <c>channel == 2</c> it writes <c>0x78</c> into the packet
    /// object's opcode slot from the channel number alone (<c>local_1b0 = 0x78</c>), starts its
    /// cursor at the first byte of the body with no opcode to skip
    /// (<c>local_1e8 = param_4; local_1d8 = param_4;</c>), reads the transient id with the varint
    /// reader <c>FUN_140a190f0</c> and the pose with <c>FUN_140a3ca40</c>, then dispatches through
    /// the same vtable slot <c>+0x250</c> that the opcode-framed path uses. So the byte-for-byte
    /// record the client sends on channel 2 is re-framed for another viewer by changing one field —
    /// the transient id in front of it — and nothing else.
    /// </para>
    ///
    /// <para>
    /// <b>The record is never re-encoded.</b> <c>ClientMovementUpdate</c> keeps the client's exact
    /// bytes for this reason: the pose is quantised at two decimal places, so a decode/re-encode
    /// round trip would move a peer by up to a centimetre per relay for no reason, and a field this
    /// server does not yet understand would be lost outright.
    /// </para>
    /// </summary>
    public static byte[] RelayBody(uint transientId, ReadOnlySpan<byte> movementRecord)
    {
        GuardRelayTransient(transientId);

        using var w = new PacketWriter(movementRecord.Length + 8);
        ClientVarInt.Write(w, transientId);
        w.WriteRaw(movementRecord);
        return w.Written.ToArray();
    }

    /// <summary>Byte length of <see cref="RelayBody"/>.</summary>
    public static int RelayBodyLength(uint transientId, int movementRecordLength) =>
        ClientVarInt.Length(transientId) + movementRecordLength;

    /// <summary>
    /// The relay body in its <b>opcode-framed</b> form: <c>78 | varint transientId | movement
    /// record</c> — the form that survives arriving on any channel, because the opcode is in the
    /// bytes rather than implied by the channel.
    ///
    /// <para>
    /// <b>[P]</b> from <c>FUN_140af3950</c> case <c>0x78</c> (L1422-1430) → reader
    /// <c>FUN_140a600b0</c>: L18 <c>u8</c> opcode → <c>+8</c>, L28
    /// <c>FUN_140a190f0(&amp;cursor, rec+0x1a8)</c> the transient id, L29
    /// <c>FUN_140a3ca40(rec+0x1a0, &amp;cursor)</c> the pose. Its constructor <c>FUN_140a76c20</c>
    /// pre-seeds <c>0x78</c> into the same slot. The identical layout
    /// <c>{opcode +8, record +0x1a0, transientId +0x1a8}</c> is what <c>0x90
    /// PlayerUpdateManagedPosition</c> uses on channel 3, through the shared serialiser
    /// <c>FUN_140a241d0</c> — the same three-field shape <c>ManagedMovementPackets.cs</c> already
    /// proved live with the parachute.
    /// </para>
    /// </summary>
    public static byte[] PlayerUpdatePosition(uint transientId, ReadOnlySpan<byte> movementRecord)
    {
        GuardRelayTransient(transientId);

        using var w = new PacketWriter(movementRecord.Length + 8);
        w.WriteByte(ZoneOpcodes.PlayerUpdatePosition);
        ClientVarInt.Write(w, transientId);
        w.WriteRaw(movementRecord);
        return w.Written.ToArray();
    }

    /// <summary>The same opcode-framed pose written into caller-owned storage.</summary>
    public static int WritePlayerUpdatePosition(Span<byte> destination, uint transientId, ReadOnlySpan<byte> movementRecord)
    {
        GuardRelayTransient(transientId);
        if (transientId >= (1u << 30)) throw new ArgumentOutOfRangeException(nameof(transientId));
        int idLength = ClientVarInt.Length(transientId);
        int length = 1 + idLength + movementRecord.Length;
        if (destination.Length < length) throw new ArgumentException("Pose destination is too short.", nameof(destination));
        destination[0] = 0x78;
        uint packed = (transientId << 2) | (uint)(idLength - 1);
        for (int i = 0; i < idLength; i++) destination[1 + i] = (byte)(packed >> (8 * i));
        movementRecord.CopyTo(destination[(1 + idLength)..]);
        return length;
    }

    /// <summary>Byte length of <see cref="PlayerUpdatePosition"/>.</summary>
    public static int PlayerUpdatePositionLength(uint transientId, int movementRecordLength) =>
        sizeof(byte) + ClientVarInt.Length(transientId) + movementRecordLength;

    // =======================================================================================
    // 0f 45 FullCharacterDataRequest (client -> server)
    // =======================================================================================

    /// <summary><c>cCharacterPacketFullCharacterDataRequest</c> (registrations-1148.md:1146).</summary>
    public const byte FullCharacterDataRequestSub = 0x45;

    /// <summary>
    /// The <c>0x0f</c> family's fixed head: <c>u8 0x0f; u8 sub; u64 characterGuid</c> — ten bytes
    /// before any sub-specific body.
    /// </summary>
    public const int CharacterFamilyHeadLength = 10;

    /// <summary>
    /// Parses <c>0f 45 &lt;u64 characterGuid&gt;</c>, the request the client sends for a character it
    /// has been told about but has no full record for.
    ///
    /// <para>
    /// <b>The guid, not the transient id.</b> The <c>0x0f</c> dispatcher <c>FUN_140af9ca0</c>
    /// L355-388 reads <c>u8 family</c>, <b>one</b> <c>u8 sub</c> (L369 — the sub is one byte at 1148,
    /// not two) and then requires eight more bytes for a <c>u64</c> (L375-378) which it resolves
    /// through <c>FUN_140ebd250</c>, the <em>guid</em>-keyed entity map. The mirror reader
    /// <c>FUN_140c268f0</c> L16-53 confirms the same three fields before any body.
    /// </para>
    ///
    /// <para>
    /// <b>Whether sub <c>0x45</c> appends anything after the guid is [U].</b> The client has no
    /// inbound handler for it — <c>FUN_140c4d240</c> switches on <c>sub - 1</c> (L535) and has no
    /// <c>case 0x44</c> — which is consistent with a request it only ever sends, and no serialiser
    /// for it appears in any dump. So this parser accepts a body of exactly ten bytes and reports
    /// anything longer rather than assuming the tail is empty.
    /// </para>
    /// </summary>
    /// <param name="body">The zone packet body, starting at the <c>0x0f</c> opcode byte.</param>
    /// <param name="characterGuid">The character whose full record the client wants.</param>
    /// <param name="trailingBytes">
    /// Bytes after the guid. Always 0 in the shape derived above; a non-zero value is the signal
    /// that the <c>0x45</c> class carries a body this project has not yet seen, and should be logged
    /// rather than ignored.
    /// </param>
    public static bool TryParseFullCharacterDataRequest(
        ReadOnlySpan<byte> body,
        out ulong characterGuid,
        out int trailingBytes)
    {
        characterGuid = 0;
        trailingBytes = 0;

        if (body.Length < CharacterFamilyHeadLength
            || body[0] != ZoneOpcodes.CharacterBase
            || body[1] != FullCharacterDataRequestSub)
        {
            return false;
        }

        characterGuid = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(body[2..]);
        trailingBytes = body.Length - CharacterFamilyHeadLength;
        return true;
    }

    // =======================================================================================
    // 0f 01 RemovePlayer — the despawn
    // =======================================================================================

    /// <summary>
    /// The despawn: <c>0f 01 RemovePlayer</c> with <c>effectFlag = 0</c>, which removes the entity
    /// without the death/ragdoll presentation. Twelve bytes, and already this project's own
    /// <c>Cranberry.Zone.RemovePlayer</c> — leaving interest is exactly the same operation as
    /// evicting a loot pile or a car, so it deliberately reuses the same record rather than adding
    /// a second writer for one opcode.
    ///
    /// <para>
    /// <b>Order matters.</b> The viewer's transient id for the character must not be released until
    /// after this is queued, or a later spawn can reuse an id the client still has bound —
    /// which is why <see cref="TransientIdTable.Release"/> is documented as a post-despawn call and
    /// <see cref="InterestSystem"/> calls it in that order.
    /// </para>
    /// </summary>
    public static byte[] Despawn(ulong characterGuid)
    {
        using var w = new PacketWriter(RemovePlayer.Length);
        new RemovePlayer(characterGuid).WriteTo(w);
        return w.Written.ToArray();
    }

    private static void GuardRelayTransient(uint transientId)
    {
        if (transientId is 0 or TransientIdTable.LocalPlayer)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transientId),
                transientId,
                "A relayed pose must carry the viewer's transient id for the OTHER player. "
                + $"{TransientIdTable.LocalPlayer} is the viewer's own actor and 0 is the "
                + "'no network id' sentinel; either one relays a peer's movement onto the viewer.");
        }
    }

    private static int TextBytes(string value) => System.Text.Encoding.UTF8.GetByteCount(value);
}

/// <summary>
/// One peer as <c>d5 AddLightweightPc</c> carries it. Field names are this project's own; a field
/// whose meaning is not established keeps its record offset in the name rather than inventing one
/// (the same rule <c>MovementFieldMask</c> follows).
/// </summary>
public sealed record PeerCharacterRecord
{
    /// <summary>
    /// Length with a one-byte transient varint and four empty identity strings, summing
    /// <c>FUN_140a2d880</c>'s twenty-one reads:
    /// <c>1 + 8 + 1 + 12 + 16 + 8 + 1 + 4 + 4 + 12 + 16 + 4 + 8 + 4 + 4 + 1 + 4 + 4 + 8 + 4 + 1</c>.
    /// </summary>
    public const int MinimalLength = 125;

    /// <summary>u64 → rec+0x10. The character's world guid — the id <c>0f 45</c>, <c>0f 01</c> and
    /// <c>94 01</c> all name it by.</summary>
    public required ulong Guid { get; init; }

    /// <summary>
    /// Client varint → rec+0x18. <b>Per viewer</b>: the id <em>this</em> client will use for this
    /// character in every later <c>0x78</c> pose and every <c>82 15</c>, minted by
    /// <see cref="TransientIdTable"/>. Never <see cref="TransientIdTable.LocalPlayer"/>.
    /// </summary>
    public required uint TransientId { get; init; }

    /// <summary>
    /// The <c>FUN_140a40000</c> sub-record at rec+0x20 — <b>byte for byte the same eight fields the
    /// self record carries</b> (<c>SelfRecord.cs:334-341</c>), which is the single largest thing
    /// <c>d5</c> shares with <c>SendSelfToClient</c>.
    /// </summary>
    public SelfIdentity Identity { get; init; } = new();

    /// <summary>i8 → rec+0x138, sign-extended into an int. [U] — no consumer found in the handler.</summary>
    public sbyte Field138 { get; init; }

    /// <summary>
    /// u32 → rec+0x13c. The <b>model id</b>: <c>FUN_140af2ba0</c> looks it up with
    /// <c>FUN_14220c720(world+0x32200, value)</c>, the same model table
    /// <c>LightweightEntityBody.ModelId</c> uses, and takes the actor's scale (<c>+0x80</c>) and two
    /// ids (<c>+0x44</c>, <c>+0x48</c>) from the row. An unknown id falls back to defaults rather
    /// than failing.
    /// </summary>
    public uint ModelId { get; init; }

    /// <summary>u32 → rec+0x140, handed straight to the entity's <c>vtable[0x110]</c>. [U].</summary>
    public uint Field140 { get; init; }

    /// <summary>
    /// f32 x3 → rec+0x144. The spawn position. The handler builds its vec4 with
    /// <c>w = DAT_1430ef088</c> of its own. The wire carries only X, Y and Z, followed immediately
    /// by the four rotation floats.
    /// </summary>
    public Vector3 Position { get; init; }

    /// <summary>f32 x4 → rec+0x150. The orientation, used as read.</summary>
    public Vector4 Rotation { get; init; } = new(0f, 0f, 0f, 1f);

    /// <summary>u32 → rec+0x160, copied to <c>entity+0xb48</c>. [U].</summary>
    public uint Field160 { get; init; }

    /// <summary>
    /// u64 → rec+0x168. <b>The mount.</b> The handler passes it to <c>FUN_140c77410</c>, whose own
    /// log line is <c>"SetPendingMountInfo, rider %s (%llu) mount %llu"</c> — so a peer who is in a
    /// car when you first see him is spawned already in it, rather than beside it. 0 = on foot.
    /// </summary>
    public ulong MountGuid { get; init; }

    /// <summary>u32 → rec+0x170, stored at <c>entity+0x3748</c> by <c>FUN_140c77410</c>. [I] the seat.</summary>
    public uint MountField170 { get; init; }

    /// <summary>u32 → rec+0x174, stored at <c>entity+0x374c</c>. [U].</summary>
    public uint MountField174 { get; init; }

    /// <summary>
    /// u8 → rec+0x178, the movement version. <c>FUN_140af2ba0</c> copies it to
    /// <c>entity+0x5a0</c> and seeds a full 0x1fff motion baseline through
    /// <c>FUN_142335120</c>. <c>FUN_140b14d30</c> checks partial movement's State byte against
    /// that version; a zero baseline rejects the August client's version-5 pregame stream.
    /// </summary>
    public byte Field178 { get; init; }

    /// <summary>u32 → rec+0x17c, copied to <c>entity+0x9e8</c>. [U].</summary>
    public uint Field17C { get; init; }

    /// <summary>u32 → rec+0x180, copied to <c>entity+0x9ec</c>. [U].</summary>
    public uint Field180 { get; init; }

    /// <summary>u64 → rec+0x188. [U].</summary>
    public ulong Field188 { get; init; }

    /// <summary>u32 → rec+0x190. [U].</summary>
    public uint Field190 { get; init; }

    /// <summary>u8 → rec+0x194. See <see cref="PeerSpawnFlags"/>; every bit's effect is proven.</summary>
    public byte SpawnFlags { get; init; }
}

/// <summary>
/// The <c>d5</c> record's last byte, rec+0x194. Unlike the rest of the tail, every one of these bits
/// has a proven effect in <c>FUN_140af2ba0</c> — the handler tests them one after another. Bits
/// <c>0x01</c> and <c>0x10</c> are not read.
/// </summary>
[Flags]
public enum PeerSpawnFlags : byte
{
    None = 0,

    /// <summary>
    /// <c>0x02</c> — sets <c>entity+0x37ed |= 4</c>. [U] effect beyond the bit.
    /// </summary>
    Bit02 = 0x02,

    /// <summary>
    /// <c>0x04</c> — sets or clears <c>entity+0x37ec &amp; 0x10</c>. The handler writes it in both
    /// directions, so it is a genuine on/off and not a latch. [U] effect.
    /// </summary>
    Bit04 = 0x04,

    /// <summary>
    /// <c>0x08</c> — calls <c>FUN_140c64320(entity)</c>, but only when the entity's own
    /// <c>vtable[100][0xa8]</c> and <c>vtable[4][0xe8]</c> both answer false. A conditional
    /// activation. [U] effect.
    /// </summary>
    Bit08 = 0x08,

    /// <summary>
    /// <c>0x20</c> — sets or clears <c>entity+0x37e0 &amp; 0x20</c>. [U] effect.
    /// </summary>
    Bit20 = 0x20,

    /// <summary>
    /// <c>0x40</c> — passed as the bool to <c>FUN_140c76080(entity, bit, 0)</c>. The self record
    /// reaches the same function with its own <c>Field5</c> (<c>SelfRecord.cs</c> "u32 at
    /// player+0x240, non-zero passed to <c>FUN_140c76080</c>"), so this is the peer form of a switch
    /// the self record already has. [U] effect.
    /// </summary>
    Bit40 = 0x40,

    /// <summary>
    /// <c>0x80</c> — sets or clears <c>entity+0x37e0 &amp; 0x02</c>. Tested as
    /// <c>flags &lt; 0x80</c>, i.e. the sign bit. [U] effect.
    /// </summary>
    Bit80 = 0x80,
}
