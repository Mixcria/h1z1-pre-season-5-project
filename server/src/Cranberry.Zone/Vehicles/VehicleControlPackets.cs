using Cranberry.Protocol;

namespace Cranberry.Zone.Vehicles;

// The wire a drivable ground vehicle needs on top of the live-proven parachute burst (docs/43).
//
// Everything the chute already sends is vehicle-id agnostic and is REUSED UNCHANGED from
// Cranberry.Zone.VehiclePackets / MatchFlowPackets / ManagedMovementPackets:
//
//   d7 AddLightweightVehicle   spawn          (owner guid 0 for a parked car, docs/43 §4.1)
//   db LightweightToFullVehicle                clears the "awaiting full data" bit
//   70 02 MountResponse        seat + isDriver already parameterised
//   70 04 DismountResponse
//   11 0039 ManagedObjectResponseControl       the release half of ownership
//   70 01 MountRequest / 70 03 DismountRequest / 88 18 VehicleDismiss  (c2s parsers)
//
// This file adds only what the chute never needed, and nothing here edits those files: the
// post-spawn ownership transfer (§4.2), the four small state packets (§4.4), the c2s move-mode
// report (§4.5), the seat-change pair (§5.3), the multi-seat forms of Owner/Occupy (§5.2) and the
// bystander pose relay (§6).
//
// Every layout below is the August client's own parser read sequence; lengths are exact and each
// record asserts its own.

/// <summary>
/// The empty identity sub-record (<c>FUN_140a40000</c>: <c>u32×3; str; str; str; str; u64</c>),
/// 36 bytes when every string is empty. It appears verbatim inside <c>70 02 MountResponse</c>,
/// <c>70 0b SeatChangeResponse</c>, <c>70 0c SeatSwapRequest</c> and each passenger element of
/// <c>88 01</c>/<c>88 02</c>. Cranberry never populates it — the client already knows every player's
/// identity from the character records — so it is written zeroed at every call site.
/// </summary>
internal static class MountIdentityCodec
{
    /// <summary>Wire length with every string empty.</summary>
    public const int EmptyLength = 36;

    public static void WriteEmpty(PacketWriter w)
    {
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteString(string.Empty);
        w.WriteString(string.Empty);
        w.WriteString(string.Empty);
        w.WriteString(string.Empty);
        w.WriteUInt64(0);
    }
}

/// <summary>
/// <c>Character.ManagedObject</c> (base 0x0f, u8 sub 0x3b; registration <c>0x3b000f00</c>
/// <c>cCharacterPacketIdManagedObject</c>; parser <c>FUN_140a5faa0</c>, handler
/// <c>FUN_140b04c90</c>): <c>u64 objectGuid; u64 characterGuid; u64 ownerGuid</c>, <b>26 bytes
/// exact</b> — <c>param_4 = 0</c> at the parser's call site, so a trailing byte rejects it.
///
/// <para><b>This is the packet a parked car needs and the parachute never did.</b> The chute is
/// created already owned, so its owner guid rides in the <c>0xd7</c> tail; a car is spawned unowned
/// and handed over later. The handler reaches <b>the same</b> <c>FUN_140ab20c0</c> take-control
/// function the <c>0xd7</c> tail uses, and writes the owner into <b>the same</b>
/// <c>vehicle+0x45b0</c> field — which is what makes this the post-spawn equivalent of the tail
/// owner guid (docs/43 §4.2).</para>
///
/// <para>Send it to <i>every</i> client in range: the driver's copy takes control and begins
/// streaming <c>0x90</c> poses, everyone else's copy merely learns who the owner is.</para>
///
/// <para>Releasing with <see cref="Release"/> clears <c>+0x45b0</c> everywhere, but it does not by
/// itself remove the object from the ex-driver's managed map —
/// <see cref="ManagedObjectResponseControl"/><c>(false)</c> does, and that half is already
/// live-proven (docs/12 §4). Send both.</para>
/// </summary>
public sealed record CharacterManagedObject(ulong ObjectGuid, ulong CharacterGuid, ulong OwnerGuid)
{
    public const byte Opcode = ZoneOpcodes.CharacterBase;
    public const byte SubOpcode = 0x3b;
    public const int Length = 26;

