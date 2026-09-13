using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone;

// Vehicle / mount packets of the parachute drop (docs/12-parachute-drop.md). Every layout is the
// August client's parser read sequence (tools/selfschema byte maps under
// out/ghidra-aug/parachute); the numeric contents are this project's own choices unless a client
// datasheet or handler fixes them.

/// <summary>
/// The client's packed signed integer (<c>FUN_140a18f40</c>): the first byte carries the sign in
/// bit 0 and the count of extra little-endian bytes (0-3) in bits 1-2; the magnitude is the whole
/// little-endian quantity shifted right by three. The 1087 capture bytes
/// <c>0c3327 3364 ac0112 ea04 10</c> decode to 321121, −3206, 147509, 157, 2 under this reader
/// (docs/02, position-update block packing).
/// </summary>
public static class ClientPackedInt
{
    public static void Write(PacketWriter w, int value)
    {
        uint sign = value < 0 ? 1u : 0u;
        ulong magnitude = (ulong)Math.Abs((long)value);
        if (magnitude >= (1u << 29))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Client packed integers carry at most 29 magnitude bits.");
        }

        int extra = Length(value) - 1;
        uint packed = ((uint)magnitude << 3) | ((uint)extra << 1) | sign;
        for (int i = 0; i <= extra; i++)
        {
            w.WriteByte((byte)(packed >> (8 * i)));
        }
    }

    public static int Length(int value)
    {
        ulong magnitude = (ulong)Math.Abs((long)value);
        return magnitude < (1u << 5) ? 1 : magnitude < (1u << 13) ? 2 : magnitude < (1u << 21) ? 3 : 4;
    }
}

