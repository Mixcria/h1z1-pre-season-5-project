using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Cranberry.Launcher.Core;
using Cranberry.Launcher.Service;
using Cranberry.Login;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Match;

namespace Cranberry.Launcher.Tests;

public sealed class LeaderboardTests
{
    private sealed class Recorder : ITransportLog, IPacketRecorder
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) { }
    }

    [Fact]
    public async Task HttpsRanksTheBestTenSeparatelyForEachModeAndReturnsSelectedResultsAndAllMatchKd()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-leaderboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var options = new LauncherHostOptions { Port = port, ProximityVoiceEnabled = false };
            File.WriteAllText(Path.Combine(root, "launcher-host.json"), JsonSerializer.Serialize(options));
            var accounts = new LocalAccountDirectory("owner");
            var social = new SocialStore(Path.Combine(root, "state", "launcher", "social.json"), options.JoinCode, options.OwnerCode, accounts);
            var alice = social.Register(new("Alice", "test-password-123", options.JoinCode));
            var bob = social.Register(new("Bob", "test-password-123", options.JoinCode));
            var charlie = social.Register(new("Charlie", "test-password-123", options.JoinCode));
            social.Register(new("Nobody", "test-password-123", options.JoinCode));
            string economy = Path.Combine(root, "state", "economy");
            var store = new RankedScoreStore(Path.Combine(economy, "ranked-preseason5"));
            for (int n = 1; n <= 12; n++) store.Complete(alice.AccountId, MatchMode.Solo, new("alice-" + n, 2, n, n * 1000, n % 3 != 0));
            store.Complete(bob.AccountId, MatchMode.Solo, new("bob", 1, 8, 80000, false));
            store.Complete(charlie.AccountId, MatchMode.Solo, new("charlie", 1, 8, 80000, true)); // A dead squad winner must count as a death.
            store.Complete(alice.AccountId, MatchMode.Duos, new("duo", 1, 3, 178000, true));
            store.Complete(bob.AccountId, MatchMode.Fives, new("five", 1, 4, 179000, false));
            var log = new Recorder();
            var zone = new ZoneService(log, log, new GatewayTicketRegistry(), new ZoneOptions { EconomyStoreRoot = economy }) { Post = a => a() };
            await using var host = new LauncherHost(root, accounts, zone, 20042, 20043, social);
            await host.StartAsync();
            using var http = LauncherConnection.CreateHttp(new() { ServerUrl = $"https://localhost:{port}/", CertificateSha256 = host.CertificateSha256 });
            using var denied = await http.GetAsync("api/leaderboard/Solo");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            http.DefaultRequestHeaders.Authorization = new("Bearer", alice.Token);
            var solo = (await http.GetFromJsonAsync<LeaderboardView>("api/leaderboard/Solo"))!;
            Assert.Equal("Solo", solo.Mode); Assert.Equal(3, solo.TotalPlayers);
            Assert.Equal(new[] { "Bob", "Charlie", "Alice" }, solo.Players.Select(player => player.Name));
            Assert.Equal(3, solo.Me!.Position); Assert.Equal(75000, solo.Me.TotalScore);
            Assert.Equal(solo.Me.TotalScore, solo.Me.Best.Sum(score => score.Points));
            Assert.Equal(10, solo.Me.Best.Count); Assert.Equal(12000, solo.Me.Best[0].Points); Assert.Equal(3000, solo.Me.Best[^1].Points);
            Assert.Equal(12, solo.Me.Matches); Assert.Equal(12, solo.Me.KdMatches);
            Assert.Equal(78u, solo.Me.KdKills); Assert.Equal(8u, solo.Me.KdDeaths);
            Assert.Equal(0u, solo.Players[0].KdDeaths); Assert.Equal(1u, solo.Players[1].KdDeaths);
            var duos = (await http.GetFromJsonAsync<LeaderboardView>("api/leaderboard/duos"))!;
            Assert.Equal("Duos", duos.Mode); Assert.Single(duos.Players); Assert.Equal(178000, duos.Me!.TotalScore);
            Assert.Equal(1u, duos.Me.KdDeaths); Assert.Equal("Unranked", duos.Me.Tier);
            var fives = (await http.GetFromJsonAsync<LeaderboardView>("api/leaderboard/Fives"))!;
            Assert.Equal("Bob", Assert.Single(fives.Players).Name); Assert.Null(fives.Me);
            using var invalid = await http.GetAsync("api/leaderboard/Training"); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DeathTrackingSurvivesRestartAndDoesNotInventDeathsForLegacyResultsOrReplayResults()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-kd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RankedScoreStore(root);
            store.Complete("legacy", MatchMode.Solo, new("old", 1, 10, 185000));
            var tracked = new RankedResult("tracked", 2, 3, 133000, true);
            store.Complete("legacy", MatchMode.Solo, tracked);
            store = new RankedScoreStore(root);
            var profile = store.Complete("legacy", MatchMode.Solo, tracked);
            Assert.Equal(2, profile.Matches); Assert.Equal(13u, profile.TotalKills);
            Assert.Equal(1, profile.KdMatches); Assert.Equal(3u, profile.KdKills); Assert.Equal(1u, profile.KdDeaths);
            Assert.Equal(318000, profile.Points);
            Assert.Equal(0, store.Read("legacy", MatchMode.Fives).KdMatches);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