    /// <summary>Hands <paramref name="objectGuid"/> to <paramref name="riderGuid"/> to simulate.</summary>
    public static CharacterManagedObject Grant(ulong objectGuid, ulong riderGuid) =>
        new(objectGuid, riderGuid, riderGuid);

    /// <summary>Clears the owner of <paramref name="objectGuid"/> on every client.</summary>
    public static CharacterManagedObject Release(ulong objectGuid) => new(objectGuid, 0, 0);

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        w.WriteUInt64(ObjectGuid);       // parser +0x18
        w.WriteUInt64(CharacterGuid);    // parser +0x20
        w.WriteUInt64(OwnerGuid);        // parser +0x28 → vehicle+0x45b0
    }

    public static CharacterManagedObject Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        PacketGuard.Head(ref reader, Opcode, SubOpcode, "Character.ManagedObject");
        var record = new CharacterManagedObject(
            ObjectGuid: reader.ReadUInt64(),
            CharacterGuid: reader.ReadUInt64(),
            OwnerGuid: reader.ReadUInt64());
        PacketGuard.End(ref reader, "Character.ManagedObject");
        return record;
    }
}

/// <summary>
/// <c>Vehicle.Engine</c> (base 0x88, u8 sub 0x1b; parser <c>FUN_140c8b930</c>, handler
/// <c>FUN_140e80850</c>): <c>u64 characterGuid; u64 vehicleGuid; u8 engineOn</c>, <b>19 bytes</b>.
///
/// <para>The guid order is <b>character first, vehicle second</b>: the dispatcher looks the actor up
/// by the <i>second</i> guid (docs/43 §4.4). The handler flips bit <c>0x20</c> of
/// <c>actor+0x43f0</c> and starts or stops the engine loop through <c>actor+0x6288</c> — so this,
/// and nothing else, is what makes a car audibly running, and it is the completion signal for a
/// server-side hotwire timer (§8.2).</para>
/// </summary>
public sealed record VehicleEngine(ulong CharacterGuid, ulong VehicleGuid, bool EngineOn)
{
    // 140c95340/1b skips application when the origin equals the local player GUID.
    // Zero makes the native receiver update the engine-enabled bit as well as audio.
    // The driver's MotorRun effect request still needs that authoritative state update.
    public static VehicleEngine ServerIssued(ulong vehicleGuid, bool engineOn) => new(0, vehicleGuid, engineOn);

    public const byte Opcode = ZoneOpcodes.VehicleBase;
    public const byte SubOpcode = 0x1b;
    public const int Length = 19;

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        w.WriteUInt64(CharacterGuid);
        w.WriteUInt64(VehicleGuid);
        w.WriteBool(EngineOn);
    }
}

/// <summary>
/// <c>Vehicle.AccessType</c> (base 0x88, u8 sub 0x1c; parser <c>FUN_140c8afc0</c>, handler
/// <c>FUN_140c98ae0</c>): <c>u64 vehicleGuid; u16 accessType</c>, <b>12 bytes</b>. Stored into
/// <c>vehicle+0x6338</c>; when the local player owns the vehicle the handler raises the script event
/// <c>"OnOwnedVehicleAccessTypeChange"</c> with the boolean <c>accessType == 2</c>. A BR match leaves
/// it <see cref="Unlocked"/>.
/// </summary>
public sealed record VehicleAccessType(ulong VehicleGuid, ushort AccessType)
{
    public const byte Opcode = ZoneOpcodes.VehicleBase;
    public const byte SubOpcode = 0x1c;
    public const int Length = 12;

    /// <summary>Open to anyone — the only value a battle-royale round needs.</summary>
    public const ushort Unlocked = 0;

    /// <summary>The value the handler reports to the UI as "owned/locked" (<c>== 2</c>).</summary>
    public const ushort Locked = 2;

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        w.WriteUInt64(VehicleGuid);
        w.WriteUInt16(AccessType);
    }
}

