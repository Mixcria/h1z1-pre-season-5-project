using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Match;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

/// <summary>
/// <b>The vehicle lane's wiring</b> (docs/117): the bytes a crash, a shot, a flip and a boost press
/// really put on the wire, read off a fake session driven through <c>ZoneService</c> itself rather
/// than off the writers. <c>CollisionDamagePacketTests</c>, <c>VehicleBoostPacketTests</c> and
/// <c>VehicleDamageTests</c> already pin every layout and every rule; what is pinned here is the
/// <i>orchestration</i>.
///
/// <para>
/// <b>Why the seam.</b> The honest route to a car crash is a 15 s lobby timer, a 20 s countdown, a
/// drop, a landing, a walk to a parked car and then a wall — all on <c>Task.Delay</c> against the
/// wall clock, because <c>ZoneService.Later</c> has no injectable clock. So the <i>entry</i> is
/// faked through <c>ZoneService.VehicleTestSession</c>; the client bytes go in through the real
/// gateway dispatcher and everything after that is the production path.
/// </para>
/// <para>D29: send-side only. This proves what the server sent, never what the client did with it.</para>
/// </summary>
public sealed partial class VehicleDamageIntegrationTests
{
    [Fact]
    public void NativeAugustActivationLayoutTargetsTheCarAndMotorReleaseCanRestart()
    {
        // Real 17:31:04.494 activation. The old parser returned 0x0000000100000000 as target.
        var captured = Convert.FromHexString("9E0101000000905F0100B7860100000000002110000000000000010000000000000000000000110000000000004600000000000000000000000000000000000000000000000001");
        Assert.True(EffectRequest.TryParse(captured, out var parsed));
        Assert.Equal(0x1021ul, parsed.SourceCharacterId);
        Assert.Equal(0x4600000000000011ul, parsed.TargetCharacterId);

        var (service, connection, _) = Admit(new ZoneOptions { VehicleBoost = new VehicleBoostOptions { Enabled = false } });
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        car.LastSpeed = 60;
        // Same August layout with the MotorRun pair and this fixture's actors.
        BinaryPrimitives.WriteUInt32LittleEndian(captured.AsSpan(6), 90001);
        BinaryPrimitives.WriteUInt32LittleEndian(captured.AsSpan(10), 100042);
        BinaryPrimitives.WriteUInt64LittleEndian(captured.AsSpan(18), session.Guid);
        BinaryPrimitives.WriteUInt64LittleEndian(captured.AsSpan(38), car.Guid);
        using var remove = new PacketWriter();
        remove.WriteByte(0x9e); remove.WriteByte(3);
        remove.WriteUInt32(1); remove.WriteUInt32(90001); remove.WriteUInt32(100042);
        remove.WriteUInt64(session.Guid); remove.WriteUInt64(car.Guid);
        remove.WriteUInt64(0);
        for (int i = 0; i < 4; i++) remove.WriteSingle(0);
        for (int i = 0; i < 2; i++)
        {
            session.Deliver(remove.Written);
            Assert.False(car.EngineOn);
            session.Deliver(captured);
            Assert.True(car.EngineOn);
        }
        Assert.Equal(session.Guid, car.OwnerGuid);
        Assert.Equal(60f, car.LastSpeed);
    }

