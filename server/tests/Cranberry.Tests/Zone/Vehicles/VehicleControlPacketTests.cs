using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

/// <summary>
/// The wire a drivable car adds to the live-proven parachute burst (docs/43 §4-§6).
///
/// <para>Each layout is the August client's own parser read sequence, and each parser rejects a
/// trailing byte, so the tests assert <b>exact</b> lengths and, where a field order was the
/// derivation's actual finding (the character-before-vehicle order of <c>88 1b</c>, the three guids
/// of <c>0f 3b</c>), the exact bytes.</para>
/// </summary>
public sealed class VehicleControlPacketTests
{
    private const ulong Vehicle = 0x1122_3344_5566_7788UL;
    private const ulong Rider = 0x0102_0304_0506_0708UL;

    private static byte[] Write(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    // --- 0f 3b Character.ManagedObject -------------------------------------------------------

    /// <summary>
    /// The packet a parked car needs and the parachute never did: 26 bytes, three guids, in the
    /// parser's read order (<c>FUN_140a5faa0</c> → record +0x18, +0x20, +0x28).
    /// </summary>
    [Fact]
    public void CharacterManagedObjectIsTwentySixBytesOfOpcodeAndThreeGuids()
    {
        byte[] bytes = Write(w => new CharacterManagedObject(Vehicle, Rider, Rider).WriteTo(w));

        Assert.Equal(CharacterManagedObject.Length, bytes.Length);
        Assert.Equal(26, bytes.Length);
        Assert.Equal(ZoneOpcodes.CharacterBase, bytes[0]);
        Assert.Equal(0x3b, bytes[1]);
        Assert.Equal(Vehicle, BitConverter.ToUInt64(bytes, 2));
        Assert.Equal(Rider, BitConverter.ToUInt64(bytes, 10));
        Assert.Equal(Rider, BitConverter.ToUInt64(bytes, 18));
    }

    /// <summary>
    /// <c>Grant</c> puts the rider in both the character and owner fields — the handler takes
    /// control on the <b>owner</b> guid (<c>FUN_140ab20c0</c>) and attaches on the character guid, so
    /// a driver needs both. <c>Release</c> zeroes both, which clears <c>vehicle+0x45b0</c> on every
    /// client; the vehicle guid stays, because the car is not being removed from the world.
    /// </summary>
    [Fact]
    public void GrantCarriesTheRiderTwiceAndReleaseZeroesBothWithoutLosingTheVehicle()
    {
        CharacterManagedObject grant = CharacterManagedObject.Grant(Vehicle, Rider);
        Assert.Equal(Vehicle, grant.ObjectGuid);
        Assert.Equal(Rider, grant.CharacterGuid);
        Assert.Equal(Rider, grant.OwnerGuid);

        CharacterManagedObject release = CharacterManagedObject.Release(Vehicle);
        Assert.Equal(Vehicle, release.ObjectGuid);
        Assert.Equal(0UL, release.CharacterGuid);
        Assert.Equal(0UL, release.OwnerGuid);
    }

    [Fact]
    public void CharacterManagedObjectRoundTripsAndRejectsAnyOtherLength()
    {
        byte[] bytes = Write(w => new CharacterManagedObject(Vehicle, Rider, 0).WriteTo(w));

        CharacterManagedObject parsed = CharacterManagedObject.Parse(bytes);
        Assert.Equal(Vehicle, parsed.ObjectGuid);
        Assert.Equal(Rider, parsed.CharacterGuid);
        Assert.Equal(0UL, parsed.OwnerGuid);

        // param_4 = 0 at the parser's call site: one extra byte rejects the whole packet.
        Assert.Throws<PacketFormatException>(() => CharacterManagedObject.Parse([.. bytes, (byte)0]));

        for (int length = 0; length < bytes.Length; length++)
        {
            byte[] truncated = bytes[..length];
            Assert.ThrowsAny<Exception>(() => CharacterManagedObject.Parse(truncated));
        }

        Assert.Throws<PacketFormatException>(() =>
            CharacterManagedObject.Parse([ZoneOpcodes.CharacterBase, 0x3c, .. bytes[2..]]));
    }

    // --- the four small 0x88 state packets ---------------------------------------------------

    /// <summary>
    /// <c>88 1b</c>'s guid order was the derivation's actual finding: the dispatcher looks the actor
    /// up by the <b>second</b> guid, so it is character first and vehicle second. Getting this
    /// backwards would silently address the wrong actor, which is exactly the class of bug that only
    /// shows up live, so it is asserted byte-for-byte.
    /// </summary>
    [Fact]
    public void VehicleEngineIsNineteenBytesWithTheCharacterGuidFirst()
    {
        byte[] bytes = Write(w => new VehicleEngine(Rider, Vehicle, EngineOn: true).WriteTo(w));

        Assert.Equal(VehicleEngine.Length, bytes.Length);
        Assert.Equal(19, bytes.Length);
        Assert.Equal(ZoneOpcodes.VehicleBase, bytes[0]);
        Assert.Equal(0x1b, bytes[1]);
        Assert.Equal(Rider, BitConverter.ToUInt64(bytes, 2));
        Assert.Equal(Vehicle, BitConverter.ToUInt64(bytes, 10));
        Assert.Equal(1, bytes[18]);

        byte[] off = Write(w => new VehicleEngine(Rider, Vehicle, EngineOn: false).WriteTo(w));
        Assert.Equal(0, off[18]);
    }

    [Fact]
    public void VehicleAccessTypeIsTwelveBytesAndDefaultsToUnlocked()
    {
        byte[] bytes = Write(w => new VehicleAccessType(Vehicle, VehicleAccessType.Unlocked).WriteTo(w));

        Assert.Equal(VehicleAccessType.Length, bytes.Length);
        Assert.Equal(12, bytes.Length);
        Assert.Equal(0x1c, bytes[1]);
        Assert.Equal(Vehicle, BitConverter.ToUInt64(bytes, 2));
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 10));

