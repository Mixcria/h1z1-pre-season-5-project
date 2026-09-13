using Cranberry.Zone;

namespace Cranberry.Tests.Zone.Movement;

public sealed class MovementWorldResetTests
{
    [Fact]
    public void ResetDropsEveryPlayerFieldAndDismountGateButPreservesManagedOwnership()
    {
        var state = new SessionMovementState();
        // Captured August managed pose, also used by MovementStateTests.
        var managed = ClientManagedMovementUpdate.Parse(Convert.FromHexString(
            "9008FF1F2F2F1F00002511BDDA021C7E189DB73B0000000000000000000000002203220322032203000000000000000000"));
        state.RegisterManagedEntity(2, 0x2001);
        state.RegisterManagedEntity(3, 0x2002);
        Assert.True(state.TryApplyManaged(managed, out _));
        Assert.True(state.BeginPostDismountPoseHandoff(2));
        Assert.NotNull(state.Player);
        Assert.True(state.AwaitingPostDismountPose);

        state.ResetPlayerWorldPose();

        Assert.Null(state.Player);
        Assert.False(state.AwaitingPostDismountPose);
        Assert.True(state.TryGetManaged(3, out var retained));
        Assert.Equal(0x2002ul, retained.Guid);
        Assert.False(state.TryApplyManaged(managed, out _)); // removed ownership stays removed
        var header = ClientMovementUpdate.Parse([0, 0, 0, 0, 0, 0, 17]);
        Assert.True(state.TryApplyPlayer(header, out var fresh));
        Assert.Equal(EntityMovementState.Apply(null, header), fresh);
        Assert.Null(fresh.Position);
        Assert.Null(fresh.Posture);
        Assert.Null(fresh.Scalar140);
        Assert.Null(fresh.Scalar144);
    }
}
