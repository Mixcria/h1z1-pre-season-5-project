using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone.World.Doors;

// The s2c half of the door protocol (docs/42). Two packets, both of which Cranberry already sends
// in other shapes:
//
//   0xd6 AddLightweightNpc      spawns the door. The ONLY thing that differs from a ground-loot
//                               spawn is the u32 at client object +0x19c — wire offset 176 (+ varint
//                               slack) — which carries the Doors.txt row id. When it is > 0 the
//                               apply FUN_140c51c90 calls FUN_140c51c00(entity, doorId), which
//                               stores entity+0x4348 and installs the 0x70-byte door controller at
//                               entity+0xd10. Cranberry's LightweightEntityBody labels that field
//                               "(use collision)" — a 1087-era name that is wrong for 1148.
//
//   0f 0a UpdateCharacterState  swings it. Bit 48 of the state qword is the open/closed bit and is
//                               one of the three bits the applier FUN_140c81fe0 sets IMMEDIATELY
//                               (mask 0x0201010000000000), then, on a *change*, reaches the door
//                               controller through entity+0xd10's vtable[0x28] and calls
//                               FUN_14149dac0, which starts the swing and plays the Doors.txt sound.
//
// Character.RequestToggleDoorState (0f 52) is NOT here on purpose: it is registered in 1148 and the
// client neither reads nor writes it (docs/42 §3a). A server waiting for it waits forever.

/// <summary>
/// How a door's ZONE yaw is packed into <c>AddLightweightNpc</c>'s float4 at client object
/// <c>+0xa0</c>.
/// <para>
/// <b>Wave 3 shipped this as an open sweep; docs/47 §3 closed it from the binary.</b> The field is a
/// <b>quaternion <c>(x, y, z, w)</c></b>, not an Euler triple: the <c>0xd6</c> apply hands
/// <c>record+0xa0</c> to <c>FUN_142335120</c>, which copies it to the actor-spawn parameter object at
/// <c>+0x30</c> and calls <c>FUN_142339800</c>, which hands it to <c>FUN_140c43290</c> — whose first
/// output element is <c>x² − y² − z² + w²</c>, the quaternion-to-matrix <c>m00</c>, built from the
/// ±1 sign vectors at <c>0x1430f12a0/c0/e0</c> and <b>never divided by the squared norm</b>. The
/// matrix goes on to <c>FUN_140adac40</c>, which takes <c>yaw = atan2(row2.x, row2.z)</c>: the world
/// is Y-up and yaw turns about <c>+Y</c>. <b>[P-bin]</b>
/// </para>
/// <para>
/// So <see cref="ZoneEulerYawFirst"/> — wave 3's default — asks for a rotation of
/// <c>2·acos(1/sqrt(yaw²+1))</c> about the world <b>X</b> axis <i>and</i> a scale of
/// <c>yaw² + 1</c>. Over Z2's 4,103 playable door proxies only 636 (15.5 %) have
/// <c>|yaw| &lt; 0.02</c> and survive by accident; <b>3,467 doors are spawned tipped onto their face
/// and up to eleven times too large</b>. Neither Euler packing can ever be right, which is why
/// <see cref="DoorRotationPacking.PackForWire"/> refuses to put one on the wire.
/// </para>
/// <para>
/// What is <i>still</i> open is only the <b>sign</b> of the ZONE yaw relative to the client's
/// <c>atan2(row2.x, row2.z)</c> — a two-value sweep between <see cref="QuaternionYUp"/> and
/// <see cref="QuaternionYUpNegated"/>, which shows as doors hinged on the wrong jamb, never as an
/// error (docs/47 §7 q2).
/// </para>
/// </summary>
public enum DoorRotation
{
    /// <summary>
    /// <c>(yaw, 0, 0, 1)</c> — wave 3's default, and <b>proven impossible</b> by docs/47 §3b. Kept
    /// only because <c>ZoneOptions.DoorRotation</c> still names it and because
    /// <see cref="DoorRotationPacking.Pack"/> documents what it used to write;
    /// <see cref="DoorRotationPacking.PackForWire"/> substitutes <see cref="QuaternionYUp"/> for it.
    /// </summary>
    ZoneEulerYawFirst,

