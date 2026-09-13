using Cranberry.Login;
using Cranberry.Zone.HostedGames;
using System.Text.Json.Nodes;

namespace Cranberry.Tests.Zone.HostedGames;

public sealed class HostedGameStoreTests
{
    private static readonly GameWorldDefinition[] Worlds =
    [
        new(1, 13, "Solo"),
        new(8, 13, "Solo", IsHosted: true, Region: "EU"),
        new(9, 13, "Solo", IsHosted: true, Region: "EU"),
        new(10, 13, "Solo", IsHosted: true, Region: "US"),
    ];

    [Fact]
    public void OnlyAuthenticatedAdministratorsCanIssueHostKeys()
    {
        var store = NewStore();
        Assert.False(store.IssueHostKey("ordinary", false, "EU").Success);
        Assert.False(store.IssueHostKey("", true, "EU").Success);
        Assert.False(store.IssueHostKey(" ", true, "EU").Success);
        Assert.False(store.IssueHostKey("admin", true, "AU").Success);
        Assert.False(store.IssueHostKey("admin", true, "EU", targetAccount: " ").Success);
        Assert.Empty(store.ListKeys("admin", true));
        var issued = store.IssueHostKey("admin", true, "eu");
        Assert.True(issued.Success);
        Assert.Equal("EU", issued.Key!.Region);
        Assert.Matches("^HGK-[A-F0-9]{64}$", issued.Secret!);
        Assert.DoesNotContain(issued.Secret!, issued.ToString());
    }

    [Fact]
    public void HostRedemptionIsAccountBoundCaseSensitiveAndIdempotent()
    {
        var store = NewStore();
        var key = store.IssueHostKey("admin", true, "EU", targetAccount: "Host");
        Assert.False(store.Redeem("host", key.Secret!).Success);
        Assert.False(store.Redeem("", key.Secret!).Success);
        Assert.True(store.Redeem("Host", key.Secret!).Success);
        Assert.True(store.Redeem("Host", key.Secret!).Success);
        Assert.False(store.Redeem("other", key.Secret!).Success);
        Assert.Null(store.Redeem("Host", key.Secret!).Secret);
        Assert.Equal("Host", Assert.Single(store.ListKeys("Host")).Account);
    }

    [Fact]
    public void ConcurrentRedemptionGrantsExactlyOneAccount()
    {
        var store = NewStore();
        var key = store.IssueHostKey("admin", true, "EU");
        var results = new bool[32];
        Parallel.For(0, results.Length, index => results[index] = store.Redeem($"account-{index}", key.Secret!).Success);
        Assert.Single(results, success => success);
    }

    [Fact]
    public void GamesRequireRegionalHostGrantAndSupportedMode()
    {
        var store = NewStore();
        GrantHost(store, "host");
        Assert.False(store.CreateGame("host", "US", 13, "Wrong region").Success);
        Assert.False(store.CreateGame("host", "EU", 999, "Wrong mode").Success);
        Assert.False(store.CreateGame("other", "EU", 13, "No grant").Success);
        Assert.False(store.CreateGame("host", "EU", 13, " ").Success);
        Assert.False(store.CreateGame("host", "EU", 13, "Name\nInjected").Success);
        Assert.False(store.CreateGame("host", "EU", 13, "<b>Injected</b>").Success);
        Assert.False(store.CreateGame("host", "EU", 13, "Name>suffix").Success);
        var created = store.CreateGame("host", "eu", 5, " Friends ");
        Assert.True(created.Success);
        Assert.Equal(5u, created.Game!.GameModeId);
        Assert.Equal("Friends", created.Game.Name);
        Assert.True(store.CanEnter("host", created.Game.WorldId));
        Assert.False(store.CanEnter("other", created.Game.WorldId));
        Assert.False(store.CanEnter("", created.Game.WorldId));
        Assert.False(store.CanEnter("host", 1));
    }

    [Fact]
    public void ConcurrentCreationNeverAssignsOneSlotTwice()
    {
        var store = NewStore();
        GrantHost(store, "host");
        var results = new HostedGameResult[16];
        Parallel.For(0, results.Length, index => results[index] = store.CreateGame("host", "EU", 13, $"Game {index}"));
        var successful = results.Where(result => result.Success).ToArray();
        Assert.Equal(2, successful.Length);
        Assert.Equal(2, successful.Select(result => result.Game!.WorldId).Distinct().Count());
    }