/// <summary>
/// <c>Vehicle.HealthUpdateOwner</c> (base 0x88, u8 sub 0x1e; parser <c>FUN_140c8bc40</c>):
/// <c>u64 ownerCharacterGuid; u32 health</c>, <b>14 bytes</b>. Applied only when the guid is the
/// receiving client's own, and it drives the HUD refresh <c>FUN_1416de000(…, 0, 4)</c> — i.e. this
/// is the driver's vehicle-health bar, addressed to the <b>driver</b>, not to the vehicle.
/// </summary>
public sealed record VehicleHealthUpdateOwner(ulong OwnerCharacterGuid, uint Health)
{
    public const byte Opcode = ZoneOpcodes.VehicleBase;
    public const byte SubOpcode = 0x1e;
    public const int Length = 14;

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        w.WriteUInt64(OwnerCharacterGuid);
        w.WriteUInt32(Health);
    }
}

/// <summary>
/// <c>Vehicle.Deploy</c> (base 0x88, u8 sub 0x1a; parser <c>FUN_140c8b790</c>):
/// <c>u64 vehicleGuid; u32 value</c> → actor <c>vtable+0x380(value, 0)</c>, <b>14 bytes</b>.
/// </summary>
public sealed record VehicleDeploy(ulong VehicleGuid, uint Value)
{
    public const byte Opcode = ZoneOpcodes.VehicleBase;
    public const byte SubOpcode = 0x1a;
    public const int Length = 14;

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        w.WriteUInt64(VehicleGuid);
        w.WriteUInt32(Value);
    }
}

/// <summary>
/// <c>Vehicle.StateDamage</c> (base 0x88, u8 sub 0x04; parser <c>FUN_140c8cb10</c>):
/// <c>u64 vehicleGuid; u32; u32</c>, <b>18 bytes</b>.
/// </summary>
public sealed record VehicleStateDamage(ulong VehicleGuid, uint ValueA, uint ValueB)
{
    public const byte Opcode = ZoneOpcodes.VehicleBase;
    public const byte SubOpcode = 0x04;
    public const int Length = 18;

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        w.WriteUInt64(VehicleGuid);
        w.WriteUInt32(ValueA);
        w.WriteUInt32(ValueB);
    }
}

/// <summary>One element of a vehicle state list: <c>u32 hashKey; u8 value</c>, bucketed by <c>key &amp; 0xf</c>.</summary>
public readonly record struct VehicleStateEntry(uint Key, byte Value)
{
    public const int Length = 5;
}

/// <summary>
/// <c>Vehicle.StateData</c> (base 0x88, u8 sub 0x03; parser <c>FUN_140c8cc70</c>):
/// <c>u64 vehicleGuid; u32; i32 nA {u32 key; u8 value}; i32 nB {u32 key; u8 value}</c>,
/// <b>22 bytes minimum</b>.
///
/// <para>Both list readers (<c>FUN_140a511c0</c>, <c>FUN_140a4cd00</c>) are the same pair
/// <c>LightweightToFullVehicle 0xdb</c> carries immediately after its <c>u8 engineState; u32</c> —
/// which identifies that part of the <c>0xdb</c> tail as an embedded vehicle-state block (docs/43
/// §4.4). Cranberry writes both empty today and the mount works live, so <b>empty is the proven-safe
/// form</b> and the key space is an open lead (docs/43 §10 row 7).</para>
/// </summary>
public sealed record VehicleStateData(
    ulong VehicleGuid,
    uint Value = 0,
    IReadOnlyList<VehicleStateEntry>? ListA = null,
    IReadOnlyList<VehicleStateEntry>? ListB = null)
{
    public const byte Opcode = ZoneOpcodes.VehicleBase;
    public const byte SubOpcode = 0x03;
    public const int MinimumLength = 22;

    public int Length => MinimumLength
        + (((ListA?.Count ?? 0) + (ListB?.Count ?? 0)) * VehicleStateEntry.Length);

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        w.WriteUInt64(VehicleGuid);
        w.WriteUInt32(Value);
        WriteList(w, ListA);   // FUN_140a511c0
        WriteList(w, ListB);   // FUN_140a4cd00
    }

    private static void WriteList(PacketWriter w, IReadOnlyList<VehicleStateEntry>? entries)
    {
        w.WriteInt32(entries?.Count ?? 0);
        if (entries is null)
        {
            return;
        }

        foreach (VehicleStateEntry entry in entries)
        {
            w.WriteUInt32(entry.Key);
            w.WriteByte(entry.Value);
        }
    }
}

