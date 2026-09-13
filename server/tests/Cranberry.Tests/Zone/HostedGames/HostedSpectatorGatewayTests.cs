using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.World;
using Cranberry.Tests.Zone.World;

namespace Cranberry.Tests.Zone.HostedGames;

public sealed partial class HostedGameGatewayTests
{
    private static void Eliminated(SoeConnection player, Vector3 position)
    {
        Loaded(player, position, "Ended");
        Set(player.Tag!, "Hitpoints", 0u);
        Set(player.Tag!, "DeathSent", true);
    }

    private static void SpectatorRequest(Fixture f, SoeConnection player, ushort action, Action<PacketWriter> body)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte());
        writer.WriteByte(ZoneOpcodes.SpectatorBase);
        writer.WriteUInt16(action);
        body(writer);
        f.Service.OnMessage(player, writer.Written.ToArray());
    }

    [Fact]
    public void EliminatedHostEntersNativeFreeCameraWithoutRevivingOrMovingItsBody()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        f.Transfer(host, game.WorldId);
        Vector3 body = new(100, 200, 300);
        Eliminated(host, body);
        f.Sent.Clear();

        var reply = f.HostCommand(host, $"fly {game.WorldId} on");

        Assert.True(reply.Ok, string.Join("; ", reply.Lines));
        Assert.Single(f.Sent, p => p.Connection == host && p.Packet.AsSpan(1).SequenceEqual(new byte[] { 0xe2, 1, 0 }));
        Assert.True(Get<bool>(host.Tag!, "HostedObserverActive"));
        Assert.True(Get<bool>(host.Tag!, "HostedFreeCamera"));
        Assert.Equal(body, Get<Vector3?>(host.Tag!, "HostedCameraPosition"));
        Assert.Equal(body, Get<SessionMovementState>(host.Tag!, "Movement").Player!.Position);
        Assert.Equal(0u, Get<uint>(host.Tag!, "Hitpoints"));
        Assert.True(Get<bool>(host.Tag!, "DeathSent"));
        Assert.Equal("Ended", Phase(host));
        Assert.Equal(ConsoleTier.Player, Get<ConsoleSession>(host.Tag!, "DevConsole").Tier);
        Assert.True(f.HostCommand(host, $"fly {game.WorldId} on").Ok);
        Assert.Single(f.Sent, p => p.Packet.AsSpan(1).SequenceEqual(new byte[] { 0xe2, 1, 0 }));
        Assert.Equal(2u, Get<uint>(host.Tag!, "HostedObserverSequence"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void NativeCameraAdmissionRequiresBothDeathAndScopedHostAuthority(bool dead, bool owner)
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var player = f.Player(owner ? "host" : "guest");
        if (!owner) f.Admit(game, player);
        f.Transfer(player, game.WorldId);
        if (dead) Eliminated(player, Vector3.One);
        else Loaded(player, Vector3.One, "InMatch");
        f.Sent.Clear();

        Assert.False(f.HostCommand(player, $"fly {game.WorldId} on").Ok);
        SpectatorRequest(f, player, 4, writer => { writer.WriteSingle(300); writer.WriteSingle(300); });
        Assert.False(Get<bool>(player.Tag!, "HostedObserverActive"));
        Assert.DoesNotContain(f.Sent, p => p.Packet.Length > 1 && p.Packet[1] == 0xe2);
        Assert.DoesNotContain(f.Sent, p => IsHostedTeleport(p.Packet));
    }

    [Fact]
    public void CameraMovementNeverRepublishesOrMovesTheDeadBody()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        f.Transfer(host, game.WorldId);
        Vector3 body = new(100, 200, 300);
        Eliminated(host, body);
        Assert.True(f.HostCommand(host, $"fly {game.WorldId} on").Ok);
        Vector3 camera = new(2000, 700, 3000);
        byte[] movement = MovementRecord.Position(camera);
        byte[] tunnel = [new GatewayHeader(GatewayTunnelFromClient.Opcode, 2).ToByte(), .. movement];
        f.Sent.Clear();

        f.Service.OnMessage(host, tunnel);

        Assert.Equal(camera, Get<Vector3?>(host.Tag!, "HostedCameraPosition"));
        Assert.Equal(body, Get<SessionMovementState>(host.Tag!, "Movement").Player!.Position);
        Assert.DoesNotContain(f.Sent, p => p.Packet.Length > 1 && p.Packet[1] == 0x78);
        Assert.Equal(camera, (Vector3?)Call(f.Service, "WorldStreamPosition", host.Tag));
    }

    [Fact]
    public void NativeFollowChecksTargetRoundAndTracksTargetWithoutMovingCorpse()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        var guest = f.Player("guest");
        f.Admit(game, guest);
        f.Transfer(host, game.WorldId);
        f.Transfer(guest, game.WorldId);
        Vector3 corpse = new(10, 20, 30);
        Vector3 target = new(1500, 50, 2000);
        Eliminated(host, corpse);
        Loaded(guest, target, "InMatch");
        ulong guid = Get<ulong>(guest.Tag!, "Guid");

        Assert.True(f.HostCommand(host, $"spectate {game.WorldId} 0x{guid:X}").Ok);
        uint serial = Get<uint>(host.Tag!, "HostedObserverSequence");
        SpectatorRequest(f, host, 3, writer => writer.WriteUInt64(guid));
        Assert.Equal(serial, Get<uint>(host.Tag!, "HostedObserverSequence"));
        Assert.False(Get<bool>(host.Tag!, "HostedFreeCamera"));
        Assert.Equal(guid, Get<ulong>(host.Tag!, "HostedSpectateTarget"));
        Assert.Equal(target, Get<Vector3?>(host.Tag!, "HostedCameraPosition"));

        Vector3 movedTarget = target + new Vector3(3000, 0, 0);
        Get<SessionMovementState>(guest.Tag!, "Movement").PinPlayer(movedTarget);
        Call(f.Service, "RefreshHostedObserverTarget", host, host.Tag);
        Assert.Equal(movedTarget, Get<Vector3?>(host.Tag!, "HostedCameraPosition"));
        Assert.Equal(corpse, Get<SessionMovementState>(host.Tag!, "Movement").Player!.Position);

        Set(guest.Tag!, "BountyAdmission", Admission(guest) with { MatchId = Admission(guest).MatchId + 1 });
        f.Sent.Clear();
        SpectatorRequest(f, host, 3, writer => writer.WriteUInt64(guid));
        Assert.DoesNotContain(f.Sent, p => IsHostedTeleport(p.Packet));
        Call(f.Service, "RefreshHostedObserverTarget", host, host.Tag);
        Assert.True(Get<bool>(host.Tag!, "HostedFreeCamera"));
        Assert.Equal(serial + 1, Get<uint>(host.Tag!, "HostedObserverSequence"));
    }

    [Fact]
    public void FlightOffReturnsToALivingTargetAndNeverExitsTheGameWhenNoneRemain()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        var guest = f.Player("guest");
        f.Admit(game, guest);
        f.Transfer(host, game.WorldId);
        f.Transfer(guest, game.WorldId);
        Eliminated(host, Vector3.One);
        Loaded(guest, Vector3.One * 30, "InMatch");
        Assert.True(f.HostCommand(host, $"fly {game.WorldId} on").Ok);
        Assert.True(f.HostCommand(host, $"fly {game.WorldId} off").Ok);
        Assert.False(Get<bool>(host.Tag!, "HostedFreeCamera"));
        Assert.Equal(Get<ulong>(guest.Tag!, "Guid"), Get<ulong>(host.Tag!, "HostedSpectateTarget"));
        Assert.True(f.HostCommand(host, $"fly {game.WorldId} on").Ok);
        Eliminated(guest, Vector3.One * 30);

        Assert.False(f.HostCommand(host, $"fly {game.WorldId} off").Ok);
        Assert.True(Get<bool>(host.Tag!, "HostedFreeCamera"));
        Assert.Equal("Ended", Phase(host));
        Assert.Equal(ConnectionState.Open, host.State);
    }

    [Fact]
    public void NativeFlightRejectsInvalidCoordinatesAndKeepsCorpseWhenTeleporting()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        f.Transfer(host, game.WorldId);
        Vector3 corpse = new(100, 250, 300);
        Eliminated(host, corpse);
        Assert.True(f.HostCommand(host, $"fly {game.WorldId} on").Ok);
        f.Sent.Clear();
        SpectatorRequest(f, host, 3, writer => writer.WriteUInt32(1));
        SpectatorRequest(f, host, 4, writer => writer.WriteSingle(1000));
        SpectatorRequest(f, host, 4, writer => { writer.WriteSingle(1000); writer.WriteSingle(2000); writer.WriteByte(1); });
        SpectatorRequest(f, host, 4, writer => { writer.WriteSingle(float.NaN); writer.WriteSingle(10); });
        Assert.DoesNotContain(f.Sent, p => IsHostedTeleport(p.Packet));
        SpectatorRequest(f, host, 4, writer => { writer.WriteSingle(1000); writer.WriteSingle(2000); });
        Assert.Single(f.Sent, p => IsHostedTeleport(p.Packet));
        Assert.Equal(new Vector3(1000, 250, 2000), Get<Vector3?>(host.Tag!, "HostedCameraPosition"));
        Assert.Equal(corpse, Get<SessionMovementState>(host.Tag!, "Movement").Player!.Position);
    }

    [Fact]
    public void RevokingObserverAuthorityEndsCameraEvenWhenPlayerAccessRemains()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var moderator = f.Player("moderator");
        f.Admit(game, moderator);
        var key = f.Store.IssuePlayerKey("host", false, game.WorldId, moderator: true);
        Assert.True(f.Store.Redeem("moderator", key.Secret!).Success);
        f.Transfer(moderator, game.WorldId);
        Eliminated(moderator, Vector3.One);
        Assert.True(f.HostCommand(moderator, $"fly {game.WorldId} on").Ok);
        Assert.True(f.Store.RevokeKey("host", false, key.Key!.Id).Success);
        Assert.True(f.Store.CanEnter("moderator", game.WorldId));

        Assert.False(f.Validate(moderator));
        Assert.False(Get<bool>(moderator.Tag!, "HostedObserverActive"));
        Assert.Null(Get<Vector3?>(moderator.Tag!, "HostedCameraPosition"));
        Assert.Equal(ConnectionState.Closed, moderator.State);
        AssertCleared(moderator);
    }
}
