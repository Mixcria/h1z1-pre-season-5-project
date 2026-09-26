using System.Buffers.Binary;
using Cranberry.Harness.Protocol;
using Cranberry.NetworkBots;

namespace Cranberry.Harness.Tests;

public sealed class NetworkBotCadenceObservationTests
{
    private static void Spawn(BotObservation observation, ulong guid, byte id = 16)
    {
        byte[] packet = [5, 0xd5, 0, 0, 0, 0, 0, 0, 0, 0, (byte)(id << 2), 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(2), guid);
        observation.Observe(ObservedPacket.ParseGateway(packet), TimeSpan.Zero);
    }

    private static void Remove(BotObservation observation, ulong guid)
    {
        byte[] packet = [5, 0x0f, 1, 0, 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(3), guid);
        observation.Observe(ObservedPacket.ParseGateway(packet), TimeSpan.Zero);
    }

    [Fact]
    public void ObservedRemovalAllowsReusedTransientWithLowerClock()
    {
        var observation = Observer();
        Spawn(observation, 100);
        Pose(observation, 1700);
        Remove(observation, 100);
        Spawn(observation, 101);
        observation.BeginMovementWindow(1000);
        Pose(observation, 1100);
        var result = observation.MovementWindow(2000, false);
        Assert.Equal(1, result.FreshSamples);
        Assert.Equal(0, result.NonAdvancingRecords);
        Assert.Equal(1, result.MinimumFreshPairHz);
    }

    [Fact]
    public void RepeatedSameSpawnDoesNotRestartClockButDifferentOwnerDoes()
    {
        var observation = Observer();
        Spawn(observation, 100);
        Pose(observation, 1700);
        Spawn(observation, 100);
        Pose(observation, 1700);
        Assert.Equal(1, observation.MovementWindow(2000, false).FreshSamples);
        Assert.Equal(1, observation.MovementWindow(2000, false).NonAdvancingRecords);
        Spawn(observation, 101);
        Pose(observation, 1100);
        Assert.Equal(2, observation.MovementWindow(2000, false).FreshSamples);
        // A delayed removal for the old GUID cannot clear the new GUID's clock.
        Remove(observation, 100);
        Pose(observation, 1100);
        Assert.Equal(2, observation.MovementWindow(2000, false).FreshSamples);
    }

    [Fact]
    public void SameIdentityReboundToNewTransientDoesNotRetainOldStreamCounts()
    {
        var observation = Observer();
        Spawn(observation, 100);
        Pose(observation, 1700);
        Spawn(observation, 100, 17);
        Pose(observation, 1100, 17);
        Assert.Equal(1, observation.MovementWindow(2000, false).MinimumFreshPairHz);
        Spawn(observation, 101, 16);
        Pose(observation, 1100, 16);
        Assert.Equal(3, observation.MovementWindow(2000, false).FreshSamples);
        Assert.Equal(0, observation.MovementWindow(2000, false).NonAdvancingRecords);
    }