    [Fact]
    public void PlayerInvitesArePrivateTargetedSingleAccountAndRevocable()
    {
        var store = NewStore();
        var game = Create(store, "host");
        var invite = store.IssuePlayerKey("host", false, game.WorldId, targetAccount: "friend");
        Assert.True(invite.Success);
        Assert.False(store.CanEnter("friend", game.WorldId));
        Assert.False(store.Redeem("stranger", invite.Secret!).Success);
        Assert.True(store.Redeem("friend", invite.Secret!).Success);
        Assert.True(store.Redeem("friend", invite.Secret!).Success);
        Assert.True(store.CanEnter("friend", game.WorldId));
        Assert.False(store.CanEnter("Friend", game.WorldId));
        Assert.False(store.RevokeKey("friend", false, invite.Key!.Id).Success);
        Assert.True(store.RevokeKey("host", false, invite.Key.Id).Success);
        Assert.False(store.CanEnter("friend", game.WorldId));
        Assert.False(store.Redeem("friend", invite.Secret!).Success);
        Assert.True(store.RevokeKey("host", false, invite.Key.Id).Success);
    }

    [Fact]
    public void PlayerAccessCannotManageGamesOrCreateFurtherInvitations()
    {
        var store = NewStore();
        var game = Create(store, "host");
        var invite = store.IssuePlayerKey("host", false, game.WorldId);
        Assert.True(store.Redeem("friend", invite.Secret!).Success);
        Assert.False(store.CanManage("friend", false, game.WorldId));
        Assert.False(store.IssuePlayerKey("friend", false, game.WorldId).Success);
        Assert.False(store.CloseGame("friend", false, game.WorldId).Success);
        Assert.False(store.SetMode("friend", false, game.WorldId, 5).Success);
        Assert.True(store.CanManage("host", false, game.WorldId));
        Assert.True(store.CanManage("admin", true, game.WorldId));
        Assert.False(store.CanManage("", true, game.WorldId));
    }

    [Fact]
    public void HostsCannotManageEachOthersGamesOrInvites()
    {
        var store = NewStore();
        var first = Create(store, "first");
        var second = Create(store, "second");
        var invite = store.IssuePlayerKey("first", false, first.WorldId);
        Assert.False(store.IssuePlayerKey("second", false, first.WorldId).Success);
        Assert.False(store.RevokeKey("second", false, invite.Key!.Id).Success);
        Assert.False(store.CloseGame("second", false, first.WorldId).Success);
        Assert.False(store.SetMode("second", false, first.WorldId, 7).Success);
        Assert.False(store.CanEnter("first", second.WorldId));
        Assert.Single(store.ListGames("first"));
        Assert.DoesNotContain(store.ListKeys("second"), key => key.Id == invite.Key.Id);
        Assert.Equal(2, store.ListGames("admin", true).Count);
        Assert.Empty(store.ListGames("", true));
        Assert.Empty(store.ListKeys("", true));
    }

    [Fact]
    public void AdministratorsCanManageGamesButCannotCreateWithoutAHostGrant()
    {
        var store = NewStore();
        Assert.False(store.CreateGame("admin", "EU", 13, "Game").Success);
        var game = Create(store, "host");
        Assert.True(store.SetMode("admin", true, game.WorldId, 7).Success);
        Assert.Equal(7u, store.GetGame(game.WorldId)!.GameModeId);
        var invite = store.IssuePlayerKey("admin", true, game.WorldId);
        Assert.True(invite.Success);
        Assert.True(store.RevokeKey("admin", true, invite.Key!.Id).Success);
        Assert.True(store.CloseGame("admin", true, game.WorldId).Success);
    }

    [Fact]
    public void HostCanChooseEverySupportedMode()
    {
        var store = NewStore();
        var game = Create(store, "host");
        foreach (var mode in new uint[] { 5, 7, 13 })
        {
            Assert.True(store.SetMode("host", false, game.WorldId, mode).Success);
            Assert.Equal(mode, store.GetGame(game.WorldId)!.GameModeId);
        }
        Assert.False(store.SetMode("host", false, game.WorldId, 1).Success);
    }

