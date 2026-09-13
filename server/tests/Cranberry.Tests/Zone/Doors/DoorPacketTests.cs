using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Doors;

/// <summary>
/// Byte-level tests for the door wire (docs/42 §2, §3, §5). Nothing here is a round-trip against
/// Cranberry's own reader: every expected byte is written out by hand from the derivation, so a
/// change to the writer that also changed the reader would still fail.
/// </summary>
public sealed class DoorPacketTests
{
    private static byte[] Write(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    // ---------------------------------------------------------------- 0xd6, the door spawn

    /// <summary>
    /// docs/42 §2b: the door id is the u32 at client object <c>+0x19c</c>, wire offset <b>176</b>
    /// with a one-byte transient varint, in a 200-byte body — and it moves with the varint, because
    /// it is fixed relative to the <i>end</i> of the record (20 trailing bytes: three u32 then a u64).
    /// </summary>
    [Theory]
    [InlineData(7u, 200, 176)]              // one-byte varint — docs/42's own worked example
    [InlineData(1_000u, 201, 177)]          // LootWorld's transient base
    [InlineData(1_000_000u, 202, 178)]      // MatchDoors' transient base
    public void TheDoorIdSitsAtItsDerivedOffsetForEveryVarintWidth(
        uint transientId, int expectedLength, int expectedOffset)
    {
        var spawn = new AddLightweightDoor(
            Guid: 0x4400_0000_0000_0001,
            TransientId: transientId,
            ModelId: 9904,
            Position: new Vector3(1f, 2f, 3f),
            Yaw: 0.5f,
            DoorTableId: 2);

        Assert.Equal(expectedLength, spawn.Length);
        Assert.Equal(expectedOffset, spawn.DoorIdOffset);

        byte[] bytes = Write(spawn.WriteTo);
        Assert.Equal(expectedLength, bytes.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(expectedOffset)));
    }

    /// <summary>
    /// 176 is not a magic number: it is <c>200 − 20 − 4</c>, and the same arithmetic docs/42 §2b does
    /// by walking the client's reader. Asserting the identity means a future edit to
    /// <see cref="LightweightEntityBody"/> that changes the record length cannot leave a stale
    /// constant behind.
    /// </summary>
    [Fact]
    public void TheMinimalDoorIdOffsetIsDerivedFromTheRecordLength()
    {
        Assert.Equal(176, AddLightweightDoor.MinimalDoorIdOffset);
        Assert.Equal(
            LightweightEntityBody.MinimalLength - AddLightweightDoor.TrailingBytesAfterDoorId - sizeof(uint),
            AddLightweightDoor.MinimalDoorIdOffset);
    }