/// <summary>
/// c2s <c>Vehicle.CurrentMoveMode</c> (base 0x88, u8 sub 0x27): <c>u64 vehicleGuid; u8 moveMode</c>,
/// <b>11 bytes</b> — captured live, once, in the host log as
/// <c>88 27 02 20 00 00 00 00 00 00 05</c> (vehicle guid <c>0x2002</c>, mode <c>5</c>, sent while
/// under the parachute canopy). Sub 0x27 has no <c>case</c> in the client's receive dispatcher
/// <c>FUN_140c95340</c>, which independently confirms it is client→server only.
///
/// <para><b>The client reports its selected drive mode; the server does not choose it.</b> The byte
/// is <i>not</i> a <c>MoveInfo.MOVEMENT_MODE</c> value — the parachute's only two rows are modes 1
/// and 13, not 5 — and the enum has no name strings in the image, so
/// <see cref="MoveMode"/> is <b>recorded and not interpreted</b> (docs/43 §4.5, blocker 3). Capture
/// one from a car at speed and the mapping falls out.</para>
/// </summary>
public sealed record VehicleCurrentMoveMode(ulong VehicleGuid, byte MoveMode)
{
    public const byte Opcode = ZoneOpcodes.VehicleBase;
    public const byte SubOpcode = 0x27;
    public const int Length = 11;

    /// <summary>The only value ever observed live, under the canopy. Meaning unknown.</summary>
    public const byte ObservedUnderCanopy = 0x05;

    public static VehicleCurrentMoveMode Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        PacketGuard.Head(ref reader, Opcode, SubOpcode, "Vehicle.CurrentMoveMode");
        var record = new VehicleCurrentMoveMode(reader.ReadUInt64(), reader.ReadByte());
        PacketGuard.End(ref reader, "Vehicle.CurrentMoveMode");
        return record;
    }

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        w.WriteUInt64(VehicleGuid);
        w.WriteByte(MoveMode);
    }
}

/// <summary>
/// <c>Vehicle.Owner</c> (base 0x88, u8 sub 1) in its <b>multi-occupant</b> form:
/// <c>u64 vehicle; u64 owner; u32; u32 vehicleId; i32 passengers {u64; identity; str; u8 seat}</c>.
///
/// <para><see cref="Cranberry.Zone.VehicleOwner"/> is the parachute's writer and carries at most one
/// passenger, which is all a one-seat chute can hold. A car needs the whole occupant roster, so this
/// record takes a list; the element schema is byte-for-byte the same
/// <see cref="VehiclePassengerCodec"/> the chute already proved live.</para>
///
/// <para><b>The owner's trailing byte is <see cref="OwnerElementByte"/>, not its seat index.</b> The
/// live-proven <see cref="Cranberry.Zone.VehicleOwner"/> writes a literal <c>1</c> there for a rider
/// that is unambiguously in seat 0 — the same rider its companion <c>Vehicle.Occupy</c> writes as
/// seat <c>0</c> in the very same exchange — so whatever <c>FUN_140c8c4b0</c> reads out of this
/// element, it is not the seat ordinal. Writing <c>SeatIndex</c> here made the one-driver case
/// differ from the only bytes the client has ever accepted by exactly one byte, which is the worst
/// possible state to be in for docs/43 blocker 1: an E-press that misbehaves would have had two
/// candidate causes. The owner's element reproduces the proven bytes; other occupants carry their
/// seat index, which is unproven either way and marked as such.</para>
/// </summary>
public sealed record VehicleOwnerState(
    ulong VehicleGuid,
    ulong OwnerGuid,
    uint VehicleId,
    IReadOnlyList<VehicleOccupantSlot> Passengers)
{
    public const byte Opcode = ZoneOpcodes.VehicleBase;
    public const byte SubOpcode = 0x01;

    /// <summary>Head through the passenger count, before any passenger element.</summary>
    public const int HeadLength = 30;

    /// <summary>
    /// The trailing byte the live-proven <c>Vehicle.Owner</c> carries for the owning rider
    /// (<c>VehicleOwner.WriteTo</c>, <c>seatByte: 1</c>).
    /// </summary>
    public const byte OwnerElementByte = 1;

    public int Length => HeadLength + (Passengers.Count * VehicleOccupantSlot.WireLength);

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(Passengers);
        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        w.WriteUInt64(VehicleGuid);
        w.WriteUInt64(OwnerGuid);
        w.WriteUInt32(0);
        w.WriteUInt32(VehicleId);
        w.WriteInt32(Passengers.Count);
        foreach (VehicleOccupantSlot passenger in Passengers)
        {
            // See the record's remarks: the owner's element reproduces the proven byte; nothing is
            // proven about a second occupant, so it keeps its seat index.
            byte trailing = passenger.CharacterGuid == OwnerGuid && OwnerGuid != 0
                ? OwnerElementByte
                : passenger.SeatIndex;
            VehiclePassengerCodec.Write(w, passenger.CharacterGuid, trailing);
        }
    }
}

