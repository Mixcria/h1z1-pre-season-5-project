using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed partial class VehicleDamageIntegrationTests
{
    [Fact]
    public async Task RecoveryTimerPostsIntermediateFramesThroughTheListenerDispatcher()
    {
        var (service, connection, _) = Admit(useDrivingTuning: true);
        var pending = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        service.Post = pending.Enqueue;
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        Assert.True(session.Exit());
        session.RestreamVehiclesAt(Vector3.UnitX * 2);
        session.Fleet.NoteAttitude(car, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI), -0.25f);
        using var request = new PacketWriter();
        request.WriteByte(9); request.WriteUInt16(7); request.WriteUInt64(car.Guid);
        session.Deliver(request.Written);
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        bool intermediate = false;
        while (car.CoastingOwnerGuid == 0 && timeout.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(10);
            while (pending.TryDequeue(out var frame))
            {
                frame();
                float up = VehicleFlipDetector.UpDot(car.LastRotation!.Value);
                intermediate |= up > -0.9f && up < 0.9f;
            }
        }
        Assert.True(intermediate);
        Assert.True(timeout.ElapsedMilliseconds >= 1000);
        Assert.Equal(session.Guid, car.CoastingOwnerGuid);
        Assert.Equal(1f, VehicleFlipDetector.UpDot(car.LastRotation!.Value), 4);
    }

    [Theory]
    [InlineData(1u, 0f, 0f, 3.1415927f)]
    [InlineData(2u, 1.2f, 0.1f, -2.8f)]
    [InlineData(3u, -2.1f, 0.2f, 1.8f)]
    [InlineData(5u, 0.8f, 2.9f, 0.1f)]
    public void RecoverySendsIntermediateRotationsAndGrantsPhysicsOnlyAtTheEnd(uint family, float yaw, float pitch, float roll)
    {
        var (service, connection, recorder) = Admit(useDrivingTuning: true);
        service.Post = _ => { };
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(family);
        var position = new Vector3(10, 30, 40);
        Assert.True(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid, position, yaw, 1000, out _));
        Assert.True(session.Exit());
        session.RestreamVehiclesAt(position + Vector3.UnitX * 2);
        var original = Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);
        session.Fleet.NoteAttitude(car, original, -0.25f);
        car.LastClientTime = 25000000;
        float fuel = car.Fuel;
        int mark = recorder.Sent.Count;
        using var request = new PacketWriter();
        request.WriteByte(9); request.WriteUInt16(7); request.WriteUInt64(car.Guid);
        session.Deliver(request.Written);
        int afterStart = recorder.Sent.Count;
        long start = car.LastRightingMs;
        var target = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        var previous = original;
        for (int elapsed = 40; elapsed < ZoneService.VehicleRightingDurationMs; elapsed += 40)
        {
            Assert.True(service.AdvanceVehicleRighting(car, start + elapsed));
            var current = car.LastRotation!.Value;
            Assert.InRange(MathF.Abs(Quaternion.Dot(previous, current)), 0.995f, 1.00001f);
            Assert.True(MathF.Abs(Quaternion.Dot(current, target)) >= MathF.Abs(Quaternion.Dot(previous, target)) - 0.00001f);
            Assert.Equal(0ul, car.CoastingOwnerGuid);
            Assert.Equal(0ul, car.OwnerGuid);
            Assert.Equal(0, car.OccupantCount);
            previous = current;
        }
        var animationPackets = From(recorder, mark).ToList();
        Assert.Equal(30, animationPackets.Count(IsRightingFrame));
        Assert.DoesNotContain(animationPackets, IsPhysicsGrant);
        Assert.DoesNotContain(From(recorder, afterStart), p => p.Length > 1 && p[1] == 0x78);
        Assert.False(service.AdvanceVehicleRighting(car, start + ZoneService.VehicleRightingDurationMs));
        Assert.True(MathF.Abs(Quaternion.Dot(target, car.LastRotation!.Value)) > 0.99999f);
        Assert.Equal(position.X, car.Position.X);
        Assert.Equal(position.Z, car.Position.Z);
        Assert.Equal(position.Y + 0.75f, car.Position.Y, 4);
        Assert.Equal(fuel, car.Fuel);
        Assert.Equal(100000u, car.Health);
        Assert.Equal(session.Guid, car.CoastingOwnerGuid);
        Assert.False(car.UpsideDown);
        Assert.Equal(25001200u, car.LastClientTime);
        Assert.Single(From(recorder, mark), IsPhysicsGrant);
        int finished = recorder.Sent.Count;
        Assert.False(service.AdvanceVehicleRighting(car, start + 5000));
        Assert.Equal(finished, recorder.Sent.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryStopsWithoutGrantingPhysicsForAWreckOrReplacedWorld(bool replaceWorld)
    {
        var (service, connection, recorder) = Admit(useDrivingTuning: true);
        service.Post = _ => { };
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        Assert.True(session.Exit());
        session.RestreamVehiclesAt(Vector3.UnitX * 2);
        session.Fleet.NoteAttitude(car, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI), -0.25f);
        using var request = new PacketWriter();
        request.WriteByte(9); request.WriteUInt16(7); request.WriteUInt64(car.Guid);
        session.Deliver(request.Written);
        long start = car.LastRightingMs;
        Assert.True(service.AdvanceVehicleRighting(car, start + 400));
        if (replaceWorld) session.EnterMatchWithCar();
        else session.Fleet.Damage(car, 100000);
        int mark = recorder.Sent.Count;
        Assert.False(service.AdvanceVehicleRighting(car, start + 800));
        Assert.False(service.AdvanceVehicleRighting(car, start + 1600));
        Assert.Empty(From(recorder, mark));
        Assert.Equal(0ul, car.CoastingOwnerGuid);
    }

    [Fact]
    public void DuplicateOrLateAnimationTicksCannotReplayFramesOrLeavePartialRecovery()
    {
        var (service, connection, recorder) = Admit(useDrivingTuning: true);
        service.Post = _ => { };
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        Assert.True(session.Exit());
        session.RestreamVehiclesAt(Vector3.UnitX * 2);
        session.Fleet.NoteAttitude(car, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI), -0.25f);
        using var request = new PacketWriter();
        request.WriteByte(9); request.WriteUInt16(7); request.WriteUInt64(car.Guid);
        session.Deliver(request.Written);
        long start = car.LastRightingMs;
        Assert.True(service.AdvanceVehicleRighting(car, start + 200));
        int mark = recorder.Sent.Count;
        Assert.True(service.AdvanceVehicleRighting(car, start + 200));
        Assert.True(service.AdvanceVehicleRighting(car, start + 100));
        Assert.Empty(From(recorder, mark));
        Assert.False(service.AdvanceVehicleRighting(car, start + 3000));
        Assert.Single(From(recorder, mark), IsRightingFrame);
        Assert.Equal(1f, VehicleFlipDetector.UpDot(car.LastRotation!.Value), 4);
        Assert.Equal(start + 3000, car.LastRightingMs);
        Assert.Equal(session.Guid, car.CoastingOwnerGuid);
    }

    [Fact]
    public void MissingDispatcherDoesNotReserveTheVehicleOrReleaseItsSimulator()
    {
        var (service, connection, recorder) = Admit(useDrivingTuning: true);
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        Assert.True(session.Exit());
        session.RestreamVehiclesAt(Vector3.UnitX * 2);
        session.Fleet.NoteAttitude(car, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI), -0.25f);
        using var request = new PacketWriter();
        request.WriteByte(9); request.WriteUInt16(7); request.WriteUInt64(car.Guid);
        int mark = recorder.Sent.Count;
        session.Deliver(request.Written);
        Assert.Empty(From(recorder, mark));
        Assert.Equal(session.Guid, car.CoastingOwnerGuid);
        Assert.False(service.AdvanceVehicleRighting(car, Environment.TickCount64 + 2000));
        service.Post = _ => { };
        session.Deliver(request.Written);
        Assert.Equal(0ul, car.CoastingOwnerGuid);
        Assert.True(service.AdvanceVehicleRighting(car, car.LastRightingMs + 400));
    }

    private static bool IsRightingFrame(byte[] packet) => packet.Length == VehicleManagedLocation.Length + 1
        && packet[1] == 0x11 && packet[2] == 0x23 && packet[3] == 0;

    private static bool IsPhysicsGrant(byte[] packet) => packet.Length >= 5
        && packet[1] == 0x11 && packet[2] == 0x39 && packet[3] == 0 && packet[4] == 1;
}