    /// <summary>
    /// The strongest statement this lane can make about the spawn: <b>a door is a ground-loot spawn
    /// with exactly two bytes changed</b> — the <c>Doors.txt</c> row at <c>+0x19c</c> and, since
    /// docs/79 §4 E5, the physics flag at <c>+0x1b1</c>. Byte-comparing the two records proves both
    /// patches land where they are meant to and touch nothing else — no shifted string, no clobbered
    /// rotation, and no change to the record LENGTH (the flag byte was always written, as a zero).
    /// <para>
    /// The flag is deliberately on the door and NOT on the item: a ground item with a physics body is
    /// an obstacle you cannot step over (docs/68 §5 F1 open question 4), so the loot side of this
    /// comparison staying <c>0x00</c> is itself the assertion.
    /// </para>
    /// </summary>
    [Fact]
    public void ADoorSpawnIsALootSpawnWithOnlyTheDoorIdAndPhysicsFlagChanged()
    {
        const ulong guid = 0x4400_0000_0000_00FFUL;
        const uint transient = 1_000_000;
        var position = new Vector3(-526.92f, 242.02f, -3762.55f);

        // The pose the door writer now puts on the wire, spelled out rather than borrowed: docs/47
        // §3 proved +0xa0 is a quaternion, so the loot record has to be given the same quaternion
        // for the comparison to isolate the door-id field.
        const float yaw = 1.5708f;
        var rotation = new Vector4(0f, MathF.Sin(yaw / 2f), 0f, MathF.Cos(yaw / 2f));

        byte[] loot = Write(new AddLightweightItem(guid, transient, 9904, position, 0, rotation).WriteTo);
        byte[] door = Write(new AddLightweightDoor(
            guid, transient, 9904, position, Yaw: yaw, DoorTableId: 2).WriteTo);

        Assert.Equal(loot.Length, door.Length);

        var differing = new List<int>();
        for (int i = 0; i < loot.Length; i++)
        {
            if (loot[i] != door[i])
            {
                differing.Add(i);
            }
        }

        // 1,000,000 takes a three-byte client varint, two wider than the minimal one-byte case, so
        // both derived offsets shift by the same two bytes.
        int doorIdAt = AddLightweightDoor.MinimalDoorIdOffset + 2;
        int flagsAt = LightweightEntityBody.MinimalLength
            - LightweightEntityBody.TrailingBytesAfterSpawnFlags1 - sizeof(byte) + 2;

        int renderAt = doorIdAt + sizeof(uint);
        Assert.Equal(650f, BitConverter.ToSingle(door, renderAt));
        differing.RemoveAll(i => i >= renderAt && i < renderAt + sizeof(float));
        Assert.Equal([flagsAt, doorIdAt], differing);           // the flag byte comes first on the wire
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(loot.AsSpan(doorIdAt)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(door.AsSpan(doorIdAt)));
        Assert.Equal(0x00, loot[flagsAt]);
        // docs/85 §2a: bit 4 (0x10) is the collision switch. docs/114 §5 dropped the adjacent
        // 0x20 that used to ride with it — falsified as a lever by docs/85 §2c — so the shipped
        // byte is now the friend server's own 0x10. Ground loot keeps a zero: an item that stops a
        // player is an obstacle you cannot step over.
        Assert.Equal(LightweightEntityBody.DoorSpawnFlagsRetail, door[flagsAt]);
        Assert.Equal(0x10, LightweightEntityBody.DoorSpawnFlagsRetail);
    }

    /// <summary>
    /// <c>+0x19c == 0</c> is what the client reads as "not a door" — the entity would spawn as inert
    /// scenery with no controller and no swing, and nothing anywhere would report an error. The
    /// writer refuses instead.
    /// </summary>
    [Fact]
    public void ASpawnWithoutADoorsTxtRowIsRefused()
    {
        var spawn = new AddLightweightDoor(1, 2, 9904, Vector3.Zero, 0f, DoorTableId: 0);
        Assert.Throws<InvalidOperationException>(() => Write(spawn.WriteTo));
    }

    /// <summary>
    /// Every convention agrees on the identity, which is why the loot lane's <c>(0,0,0,1)</c> could
    /// never tell them apart — and why docs/47 §3 had to settle the question in the binary instead.
    /// </summary>
    [Theory]
    [InlineData(DoorRotation.ZoneEulerYawFirst)]
    [InlineData(DoorRotation.ZoneEulerYawSecond)]
    [InlineData(DoorRotation.QuaternionYUp)]
    [InlineData(DoorRotation.QuaternionYUpNegated)]
    [InlineData(DoorRotation.Identity)]
    public void EveryRotationConventionAgreesOnAZeroYaw(DoorRotation convention)
    {
        Assert.Equal(new Vector4(0f, 0f, 0f, 1f), DoorRotationPacking.Pack(0f, convention));
    }

    [Fact]
    public void EachRotationConventionPacksTheYawWhereItsNameSays()
    {
        const float yaw = 1.5707964f;   // π/2, the commonest door yaw in Z2

        Assert.Equal(new Vector4(yaw, 0f, 0f, 1f), DoorRotationPacking.Pack(yaw, DoorRotation.ZoneEulerYawFirst));
        Assert.Equal(new Vector4(0f, yaw, 0f, 1f), DoorRotationPacking.Pack(yaw, DoorRotation.ZoneEulerYawSecond));
        Assert.Equal(new Vector4(0f, 0f, 0f, 1f), DoorRotationPacking.Pack(yaw, DoorRotation.Identity));

        Vector4 quaternion = DoorRotationPacking.Pack(yaw, DoorRotation.QuaternionYUp);
        Assert.Equal(0f, quaternion.X);
        Assert.Equal(MathF.Sin(yaw / 2f), quaternion.Y, 6);
        Assert.Equal(0f, quaternion.Z);
        Assert.Equal(MathF.Cos(yaw / 2f), quaternion.W, 6);

        // The sign sweep (docs/47 §7 q2): same axis, same w, opposite handedness.
        Vector4 negated = DoorRotationPacking.Pack(yaw, DoorRotation.QuaternionYUpNegated);
        Assert.Equal(-quaternion.Y, negated.Y, 6);
        Assert.Equal(quaternion.W, negated.W, 6);
    }