/// <summary>One occupant: the character guid and the seat index it sits in.</summary>
/// <param name="SeatIndex">Seat ordinal, the <c>SEAT</c> column of <c>SeatInfo.txt</c>.</param>
public readonly record struct VehicleOccupantSlot(byte SeatIndex, ulong CharacterGuid)
{
    /// <summary>Length of one passenger element with an empty identity (<c>VehiclePassengerCodec</c>).</summary>
    public const int WireLength = 49;
}

/// <summary>
/// <c>Vehicle.Occupy</c> (base 0x88, u8 sub 2; parser <c>FUN_140c8c1f0</c>) in its
/// <b>multi-seat</b> form: <c>u64 vehicle; u64 character; u32 vehicleId; u32;
/// i32 seats {u32 seat; u8 occupied}; i32 passengers; i32 guids; bytes; bytes</c>.
///
/// <para><see cref="Cranberry.Zone.VehicleOccupy"/> hardcodes a one-entry seat list — correct for the
/// one-seat parachute, wrong for a five-seat car. Here the seat list carries <b>one entry per seat of
/// that vehicle</b> (seat-list reader <c>FUN_140c8d210</c>, the same 5-byte element shape as the
/// state lists) and the passenger list one entry per actual occupant (docs/43 §5.2).</para>
/// </summary>
public sealed record VehicleOccupantState(
    ulong VehicleGuid,
    ulong CharacterGuid,
    uint VehicleId,
    int SeatCount,
    IReadOnlyList<VehicleOccupantSlot> Occupants,
    uint Value = 0)
{
    public const byte Opcode = ZoneOpcodes.VehicleBase;
    public const byte SubOpcode = 0x02;

    /// <summary>
    /// Everything but the two variable-length lists' elements: the 26-byte head, the two list
    /// counts, and the trailing empty guid list and two counted-byte blocks. With one seat and one
    /// occupant this plus one of each element is the parachute's live-proven 100 bytes.
    /// </summary>
    public const int HeadLength = 46;

    /// <summary>One seat-list element: <c>u32 seat; u8 occupied</c>.</summary>
    public const int SeatEntryLength = 5;

    public int Length => HeadLength
        + (SeatCount * SeatEntryLength)
        + (Occupants.Count * VehicleOccupantSlot.WireLength);

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(Occupants);
        ArgumentOutOfRangeException.ThrowIfNegative(SeatCount);

        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        w.WriteUInt64(VehicleGuid);
        w.WriteUInt64(CharacterGuid);
        w.WriteUInt32(VehicleId);
        w.WriteUInt32(Value);

        w.WriteInt32(SeatCount);                       // FUN_140c8d210
        for (int seat = 0; seat < SeatCount; seat++)
        {
            w.WriteUInt32((uint)seat);
            w.WriteBool(IsOccupied(seat));
        }

        w.WriteInt32(Occupants.Count);                 // FUN_140a54670
        foreach (VehicleOccupantSlot occupant in Occupants)
        {
            VehiclePassengerCodec.Write(w, occupant.CharacterGuid, occupant.SeatIndex);
        }

        w.WriteInt32(0);    // guids (FUN_140c8dac0)
        w.WriteInt32(0);    // counted bytes
        w.WriteInt32(0);    // counted bytes
    }

    private bool IsOccupied(int seat)
    {
        foreach (VehicleOccupantSlot occupant in Occupants)
        {
            if (occupant.SeatIndex == seat)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// c2s <c>Mount.SeatChangeRequest</c> (base 0x70, u8 sub 0x0a) — <b>the body is a CANDIDATE, not a
/// derivation.</b>
///
/// <para>
/// The packet is registered at 1148 (<c>0xa007000 MountBasePacket::cSeatChangeRequest</c>) and the
/// mount dispatcher <c>FUN_140c9fee0</c> puts <c>0x0a</c> in the client-to-server set, but the
/// Command/Mount send path has no per-packet serializer — the same reason docs/36 gives for
/// <c>09 07 InteractRequest</c> — so nothing in the binary states the field order. This reads the
/// obvious mirror of <c>70 01 MountRequest</c>, <c>u64 guid; u32 seat</c>, and <b>every parse is
/// logged with its full hex whether it is acted on or not</b>, so the first live seat-key press is
/// the derivation (docs/43 §5.3, blocker 2).
/// </para>
/// <para>
/// The plausibility gate is what makes acting on a guess safe: a seat index that does not resolve
/// on the vehicle the player is actually sitting in is refused and logged, so a wrong guess costs a
/// log line rather than a teleport into seat 1,633,772,861.
/// </para>
/// </summary>
public sealed record SeatChangeRequest(ulong Guid, uint Seat, ReadOnlyMemory<byte> Raw)
{
    public const byte Opcode = ZoneOpcodes.MountBase;

    public const byte SubOpcode = 0x0a;

    /// <summary>Head plus the two candidate fields. The client may send more; trailing bytes are kept.</summary>
    public const int MinimumLength = 14;

    public static bool TryParse(ReadOnlySpan<byte> payload, out SeatChangeRequest? request)
    {
        request = null;
        if (payload.Length < MinimumLength || payload[0] != Opcode || payload[1] != SubOpcode)
        {
            return false;
        }

        var reader = new PacketReader(payload);
        reader.Skip(2);
        request = new SeatChangeRequest(reader.ReadUInt64(), reader.ReadUInt32(), payload.ToArray());
        return true;
    }
}

/// <summary>
/// <c>Mount.SeatChangeResponse</c> (base 0x70, u8 sub 0x0b; parser <c>FUN_140c9e360</c>):
/// <c>u64 rider; u64 mount; identity(36 B); u32; u32; u32</c>, <b>66 bytes</b>.
/// </summary>
public sealed record SeatChangeResponse(
    ulong Rider,
    ulong Mount,
    uint Seat,
    uint IsDriver,
    uint Status = 1)
{
    public const byte Opcode = ZoneOpcodes.MountBase;
    public const byte SubOpcode = 0x0b;
    public const int Length = 66;

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        w.WriteUInt64(Rider);
        w.WriteUInt64(Mount);
        MountIdentityCodec.WriteEmpty(w);
        w.WriteUInt32(Seat);
        w.WriteUInt32(IsDriver);
        w.WriteUInt32(Status);
    }
}

/// <summary>
/// <c>Mount.SeatSwapRequest</c> (base 0x70, u8 sub 0x0c; parser <c>FUN_140c9e540</c>):
/// <c>u64 rider; identity(36 B); u32 seat</c>, <b>50 bytes</b>. The client's own locale names it
/// ("SWAP SEAT REQUEST", "Another player has already requested that seat.").
/// </summary>
public sealed record SeatSwapRequest(ulong Rider, uint Seat)
{
    public const byte Opcode = ZoneOpcodes.MountBase;
    public const byte SubOpcode = 0x0c;
    public const int Length = 50;

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte(Opcode);
        w.WriteByte(SubOpcode);
        w.WriteUInt64(Rider);
        MountIdentityCodec.WriteEmpty(w);
        w.WriteUInt32(Seat);
    }
}

