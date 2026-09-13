using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.HostedGames;

public sealed partial class HostedGameGatewayTests
{
    private static void Loaded(SoeConnection player, Vector3 position, string phase = "Lobby")
    {
        SetPhase(player, phase);
        Set(player.Tag!, "Hitpoints", 10_000u);
        Set(player.Tag!, "PregameClientReady", true);
        var movement = Get<SessionMovementState>(player.Tag!, "Movement");
        // A zero-field August movement sample creates an actor; PinPlayer then supplies its pose.
        movement.ApplyPlayer(ClientMovementUpdate.Parse(new byte[7]));
        movement.PinPlayer(position);
    }

    private static bool IsHostedAlert(byte[] packet) => packet.Length > 4
        && packet[1] == 0x11 && packet[2] == 0x31 && packet[3] == 0;
    private static bool IsHostedTeleport(byte[] packet) => packet.Length > 4
        && packet[1] == 0x11 && packet[2] == 0x0a && packet[3] == 0;

    [Fact]
    public void HostedAnnouncementAndRosterStayInsideTheCurrentGameIncarnation()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var otherGame = f.Host("other-host");
        var host = f.Player("host");
        var guest = f.Player("guest");
        var outsider = f.Player("other-host");
        var menu = f.Player("menu-guest");
        f.Admit(game, guest);
        f.Admit(game, menu);
        f.Transfer(host, game.WorldId);
        f.Transfer(guest, game.WorldId);
        f.Transfer(outsider, otherGame.WorldId);
        f.Sent.Clear();

        var reply = f.HostCommand(host, $"announce {game.WorldId} Meet at the tower!");
        var roster = f.HostCommand(host, $"roster {game.WorldId}");