    [Fact]
    public void ExpiryStartsAtIssuanceAndExpiresAtTheExactBoundary()
    {
        var now = DateTimeOffset.Parse("2026-09-06T12:00:00Z");
        var store = new HostedGameStore(worlds: Worlds, utcNow: () => now);
        var key = store.IssueHostKey("admin", true, "EU", TimeSpan.FromMinutes(10));
        now = now.AddMinutes(9);
        Assert.True(store.Redeem("host", key.Secret!).Success);
        var game = store.CreateGame("host", "EU", 13, "Game").Game!;
        var invite = store.IssuePlayerKey("host", false, game.WorldId, TimeSpan.FromHours(1));
        Assert.Equal(key.Key!.ExpiresAt, invite.Key!.ExpiresAt);
        Assert.True(store.Redeem("friend", invite.Secret!).Success);
        now = now.AddMinutes(1).AddTicks(-1);
        Assert.True(store.CanEnter("host", game.WorldId));
        Assert.True(store.CanEnter("friend", game.WorldId));
        now = now.AddTicks(1);
        Assert.False(store.CanEnter("host", game.WorldId));
        Assert.False(store.CanEnter("friend", game.WorldId));
        Assert.False(store.GetGame(game.WorldId)!.IsActive);
        Assert.False(store.Redeem("host", key.Secret!).Success);
        Assert.False(store.IssuePlayerKey("host", false, game.WorldId).Success);
    }

    [Fact]
    public void TemporaryPlayerAccessExpiresWithoutEndingItsGame()
    {
        var now = DateTimeOffset.Parse("2026-09-06T12:00:00Z");
        var store = new HostedGameStore(worlds: Worlds, utcNow: () => now);
        var game = Create(store, "host");
        var invite = store.IssuePlayerKey("host", false, game.WorldId, TimeSpan.FromMinutes(5));
        Assert.True(store.Redeem("friend", invite.Secret!).Success);
        now = now.AddMinutes(5);
        Assert.False(store.CanEnter("friend", game.WorldId));
        Assert.True(store.CanEnter("host", game.WorldId));
        Assert.True(store.GetGame(game.WorldId)!.IsActive);
    }

    [Fact]
    public void NonpositiveOrOverflowingLifetimesFailWithoutIssuingKeys()
    {
        var now = DateTimeOffset.MaxValue.AddMinutes(-1);
        var store = new HostedGameStore(worlds: Worlds, utcNow: () => now);
        Assert.False(store.IssueHostKey("admin", true, "EU", TimeSpan.Zero).Success);
        Assert.False(store.IssueHostKey("admin", true, "EU", TimeSpan.FromMinutes(-1)).Success);
        Assert.False(store.IssueHostKey("admin", true, "EU", TimeSpan.FromMinutes(2)).Success);
        Assert.Empty(store.ListKeys("admin", true));
    }

    [Fact]
    public void RevokingParentHostKeyInvalidatesAllItsGamesAndPlayerKeys()
    {
        var store = NewStore();
        var hostKey = GrantHost(store, "host");
        var first = store.CreateGame("host", "EU", 13, "First").Game!;
        var second = store.CreateGame("host", "EU", 5, "Second").Game!;
        var invite = store.IssuePlayerKey("host", false, first.WorldId);
        Assert.True(store.Redeem("friend", invite.Secret!).Success);
        Assert.False(store.RevokeKey("host", false, hostKey.Key!.Id).Success);
        Assert.True(store.RevokeKey("admin", true, hostKey.Key.Id).Success);
        Assert.False(store.CanEnter("host", first.WorldId));
        Assert.False(store.CanEnter("friend", first.WorldId));
        Assert.False(store.GetGame(second.WorldId)!.IsActive);
        Assert.False(store.Redeem("another", invite.Secret!).Success);
        Assert.All(store.ListKeys("admin", true), key => Assert.False(key.IsActive));
    }

