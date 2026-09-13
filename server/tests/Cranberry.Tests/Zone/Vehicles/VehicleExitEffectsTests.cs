using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed partial class VehicleDamageIntegrationTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(5u)]
    public void CapturedCoastStopReleasesLocalOwnershipAndWakesTheRestingController(uint family)
    {
        var (service, connection, recorder) = Admit(new ZoneOptions
        {
            VehicleRelay = new VehicleRelayOptions { MinIntervalMs = int.MaxValue, MaxObserversPerPose = 1 },
        });
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(family);
        session.RefreshInventory(car);
        var broadcast = (VehiclePoseBroadcast)typeof(ZoneService)
            .GetField("_vehiclePoses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var viewers = new[] { new StopObserver(91, car.Guid), new StopObserver(92, car.Guid) };
        foreach (var viewer in viewers) broadcast.Register(viewer);
        var finalPosition = new Vector3(10, -0.38f, 0); // preserve the captured negative Y without an offset correction
        session.Deliver(SpeedReport(car.TransientId, 100, finalPosition, 1, true), channel: 3);
        Assert.True(session.Exit());
        long poseAt = car.LastPoseMs;
        foreach (var viewer in viewers) viewer.Poses.Clear();
        int mark = recorder.Sent.Count;
        using var writer = new PacketWriter();
        writer.WriteByte(0x90); ClientVarInt.Write(writer, car.TransientId);
        // wire-20260908-180852 18:16:25.388: final movement mask 0001, posture 41.
        writer.WriteUInt16(1); writer.WriteUInt32(101); writer.WriteByte(0);
        ClientVarInt.Write(writer, 0x41);
        session.Deliver(writer.Written.ToArray(), channel: 3);
        foreach (var viewer in viewers)
        {
            var motion = ClientMovementUpdate.Parse(Assert.Single(viewer.Poses).MovementPayload.Span);
            Assert.Equal(0x49u, motion.Posture);
            Assert.Equal(MovementFieldMask.All, motion.Fields);
            Assert.Equal(car.Position, motion.EffectivePosition);
            Assert.Equal(0f, motion.HorizontalSpeed);
            Assert.Equal(0f, motion.VerticalSpeed);
            Assert.Equal(Vector3.Zero, motion.AuxiliaryVector);
            Assert.Equal(0f, motion.Scalar144);
        }
        var sent = From(recorder, mark).ToArray();
        AssertNativeRestingHandoff(sent, car.Position);
        Assert.Single(Sub8(sent, 0x9e, 3));
        Assert.Equal(0, Assert.Single(Sub8(sent, 0x88, 0x1b))[^1]);
        var release = Assert.Single(Sub8(sent, 0x0f, 0x3b));
        Assert.True(Array.IndexOf(sent, release) < Array.FindIndex(sent, p => p[1] == 0x78));
        Assert.False(car.EngineOn);
        Assert.Equal(0ul, car.CoastingOwnerGuid);
        Assert.Equal(poseAt, car.LastPoseMs);
        Assert.Equal(finalPosition, car.Position);
        session.Deliver(SpeedReport(car.TransientId, 102, Vector3.Zero, 9, true), channel: 3);
        Assert.Equal(finalPosition, car.Position); // late local packets cannot undo the handoff
        car.LastInteractionMs = long.MinValue;
        mark = recorder.Sent.Count;
        Assert.True(session.Enter(car.Guid));
        Assert.Single(Sub8(From(recorder, mark), 0x0f, 0x3b));
        Assert.False(car.EngineOn);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(5u)]
    public void TakingTheDriverSeatPreservesTheNativeBodyOrigin(uint family)
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(family, asDriver: false);
        var position = car.Position;
        int mark = recorder.Sent.Count;
        session.Deliver(SeatChange(car.Guid, 0));
        var sent = From(recorder, mark).ToArray();
        Assert.Single(Sub8(sent, 0x0f, 0x3b));
        Assert.DoesNotContain(sent, p => p[1] == 0x11 && p[2] == 0x23 && p[3] == 0);
        Assert.Equal(session.Guid, car.DriverGuid);
        Assert.Equal(position, car.Position);
        connection.Disconnect();
    }

    private static void AssertNativeRestingHandoff(byte[][] packets, Vector3 expectedPosition)
    {
        // Native 140c110b0 emptied the driver's queue. 140c0e400 inserts records (even
        // equal timestamps), then asks 140c10cb0 whether to schedule an entity update:
        // count > 1 OR nonzero linear/angular velocity. A lone stopped record stays asleep.
        // This was observed in PID 13096: correct final pose queued, visible pose at spawn.
        // 11/23 bypasses 142337f30's physics-local offset conversion. The native body
        // transform must survive release; the movement queue supplies the final physics pose.
        Assert.DoesNotContain(packets, p => p[1] == 0x11 && p[2] == 0x23 && p[3] == 0);
        var queue = new List<ClientMovementUpdate>();
        bool scheduled = false;
        foreach (var packet in packets.Where(p => p[1] == 0x78))
        {
            var managed = packet.AsSpan(1).ToArray();
            managed[0] = 0x90; // the native inbound/outbound movement records share a layout
            var pose = ClientManagedMovementUpdate.Parse(managed).Movement;
            Assert.Equal(MovementFieldMask.All, pose.Fields);
            Assert.Equal(0x49u, pose.Posture);
            Assert.Equal(expectedPosition, pose.EffectivePosition);
            Assert.Equal(0f, pose.HorizontalSpeed);
            Assert.Equal(0f, pose.VerticalSpeed);
            Assert.Equal(0f, pose.Scalar154);
            Assert.Equal(Vector3.Zero, pose.AuxiliaryVector);
            if (queue.Count != 0 && unchecked((int)(pose.ClientTime - queue[^1].ClientTime)) < 0)
                queue.Clear(); // 140c0e400 discards the old queue on clock regression
            queue.Add(pose);
            scheduled |= queue.Count > 1;
            if (queue.Count == 1) Assert.False(scheduled);
        }
        Assert.True(scheduled, "A single zero-motion baseline is accepted but never wakes the native vehicle tick.");
        Assert.Equal(2, queue.Count);
        Assert.Equal(queue[0].Payload.ToArray(), queue[1].Payload.ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FinalSpeedOnlyStopReachesEveryViewerWithoutCreatingAPositionOrRevokingPhysics(bool coasting, bool vertical)
    {
        var (service, connection, _) = Admit(new ZoneOptions
        {
            VehicleRelay = new VehicleRelayOptions { MinIntervalMs = int.MaxValue, MaxObserversPerPose = 1 },
        });
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        var broadcast = (VehiclePoseBroadcast)typeof(ZoneService)
            .GetField("_vehiclePoses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var viewers = new[] { new StopObserver(91, car.Guid), new StopObserver(92, car.Guid) };
        foreach (var viewer in viewers) broadcast.Register(viewer);
        var position = new Vector3(10, 0, 0);
        session.Deliver(SpeedReport(car.TransientId, 100, position, 10, vertical), channel: 3);
        Assert.Single(viewers.SelectMany(v => v.Poses)); // ordinary fan-out limit is still enforced
        if (coasting) Assert.True(session.Exit());
        long poseAt = car.LastPoseMs;
        long restAt = car.CoastRestSinceMs;
        foreach (var viewer in viewers) viewer.Poses.Clear();

        byte[] stop = SpeedReport(car.TransientId, 101, null, 0, vertical);
        session.Deliver(stop, channel: 3);
        foreach (var viewer in viewers)
        {
            var received = Assert.Single(viewer.Poses);
            Assert.Equal(ClientManagedMovementUpdate.Parse(stop).Movement.Payload.ToArray(), received.MovementPayload.ToArray());
            var movement = ClientMovementUpdate.Parse(received.MovementPayload.Span);
            Assert.Null(movement.EffectivePosition);
            Assert.Equal(0f, movement.HorizontalSpeed);
            Assert.Equal(vertical ? 0f : (float?)null, movement.VerticalSpeed);
        }
        Assert.Equal(position, car.Position);
        Assert.Equal(poseAt, car.LastPoseMs);
        Assert.Equal(restAt, car.CoastRestSinceMs);
        Assert.Equal(coasting ? session.Guid : 0ul, car.CoastingOwnerGuid);

        // Repeated zero reports are not further transitions and retain normal rate limiting.
        foreach (var viewer in viewers) viewer.Poses.Clear();
        session.Deliver(SpeedReport(car.TransientId, 102, null, 0, vertical), channel: 3);
        Assert.Empty(viewers.SelectMany(v => v.Poses));

        // A stale managed registration does not authorize an old driver after handoff.
        if (!coasting) Assert.True(session.Exit());
        car.LastInteractionMs = long.MinValue;
        Assert.Equal(VehicleActionResult.Ok, session.Fleet.TryEnter(car.Guid, 99, 0,
            Environment.TickCount64, out _, out _));
        uint acceptedTime = car.LastClientTime;
        session.Deliver(SpeedReport(car.TransientId, 103, null, 5, vertical), channel: 3);
        Assert.Equal(acceptedTime, car.LastClientTime);
        Assert.Empty(viewers.SelectMany(v => v.Poses));
    }

    private sealed class StopObserver(ulong guid, ulong vehicle) : IVehicleObserver
    {
        public ulong CharacterGuid => guid;
        public bool IsOpen => true;
        public Vector3? Position => Vector3.Zero;
        public bool Holds(ulong vehicleGuid) => vehicle == vehicleGuid;
        public List<VehiclePoseRelay> Poses { get; } = [];
        public void Relay(VehiclePoseRelay pose) => Poses.Add(pose);
    }

    private static byte[] SpeedReport(uint transientId, uint time, Vector3? position, int speed, bool vertical)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(0x90);
        ClientVarInt.Write(writer, transientId);
        writer.WriteUInt16((ushort)(MovementFieldMask.HorizontalSpeed | (vertical ? MovementFieldMask.VerticalSpeed : 0)
            | (position is null ? 0 : MovementFieldMask.Position)));
        writer.WriteUInt32(time);
        writer.WriteByte(0);
        if (position is { } at)
        {
            ClientPackedInt.Write(writer, (int)(at.X * 100));
            ClientPackedInt.Write(writer, (int)(at.Y * 100));
            ClientPackedInt.Write(writer, (int)(at.Z * 100));
        }
        if (vertical) ClientPackedInt.Write(writer, 0); // vertical speed / 100
        ClientPackedInt.Write(writer, speed * 10); // horizontal speed / 10
        return writer.Written.ToArray();
    }

    [Theory]
    [InlineData(1u, 90001u, 100042u)]
    [InlineData(2u, 90062u, 110237u)]
    [InlineData(3u, 90063u, 110260u)]
    [InlineData(5u, 90187u, 120649u)]
    public void ExitExplicitlyRemovesMotorEffectBeforeDismountAndKeepsCoasting(
        uint family, uint clientEffect, uint serverEffect)
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(family);
        session.RefreshInventory(car);
        int mark = recorder.Sent.Count;
        Assert.True(session.Exit());
        var sent = From(recorder, mark).ToArray();
        var effect = Assert.Single(Sub8(sent, 0x9e, 3));
        Assert.Equal(55, effect.Length); // gateway byte + native 54-byte removal
        Assert.Equal(4u, BitConverter.ToUInt32(effect, 3));
        Assert.Equal(clientEffect, BitConverter.ToUInt32(effect, 7));
        Assert.Equal(serverEffect, BitConverter.ToUInt32(effect, 11));
        Assert.Equal(car.Guid, BitConverter.ToUInt64(effect, 15));
        Assert.Equal(session.Guid, BitConverter.ToUInt64(effect, 23));
        Assert.True(Array.IndexOf(sent, effect) < Array.FindIndex(sent, p => p[1] == 0x70 && p[2] == 4));
        var engine = Assert.Single(Sub8(sent, 0x88, 0x1b));
        Assert.Equal(0ul, BitConverter.ToUInt64(engine, 3)); // server-origin applies locally
        Assert.Equal(0, engine[^1]);
        Assert.False(car.EngineOn);
        Assert.Equal(session.Guid, car.CoastingOwnerGuid);
        Assert.Empty(Sub8(sent, 0x0f, 0x3b));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateMotorActivationIsRemovedWithoutStoppingANewDriver(bool newDriver)
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        session.RefreshInventory(car);
        Assert.True(session.Exit());
        if (newDriver)
        {
            car.LastInteractionMs = long.MinValue;
            Assert.Equal(VehicleActionResult.Ok, session.Fleet.TryEnter(car.Guid, 99, 0,
                Environment.TickCount64, out _, out _));
            car.EngineOn = true;
        }
        // Actual August 71-byte AddEffect layout; replace only this fixture's IDs.
        byte[] add = Convert.FromHexString("9E0101000000905F0100B7860100000000002110000000000000010000000000000000000000110000000000004600000000000000000000000000000000000000000000000001");
        BinaryPrimitives.WriteUInt32LittleEndian(add.AsSpan(6), 90001);
        BinaryPrimitives.WriteUInt32LittleEndian(add.AsSpan(10), 100042);
        BinaryPrimitives.WriteUInt64LittleEndian(add.AsSpan(18), session.Guid);
        BinaryPrimitives.WriteUInt64LittleEndian(add.AsSpan(38), car.Guid);
        int mark = recorder.Sent.Count;
        session.Deliver(add);
        var sent = From(recorder, mark).ToArray();
        Assert.Single(Sub8(sent, 0x9e, 3));
        Assert.Equal(newDriver, car.EngineOn);
        if (newDriver) Assert.Empty(Sub8(sent, 0x88, 0x1b));
        else Assert.Equal(0, Assert.Single(Sub8(sent, 0x88, 0x1b))[^1]);
        Assert.Empty(Sub8(sent, 0xa0, 1));
    }
}
