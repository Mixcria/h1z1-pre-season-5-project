using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Combat;

// Reuse the shared-world combat fixture and its routed packet recorder.
public sealed partial class LivePlayerCombatTests
{
    [Fact]
    public void AnimatedRightingHandsPhysicsToARemainingViewerAfterTheInitiatorLeaves()
    {
        using var f = new Fixture();
        f.Service.Post = _ => { }; // Advance the production animation step explicitly.
        var initiator = f.Add(1, Vector3.Zero);
        var viewer = f.Add(2, Vector3.Zero);
        var otherMatch = f.Add(3, Vector3.Zero, matchId: 2);
        f.Add(4, new Vector3(1000, 0, 0)); // Keep the shared match live after the initiator leaves.
        var car = AddFeedbackVehicle(f, initiator);
        var fleet = Get<VehicleFleet>(initiator.Tag!, "Fleet");
        Call(f.Service, "EnsureVehicleFleet", viewer, viewer.Tag, false);
        Get<MatchVehicleStream>(viewer.Tag!, "StreamedVehicles").NoteSpawned(car.Guid, car.Position);
        fleet.NoteAttitude(car, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI), -0.25f);

        int mark = f.Recorder.Routed.Count;
        Assert.True(f.Service.ForVehicleTest(initiator).Enter(car.Guid));
        long started = car.LastRightingMs;
        Assert.True(f.Service.AdvanceVehicleRighting(car, started + ZoneService.VehicleRightingDurationMs / 2));
        Assert.Equal(0ul, car.CoastingOwnerGuid);
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), r => IsRightingPhysicsGrant(r.Packet));
        Assert.Single(f.Recorder.Routed.Skip(mark), r => r.Connection == viewer
            && IsRightingTransform(r.Packet) && MathF.Abs(BitConverter.ToSingle(r.Packet, 16)) > 0);

        Call(f.Service, "LeaveSharedLoot", initiator.Tag);
        mark = f.Recorder.Routed.Count;
        Assert.False(f.Service.AdvanceVehicleRighting(car, started + ZoneService.VehicleRightingDurationMs));

        Assert.Equal(Get<ulong>(viewer.Tag!, "Guid"), car.CoastingOwnerGuid);
        Assert.True(Get<SessionMovementState>(viewer.Tag!, "Movement").TryGetManaged(car.TransientId, out _));
        Assert.False(Get<SessionMovementState>(initiator.Tag!, "Movement").TryGetManaged(car.TransientId, out _));
        var completion = f.Recorder.Routed.Skip(mark).ToArray();
        Assert.DoesNotContain(completion, r => r.Connection == initiator || r.Connection == otherMatch);
        var sent = completion.Where(r => r.Connection == viewer).Select(r => r.Packet).ToArray();
        Assert.Single(sent, IsRightingPhysicsGrant);
        int finalPose = Array.FindIndex(sent, IsRightingTransform);
        int grant = Array.FindIndex(sent, IsRightingPhysicsGrant);
        Assert.True(finalPose >= 0 && finalPose < grant);
        Assert.Equal(2, sent.Take(grant).Count(p => p.Length > 1 && p[1] == 0x78));
        Assert.Equal(0.75f, car.Position.Y, 3);
        Assert.Equal(1f, VehicleFlipDetector.UpDot(car.LastRotation!.Value), 4);
        Assert.False(car.UpsideDown);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnimatedRightingCompletesWithoutGrantWhenNoLiveViewerHoldsTheVehicle(bool secondViewerDead)
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var initiator = f.Add(1, Vector3.Zero);
        var viewer = f.Add(2, Vector3.Zero);
        var car = AddFeedbackVehicle(f, initiator);
        var fleet = Get<VehicleFleet>(initiator.Tag!, "Fleet");
        Call(f.Service, "EnsureVehicleFleet", viewer, viewer.Tag, false);
        Get<MatchVehicleStream>(viewer.Tag!, "StreamedVehicles").NoteSpawned(car.Guid, car.Position);
        fleet.NoteAttitude(car, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI), -0.25f);
        Assert.True(f.Service.ForVehicleTest(initiator).Enter(car.Guid));
        long started = car.LastRightingMs;
        Assert.True(f.Service.AdvanceVehicleRighting(car, started + ZoneService.VehicleRightingDurationMs / 2));

        Set(initiator.Tag!, "DeathSent", true);
        if (secondViewerDead) Set(viewer.Tag!, "DeathSent", true);
        else Get<MatchVehicleStream>(viewer.Tag!, "StreamedVehicles").Clear();
        int mark = f.Recorder.Routed.Count;
        Assert.False(f.Service.AdvanceVehicleRighting(car, started + ZoneService.VehicleRightingDurationMs));

        Assert.False(car.UpsideDown);
        Assert.Equal(1f, VehicleFlipDetector.UpDot(car.LastRotation!.Value), 4);
        Assert.Equal(0.75f, car.Position.Y, 3);
        Assert.Equal(0ul, car.CoastingOwnerGuid);
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), r => IsRightingPhysicsGrant(r.Packet));
        Assert.Single(f.Recorder.Routed.Skip(mark), r => r.Connection == initiator && IsRightingTransform(r.Packet));
        foreach (var connection in new[] { initiator, viewer })
            Assert.False(Get<SessionMovementState>(connection.Tag!, "Movement").TryGetManaged(car.TransientId, out _));
        if (!secondViewerDead)
            Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), r => r.Connection == viewer);
        mark = f.Recorder.Routed.Count;
        Assert.False(f.Service.AdvanceVehicleRighting(car, started + ZoneService.VehicleRightingDurationMs + 40));
        Assert.Equal(mark, f.Recorder.Routed.Count);
    }

    private static bool IsRightingPhysicsGrant(byte[] packet) => packet.Length == ManagedObjectResponseControl.Length + 1
        && packet[1] == 0x11 && packet[2] == 0x39 && packet[3] == 0 && packet[4] == 1;

    private static bool IsRightingTransform(byte[] packet) => packet.Length == VehicleManagedLocation.Length + 1
        && packet[1] == 0x11 && packet[2] == 0x23 && packet[3] == 0;
}
