using Cranberry.NetworkBots;
namespace Cranberry.Harness.Tests;
public sealed class MovementEmissionScheduleTests
{
    [Theory]
    [InlineData(60,6)]
    [InlineData(60,60)]
    [InlineData(30,60)]
    public void IndependentDeadlinesProduceExactCounts(int playerHz,int canopyHz)
    {
        var schedule=new MovementEmissionSchedule(1000,playerHz,canopyHz);
        int players=0,canopies=0,canopyOnly=0;
        while(schedule.NextDeadlineTicks<=10000)
        {
            long due=schedule.NextDeadlineTicks;
            Assert.True(schedule.TryTake(due,out var batch));
            Assert.Equal(due,batch.ElapsedTicks);
            if(batch.PlayerDue)players++;
            if(batch.CanopyDue)canopies++;
            if(batch.CanopyDue&&!batch.PlayerDue)canopyOnly++;
        }
        Assert.Equal(playerHz*10,players);Assert.Equal(canopyHz*10,canopies);
        Assert.Equal(0,schedule.Player.SkippedFrames);Assert.Equal(0,schedule.Canopy!.SkippedFrames);
        Assert.Equal(playerHz<canopyHz?300:0,canopyOnly);
    }
    [Fact]
    public void StallSkipsBothStreamsWithoutCatchupAndBackwardTimeCannotMutate()
    {
        var schedule=new MovementEmissionSchedule(1000,30,60);
        Assert.True(schedule.TryTake(500,out var batch));
        Assert.True(batch.PlayerDue&&batch.CanopyDue);
        Assert.Equal(14,schedule.Player.SkippedFrames);Assert.Equal(29,schedule.Canopy!.SkippedFrames);
        Assert.Equal(466,schedule.Player.MaximumLatenessTicks);Assert.Equal(483,schedule.Canopy.MaximumLatenessTicks);
        Assert.False(schedule.TryTake(500,out _));
        long next=schedule.NextDeadlineTicks;
        Assert.Throws<ArgumentOutOfRangeException>(()=>schedule.TryTake(499,out _));
        Assert.Equal(next,schedule.NextDeadlineTicks);Assert.Equal(1,schedule.Player.EmittedFrames);Assert.Equal(1,schedule.Canopy.EmittedFrames);
        Assert.True(schedule.TryTake(517,out batch));Assert.False(batch.PlayerDue);Assert.True(batch.CanopyDue);
    }
    [Fact]
    public void OptionalCanopyDoesNotAlterPlayerOnlySchedule()
    {
        var single=new MovementEmissionSchedule(1000,60);
        var paired=new MovementEmissionSchedule(1000,60,6);
        for(long now=0;now<=1000;now++)
        {
            single.TryTake(now,out var a);paired.TryTake(now,out var b);
            Assert.Equal(a.PlayerDue,b.PlayerDue);Assert.False(a.CanopyDue);
        }
        Assert.Null(single.Canopy);
        Assert.Throws<ArgumentOutOfRangeException>(()=>new MovementEmissionSchedule(1000,60,0));
    }
}