        Assert.True(reply.Ok);
        Assert.True(roster.Ok);
        Assert.Single(f.Sent, p => p.Connection == host && IsHostedAlert(p.Packet));
        Assert.Single(f.Sent, p => p.Connection == guest && IsHostedAlert(p.Packet));
        Assert.DoesNotContain(f.Sent, p => p.Connection == outsider || p.Connection == menu);
        Assert.Contains(roster.Lines, line => line.Contains("guest [Queued", StringComparison.Ordinal));
        Assert.DoesNotContain(roster.Lines, line => line.Contains("other-host", StringComparison.Ordinal)
            || line.Contains("menu-guest", StringComparison.Ordinal));
        Assert.Equal(ConsoleTier.Player, Get<ConsoleSession>(host.Tag!, "DevConsole").Tier);
    }

    [Fact]
    public void HostedAnnouncementCannotBeSentByAnInvitedOrdinaryPlayer()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var guest = f.Player("guest");
        f.Admit(game, guest);
        f.Transfer(guest, game.WorldId);
        f.Sent.Clear();

        Assert.False(f.HostCommand(guest, $"announce {game.WorldId} hello").Ok);
        Assert.DoesNotContain(f.Sent, p => IsHostedAlert(p.Packet));
    }

    [Fact]
    public void HostedAnnouncementsRefuseOversizeTextAndEmptyGames()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        Assert.False(f.HostCommand(host, $"announce {game.WorldId} hello").Ok);
        f.Transfer(host, game.WorldId);
        f.Sent.Clear();
        Assert.False(f.HostCommand(host, $"announce {game.WorldId} {new string('a', 241)}").Ok);
        Assert.DoesNotContain(f.Sent, p => IsHostedAlert(p.Packet));
    }

    [Theory]
    [InlineData("bring", "Lobby")]
    [InlineData("goto", "Lobby")]
    [InlineData("bring", "InMatch")]
    [InlineData("goto", "InMatch")]
    public void HostedTeleportMovesOnlyTheSelectedPlayerInTheSameRound(string verb, string phase)
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        var guest = f.Player("guest");
        f.Admit(game, guest);
        f.Transfer(host, game.WorldId);
        f.Transfer(guest, game.WorldId);
        var hostPosition = new Vector3(100, 50, 100);
        var guestPosition = new Vector3(200, 60, 200);
        Loaded(host, hostPosition, phase);
        Loaded(guest, guestPosition, phase);
        f.Sent.Clear();

        string character = verb == "bring" ? $"0x{Get<ulong>(guest.Tag!, "Guid"):X}" : Get<ulong>(guest.Tag!, "Guid").ToString();
        var reply = f.HostCommand(host, $"{verb} {game.WorldId} {character}");

        Assert.True(reply.Ok, string.Join("; ", reply.Lines));
        var moved = verb == "bring" ? guest : host;
        var stationary = verb == "bring" ? host : guest;
        var destination = verb == "bring" ? hostPosition : guestPosition;
        Assert.Equal(destination + new Vector3(2, 0, 0), Get<SessionMovementState>(moved.Tag!, "Movement").Player!.Position);
        Assert.Equal(destination, Get<SessionMovementState>(stationary.Tag!, "Movement").Player!.Position);
        Assert.Single(f.Sent, p => p.Connection == moved && IsHostedTeleport(p.Packet));
        Assert.DoesNotContain(f.Sent, p => p.Connection == stationary && IsHostedTeleport(p.Packet));
        Assert.Equal(phase, Phase(host));
        Assert.Equal(phase, Phase(guest));
        Assert.Equal(ConsoleTier.Player, Get<ConsoleSession>(host.Tag!, "DevConsole").Tier);
    }

    [Theory]
    [InlineData("Menu")]
    [InlineData("Queued")]
    [InlineData("Dropping")]
    [InlineData("Ended")]
    public void HostedTeleportRefusesLoadingDroppingAndEliminatedPlayers(string phase)
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        var guest = f.Player("guest");
        f.Admit(game, guest);
        f.Transfer(host, game.WorldId);
        f.Transfer(guest, game.WorldId);
        Loaded(host, Vector3.One);
        Loaded(guest, Vector3.One * 50, phase);
        f.Sent.Clear();

        Assert.False(f.HostCommand(host, $"bring {game.WorldId} {Get<ulong>(guest.Tag!, "Guid")}").Ok);
        Assert.DoesNotContain(f.Sent, p => IsHostedTeleport(p.Packet));
    }

    [Fact]
    public void HostedTeleportsRefuseCrossGameAndCrossRoundTargetsEvenForAnOwner()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var otherGame = f.Host("other-host");
        var host = f.Player("host");
        var outsider = f.Player("other-host");
        f.Transfer(host, game.WorldId);
        f.Transfer(outsider, otherGame.WorldId);
        Loaded(host, Vector3.One);
        Loaded(outsider, Vector3.One * 50);
        f.Sent.Clear();
        string command = $"bring {game.WorldId} {Get<ulong>(outsider.Tag!, "Guid")}";

        Assert.False(f.HostCommand(host, command, ConsoleTier.Owner).Ok);
        Set(outsider.Tag!, "HostedGameId", game.Id);
        Set(outsider.Tag!, "BountyWorldId", game.WorldId);
        f.Admit(game, outsider);
        Set(outsider.Tag!, "BountyAdmission", Admission(host) with { MatchId = Admission(host).MatchId + 1 });
        Assert.False(f.HostCommand(host, command, ConsoleTier.Owner).Ok);
        Assert.DoesNotContain(f.Sent, p => IsHostedTeleport(p.Packet));
    }

    [Fact]
    public void HostedRevokedMembershipIsImmediatelyExcludedFromRosterAndAdminActions()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        var guest = f.Player("guest");
        var key = f.Invitation(game, "guest");
        Assert.True(f.Store.Redeem("guest", key.Secret!).Success);
        f.Transfer(host, game.WorldId);
        f.Transfer(guest, game.WorldId);
        Loaded(host, Vector3.One);
        Loaded(guest, Vector3.One * 50);
        Assert.True(f.Store.RevokeKey("host", false, key.Key!.Id).Success);
        f.Sent.Clear();

        Assert.False(f.HostCommand(host, $"bring {game.WorldId} {Get<ulong>(guest.Tag!, "Guid")}").Ok);
        Assert.True(f.HostCommand(host, $"announce {game.WorldId} hello").Ok);
        Assert.DoesNotContain(f.Sent, p => p.Connection == guest);
    }

    [Fact]
    public void HostedAdminCommandsRequireAnAuthenticatedSessionEvenWithAnAccountName()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        f.Transfer(host, game.WorldId);
        Set(host.Tag!, "Authenticated", false);
        f.Sent.Clear();

        Assert.False(f.HostCommand(host, $"announce {game.WorldId} hello", ConsoleTier.Owner).Ok);
        Assert.False(f.HostCommand(host, $"roster {game.WorldId}").Ok);
        Assert.DoesNotContain(f.Sent, p => IsHostedAlert(p.Packet));
    }
}