    /// <summary>
    /// <c>(0, yaw, 0, 1)</c> — the other Euler candidate, <b>proven impossible</b> the same way and
    /// substituted the same way.
    /// </summary>
    ZoneEulerYawSecond,

    /// <summary>
    /// <c>(0, sin(yaw/2), 0, cos(yaw/2))</c> — a unit quaternion about world-up. <b>The only shipped
    /// packing the client's decoder can read correctly</b>, up to the sign of the yaw.
    /// </summary>
    QuaternionYUp,

    /// <summary>
    /// <c>(0, −sin(yaw/2), 0, cos(yaw/2))</c> — <see cref="QuaternionYUp"/> with the ZONE yaw
    /// negated. The other half of the sign sweep (docs/47 §7 q2): use it if doors come out upright
    /// but mirrored, hinged on the wrong jamb, or facing into the room.
    /// </summary>
    QuaternionYUpNegated,

    /// <summary>
    /// <c>(0, 0, 0, 1)</c> — the known-safe fallback. Every door faces the same way and no door is
    /// ever tipped on its side; use it if a sweep goes badly and the acceptance check is being run
    /// for the toggle alone.
    /// </summary>
    Identity,
}

/// <summary>Packs a door's yaw for the wire under a chosen <see cref="DoorRotation"/>.</summary>
public static class DoorRotationPacking
{
    /// <summary>
    /// The literal float4 each convention names, including the two docs/47 §3b proves the client
    /// cannot read as a pose. This is the documentation-and-test entry point;
    /// <see cref="PackForWire"/> is what a packet uses.
    /// </summary>
    public static Vector4 Pack(float yaw, DoorRotation convention) => convention switch
    {
        DoorRotation.ZoneEulerYawFirst => new Vector4(yaw, 0f, 0f, 1f),
        DoorRotation.ZoneEulerYawSecond => new Vector4(0f, yaw, 0f, 1f),
        DoorRotation.QuaternionYUp => new Vector4(0f, MathF.Sin(yaw / 2f), 0f, MathF.Cos(yaw / 2f)),
        DoorRotation.QuaternionYUpNegated => new Vector4(0f, MathF.Sin(-yaw / 2f), 0f, MathF.Cos(yaw / 2f)),
        DoorRotation.Identity => new Vector4(0f, 0f, 0f, 1f),
        _ => throw new ArgumentOutOfRangeException(nameof(convention), convention, "Unknown door rotation convention."),
    };

    /// <summary>
    /// True for the two Euler packings docs/47 §3b proves the client cannot read as a pose. They are
    /// not "untested" — they are arithmetically wrong, and a sweep that includes them wastes a
    /// play-test.
    /// </summary>
    public static bool IsProvenImpossible(DoorRotation convention) =>
        convention is DoorRotation.ZoneEulerYawFirst or DoorRotation.ZoneEulerYawSecond;

    /// <summary>
    /// The convention that will actually be written for <paramref name="convention"/>: itself,
    /// unless docs/47 §3b proves it impossible, in which case <see cref="DoorRotation.QuaternionYUp"/>.
    /// <para>
    /// <b>Why substitute rather than throw or obey.</b> <c>ZoneOptions.DoorRotation</c> — a file this
    /// lane does not own — still defaults to <see cref="DoorRotation.ZoneEulerYawFirst"/>, and
    /// obeying it puts 84.5 % of Z2's doors on their face. Throwing would take the door burst, and
    /// with it the landing, down inside a <c>SendTunnel</c> callback. Substituting is the only option
    /// that leaves the match intact and the doors upright; once
    /// <c>ZoneOptions.DoorRotation = DoorRotation.QuaternionYUp</c> lands (docs/47 §I1) this becomes
    /// a no-op. <see cref="DoorRotation.Identity"/> is <i>not</i> substituted: it is a valid unit
    /// quaternion and a deliberate fallback.
    /// </para>
    /// </summary>
    public static DoorRotation ForWire(DoorRotation convention) =>
        IsProvenImpossible(convention) ? DoorRotation.QuaternionYUp : convention;

