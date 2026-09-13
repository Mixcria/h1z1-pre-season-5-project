using Cranberry.Harness.Soe;
using Cranberry.Harness.Behaviour;
using Cranberry.NetworkBots;
using Cranberry.Harness.Protocol;
using Cranberry.Harness.Verification;
using System.Numerics;

namespace Cranberry.Harness.Tests;

public sealed class NetworkBotLifecycleTests
{
    [Fact]
    public void ReceivedLogoutClearsDepartedWorldAndPreservesSessionHistory()
    {
        var observation = new BotObservation
        {
            Tick = () => 2000, SelfGuid = () => 100, Results = 2,
            LastResultTick = 1950, CompletedLogouts = 1, ItemAdds = 7,
            ReturnTicket = "synthetic-return-ticket"
        };
        observation.Peers[101] = 16;
        observation.Loot[31] = new(0xd6, 31, 17, 2, Vector3.One);
        observation.Canopies[33] = new(0xd7, 33, 18, 9374, Vector3.One);
        observation.MountedRiders[101] = 33;
        observation.Inventory[35] = new ItemGrant(100, 63, 1429, 35, 30, 36, 1, 1, 80);
        observation.GrantedUnitsByDefinition[1429] = 30;
        observation.Opcodes[0xce] = 2;
        observation.RemoteHandItems[101] = 35;
        observation.WorldItemDefinitions[31] = 2;
        observation.Removed.Add(37); observation.Deaths.Add(39);
        observation.PeerFireStarts[16] = 3;
        observation.OwnChuteGuid = 33; observation.ChuteTransient = 18;
        observation.ChuteSpawn = Vector3.One; observation.FirstAmmoTick = 1900;
        observation.BeginMovementWindow(1900);
        ReceivePose(observation, 1990);
        long messages = observation.Messages;

        ReceiveLogout(observation);

        Assert.Empty(observation.Peers); Assert.Empty(observation.Loot);
        Assert.Empty(observation.Canopies); Assert.Empty(observation.MountedRiders);
        Assert.Empty(observation.Inventory); Assert.Empty(observation.RemoteHandItems);
        Assert.Empty(observation.WorldItemDefinitions); Assert.Empty(observation.Removed);
        Assert.Empty(observation.Deaths); Assert.Empty(observation.PeerPoses);
        Assert.Empty(observation.PeerFireStarts); Assert.Empty(observation.WindowJumpPeers);
        Assert.Equal(0ul, observation.OwnChuteGuid);
        Assert.Null(observation.ChuteTransient); Assert.Null(observation.ChuteSpawn);
        Assert.Null(observation.FirstAmmoTick);
        Assert.Equal(0u, observation.Health);
        Assert.Equal(0, observation.MovementWindow(2001, false).Samples);
        Assert.Equal(-1, observation.MovementWindow(2001, false).P99Ms);
        Assert.Equal(2, observation.CompletedLogouts);
        Assert.Equal(2000u, observation.LastLogoutTick);
        Assert.Equal(2, observation.Results); Assert.Equal(1950u, observation.LastResultTick);
        Assert.Equal("synthetic-return-ticket", observation.ReturnTicket);
        Assert.Equal(7, observation.ItemAdds);
        Assert.Equal(30L, observation.GrantedUnitsByDefinition[1429]);
        Assert.Equal(messages + 1, observation.Messages);
        Assert.Equal(1, observation.Poses);
        Assert.Equal(1, observation.PoseAgeMs.Sum());
        Assert.Equal(2, observation.Opcodes[0xce]);
        Assert.Equal(1, observation.Opcodes[0x11]);
        byte[] ticket = System.Text.Encoding.UTF8.GetBytes("next-return-ticket");
        observation.Observe(ObservedPacket.ParseGateway(
            [5, 0xc4, 1, .. BitConverter.GetBytes((uint)ticket.Length), .. ticket]), TimeSpan.Zero);
        Assert.Equal("next-return-ticket", observation.ReturnTicket);
        Assert.Equal(2, observation.CompletedLogouts);
        Assert.Equal(2, observation.Results);
    }