    [Fact]
    public void ParkedTimeCannotAuthorizeAJumpBackToTheSpawnOnReentry()
    {
        var (service, connection, _) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        var at = new Vector3(100, 0, 0);
        Assert.True(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid, at, 0, 1000, out _));
        Assert.True(session.Exit());
        car.EndCoast();
        car.LastInteractionMs = long.MinValue;
        Assert.Equal(VehicleActionResult.Ok, session.Fleet.TryEnter(car.Guid, session.Guid, 0, 120000, out _, out _));
        Assert.False(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid, Vector3.Zero, 0, 120017, out _));
        Assert.False(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid, Vector3.Zero, 0, 120000, out _));
        Assert.Equal(at, car.Position);
        Assert.True(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid, at, 0, 120034, out _));
    }

    [Fact]
    public void BriefRolloverGetsTimeToRecoverWithoutRoofDamage()
    {
        var (service, connection, _) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        session.Fleet.NoteAttitude(car, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI), -0.25f);
        long start = car.LastFlipPulseMs;
        session.PumpDamage(start + 5999);
        Assert.Equal(100000u, car.Health);
        session.Fleet.NoteAttitude(car, Quaternion.Identity, -0.25f);
        session.PumpDamage(start + 9000);
        Assert.Equal(100000u, car.Health);
    }

    [Fact]
    public void SleepingCarRetainsItsPhysicsControllerThroughReentry()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        var stoppedAt = new Vector3(-3248.51f, 75.19f, -2353.78f);
        Assert.True(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid, stoppedAt, 1.2f,
            Environment.TickCount64, out _));
        var tilt = Quaternion.CreateFromYawPitchRoll(1.2f, 0.1f, 0.2f);
        session.Fleet.NoteAttitude(car, tilt, -0.25f);
        car.LastClientTime = 25000000;
        Assert.True(session.Exit());
        int mark = recorder.Sent.Count;
        session.PumpCoasting(car.CoastStartedMs + 6000);
        var released = From(recorder, mark).ToList();
        Assert.Empty(released);
        Assert.Equal(session.Guid, car.CoastingOwnerGuid);
        using var parkedWriter = new PacketWriter();
        parkedWriter.WriteByte(5);
        VehiclePoseRelay.Parked(car).WriteTo(parkedWriter);
        byte[] snapshot = parkedWriter.Written.ToArray();
        var managedBytes = snapshot[1..];
        managedBytes[0] = 0x90;
        var parsed = ClientManagedMovementUpdate.Parse(managedBytes);
        Assert.Equal(car.TransientId, parsed.TransientId);
        var movement = parsed.Movement;
        Assert.Equal(stoppedAt, movement.EffectivePosition);
        Assert.Equal(25000001u, movement.ClientTime);
        Assert.Equal(0f, movement.HorizontalSpeed);
        Assert.Equal(0f, movement.VerticalSpeed);
        Assert.Equal(Vector3.Zero, movement.AuxiliaryVector);
        // Native 140b14d30 cannot merge a partial record after take-control has emptied the queue.
        Assert.Equal(MovementFieldMask.All, movement.Fields);
        Assert.Equal(0x49u, movement.Posture);
        var parkedAttitude = Quaternion.CreateFromYawPitchRoll(movement.Orientation!.Value,
            movement.Scalar14C!.Value, movement.Scalar150!.Value);
        Assert.True(MathF.Abs(Quaternion.Dot(tilt, parkedAttitude)) > 0.999f);
        Assert.Equal(new Quaternion(1, 1, 1, 1), movement.Rotation);
        Assert.Equal(new PreciseMovementPose(Vector3.Zero, new Quaternion(0, 0, 0, 0)), movement.PrecisePose);
        car.LastInteractionMs = long.MinValue;
        mark = recorder.Sent.Count;
        Assert.True(session.Enter(car.Guid));
        var entered = From(recorder, mark).ToList();
        Assert.Empty(Sub8(entered, 0x0f, 0x3b));
        Assert.DoesNotContain(entered, p => p[1] == 0x78
            || (p[1] == 0x11 && p[2] == 0x23));
        Assert.Equal(stoppedAt, car.Position);
    }

    [Fact]
    public void ReenteringMovingCoastDoesNotRecreateThePhysicsController()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        car.LastSpeed = 60;
        Assert.True(session.Exit());
        car.LastInteractionMs = long.MinValue;
        int mark = recorder.Sent.Count;
        Assert.True(session.Enter(car.Guid));
        Assert.Equal(session.Guid, car.DriverGuid);
        Assert.Equal(0ul, car.CoastingOwnerGuid);
        Assert.Empty(Sub8(From(recorder, mark), 0x0f, 0x3b));
        Assert.DoesNotContain(From(recorder, mark), p => p[1] == 0x78);
    }

    [Fact]
    public void StreamOutReleasesManagedPhysicsBeforeRemovingTheActorAndRejectsLateMovement()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(3);
        var final = new Vector3(1467.35f, 51.29f, -2236.71f);
        Assert.True(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid, final, 1.4f,
            Environment.TickCount64, out _));
        Assert.True(session.Exit());
        int mark = recorder.Sent.Count;
        session.RestreamVehiclesAt(new Vector3(9000, 51, 9000));
        var sent = From(recorder, mark).ToList();
        int release = sent.FindIndex(p => p[1] == 0x11 && p[2] == 0x39 && p[3] == 0);
        int remove = sent.FindIndex(p => p[1] == 0x0f && p[2] == 1);
        Assert.True(release >= 0 && remove > release);
        Assert.Equal(0ul, car.CoastingOwnerGuid);
        Assert.False(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid, Vector3.Zero,
            0, Environment.TickCount64 + 60000, out _));
        Assert.Equal(final, car.Position);
    }

    [Fact]
    public void MountingDuringADeferredEvictionKeepsTheActorAndRestoresStreamMembership()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(3);
        Assert.True(session.Exit());
        var deferred = session.PlanRestreamVehiclesAt(new Vector3(9000, 51, 9000));
        Assert.NotEmpty(deferred);
        Assert.False(session.IsVehicleStreamed(car.Guid));
        car.LastInteractionMs = long.MinValue;
        Assert.True(session.Enter(car.Guid));
        int mark = recorder.Sent.Count;
        foreach (var action in deferred) action();
        Assert.Equal(session.Guid, car.OwnerGuid);
        Assert.Equal(session.Guid, car.DriverGuid);
        Assert.True(session.IsVehicleStreamed(car.Guid));
        Assert.Empty(From(recorder, mark));
        Assert.True(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid,
            new Vector3(1, 0, 0), 0, Environment.TickCount64, out _));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(5u)]
    public void ReenteringAfterReleasePreservesTheNativeBodyOrigin(uint family)
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(family);
        var final = new Vector3(1467.35f, 51.29f, -2236.71f);
        Assert.True(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid, final, 1.4f,
            Environment.TickCount64, out _));
        Assert.True(session.Exit());
        session.RestreamVehiclesAt(new Vector3(9000, 51, 9000));
        // A removed actor must be streamed back before a native mount request can name it.
        session.RestreamVehiclesAt(final);
        car.LastInteractionMs = long.MinValue;
        int mark = recorder.Sent.Count;
        Assert.True(session.Enter(car.Guid));
        var sent = From(recorder, mark).ToList();
        Assert.Single(Sub8(sent, 0x0f, 0x3b));
        Assert.DoesNotContain(sent, p => p[1] == 0x11 && p[2] == 0x23 && p[3] == 0);
        Assert.DoesNotContain(sent, p => p[1] == 0x78); // grant clears this queue before it can tick
        Assert.Equal(final, car.Position);
        Assert.Equal(session.Guid, car.DriverGuid);
        Assert.DoesNotContain(sent, p => p[1] == 0x0f && p[2] == 1);
    }

    [Fact]
    public void NativeVehicleAttitudeComesFromEulerFieldsAndRetainsSparseTilt()
    {
        var (service, connection, _) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        var native = ClientManagedMovementUpdate.Parse(Convert.FromHexString(
            "90DE127AFF1F36A29C010025014DEA23BAE8348120A70A863F00000000000000002203220322032203000000000000000000"));
        using var packet = new PacketWriter();
        packet.WriteByte(0x90);
        ClientVarInt.Write(packet, car.TransientId);
        packet.WriteRaw(native.Movement.Payload.Span);
        session.Deliver(packet.Written, channel: 3);
        var expected = Quaternion.CreateFromYawPitchRoll(native.Movement.Orientation!.Value, 0, 0);
        Assert.True(Quaternion.Dot(expected, car.LastRotation!.Value) > 0.99999f);
        Assert.False(car.UpsideDown);

        // A subsequent roll delta omits yaw, pitch and the unrelated four-value 0x200 block.
        using var rolled = new PacketWriter();
        rolled.WriteByte(0x90);
        ClientVarInt.Write(rolled, car.TransientId);
        rolled.WriteUInt16(0x0082);
        rolled.WriteUInt32(native.Movement.ClientTime + 100);
        rolled.WriteByte(0);
        ClientPackedInt.Write(rolled, (int)MathF.Round(car.Position.X * 100));
        ClientPackedInt.Write(rolled, (int)MathF.Round(car.Position.Y * 100));
        ClientPackedInt.Write(rolled, (int)MathF.Round(car.Position.Z * 100));
        ClientPackedInt.Write(rolled, 314);
        session.Deliver(rolled.Written, channel: 3);
        Assert.True(car.UpsideDown);
        var parked = ClientMovementUpdate.Parse(VehiclePoseRelay.Parked(car).MovementPayload.Span);
        var retained = Quaternion.CreateFromYawPitchRoll(parked.Orientation!.Value,
            parked.Scalar14C!.Value, parked.Scalar150!.Value);
        Assert.True(MathF.Abs(Quaternion.Dot(retained, car.LastRotation!.Value)) > 0.9999f);
    }

    [Fact]
    public void CapturedHugeLandingAndRepeatedRampCannotDestroyAFullHealthCar()
    {
        var (service, connection, _) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        foreach (uint amount in new uint[] { 1006, 158334, 225000, uint.MaxValue, 158334 })
            session.Deliver(Collision(car.Guid, car.Guid, amount, CollisionDamageCause.VehicleCollision));
        Assert.Equal(80000u, car.Health);
        Assert.Equal(10000u, session.Hitpoints);
        Assert.Equal(session.Guid, car.DriverGuid);
    }

    [Theory]
    [InlineData(1u)] [InlineData(2u)] [InlineData(3u)] [InlineData(5u)]
    public void DriverCanToggleEngineAtSpeedAndHonkRepeatedly(uint vehicleId)
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(vehicleId);
        car.LastSpeed = 60;
        uint engine = VehicleDriverControls.EngineAbility(vehicleId);
        int mark = recorder.Sent.Count;
        session.Deliver(NativeDriverAbility(engine, 11, false, session.Guid, car.Guid));
        Assert.False(car.EngineOn);
        Assert.Equal(session.Guid, car.OwnerGuid);
        Assert.Equal(session.Guid, car.DriverGuid);
        Assert.Equal(60f, car.LastSpeed);
        session.Deliver(NativeDriverAbility(engine, 11, true, session.Guid, car.Guid));
        Assert.True(car.EngineOn);
        session.Deliver(NativeDriverAbility(engine, 11, false, session.Guid, car.Guid));
        car.Fuel = 0;
        session.Deliver(NativeDriverAbility(engine, 11, true, session.Guid, car.Guid));
        Assert.False(car.EngineOn);
        for (int i = 0; i < 2; i++)
        {
            session.Deliver(NativeDriverAbility(1111301, 13, true, session.Guid, car.Guid));
            session.Deliver(NativeDriverAbility(1111301, 13, true, session.Guid, car.Guid));
            Assert.True(car.HornOn);
            session.Deliver(NativeDriverAbility(1111301, 13, false, session.Guid, car.Guid));
            Assert.False(car.HornOn);
        }
        Assert.Equal(2, Sub8(From(recorder, mark), 0x0f, 0x15).Count);
        Assert.Equal(2, Sub8(From(recorder, mark), 0x0f, 0x16).Count);
        Assert.Empty(Sub8(From(recorder, mark), 0x0f, 0x3b));
        session.Deliver(NativeDriverAbility(1111301, 13, true, session.Guid, car.Guid));
        session.Exit();
        Assert.False(car.HornOn);
    }

    [Fact]
    public void PassengerAndWrongAbilityCannotControlEngineOrHorn()
    {
        var (service, connection, _) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(asDriver: false);
        car.EngineOn = true;
        session.Deliver(NativeDriverAbility(1111153, 11, false, session.Guid, car.Guid));
        session.Deliver(NativeDriverAbility(1111301, 13, true, session.Guid, car.Guid));
        Assert.True(car.EngineOn);
        Assert.False(car.HornOn);
        car = session.EnterMatchWithCar();
        session.Deliver(NativeDriverAbility(1111285, 11, false, session.Guid, car.Guid));
        Assert.True(car.EngineOn);
    }

    [Theory]
    [InlineData(1u)] [InlineData(2u)] [InlineData(3u)] [InlineData(5u)]
    public void CapturedHeadlightsAndHornRequestsReachControlsWithoutCombatAndClearBusyOnEveryRelease(uint family)
    {
        var (service, connection, recorder) = Admit(new ZoneOptions
        {
            Combat = new Cranberry.Zone.Combat.CombatOptions { Enabled = false },
        });
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(family);
        uint ability = VehicleDriverControls.HeadlightsAbility(family);
        int mark = recorder.Sent.Count;
        session.Deliver(NativeDriverAbility(ability, 10, true, session.Guid + 1, car.Guid));
        session.Deliver(NativeDriverAbility(ability, 10, true, session.Guid, car.Guid + 1));
        session.Deliver(VehicleDriverControls.Ability(ability, 10, true)); // S2C is not a request.
        Assert.False(car.HeadlightsOn);
        Assert.Empty(Sub8(From(recorder, mark), 0x0f, 0x15));
        for (int i = 0; i < 3; i++)
        {
            session.Deliver(NativeDriverAbility(ability, 10, true, session.Guid, car.Guid));
            Assert.True(car.HeadlightsOn);
            session.Deliver(NativeDriverAbility(ability, 10, false, session.Guid, car.Guid));
            session.Deliver(NativeDriverAbility(ability, 10, false, session.Guid, car.Guid));
            Assert.False(car.HeadlightsOn);
            session.Deliver(NativeDriverAbility(1111301, 13, true, session.Guid, car.Guid));
            Assert.True(car.HornOn);
            session.Deliver(NativeDriverAbility(1111301, 13, false, session.Guid, car.Guid));
            Assert.False(car.HornOn);
        }
        Assert.Equal(6, Sub8(From(recorder, mark), 0x0f, 0x15).Count);
        Assert.Equal(6, Sub8(From(recorder, mark), 0x0f, 0x16).Count);
        Assert.Equal(9, Sub8(From(recorder, mark), 0xa0, 0x0f).Count);
    }

    [Fact]
    public void RunningMotorSurvivesInventoryRefreshStopsBeforeDismountAndReentryWaitsForInput()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        int mark = recorder.Sent.Count;
        session.RefreshInventory(car);
        session.RefreshInventory(car);
        session.RefreshInventory(car);
        var start = Assert.Single(Sub8(From(recorder, mark), 0xa0, 1));
        Assert.Equal(VehicleDriverControls.StartEngineRuntime(car, session.Guid), start[1..]);
        Assert.All(Sub8(From(recorder, mark), 0xa0, 0x0d), packet => Assert.Equal(11u,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(7))));
        mark = recorder.Sent.Count;
        session.Exit();
        var exited = From(recorder, mark).ToList();
        var stop = Assert.Single(Sub8(exited, 0xa0, 3));
        Assert.Equal(VehicleDriverControls.StopEngineRuntime(1), stop[1..]);
        Assert.True(exited.IndexOf(stop) < exited.FindIndex(p => p[1] == 0x70 && p[2] == 4));
        car.LastInteractionMs = long.MinValue;
        mark = recorder.Sent.Count;
        Assert.True(session.Enter(car.Guid));
        Assert.Empty(Sub8(From(recorder, mark), 0xa0, 1));
        Assert.DoesNotContain(Sub8(From(recorder, mark), 0x88, 0x1b), p => p[^1] != 0);
        Assert.False(car.EngineOn);
    }

    private static byte[] NativeDriverAbility(uint ability, uint key, bool on, ulong source, ulong target)
    {
        // Fresh 18:08:22.411/489 August horn packets. Only fixture actor/ability IDs change.
        byte[] bytes = Convert.FromHexString(on
            ? "A001010000000000000005F510000D000000211000000000000001000000000000000000000038000000000000460000000000000000171338C5A21993423B5026450000803F0001"
            : "A0030100000005F510000D000000");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(on ? 10 : 6), ability);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(on ? 14 : 10), key);
        if (on)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(18), source);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(38), target);
        }
        return bytes;
    }

    [Fact]
    public void NativeMotorInitFailureClearsRuntimeAndDoesNotLoopOnInventoryRefresh()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        session.RefreshInventory(car);
        byte[] failure = VehicleDriverControls.StartEngineRuntime(car, session.Guid);
        BinaryPrimitives.WriteUInt32LittleEndian(failure.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(failure.AsSpan(6), 11);
        int mark = recorder.Sent.Count;
        session.Deliver(failure);
        Assert.False(car.EngineOn);
        Assert.Single(Sub8(From(recorder, mark), 0xa0, 3));
        session.RefreshInventory(car);
        Assert.Empty(Sub8(From(recorder, mark), 0xa0, 1));
    }

    [Fact]
    public void MotorReleaseAfterExitIsAcknowledgedWithoutRestartingOrChangingTheCar()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        session.RefreshInventory(car);
        session.Exit();
        int mark = recorder.Sent.Count;
        session.Deliver(Remove(session.Guid, car.Guid, 90001, 100042));
        Assert.Single(Sub8(From(recorder, mark), 0x9e, 3));
        Assert.False(car.EngineOn);
        Assert.Equal(0ul, car.DriverGuid);
        Assert.Empty(Sub8(From(recorder, mark), 0x88, 0x1b));
    }

    [Fact]
    public void DriverExitKeepsPhysicsAfterFreshPosesSettleAndDuringSleep()
    {
        var (service, connection, recorder) = Admit();
        {
            var session = service.ForVehicleTest(connection);
            var car = session.EnterMatchWithCar();
            car.LastSpeed = 60f;
            int mark = recorder.Sent.Count;
            Assert.True(session.Exit());
            Assert.Equal(0ul, car.DriverGuid);
            Assert.Equal(session.Guid, car.CoastingOwnerGuid);
            Assert.False(car.EngineOn);
            Assert.Empty(Sub8(From(recorder, mark).ToList(), 0x0f, 0x3b));
            Assert.DoesNotContain(From(recorder, mark), p => p.Length >= 4 && p[1] == 0x11 && p[2] == 0x39 && p[3] == 0);
            using (var pose = new PacketWriter())
            {
                pose.WriteByte(0x90);
                ClientVarInt.Write(pose, car.TransientId);
                PositionUpdateBlock.AtRest(new(1, 0, 0), 0).WriteTo(pose);
                session.Deliver(pose.Written, channel: 3);
            }
            Assert.Equal(new Vector3(1, 0, 0), car.Position);
            long start = car.CoastStartedMs;
            Assert.True(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid, new(2, 0, 0), 0, start + 200, out _));
            Assert.True(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid, new(3, 0, 0), 0, start + 400, out _));
            session.PumpCoasting(start + 500);
            Assert.Equal(session.Guid, car.CoastingOwnerGuid);
            Assert.False(session.Fleet.TryApplyOwnerPose(car.TransientId, 99, new(3, 0, 0), 0, start + 600, out _));
            Assert.True(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid, new(3, 0, 0), 0, start + 800, out _));
            Assert.True(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid, new(3, 0, 0), 0, start + 1800, out _));
            session.PumpCoasting(start + 1800);
            Assert.Equal(session.Guid, car.CoastingOwnerGuid);
            Assert.Empty(Sub8(From(recorder, mark).ToList(), 0x0f, 0x3b));
            Assert.True(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid, new(4, 0, 0), 0, start + 2000, out _));
            session.PumpCoasting(start + 9000);
            Assert.Empty(Sub8(From(recorder, mark).ToList(), 0x0f, 0x3b));
        }
    }

    [Fact]
    public void PassengerExitDoesNotStopTheDriverOrRevokePhysics()
    {
        var (service, connection, recorder) = Admit();
        {
            var session = service.ForVehicleTest(connection);
            var car = session.EnterMatchWithCar(asDriver: false);
            Assert.Equal(VehicleActionResult.Ok, session.Fleet.TryEnter(car.Guid, 99, 0, 10000, out _, out _));
            car.LastInteractionMs = long.MinValue;
            car.EngineOn = true;
            int mark = recorder.Sent.Count;
            Assert.True(session.Exit());
            Assert.Equal(99ul, car.DriverGuid);
            Assert.True(car.EngineOn);
            var sent = From(recorder, mark).ToList();
            Assert.Empty(Sub8(sent, 0x88, VehicleEngine.SubOpcode));
            Assert.Empty(Sub8(sent, 0x0f, 0x3b));
            Assert.Empty(Sub8(sent, 0x88, 1));
        }
    }

    private sealed class RecordingRecorder : IPacketRecorder
    {
        public List<byte[]> Sent { get; } = [];

        public void RecordSession(IPEndPoint remote, in SessionRequest request)
        {
        }

        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            if (direction == "s2c")
            {
                Sent.Add(bytes.ToArray());
            }
        }

        public void RecordRaw(SoeConnection connection, long keystreamPosition, ReadOnlySpan<byte> ciphertext)
        {
        }
    }

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;

        public void Log(TransportLogLevel level, string message)
        {
        }
    }

    private static (ZoneService Service, SoeConnection Connection, RecordingRecorder Recorder) Admit(
        ZoneOptions? options = null, bool useDrivingTuning = false)
    {
        options ??= new ZoneOptions();
        // These fixtures test collision authority, wire packets and wreck transitions at their
        // original unscaled amounts. VehicleDrivingTuningTests exercise the shipped balance.
        if (!useDrivingTuning) options = options with { VehicleDamage = options.VehicleDamage with
        { MinimumCollisionDamage = 0, CollisionDamageMultiplier = 1, FlipDamageMultiplier = 1,
          FlipInitialGraceMs = 6000, MaximumCollisionDamage = options.VehicleDamage.MaximumCollisionDamage == 2000
            ? 20000 : options.VehicleDamage.MaximumCollisionDamage } };
        var tickets = new GatewayTicketRegistry();
        GatewayAdmission admission = tickets.Issue(
            0x1001, "Cranberry", gender: 2, headId: 3, hairId: 2, skinToneId: 664, profileId: 270);
        var recorder = new RecordingRecorder();
        var service = new ZoneService(new SilentLog(), recorder, tickets, options ?? new ZoneOptions());
        var request = new SessionRequest(3, 0x11223344, 512, ZoneService.ProtocolName);
        var connection = new SoeConnection(
            new IPEndPoint(IPAddress.Loopback, 5555),
            in request,
            SessionSettings.WithSeed(1),
            service.OnSessionRequest(new IPEndPoint(IPAddress.Loopback, 5555), in request),
            service,
            new SilentLog(),
            (_, _) => { },
            now: 0);
        service.OnConnected(connection);

        using var writer = new PacketWriter();
        writer.WriteByte(GatewayLoginRequest.Opcode);
        writer.WriteUInt64(admission.Guid);
        writer.WriteString(admission.Ticket);
        writer.WriteString(GatewayLoginRequest.AugustProtocol);
        writer.WriteString(GatewayLoginRequest.AugustVersion);
        service.OnMessage(connection, writer.Written.ToArray());
        return (service, connection, recorder);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RetailVehicleSelfReportsAndSeatedFallReportsCannotDamageTheRider(bool driver)
    {
        var (service, connection, _) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(asDriver: driver);
        session.Deliver(Collision(car.Guid, car.Guid, 2998, CollisionDamageCause.FallDamage));
        Assert.Equal(driver ? 97_002u : 100_000u, car.Health);
        Assert.Equal(10_000u, session.Hitpoints);
        session.Deliver(Collision(session.Guid, session.Guid, 10_000, CollisionDamageCause.FallDamage));
        Assert.Equal(10_000u, session.Hitpoints);
        session.Deliver(Collision(12345, 12345, 10_000, CollisionDamageCause.FallDamage));
        Assert.Equal(10_000u, session.Hitpoints);
    }

    [Fact]
    public void MountedInventoryPublishesCarItemsAbilitiesAndHonoursOneFuelTransfer()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(vehicleId: 3);
        int mark = recorder.Sent.Count;
        session.OpenInventory();
        var packets = From(recorder, mark).ToArray();
        var grant = Assert.Single(packets, p => p.Length > 20 && p[1] == 0xf0 && p[2] == 1
            && BitConverter.ToUInt64(p, 4) == car.Guid);
        Assert.Equal(session.Guid, BitConverter.ToUInt64(grant, 12));
        Assert.Single(Sub8(packets, 0xa0, 6));
        Assert.Contains(packets, p => p.Length > 20 && p[1] == 0x86 && p[2] == 4
            && BitConverter.ToUInt64(p, 3) == car.Guid);
        var fuel = Assert.Single(car.Inventory.Items, i => i.DefinitionId == 73);
        using var move = new PacketWriter();
        move.WriteByte(0xc8);
        move.WriteUInt16(1);
        move.WriteUInt64(session.Inventory!.BaseBag!.Guid);
        move.WriteUInt64(car.Guid);
        move.WriteUInt64(fuel.ItemGuid);
        move.WriteUInt64(session.Guid);
        move.WriteUInt32(1);
        move.WriteInt32(-1);
        session.Deliver(move.Written);
        Assert.False(car.Inventory.TryGet(fuel.ItemGuid, out _));
        Assert.Equal(1u, Assert.Single(session.Inventory.Items.Values, i => i.DefinitionId == 73).Count);
        session.Deliver(move.Written);
        Assert.Equal(1u, Assert.Single(session.Inventory.Items.Values, i => i.DefinitionId == 73).Count);
        session.Exit();
        Assert.Equal(new byte[] { 0xa0, 6, 0, 0, 0, 0 }, Sub8(From(recorder, mark), 0xa0, 6).Last()[1..]);
    }

    /// <summary>Byte 0 is the gateway tunnel header; the zone packet starts at 1.</summary>
    private static IEnumerable<byte[]> From(RecordingRecorder recorder, int mark) =>
        recorder.Sent.Skip(mark).Where(p => p.Length >= 2);

    private static List<byte[]> Sub8(IEnumerable<byte[]> packets, byte opcode, byte sub) =>
        [.. packets.Where(p => p.Length >= 3 && p[1] == opcode && p[2] == sub)];

    private static List<byte[]> Opcode(IEnumerable<byte[]> packets, byte opcode) =>
        [.. packets.Where(p => p.Length >= 2 && p[1] == opcode)];

    private static byte[] Collision(
        ulong character,
        ulong obj,
        uint damage,
        CollisionDamageCause cause,
        Vector3? at = null)
    {
        using var writer = new PacketWriter();
        new CollisionDamageReport(character, obj, damage, cause, at ?? Vector3.Zero).WriteTo(writer);
        return writer.Written.ToArray();
    }

    /// <summary><c>8d ResourceEvent</c> type 3: <c>u8 8d; u32 gameTime; u8 3; u64 subject; u32 id; u32 type; u32 value</c>.</summary>
    private static (uint ResourceId, uint Value) Resource(byte[] packet) =>
        (BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(15)),
         BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(23)));

    private static List<byte[]> ConditionRows(IEnumerable<byte[]> packets) =>
        [.. Opcode(packets, ZoneOpcodes.ResourceEventBase)
            .Where(p => p.Length >= 27
                && Resource(p).ResourceId == AugustVehicleDamageFacts.ConditionResourceId)];

    // ============================================================== 1. drive into something

    /// <summary>
    /// <b>The owner's click recipe, step 1: drive into a wall.</b> The client reports the crash as
    /// <c>8e 01</c> naming the CAR in <c>objectCharacterId</c>, and the car's condition drops by the
    /// client's own damage word — no divisor, no threshold, no cap.
    /// </summary>
    [Fact]
    public void ACrashReportDropsTheCarsConditionByTheClientsOwnNumber()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        MatchVehicle car = session.EnterMatchWithCar();
        int mark = recorder.Sent.Count;

        session.Deliver(Collision(car.Guid, car.Guid, 12_000, CollisionDamageCause.VehicleCollision));

        Assert.Equal(88_000u, car.Health);

        List<byte[]> sent = [.. From(recorder, mark)];
        byte[] condition = Assert.Single(ConditionRows(sent));
        Assert.Equal(88_000u, Resource(condition).Value);

        // The driver's own bar: 88 1e, the writer that had existed since wave 5 and had never once
        // been sent.
        byte[] bar = Assert.Single(
            Sub8(sent, ZoneOpcodes.VehicleBase, VehicleHealthUpdateOwner.SubOpcode));
        Assert.Equal(VehicleHealthUpdateOwner.Length, bar.Length - 1);
        Assert.Equal(session.Guid, BinaryPrimitives.ReadUInt64LittleEndian(bar.AsSpan(3)));
        Assert.Equal(88_000u, BinaryPrimitives.ReadUInt32LittleEndian(bar.AsSpan(11)));

        // The player is untouched: the CAR hit the wall.
        Assert.Equal(10_000u, session.Hitpoints);
    }

    /// <summary>
    /// A fall the client reports about ITSELF takes the player's health through the one production
    /// damage path, with the client's own number.
    /// </summary>
    [Fact]
    public void AFallReportTakesThePlayersHealth()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        session.EnterMatchWithCar(asDriver: false);
        Assert.True(session.Exit());
        int mark = recorder.Sent.Count;

        session.CollisionAt(Collision(session.Guid, session.Guid, 2_500, CollisionDamageCause.FallDamage),
            session.ExitProtectedUntilMs);

        Assert.Equal(7_500u, session.Hitpoints);
        Assert.NotEmpty(Sub8(From(recorder, mark).ToList(), ZoneOpcodes.ClientUpdateBase, 0x01));
    }

    /// <summary>
    /// <b>The ramp rule, end to end.</b> Four reports about one continuing fall — 7, 14, 22, 30 —
    /// cost 30 in total, not 73. This is the owner's round-25 fix and it is why decoding this packet
    /// is safe at all.
    /// </summary>
    [Fact]
    public void ARampOfFallReportsCostsItsPeakOnce()
    {
        (ZoneService service, SoeConnection connection, _) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        session.EnterMatchWithCar(asDriver: false);
        Assert.True(session.Exit());

        foreach (uint step in new uint[] { 7, 14, 22, 30 })
        {
            session.CollisionAt(Collision(session.Guid, session.Guid, step, CollisionDamageCause.FallDamage),
                session.ExitProtectedUntilMs);
        }

        Assert.Equal(10_000u - 30u, session.Hitpoints);
    }

    /// <summary>
    /// <b>Gate 3.</b> A fall report under the canopy is dropped. Without it the first thing this
    /// decode would do is kill every player on the drop — one recovered sample is a fall reporting
    /// 42,637 against a 10,000 bar.
    /// </summary>
    [Fact]
    public void AFallUnderTheCanopyIsSuppressed()
    {
        (ZoneService service, SoeConnection connection, _) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        session.EnterMatchWithCar(asDriver: false);
        Assert.True(session.Exit());
        session.UnderCanopy();

        session.Deliver(Collision(session.Guid, session.Guid, 42_637, CollisionDamageCause.FallDamage));

        Assert.Equal(10_000u, session.Hitpoints);
    }

    /// <summary><b>Gate 2.</b> Nothing inside the post-arrival grace counts: the client settles its actor and calls it an impact.</summary>
    [Fact]
    public void AReportInsideThePostArrivalGraceIsDropped()
    {
        (ZoneService service, SoeConnection connection, _) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        session.EnterMatchWithCar(asDriver: false);
        Assert.True(session.Exit());
        session.JustArrived();

        session.Deliver(Collision(session.Guid, session.Guid, 5_000, CollisionDamageCause.FallDamage));

        Assert.Equal(10_000u, session.Hitpoints);
    }

    /// <summary><b>Gate 1.</b> Nothing before the world release counts.</summary>
    [Fact]
    public void AReportBeforeTheWorldReleaseIsDropped()
    {
        (ZoneService service, SoeConnection connection, _) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        session.EnterMatchWithCar(asDriver: false);
        Assert.True(session.Exit());
        session.BeforeRelease();

        session.Deliver(Collision(session.Guid, session.Guid, 5_000, CollisionDamageCause.FallDamage));

        Assert.Equal(10_000u, session.Hitpoints);
    }

    /// <summary>
    /// A gas report is counted and never charged: the server owns the gas ladder, and billing the
    /// client's copy of the same damage would take it twice. 44 of the 47 live samples are this.
    /// </summary>
    [Fact]
    public void AGasReportIsCountedAndNeverCharged()
    {
        (ZoneService service, SoeConnection connection, _) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        session.EnterMatchWithCar(asDriver: false);
        Assert.True(session.Exit());

        session.Deliver(Collision(session.Guid, session.Guid, 28_963, CollisionDamageCause.ToxicGas));

        Assert.Equal(10_000u, session.Hitpoints);
        Assert.Equal(1, session.GasReports);
    }

    /// <summary>A cause nobody has ever seen is logged and dropped, never guessed at.</summary>
    [Fact]
    public void AnUnseenCauseIsDropped()
    {
        (ZoneService service, SoeConnection connection, _) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        session.EnterMatchWithCar(asDriver: false);

        session.Deliver(Collision(
            session.Guid, session.Guid, 9_000, CollisionDamageCause.ExplosiveDamage));

        Assert.Equal(10_000u, session.Hitpoints);
    }

    /// <summary>The one-word revert: with the switch off nothing moves at all.</summary>
    [Fact]
    public void TheCollisionSwitchOffMovesNothing()
    {
        (ZoneService service, SoeConnection connection, _) = Admit(new ZoneOptions
        {
            MatchEnd = MatchEndOptions.Default with { CollisionDamage = false },
        });
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        MatchVehicle car = session.EnterMatchWithCar();

        session.Deliver(Collision(car.Guid, car.Guid, 30_000, CollisionDamageCause.VehicleCollision));
        session.Deliver(Collision(session.Guid, session.Guid, 3_000, CollisionDamageCause.FallDamage));

        Assert.Equal(100_000u, car.Health);
        Assert.Equal(10_000u, session.Hitpoints);
    }

    // ================================================================ 2. shoot a car to bits

    /// <summary>
    /// <b>The owner's click recipe, step 2: shoot a car.</b> Driven through the fleet rather than
    /// through a synthetic <c>82 06</c>, because the hit report's own decode is pinned elsewhere —
    /// what is proven here is that the condition walks the whole ladder, plays each stage effect
    /// once, and ends with the driver out of a wreck that stays in the world.
    /// </summary>
    [Fact]
    public void ShootingACarWalksTheLadderThenWrecksItAndPutsTheDriverOut()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) = Admit(new ZoneOptions { VehicleDamage = new VehicleDamageOptions { MaximumCollisionDamage = uint.MaxValue } });
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        MatchVehicle car = session.EnterMatchWithCar();
        int mark = recorder.Sent.Count;

        // A test cannot wait out the 500 ms burst window on a wall clock, and it does not need to:
        // inside one window each report costs only what it ADDS to the running peak, so a rising
        // ramp is the ladder. 50,000 then 65,000 then 80,000 then 90,000 then 100,000 charges
        // 50,000 / 15,000 / 15,000 / 10,000 / 10,000 - which is exactly the owner's
        // 50 / 35 / 20 / 10 ladder and then zero.
        foreach (uint reported in new uint[] { 50_000, 65_000, 80_000, 90_000, 100_000 })
        {
            session.Deliver(Collision(
                car.Guid, car.Guid, reported, CollisionDamageCause.VehicleCollision));
        }

        List<byte[]> sent = [.. From(recorder, mark)];

        Assert.Equal(0u, car.Health);
        Assert.True(car.IsEmpty);
        Assert.Equal(0ul, car.OwnerGuid);
        Assert.False(car.EngineOn);

        // Each damage stage plays once, then the native corpse loop follows the model swap.
        uint[] effects = [.. Opcode(sent, ZoneOpcodes.CommandBase)
            .Where(p => p.Length == PlayDialogEffect.Length + 1
                && BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)) == PlayDialogEffect.SubOpcode)
            .Select(p => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(12)))];
        Assert.Equal<uint[]>([182, 181, 180, 5_227, 5_207], effects);

        // The driver came out through the same clearing burst a dismount sends, and the wreck is
        // still in the world.
        Assert.NotEmpty(Sub8(sent, ZoneOpcodes.MountBase, DismountResponse.SubOpcode));
        Assert.True(session.Fleet.TryGet(car.Guid, out _));
        Assert.Equal(1, session.Fleet.Count);

        // A zero-health vehicle now explodes and kills its occupant through normal death handling.
        Assert.Equal(0u, session.Hitpoints);
    }

    /// <summary>
    /// The last condition row a wreck sends is a zero, so the client's bar reaches empty rather than
    /// stopping one packet short of it.
    /// </summary>
    [Fact]
    public void AWreckSendsAZeroConditionRow()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) = Admit(new ZoneOptions { VehicleDamage = new VehicleDamageOptions { MaximumCollisionDamage = uint.MaxValue } });
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        MatchVehicle car = session.EnterMatchWithCar();
        int mark = recorder.Sent.Count;

        session.Deliver(Collision(
            car.Guid, car.Guid, 500_000, CollisionDamageCause.VehicleCollision));

        byte[] last = ConditionRows(From(recorder, mark).ToList())[^1];
        Assert.Equal(0u, Resource(last).Value);
    }

    // ========================================================================= 3. flip a car

    /// <summary>
    /// <b>The owner's click recipe, step 3: roll it onto its roof.</b> The pose the owning client
    /// streams is the only thing that can say a car is inverted, and each pulse is the client's own
    /// <c>UPSIDE_DOWN_DAMAGE_PULSE</c>.
    /// </summary>
    [Fact]
    public void AFlippedCarIsPulsedFromTheWorldPumpAndTheConditionRowGoesOut()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        MatchVehicle car = session.EnterMatchWithCar();

        session.Fleet.NoteAttitude(
            car,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI),
            new VehicleDamageOptions().UpsideDownDotThreshold);
        Assert.True(car.UpsideDown);

        int mark = recorder.Sent.Count;
        long start = car.LastFlipPulseMs;

        session.PumpDamage(start + 5_999);
        Assert.Equal(100_000u, car.Health);

        session.PumpDamage(start + 6_000);
        Assert.Equal(95_000u, car.Health);

        session.PumpDamage(start + 9_000);
        Assert.Equal(90_000u, car.Health);

        List<byte[]> rows = ConditionRows(From(recorder, mark).ToList());
        Assert.Equal(2, rows.Count);
        Assert.Equal(95_000u, Resource(rows[0]).Value);
        Assert.Equal(90_000u, Resource(rows[1]).Value);
    }

    /// <summary>Twenty pulses wreck an OffRoader, and the driver is put out of it.</summary>
    [Fact]
    public void EnoughPulsesWreckTheCar()
    {
        (ZoneService service, SoeConnection connection, _) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        MatchVehicle car = session.EnterMatchWithCar();

        session.Fleet.NoteAttitude(
            car,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI),
            new VehicleDamageOptions().UpsideDownDotThreshold);

        long at = car.LastFlipPulseMs + 3_000;
        for (int pulse = 0; pulse < 20; pulse++)
        {
            at += 3_000;
            session.PumpDamage(at);
        }

        Assert.Equal(0u, car.Health);
        Assert.True(car.IsEmpty);
    }

    // ============================================================================ 4. boost

    /// <summary>
    /// <b>The owner's click recipe, step 4: hold the boost.</b> The press answers with
    /// <c>0f 33 Character.Turbo</c> and the <c>VEH_Engine_Boost_*</c> tag; the release answers with
    /// the <c>9e 03</c> echo, and <b>the echo is the half that matters</b> — without it the client's
    /// own effect manager refuses press #2 and every press after it.
    /// </summary>
    [Fact]
    public void ABoostPressAnswersWithTurboAndTheReleaseEchoesTheEffectBack()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        MatchVehicle car = session.EnterMatchWithCar();
        int mark = recorder.Sent.Count;

        session.Deliver(Add(session.Guid, car.Guid, 90_000, 100_023));

        List<byte[]> pressed = [.. From(recorder, mark)];
        byte[] turbo = Assert.Single(Sub8(pressed, ZoneOpcodes.CharacterBase, CharacterTurbo.SubOpcode));
        Assert.Equal(CharacterTurbo.Length, turbo.Length - 1);
        Assert.Equal(0, turbo[^1]);
        byte[] tag = Assert.Single(
            Sub8(pressed, ZoneOpcodes.CharacterBase, AddEffectTagCompositeEffect.SubOpcode));
        Assert.Equal(5_016u, BinaryPrimitives.ReadUInt32LittleEndian(tag.AsSpan(11)));
        Assert.True(session.Boost.IsBoosting(car.Guid));
        Assert.Equal(1, session.Boost.Presses);

        mark = recorder.Sent.Count;
        session.Deliver(Remove(session.Guid, car.Guid, 90_000, 100_023));

        List<byte[]> released = [.. From(recorder, mark)];
        byte[] echo = Assert.Single(Opcode(released, ZoneOpcodes.EffectsBase));
        Assert.Equal(EffectRequest.RemoveLength, echo.Length - 1);
        Assert.Equal(EffectRequest.RemoveSub, echo[2]);
        // The swap: the car first, then the player.
        Assert.Equal(car.Guid, BinaryPrimitives.ReadUInt64LittleEndian(echo.AsSpan(15)));
        Assert.Equal(session.Guid, BinaryPrimitives.ReadUInt64LittleEndian(echo.AsSpan(23)));
        Assert.Single(Sub8(released, ZoneOpcodes.CharacterBase, RemoveEffectTagCompositeEffect.SubOpcode));
        Assert.False(session.Boost.IsBoosting(car.Guid));
        Assert.Equal(1, session.Boost.Releases);
    }

    /// <summary>
    /// Press, release, press again — six times. Every press is answered, which is the whole point:
    /// the failure this path exists to avoid is press #1 working and #2-#6 doing nothing.
    /// </summary>
    [Fact]
    public void SixPressesAreSixBoosts()
    {
        (ZoneService service, SoeConnection connection, _) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        MatchVehicle car = session.EnterMatchWithCar();

        for (int press = 0; press < 6; press++)
        {
            session.Deliver(Add(session.Guid, car.Guid, 90_000, 100_023));
            session.Deliver(Remove(session.Guid, car.Guid, 90_000, 100_023));
        }

        Assert.Equal(6, session.Boost.Presses);
        Assert.Equal(6, session.Boost.Releases);
        Assert.Equal(0, session.Boost.Refusals);
    }

    /// <summary>An empty tank refuses on <c>88 2b</c>, which is the only refusal channel the client reads.</summary>
    [Fact]
    public void AnEmptyTankRefusesOnEightEightTwoB()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        MatchVehicle car = session.EnterMatchWithCar(fuel: 0f);
        int mark = recorder.Sent.Count;

        session.Deliver(Add(session.Guid, car.Guid, 90_000, 100_023));

        List<byte[]> sent = [.. From(recorder, mark)];
        byte[] failed = Assert.Single(
            Sub8(sent, ZoneOpcodes.VehicleBase, VehicleActivateBoostFailed.SubOpcode));
        Assert.Equal(VehicleActivateBoostFailed.Length, failed.Length - 1);
        Assert.Equal(car.Guid, BinaryPrimitives.ReadUInt64LittleEndian(failed.AsSpan(3)));
        Assert.Empty(Sub8(sent, ZoneOpcodes.CharacterBase, CharacterTurbo.SubOpcode));
        Assert.Equal(1, session.Boost.Refusals);
        Assert.False(session.Boost.IsBoosting(car.Guid));
    }

    /// <summary>A passenger's boost key does nothing but a refusal.</summary>
    [Fact]
    public void APassengerCannotBoost()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        MatchVehicle car = session.EnterMatchWithCar(asDriver: false);
        int mark = recorder.Sent.Count;

        session.Deliver(Add(session.Guid, car.Guid, 90_000, 100_023));

        Assert.Single(Sub8(
            From(recorder, mark).ToList(), ZoneOpcodes.VehicleBase,
            VehicleActivateBoostFailed.SubOpcode));
        Assert.False(session.Boost.IsBoosting(car.Guid));
    }

    /// <summary>
    /// A held boost drains the tank at the ruled multiplier on top of the cruising rate the fuel
    /// pump already bills — the client's own <c>AbilityEx</c> rows say the tank is what a boost
    /// spends (<c>RESOURCE_TYPE 50</c>).
    /// </summary>
    [Fact]
    public void AHeldBoostDrainsTheTankFaster()
    {
        (ZoneService service, SoeConnection connection, _) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        MatchVehicle car = session.EnterMatchWithCar(fuel: 5_000f);

        session.Deliver(Add(session.Guid, car.Guid, 90_000, 100_023));
        session.PumpDamage(Environment.TickCount64, billedSeconds: 10f);

        // Ten seconds of boost at (4 - 1) x 8 units/s on top of whatever the fuel pump billed.
        Assert.Equal(5_000f - (3f * 8f * 10f), car.Fuel, 1);
    }

    /// <summary>A boost held until the tank runs dry ends itself, and says so on <c>88 2b</c>.</summary>
    [Fact]
    public void ABoostHeldUntilTheTankIsEmptyEndsItself()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        MatchVehicle car = session.EnterMatchWithCar(fuel: 50f);

        session.Deliver(Add(session.Guid, car.Guid, 90_000, 100_023));
        int mark = recorder.Sent.Count;
        session.PumpDamage(Environment.TickCount64, billedSeconds: 10f);

        Assert.Equal(0f, car.Fuel);
        Assert.False(session.Boost.IsBoosting(car.Guid));
        Assert.Single(Sub8(
            From(recorder, mark).ToList(), ZoneOpcodes.VehicleBase,
            VehicleActivateBoostFailed.SubOpcode));
    }

    /// <summary>The one-word revert: with the switch off the press is logged and unanswered.</summary>
    [Fact]
    public void TheBoostSwitchOffAnswersNothing()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) = Admit(
            new ZoneOptions { VehicleBoost = new VehicleBoostOptions { Enabled = false } });
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        MatchVehicle car = session.EnterMatchWithCar();
        int mark = recorder.Sent.Count;

        session.Deliver(Add(session.Guid, car.Guid, 90_000, 100_023));

        List<byte[]> sent = [.. From(recorder, mark)];
        Assert.Empty(Sub8(sent, ZoneOpcodes.CharacterBase, CharacterTurbo.SubOpcode));
        Assert.Empty(Opcode(sent, ZoneOpcodes.EffectsBase));
        Assert.Equal(0, session.Boost.Presses);
    }

    /// <summary>Leaving the car takes the boost off before the car stops being the player's.</summary>
    [Fact]
    public void LeavingTheCarEndsTheBoost()
    {
        (ZoneService service, SoeConnection connection, _) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        MatchVehicle car = session.EnterMatchWithCar();

        session.Deliver(Add(session.Guid, car.Guid, 90_000, 100_023));
        Assert.True(session.Boost.IsBoosting(car.Guid));

        Assert.True(session.Exit());
        Assert.Equal(-1, car.SeatOf(session.Guid));
        Assert.False(session.Boost.IsBoosting(car.Guid));
    }

    private static byte[] Add(ulong player, ulong vehicle, uint clientEffect, uint serverEffect)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(ZoneOpcodes.EffectsBase);
        writer.WriteByte(EffectRequest.AddSub);
        writer.WriteUInt32(1);
        writer.WriteUInt32(clientEffect);
        writer.WriteUInt32(serverEffect);
        writer.WriteUInt64(player);
        writer.WriteUInt64(vehicle);
        return writer.Written.ToArray();
    }

    private static byte[] Remove(ulong player, ulong vehicle, uint clientEffect, uint serverEffect)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(ZoneOpcodes.EffectsBase);
        writer.WriteByte(EffectRequest.RemoveSub);
        writer.WriteUInt32(1);
        writer.WriteUInt32(clientEffect);
        writer.WriteUInt32(serverEffect);
        writer.WriteUInt64(player);
        writer.WriteUInt64(vehicle);
        writer.WriteUInt64(0);
        for (int index = 0; index < 4; index++)
        {
            writer.WriteSingle(0f);
        }

        return writer.Written.ToArray();
    }

    // ==================================================================== 5. the seat change

    [Theory]
    [InlineData(1u)] [InlineData(2u)] [InlineData(3u)] [InlineData(5u)]
    public void MovingToAPassengerSeatClearsTheNativeEngineFlagWithoutReleasingPhysics(uint family)
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(family);
        session.RefreshInventory(car);
        int mark = recorder.Sent.Count;
        session.Deliver(SeatChange(car.Guid, seat: 1));
        Assert.False(car.EngineOn);
        Assert.Equal(session.Guid, car.CoastingOwnerGuid);
        var sent = From(recorder, mark).ToList();
        byte[] engine = Assert.Single(Sub8(sent, 0x88, VehicleEngine.SubOpcode));
        Assert.Equal(0, engine[^1]);
        Assert.Contains(Sub8(sent, 0xa0, 3), p => BitConverter.ToUInt32(p, 3) == 3u);
        Assert.Empty(Sub8(sent, 0x0f, 0x3b));
        Assert.DoesNotContain(sent, p => p[1] == 0x11 && p[2] is 0x23 or 0x39);
    }

    /// <summary>
    /// <c>70 0a</c> was unhandled before this lane. The body is a candidate, so a request whose seat
    /// resolves on the car the player is in is answered with <c>70 0b SeatChangeResponse</c> and the
    /// two occupancy rows, and one whose seat does not resolve is refused rather than acted on.
    /// </summary>
    [Fact]
    public void ASeatChangeMovesTheRiderAndAnswersWithSeventyZeroB()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        MatchVehicle car = session.EnterMatchWithCar();
        int mark = recorder.Sent.Count;

        session.Deliver(SeatChange(car.Guid, seat: 2));

        Assert.Equal(2, car.SeatOf(session.Guid));
        Assert.Equal(0ul, car.OwnerGuid);

        List<byte[]> sent = [.. From(recorder, mark)];
        byte[] response = Assert.Single(
            Sub8(sent, ZoneOpcodes.MountBase, SeatChangeResponse.SubOpcode));
        Assert.Equal(SeatChangeResponse.Length, response.Length - 1);
        Assert.Equal(session.Guid, BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(3)));
        Assert.Equal(car.Guid, BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(11)));
        Assert.NotEmpty(Sub8(sent, ZoneOpcodes.VehicleBase, VehicleOwnerState.SubOpcode));
        Assert.NotEmpty(Sub8(sent, ZoneOpcodes.VehicleBase, VehicleOccupantState.SubOpcode));
    }

    /// <summary>A seat the car does not have is evidence the candidate body is wrong, not an instruction.</summary>
    [Fact]
    public void AnImpossibleSeatIsRefusedRatherThanActedOn()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) = Admit();
        ZoneService.VehicleTestSession session = service.ForVehicleTest(connection);
        MatchVehicle car = session.EnterMatchWithCar();
        int mark = recorder.Sent.Count;

        session.Deliver(SeatChange(car.Guid, seat: 1_633_772_861));

        Assert.Equal(0, car.SeatOf(session.Guid));
        Assert.Empty(Sub8(From(recorder, mark).ToList(), ZoneOpcodes.MountBase, SeatChangeResponse.SubOpcode));
    }

    private static byte[] SeatChange(ulong guid, uint seat)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(ZoneOpcodes.MountBase);
        writer.WriteByte(SeatChangeRequest.SubOpcode);
        writer.WriteUInt64(guid);
        writer.WriteUInt32(seat);
        return writer.Written.ToArray();
    }
}
