using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone;

// Frozen vectors of the parachute packets (docs/12-parachute-drop.md).
public sealed class VehiclePacketTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    [Theory]
    [InlineData(321121, "0C3327")]   // 1087 capture: x·100
    [InlineData(-3206, "3364")]      // y·100 (negative)
    [InlineData(147509, "AC0112")]   // z·100
    [InlineData(157, "EA04")]        // vertical speed ·100
    [InlineData(2, "10")]            // horizontal speed ·10
    [InlineData(0, "00")]
    [InlineData(31, "F8")]
    [InlineData(32, "0201")]
    public void PackedIntegersMatchThe1087CaptureBytes(int value, string hex)
    {
        Assert.Equal(Convert.FromHexString(hex), Bytes(w => ClientPackedInt.Write(w, value)));
        Assert.Equal(hex.Length / 2, ClientPackedInt.Length(value));
    }

    [Fact]
    public void RetailPositionBlockReproducesThe1087Bytes()
    {
        PositionUpdateBlock block = PositionUpdateBlock.Retail(new Vector3(3211.21f, -32.06f, 1475.09f));
        byte[] bytes = Bytes(block.WriteTo);
        Assert.Equal(Convert.FromHexString("DA00" + "00000000" + "00" + "0C3327" + "3364" + "AC0112" + "00" + "00" + "EA04" + "10"), bytes);
        Assert.Equal(bytes.Length, block.Length);
        Assert.Equal(Convert.FromHexString("0000" + "00000000" + "00"), Bytes(PositionUpdateBlock.Empty.WriteTo));
    }

    [Theory]
    [InlineData(-0.2618f)]
    [InlineData(2.355217f)]
    [InlineData(0f)]
    public void ParkedVehicleHeadingSurvivesTheClientsSecondTransform(float yaw)
    {
        var position = new Vector3(3006.8599f, 88.7106f, -547.6171f);
        PositionUpdateBlock block = PositionUpdateBlock.AtRest(position, yaw);
        byte[] bytes = Bytes(block.WriteTo);
        byte[] positionBytes = Bytes(w =>
        {
            ClientPackedInt.Write(w, 300686);
            ClientPackedInt.Write(w, 8871);
            ClientPackedInt.Write(w, -54762);
        });

        Assert.Equal((ushort)0xfa, BitConverter.ToUInt16(bytes));
        Assert.Equal(positionBytes, bytes[7..(7 + positionBytes.Length)]);
        int orientationOffset = 7 + positionBytes.Length;
        Assert.Equal(yaw, BitConverter.ToSingle(bytes, orientationOffset));
        Assert.Equal(new byte[4], bytes[(orientationOffset + 4)..]); // zero tilts and speeds
        Assert.Equal(bytes.Length, block.Length);

        // The complete vehicle record must end after this larger block, with no truncation
        // into its trailing string. A unit quaternion in the body alone cannot preserve yaw.
        var car = new AddLightweightVehicle(0x4600000000000001, 2000219, 9301,
            position, new PlannedVehicle(3, 9301, position, yaw, 1, -1).Rotation, 3, 0,
            PositionUpdate: block);
        byte[] spawn = Bytes(car.WriteTo);
        Assert.Equal(car.Length, spawn.Length);
        Assert.Equal(bytes, spawn[^(block.Length + 4)..^4]);
    }

    [Fact]
    public void CapturedSlopedATVRetainsItsBankInTheNativeSecondTransform()
    {
        Quaternion captured = new(2.7221884746353453e-09f, 8.73803713830057e-08f,
            -0.031138211488723755f, 0.9995151162147522f);
        PositionUpdateBlock block = PositionUpdateBlock.AtRest(Vector3.Zero, 0, captured);
        byte[] bytes = Bytes(block.WriteTo);
        Assert.Equal((ushort)0xfa, BitConverter.ToUInt16(bytes));
        Assert.InRange(BitConverter.ToSingle(bytes, 10), -0.000001f, 0.000001f);
        // Zero pitch, -0.06 rad bank, zero speeds; sign/magnitude packed by August's reader.
        Assert.Equal(Convert.FromHexString("00310000"), bytes[14..]);
        Assert.Equal(bytes.Length, block.Length);
        Assert.Equal(Bytes(PositionUpdateBlock.AtRest(Vector3.Zero, 1.5f).WriteTo),
            Bytes(PositionUpdateBlock.AtRest(Vector3.Zero, 1.5f, null).WriteTo));
    }

    [Fact]
    public void PositionOrientationCannotProduceAnInconsistentWireFlag()
    {
        Assert.Throws<InvalidOperationException>(() => Bytes(new PositionUpdateBlock(0x20, []).WriteTo));
        Assert.Throws<InvalidOperationException>(() => Bytes(new PositionUpdateBlock(0, [], Orientation: 1f).WriteTo));
        Assert.Throws<InvalidOperationException>(() => Bytes(PositionUpdateBlock.AtRest(Vector3.Zero, float.NaN).WriteTo));
    }

    [Fact]
    public void AddLightweightVehicleMinimalIs227Bytes()
    {
        var chute = new AddLightweightVehicle(
            Guid: 0x2001, TransientId: 2, ModelId: 9374, Position: new Vector3(1, 2, 3), Rotation: new Vector4(0, 0, 0, 1),
            VehicleId: 13, OwnerGuid: 0x1001);
        byte[] bytes = Bytes(chute.WriteTo);
        string expected =
            "D7" + "0120000000000000" + "08" + "00000000" + "00000000" + "00" + "9E240000"
            + "0000803F0000803F0000803F0000803F" + "00000000" + "00000000" + "00000000"
            + "0000803F" + "00000040" + "00004040" + "000000000000000000000000" + "0000803F"
            + "0000803F0000803F0000803F0000803F" + "00000000" + "00000000"
            + "00000000" + "00000000" + "00000000" + "0D000000" + "00000000" + "00000000" + "01" + "00000000"
            + "000000" + "00" + "00000000" + "0000000000000000"
            + "0000000000000000"
            + "00000000" + "00000000" + "0000000000000000" + "00000000" + "00000000" + "00000000" + "00000000" + "00000000"
            + "0000000000000000"
            + "0110000000000000" + "00000000" + "00000000" + "0000" + "00000000" + "00" + "00000000";
        Assert.Equal(AddLightweightVehicle.MinimalLength, bytes.Length);
        Assert.Equal(chute.Length, bytes.Length);
        Assert.Equal(Convert.FromHexString(expected), bytes);
    }

    [Fact]
    public void AddLightweightVehicleWithTheRetailBlockIs241BytesAtTheAirSpawn()
    {
        var air = new Vector3(-233.83f, 2006.36f, -4892.03f);
        var chute = new AddLightweightVehicle(0x2001, 2, 9374, air, new Vector4(0, 0, 0, 1), 13, 0x1001,
            PositionUpdate: PositionUpdateBlock.Retail(air));
        byte[] bytes = Bytes(chute.WriteTo);
        Assert.Equal(241, bytes.Length);
        Assert.Equal(chute.Length, bytes.Length);
        Assert.Equal(Convert.FromHexString("DA00"), bytes[216..218]);
    }

    /// <summary>
    /// docs/117 §B: a parked car ships the three deltas that make it draw solid and tinted — an
    /// at-rest position block in the <c>0xd7</c> tail (step 1), the <c>+0x1b1</c> collidable bit
    /// (step 2) and the OffRoader's <c>+0x1a4</c> shader group 838 (step 3). None of the three moves
    /// the body length: the flag byte and the shader dword replace bytes the body already wrote as
    /// zero, and only the tail block grows the record.
    /// </summary>
    [Fact]
    public void AParkedOffRoaderCarriesTheAtRestBlockTheCollidableFlagAndShaderGroup()
    {
        var pos = new Vector3(1049.90f, 10.49f, 578.22f);
        var rot = new Vector4(0, 0, 0, 1);
        const uint transient = 2000219;
        var car = new AddLightweightVehicle(
            Guid: 0x4600000000000001, TransientId: transient, ModelId: 9258, Position: pos, Rotation: rot,
            VehicleId: 1, OwnerGuid: 0,
            PositionUpdate: PositionUpdateBlock.AtRest(pos),
            SpawnFlags1: LightweightEntityBody.CollidableFlag,
            BodyShaderGroupId: VehicleShaderGroups.For(1));
        byte[] bytes = Bytes(car.WriteTo);

        // Length = the empty-block minimal, minus the empty block, plus the at-rest block, plus the
        // transient's varint growth. The record declares itself.
        Assert.Equal(car.Length, bytes.Length);
        Assert.Equal(
            AddLightweightVehicle.MinimalLength
                - PositionUpdateBlock.Empty.Length
                + PositionUpdateBlock.AtRest(pos).Length
                + ClientVarInt.Length(transient) - 1,
            bytes.Length);

        // Step 2: the +0x1b1 collidable bit, at the offset the body computes for a 3-byte transient.
        int flagOffset = car.Body.SpawnFlags1Offset;
        Assert.Equal(135, flagOffset);
        Assert.Equal(LightweightEntityBody.CollidableFlag, bytes[flagOffset]);

        // Step 3: 838 little-endian (46 03 00 00) appears exactly once — the body's +0x1a4 dword.
        Assert.Equal(838u, VehicleShaderGroups.For(1));
        Assert.Equal(1, CountSubsequence(bytes, [0x46, 0x03, 0x00, 0x00]));

        // Step 1: the tail block is the at-rest block (flags 0x00da), sitting just before the
        // record's trailing empty string (an i32 zero = 4 bytes).
        byte[] atRest = Bytes(PositionUpdateBlock.AtRest(pos).WriteTo);
        int blockStart = bytes.Length - 4 - atRest.Length;
        Assert.Equal(atRest, bytes[blockStart..(blockStart + atRest.Length)]);
        Assert.Equal(Convert.FromHexString("DA00"), bytes[blockStart..(blockStart + 2)]);

        // The OffRoader is the only tinted model: the other three ship 0 at +0x1a4.
        Assert.Equal(0u, VehicleShaderGroups.For(2));
        Assert.Equal(0u, VehicleShaderGroups.For(3));
        Assert.Equal(229u, VehicleShaderGroups.For(5));
    }

    private static int CountSubsequence(byte[] haystack, byte[] needle)
    {
        int count = 0;
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void LightweightToFullVehicleIs271ZeroBytesExceptHeaderAndGuid()
    {
        byte[] bytes = Bytes(w => new LightweightToFullVehicle(2, 0x2001).WriteTo(w));
        Assert.Equal(LightweightToFullVehicle.MinimalLength, bytes.Length);
        Assert.Equal(Convert.FromHexString("DB08"), bytes[..2]);
        Assert.Equal(Convert.FromHexString("0120000000000000"), bytes[90..98]);
        Assert.All(Enumerable.Range(2, 88), i => Assert.Equal(0, bytes[i]));
        Assert.All(Enumerable.Range(98, 271 - 98), i => Assert.Equal(0, bytes[i]));
    }

    [Fact]
    public void OwnerOccupyAndDismountResponseMatchTheParsers()
    {
        const string emptyIdentity =
            "000000000000000000000000" +
            "00000000000000000000000000000000" +
            "0000000000000000";

        byte[] owner = Bytes(w => new VehicleOwner(0x2001, 0x1001, 13).WriteTo(w));
        Assert.Equal(VehicleOwner.OccupiedLength, owner.Length);
        Assert.Equal(
            Convert.FromHexString(
                "8801" + "0120000000000000" + "0110000000000000" + "00000000" + "0D000000" +
                "01000000" + "0110000000000000" + emptyIdentity + "00000000" + "01"),
            owner);

        byte[] occupy = Bytes(w => new VehicleOccupy(0x2001, 0x1001, 13).WriteTo(w));
        Assert.Equal(VehicleOccupy.OccupiedLength, occupy.Length);
        Assert.Equal(
            Convert.FromHexString(
                "8802" + "0120000000000000" + "0110000000000000" + "0D000000" + "00000000" +
                "01000000" + "00000000" + "01" +
                "01000000" + "0110000000000000" + emptyIdentity + "00000000" + "00" +
                "00000000" + "00000000" + "00000000"),
            occupy);
        Assert.Equal(
            Convert.FromHexString("7004" + "0110000000000000" + "0120000000000000" + "00000000" + "00" + "FF" + "00"),
            Bytes(w => new DismountResponse(0x1001, 0x2001).WriteTo(w)));

        byte[] clearedOwner = Bytes(w => new VehicleOwner(0x2001, 0, 0).WriteTo(w));
        Assert.Equal(VehicleOwner.ClearedLength, clearedOwner.Length);
        Assert.Equal(Convert.FromHexString(
            "8801" + "0120000000000000" + "0000000000000000" + "00000000" + "00000000" + "00000000"),
            clearedOwner);
    }

    [Fact]
    public void NegativeManagedControlReleaseMatchesTheAugustParser()
    {
        byte[] bytes = Bytes(w => new ManagedObjectResponseControl(false, 0x2001).WriteTo(w));

        Assert.Equal(ManagedObjectResponseControl.Length, bytes.Length);
        Assert.Equal(Convert.FromHexString("11390000" + "0120000000000000"), bytes);
    }

    [Fact]
    public void ClearedOccupyUsesNullVehicleAndOneUnoccupiedSeat()
    {
        byte[] bytes = Bytes(w => new VehicleOccupyCleared(0x1001).WriteTo(w));

        Assert.Equal(VehicleOccupyCleared.Length, bytes.Length);
        Assert.Equal(
            Convert.FromHexString(
                "8802" +
                "0000000000000000" +
                "0110000000000000" +
                "00000000" +
                "01000000" +
                "01000000" +
                "00000000" +
                "00" +
                "00000000" +
                "00000000" +
                "00000000" +
                "00000000"),
            bytes);
    }

    [Fact]
    public void RemovePlayerIsTheTwelveByteAugustForm()
    {
        Assert.Equal(
            Convert.FromHexString("0F01" + "0120000000000000" + "0000"),
            Bytes(w => new RemovePlayer(0x2001).WriteTo(w)));
    }

    [Fact]
    public void MountRequestParsesTheSixteenByteRequest()
    {
        MountRequest request = MountRequest.Parse(Convert.FromHexString("7001" + "0120000000000000" + "00000000" + "00" + "01"));
        Assert.Equal(0x2001u, request.VehicleGuid);
        Assert.Equal(0u, request.Seat);
        Assert.Equal(1, request.Flag);
        Assert.Throws<PacketFormatException>(() => MountRequest.Parse(Convert.FromHexString("7003" + "00")));
    }

    [Fact]
    public void AutoMountEchoAndDismountRequestAreStrictlyParsed()
    {
        VehicleAutoMount echo = VehicleAutoMount.Parse(
            Convert.FromHexString("8819" + "0120000000000000" + "00" + "00000000"));
        Assert.Equal(0x2001ul, echo.VehicleGuid);
        Assert.False(echo.Flag);
        Assert.Equal(0u, echo.Value);

        DismountRequest dismount = DismountRequest.Parse(Convert.FromHexString("700301"));
        Assert.Equal(1, dismount.Flag);

        Assert.Throws<PacketFormatException>(() => VehicleAutoMount.Parse(
            Convert.FromHexString("8819" + "0120000000000000" + "00")));
        Assert.Throws<PacketFormatException>(() => VehicleAutoMount.Parse(
            Convert.FromHexString("8819" + "0120000000000000" + "00" + "0000000000")));
        Assert.Throws<PacketFormatException>(() => DismountRequest.Parse(Convert.FromHexString("7003")));
        Assert.Throws<PacketFormatException>(() => DismountRequest.Parse(Convert.FromHexString("70030100")));
    }

    [Fact]
    public void VehicleDismissMatchesTheAugustLandingSignalAndIsStrictlyParsed()
    {
        VehicleDismiss dismiss = VehicleDismiss.Parse(
            Convert.FromHexString("88180000000000000000"));

        Assert.Equal(0ul, dismiss.VehicleGuid);
        Assert.Throws<PacketFormatException>(() => VehicleDismiss.Parse(
            Convert.FromHexString("881800000000000000")));
        Assert.Throws<PacketFormatException>(() => VehicleDismiss.Parse(
            Convert.FromHexString("8818000000000000000000")));
    }
}