/// <summary>
/// The position-update block read by <c>FUN_140a3ca40</c> and applied by <c>FUN_140c355c0</c>:
/// <c>u16 flags; u32 sequence/time; u8;</c> then one field per set flag bit in the reader's fixed
/// order — 0x01 varint, 0x02 position ×3, 0x20 u32, 0x40, 0x80, 0x04, 0x08 vertical speed,
/// 0x10 horizontal speed, 0x100 ×3, 0x200 ×4, 0x400, 0x800, 0x1000 ×7 — all packed integers
/// except 0x01 and 0x20; ÷100 scaled except 0x10/0x400/0x800 (×0.1).
/// </summary>
public sealed record PositionUpdateBlock(ushort Flags, IReadOnlyList<int> PackedValues, uint SequenceTime = 0, byte Byte6 = 0,
    float? Orientation = null)
{
    public const ushort RetailFlags = 0x00da;
    public const float RetailVerticalSpeed = 1.57f;
    public const float RetailHorizontalSpeed = 0.2f;

    /// <summary>Flags 0: the 7-byte block with no fields (docs/12 minimal vector).</summary>
    public static readonly PositionUpdateBlock Empty = new(0, Array.Empty<int>());

    /// <summary>
    /// The shape of the 1087 capture (docs/12 §1): flags 0x00da = position (0x02), two zero
    /// fields (0x40, 0x80), vertical speed (0x08, ÷100) and horizontal speed (0x10, ×0.1).
    /// </summary>
    public static PositionUpdateBlock Retail(
        Vector3 position,
        float verticalSpeed = RetailVerticalSpeed,
        float horizontalSpeed = RetailHorizontalSpeed) =>
        new(RetailFlags,
        [
            Scaled(position.X, 100), Scaled(position.Y, 100), Scaled(position.Z, 100),
            0, 0,
            Scaled(verticalSpeed, 100), Scaled(horizontalSpeed, 10),
        ]);

    /// <summary>
    /// A parked entity at rest: the same <c>0x00da</c> shape as <see cref="Retail"/> but with both
    /// speed fields zeroed (docs/117 §B step 1). A real server states a parked car's position twice —
    /// once in the lightweight body and again, packed, in the <c>0xd7</c> tail's position block — and
    /// this is that second statement. The friend's server and the owner's Z1 both do it for every
    /// parked car; without it the client builds the actor but never draws it.
    /// </summary>
    public static PositionUpdateBlock AtRest(Vector3 position) =>
        Retail(position, verticalSpeed: 0f, horizontalSpeed: 0f);

    /// <summary>
    /// A parked car's complete pose. August's reader rebuilds rotation whenever either tilt
    /// bit is present (140a3ca40 -> 142339720), so omitting orientation silently replaces the
    /// lightweight body's heading with zero. Keep the authored yaw in that second transform.
    /// </summary>
    public static PositionUpdateBlock AtRest(Vector3 position, float yaw) =>
        AtRest(position) with { Flags = RetailFlags | 0x20, Orientation = yaw };

    /// <summary>
    /// Preserve slope in the client's second transform, using the same native Euler fields
    /// as VehiclePoseRelay.Parked. The body quaternion alone is overwritten by this tail.
    /// </summary>
    public static PositionUpdateBlock AtRest(Vector3 position, float yaw, Quaternion? rotation)
    {
        if (rotation is not { } q) return AtRest(position, yaw);
        float pitch = MathF.Asin(Math.Clamp(2f * (q.W * q.X - q.Y * q.Z), -1f, 1f));
        yaw = MathF.Atan2(2f * (q.W * q.Y + q.X * q.Z), 1f - 2f * (q.X * q.X + q.Y * q.Y));
        float roll = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.X * q.X + q.Z * q.Z));
        return new(RetailFlags | 0x20,
            [Scaled(position.X, 100), Scaled(position.Y, 100), Scaled(position.Z, 100),
             Scaled(pitch, 100), Scaled(roll, 100), 0, 0], Orientation: yaw);
    }

    public int Length => 7 + PackedValues.Sum(ClientPackedInt.Length) + (Orientation.HasValue ? 4 : 0);

    public void WriteTo(PacketWriter w)
    {
        if ((Flags & 0x01) != 0)
        {
            throw new NotSupportedException("Flag bit 0x01 is a varint, not a packed integer.");
        }
        if (((Flags & 0x20) != 0) != Orientation.HasValue
            || (Orientation.HasValue && !float.IsFinite(Orientation.Value)))
            throw new InvalidOperationException("Position orientation must be finite and present exactly when flag 0x20 is set.");

        w.WriteUInt16(Flags);
        w.WriteUInt32(SequenceTime);
        w.WriteByte(Byte6);
        int beforeOrientation = (Flags & 0x02) != 0 ? 3 : 0;
        if (beforeOrientation > PackedValues.Count)
            throw new InvalidOperationException("Position flag 0x02 requires three packed coordinates.");
        for (int i = 0; i < beforeOrientation; i++)
            ClientPackedInt.Write(w, PackedValues[i]);
        if (Orientation is { } yaw)
            w.WriteSingle(yaw);
        for (int i = beforeOrientation; i < PackedValues.Count; i++)
        {
            ClientPackedInt.Write(w, PackedValues[i]);
        }
    }

    private static int Scaled(float value, int scale) => (int)MathF.Round(value * scale);
}

