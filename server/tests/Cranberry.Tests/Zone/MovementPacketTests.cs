using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class MovementPacketTests
{
    // C:\Aug2017\captures\wire-20260828-212913.txt line 107, without gateway byte 0x46.
    private const string StationaryCapture = "020018F6B21C00000000";

    // The same capture, line 79: every field selected, including the seven-value precise pose.
    private const string FullCapture =
        "FF1F99F5B21C00850100000000000000000000000000000000000000000000000000000000";

    // The same capture, line 89: non-zero signed and multi-byte values prove field order/scales.
    private const string FallingCapture =
        "FF01C5F5B21C0084009B180000000000000000EB04EA04000000";

    [Fact]
    public void ParsesStationaryAugustCapture()
    {
        byte[] payload = Convert.FromHexString(StationaryCapture);

        ClientMovementUpdate update = ClientMovementUpdate.Parse(payload);

        Assert.Equal(MovementFieldMask.Position, update.Fields);
        Assert.Equal(481_490_456u, update.ClientTime);
        Assert.Equal(0, update.State);
        Assert.Equal(Vector3.Zero, update.Position);
        Assert.Equal(Vector3.Zero, update.EffectivePosition);
        Assert.Null(update.Posture);
        Assert.Equal(payload, update.Payload.ToArray());
    }

    [Fact]
    public void ParsesAllFieldsFromAugustCaptureToTheExactEnd()
    {
        ClientMovementUpdate update = ClientMovementUpdate.Parse(
            Convert.FromHexString(FullCapture));

        Assert.Equal(MovementFieldMask.All, update.Fields);
        Assert.Equal(481_490_329u, update.ClientTime);
        Assert.Equal(97u, update.Posture);
        Assert.Equal(Vector3.Zero, update.Position);
        Assert.Equal(0f, update.Orientation);
        Assert.Equal(Vector3.Zero, update.AuxiliaryVector);
        Assert.Equal(Quaternion.Zero, update.Rotation);
        Assert.Equal(Vector3.Zero, update.PrecisePose?.Position);
        Assert.Equal(Quaternion.Zero, update.PrecisePose?.Rotation);
    }

    [Fact]
    public void DecodesSignedValuesAndTheirDifferentScales()
    {
        ClientMovementUpdate update = ClientMovementUpdate.Parse(
            Convert.FromHexString(FallingCapture));

        Assert.Equal((MovementFieldMask)0x01ff, update.Fields);
        Assert.Equal(33u, update.Posture);
        Assert.NotNull(update.Position);
        Assert.InRange(update.Position.Value.Y, -7.871f, -7.869f);
        Assert.InRange(update.VerticalSpeed!.Value, -1.571f, -1.569f);
        Assert.InRange(update.HorizontalSpeed!.Value, 15.699f, 15.701f);
    }

    [Fact]
    public void DecodesSevenValuePrecisePoseAtTwoDecimalPlaces()
    {
        // flags 0x1000; position (1.25, -2.5, 3.75); rotation (0, 0, 0, 1).
        byte[] payload = Convert.FromHexString(
            "00100000000000EA03D307BA0B0000002203");

        ClientMovementUpdate update = ClientMovementUpdate.Parse(payload);

        Assert.Equal(new Vector3(1.25f, -2.5f, 3.75f), update.PrecisePose?.Position);
        Assert.Equal(Quaternion.Identity, update.PrecisePose?.Rotation);
        Assert.Equal(update.PrecisePose?.Position, update.EffectivePosition);
    }

    [Fact]
    public void FourByteSignedValuesUseALogicalShift()
    {
        // The maximum 29-bit magnitude encodes as FE FF FF FF; its negative form is FF FF FF FF.
        byte[] payload = Convert.FromHexString(
            "02000000000000FEFFFFFFFFFFFFFF00");

        ClientMovementUpdate update = ClientMovementUpdate.Parse(payload);

        Assert.Equal(0x1fff_ffff / 100f, update.Position?.X);
        Assert.Equal(-0x1fff_ffff / 100f, update.Position?.Y);
        Assert.Equal(0f, update.Position?.Z);
    }

    [Fact]
    public void RejectsEveryTruncationOfAFullRecord()
    {
        byte[] payload = Convert.FromHexString(FullCapture);

        for (int length = 0; length < payload.Length; length++)
        {
            Assert.Throws<PacketFormatException>(() =>
                ClientMovementUpdate.Parse(payload.AsSpan(0, length)));
        }
    }

    [Fact]
    public void RejectsUnknownFlagsAndTrailingBytes()
    {
        Assert.Throws<PacketFormatException>(() =>
            ClientMovementUpdate.Parse(Convert.FromHexString("00200000000000")));

        Assert.Throws<PacketFormatException>(() =>
            ClientMovementUpdate.Parse(Convert.FromHexString(StationaryCapture + "00")));
    }

    [Fact]
    public void EntityRelayPrependsTransientIdAndKeepsCapturedBytesExact()
    {
        ClientMovementUpdate update = ClientMovementUpdate.Parse(
            Convert.FromHexString(FallingCapture));
        using var writer = new PacketWriter();

        update.WriteForEntity(writer, transientId: 4097);

        Assert.Equal("0540" + FallingCapture, Convert.ToHexString(writer.Written));
    }
}