/// <summary>
/// s2c <c>PlayerUpdatePosition</c> (0x78): <c>u8 0x78; varint transientId; movement record</c> —
/// <b>byte-identical in shape to the c2s <c>0x90 PlayerUpdateManagedPosition</c></b> the driver's
/// client streams (parser <c>FUN_140a600b0</c> reads the same varint reader
/// <c>FUN_140a190f0</c> and the same movement-record reader <c>FUN_140a3ca40</c>; the client has a
/// <c>case 0x78</c> but <b>no <c>case 0x90</c></b>, so 0x90 is send-only and 0x78 is its receive
/// side — docs/43 §3.3).
///
/// <para>That makes the bystander relay a <b>verbatim copy with one byte changed</b>: this record
/// re-emits the driver's own movement payload untouched, so nothing is re-quantised on the way
/// through and a pose the owner authored is the pose everyone else sees. The transient id needs no
/// re-encoding because Cranberry's <c>TransientIdTable</c> is zone-wide.</para>
///
/// <para><b>Unverified:</b> whether a bystander's client accepts a <c>0x78</c> for an entity it holds
/// as a <i>vehicle</i> actor rather than a character actor. <c>case 0x78</c> dispatches through the
/// zone client's <c>vtable+0x250</c> with no type test visible in the arm, so it should be uniform —
/// but this is the single most valuable thing to prove in the first two-client test
/// (docs/43 §6, blocker 4).</para>
/// </summary>
public sealed record VehiclePoseRelay(uint TransientId, ReadOnlyMemory<byte> MovementPayload)
{
    public const byte Opcode = ZoneOpcodes.PlayerUpdatePosition;