    // ---------------------------------------------------------------- 0f 0a, the swing

    /// <summary>
    /// docs/42 §5c: <c>u8 0x0f; u8 0x0a; u64 doorGuid; u64 state; u32 time</c> = 22 bytes, with
    /// <b>bit 48</b> as the open bit. Written out byte by byte rather than compared to a helper.
    /// </summary>
    [Fact]
    public void UpdateCharacterStateIsTwentyTwoBytesWithBitFortyEightAsTheOpenBit()
    {
        const ulong guid = 0x4400_0000_0000_002AUL;

        byte[] open = Write(new DoorStateUpdate(guid, IsOpen: true).WriteTo);
        Assert.Equal(DoorStateUpdate.Length, open.Length);
        Assert.Equal(22, open.Length);
        Assert.Equal(
            (byte[])
            [
                0x0f, 0x0a,
                0x2a, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x44,     // guid, little-endian
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00,     // 1 << 48
                0x00, 0x00, 0x00, 0x00,                             // time
            ],
            open);

        byte[] closed = Write(new DoorStateUpdate(guid, IsOpen: false).WriteTo);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(closed.AsSpan(10)));

        // Byte 6 of the state qword is the byte the client tests as entity+0x18de & 1.
        Assert.Equal(1, open[10 + 6] & 1);
        Assert.Equal(0, closed[10 + 6] & 1);
    }

    /// <summary>
    /// Bit 48 is one of the three the applier acts on immediately (mask
    /// <c>0x0201010000000000</c>), which is why no timed queue and no <c>time</c> value is involved.
    /// </summary>
    [Fact]
    public void TheOpenBitIsOneOfTheImmediatelyAppliedBits()
    {
        Assert.Equal(1UL << 48, DoorStateBits.Open);
        Assert.Equal(DoorStateBits.Open, DoorStateBits.Open & DoorStateBits.ImmediateMask);
        Assert.Equal(DoorStateBits.Open, DoorStateBits.For(true));
        Assert.Equal(0UL, DoorStateBits.For(false));
    }

    /// <summary>
    /// docs/42 §5c: the 38-byte delta form, kept as the fallback for the day a door needs a second
    /// state bit — <c>0f 0a</c> implicitly clears bits 56 and 57 and this one does not.
    /// </summary>
    [Fact]
    public void UpdateCharacterStateDeltaIsThirtyEightBytesAndMovesOnlyBitFortyEight()
    {
        const ulong guid = 0x4400_0000_0000_002AUL;

        byte[] open = Write(new DoorStateDelta(guid, IsOpen: true).WriteTo);
        Assert.Equal(DoorStateDelta.Length, open.Length);
        Assert.Equal(38, open.Length);
        Assert.Equal(0x0f, open[0]);
        Assert.Equal(0x3f, open[1]);
        Assert.Equal(guid, BinaryPrimitives.ReadUInt64LittleEndian(open.AsSpan(2)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(open.AsSpan(10)));            // source sentinel
        Assert.Equal(DoorStateBits.Open, BinaryPrimitives.ReadUInt64LittleEndian(open.AsSpan(18)));  // gained
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(open.AsSpan(26)));            // lost

        byte[] closed = Write(new DoorStateDelta(guid, IsOpen: false).WriteTo);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(closed.AsSpan(18)));
        Assert.Equal(DoorStateBits.Open, BinaryPrimitives.ReadUInt64LittleEndian(closed.AsSpan(26)));
    }

    // ---------------------------------------------------------------- c2s, what [F] actually sends

    /// <summary>
    /// The exact 28 bytes the August client sent for a live <c>[F]</c> press
    /// (<c>logs/host-20260829-201025.log:300</c>), decoded against the client's own writer
    /// <c>FUN_140b78180</c>: <c>u64 targetGuid; f32 x,y,z,w; u8 flag</c>. Cranberry's older
    /// <c>LootPackets.InteractRequest</c> reads the guid and reports "17 trailing"; these 17 bytes
    /// are exactly that tail, which closes docs/19 open question 2.
    /// </summary>
    [Fact]
    public void InteractRequestParsesTheLiveTwentyEightByteForm()
    {
        byte[] live = Convert.FromHexString(
            "0907000800000000000020D45B4CC23E370642FD7E80430000803F01");
        Assert.Equal(DoorToggleRequest.InteractRequestLength, live.Length);

        Assert.True(DoorToggleRequest.Matches(live));
        DoorToggleRequest request = DoorToggleRequest.Parse(live);

        Assert.Equal(DoorRequestKind.InteractRequest, request.Kind);
        Assert.Equal(0x2000_0000_0000_0008UL, request.TargetGuid);
        Assert.True(request.HasPoint);
        Assert.Equal(-51.089676f, request.Point.X, 4);
        Assert.Equal(33.553947f, request.Point.Y, 4);
        Assert.Equal(256.99210f, request.Point.Z, 4);
        Assert.Equal(1.0f, request.Point.W, 4);
        Assert.Equal((byte)1, request.Flag);
        Assert.Equal(
            new Vector3(-51.089676f, 33.553947f, 256.99210f),
            request.Position!.Value,
            Vector3EqualTo(0.001f));
    }

    /// <summary>
    /// The other half of the same press, 2 ms earlier
    /// (<c>logs/host-20260829-201025.log:297</c>): <c>u64 selectingCharacterGuid; u64 targetGuid</c>.
    /// The order matters — a swapped pair would look up the player's own guid as a door.
    /// </summary>
    [Fact]
    public void PlayerSelectParsesTheLiveNineteenByteForm()
    {
        byte[] live = Convert.FromHexString("09150003100000000000000800000000000020");
        Assert.Equal(DoorToggleRequest.PlayerSelectLength, live.Length);

        DoorToggleRequest request = DoorToggleRequest.Parse(live);

        Assert.Equal(DoorRequestKind.PlayerSelect, request.Kind);
        Assert.Equal(0x1003UL, request.SelectingCharacterGuid);
        Assert.Equal(0x2000_0000_0000_0008UL, request.TargetGuid);
        Assert.False(request.HasPoint);
        Assert.Null(request.Position);
    }

    /// <summary>
    /// A <c>09 07</c> that carries only the guid — the shortest form Cranberry's older parser
    /// accepted — is still read, and reports its tail as absent rather than inventing a position.
    /// </summary>
    [Fact]
    public void AnInteractRequestWithoutItsTailIsReadWithoutAPosition()
    {
        byte[] truncated = Convert.FromHexString("0907000800000000000020");
        Assert.Equal(DoorToggleRequest.InteractRequestMinimumLength, truncated.Length);

        DoorToggleRequest request = DoorToggleRequest.Parse(truncated);
        Assert.Equal(0x2000_0000_0000_0008UL, request.TargetGuid);
        Assert.False(request.HasPoint);
    }

    /// <summary>
    /// <c>0f 52 RequestToggleDoorState</c> is dead in 1148 and any other Command sub is somebody
    /// else's packet, so the reader must not claim them. <c>09 08 InteractCancel</c> in particular
    /// is a live regression signal the loot lane depends on.
    /// </summary>
    [Theory]
    [InlineData("0f5200")]          // the retired RequestToggleDoorState
    [InlineData("090800")]          // Command.InteractCancel
    [InlineData("091600")]          // Command.FreeInteractionNpc
    public void OtherPacketsAreNotMistakenForADoorRequest(string hex)
    {
        byte[] payload = Convert.FromHexString(hex);
        Assert.False(DoorToggleRequest.Matches(payload));
        Assert.False(DoorToggleRequest.TryParse(payload, out DoorToggleRequest? request));
        Assert.Null(request);
        Assert.Throws<PacketFormatException>(() => DoorToggleRequest.Parse(payload));
    }

    private static IEqualityComparer<Vector3> Vector3EqualTo(float tolerance) =>
        new Vector3Tolerance(tolerance);

    private sealed class Vector3Tolerance(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 x, Vector3 y) => (x - y).Length() <= tolerance;

        public int GetHashCode(Vector3 obj) => 0;
    }
}
