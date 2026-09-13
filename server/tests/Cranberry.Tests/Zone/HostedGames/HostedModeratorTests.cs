using Cranberry.Zone.HostedGames;

namespace Cranberry.Tests.Zone.HostedGames;

public sealed class HostedModeratorTests
{
    private static HostedGameInfo Create(HostedGameStore store, string account)
    {
        var grant = store.IssueHostKey("server-admin", true, "EU");
        Assert.True(store.Redeem(account, grant.Secret!).Success);
        return store.CreateGame(account, "EU", 13, account + " friends").Game!;
    }

    [Fact]
    public void ModeratorIsBoundToOneGameAndCannotGrantAdministratorsOrCloseTheGame()
    {
        var store = new HostedGameStore();
        var first = Create(store, "first");
        var second = Create(store, "second");
        var invitation = store.IssuePlayerKey("first", false, first.WorldId, targetAccount: "mod", moderator: true);
        Assert.True(invitation.Success);
        Assert.Equal(HostedKeyKind.Moderator, invitation.Key!.Kind);
        Assert.False(store.CanManage("mod", false, first.WorldId));
        Assert.False(store.Redeem("other", invitation.Secret!).Success);
        Assert.True(store.Redeem("mod", invitation.Secret!).Success);
        Assert.True(store.CanManage("mod", false, first.WorldId));
        Assert.True(store.CanEnter("mod", first.WorldId));
        Assert.False(store.CanManage("mod", false, second.WorldId));
        Assert.False(store.CanEnter("mod", second.WorldId));
        Assert.True(store.SetMode("mod", false, first.WorldId, 7).Success);
        var player = store.IssuePlayerKey("mod", false, first.WorldId);
        Assert.True(player.Success);
        Assert.True(store.RevokeKey("mod", false, player.Key!.Id).Success);
        Assert.False(store.IssuePlayerKey("mod", false, first.WorldId, moderator: true).Success);
        Assert.False(store.IssueHostKey("mod", false, "EU").Success);
        Assert.False(store.CloseGame("mod", false, first.WorldId).Success);
        Assert.False(store.RevokeKey("mod", false, invitation.Key.Id).Success);
        Assert.True(store.RevokeKey("first", false, invitation.Key.Id).Success);
        Assert.False(store.CanManage("mod", false, first.WorldId));
        Assert.False(store.CanEnter("mod", first.WorldId));
    }

    [Fact]
    public void ModeratorExpiresWithParentGrantAndDoesNotSurviveWorldReuse()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new HostedGameStore(utcNow: () => now);
        var grant = store.IssueHostKey("admin", true, "EU", TimeSpan.FromHours(1));
        Assert.True(store.Redeem("host", grant.Secret!).Success);
        var game = store.CreateGame("host", "EU", 13, "Friends").Game!;
        var invitation = store.IssuePlayerKey("host", false, game.WorldId, TimeSpan.FromDays(1), moderator: true);
        Assert.Equal(grant.Key!.ExpiresAt, invitation.Key!.ExpiresAt);
        Assert.True(store.Redeem("mod", invitation.Secret!).Success);
        now += TimeSpan.FromHours(1);
        Assert.False(store.CanManage("mod", false, game.WorldId));
        var replacement = Create(store, "new-host");
        Assert.Equal(game.WorldId, replacement.WorldId);
        Assert.False(store.CanManage("mod", false, replacement.WorldId));
        Assert.False(store.CanEnter("mod", replacement.WorldId));
    }

    [Fact]
    public void ModeratorPermissionsPersistAndFailClosedAfterRevocation()
    {
        string folder = Path.Combine(Path.GetTempPath(), "cranberry-hosted-moderator-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "hosted.json");
        try
        {
            var store = new HostedGameStore(path);
            var game = Create(store, "host");
            var invitation = store.IssuePlayerKey("host", false, game.WorldId, moderator: true);
            Assert.True(store.Redeem("mod", invitation.Secret!).Success);
            var loaded = new HostedGameStore(path);
            Assert.True(loaded.CanManage("mod", false, game.WorldId));
            Assert.DoesNotContain(invitation.Secret!, File.ReadAllText(path));
            Assert.True(loaded.RevokeKey("host", false, invitation.Key!.Id).Success);
            Assert.False(new HostedGameStore(path).CanManage("mod", false, game.WorldId));
        }
        finally
        {
            // The exact unique directory allocated by this test contains only its fixture.
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