    /// <summary>August can finish coasting with posture 0x41 alone, leaving residual speed/RPM
    /// from its previous delta. Supply the zero motion fields without changing position or control.</summary>
    public static VehiclePoseRelay MotionStopped(ClientManagedMovementUpdate update)
    {
        using var writer = new PacketWriter();
        writer.WriteUInt16((ushort)(MovementFieldMask.Posture | MovementFieldMask.Scalar154
            | MovementFieldMask.VerticalSpeed | MovementFieldMask.HorizontalSpeed
            | MovementFieldMask.AuxiliaryVector | MovementFieldMask.Scalar140 | MovementFieldMask.Scalar144));
        writer.WriteUInt32(update.Movement.ClientTime);
        writer.WriteByte(update.Movement.State);
        ClientVarInt.Write(writer, update.Movement.Posture!.Value);
        for (int i = 0; i < 8; i++) ClientPackedInt.Write(writer, 0);
        return new(update.TransientId, writer.Written.ToArray());
    }

    /// <summary>Seed the remote movement controller after local simulation empties its queue.
    /// FUN_140b14d30 rejects a partial mask when FUN_140c16a90 cannot obtain a baseline; the
    /// driver's take-control path explicitly clears that baseline in FUN_140c110b0. A full
    /// 0x1fff record with valid posture is therefore required at a control handoff.</summary>
    public static VehiclePoseRelay Parked(MatchVehicle vehicle)
    {
        using var writer = new PacketWriter();
        writer.WriteUInt16((ushort)MovementFieldMask.All);
        writer.WriteUInt32(unchecked(vehicle.LastClientTime + 1));
        writer.WriteByte(vehicle.MovementVersion);
        // Actual offroader full record, wire-20260906-180440 18:08:21.606: valid, snap, stopped.
        ClientVarInt.Write(writer, 0x49);
        void Packed(float value) => ClientPackedInt.Write(writer, (int)MathF.Round(value * 100f));
        void Position()
        {
            Packed(vehicle.Position.X); Packed(vehicle.Position.Y); Packed(vehicle.Position.Z);
        }
        var rotation = vehicle.LastRotation ?? System.Numerics.Quaternion.CreateFromAxisAngle(
            System.Numerics.Vector3.UnitY, vehicle.Yaw);
        // The actual orientation is reconstructed from yaw/pitch/roll by 142339720 -> 140c43490.
        // Movement bit 0x200 is NOT the body's quaternion: the native full vehicle record has
        // (1,1,1,1) there, and all seven 0x1000 values are zero while the car is at a real pose.
        float pitch = MathF.Asin(Math.Clamp(2f * (rotation.W * rotation.X - rotation.Y * rotation.Z), -1f, 1f));
        float yaw = vehicle.LastRotation is null ? vehicle.Yaw
            : MathF.Atan2(2f * (rotation.W * rotation.Y + rotation.X * rotation.Z),
                1f - 2f * (rotation.X * rotation.X + rotation.Y * rotation.Y));
        float roll = MathF.Atan2(2f * (rotation.W * rotation.Z + rotation.X * rotation.Y),
            1f - 2f * (rotation.X * rotation.X + rotation.Z * rotation.Z));
        Position();
        writer.WriteSingle(yaw);
        Packed(pitch); Packed(roll);
        for (int i = 0; i < 6; i++) ClientPackedInt.Write(writer, 0); // scalar, V/H speed, vector
        for (int i = 0; i < 4; i++) Packed(1f); // native neutral 0x200 block
        ClientPackedInt.Write(writer, 0);
        ClientPackedInt.Write(writer, 0);
        for (int i = 0; i < 7; i++) ClientPackedInt.Write(writer, 0); // native neutral 0x1000 block
        return new(vehicle.TransientId, writer.Written.ToArray());
    }