/// <summary>
/// <c>AddLightweightVehicle</c> (0xd7; handler <c>FUN_140af33a0</c>, parser <c>FUN_140a2dd10</c> =
/// lightweight body <c>FUN_140a2d040</c> + tail). The body's vehicle id (&gt; 0) selects the vehicle
/// actor class, the model id names the mesh; the tail's owner guid, when it is the local player's,
/// registers the vehicle as a client-managed object (<c>FUN_140ab20c0</c>) so the client simulates
/// the chute itself. The target-object block (<c>FUN_140a3d220</c>) ends after its u64 when that
/// guid is the null guid (<c>DAT_143f67f78</c> = 0). Leftover bytes or a stream error reject the
/// packet. Minimal (empty position block) = 227 bytes (docs/12 §1).
/// <para>
/// <b><c>ShaderParameterGroupId</c> is an experiment, defaulting to 0 and therefore to the bytes
/// this record has always written</b> (D241, docs/115 §6). The tail's <c>+0x1c8</c> and
/// <c>+0x1cc</c> dwords are unassigned in every note this project has — docs/12 §6 lists them as
/// open — and the vehicle-skin carrier the client actually registers,
/// <c>ZoneOpcodes.VehicleSkinBase</c> 0xf2, has no derived sub-opcodes. So the parachute-skin lane
/// puts the client's own shader group (484 / 492 / 491) into <c>+0x1c8</c> behind
/// <c>CRANBERRY_PARACHUTE_SKIN</c> and leaves the proven drop path untouched otherwise.
/// </para>
/// </summary>
public sealed record AddLightweightVehicle(
    ulong Guid,
    uint TransientId,
    uint ModelId,
    Vector3 Position,
    Vector4 Rotation,
    uint VehicleId,
    ulong OwnerGuid,
    PositionUpdateBlock? PositionUpdate = null,
    uint NameId = 0,
    byte PositionUpdateType = 1,
    uint ProfileId = 0,
    uint ShaderParameterGroupId = 0,
    byte SpawnFlags1 = 0,
    uint BodyShaderGroupId = 0,
    float RenderDistance = 0f)
{
    public const byte Opcode = ZoneOpcodes.AddLightweightVehicle;
    public const int MinimalLength = 227;

    /// <summary>The shared <c>FUN_140a2d040</c> section of this record. <see cref="SpawnFlags1"/>
    /// (the <c>+0x1b1</c> collidable bit, docs/117 §B step 2) and <see cref="BodyShaderGroupId"/>
    /// (the <c>+0x1a4</c> shader group, step 3) both replace bytes the body already writes as zero,
    /// so the record length is unchanged and the parachute — which sets neither — is byte-identical.</summary>
    public LightweightEntityBody Body => new(
        Opcode,
        Guid,
        TransientId,
        ModelId,
        Position,
        Rotation,
        VehicleId,
        NameId,
        PositionUpdateType,
        ProfileId,
        SpawnFlags1: SpawnFlags1,
        ShaderGroupId: BodyShaderGroupId,
        RenderDistance: RenderDistance);

    /// <summary>Wire length of this record.</summary>
    public int Length => MinimalLength - PositionUpdateBlock.Empty.Length + (PositionUpdate ?? PositionUpdateBlock.Empty).Length
        + ClientVarInt.Length(TransientId) - 1;

    public void WriteTo(PacketWriter w)
    {
        // --- FUN_140a2d040 body (shared with AddLightweightNpc 0xd6, docs/13 §3a) --------------
        Body.WriteTo(w);

        // --- FUN_140a2dd10 tail (consumed by vehicle vtable+0x280 = FUN_140e6e8b0) -------------
        w.WriteUInt64(OwnerGuid);                   // +0x1c0 → vehicle+0x45b0
        // +0x1c8 is one of the two dwords docs/12 §6 has listed as OPEN since wave 1; the
        // self-schema (out/ghidra-aug/parachute/selfschema-FUN_140a2dd10.md:106) gives its offset
        // and width and nothing else, and FUN_140e6e8b0 is not in the dump. D241 makes it the
        // standing experiment for the parachute skins the client ships (VehicleSkinMods rows
        // 11/12/13 → shader groups 484/492/491): with ShaderParameterGroupId at its default 0 this
        // writes the same zero it always has, so the shipped drop is byte-identical.
        w.WriteUInt32(ShaderParameterGroupId);      // +0x1c8 — [U] meaning; 0 = untouched
        w.WriteUInt32(0);                           // +0x1cc
        (PositionUpdate ?? PositionUpdateBlock.Empty).WriteTo(w);   // FUN_140a3ca40
        w.WriteString(string.Empty);                // +0x360 (FUN_140c75aa0)
    }
}

