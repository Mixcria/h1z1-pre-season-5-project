using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed class VehicleSeatMotionTests
{
    [Theory]
    [InlineData(0f, true)]
    [InlineData(0.1f, true)]
    [InlineData(1f, true)]
    [InlineData(1.1f, false)]
    [InlineData(20f, false)]
    [InlineData(-0.1f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    [InlineData(float.NegativeInfinity, false)]
    public void NativeMagnitudeUsesTheClientThresholdAndRejectsInvalidValues(float speed, bool allowed)
    {
        var motion = new VehicleSeatMotion();
        motion.Observe(1, speed, stopped: false);
        Assert.Equal(allowed, motion.AllowsSeatChange(fallbackSpeed: 0));
        if (allowed) Assert.True(motion.AllowsSeatChange(fallbackSpeed: 30));
    }

    [Fact]
    public void MissingInitialMagnitudeRetainsTheConservativeFiniteZeroFallback()
    {
        var motion = new VehicleSeatMotion();
        Assert.True(motion.AllowsSeatChange(0));
        foreach (float speed in new[] { 0.001f, 1f, -1f, float.NaN, float.PositiveInfinity })
            Assert.False(motion.AllowsSeatChange(speed));
    }

    [Fact]
    public void SparseUpdatesRetainSpeedAndVersionChangesRequireAFreshBaseline()
    {
        var motion = new VehicleSeatMotion();
        motion.Observe(1, 0.5f, stopped: false);
        motion.Observe(1, null, stopped: false);
        Assert.Equal(0.5f, motion.Speed);
        Assert.True(motion.AllowsSeatChange(30));
        motion.Observe(2, null, stopped: false);
        Assert.Null(motion.Speed);
        Assert.False(motion.AllowsSeatChange(30));
        motion.Observe(2, 0.2f, stopped: false);
        Assert.True(motion.AllowsSeatChange(30));
        motion.Reset();
        Assert.Null(motion.Speed);
        Assert.False(motion.AllowsSeatChange(30));
    }

    [Fact]
    public void ExplicitSparseStopRecoversMotionButCannotHideAFreshInvalidMagnitude()
    {
        var motion = new VehicleSeatMotion();
        motion.Observe(1, 15, stopped: false);
        motion.Observe(1, null, stopped: true);
        Assert.Equal(0f, motion.Speed);
        Assert.True(motion.AllowsSeatChange(30));
        motion.Observe(1, -1, stopped: true);
        motion.Observe(1, null, stopped: false);
        Assert.True(motion.InvalidSpeed);
        Assert.False(motion.AllowsSeatChange(0));
        motion.Observe(1, 0, stopped: false);
        Assert.False(motion.InvalidSpeed);
        Assert.True(motion.AllowsSeatChange(30));
        motion.Observe(1, 2, stopped: true);
        Assert.False(motion.AllowsSeatChange(0));
    }
}

public sealed partial class VehicleOccupantReplicationTests
{
    [Fact]
    public void NativeSeatSpeedAllowsSmallMotionDespiteArrivalTimePoseEstimate()
    {
        using var f = new Fixture();
        var driver = f.Add(); f.Stream(driver); f.Enter(driver, 0);
        SeatSpeedUpdate(f, driver, tenths: 5, at: f.Car.Position, time: 1);
        f.Car.LastPoseMs = Environment.TickCount64;
        SeatSpeedUpdate(f, driver, tenths: null, at: f.Car.Position + new Vector3(0.02f, 0, 0), time: 2);
        Assert.True(f.Car.LastSpeed > 0);
        Assert.Equal(0.5f, f.Car.SeatMotion.Speed);
        float poseEstimate = f.Car.LastSpeed;

        f.Clear(); f.Seat(driver, 2);

        Assert.Equal(2, f.Car.SeatOf(GuidOf(driver)));
        Assert.Equal(poseEstimate, f.Car.LastSpeed); // Dismount/pose policy is unchanged.
        Assert.Equal(0.5f, f.Car.SeatMotion.Speed); // Same simulator now coasts.
        Assert.Single(f.Sent(driver), p => Is(p, 0x70, 0x0b));
        f.Seat(driver, 0);
        Assert.Equal(GuidOf(driver), f.Car.OwnerGuid);
        Assert.Equal(0.5f, f.Car.SeatMotion.Speed); // Returning does not lose sparse state.
    }

    [Fact]
    public void NativeSeatSpeedRejectsMotionThenAcceptsAnExplicitSparseStop()
    {
        using var f = new Fixture();
        var driver = f.Add(); f.Stream(driver); f.Enter(driver, 0);
        SeatSpeedUpdate(f, driver, tenths: 11);
        f.Clear(); f.Seat(driver, 1);
        Assert.Equal(0, f.Car.SeatOf(GuidOf(driver)));
        Assert.DoesNotContain(f.Sent(driver), p => Is(p, 0x70, 0x0b));

        SeatSpeedUpdate(f, driver, tenths: null, time: 101, stopped: true);
        f.Seat(driver, 1);
        Assert.Equal(1, f.Car.SeatOf(GuidOf(driver)));
        Assert.Equal(0f, f.Car.SeatMotion.Speed);
    }

    [Fact]
    public void NativeSeatSpeedCannotComeFromAPassengerOrARejectedPose()
    {
        using var f = new Fixture();
        var driver = f.Add(); var passenger = f.Add();
        f.Stream(driver); f.Stream(passenger); f.Enter(driver, 0); f.Enter(passenger, 1);
        SeatSpeedUpdate(f, driver, tenths: 50, at: f.Car.Position);
        Movement(passenger).RegisterManagedEntity(f.Car.TransientId, f.Car.Guid);
        SeatSpeedUpdate(f, passenger, tenths: 0);
        Assert.Equal(5f, f.Car.SeatMotion.Speed);

        f.Car.LastPoseMs = Environment.TickCount64;
        SeatSpeedUpdate(f, driver, tenths: 0, at: new Vector3(100000, 50, 100), time: 101);
        Assert.Equal(5f, f.Car.SeatMotion.Speed);
        f.Clear(); f.Seat(passenger, 2);
        Assert.Equal(1, f.Car.SeatOf(GuidOf(passenger)));
        Assert.DoesNotContain(f.Sent(passenger), p => Is(p, 0x70, 0x0b));
    }

    [Fact]
    public void NativeSeatSpeedResetsOnHandoffAndRefusesTheFormerSimulator()
    {
        using var f = new Fixture();
        var driver = f.Add(); var passenger = f.Add();
        f.Stream(driver); f.Stream(passenger); f.Enter(driver, 0); f.Enter(passenger, 1);
        SeatSpeedUpdate(f, driver, tenths: 5);
        f.Seat(driver, 2);
        Assert.Equal(0.5f, f.Car.SeatMotion.Speed);
        SeatSpeedUpdate(f, driver, tenths: 8); // The coasting simulator remains authoritative.
        Assert.Equal(0.8f, f.Car.SeatMotion.Speed);
        f.Seat(passenger, 0);
        Assert.Null(f.Car.SeatMotion.Speed);
        SeatSpeedUpdate(f, passenger, tenths: 20);
        Movement(driver).RegisterManagedEntity(f.Car.TransientId, f.Car.Guid);
        SeatSpeedUpdate(f, driver, tenths: 0);
        Assert.Equal(2f, f.Car.SeatMotion.Speed);
        f.Disconnect(passenger);
        Assert.Null(f.Car.SeatMotion.Speed);
    }

    [Fact]
    public void NativeSeatSpeedStillHonoursCooldownAndSeatOccupancyAndClearsOnWreck()
    {
        using var f = new Fixture();
        var driver = f.Add(); var passenger = f.Add();
        f.Stream(driver); f.Stream(passenger); f.Enter(driver, 0); f.Enter(passenger, 1);
        SeatSpeedUpdate(f, driver, tenths: 0);
        f.Seat(driver, 1);
        Assert.Equal(0, f.Car.SeatOf(GuidOf(driver)));
        f.Car.LastSeatChangeMs = 1234;
        Assert.Equal(VehicleActionResult.Cooldown,
            f.Fleet.TryChangeSeat(GuidOf(driver), 2, 1235, f.Car.LastSpeed, out _));
        Assert.Equal(0, f.Car.SeatOf(GuidOf(driver)));
        f.Fleet.ApplyDamage(f.Car, uint.MaxValue);
        Assert.Null(f.Car.SeatMotion.Speed);
    }

    private static void SeatSpeedUpdate(Fixture fixture, SoeConnection source, int? tenths,
        Vector3? at = null, uint time = 100, byte version = 0, bool stopped = false)
    {
        fixture.Deliver(source, Bytes(w =>
        {
            w.WriteByte(0x90);
            ClientVarInt.Write(w, fixture.Car.TransientId);
            w.WriteUInt16((ushort)((tenths.HasValue ? 0x10 : 0) | (at.HasValue ? 2 : 0) | (stopped ? 1 : 0)));
            w.WriteUInt32(time);
            w.WriteByte(version);
            if (stopped) ClientVarInt.Write(w, 0x40);
            if (at is Vector3 position)
            {
                ClientPackedInt.Write(w, (int)MathF.Round(position.X * 100));
                ClientPackedInt.Write(w, (int)MathF.Round(position.Y * 100));
                ClientPackedInt.Write(w, (int)MathF.Round(position.Z * 100));
            }
            if (tenths is int magnitude) ClientPackedInt.Write(w, magnitude);
        }), channel: 3);
    }
}