    [Fact]
    public void ClosingAndReusingSlotNeverRevivesOldInvites()
    {
        var store = NewStore();
        var original = Create(store, "host");
        var invite = store.IssuePlayerKey("host", false, original.WorldId);
        Assert.True(store.Redeem("friend", invite.Secret!).Success);
        Assert.True(store.CloseGame("host", false, original.WorldId).Success);
        var replacement = store.CreateGame("host", "EU", 7, "Replacement").Game!;
        Assert.Equal(original.WorldId, replacement.WorldId);
        Assert.NotEqual(original.Id, replacement.Id);
        Assert.False(store.CanEnter("friend", replacement.WorldId));
        Assert.False(store.Redeem("friend", invite.Secret!).Success);
        Assert.False(Assert.Single(store.ListKeys("friend")).IsActive);
        Assert.Equal(replacement.Id, store.GetGame(original.WorldId)!.Id);
    }

    [Fact]
    public void ExpiredSlotsCanBeReusedWithoutRevivingPreviousInstanceAfterClockCorrection()
    {
        var now = DateTimeOffset.Parse("2026-09-06T12:00:00Z");
        var store = new HostedGameStore(worlds: Worlds, utcNow: () => now);
        var key = store.IssueHostKey("admin", true, "EU", TimeSpan.FromMinutes(1));
        Assert.True(store.Redeem("first", key.Secret!).Success);
        var original = store.CreateGame("first", "EU", 13, "First").Game!;
        var invite = store.IssuePlayerKey("first", false, original.WorldId);
        Assert.True(store.Redeem("friend", invite.Secret!).Success);
        now = now.AddMinutes(1);
        var replacement = Create(store, "second");
        Assert.Equal(original.WorldId, replacement.WorldId);
        now = now.AddMinutes(-1);
        Assert.False(store.CanEnter("first", replacement.WorldId));
        Assert.False(store.CanEnter("friend", replacement.WorldId));
        Assert.False(store.Redeem("friend", invite.Secret!).Success);
    }

