using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Harness.Protocol;
using Cranberry.NetworkBots;
namespace Cranberry.Harness.Tests;
public sealed class AirborneCohortTests
{
    private static BotObservation Create()
    {
        var o=new BotObservation { Tick=()=>2000,SelfGuid=()=>1 };
        o.Canopies[100]=new(0xd7,100,16,9374,Vector3.Zero);
        o.Canopies[101]=new(0xd7,101,17,9374,Vector3.Zero);
        o.MountedRiders[2]=100;o.MountedRiders[3]=101;o.Peers[2]=18;
        o.BeginMovementWindow(1000,true);return o;
    }
    private static void Pose(BotObservation o,uint tick,byte id)
    {
        byte[] p=[5,0x78,(byte)(id<<2),0,0,0,0,0,0,0];
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(5),tick);
        o.Observe(ObservedPacket.ParseGateway(p),TimeSpan.Zero);
    }
    private static void Remove(BotObservation o,ulong guid)
    {
        byte[] p=[5,0x0f,1,0,0,0,0,0,0,0,0];
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(3),guid);
        o.Observe(ObservedPacket.ParseGateway(p),TimeSpan.Zero);
    }
    [Fact]
    public void ActorSpamCannotHideMissingCapturedCanopy()
    {
        var o=Create();Pose(o,1100,16);
        for(uint tick=1100;tick<1200;tick++)Pose(o,tick,18);
        var w=o.MovementWindow(2000,true);
        Assert.Equal(2,w.StartingAirborneCohortSize);Assert.Equal(2,w.ExpectedPeers);
        Assert.Equal(0,w.MinimumFreshPairHz);Assert.Equal(.5,w.FreshPeerCoverage);Assert.False(w.AirborneCohortChanged);
    }
    [Fact]
    public void RemovalCannotShrinkExpectedCohortAndMutationStaysStickyAfterRestore()
    {
        var o=Create();Remove(o,101);
        var w=o.MovementWindow(2000,true);Assert.Equal(2,w.ExpectedPeers);Assert.True(w.AirborneCohortChanged);
        o.Canopies[101]=new(0xd7,101,17,9374,Vector3.Zero);
        Assert.True(o.MovementWindow(2000,true).AirborneCohortChanged);
    }
    [Fact]
    public void SameIdentityRefreshPreservesHighWaterButRebindMarksCohort()
    {
        var o=Create();Pose(o,1500,16);
        o.Canopies[100]=o.Canopies[100] with { Position=Vector3.One };
        Pose(o,1500,16);Pose(o,1400,16);Pose(o,1600,16);
        Assert.False(o.MovementWindow(2000,true).AirborneCohortChanged);
        Assert.Equal(2,o.MovementWindow(2000,true).FreshSamples);
        o.Canopies[100]=o.Canopies[100] with { TransientId=19 };
        var w=o.MovementWindow(2000,true);Assert.True(w.AirborneCohortChanged);Assert.Equal(2,w.ExpectedPeers);
    }
    [Fact]
    public void WorldResetCannotEraseActiveCohortFailure()
    {
        var o=Create();o.ResetMatchView();var w=o.MovementWindow(2000,true);
        Assert.True(w.AirborneCohortChanged);Assert.Equal(2,w.StartingAirborneCohortSize);
        o.BeginMovementWindow(2000);Assert.Null(o.MovementWindow(2001,false).StartingAirborneCohortSize);
    }
}