    /// <summary>
    /// <see cref="Pack"/> through <see cref="ForWire"/> — what every door packet writes. No caller
    /// can put a proven-impossible pose on the wire by choosing the wrong option value.
    /// </summary>
    public static Vector4 PackForWire(float yaw, DoorRotation convention) =>
        Pack(yaw, ForWire(convention));
}

/// <summary>
/// <c>AddLightweightNpc</c> (0xd6) in its <b>door</b> form: the shared lightweight-entity body with
/// the door mesh at <c>+0x40</c>, the proxy's yaw at <c>+0xa0</c> and — the one field that makes it a
/// door rather than a prop — the <c>Doors.txt</c> row id at <c>+0x19c</c> (docs/42 §2).
/// <para>
/// The body itself is <b>not</b> re-transcribed here: it is written by
/// <see cref="LightweightEntityBody"/>, which is already a field-for-field transcription of the
/// client's reader <c>FUN_140a2d040</c>, and this type then patches the one u32. That keeps a single
/// source of truth for the record — a door cannot drift out of step with a loot spawn — and the
/// patch is self-checking: <see cref="WriteTo"/> refuses to write unless the four bytes it is about
/// to overwrite are the zeros the body wrote there.
/// </para>
/// <para>
/// <b>Rules the caller must respect</b> (docs/42 §8):
/// the matching <c>LightweightToFullNpc</c> (0xda) must follow this packet and must keep <b>its</b>
/// <c>+0x198</c> at 0, or the full-npc apply replaces the door controller with a movement rail; and
/// a door must be spawned <b>closed</b>, because the controller reads the state bit at construction
/// (which is always 0 then) and would treat a pre-rotated pose as the open one.
/// </para>
/// </summary>
public sealed record AddLightweightDoor(
    ulong Guid,
    uint TransientId,
    uint ModelId,
    Vector3 Position,
    float Yaw,
    uint DoorTableId,
    DoorRotation Rotation = DoorRotation.QuaternionYUp,
    uint NameId = 0,
    byte SpawnFlags1 = LightweightEntityBody.DoorSpawnFlagsRetail,
    byte PositionUpdateType = DoorCollision.CollidablePositionUpdateType)
{
    public const byte Opcode = ZoneOpcodes.AddLightweightNpc;

    /// <summary>Body length when the transient id fits a one-byte client varint (200).</summary>
    public const int MinimalLength = LightweightEntityBody.MinimalLength;

    /// <summary>
    /// Bytes the body writes <em>after</em> the door id: <c>+0x1a0</c>, <c>+0x1a4</c> and
    /// <c>+0x188</c> (u32 each) then <c>+0x1a8</c> (u64). This is what fixes the door id's offset —
    /// it is measured back from the end of the record, so a body change that adds or removes an
    /// earlier field moves the offset with it instead of silently misplacing the door id.
    /// </summary>
    public const int TrailingBytesAfterDoorId = 20;

    /// <summary>
    /// Wire offset of the door id with a one-byte transient varint: <b>176</b>, which is
    /// <c>200 − 20 − 4</c> and is the number docs/42 §2b derives by walking the reader. Cranberry's
    /// own transient ids (1000+) take a two-byte varint and push it to 177.
    /// </summary>
    public const int MinimalDoorIdOffset =
        MinimalLength - TrailingBytesAfterDoorId - sizeof(uint);

    /// <summary>
    /// The packing these bytes will actually carry — <see cref="Rotation"/> unless docs/47 §3b
    /// proves it impossible, in which case <see cref="DoorRotation.QuaternionYUp"/>. Log this, not
    /// the option, or the log will claim a pose the wire does not carry.
    /// </summary>
    public DoorRotation EffectiveRotation => DoorRotationPacking.ForWire(Rotation);

    /// <summary>The shared <c>FUN_140a2d040</c> body with the door field choices.</summary>
    public LightweightEntityBody Body => new(
        Opcode,
        Guid,
        TransientId,
        ModelId,
        Position,
        DoorRotationPacking.PackForWire(Yaw, Rotation),
        VehicleId: 0,               // +0x110: any non-zero value would select a vehicle actor class
        NameId: NameId,
        // +0x11c. docs/55 §2a / D55.7: FUN_140c51c90 stores this at entity+0x382c and calls
        // FUN_140c872c0 → FUN_141fdc290(actor, positionUpdateType == 0), the ONLY writer of the
        // actor's static bit (actor+0x4e1 & 4) in the whole image — and that bit is the gate on
        // every collision acquire (FUN_141fe6ad0 / FUN_141febab0 / FUN_141fd8920). 0 is therefore
        // the COLLIDABLE value: a non-zero value makes FUN_141fdc290 release collision instead.
        // Do not sweep this looking for door collision; the lever is the model id (DoorCollision).
        PositionUpdateType: PositionUpdateType,
        ProfileId: 0,
        NpcDefinitionId: 0,
        // +0x1b1. docs/85 §2a: bit 0x10 is THE collision switch — FUN_140c51c90 shifts this byte
        // right by 4 and hands bit 0 to FUN_140c75ef0, which is the same function 0f 1e
        // Character.SetCollidable reaches and which drives FUN_141fed190, the actor's collision
        // enable. Wave 8 sent bit 0x20 instead, the owner walked through the doors, and docs/85 §2c
        // shows why that bit could never have worked on its own. The model id (DoorCollision) only
        // chooses which actor definition a body is built FROM.
        SpawnFlags1: SpawnFlags1,
        RenderDistance: 650f);

    public int Length => Body.Length;

    /// <summary>Byte offset of the door id in this record.</summary>
    public int DoorIdOffset => Length - TrailingBytesAfterDoorId - sizeof(uint);

    /// <summary>Byte offset of the <c>+0x1b1</c> collision flag byte in this record (docs/85 §2a).</summary>
    public int SpawnFlags1Offset => Body.SpawnFlags1Offset;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (DoorTableId == 0)
        {
            // The client reads +0x19c <= 0 as "not a door": the entity spawns as inert scenery with
            // no controller and no swing. Refusing here turns a silent nothing-happens into a
            // failure at the call site.
            throw new InvalidOperationException(
                "A door's Doors.txt row id must be > 0; the client reads 0 at +0x19c as 'not a door'.");
        }

        int start = writer.Position;
        Body.WriteTo(writer);
        int at = start + DoorIdOffset;

        uint existing = BinaryPrimitives.ReadUInt32LittleEndian(writer.Written[at..]);
        if (existing != 0)
        {
            throw new InvalidOperationException(
                $"The lightweight body no longer has a zero u32 at offset {DoorIdOffset} (found "
                + $"0x{existing:x8}); LightweightEntityBody's field order has changed and the door id "
                + "would land on the wrong field.");
        }

        writer.PatchUInt32(at, DoorTableId);
    }
}

