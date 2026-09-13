using Cranberry.Zone.Gas;
using Cranberry.Zone.Match;
using Cranberry.Tests.Zone.MatchDrop;
using Xunit.Abstractions;

namespace Cranberry.Tests.Zone.Gas;

public sealed class ResumeGasTests(ITestOutputHelper output)
{
    [Fact]
    public void MeasuredTargetLadderStillAllowsASafeDrop()
    {
        var schedule = GasSchedule.Create(new GasSettings(), 0x123456789ABCDEF0UL);
        Assert.Equal(8000f, schedule.InitialCircle.Radius);
        Assert.Equal(new float[] { 2000, 1400, 900, 625, 300, 137.5f, 75, 60, 45, 40 },
            schedule.Phases.Select(p => p.Target.Radius));
        Assert.False(schedule.IsLethalAt(369999));
        Assert.True(schedule.IsLethalAt(370000));
        foreach (var phase in schedule.Phases)
            output.WriteLine($"PHASE {phase.Index} {phase.RevealAtMs} {phase.ShrinkStartAtMs} {phase.ClosedAtMs}");
        var first = GasSchedule.Create(new GasSettings(), MatchSeeds.For(2, MatchSeeds.GasSalt)).Phase(1).Target;
        var drop = DropPlanner.Plan(DropFixture.Places, first, DropOptions.Default, 2)!;
        Assert.NotNull(drop);
        output.WriteLine($"DROP {drop}");
    }
}
