using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.DevConsole;

namespace Cranberry.Tests.Zone.HostedGames;

public sealed partial class HostedGameGatewayTests
{
    [Fact]
    public void HostedKickRemovesOnlyTheSelectedCurrentRoundMemberAndKeepsTheirInvitation()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        var guest = f.Player("guest");
        var remaining = f.Player("remaining");
        f.Admit(game, guest); f.Admit(game, remaining);
        f.Transfer(host, game.WorldId); f.Transfer(guest, game.WorldId); f.Transfer(remaining, game.WorldId);
        Loaded(host, Vector3.One); Loaded(guest, Vector3.One * 2); Loaded(remaining, Vector3.One * 3);

        var reply = f.HostCommand(host, $"kick {game.WorldId} 0x{Get<ulong>(guest.Tag!, "Guid"):X}");

        Assert.True(reply.Ok, string.Join("; ", reply.Lines));
        Assert.NotEqual(ConnectionState.Open, guest.State);
        Assert.Equal(ConnectionState.Open, host.State);
        Assert.Equal(ConnectionState.Open, remaining.State);
        Assert.True(f.Store.CanEnter("guest", game.WorldId));
        Assert.Equal(ConsoleTier.Player, Get<ConsoleSession>(host.Tag!, "DevConsole").Tier);
    }

    [Fact]
    public void HostedKickRejectsGuestsSelfAndAnAdministratorOutsideTheGame()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host"); var guest = f.Player("guest");
        f.Admit(game, guest); f.Transfer(guest, game.WorldId);
        Assert.False(f.HostCommand(host, $"kick {game.WorldId} {Get<ulong>(guest.Tag!, "Guid")}").Ok);
        f.Transfer(host, game.WorldId);
        Assert.False(f.HostCommand(guest, $"kick {game.WorldId} {Get<ulong>(host.Tag!, "Guid")}").Ok);
        Assert.False(f.HostCommand(host, $"kick {game.WorldId} {Get<ulong>(host.Tag!, "Guid")}").Ok);
        Assert.Equal(ConnectionState.Open, host.State); Assert.Equal(ConnectionState.Open, guest.State);
    }

    [Fact]
    public void HostedKickRejectsCrossGameCrossRoundRevokedAndUnauthenticatedRequests()
    {
        using var f = new Fixture();
        var game = f.Host("host"); var other = f.Host("other");
        var host = f.Player("host"); var target = f.Player("other");
        f.Transfer(host, game.WorldId); f.Transfer(target, other.WorldId);
        string command = $"kick {game.WorldId} {Get<ulong>(target.Tag!, "Guid")}";
        Assert.False(f.HostCommand(host, command, ConsoleTier.Owner).Ok);
        f.Admit(game, target);
        Set(target.Tag!, "HostedGameId", game.Id); Set(target.Tag!, "BountyWorldId", game.WorldId);
        Set(target.Tag!, "BountyAdmission", Admission(host) with { MatchId = Admission(host).MatchId + 1 });
        Assert.False(f.HostCommand(host, command, ConsoleTier.Owner).Ok);
        Set(target.Tag!, "BountyAdmission", Admission(host));
        Set(host.Tag!, "Authenticated", false);
        Assert.False(f.HostCommand(host, command, ConsoleTier.Owner).Ok);
        Set(host.Tag!, "Authenticated", true);
        Assert.True(f.Store.CloseGame("host", false, game.WorldId).Success);
        Assert.False(f.HostCommand(host, command, ConsoleTier.Owner).Ok);
        Assert.Equal(ConnectionState.Open, target.State);
    }

    [Fact]
    public void HostedModeratorCanKickGuestsButCannotKickOwnerOrAnotherModerator()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host"); var moderator = f.Player("moderator");
        var otherModerator = f.Player("other-moderator"); var guest = f.Player("guest");
        foreach (var account in new[] { "moderator", "other-moderator" })
        {
            var key = f.Store.IssuePlayerKey("host", false, game.WorldId, null, account, moderator: true);
            Assert.True(f.Store.Redeem(account, key.Secret!).Success);
        }
        f.Admit(game, guest);
        foreach (var player in new[] { host, moderator, otherModerator, guest }) f.Transfer(player, game.WorldId);
        foreach (var target in new[] { host, otherModerator })
        {
            Assert.False(f.HostCommand(moderator, $"kick {game.WorldId} {Get<ulong>(target.Tag!, "Guid")}").Ok);
            Assert.Equal(ConnectionState.Open, target.State);
        }
        Assert.True(f.HostCommand(moderator, $"kick {game.WorldId} {Get<ulong>(guest.Tag!, "Guid")}").Ok);
        Assert.NotEqual(ConnectionState.Open, guest.State);
    }
}
