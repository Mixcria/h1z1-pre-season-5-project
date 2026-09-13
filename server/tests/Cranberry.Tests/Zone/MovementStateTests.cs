using System.Numerics;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class MovementStateTests
{
    private const string StationaryCapture = "020018F6B21C00000000";
    private const string FallingCapture =
        "FF01C5F5B21C0084009B180000000000000000EB04EA04000000";
    private const string FullParachuteCapture =
        "9008FF1F2F2F1F00002511BDDA021C7E189DB73B0000000000000000000000002203220322032203000000000000000000";

    [Fact]
    public void SparsePlayerUpdatesMergeWithoutDiscardingEarlierFields()
    {
        var state = new SessionMovementState();
        ClientMovementUpdate falling = ClientMovementUpdate.Parse(Convert.FromHexString(FallingCapture));
        ClientMovementUpdate stationary = ClientMovementUpdate.Parse(Convert.FromHexString(StationaryCapture));

        state.ApplyPlayer(falling);
        EntityMovementState merged = state.ApplyPlayer(stationary);

        Assert.Equal(stationary.ClientTime, merged.ClientTime);
        Assert.Equal(Vector3.Zero, merged.Position);
        Assert.Equal(33u, merged.Posture);
        Assert.InRange(merged.VerticalSpeed!.Value, -1.571f, -1.569f);
        Assert.InRange(merged.HorizontalSpeed!.Value, 15.699f, 15.701f);
        Assert.Same(stationary, merged.LastUpdate);
    }

    [Fact]
    public void PrecisePoseReplacesAnOlderOrdinaryPosition()
    {
        var state = new SessionMovementState();
        state.ApplyPlayer(ClientMovementUpdate.Parse(Convert.FromHexString(StationaryCapture)));
        ClientMovementUpdate precise = ClientMovementUpdate.Parse(
            Convert.FromHexString("00100000000000EA03D307BA0B0000002203"));

        EntityMovementState merged = state.ApplyPlayer(precise);

        Assert.Equal(new Vector3(1.25f, -2.5f, 3.75f), merged.Position);
        Assert.Equal(Quaternion.Identity, merged.Rotation);
    }

    [Fact]
    public void ManagedUpdatesRequireServerRegisteredOwnership()
    {
        var state = new SessionMovementState();
        ClientManagedMovementUpdate update = ClientManagedMovementUpdate.Parse(
            Convert.FromHexString(FullParachuteCapture));

        Assert.False(state.TryApplyManaged(update, out _));
        Assert.Equal(0, state.ManagedEntityCount);

        state.RegisterManagedEntity(transientId: 2, guid: 0x2001);

        Assert.True(state.TryApplyManaged(update, out ManagedEntityMovementState? chute));
        Assert.Equal(0x2001ul, chute.Guid);
        Assert.Equal(new Vector3(-233.83f, 2006.43f, -4892.03f), chute.Movement?.Position);
        Assert.True(state.TryPromoteManagedPoseToPlayer(2));
        Assert.Equal(new Vector3(-233.83f, 2006.43f, -4892.03f), state.Player?.Position);
    }

    [Fact]
    public void ManagedRegistrationIsIdempotentButCannotBeReassignedSilently()
    {
        var state = new SessionMovementState();

        state.RegisterManagedEntity(2, 0x2001);
        state.RegisterManagedEntity(2, 0x2001);

        Assert.Throws<InvalidOperationException>(() => state.RegisterManagedEntity(2, 0x3001));
        Assert.False(state.TryPromoteManagedPoseToPlayer(2));
        Assert.True(state.RemoveManagedEntity(2));
        Assert.False(state.TryGetManaged(2, out _));
        Assert.False(state.TryPromoteManagedPoseToPlayer(2));
    }

    [Fact]
    public void DismountHandoffRejectsTheCapturedZeroPoseUntilRealChannelTwoMovementReturns()
    {
        const string staleZeroPose = "011202968F0000051150D3030000000000F21E157E0141843801";
        const string firstRealPose =
            "FF1F2F968F0000A60008F5CF05AC0C032D302E3F3D0ABE000068EA04D2010000006900D20200805000000030258A015B0300";

        var state = new SessionMovementState();
        state.RegisterManagedEntity(2, 0x2001);
        Assert.True(state.TryApplyManaged(
            ClientManagedMovementUpdate.Parse(Convert.FromHexString(FullParachuteCapture)),
            out _));

        Assert.True(state.BeginPostDismountPoseHandoff(2));
        Assert.True(state.AwaitingPostDismountPose);
        Assert.Equal(0, state.ManagedEntityCount);
        Vector3 promoted = state.Player!.Position!.Value;

        Assert.False(state.TryApplyPlayer(
            ClientMovementUpdate.Parse(Convert.FromHexString(staleZeroPose)),
            out _));
        Assert.True(state.AwaitingPostDismountPose);
        Assert.Equal(promoted, state.Player!.Position);

        Assert.True(state.TryApplyPlayer(
            ClientMovementUpdate.Parse(Convert.FromHexString(firstRealPose)),
            out EntityMovementState? resumed));
        Assert.False(state.AwaitingPostDismountPose);
        Assert.InRange(resumed.Position!.Value.X, -476.15f, -476.13f);
        Assert.InRange(resumed.Position.Value.Y, 249.80f, 249.82f);
        Assert.InRange(resumed.Position.Value.Z, -3783.74f, -3783.72f);
    }

    [Fact]
    public void ParachuteTouchdownRequiresARealDescentThenTwoSettledIntervals()
    {
        var detector = new ParachuteTouchdownDetector();
        detector.Reset(spawnY: 2_000f);

        Assert.False(detector.Observe(1_000, new Vector3(0, 2_000, 0)));
        Assert.False(detector.Observe(2_000, new Vector3(0, 1_955, 0)));
        Assert.True(detector.DescentObserved);
        Assert.False(detector.Observe(3_000, new Vector3(0, 1_910, 0)));
        Assert.False(detector.Observe(40_000, new Vector3(0, 250.0f, 0))); // discontinuous gap
        Assert.False(detector.Observe(41_000, new Vector3(0, 249.5f, 0)));
        Assert.True(detector.Observe(42_000, new Vector3(0, 249.2f, 0)));
        Assert.False(detector.Observe(43_000, new Vector3(0, 249.1f, 0)));
    }

    [Fact]
    public void ParachuteTouchdownDoesNotMistakeSpawnPauseOrSparseGapsForLanding()
    {
        var detector = new ParachuteTouchdownDetector();
        detector.Reset(spawnY: 2_000f);

        Assert.False(detector.Observe(1_000, new Vector3(0, 2_000, 0)));
        Assert.False(detector.Observe(2_000, new Vector3(0, 2_000, 0)));
        Assert.False(detector.Observe(3_000, new Vector3(0, 2_000, 0)));
        Assert.False(detector.DescentObserved);

        Assert.False(detector.Observe(4_000, new Vector3(0, 1_950, 0)));
        Assert.False(detector.Observe(20_000, new Vector3(0, 1_950, 0)));
        Assert.False(detector.Observe(21_000, new Vector3(0, 1_950, 0)));
        Assert.True(detector.Observe(22_000, new Vector3(0, 1_950, 0)));
    }
}