        // The handler reports "owned/locked" to the UI on accessType == 2.
        Assert.Equal(2, VehicleAccessType.Locked);
    }

    /// <summary>
    /// The health bar is addressed to the <b>driver</b>, not to the vehicle: the client applies it
    /// only when the guid is its own player's.
    /// </summary>
    [Fact]
    public void VehicleHealthUpdateOwnerIsFourteenBytesKeyedOnTheOwningCharacter()
    {
        byte[] bytes = Write(w => new VehicleHealthUpdateOwner(Rider, 12_345).WriteTo(w));

        Assert.Equal(VehicleHealthUpdateOwner.Length, bytes.Length);
        Assert.Equal(14, bytes.Length);
        Assert.Equal(0x1e, bytes[1]);
        Assert.Equal(Rider, BitConverter.ToUInt64(bytes, 2));
        Assert.Equal(12_345u, BitConverter.ToUInt32(bytes, 10));
    }

    [Fact]
    public void VehicleDeployAndStateDamageMatchTheirParserLengths()
    {
        byte[] deploy = Write(w => new VehicleDeploy(Vehicle, 7).WriteTo(w));
        Assert.Equal(VehicleDeploy.Length, deploy.Length);
        Assert.Equal(14, deploy.Length);
        Assert.Equal(0x1a, deploy[1]);

        byte[] damage = Write(w => new VehicleStateDamage(Vehicle, 3, 4).WriteTo(w));
        Assert.Equal(VehicleStateDamage.Length, damage.Length);
        Assert.Equal(18, damage.Length);
        Assert.Equal(0x04, damage[1]);
        Assert.Equal(3u, BitConverter.ToUInt32(damage, 10));
        Assert.Equal(4u, BitConverter.ToUInt32(damage, 14));
    }

    /// <summary>
    /// Empty is the form Cranberry already sends inside <c>0xdb</c> and which mounts live, so it is
    /// the proven-safe baseline; the 5-byte <c>{u32 key; u8 value}</c> element is asserted so a
    /// future key experiment starts from a shape the client can actually read.
    /// </summary>
    [Fact]
    public void VehicleStateDataIsTwentyTwoBytesEmptyAndGrowsByFivePerEntry()
    {
        byte[] empty = Write(w => new VehicleStateData(Vehicle).WriteTo(w));
        Assert.Equal(VehicleStateData.MinimumLength, empty.Length);
        Assert.Equal(22, empty.Length);
        Assert.Equal(0, BitConverter.ToInt32(empty, 14));
        Assert.Equal(0, BitConverter.ToInt32(empty, 18));

        var populated = new VehicleStateData(
            Vehicle,
            Value: 1,
            ListA: [new VehicleStateEntry(0x1234_5678, 9)],
            ListB: [new VehicleStateEntry(1, 2), new VehicleStateEntry(3, 4)]);

        byte[] bytes = Write(w => populated.WriteTo(w));
        Assert.Equal(populated.Length, bytes.Length);
        Assert.Equal(22 + (3 * VehicleStateEntry.Length), bytes.Length);
        Assert.Equal(1, BitConverter.ToInt32(bytes, 14));
        Assert.Equal(0x1234_5678u, BitConverter.ToUInt32(bytes, 18));
        Assert.Equal(9, bytes[22]);
        Assert.Equal(2, BitConverter.ToInt32(bytes, 23));
    }

    // --- 88 27, the one live c2s value -------------------------------------------------------

    /// <summary>
    /// The exact 11 bytes the August client sent under the canopy, from the host log
    /// (<c>grep -ihoE "8827[0-9a-f]*" logs/host-*.log</c> → one distinct value, four times). This is
    /// the only live evidence for the packet, so the test is the capture itself.
    /// </summary>
    [Fact]
    public void VehicleCurrentMoveModeParsesTheOneLiveCapture()
    {
        byte[] captured = Convert.FromHexString("88270220000000000000" + "05");

        VehicleCurrentMoveMode parsed = VehicleCurrentMoveMode.Parse(captured);

        Assert.Equal(VehicleCurrentMoveMode.Length, captured.Length);
        Assert.Equal(11, captured.Length);
        Assert.Equal(0x2002UL, parsed.VehicleGuid);
        Assert.Equal(VehicleCurrentMoveMode.ObservedUnderCanopy, parsed.MoveMode);
        Assert.Equal(5, parsed.MoveMode);

        Assert.Equal(captured, Write(w => parsed.WriteTo(w)));
        Assert.Throws<PacketFormatException>(() => VehicleCurrentMoveMode.Parse([.. captured, (byte)0]));
    }

    // --- the multi-seat forms of the possession burst -----------------------------------------

    /// <summary>
    /// The parachute's <c>88 02</c> writer is live-proven at 100 bytes with one seat and one
    /// occupant. The multi-seat writer must be a strict superset: for that same one-seat, seat-0
    /// case it has to produce <b>identical bytes</b>, or a car would be shipping a shape the live
    /// capture never validated.
    /// </summary>
    [Fact]
    public void MultiSeatOccupyIsByteIdenticalToTheLiveProvenChuteWriterForOneSeat()
    {
        byte[] chute = Write(w => new VehicleOccupy(Vehicle, Rider, VehicleId: 13, Seat: 0).WriteTo(w));
        byte[] car = Write(w => new VehicleOccupantState(
            Vehicle, Rider, VehicleId: 13, SeatCount: 1, Occupants: [new VehicleOccupantSlot(0, Rider)]).WriteTo(w));

        Assert.Equal(VehicleOccupy.OccupiedLength, chute.Length);
        Assert.Equal(chute, car);
    }

    /// <summary>
    /// A five-seat OffRoader with the driver aboard: the seat list carries one entry per seat with
    /// only seat 0 marked occupied, and the passenger list one entry per actual occupant.
    /// </summary>
    [Fact]
    public void MultiSeatOccupyMarksEverySeatAndOnlyTheOccupiedOnesTrue()
    {
        var occupy = new VehicleOccupantState(
            Vehicle, Rider, VehicleId: 1, SeatCount: 5, Occupants: [new VehicleOccupantSlot(2, Rider)]);

        byte[] bytes = Write(w => occupy.WriteTo(w));

        Assert.Equal(occupy.Length, bytes.Length);
        Assert.Equal(
            VehicleOccupantState.HeadLength
                + (5 * VehicleOccupantState.SeatEntryLength)
                + VehicleOccupantSlot.WireLength,
            bytes.Length);

        Assert.Equal(5, BitConverter.ToInt32(bytes, 26));
        for (int seat = 0; seat < 5; seat++)
        {
            int at = 30 + (seat * VehicleOccupantState.SeatEntryLength);
            Assert.Equal((uint)seat, BitConverter.ToUInt32(bytes, at));
            Assert.Equal(seat == 2 ? 1 : 0, bytes[at + 4]);
        }

        Assert.Equal(1, BitConverter.ToInt32(bytes, 55));
        Assert.Equal(Rider, BitConverter.ToUInt64(bytes, 59));
    }

    [Fact]
    public void OwnerStateGrowsByOnePassengerElementPerOccupant()
    {
        byte[] empty = Write(w => new VehicleOwnerState(Vehicle, 0, VehicleId: 1, Passengers: []).WriteTo(w));
        Assert.Equal(VehicleOwnerState.HeadLength, empty.Length);
        Assert.Equal(VehicleOwner.ClearedLength, empty.Length);

        var owned = new VehicleOwnerState(
            Vehicle, Rider, VehicleId: 1, Passengers: [new VehicleOccupantSlot(0, Rider), new VehicleOccupantSlot(3, 99)]);
        byte[] bytes = Write(w => owned.WriteTo(w));

        Assert.Equal(owned.Length, bytes.Length);
        Assert.Equal(VehicleOwner.ClearedLength + (2 * VehicleOccupantSlot.WireLength), bytes.Length);
        Assert.Equal(VehicleOwner.OccupiedLength + VehicleOccupantSlot.WireLength, bytes.Length);
        Assert.Equal(Rider, BitConverter.ToUInt64(bytes, 10));
        Assert.Equal(2, BitConverter.ToInt32(bytes, 26));
    }

    /// <summary>
    /// The one-occupant owner packet must be byte-identical to the live-proven
    /// <see cref="VehicleOwner"/>, trailing byte included.
    /// <para>
    /// The proven writer puts a literal <c>1</c> in the owning rider's element while its companion
    /// <c>Vehicle.Occupy</c> writes that same rider as seat <c>0</c> in the same exchange, so the
    /// byte is not the seat ordinal. Writing <c>SeatIndex</c> here left the single-driver case one
    /// byte away from the only bytes the client has ever accepted — the worst possible state for
    /// docs/43 blocker 1, where an E-press that misbehaves must have exactly one candidate cause.
    /// </para>
    /// </summary>
    [Fact]
    public void OneDriverOwnerStateIsByteIdenticalToTheProvenOwnerPacket()
    {
        byte[] proven = Write(w => new VehicleOwner(Vehicle, Rider, VehicleId: 7).WriteTo(w));
        byte[] state = Write(w => new VehicleOwnerState(
            Vehicle, Rider, VehicleId: 7, Passengers: [new VehicleOccupantSlot(0, Rider)]).WriteTo(w));

        Assert.Equal(VehicleOwner.OccupiedLength, proven.Length);
        Assert.Equal(proven, state);
        Assert.Equal(VehicleOwnerState.OwnerElementByte, state[^1]);

        // A non-owning passenger keeps its seat index; nothing is proven about that element either
        // way, and it must not silently inherit the owner's byte.
        byte[] pair = Write(w => new VehicleOwnerState(
            Vehicle,
            Rider,
            VehicleId: 7,
            Passengers: [new VehicleOccupantSlot(0, Rider), new VehicleOccupantSlot(3, 99)]).WriteTo(w));
        Assert.Equal(
            VehicleOwnerState.OwnerElementByte,
            pair[pair.Length - 1 - VehicleOccupantSlot.WireLength]);
        Assert.Equal(3, pair[^1]);
    }

    /// <summary>
    /// <c>MountResponse</c> carries seat and isDriver as separate dwords, and the record's
    /// <c>IsDriver</c> default of 1 is the parachute's — a one-seat, driver-only mount. A car
    /// passenger must be told it is not driving.
    /// </summary>
    [Fact]
    public void MountResponseCarriesTheDriverFlagIndependentlyOfTheSeat()
    {
        byte[] driver = Write(w => new MountResponse(Rider, Vehicle, Seat: 0, IsDriver: 1).WriteTo(w));
        byte[] passenger = Write(w => new MountResponse(Rider, Vehicle, Seat: 2, IsDriver: 0).WriteTo(w));

        // u64 rider; u64 mount; u32 seat; u32 status; u32 isDriver
        Assert.Equal(0u, BitConverter.ToUInt32(driver, 18));
        Assert.Equal(1u, BitConverter.ToUInt32(driver, 26));
        Assert.Equal(2u, BitConverter.ToUInt32(passenger, 18));
        Assert.Equal(0u, BitConverter.ToUInt32(passenger, 26));
        Assert.Equal(1u, BitConverter.ToUInt32(passenger, 22));   // status is still success
    }

    // --- the seat-change pair -----------------------------------------------------------------

    [Fact]
    public void SeatChangeResponseAndSeatSwapRequestMatchTheirParserLengths()
    {
        byte[] response = Write(w => new SeatChangeResponse(Rider, Vehicle, Seat: 2, IsDriver: 0).WriteTo(w));
        Assert.Equal(SeatChangeResponse.Length, response.Length);
        Assert.Equal(66, response.Length);
        Assert.Equal(ZoneOpcodes.MountBase, response[0]);
        Assert.Equal(0x0b, response[1]);
        // rider, mount, then the 36-byte empty identity, then seat / isDriver / status.
        Assert.Equal(2u, BitConverter.ToUInt32(response, 2 + 8 + 8 + MountIdentityCodecProbe.EmptyLength));

        byte[] swap = Write(w => new SeatSwapRequest(Rider, Seat: 1).WriteTo(w));
        Assert.Equal(SeatSwapRequest.Length, swap.Length);
        Assert.Equal(50, swap.Length);
        Assert.Equal(0x0c, swap[1]);
        Assert.Equal(1u, BitConverter.ToUInt32(swap, 2 + 8 + MountIdentityCodecProbe.EmptyLength));
    }

    /// <summary>
    /// The live-proven <c>70 02 MountResponse</c> is reused unchanged for a car — only its seat and
    /// isDriver arguments change — so its 74-byte length is pinned here alongside the new pair it
    /// shares an identity sub-record with.
    /// </summary>
    [Fact]
    public void MountResponseIsSeventyFourBytesForAnySeat()
    {
        byte[] driver = Write(w => new MountResponse(Rider, Vehicle, Seat: 0, IsDriver: 1).WriteTo(w));
        byte[] passenger = Write(w => new MountResponse(Rider, Vehicle, Seat: 4, IsDriver: 0).WriteTo(w));

        Assert.Equal(74, driver.Length);
        Assert.Equal(74, passenger.Length);
        Assert.Equal(4u, BitConverter.ToUInt32(passenger, 18));
        Assert.Equal(0u, BitConverter.ToUInt32(passenger, 26));
    }

    // --- 0x78, the bystander relay ------------------------------------------------------------

    [Fact]
    public void ManagedLocationMatchesAugustDirectControllerTransformLayout()
    {
        // Native 140a36d90: GUID, float4 position, float4 quaternion, bool apply, u8 version.
        byte[] bytes = Write(new VehicleManagedLocation(0x4600000000000025,
            new Vector4(1467.35f, 51.29f, -2236.71f, 1), Quaternion.Identity, 7).WriteTo);
        Assert.Equal(45, bytes.Length);
        Assert.Equal(Convert.FromHexString("1123002500000000000046"), bytes[..11]);
        Assert.Equal(1467.35f, BitConverter.ToSingle(bytes, 11));
        Assert.Equal(51.29f, BitConverter.ToSingle(bytes, 15));
        Assert.Equal(-2236.71f, BitConverter.ToSingle(bytes, 19));
        Assert.Equal(1f, BitConverter.ToSingle(bytes, 23));
        Assert.Equal(0f, BitConverter.ToSingle(bytes, 27));
        Assert.Equal(1f, BitConverter.ToSingle(bytes, 39));
        Assert.Equal(new byte[] { 1, 7 }, bytes[43..]);
    }

    [Fact]
    public void ParkedBaselineMatchesTheNativeFullOffroaderRecord()
    {
        // wire-20260906-180440.txt, 18:08:21.606: the actual first offroader pose after grant.
        // A partial mask cannot seed the queue emptied by 140c18990 -> 140c110b0; 140b14d30
        // only bypasses its missing-baseline refusal for exactly 0x1fff, not merely position bits.
        byte[] captured = Convert.FromHexString(
            "90DE127AFF1F36A29C010025014DEA23BAE8348120A70A863F00000000000000002203220322032203000000000000000000");
        var native = ClientManagedMovementUpdate.Parse(captured);
        var car = new MatchVehicle(Vehicle, native.TransientId, VehicleRoster.LoadDefault().Require(1),
            native.Movement.Position!.Value, native.Movement.Orientation!.Value, 100000, 5000)
        {
            LastClientTime = native.Movement.ClientTime - 1,
        };
        captured[0] = VehiclePoseRelay.Opcode;
        Assert.Equal(captured, Write(VehiclePoseRelay.Parked(car).WriteTo));
        Assert.Equal(MovementFieldMask.All, native.Movement.Fields);
        Assert.Equal(0x49u, native.Movement.Posture);
        Assert.Equal(new Quaternion(1, 1, 1, 1), native.Movement.Rotation);
        Assert.Equal(Vector3.Zero, native.Movement.PrecisePose!.Value.Position);
    }

    /// <summary>
    /// The headline claim of docs/43 §3.3: <c>0x78 PlayerUpdatePosition</c> (s2c) and
    /// <c>0x90 PlayerUpdateManagedPosition</c> (c2s) are byte-identical in shape, so relaying an
    /// owner's vehicle pose is <b>a verbatim copy with one byte changed</b>. Proved here against the
    /// real captured parachute pose from
    /// <c>captures/wire-20260829-085701.txt</c>: relaying it must differ from the original in
    /// exactly one position, index 0.
    /// </summary>
    [Fact]
    public void RelayingAnOwnerPoseChangesExactlyOneByteOfTheCapturedRecord()
    {
        const string capture =
            "9008FF1F2F2F1F00002511BDDA021C7E189DB73B0000000000000000000000002203220322032203000000000000000000";
        byte[] managedBytes = Convert.FromHexString(capture);
        ClientManagedMovementUpdate managed = ClientManagedMovementUpdate.Parse(managedBytes);

        VehiclePoseRelay relay = VehiclePoseRelay.From(managed);
        byte[] relayed = Write(w => relay.WriteTo(w));

        Assert.Equal(managedBytes.Length, relayed.Length);
        Assert.Equal(relay.Length, relayed.Length);
        Assert.Equal(ZoneOpcodes.PlayerUpdatePosition, relayed[0]);
        Assert.Equal(0x78, relayed[0]);
        Assert.Equal(ZoneOpcodes.PlayerUpdateManagedPosition, managedBytes[0]);
        Assert.Equal(managedBytes[1..], relayed[1..]);
    }

    /// <summary>A multi-byte transient id still encodes as the client's own varint.</summary>
    [Fact]
    public void RelayPreservesAMultiByteTransientId()
    {
        ClientManagedMovementUpdate managed = ClientManagedMovementUpdate.Parse(
            Convert.FromHexString("900540" + "020018F6B21C00000000"));

        byte[] relayed = Write(w => VehiclePoseRelay.From(managed).WriteTo(w));

        Assert.Equal(0x78, relayed[0]);
        Assert.Equal(0x05, relayed[1]);
        Assert.Equal(0x40, relayed[2]);
        Assert.Equal(4_097u, managed.TransientId);
    }
}

/// <summary>Mirrors the internal identity length so the test can index past it without guessing.</summary>
internal static class MountIdentityCodecProbe
{
    public const int EmptyLength = 36;
}