/// <summary>
/// <c>LightweightToFullVehicle</c> (0xdb; parser <c>FUN_140a2f2f0</c> = base <c>FUN_140a2ded0</c> +
/// tail, apply <c>FUN_140b02e00</c>). Applies to the actor created by the lightweight record
/// (<c>+0x37ec &amp; 0x40</c>, cleared afterwards); leftover bytes reject. The virtual read at
/// +0x128 is <c>FUN_140a14ca0</c> (one u8 type, 0 = nothing more). Minimal = 271 bytes, all zero
/// except the opcode, the transient id and the vehicle guid at offset 90 (docs/12 §2).
/// </summary>
public sealed record LightweightToFullVehicle(uint TransientId, ulong VehicleGuid,
    IReadOnlyList<CharacterResource>? Resources = null,
    IReadOnlyList<Cranberry.Zone.Vehicles.VehicleOccupantSlot>? Occupants = null,
    bool EngineOn = false)
{
    public const byte Opcode = ZoneOpcodes.LightweightToFullVehicle;
    public const int MinimalLength = 271;
    public const int VehicleGuidOffset = 90;

    public void WriteTo(PacketWriter w)
    {
        // --- FUN_140a2ded0 base -----------------------------------------------------------------
        w.WriteByte(Opcode);                        // +8
        ClientVarInt.Write(w, TransientId);         // +0x10
        WriteZeroUInt32s(w, 3);                     // +0x14, +0x18, +0x1c
        w.WriteInt32(0);                            // attachments (FUN_140a2ed70 elements)
        w.WriteString(string.Empty);                // +0x40
        w.WriteString(string.Empty);                // +0x58
        WriteZeroUInt32s(w, 5);                     // +0x70 .. +0x80
        w.WriteInt32(0);                            // effect tags (FUN_140a383c0 elements)
        w.WriteUInt32(0);                           // FUN_140a377d0: u32
        w.WriteString(string.Empty);                //   str
        w.WriteString(string.Empty);                //   str
        w.WriteUInt32(0);                           //   u32
        w.WriteString(string.Empty);                //   str
        WriteZeroUInt32s(w, 4);                     // f32×4
        w.WriteUInt32(0);                           // +0xc0
        w.WriteUInt64(VehicleGuid);                 // +0x120 (offset 90)
        w.WriteByte(0);                             // virtual +0x128 vt+8 = FUN_140a14ca0: u8 type 0
        w.WriteInt32(0);                            // FUN_140a4e980 list
        w.WriteInt32(0);                            // FUN_140a4e6c0 list
        w.WriteUInt32(0);                           // +0x198
        w.WriteUInt32(0);                           // +0x19c
        WriteZeroUInt32s(w, 4);                     // f32×4
        WriteZeroUInt32s(w, 7);                     // +0x1b0 .. +0x1c8
        w.WriteByte(0);                             // +0x1cc
        w.WriteByte(0);                             // +0x1d0
        WriteZeroUInt32s(w, 3);                     // +0x1d4, +0x1d8, +0x1dc
        w.WriteUInt64(0);                           // +0x1e0
        for (int index = 0; index < 6; index++)
        {
            if (index == 1 && Resources is { Count: > 0 })
            {
                w.WriteInt32(4 + Resources.Count * CharacterResource.WireLength);
                SelfRecordCodec.WriteResources(w, Resources);
            }
            else w.WriteInt32(0);                  // FUN_1400eda8b counted bytes ×6
        }

        w.WriteUInt32(0);                           // +0x1e8

        // --- FUN_140a2f2f0 tail -----------------------------------------------------------------
        w.WriteBool(EngineOn);                      // +0x250 engine state
        w.WriteUInt32(0);                           // +0x258
        w.WriteInt32(0);                            // FUN_140a511c0 list
        w.WriteInt32(0);                            // FUN_140a4cd00 list
        WriteZeroUInt32s(w, 4);                     // f32×4
        WriteZeroUInt32s(w, 4);                     // f32×4
        w.WriteByte(0);                             // +0x3d0
        w.WriteInt32(Occupants?.Count ?? 0);         // FUN_140a54670 passengers
        foreach (var occupant in Occupants ?? [])
            VehiclePassengerCodec.Write(w, occupant.CharacterGuid, checked((byte)occupant.SeatIndex));
        w.WriteUInt32(0);                           // FUN_140a55200
        w.WriteInt32(0);                            // FUN_140a4e850 list
        w.WriteInt32(0);                            // FUN_140a4dfb0 list
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
/// <c>Vehicle.Owner</c> (base 0x88, u8 sub 1; parser <c>FUN_140c8c4b0</c>: <c>u64 vehicle; u64
/// owner; u32; u32 vehicleId; i32 passengers {u64; identity; str; u8 seat}</c>. The handler acts
/// only when the owner is the local player. A live owner packet includes that player in the
/// passenger array; an empty array gives the client simulation ownership but no driver state.
/// </summary>
public sealed record VehicleOwner(ulong VehicleGuid, ulong OwnerGuid, uint VehicleId)
{
    public const byte Opcode = ZoneOpcodes.VehicleBase;
    public const byte SubOpcode = 0x01;
    public const int OccupiedLength = 79;
    public const int ClearedLength = 30;

    public void WriteTo(PacketWriter w)
    {
        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        w.WriteUInt64(VehicleGuid);
        w.WriteUInt64(OwnerGuid);
        w.WriteUInt32(0);
        w.WriteUInt32(VehicleId);
        if (OwnerGuid == 0)
        {
            w.WriteInt32(0);
        }
        else
        {
            w.WriteInt32(1);
            VehiclePassengerCodec.Write(w, OwnerGuid, seatByte: 1);
        }
    }
}

/// <summary>
/// <c>Vehicle.Occupy</c> (base 0x88, u8 sub 2; parser <c>FUN_140c8c1f0</c>: <c>u64 vehicle; u64
/// character; u32 vehicleId; u32; i32 seats {u32 seat; u8 occupied}; i32 passengers; i32 guids;
/// bytes; bytes</c>. The occupied form is 100 bytes: seat 0 is marked occupied and the rider is
/// present in the passenger list. Sending both arrays empty explicitly clears the client's
/// occupant state and leaves a mounted-looking actor without driving control.
/// </summary>
public sealed record VehicleOccupy(ulong VehicleGuid, ulong CharacterGuid, uint VehicleId, uint Seat = 0)
{
    public const byte Opcode = ZoneOpcodes.VehicleBase;
    public const byte SubOpcode = 0x02;
    public const int OccupiedLength = 100;

    public void WriteTo(PacketWriter w)
    {
        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        w.WriteUInt64(VehicleGuid);
        w.WriteUInt64(CharacterGuid);
        w.WriteUInt32(VehicleId);
        w.WriteUInt32(0);
        w.WriteInt32(1);    // seats (FUN_140c8d210)
        w.WriteUInt32(Seat);
        w.WriteBool(true);
        w.WriteInt32(1);    // passengers (FUN_140a54670)
        VehiclePassengerCodec.Write(w, CharacterGuid, checked((byte)Seat));
        w.WriteInt32(0);    // guids (FUN_140c8dac0)
        w.WriteInt32(0);    // counted bytes
        w.WriteInt32(0);    // counted bytes
    }
}

/// <summary>The inline passenger schema shared by Vehicle.Owner and Vehicle.Occupy.</summary>
internal static class VehiclePassengerCodec
{
    public static void Write(PacketWriter w, ulong characterGuid, byte seatByte)
    {
        w.WriteUInt64(characterGuid);
        w.WriteUInt32(0);             // identity dword 1
        w.WriteUInt32(0);             // identity dword 2
        w.WriteUInt32(0);             // identity dword 3
        w.WriteString(string.Empty);  // first name
        w.WriteString(string.Empty);  // last name
        w.WriteString(string.Empty);  // platform id
        w.WriteString(string.Empty);  // character name (optional for possession)
        w.WriteUInt64(0);             // identity qword
        w.WriteString(string.Empty);  // passenger string
        w.WriteByte(seatByte);
    }
}

/// <summary>
/// The dismount form of <c>Vehicle.Occupy</c>. The null vehicle guid makes
/// <c>FUN_140c99f20</c> take its clear path; value <c>1</c> changes the possession state and the
/// single unoccupied seat mirrors the shape consumed by <c>FUN_140c8d210</c>. The remaining
/// collections are empty. Total length: 51 bytes.
/// </summary>
public sealed record VehicleOccupyCleared(ulong CharacterGuid)
{
    public const byte Opcode = ZoneOpcodes.VehicleBase;
    public const byte SubOpcode = VehicleOccupy.SubOpcode;
    public const int Length = 51;

    public void WriteTo(PacketWriter w)
    {
        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        w.WriteUInt64(0);          // vehicle guid: null selects the clear path
        w.WriteUInt64(CharacterGuid);
        w.WriteUInt32(0);          // vehicle id
        w.WriteUInt32(1);          // clear possession/loadout state
        w.WriteInt32(1);           // seats
        w.WriteUInt32(0);          // seat 0
        w.WriteBool(false);        // unoccupied
        w.WriteInt32(0);           // passengers
        w.WriteInt32(0);           // guids
        w.WriteInt32(0);           // counted bytes
        w.WriteInt32(0);           // counted bytes
    }
}

/// <summary>
/// <c>Mount.DismountResponse</c> (base 0x70, u8 sub 4; parser <c>FUN_140c9de60</c>: <c>u64 rider;
/// u64 mount; u32; u8 flag; i8 seat; u8</c>, 25 bytes). The handler needs no pending request; for
/// the local rider it runs <c>FUN_140c3fb60</c> (the chute-collapse state). Values other than the
/// guids are the 2016 server's (lead).
/// </summary>
public sealed record DismountResponse(ulong Rider, ulong Mount, uint Value = 0, bool Flag = false, byte Seat = 0xff, byte Trailer = 0)
{
    public const byte Opcode = ZoneOpcodes.MountBase;
    public const byte SubOpcode = 0x04;

    public void WriteTo(PacketWriter w)
    {
        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        w.WriteUInt64(Rider);
        w.WriteUInt64(Mount);
        w.WriteUInt32(Value);
        w.WriteBool(Flag);
        w.WriteByte(Seat);
        w.WriteByte(Trailer);
    }
}

/// <summary>
/// c2s <c>Mount.MountRequest</c> (base 0x70, u8 sub 1; sender <c>FUN_140b782e0</c>
/// "BaseClient::SendMountRequst", writer <c>FUN_140a2a120</c>: <c>u64 vehicleGuid; u32 seat; u8;
/// u8 flag</c>, 16 bytes) — sent when a mountable actor with a pending auto-mount is ready
/// ("Mountable npc ready, automount %d, guid %I64u" in ClientMountLog.txt).
/// </summary>
public sealed record MountRequest(ulong VehicleGuid, uint Seat, byte Byte14, byte Flag)
{
    public const byte Opcode = ZoneOpcodes.MountBase;
    public const byte SubOpcode = 0x01;
    public const int Length = 16;

    public static MountRequest Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        byte opcode = reader.ReadByte();
        byte sub = reader.ReadByte();
        if (opcode != Opcode || sub != SubOpcode)
        {
            throw new PacketFormatException($"Expected Mount.MountRequest 70 01, got {opcode:x2} {sub:x2}.");
        }

        var request = new MountRequest(
            VehicleGuid: reader.ReadUInt64(),
            Seat: reader.ReadUInt32(),
            Byte14: reader.ReadByte(),
            Flag: reader.ReadByte());
        if (!reader.AtEnd)
        {
            throw new PacketFormatException($"Mount.MountRequest has {reader.Remaining} trailing byte(s).");
        }

        return request;
    }
}

/// <summary>c2s <c>Mount.DismountRequest</c> (base 0x70, u8 sub 3; <c>u8</c>, 3 bytes).</summary>
public sealed record DismountRequest(byte Flag)
{
    public const byte Opcode = ZoneOpcodes.MountBase;
    public const byte SubOpcode = 0x03;
    public const int Length = 3;

    public static DismountRequest Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        byte opcode = reader.ReadByte();
        byte subOpcode = reader.ReadByte();
        if (opcode != Opcode || subOpcode != SubOpcode)
        {
            throw new PacketFormatException(
                $"Expected Mount.DismountRequest 70 03, got {opcode:x2} {subOpcode:x2}.");
        }

        var request = new DismountRequest(reader.ReadByte());
        if (!reader.AtEnd)
        {
            throw new PacketFormatException(
                $"Mount.DismountRequest has {reader.Remaining} trailing byte(s).");
        }

        return request;
    }
}

/// <summary>
/// c2s <c>Vehicle.Dismiss</c> (base 0x88, u8 sub 0x18; <c>u64 vehicleGuid</c>, 10 bytes).
/// The August client emitted the null-guid form at the end of the live parachute descent on
/// 2026-08-29. While the session is still mounted it is an exit signal; after the server has
/// already cleared the mount, the same null-guid packet is only an acknowledgement.
/// </summary>
public sealed record VehicleDismiss(ulong VehicleGuid)
{
    public const byte Opcode = ZoneOpcodes.VehicleBase;
    public const byte SubOpcode = 0x18;
    public const int Length = 10;

    public static VehicleDismiss Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        byte opcode = reader.ReadByte();
        byte subOpcode = reader.ReadByte();
        if (opcode != Opcode || subOpcode != SubOpcode)
        {
            throw new PacketFormatException(
                $"Expected Vehicle.Dismiss 88 18, got {opcode:x2} {subOpcode:x2}.");
        }

        var request = new VehicleDismiss(reader.ReadUInt64());
        if (!reader.AtEnd)
        {
            throw new PacketFormatException(
                $"Vehicle.Dismiss has {reader.Remaining} trailing byte(s).");
        }

        return request;
    }
}
