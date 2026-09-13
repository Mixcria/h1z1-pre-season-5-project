using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Cranberry.Zone.Match;

/// <summary>
/// Durable ranked results and an immutable leaderboard. Runtime reads never open a file or wait
/// on a database lock. Production writes use a bounded queue and publish only after commit.
/// The synchronous Complete API is retained for offline tools and deterministic fixtures.
/// </summary>
public sealed class RankedScoreStore
{
    public const int MaxPendingWrites = 8192;
    private readonly string? _connectionString;
    private readonly bool _backgroundWrites;
    private readonly int _maxPendingWrites;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<(string AccountKey, MatchMode Mode), RankedProfile> _profiles = new();
    private readonly Dictionary<string, RankedIdentity> _identities = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RankedIdentity> _registeredIdentities = new(StringComparer.Ordinal);
    private readonly HashSet<(string AccountKey, MatchMode Mode, string Id)> _memoryReceipts = [];
    private readonly object _writeGate = new();
    private readonly object _queueGate = new();
    private readonly Queue<Operation> _queue = new();
    private Task _writer = Task.CompletedTask;
    private int _pending;
    private int _failedWrites;
    private RankedLeaderboard _leaderboard = RankedLeaderboard.Empty;
    private IReadOnlyDictionary<string, RankedIdentity> _identityView = new Dictionary<string, RankedIdentity>();
    public RankedLeaderboard Leaderboard => Volatile.Read(ref _leaderboard);
    public int PendingWrites { get { lock (_queueGate) return _pending; } }
    public int ImportedProfiles { get; private set; }
    public string? DatabasePath { get; }

