using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone.Movement;

public sealed class LookPitchTests
{
    [Fact]
    public void DownwardAimSurvivesSparsePositionButCannotComeFromAPreciseQuaternionOrAnOldWorld()
    {
        var state = new SessionMovementState();
        using var look = new PacketWriter();
        new PositionUpdateBlock(0x0200, [0, -157, 0, 0]).WriteTo(look);
        Assert.Equal(-1.57f, state.ApplyPlayer(ClientMovementUpdate.Parse(look.Written)).LookPitch);
        using var move = new PacketWriter();
        PositionUpdateBlock.AtRest(new(20, 30, 40), 1).WriteTo(move);
        Assert.Equal(-1.57f, state.ApplyPlayer(ClientMovementUpdate.Parse(move.Written)).LookPitch);
        using var precise = new PacketWriter();
        new PositionUpdateBlock(0x1000, [2000, 3000, 4000, 0, 70, 0, 70]).WriteTo(precise);
        Assert.Equal(-1.57f, state.ApplyPlayer(ClientMovementUpdate.Parse(precise.Written)).LookPitch);
        state.ResetPlayerWorldPose();
        Assert.Null(state.ApplyPlayer(ClientMovementUpdate.Parse(precise.Written)).LookPitch);
        Assert.Null(state.ApplyPlayer(ClientMovementUpdate.Parse(move.Written)).LookPitch);
    }

    [Fact]
    public void CapturedAugustLookPitchMatchesTheSignedBulletElevation()
    {
        // wire-20260908-205510.txt line 10570: lookInfo (3.10,-0.18,0,0),
        // bullet direction (0.04552857,-0.17384519,-0.98372000).
        using var look = new PacketWriter();
        new PositionUpdateBlock(0x0200, [310, -18, 0, 0]).WriteTo(look);
        var state = new SessionMovementState();
        float pitch = state.ApplyPlayer(ClientMovementUpdate.Parse(look.Written)).LookPitch!.Value;
        Assert.InRange(MathF.Abs(MathF.Sin(pitch) - -0.17384519f), 0, 0.01f);
    }
}