/// <summary>
/// The 64-bit CharacterState word at client entity <c>+0x18d8</c>, as far as doors are concerned.
/// </summary>
public static class DoorStateBits
{
    /// <summary>
    /// Bit 48 — the door's open/closed bit. Byte 6 of the state qword is tested as
    /// <c>entity+0x18de &amp; 1</c> by the applier <c>FUN_140c81fe0</c>, and a <b>change</b> in it is
    /// what reaches the door controller (docs/42 §5a).
    /// </summary>
    public const ulong Open = 1UL << 48;

    /// <summary>
    /// The three bits <c>FUN_140c81fe0</c> applies immediately rather than through its timed queue
    /// (48, 56, 57). Recorded because <see cref="DoorStateUpdate"/> implicitly clears the other two;
    /// nothing is known to use them on a door entity, and <see cref="DoorStateDelta"/> is the
    /// surgical alternative if that ever stops being true.
    /// </summary>
    public const ulong ImmediateMask = 0x0201_0100_0000_0000UL;

    public static ulong For(bool isOpen) => isOpen ? Open : 0UL;
}

/// <summary>
/// Sends the door's open state. A chosen swing direction uses the 38-byte
/// <see cref="DoorStateDelta"/> with a source-guid marker understood by the launcher helper.
/// Direction zero preserves the legacy 22-byte <c>0f 0a</c> state packet.
/// <para>
/// <b>Naturally idempotent.</b> The applier reaches the door controller only when bit 48 actually
/// changes, so re-sending the same state re-plays neither the sound nor the swing. That is what lets
/// the server answer both packets of one <c>[F]</c> press without a second thought — though the
/// server-side toggle itself must still fire once (see <see cref="MatchDoors"/>).
/// </para>
/// <para>
/// <c>time</c> is irrelevant and defaults to 0: it is stored only on the deferred queue, bit 48 is
/// applied immediately, and the applier rebases it onto the client's own clock whenever the source
/// guid is the invalid sentinel — which this packet always supplies.
/// </para>
/// <para>
/// The unmodified client has no wire angle. The bidirectional helper chooses closed yaw ±π/2
/// from the marker; the native controller retains the pivot, animation, sound and 2 rad/s speed.
/// </para>
/// </summary>
public sealed record DoorStateUpdate(ulong DoorGuid, bool IsOpen, uint Time = 0, int SwingDirection = 0)
{
    public const byte Opcode = ZoneOpcodes.CharacterBase;
    public const byte SubOpcode = 0x0a;
    public const int Length = 22;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (SwingDirection is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(SwingDirection));
        if (SwingDirection != 0)
        {
            new DoorStateDelta(DoorGuid, IsOpen,
                IsOpen ? (SwingDirection < 0 ? DoorSwing.NegativeSource : DoorSwing.PositiveSource) : 0,
                Time).WriteTo(writer);
            return;
        }
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(DoorGuid);
        writer.WriteUInt64(DoorStateBits.For(IsOpen));  // → entity+0x18d8; lost = ~state
        writer.WriteUInt32(Time);
    }
}