    public RankedScoreStore(string? root, bool backgroundWrites = false, Action<string>? log = null, int maxPendingWrites = MaxPendingWrites)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPendingWrites, 1);
        _maxPendingWrites = maxPendingWrites;
        _backgroundWrites = backgroundWrites && !string.IsNullOrWhiteSpace(root);
        _log = log ?? (_ => { });
        if (string.IsNullOrWhiteSpace(root)) return;
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        DatabasePath = Path.Combine(root, "ranked.sqlite");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Pooling = false, DefaultTimeout = 2,
        }.ToString();
        using var db = Open();
        Execute(db!, null, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS ranked_schema(version INTEGER NOT NULL);
            INSERT INTO ranked_schema SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM ranked_schema);
            CREATE TABLE IF NOT EXISTS profiles(
                account_key TEXT NOT NULL, mode INTEGER NOT NULL, profile TEXT NOT NULL,
                PRIMARY KEY(account_key, mode));
            CREATE TABLE IF NOT EXISTS receipts(
                account_key TEXT NOT NULL, mode INTEGER NOT NULL, result_id TEXT NOT NULL,
                PRIMARY KEY(account_key, mode, result_id)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS identities(
                account_key TEXT PRIMARY KEY, guid TEXT NOT NULL UNIQUE, name TEXT NOT NULL);
            """);
        if (Convert.ToInt32(Scalar(db!, null, "SELECT version FROM ranked_schema")) != 1)
            throw new InvalidDataException("Unsupported ranked database version; existing data was not migrated.");
        ImportLegacy(db!, root);
        using (var command = db!.CreateCommand())
        {
            command.CommandText = "SELECT account_key, mode, profile FROM profiles";
            using var rows = command.ExecuteReader();
            while (rows.Read())
                _profiles[(rows.GetString(0), (MatchMode)rows.GetInt32(1))] = ParseProfile(rows.GetString(2));
        }
        using (var command = db!.CreateCommand())
        {
            command.CommandText = "SELECT account_key, guid, name FROM identities";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                var identity = new RankedIdentity(rows.GetString(0), ulong.Parse(rows.GetString(1), System.Globalization.CultureInfo.InvariantCulture), rows.GetString(2));
                _identities.Add(identity.AccountKey, identity);
                _registeredIdentities[identity.AccountKey] = identity;
            }
        }
        Publish();
    }

    public static string AccountKey(string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account)));
    }

    public RankedProfile Read(string account, MatchMode mode) =>
        _profiles.GetValueOrDefault((AccountKey(account), mode), RankedProfile.Empty);

    public IReadOnlyDictionary<string, RankedProfile> ReadMany(IEnumerable<string> accounts, MatchMode mode) =>
        accounts.Distinct(StringComparer.Ordinal).ToDictionary(account => account, account => Read(account, mode), StringComparer.Ordinal);

    public RankedProfile Complete(string account, MatchMode mode, RankedResult result)
    {
        var operation = ResultOperation(account, mode, result);
        Commit([operation]);
        return operation.Completion.Task.GetAwaiter().GetResult();
    }

    public Task<RankedProfile> CompleteAsync(string account, MatchMode mode, RankedResult result)
    {
        var operation = ResultOperation(account, mode, result);
        Submit(operation);
        return operation.Completion.Task;
    }

    public void RegisterIdentity(string account, ulong guid, string name)
    {
        var identity = Identity(account, guid, name);
        _registeredIdentities[identity.AccountKey] = identity;
        if (Volatile.Read(ref _identityView).GetValueOrDefault(identity.AccountKey) == identity) return;
        // A menu-only account has no public statistics yet. Persist its identity with its first
        // result, rather than scheduling a database write for every first-time menu connection.
        if (!_profiles.ContainsKey((identity.AccountKey, MatchMode.Solo))
            && !_profiles.ContainsKey((identity.AccountKey, MatchMode.Duos))
            && !_profiles.ContainsKey((identity.AccountKey, MatchMode.Fives))) return;
        try { Submit(new(identity.AccountKey, MatchMode.Unknown, null, identity)); }
        catch (IOException error)
        {
            // Keep admission available during a storage outage. The current authoritative
            // name is still registered and will be persisted with the next accepted result.
            _log($"ranked identity update deferred: {error.Message}");
        }
    }

    /// <summary>Boot-time migration of offline characters. Call before starting the listeners.</summary>
    public void SeedIdentities(IEnumerable<(string Account, ulong Guid, string Name)> characters)
    {
        var operations = characters.GroupBy(c => c.Account, StringComparer.Ordinal).Select(group =>
        {
            string key = AccountKey(group.Key);
            var existing = Volatile.Read(ref _identityView).GetValueOrDefault(key);
            var selected = group.OrderBy(c => c.Guid).First();
            if (existing is not null)
                selected = group.FirstOrDefault(c => c.Guid == existing.CharacterGuid, selected);
            var identity = Identity(selected.Account, selected.Guid, selected.Name);
            _registeredIdentities[key] = identity;
            return new Operation(key, MatchMode.Unknown, null, identity);
        }).Where(op => Volatile.Read(ref _identityView).GetValueOrDefault(op.AccountKey) != op.Identity).ToArray();
        foreach (var batch in operations.Chunk(128)) Commit(batch);
    }

    public void FlushPending(TimeSpan? timeout = null)
    {
        Task writer;
        lock (_queueGate) writer = _writer;
        if (!writer.Wait(timeout ?? TimeSpan.FromSeconds(15)))
            throw new IOException($"Ranked storage still has {PendingWrites} pending writes; the database has not acknowledged those results.");
        writer.GetAwaiter().GetResult();
        if (Volatile.Read(ref _failedWrites) != 0)
            throw new IOException($"{_failedWrites} ranked writes were rejected; inspect the ranked storage error log.");
    }

    private static RankedIdentity Identity(string account, ulong guid, string name)
    {
        if (guid == 0 || string.IsNullOrWhiteSpace(name) || Encoding.UTF8.GetByteCount(name) > 128 || name.Any(char.IsControl))
            throw new ArgumentException("Ranked identity requires an authoritative character ID and a valid name.");
        return new(AccountKey(account), guid, name);
    }

    private static Operation ResultOperation(string account, MatchMode mode, RankedResult result)
    {
        if (mode is not (MatchMode.Solo or MatchMode.Duos or MatchMode.Fives))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (string.IsNullOrWhiteSpace(result.Id) || result.Id.Length > 256 || result.Kills < 0 || result.Points < 0 || result.Placement == 0)
            throw new ArgumentException("Invalid ranked result.", nameof(result));
        return new(AccountKey(account), mode, result, null);
    }

    private void Submit(Operation operation)
    {
        if (!_backgroundWrites) { Commit([operation]); return; }
        lock (_queueGate)
        {
            if (_pending >= _maxPendingWrites) throw new IOException("Ranked write queue is full; retry the result after storage recovers.");
            _queue.Enqueue(operation);
            _pending++;
            if (_writer.IsCompleted) _writer = Task.Run(Drain);
        }
    }

    private async Task Drain()
    {
        // Coalesce simultaneous eliminations into one durable transaction.
        await Task.Delay(25).ConfigureAwait(false);
        while (true)
        {
            Operation[] batch;
            lock (_queueGate)
            {
                if (_queue.Count == 0)
                {
                    // Clear under the enqueue lock, avoiding the final-enqueue/lost-wakeup race.
                    _writer = Task.CompletedTask;
                    return;
                }
                batch = new Operation[Math.Min(128, _queue.Count)];
                for (int i = 0; i < batch.Length; i++) batch[i] = _queue.Dequeue();
            }
            await CommitWithRetry(batch).ConfigureAwait(false);
            lock (_queueGate) _pending -= batch.Length;
        }
    }

    private async Task CommitWithRetry(Operation[] batch)
    {
        int retry = 0;
        while (true)
        {
            try { Commit(batch); return; }
            catch (Exception error) when (error is SqliteException { SqliteErrorCode: not 19 }
                or IOException or UnauthorizedAccessException)
            {
                if (retry == 0 || retry % 12 == 0)
                    _log($"ranked storage: commit failed; retaining {PendingWrites} queued writes and retrying ({error.Message})");
                await Task.Delay(Math.Min(5000, 250 * (1 << Math.Min(retry++, 4)))).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // A permanently invalid record must not strand unrelated players behind it
                // or leave completion tasks unresolved. Transactions make splitting safe.
                if (batch.Length > 1)
                {
                    foreach (var operation in batch)
                        await CommitWithRetry([operation]).ConfigureAwait(false);
                }
                else
                {
                    Interlocked.Increment(ref _failedWrites);
                    _log($"ranked storage: write rejected for {batch[0].AccountKey}/{batch[0].Mode}: {error.Message}");
                    if (batch[0].Result is not null) batch[0].Completion.TrySetException(error);
                    else batch[0].Completion.TrySetResult(RankedProfile.Empty);
                }
                return;
            }
        }
    }

    private void Commit(Operation[] operations)
    {
        lock (_writeGate)
        {
            using var db = Open();
            using var transaction = db?.BeginTransaction();
            var changed = new Dictionary<(string, MatchMode), RankedProfile>();
            var identities = new Dictionary<string, RankedIdentity>(StringComparer.Ordinal);
            var receipts = new HashSet<(string, MatchMode, string)>();
            foreach (var operation in operations)
            {
                if (operation.Identity is { } identity)
                {
                    identity = _registeredIdentities.GetValueOrDefault(identity.AccountKey, identity);
                    if (db is not null)
                        Execute(db, transaction, """
                            INSERT INTO identities(account_key,guid,name) VALUES($a,$g,$n)
                            ON CONFLICT(account_key) DO UPDATE SET guid=excluded.guid, name=excluded.name
                            """, ("$a", identity.AccountKey), ("$g", identity.CharacterGuid.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$n", identity.Name));
                    identities[identity.AccountKey] = identity;
                    continue;
                }
                var key = (operation.AccountKey, operation.Mode);
                RankedResult result = operation.Result!;
                if (!changed.TryGetValue(key, out var old))
                    old = db is null ? _profiles.GetValueOrDefault(key, RankedProfile.Empty)
                        : Scalar(db, transaction, "SELECT profile FROM profiles WHERE account_key=$a AND mode=$m",
                            ("$a", key.AccountKey), ("$m", (int)key.Mode)) is string json ? ParseProfile(json) : RankedProfile.Empty;
                bool added = db is null
                    ? !_memoryReceipts.Contains((key.AccountKey, key.Mode, result.Id)) && receipts.Add((key.AccountKey, key.Mode, result.Id))
                    : Execute(db, transaction, "INSERT OR IGNORE INTO receipts VALUES($a,$m,$id)",
                        ("$a", key.AccountKey), ("$m", (int)key.Mode), ("$id", result.Id)) != 0;
                var next = added ? Add(old, result) : old;
                if (_registeredIdentities.TryGetValue(key.AccountKey, out var registered))
                {
                    var previousIdentity = identities.TryGetValue(key.AccountKey, out var stagedIdentity)
                        ? stagedIdentity : _identities.GetValueOrDefault(key.AccountKey);
                    if (db is not null && previousIdentity != registered)
                        Execute(db, transaction, """
                            INSERT INTO identities(account_key,guid,name) VALUES($a,$g,$n)
                            ON CONFLICT(account_key) DO UPDATE SET guid=excluded.guid, name=excluded.name
                            """, ("$a", registered.AccountKey), ("$g", registered.CharacterGuid.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$n", registered.Name));
                    identities[registered.AccountKey] = registered;
                }
                changed[key] = next;
                operation.Profile = next;
                if (db is not null && added)
                    Execute(db, transaction, """
                        INSERT INTO profiles VALUES($a,$m,$p)
                        ON CONFLICT(account_key,mode) DO UPDATE SET profile=excluded.profile
                        """, ("$a", key.AccountKey), ("$m", (int)key.Mode), ("$p", JsonSerializer.Serialize(next)));
            }
            transaction?.Commit();
            foreach (var (key, profile) in changed) _profiles[key] = profile;
            foreach (var (key, identity) in identities) _identities[key] = identity;
            _memoryReceipts.UnionWith(receipts);
            Publish();
            foreach (var operation in operations) operation.Completion.TrySetResult(operation.Profile ?? RankedProfile.Empty);
        }
    }

    private void Publish()
    {
        Volatile.Write(ref _identityView, new Dictionary<string, RankedIdentity>(_identities, StringComparer.Ordinal));
        Volatile.Write(ref _leaderboard, new RankedLeaderboard(_identities.Values, _profiles));
    }

    private static RankedProfile Add(RankedProfile old, RankedResult result) => new(checked(old.Matches + 1),
        old.Best.Append(result).OrderByDescending(x => x.Points).ThenBy(x => x.Id, StringComparer.Ordinal).Take(10).ToArray())
    {
        Wins = checked(old.Wins + (result.Placement == 1 ? 1u : 0u)),
        TopTens = checked(old.TopTens + (result.Placement <= 10 ? 1u : 0u)),
        TotalKills = checked(old.TotalKills + (uint)result.Kills),
        TotalPlacements = checked(old.TotalPlacements + result.Placement),
        TotalPoints = checked(old.TotalPoints + (uint)result.Points),
        TopKills = Math.Max(old.TopKills, (uint)result.Kills),
        KdMatches = checked(old.KdMatches + (result.Died.HasValue ? 1 : 0)),
        KdKills = checked(old.KdKills + (result.Died.HasValue ? (uint)result.Kills : 0)),
        KdDeaths = checked(old.KdDeaths + (result.Died == true ? 1u : 0u)),
    };

    private void ImportLegacy(SqliteConnection db, string root)
    {
        // Original files remain untouched. Profile and all replay IDs migrate in the same
        // transaction; an interrupted import cannot duplicate totals on the next startup.
        using var transaction = db.BeginTransaction();
        foreach (string file in Directory.EnumerateFiles(root, "*.json"))
        {
            string filename = Path.GetFileNameWithoutExtension(file);
            int dash = filename.LastIndexOf('-');
            if (dash != 64 || !filename[..dash].All(Uri.IsHexDigit)
                || !Enum.TryParse<MatchMode>(filename[(dash + 1)..], out var mode)
                || mode is not (MatchMode.Solo or MatchMode.Duos or MatchMode.Fives)) continue;
            string accountKey = filename[..dash].ToUpperInvariant();
            if (Scalar(db, transaction, "SELECT 1 FROM profiles WHERE account_key=$a AND mode=$m",
                ("$a", accountKey), ("$m", (int)mode)) is not null) continue;
            var legacy = ParseProfile(File.ReadAllText(file));
            Execute(db, transaction, "INSERT INTO profiles VALUES($a,$m,$p)", ("$a", accountKey), ("$m", (int)mode),
                ("$p", JsonSerializer.Serialize(legacy with { CompletedIds = [] })));
            foreach (string id in legacy.CompletedIds.Concat(legacy.Best.Select(result => result.Id)).Distinct(StringComparer.Ordinal))
                Execute(db, transaction, "INSERT OR IGNORE INTO receipts VALUES($a,$m,$id)",
                    ("$a", accountKey), ("$m", (int)mode), ("$id", id));
            ImportedProfiles++;
        }
        transaction.Commit();
    }

    private static RankedProfile ParseProfile(string json)
    {
        var profile = JsonSerializer.Deserialize<RankedProfile>(json) ?? throw new InvalidDataException("Null ranked profile.");
        if (profile.Matches < 0 || profile.Best is null || profile.CompletedIds is null || profile.Best.Length > 10
            || profile.Best.Any(r => r is null || string.IsNullOrWhiteSpace(r.Id) || r.Kills < 0 || r.Points < 0)
            || profile.Best.Sum(r => (long)r.Points) > int.MaxValue)
            throw new InvalidDataException("Invalid ranked profile; original data was retained.");
        return profile;
    }

    private SqliteConnection? Open()
    {
        if (_connectionString is null) return null;
        var db = new SqliteConnection(_connectionString);
        try
        {
            db.Open();
            Execute(db, null, "PRAGMA synchronous=FULL;");
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? transaction, string sql, (string, object)[] values)
    {
        var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return command;
    }
    private static int Execute(SqliteConnection db, SqliteTransaction? transaction, string sql, params (string, object)[] values)
    {
        using var command = Command(db, transaction, sql, values);
        return command.ExecuteNonQuery();
    }
    private static object? Scalar(SqliteConnection db, SqliteTransaction? transaction, string sql, params (string, object)[] values)
    {
        using var command = Command(db, transaction, sql, values);
        return command.ExecuteScalar();
    }

    private sealed class Operation(string accountKey, MatchMode mode, RankedResult? result, RankedIdentity? identity)
    {
        public string AccountKey { get; } = accountKey;
        public MatchMode Mode { get; } = mode;
        public RankedResult? Result { get; } = result;
        public RankedIdentity? Identity { get; } = identity;
        public RankedProfile? Profile { get; set; }
        public TaskCompletionSource<RankedProfile> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
