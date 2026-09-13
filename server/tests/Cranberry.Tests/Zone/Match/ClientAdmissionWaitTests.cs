using Cranberry.Zone.Match;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed partial class WorldModeRoutingTests
{
    [Fact]
    public void PlayDuringClientInitializationWaitsAndQueuesTheSelectedModeWithoutAnotherClick()
    {
        var f = new Fixture(); var player = f.Player();
        bool ready = false;
        f.Service.DoorSwingClientReady = _ => ready;
        f.Sent.Clear();
        f.Transfer(player, 1);
        Assert.Empty(f.Sent); // Initialization is not a failed world transfer or an error notice.
        Assert.Equal("Menu", Get<object>(player.Tag!, "Match").ToString());
        Assert.Equal(MatchAdmissionContext.Unknown, Get<MatchAdmissionContext>(player.Tag!, "BountyAdmission"));
        var pending = Get<object>(player.Tag!, "PendingClientAdmission");
        ready = true;
        Call(f.Service, "PumpClientAdmission", player, player.Tag, pending, Environment.TickCount64);
        Assert.Equal("Queued", Get<object>(player.Tag!, "Match").ToString());
        Assert.Equal(1u, Get<uint>(player.Tag!, "BountyWorldId"));
        Assert.Null(Get<object?>(player.Tag!, "PendingClientAdmission"));
        int generation = Get<int>(player.Tag!, "MatchAdmissionGeneration");
        Call(f.Service, "PumpClientAdmission", player, player.Tag, pending, Environment.TickCount64);
        Assert.Equal(generation, Get<int>(player.Tag!, "MatchAdmissionGeneration"));
    }

    [Theory]
    [InlineData(0xee)]
    [InlineData(0xef)]
    public void CancelledInitializationWaitCannotQueueLater(byte opcode)
    {
        var f = new Fixture(); var player = f.Player(); bool ready = false;
        f.Service.DoorSwingClientReady = _ => ready;
        f.Transfer(player, 1);
        var pending = Get<object>(player.Tag!, "PendingClientAdmission");
        f.Service.OnMessage(player, new byte[] { new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte(), opcode });
        ready = true;
        Call(f.Service, "PumpClientAdmission", player, player.Tag, pending, Environment.TickCount64);
        Assert.Equal("Menu", Get<object>(player.Tag!, "Match").ToString());
        Assert.Equal(MatchAdmissionContext.Unknown, Get<MatchAdmissionContext>(player.Tag!, "BountyAdmission"));
        Assert.Null(Get<object?>(player.Tag!, "PendingClientAdmission"));
    }

    [Fact]
    public void RepeatedPlayIsCoalescedAndChangingModeInvalidatesThePreviousWait()
    {
        var f = new Fixture(); var player = f.Player(); bool ready = false;
        f.Service.DoorSwingClientReady = _ => ready;
        f.Transfer(player, 1); var old = Get<object>(player.Tag!, "PendingClientAdmission");
        f.Transfer(player, 1); Assert.Same(old, Get<object>(player.Tag!, "PendingClientAdmission"));
        f.Transfer(player, 7); var current = Get<object>(player.Tag!, "PendingClientAdmission");
        Assert.NotSame(old, current);
        ready = true;
        Call(f.Service, "PumpClientAdmission", player, player.Tag, old, Environment.TickCount64);
        Assert.Equal("Menu", Get<object>(player.Tag!, "Match").ToString());
        Call(f.Service, "PumpClientAdmission", player, player.Tag, current, Environment.TickCount64);
        Assert.Equal("Queued", Get<object>(player.Tag!, "Match").ToString());
        Assert.Equal(7u, Get<uint>(player.Tag!, "BountyWorldId"));
    }

    [Fact]
    public void AnInitializationFailureExpiresOnceWithoutReservingAMatch()
    {
        var f = new Fixture(); var player = f.Player();
        f.Service.DoorSwingClientReady = _ => false;
        f.Transfer(player, 1); var pending = Get<object>(player.Tag!, "PendingClientAdmission");
        f.Sent.Clear();
        Call(f.Service, "PumpClientAdmission", player, player.Tag, pending, Environment.TickCount64 + 60_001);
        Assert.Null(Get<object?>(player.Tag!, "PendingClientAdmission"));
        Assert.Equal("Menu", Get<object>(player.Tag!, "Match").ToString());
        Assert.Equal(MatchAdmissionContext.Unknown, Get<MatchAdmissionContext>(player.Tag!, "BountyAdmission"));
        Assert.NotEmpty(f.Sent);
        int count = f.Sent.Count;
        Call(f.Service, "PumpClientAdmission", player, player.Tag, pending, Environment.TickCount64 + 60_002);
        Assert.Equal(count, f.Sent.Count);
    }

    [Fact]
    public void LogoutInvalidatesThePendingPlay()
    {
        var f = new Fixture(); var player = f.Player(); bool ready = false;
        f.Service.DoorSwingClientReady = _ => ready;
        f.Transfer(player, 1); var pending = Get<object>(player.Tag!, "PendingClientAdmission");
        Set(player.Tag!, "LogoutPrepared", true); ready = true;
        Call(f.Service, "PumpClientAdmission", player, player.Tag, pending, Environment.TickCount64);
        Assert.Equal("Menu", Get<object>(player.Tag!, "Match").ToString());
        Assert.Null(Get<object?>(player.Tag!, "PendingClientAdmission"));
    }

    [Fact]
    public void DisconnectInvalidatesThePendingPlay()
    {
        var f = new Fixture(); var player = f.Player(); bool ready = false;
        f.Service.DoorSwingClientReady = _ => ready;
        f.Transfer(player, 1); var pending = Get<object>(player.Tag!, "PendingClientAdmission");
        f.Service.OnDisconnected(player, DisconnectCause.Timeout); ready = true;
        Call(f.Service, "PumpClientAdmission", player, player.Tag, pending, Environment.TickCount64);
        Assert.Equal("Menu", Get<object>(player.Tag!, "Match").ToString());
        Assert.Null(Get<object?>(player.Tag!, "PendingClientAdmission"));
    }
}
