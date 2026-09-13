using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed partial class VehicleDamageIntegrationTests
{
    [Fact]
    public void RightingIgnoresOldPhysicsPacketsAndAcceptsTheCorrectedVersion()
    {
        var (service, connection, _) = Admit(useDrivingTuning: true);
        service.Post = _ => { };
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        Assert.True(session.Exit());
        session.RestreamVehiclesAt(new Vector3(2, 0, 0));
        session.Fleet.NoteAttitude(car, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI), -0.25f);
        byte oldVersion = car.MovementVersion;
        using var request = new PacketWriter();
        request.WriteByte(9); request.WriteUInt16(7); request.WriteUInt64(car.Guid);
        session.Deliver(request.Written);
        var original = car.Position;
        void Pose(byte version, Vector3 position)
        {
            using var packet = new PacketWriter();
            packet.WriteByte(0x90);
            ClientVarInt.Write(packet, car.TransientId);
            (PositionUpdateBlock.AtRest(position, car.Yaw) with { Byte6 = version, SequenceTime = 1000 }).WriteTo(packet);
            session.Deliver(packet.Written, channel: 3);
        }
        Pose(oldVersion, original + Vector3.UnitX);
        Pose(car.MovementVersion, original + Vector3.UnitX);
        Assert.Equal(original, car.Position); // No client simulates the body during the roll.
        Assert.False(service.AdvanceVehicleRighting(car, car.LastRightingMs + ZoneService.VehicleRightingDurationMs));
        var corrected = car.Position;
        Pose(oldVersion, original);
        Assert.Equal(corrected, car.Position);
        Pose(car.MovementVersion, corrected + Vector3.UnitX);
        Assert.Equal(corrected + Vector3.UnitX, car.Position);
        Assert.False(car.UpsideDown);
    }

    [Fact]
    public void DefaultDrivingIgnoresBumpsAndCapsTheWholeCollisionRampAtTwoPercent()
    {
        var (service, connection, _) = Admit(useDrivingTuning: true);
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        long now = Environment.TickCount64;
        foreach (uint raw in new uint[] { 7, 250, 1000 })
            session.CollisionAt(Collision(car.Guid, car.Guid, raw, CollisionDamageCause.VehicleCollision), now);
        Assert.Equal(100000u, car.Health);
        foreach (uint raw in new uint[] { 1006, 5000, 158334, uint.MaxValue, 158334 })
            session.CollisionAt(Collision(car.Guid, car.Guid, raw, CollisionDamageCause.VehicleCollision), now + 10);
        Assert.Equal(98000u, car.Health);
        Assert.Equal(10000u, session.Hitpoints);
    }

    [Fact]
    public void DefaultRolloverGivesFifteenSecondsGraceAndCostsHalfAPercentPerPulse()
    {
        var (service, connection, _) = Admit(useDrivingTuning: true);
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        var options = new VehicleDamageOptions();
        session.Fleet.NoteAttitude(car, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI), options.UpsideDownDotThreshold);
        long start = car.UpsideDownSinceMs;
        Assert.Empty(session.Fleet.PulseUpsideDown(start + 14999, options));
        Assert.Equal(500u, Assert.Single(session.Fleet.PulseUpsideDown(start + 15000, options)).Charged);
    }

    [Theory]
    [InlineData(2f, false, true)]
    [InlineData(20f, false, false)]
    [InlineData(2f, true, false)]
    public void FStartsAnimatedRightingOnlyForANearbyEmptyOverturnedCar(float distance, bool occupied, bool expectFlip)
    {
        var (service, connection, recorder) = Admit(useDrivingTuning: true);
        service.Post = _ => { };
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        if (!occupied) Assert.True(session.Exit());
        session.RestreamVehiclesAt(new Vector3(distance, 0, 0));
        session.Fleet.NoteAttitude(car, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI), -0.25f);
        int mark = recorder.Sent.Count;
        using var request = new PacketWriter();
        request.WriteByte(9); request.WriteUInt16(7); request.WriteUInt64(car.Guid);
        session.Deliver(request.Written); session.Deliver(request.Written);
        var flips = From(recorder, mark).Where(p => p.Length >= 4 && p[1] == 0x11 && p[2] == 0x23 && p[3] == 0).ToList();
        Assert.Equal(expectFlip ? 1 : 0, flips.Count);
        if (expectFlip)
        {
            Assert.Equal(VehicleManagedLocation.Length + 1, flips[0].Length);
            Assert.Equal(car.Guid, BitConverter.ToUInt64(flips[0], 4));
            Assert.Equal(-1f, VehicleFlipDetector.UpDot(car.LastRotation!.Value), 4);
            Assert.Equal(0f, car.LastSpeed);
            Assert.Equal(0f, car.Position.Y, 3);
            Assert.Equal(0ul, car.CoastingOwnerGuid);
            long started = car.LastRightingMs;
            Assert.True(service.AdvanceVehicleRighting(car, started + ZoneService.VehicleRightingDurationMs / 2));
            Assert.InRange(VehicleFlipDetector.UpDot(car.LastRotation!.Value), -0.01f, 0.01f);
            Assert.True(car.Position.Y > 0.75f);
            Assert.Equal(0ul, car.CoastingOwnerGuid);
            Assert.True(session.Enter(car.Guid)); // Interaction is consumed without mounting mid-roll.
            Assert.Equal(0, car.OccupantCount);
            Assert.False(service.AdvanceVehicleRighting(car, started + ZoneService.VehicleRightingDurationMs));
            Assert.False(car.UpsideDown);
            Assert.Equal(1f, VehicleFlipDetector.UpDot(car.LastRotation!.Value), 4);
            Assert.Equal(0.75f, car.Position.Y, 3);
            Assert.Equal(session.Guid, car.CoastingOwnerGuid);
            Assert.Equal(0, car.OccupantCount);
            Assert.Equal(100000u, car.Health);
        }
    }
}
