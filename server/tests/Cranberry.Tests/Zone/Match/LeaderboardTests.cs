using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cranberry.Protocol;
using Cranberry.Zone.Match;
using Microsoft.Data.Sqlite;

namespace Cranberry.Tests.Zone.MatchEndgame;

public sealed class LeaderboardTests
{
    public static byte[] Selection(uint mode = 1, uint tier = 7, uint division = 1, uint filter = 1)
    {
        using var w = new PacketWriter();
        w.WriteRaw(Convert.FromHexString("670a00000000000000000000000000000000"));
        w.WriteUInt32(mode); w.WriteUInt32(0); w.WriteUInt32(0);
        w.WriteUInt32(tier); w.WriteUInt32(division); w.WriteUInt32(filter);
        return w.Written.ToArray();
    }

    public static byte[] Other(ulong guid, uint mode = 1)
    {
        using var w = new PacketWriter();
        w.WriteRaw(Convert.FromHexString("671d0000000000000000"));
        w.WriteUInt64(guid); w.WriteUInt32(mode); w.WriteUInt32(0);
        return w.Written.ToArray();
    }

    [Fact]
    public void NativeRequestsUseTwoUlongsThenModeAndExactWidths()
    {
        var select = Selection(3, 6, 4, 0);
        Assert.Equal(42, select.Length);
        Assert.True(SelectLeaderboardRequest.TryParse(select, out var request));
        Assert.Equal(new SelectLeaderboardRequest(0, MatchMode.Fives, 6, 4, true), request);
        var other = Other(0x1020304050607080, 2);
        Assert.Equal(26, other.Length);
        Assert.True(PlayerTopTenRequest.TryParse(other, out var top));
        Assert.Equal(new PlayerTopTenRequest(0, 0x1020304050607080, MatchMode.Duos), top);
        for (int n = 0; n < select.Length; n++) Assert.False(SelectLeaderboardRequest.TryParse(select.AsSpan(0, n), out _));
        for (int n = 0; n < other.Length; n++) Assert.False(PlayerTopTenRequest.TryParse(other.AsSpan(0, n), out _));
        Assert.False(SelectLeaderboardRequest.TryParse([.. select, 0], out _));
        Assert.False(PlayerTopTenRequest.TryParse([.. other, 0], out _));
        Assert.False(SelectLeaderboardRequest.TryParse(Selection(4), out _));
        Assert.False(SelectLeaderboardRequest.TryParse(Selection(tier: 8), out _));
        Assert.False(SelectLeaderboardRequest.TryParse(Selection(division: 6), out _));
        Assert.False(SelectLeaderboardRequest.TryParse(Selection(filter: uint.MaxValue), out _));
        Assert.False(PlayerTopTenRequest.TryParse(Other(0), out _));
    }

    private static RankedProfile Profile(int points = 190000, int matches = 10) =>
        new(matches, Enumerable.Range(0, Math.Min(10, matches)).Select(n => new RankedResult("game-" + n, 1, 15, points)).ToArray());

    [Fact]
    public void NativeLeaderboardRowsIncludeSevenWordsAndTheSelfIndexFlag()
    {
        var profile = Profile();
        var rows = new[]
        {
            new RankedLeaderboardEntry(new("a", 0x1234, "Éva"), profile, new(RankedTier.Royalty, 1), 1),
            new RankedLeaderboardEntry(new("b", 0x5678, "Bob"), profile, new(RankedTier.Royalty, 1), 2),
        };
        using var w = new PacketWriter();
        LeaderboardPackets.WriteLeaderboard(w, 3, 0x5678, "b", rows);
        Assert.Equal(22 + 2 * 45 + Encoding.UTF8.GetByteCount("ÉvaBob"), w.Written.Length);
        var r = new PacketReader(w.Written);
        Assert.Equal(0x67, r.ReadByte()); Assert.Equal(0x0b, r.ReadByte());
        Assert.Equal(3UL, r.ReadUInt64()); Assert.Equal(0x5678UL, r.ReadUInt64()); Assert.Equal(2, r.ReadInt32());
        foreach (var row in rows)
        {
            Assert.Equal(row.Identity.CharacterGuid, r.ReadUInt64());
            Assert.Equal(row.Position, r.ReadUInt32()); Assert.Equal(row.Identity.Name, r.ReadString());
            Assert.Equal(1900000u, r.ReadUInt32());
            for (int i = 0; i < 6; i++) Assert.Equal(0u, r.ReadUInt32());
            Assert.Equal(row.Identity.AccountKey == "b", r.ReadBool());
        }
        Assert.True(r.AtEnd);
    }