    // Authored minimum sparse relay: gateway channel0, opcode78, one-byte
    // transient varint, zero flags, u32 client timestamp, movement version0.
    private static void Pose(BotObservation observation, uint tick, byte id = 16)
    {
        byte[] bytes = [5, 0x78, (byte)(id << 2), 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), tick);
        observation.Observe(ObservedPacket.ParseGateway(bytes), TimeSpan.Zero);
    }

    private static BotObservation Observer(uint now = 2000)
    {
        var observation = new BotObservation { Tick = () => now };
        observation.Peers[100] = 16;
        observation.BeginMovementWindow(1000);
        return observation;
    }

    [Fact]
    public void DuplicateAndReorderedArrivalsCannotInflateFreshRateOrLowerHighWater()
    {
        var observation = Observer();
        foreach (uint tick in new uint[] { 1100, 1100, 1200, 1150, 1150, 1190, 1300 }) Pose(observation, tick);
        var result = observation.MovementWindow(2000, false);
        Assert.Equal(7, result.Samples);
        Assert.Equal(7, result.MinimumPairHz); // Historical property intentionally unchanged.
        Assert.Equal(3, result.FreshSamples);
        Assert.Equal(4, result.NonAdvancingRecords);
        Assert.Equal(3, result.MinimumFreshPairHz);
        Assert.Equal(1, result.ExpectedPeers);
        Assert.Equal(1, result.PeersWithFreshSamples);
        Assert.Equal(1d, result.FreshPeerCoverage);
    }

    [Fact]
    public void SixtyNewTimestampsPlusSixtyDuplicatesAreSixtyFreshHz()
    {
        var observation = Observer();
        for (uint i = 0; i < 60; i++)
        {
            uint timestamp = 1000 + i * 1000 / 60;
            Pose(observation, timestamp);
            Pose(observation, timestamp);
        }
        var result = observation.MovementWindow(2000, false);
        Assert.Equal(120, result.MinimumPairHz);
        Assert.Equal(60, result.MinimumFreshPairHz);
        Assert.Equal(60, result.NonAdvancingRecords);
    }

    [Fact]
    public void PeerCoverageCannotHideARecipientWithNoFreshUpdates()
    {
        var observation = Observer();
        observation.Peers[101] = 17;
        Pose(observation, 1100);
        Pose(observation, 1200);
        var result = observation.MovementWindow(2000, false);
        Assert.Equal(2, result.FreshSamples);
        Assert.Equal(0, result.MinimumFreshPairHz);
        Assert.Equal(2, result.ExpectedPeers);
        Assert.Equal(1, result.PeersWithFreshSamples);
        Assert.Equal(.5, result.FreshPeerCoverage);
        Pose(observation, 1100, 17); // Same timestamp on a different peer is fresh.
        Assert.Equal(1, observation.MovementWindow(2000, false).MinimumFreshPairHz);
    }

    [Fact]
    public void WindowResetRetainsHighWaterButWorldResetClearsIt()
    {
        var observation = Observer();
        Pose(observation, 1500);
        observation.BeginMovementWindow(1400);
        Pose(observation, 1500);
        Pose(observation, 1450);
        Pose(observation, 1600);
        var result = observation.MovementWindow(2000, false);
        Assert.Equal(1, result.FreshSamples);
        Assert.Equal(2, result.NonAdvancingRecords);
        observation.ResetMatchView();
        observation.Peers[100] = 16;
        observation.BeginMovementWindow(1000);
        Pose(observation, 1450);
        Assert.Equal(1, observation.MovementWindow(2000, false).FreshSamples);
    }

    [Fact]
    public void OldQueuedAndFutureInvalidRecordsDoNotCountAsFreshInWindow()
    {
        var observation = Observer();
        Pose(observation, 900);
        Pose(observation, 2001);
        Pose(observation, 1100);
        var result = observation.MovementWindow(2000, false);
        Assert.Equal(1, result.OlderRecords);
        Assert.Equal(1, observation.InvalidPoses);
        Assert.Equal(1, result.FreshSamples);
        Assert.Equal(0, result.NonAdvancingRecords);
    }

    [Fact]
    public void TimestampWrapStillRequiresStrictForwardAdvancement()
    {
        var observation = Observer(100);
        observation.BeginMovementWindow(uint.MaxValue - 99);
        foreach (uint tick in new uint[] { uint.MaxValue - 49, 0, uint.MaxValue - 24, 50, 50 }) Pose(observation, tick);
        var result = observation.MovementWindow(100, false);
        Assert.Equal(.2, result.Seconds, 8);
        Assert.Equal(3, result.FreshSamples);
        Assert.Equal(2, result.NonAdvancingRecords);
        Assert.Equal(15, result.MinimumFreshPairHz, 8);
    }

    [Fact]
    public void NoExpectedPeersIsUnknownCoverageNotSuccess()
    {
        var observation = Observer();
        observation.Peers.Clear();
        var result = observation.MovementWindow(2000, false);
        Assert.Equal(0, result.ExpectedPeers);
        Assert.Null(result.FreshPeerCoverage);
        Assert.Equal(0, result.MinimumFreshPairHz);
    }
}
