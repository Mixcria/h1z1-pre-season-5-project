using Cranberry.NetworkBots;
using Cranberry.Zone.Match;

namespace Cranberry.Harness.Tests;

public sealed class NetworkBotFixtureCapacityTests
{
    [Theory]
    [InlineData(2, 150)]
    [InlineData(150, 150)]
    [InlineData(151, 151)]
    [InlineData(175, 175)]
    public void ExplicitFixturePopulationExpandsOnlyWhenNeeded(int population, int maximum)
    {
        var options = Assert.IsType<PublicQueueOptions>(LocalServer.QueueOptionsFor(population));
        Assert.Equal(population, options.MinPlayers);
        Assert.Equal(maximum, options.MaxPlayers);
        Assert.Equal(3000, options.WaitMs);
        Assert.Equal(2, options.MaxAllocatedMatches);
        Assert.Equal(150, new PublicQueueOptions().MaxPlayers); // Production default untouched.
    }

    [Fact]
    public void Direct175OptionsAdmitOneCompleteRosterWithoutEnvironmentClamping()
    {
        var queue = new PublicMatchQueue(LocalServer.QueueOptionsFor(175)!);
        ulong match = 0;
        for (ulong player = 1; player <= 175; player++)
        {
            Assert.True(queue.TryReserve(1, MatchMode.Solo, [player], 0, out ulong id));
            if (player == 1) match = id;
            Assert.Equal(match, id);
        }
        // An unready full round must not silently split or accept the extra seat.
        Assert.False(queue.TryReserve(1, MatchMode.Solo, [176], 0, out _));
        Assert.Equal(175, queue.Position(175));
        queue.Poll(0);
        queue.SetReadyPlayers(match, Enumerable.Range(1, 175).Select(i => (ulong)i).ToArray(), 0);
        queue.Poll(1);
        Assert.Equal(PublicMatchPhase.ROSTER_FROZEN, Assert.Single(queue.Snapshots).Phase);
        Assert.True(queue.TryReserve(1, MatchMode.Solo, [176], 2, out ulong next));
        Assert.NotEqual(match, next);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(176)]
    public void InvalidFixtureCapacityRejectedWithoutStartingListeners(int population)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalServer.QueueOptionsFor(population));
    }

    [Fact]
    public void NoQueueAndExistingMatchAllocationBehaviorPreserved()
    {
        Assert.Null(LocalServer.QueueOptionsFor(0));
        Assert.Equal(5, LocalServer.QueueOptionsFor(175, 5)!.MaxAllocatedMatches);
    }
}