    [Fact]
    public void OtherPlayerReplyReplacesTheNameModeAndAllBestTenRowsIncludingEmptyResults()
    {
        using var w = new PacketWriter();
        var request = new PlayerTopTenRequest(123, 555, MatchMode.Fives);
        var player = new RankedLeaderboardEntry(new("a", 555, "Alice"), Profile(), new(RankedTier.Royalty, 1), 1);
        LeaderboardPackets.WritePlayerTopTen(w, request, player);
        var r = new PacketReader(w.Written);
        Assert.Equal(42 + 5 + 360, w.Written.Length);
        Assert.Equal(0x67, r.ReadByte()); Assert.Equal(0x1e, r.ReadByte());
        Assert.Equal(123UL, r.ReadUInt64()); Assert.Equal(555UL, r.ReadUInt64());
        Assert.Equal("Alice", r.ReadString()); Assert.Equal(3u, r.ReadUInt32());
        Assert.Equal(7u, r.ReadUInt32()); Assert.Equal(1u, r.ReadUInt32()); Assert.Equal(1900000u, r.ReadUInt32());
        Assert.Equal(10, r.ReadInt32());
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(0UL, r.ReadUInt64()); Assert.Equal(190000u, r.ReadUInt32());
            Assert.Equal(1u, r.ReadUInt32()); Assert.Equal(15u, r.ReadUInt32()); Assert.Equal(0u, r.ReadUInt32());
            Assert.Equal(1u, r.ReadUInt32()); Assert.Equal(15u, r.ReadUInt32()); Assert.Equal(0u, r.ReadUInt32());
        }
        Assert.True(r.AtEnd);
        using var empty = new PacketWriter();
        LeaderboardPackets.WritePlayerTopTen(empty, request, null);
        Assert.Equal(42, empty.Written.Length);
        Assert.Equal(0, BitConverter.ToInt32(empty.Written[^4..]));
    }

    [Fact]
    public void LeaderboardSeparatesModesFiltersDivisionsCentersMeAndUsesStableTies()
    {
        var identities = Enumerable.Range(1, 130).Select(i => new RankedIdentity("account-" + i.ToString("D4"), (ulong)i, "Player " + i)).ToArray();
        var profiles = identities.Select(i => KeyValuePair.Create((i.AccountKey, MatchMode.Solo), Profile())).ToList();
        profiles.Add(KeyValuePair.Create((identities[0].AccountKey, MatchMode.Duos), Profile(180000)));
        profiles.Add(KeyValuePair.Create(("new", MatchMode.Solo), Profile(matches: 9)));
        var board = new RankedLeaderboard([.. identities, new("new", 1000, "Unranked")], profiles);
        var top = board.Select(identities[69].AccountKey, MatchMode.Solo, 7, 1, false);
        Assert.Equal(50, top.Length); Assert.Equal(1UL, top[0].Identity.CharacterGuid); Assert.Equal(50u, top[^1].Position);
        var around = board.Select(identities[69].AccountKey, MatchMode.Solo, 1, 5, true);
        Assert.Equal(50, around.Length); Assert.Equal(70UL, around[25].Identity.CharacterGuid);
        var tail = board.Select(identities[^1].AccountKey, MatchMode.Solo, 7, 1, true);
        Assert.Equal(130u, tail[^1].Position); Assert.Equal(50, tail.Length);
        Assert.Empty(board.Select("new", MatchMode.Solo, 0, 5, true));
        Assert.Equal(0u, board.Position("new", MatchMode.Solo));
        Assert.Empty(board.Select("a", MatchMode.Solo, 6, 1, false));
        Assert.Single(board.Select("a", MatchMode.Duos, 7, 5, false));
        Assert.NotNull(board.Player(1000, MatchMode.Solo)); // Personal Top 10 remains available before qualification.
        Assert.Null(board.Player(1000, MatchMode.Fives));
    }

    [Fact]
    public void BudgetPermitsUiBurstsButBoundsSustainedRequestsAndRecovers()
    {
        var budget = new LeaderboardRequestBudget();
        for (int i = 0; i < 12; i++) Assert.True(budget.TryTake(1000));
        Assert.False(budget.TryTake(1000)); Assert.False(budget.TryTake(1249));
        Assert.True(budget.TryTake(1250)); Assert.False(budget.TryTake(1250));
        Assert.True(budget.TryTake(99999));
    }

    [Fact]
    public void LegacyMigrationPreservesTotalsIdsKdAndFilesWithoutImportingTwice()
    {
        string root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var legacy = Profile() with { Matches = 11, CompletedIds = ["discarded-game"], Wins = 10, TotalKills = 150, TotalPoints = 1901000, KdMatches = 1, KdDeaths = 1, KdKills = 3 };
            string filename = Path.Combine(root, RankedScoreStore.AccountKey("a") + "-Solo.json");
            string original = JsonSerializer.Serialize(legacy);
            File.WriteAllText(filename, original);
            var store = new RankedScoreStore(root);
            store.RegisterIdentity("a", 123, "Alice");
            Assert.Equal(1, store.ImportedProfiles);
            Assert.Equal(11, store.Complete("a", MatchMode.Solo, new("discarded-game", 175, 0, 1000)).Matches);
            Assert.Equal(11, store.Complete("a", MatchMode.Solo, legacy.Best[0]).Matches);
            store.Complete("a", MatchMode.Solo, new("new-game", 1, 20, 195000, false));
            store = new RankedScoreStore(root);
            var result = store.Read("a", MatchMode.Solo);
            Assert.Equal(0, store.ImportedProfiles); Assert.Equal(12, result.Matches);
            Assert.Equal(2096000u, result.TotalPoints); Assert.Equal(170u, result.TotalKills);
            Assert.Equal(2, result.KdMatches); Assert.Equal(23u, result.KdKills); Assert.Equal(1u, result.KdDeaths);
            Assert.Equal("Alice", store.Leaderboard.Player(123, MatchMode.Solo)!.Identity.Name);
            Assert.Equal(original, File.ReadAllText(filename));
            Assert.Empty(result.CompletedIds); // Replay IDs now live in the indexed receipts table.
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DatabaseLockCannotBlockMenuReadsAndPendingCommitRetriesWithoutLosingOrDuplicatingResults()
    {
        string root = TempRoot();
        try
        {
            var store = new RankedScoreStore(root, backgroundWrites: true);
            store.RegisterIdentity("a", 7, "Alice");
            await store.CompleteAsync("a", MatchMode.Solo, new("old", 1, 1, 176000));
            store.FlushPending();
            using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString());
            blocker.Open();
            using (var transaction = blocker.BeginTransaction())
            {
                Task<RankedProfile> pending = store.CompleteAsync("a", MatchMode.Solo, new("new", 1, 2, 177000, false));
                // Force at least one SQLite timeout/retry, then prove the committed view remains readable.
                await Task.Delay(2300);
                Assert.False(pending.IsCompleted);
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < 1000; i++)
                {
                    Assert.Equal(1, store.Read("a", MatchMode.Solo).Matches);
                    Assert.Empty(store.Leaderboard.Select(RankedScoreStore.AccountKey("a"), MatchMode.Solo, 7, 1, false));
                }
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"Cached reads blocked for {timer.Elapsed}.");
                transaction.Rollback();
                Assert.Equal(2, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Matches);
            }
            var duplicate = await store.CompleteAsync("a", MatchMode.Solo, new("new", 1, 2, 177000, false));
            Assert.Equal(2, duplicate.Matches);
            store.FlushPending();
            Assert.Equal(2, new RankedScoreStore(root).Read("a", MatchMode.Solo).Matches);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BurstResultsSurviveRestartWithIndependentModesAndAuthoritativeIdentityUpdates()
    {
        string root = TempRoot();
        try
        {
            var store = new RankedScoreStore(root, backgroundWrites: true);
            var tasks = new List<Task<RankedProfile>>();
            foreach (var mode in new[] { MatchMode.Solo, MatchMode.Duos, MatchMode.Fives })
                for (int player = 1; player <= 150; player++)
                {
                    store.RegisterIdentity("account-" + player, (ulong)player, "Player " + player);
                    tasks.Add(store.CompleteAsync("account-" + player, mode, new("match", (uint)player, 1, 1000, player != 1)));
                }
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));
            store.RegisterIdentity("account-1", 1001, "Renamed");
            store.FlushPending();
            var restored = new RankedScoreStore(root);
            Assert.Equal(0, store.PendingWrites);
            Assert.Equal(1, restored.Read("account-150", MatchMode.Fives).Matches);
            Assert.Equal("Renamed", restored.Leaderboard.Player(1001, MatchMode.Solo)!.Identity.Name);
            Assert.Null(restored.Leaderboard.Player(1, MatchMode.Solo));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "cranberry-ranked-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InvalidStoredTotalsDoNotStrandOtherPlayersOrAcknowledgeAnUnsavedResult()
    {
        string root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, RankedScoreStore.AccountKey("bad") + "-Solo.json"),
                JsonSerializer.Serialize(new RankedProfile(10, []) { TotalPoints = uint.MaxValue }));
            var store = new RankedScoreStore(root, backgroundWrites: true);
            var rejected = store.CompleteAsync("bad", MatchMode.Solo, new("overflow", 1, 0, 175000));
            var accepted = store.CompleteAsync("good", MatchMode.Solo, new("win", 1, 0, 175000));
            await Assert.ThrowsAsync<OverflowException>(async () => await rejected.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, (await accepted.WaitAsync(TimeSpan.FromSeconds(10))).Matches);
            Assert.Throws<IOException>(() => store.FlushPending());
            Assert.Equal(0, store.PendingWrites);
            var restored = new RankedScoreStore(root);
            Assert.Equal(10, restored.Read("bad", MatchMode.Solo).Matches);
            Assert.Equal(1, restored.Read("good", MatchMode.Solo).Matches);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RapidIdentityChangesAndResultsPublishTheSameNameThatSurvivesRestart()
    {
        string root = TempRoot();
        try
        {
            var store = new RankedScoreStore(root, backgroundWrites: true);
            store.RegisterIdentity("a", 1, "Before");
            store.Complete("a", MatchMode.Solo, new("first", 1, 0, 175000));
            using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString());
            blocker.Open();
            using var transaction = blocker.BeginTransaction();
            store.RegisterIdentity("a", 1, "Intermediate");
            var completion = store.CompleteAsync("a", MatchMode.Solo, new("second", 1, 0, 175000));
            store.RegisterIdentity("a", 2, "Latest");
            transaction.Rollback();
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            store.FlushPending();
            var restored = new RankedScoreStore(root);
            Assert.Equal("Latest", store.Leaderboard.Player(2, MatchMode.Solo)!.Identity.Name);
            Assert.Equal("Latest", restored.Leaderboard.Player(2, MatchMode.Solo)!.Identity.Name);
            Assert.Null(restored.Leaderboard.Player(1, MatchMode.Solo));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FullWriteQueueRefusesNewWorkWithoutBlockingAndDrainsEveryAcceptedResult()
    {
        string root = TempRoot();
        try
        {
            var store = new RankedScoreStore(root, backgroundWrites: true, maxPendingWrites: 8);
            using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString());
            blocker.Open();
            using var transaction = blocker.BeginTransaction();
            var tasks = Enumerable.Range(1, 8).Select(i =>
                store.CompleteAsync("a", MatchMode.Solo, new("game-" + i, 1, 1, 176000))).ToArray();
            Assert.Throws<IOException>(() => { _ = store.CompleteAsync("a", MatchMode.Solo, new("overflow", 1, 1, 176000)); });
            Assert.Equal(8, store.PendingWrites);
            Assert.Equal(0, store.Read("a", MatchMode.Solo).Matches);
            transaction.Rollback();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
            store.FlushPending();
            Assert.Equal(8, store.Read("a", MatchMode.Solo).Matches);
            Assert.Equal(0, store.PendingWrites);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UnplayedMenuAccountsDoNotQueueOrCreateRankedProfiles()
    {
        string root = TempRoot();
        try
        {
            var store = new RankedScoreStore(root, backgroundWrites: true);
            for (int i = 1; i <= 2000; i++) store.RegisterIdentity("menu-" + i, (ulong)i, "Menu " + i);
            Assert.Equal(0, store.PendingWrites);
            Assert.Equal(0, store.Read("menu-1", MatchMode.Solo).Matches);
        }
        finally { Directory.Delete(root, true); }
    }
}
