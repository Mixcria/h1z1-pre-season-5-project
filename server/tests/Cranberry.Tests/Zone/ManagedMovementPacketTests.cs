using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class ManagedMovementPacketTests
{
    // C:\Aug2017\captures\wire-20260829-085701.txt at 08:57:58.939, excluding
    // gateway byte 0x66. This is the first live parachute update after the mount burst.
    private const string FullParachuteCapture =
        "9008FF1F2F2F1F00002511BDDA021C7E189DB73B0000000000000000000000002203220322032203000000000000000000";

    [Fact]
    public void ParsesLiveParachuteManagedMovement()
    {
        ClientManagedMovementUpdate update = ClientManagedMovementUpdate.Parse(
            Convert.FromHexString(FullParachuteCapture));

        Assert.Equal(2u, update.TransientId);
        Assert.Equal(MovementFieldMask.All, update.Movement.Fields);
        Assert.Equal(2_043_695u, update.Movement.ClientTime);
        Assert.Equal(1_097u, update.Movement.Posture);
        Assert.NotNull(update.Movement.Position);
        Assert.InRange(update.Movement.Position.Value.X, -233.831f, -233.829f);
        Assert.InRange(update.Movement.Position.Value.Y, 2_006.429f, 2_006.431f);
        Assert.InRange(update.Movement.Position.Value.Z, -4_892.031f, -4_892.029f);
        Assert.Equal(new Quaternion(1, 1, 1, 1), update.Movement.Rotation);
    }

    [Fact]
    public void ReEmitsCapturedPacketWithoutRequantizingMovement()
    {
        ClientManagedMovementUpdate update = ClientManagedMovementUpdate.Parse(
            Convert.FromHexString(FullParachuteCapture));
        using var writer = new PacketWriter();

        update.WriteTo(writer);

        Assert.Equal(FullParachuteCapture, Convert.ToHexString(writer.Written));
    }

    [Fact]
    public void ParsesMultiByteTransientId()
    {
        const string stationaryMovement = "020018F6B21C00000000";
        ClientManagedMovementUpdate update = ClientManagedMovementUpdate.Parse(
            Convert.FromHexString("900540" + stationaryMovement));

        Assert.Equal(4_097u, update.TransientId);
        Assert.Equal(Vector3.Zero, update.Movement.EffectivePosition);
    }

    [Fact]
    public void RejectsWrongOpcodeAndEveryTruncation()
    {
        Assert.Throws<PacketFormatException>(() =>
            ClientManagedMovementUpdate.Parse(Convert.FromHexString("910800000000000000")));

        byte[] payload = Convert.FromHexString(FullParachuteCapture);
        for (int length = 0; length < payload.Length; length++)
        {
            Assert.Throws<PacketFormatException>(() =>
                ClientManagedMovementUpdate.Parse(payload.AsSpan(0, length)));
        }
    }
}