    /// <summary>
    /// The relay form of a pose the owning client just sent us on channel 3. The movement record is
    /// carried through by reference, never re-encoded.
    /// </summary>
    public static VehiclePoseRelay From(ClientManagedMovementUpdate managed)
    {
        ArgumentNullException.ThrowIfNull(managed);
        return new VehiclePoseRelay(managed.TransientId, managed.Movement.Payload);
    }

    public int Length => 1 + ClientVarInt.Length(TransientId) + MovementPayload.Length;

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte(Opcode);
        ClientVarInt.Write(w, TransientId);
        w.WriteRaw(MovementPayload.Span);
    }
}

/// <summary>
/// ClientUpdate.UpdateManagedLocation, native 140a36d90 / 140afc660 case 0x23. Unlike the
/// interpolated 0x78, this synchronously sets the actor's controller transform and clears its
/// linear/angular motion through virtual +0x30/+0x40/+0x50. Its position is a body origin,
/// whereas movement records include a physics-local offset handled by native 142337f30.
/// Ordinary control handoffs retain that body transform and use the vehicle movement path.
/// </summary>
public sealed record VehicleManagedLocation(ulong Guid, System.Numerics.Vector4 Position,
    System.Numerics.Quaternion Rotation, byte MovementVersion = 0)
{
    public const int Length = 45;
    public static VehicleManagedLocation At(MatchVehicle vehicle) => new(vehicle.Guid,
        new System.Numerics.Vector4(vehicle.Position, 1f),
        vehicle.LastRotation ?? System.Numerics.Quaternion.CreateFromAxisAngle(
            System.Numerics.Vector3.UnitY, vehicle.Yaw), vehicle.MovementVersion);

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(ZoneOpcodes.ClientUpdateBase);
        writer.WriteUInt16(0x23);
        writer.WriteUInt64(Guid);
        writer.WriteSingle(Position.X); writer.WriteSingle(Position.Y);
        writer.WriteSingle(Position.Z); writer.WriteSingle(Position.W);
        writer.WriteSingle(Rotation.X); writer.WriteSingle(Rotation.Y);
        writer.WriteSingle(Rotation.Z); writer.WriteSingle(Rotation.W);
        writer.WriteBool(true);
        writer.WriteByte(MovementVersion);
    }
}

/// <summary>Head/tail checks shared by this file's parsers.</summary>
internal static class PacketGuard
{
    public static void Head(ref PacketReader reader, byte opcode, byte subOpcode, string name)
    {
        byte actualOpcode = reader.ReadByte();
        byte actualSub = reader.ReadByte();
        if (actualOpcode != opcode || actualSub != subOpcode)
        {
            throw new PacketFormatException(
                $"Expected {name} {opcode:x2} {subOpcode:x2}, got {actualOpcode:x2} {actualSub:x2}.");
        }
    }

    public static void End(ref PacketReader reader, string name)
    {
        if (!reader.AtEnd)
        {
            throw new PacketFormatException($"{name} has {reader.Remaining} trailing byte(s).");
        }
    }
}