/// <summary>
/// s2c <c>Character.UpdateCharacterStateDelta</c> (base 0x0f, u8 sub 0x3f) — 38 bytes:
/// <c>u8 0x0f; u8 0x3f; u64 guid; u64 sourceGuid; u64 gained; u64 lost; u32 time</c>. Same applier
/// as <see cref="DoorStateUpdate"/> (<c>FUN_140c81fe0</c>, docs/21 §2j).
/// <para>
/// Sets or clears only bit 48. Bidirectional opening uses one of <see cref="DoorSwing"/>'s
/// two exact source-guid markers, without allocating any unknown native state bits.
/// </para>
/// <para><c>sourceGuid</c> defaults to the null sentinel (0), which is what makes the applier rebase
/// <c>time</c> onto the client's own clock.</para>
/// </summary>
public sealed record DoorStateDelta(ulong DoorGuid, bool IsOpen, ulong SourceGuid = 0, uint Time = 0)
{
    public const byte Opcode = ZoneOpcodes.CharacterBase;
    public const byte SubOpcode = 0x3f;
    public const int Length = 38;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(DoorGuid);
        writer.WriteUInt64(SourceGuid);
        writer.WriteUInt64(IsOpen ? DoorStateBits.Open : 0UL);   // gained
        writer.WriteUInt64(IsOpen ? 0UL : DoorStateBits.Open);   // lost
        writer.WriteUInt32(Time);
    }
}