    [Fact]
    public void PersistenceRestoresAccountsGamesRevocationAndOnlyHashesSecrets()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "hosted.json");
        var store = new HostedGameStore(path, Worlds);
        var hostKey = GrantHost(store, "host");
        var game = store.CreateGame("host", "EU", 7, "Friends").Game!;
        var playerKey = store.IssuePlayerKey("host", false, game.WorldId);
        Assert.True(store.Redeem("friend", playerKey.Secret!).Success);
        var disk = File.ReadAllText(path);
        Assert.DoesNotContain(hostKey.Secret!, disk);
        Assert.DoesNotContain(playerKey.Secret!, disk);
        Assert.DoesNotContain("HGK-", disk);
        Assert.Contains("\"Hash\"", disk);
        var restored = new HostedGameStore(path, Worlds);
        Assert.True(restored.CanEnter("host", game.WorldId));
        Assert.True(restored.CanEnter("friend", game.WorldId));
        Assert.Equal(game.Id, restored.GetGame(game.WorldId)!.Id);
        Assert.Equal(7u, restored.GetGame(game.WorldId)!.GameModeId);
        Assert.False(restored.Redeem("other", playerKey.Secret!).Success);
        Assert.True(restored.RevokeKey("admin", true, hostKey.Key!.Id).Success);
        Assert.False(new HostedGameStore(path, Worlds).CanEnter("friend", game.WorldId));
    }

    [Fact]
    public void RemovedOrRelocatedConfiguredSlotsDoNotGrantOldAccess()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "hosted.json");
        var game = Create(new HostedGameStore(path, Worlds), "host");
        var removed = new HostedGameStore(path, Worlds.Where(world => world.WorldId != game.WorldId));
        Assert.False(removed.CanEnter("host", game.WorldId));
        var relocated = new HostedGameStore(path, Worlds.Select(world => world.WorldId == game.WorldId ? world with { Region = "US" } : world));
        Assert.False(relocated.CanEnter("host", game.WorldId));
    }

    [Fact]
    public void FailedDiskCommitDoesNotIssueASecretOrChangeInMemoryPermissions()
    {
        using var temporary = new TemporaryDirectory();
        var blocker = Path.Combine(temporary.Path, "blocker");
        File.WriteAllText(blocker, "A file prevents creating the permissions directory.");
        var store = new HostedGameStore(Path.Combine(blocker, "hosted.json"), Worlds);
        var result = store.IssueHostKey("admin", true, "EU");
        Assert.False(result.Success);
        Assert.Null(result.Secret);
        Assert.Empty(store.ListKeys("admin", true));
    }

    [Fact]
    public void FailedRevocationRollsBackMemoryAndPreservesExistingFile()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "hosted.json");
        var store = new HostedGameStore(path, Worlds);
        var key = GrantHost(store, "host");
        var game = store.CreateGame("host", "EU", 13, "Game").Game!;
        var previous = File.ReadAllBytes(path);
        using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Windows refuses replacement while this handle denies delete sharing.
            if (OperatingSystem.IsWindows())
            {
                Assert.False(store.RevokeKey("admin", true, key.Key!.Id).Success);
                Assert.True(store.CanEnter("host", game.WorldId));
            }
        }
        Assert.Equal(previous, File.ReadAllBytes(path));
        Assert.True(new HostedGameStore(path, Worlds).CanEnter("host", game.WorldId));
    }

    [Fact]
    public void CorruptPermissionsFailClosedAndArePreserved()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "hosted.json");
        const string corrupt = "{broken";
        File.WriteAllText(path, corrupt);
        Assert.Throws<InvalidDataException>(() => new HostedGameStore(path, Worlds));
        Assert.Equal(corrupt, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Keys\":[],\"Games\":[]}")]
    [InlineData("{\"Version\":1,\"Games\":[]}")]
    [InlineData("{\"Version\":1,\"Keys\":[]}")]
    [InlineData("{\"Version\":2,\"Keys\":[],\"Games\":[]}")]
    public void MissingStateFieldsOrUnknownVersionCannotSilentlyDiscardPermissions(string contents)
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "hosted.json");
        File.WriteAllText(path, contents);
        Assert.Throws<InvalidDataException>(() => new HostedGameStore(path, Worlds));
        Assert.Equal(contents, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("Keys", "Kind")]
    [InlineData("Keys", "Revoked")]
    [InlineData("Keys", "TargetAccount")]
    [InlineData("Keys", "Account")]
    [InlineData("Keys", "ExpiresAt")]
    [InlineData("Games", "Closed")]
    [InlineData("Games", "HostKeyId")]
    public void MissingNestedSecurityFieldsCannotRestoreDefaultPermissions(string collection, string property)
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "hosted.json");
        var store = new HostedGameStore(path, Worlds);
        var key = GrantHost(store, "host");
        var game = store.CreateGame("host", "EU", 13, "Game").Game!;
        Assert.True(store.CloseGame("host", false, game.WorldId).Success);
        Assert.True(store.RevokeKey("admin", true, key.Key!.Id).Success);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.True(json[collection]![0]!.AsObject().Remove(property));
        var contents = json.ToJsonString();
        File.WriteAllText(path, contents);
        Assert.Throws<InvalidDataException>(() => new HostedGameStore(path, Worlds));
        Assert.Equal(contents, File.ReadAllText(path));
    }

    [Fact]
    public void PersistedNamesCannotInjectNativeTextFieldMarkup()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "hosted.json");
        Create(new HostedGameStore(path, Worlds), "host");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["Games"]![0]!["Name"] = "<a href='event:unexpected'>Injected</a>";
        File.WriteAllText(path, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => new HostedGameStore(path, Worlds));
    }

    private static HostedGameStore NewStore() => new(worlds: Worlds);

    private static HostedGameResult GrantHost(HostedGameStore store, string account)
    {
        var result = store.IssueHostKey("admin", true, "EU", targetAccount: account);
        Assert.True(result.Success);
        Assert.True(store.Redeem(account, result.Secret!).Success);
        return result;
    }

    private static HostedGameInfo Create(HostedGameStore store, string account)
    {
        GrantHost(store, account);
        var result = store.CreateGame(account, "EU", 13, "Friends");
        Assert.True(result.Success);
        return result.Game!;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cranberry-hosted-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
