using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Cranberry.Protocol;
using Cranberry.Zone.Match;
using Microsoft.Data.Sqlite;

if (args.FirstOrDefault() == "--verify-legacy")
{
    VerifyLegacy(args[1], args[2]);
    return;
}

// Isolated fixtures only. Never point this at the live ranked store.
string output = Path.GetFullPath(args.ElementAtOrDefault(0) ?? throw new ArgumentException("Supply a new output directory."));
if (Directory.Exists(output)) throw new ArgumentException("Output directory must not already exist.");
Directory.CreateDirectory(output);
int population = int.Parse(args.ElementAtOrDefault(1) ?? "10000");
if (population is < 150 or > 100000) throw new ArgumentOutOfRangeException(nameof(population));
string root = Path.Combine(output, "state");
Directory.CreateDirectory(root);
var identities = Enumerable.Range(1, population).Select(i => (Account: "probe-" + i, Guid: (ulong)i, Name: "Probe " + i)).ToArray();
foreach (var identity in identities)
{
    var profile = new RankedProfile(10, Enumerable.Range(1, 10).Select(i =>
        new RankedResult("fixture-" + i, 1, 15, 190000 + (int)(identity.Guid % 100))).ToArray())
    { Wins = 10, TotalKills = 150, TotalPoints = (uint)(1900000 + identity.Guid % 100 * 10) };
    File.WriteAllText(Path.Combine(root, RankedScoreStore.AccountKey(identity.Account) + "-Solo.json"), JsonSerializer.Serialize(profile));
}
var boot = Stopwatch.StartNew();
var store = new RankedScoreStore(root, backgroundWrites: true);
store.SeedIdentities(identities);
boot.Stop();
// Warm JIT and packet buffers before collecting measurements.
for (int i = 0; i < 1000; i++) Query(i);
long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
long[] latencies = new long[100000];
var measured = Stopwatch.StartNew();
for (int i = 0; i < latencies.Length; i++)
{
    long start = Stopwatch.GetTimestamp();
    Query(i);
    latencies[i] = Stopwatch.GetTimestamp() - start;
}
measured.Stop();
long queryAllocations = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
Array.Sort(latencies);

// New match completions while the same read/serialize work continues on this thread.
var commitClock = Stopwatch.StartNew();
var commits = Enumerable.Range(1, 150).Select(i =>
    store.CompleteAsync("probe-" + i, MatchMode.Solo, new("live-burst", (uint)i, i % 10, 200000, i != 1))).ToArray();
int concurrentQueries = 0;
while (commits.Any(t => !t.IsCompleted) && commitClock.Elapsed < TimeSpan.FromSeconds(30))
{
    Query(concurrentQueries++);
    if (concurrentQueries % 100 == 0) Thread.Yield();
}
await Task.WhenAll(commits).WaitAsync(TimeSpan.FromSeconds(30));
store.FlushPending();
commitClock.Stop();
var restored = new RankedScoreStore(root);
if (Enumerable.Range(1, 150).Any(i => restored.Read("probe-" + i, MatchMode.Solo).Matches != 11))
    throw new InvalidOperationException("Restart lost a committed result.");
var sample = store.Leaderboard.Select(RankedScoreStore.AccountKey("probe-1"), MatchMode.Solo, 7, 1, false);
using var wire = new PacketWriter();
LeaderboardPackets.WriteLeaderboard(wire, 0, 1, RankedScoreStore.AccountKey("probe-1"), sample);
var report = new
{
    population, importedProfiles = store.ImportedProfiles, startupMs = boot.Elapsed.TotalMilliseconds,
    queries = latencies.Length, querySeconds = measured.Elapsed.TotalSeconds,
    requestsPerSecond = latencies.Length / measured.Elapsed.TotalSeconds,
    queryP50Microseconds = Micros(latencies[latencies.Length / 2]),
    queryP95Microseconds = Micros(latencies[(int)(latencies.Length * .95)]),
    queryP99Microseconds = Micros(latencies[(int)(latencies.Length * .99)]),
    allocatedBytesPerQuery = queryAllocations / latencies.Length,
    resultRows = sample.Length, packetBytes = wire.Written.Length,
    concurrentQueriesDuring150Commits = concurrentQueries,
    commitBurstMs = commitClock.Elapsed.TotalMilliseconds,
    pendingWrites = store.PendingWrites, restartVerified = true,
    limitation = "Local cache/serialization and SQLite benchmark, not an Internet or full gameplay capacity test.",
};
string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(output, "result.json"), json);
Console.WriteLine(json);

void Query(int n)
{
    int player = n % population + 1;
    string key = RankedScoreStore.AccountKey("probe-" + player);
    var rows = store.Leaderboard.Select(key, MatchMode.Solo, 7, 1, n % 2 == 0);
    using var writer = new PacketWriter();
    LeaderboardPackets.WriteLeaderboard(writer, 0, (ulong)player, key, rows);
}
static double Micros(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

static void VerifyLegacy(string source, string output)
{
    output = Path.GetFullPath(output);
    if (Directory.Exists(output)) throw new ArgumentException("Output directory must not already exist.");
    var originals = Directory.GetFiles(Path.GetFullPath(source), "*.json")
        .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
    if (originals.Count == 0) throw new ArgumentException("No legacy scores found.");
    string copy = Path.Combine(output, "state");
    Directory.CreateDirectory(copy);
    foreach (var (name, bytes) in originals) File.WriteAllBytes(Path.Combine(copy, name!), bytes);
    var store = new RankedScoreStore(copy);
    if (store.ImportedProfiles != originals.Count) throw new InvalidOperationException("Profile import count differs.");
    int verifiedReceipts = 0;
    using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
    {
        db.Open();
        foreach (var (name, bytes) in originals)
        {
            var original = JsonSerializer.Deserialize<RankedProfile>(bytes)!;
            string stem = Path.GetFileNameWithoutExtension(name)!;
            using var profile = db.CreateCommand();
            profile.CommandText = "SELECT profile FROM profiles WHERE account_key=$a AND mode=$m";
            profile.Parameters.AddWithValue("$a", stem[..64]);
            profile.Parameters.AddWithValue("$m", (int)Enum.Parse<MatchMode>(stem[65..]));
            var migrated = JsonSerializer.Deserialize<RankedProfile>((string)profile.ExecuteScalar()!)!;
            if (JsonSerializer.Serialize(original with { CompletedIds = [] }) != JsonSerializer.Serialize(migrated))
                throw new InvalidOperationException("Migrated statistics differ.");
            profile.CommandText = "SELECT result_id FROM receipts WHERE account_key=$a AND mode=$m";
            using var reader = profile.ExecuteReader();
            var receipts = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read()) receipts.Add(reader.GetString(0));
            if (!receipts.SetEquals(original.CompletedIds.Concat(original.Best.Select(r => r.Id))))
                throw new InvalidOperationException("Replay receipts differ.");
            verifiedReceipts += receipts.Count;
            if (!SHA256.HashData(bytes).SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, name!)))))
                throw new InvalidOperationException("Source changed during verification; repeat against a stable snapshot.");
        }
    }
    if (new RankedScoreStore(copy).ImportedProfiles != 0) throw new InvalidOperationException("Import repeated after restart.");
    string report = JsonSerializer.Serialize(new
    {
        importedProfiles = store.ImportedProfiles, verifiedReceipts, allStatisticsPreserved = true,
        originalFilesUnchanged = true, restartImportedProfiles = 0,
    }, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(output, "result.json"), report);
    Console.WriteLine(report);
}