    [Fact]
    public void NextWorldMayReuseATransientAndAnEarlierMovementClock()
    {
        uint tick = 2000;
        var observation = new BotObservation { Tick = () => tick };
        observation.Peers[101] = 16;
        observation.BeginMovementWindow(1900);
        ReceivePose(observation, 1990);
        ReceiveLogout(observation);
        tick = 500;
        observation.Peers[202] = 16;
        observation.BeginMovementWindow(400);
        ReceivePose(observation, 450);
        var window = observation.MovementWindow(510, false);
        Assert.Equal(1, window.Samples);
        Assert.Equal(0, window.OutOfOrderTimestamps);
        Assert.Equal(0, window.MissingPeers);
        Assert.Equal(50, window.P99Ms);
        Assert.Equal(202ul, Assert.Single(observation.Peers).Key);
    }

    [Theory]
    [InlineData(new byte[] { 0x11, 0x30 })]
    [InlineData(new byte[] { 0x11, 0x30, 1 })]
    [InlineData(new byte[] { 0x11, 0x30, 0, 0 })]
    [InlineData(new byte[] { 0x11, 0x31, 0 })]
    public void OtherOrIncompletePacketsDoNotClearAnActiveWorld(byte[] payload)
    {
        var observation = new BotObservation();
        observation.Peers[101] = 16;
        observation.Observe(ObservedPacket.ParseGateway([5, .. payload]), TimeSpan.Zero);
        Assert.Single(observation.Peers);
        Assert.Equal(0, observation.CompletedLogouts);
    }

    [Fact]
    public void LogoutBytesInTheLoginLayerDoNotClearTheWorld()
    {
        var observation = new BotObservation();
        observation.Peers[101] = 16;
        observation.Observe(ObservedPacket.ParseLogin([0x11, 0x30, 0]), TimeSpan.Zero);
        Assert.Single(observation.Peers);
        Assert.Equal(0, observation.CompletedLogouts);
    }

    [Fact]
    public void ExplicitReconnectResetStillInvalidatesTicketAndWindow()
    {
        var observation = new BotObservation { Tick = () => 2000, ReturnTicket = "old-ticket", Results = 2 };
        observation.Peers[101] = 16;
        observation.BeginMovementWindow(1900);
        ReceivePose(observation, 1990);
        observation.ResetMatchView();
        Assert.Null(observation.ReturnTicket);
        Assert.Empty(observation.Peers);
        Assert.Equal(0, observation.MovementWindow(2001, false).Samples);
        Assert.Equal(2, observation.Results);
    }

    private static void ReceiveLogout(BotObservation observation) =>
        observation.Observe(ObservedPacket.ParseGateway([5, 0x11, 0x30, 0]), TimeSpan.Zero);

    private static void ReceivePose(BotObservation observation, uint tick)
    {
        byte[] movement = BotWire.Movement(Vector3.Zero, tick, posture: 0x20);
        observation.Observe(ObservedPacket.ParseGateway([5, 0x78, 0x40, .. movement[1..]]), TimeSpan.Zero);
    }

    [Fact]
    public async Task CleanGatewayCompletionIsReportedWithoutSpinningOrStallingOtherClients()
    {
        string output = Path.Combine(Path.GetTempPath(), "cranberry-bot-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        using var server = new LocalServer(2, output);
        await using var first = new HarnessClient(new HarnessOptions { LoginEndPoint = server.LoginEndPoint, CharacterIndex = 0 });
        await using var second = new HarnessClient(new HarnessOptions { LoginEndPoint = server.LoginEndPoint, CharacterIndex = 1 });
        await Task.WhenAll(first.ConnectAsync(), second.ConnectAsync());
        first.GatewayLink!.Disconnect();
        await first.ExpectAsync(HarnessMilestone.LinkClosed, TimeSpan.FromSeconds(2));
        Assert.Equal(LinkCloseCause.LocalRequest, first.GatewayLink.CloseCause);
        Assert.Equal(LinkCloseCause.None, second.GatewayLink!.CloseCause);
        second.GatewayLink.Disconnect();
        await second.ExpectAsync(HarnessMilestone.LinkClosed, TimeSpan.FromSeconds(2));
    }
}
