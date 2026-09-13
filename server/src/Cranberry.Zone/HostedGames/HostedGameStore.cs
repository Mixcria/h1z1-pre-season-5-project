using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cranberry.Login;

namespace Cranberry.Zone.HostedGames;

public enum HostedKeyKind { Host, Player, Moderator }

public sealed record HostedKeyInfo(
    string Id, HostedKeyKind Kind, string Region, string IssuedBy, string? TargetAccount,
    string? Account, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, bool Revoked,
    bool IsActive, uint? WorldId, string? GameId);

public sealed record HostedGameInfo(
    string Id, uint WorldId, string Region, uint GameModeId, string Name, string OwnerAccount,
    string HostKeyId, DateTimeOffset CreatedAt, bool Closed, bool IsActive)
{
    public int QueueDurationMinutes { get; init; }
    public int MaxPlayers { get; init; } = 150;
}

/// <summary>The secret is returned once, on successful issuance, and is never persisted.</summary>
public sealed record HostedGameResult(
    bool Success, string Message, HostedKeyInfo? Key = null, string? Secret = null, HostedGameInfo? Game = null)
{
    // Keep an accidentally logged result from exposing the one-time credential.
    public override string ToString() => Message;
}

/// <summary>
/// Account-bound hosted-game permissions. All lifetimes begin at issuance, not redemption.
/// A null path is memory-only. A configured path commits to disk before a mutation succeeds;
/// an unreadable/corrupt existing file fails closed instead of discarding permissions.
/// All account comparisons are ordinal, matching authenticated account identifiers.
/// </summary>
public sealed class HostedGameStore
{
    public const string SecretPrefix = "HGK-";

    /// <summary>
    /// True when <paramref name="bytes"/> carries an issued hosted-game secret. Secrets are
    /// issued as <see cref="SecretPrefix"/> followed by upper-case hex, but a client may echo a
    /// hand-typed, case-folded copy back over the wire, so the three letters match either case;
    /// every redaction site (wire captures, the zone packet log) shares this one predicate
    /// rather than each guessing at the pattern.
    /// </summary>
    public static bool ContainsSecret(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i + 4 <= bytes.Length; i++)
        {
            if ((bytes[i] | 0x20) == (byte)'h' && (bytes[i + 1] | 0x20) == (byte)'g'
                && (bytes[i + 2] | 0x20) == (byte)'k' && bytes[i + 3] == (byte)'-')
            {
                return true;
            }
        }

        return false;
    }
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<uint, GameWorldDefinition> _slots;
    private readonly Dictionary<string, string> _regions;
    private State _state = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public HostedGameStore(string? path = null, IEnumerable<GameWorldDefinition>? worlds = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _slots = (worlds ?? GameWorldCatalog.Default).Where(world => world.IsHosted)
            .ToDictionary(world => world.WorldId);
        if (_slots.Values.Any(world => string.IsNullOrWhiteSpace(world.Region)))
            throw new ArgumentException("Hosted slots require a region.", nameof(worlds));
        _regions = _slots.Values.Select(world => world.Region).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(region => region, region => region, StringComparer.OrdinalIgnoreCase);
        _path = string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetFullPath(path);
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<State>(File.ReadAllBytes(_path), Json);
            if (loaded is null || !ValidState(loaded))
                throw new InvalidDataException("The hosted-game permissions file is invalid.");
            _state = loaded;
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The hosted-game permissions file is invalid.");
        }
    }

    public HostedGameResult IssueHostKey(string actor, bool isAdmin, string region,
        TimeSpan? lifetime = null, string? targetAccount = null)
    {
        lock (_gate)
        {
            if (!ValidAccount(actor) || !isAdmin) return Fail("Administrator access is required to issue host keys.");
            if (!TryRegion(region, out var configuredRegion)) return Fail("That region has no configured hosted slots.");
            if (!ValidTarget(targetAccount)) return Fail("The target account is invalid.");
            var now = _utcNow();
            if (!TryExpiry(now, lifetime, out var expires)) return Fail("Lifetime must be a positive duration.");
            return Issue(new KeyRow(NewId(), HostedKeyKind.Host, configuredRegion, actor, targetAccount,
                null, now, expires, false, null, null, string.Empty));
        }
    }

    public HostedGameResult Redeem(string actor, string key)
    {
        lock (_gate)
        {
            if (!ValidAccount(actor)) return Fail("An authenticated account is required.");
            if (string.IsNullOrWhiteSpace(key) || key.Length > 256) return Fail("The key is invalid or unavailable.");
            var hash = Hash(key.Trim());
            var row = _state.Keys.FirstOrDefault(candidate => candidate.Hash == hash);
            var now = _utcNow();
            if (row is null || !KeyActive(row, now)) return Fail("The key is invalid or unavailable.");
            if (row.TargetAccount is not null && !SameAccount(row.TargetAccount, actor))
                return Fail("This key is assigned to a different account.");
            if (row.Account is not null && !SameAccount(row.Account, actor))
                return Fail("This key has already been redeemed by another account.");
            if (row.Account is not null)
                return new(true, "This account already has this key's access.", KeyInfo(row, now));
            var redeemed = row with { Account = actor };
            var updated = _state with { Keys = _state.Keys.Select(candidate => candidate.Id == row.Id ? redeemed : candidate).ToList() };
            if (!Commit(updated)) return SaveFailed();
            return new(true, row.Kind == HostedKeyKind.Host ? "Hosting access granted."
                : row.Kind == HostedKeyKind.Moderator ? "Game administrator access granted." : "Player access granted.", KeyInfo(redeemed, now));
        }
    }

    public HostedGameResult CreateGame(string actor, string region, uint gameModeId, string name,
        int queueDurationMinutes = 0, int maxPlayers = 150)
    {
        lock (_gate)
        {
            if (!ValidAccount(actor)) return Fail("An authenticated account is required.");
            if (!TryRegion(region, out var configuredRegion)) return Fail("That region has no configured hosted slots.");
            if (!ValidMode(gameModeId)) return Fail("Choose solo, duos, or fives.");
            if (queueDurationMinutes is < 0 or > 60) return Fail("Queue duration must be 1 to 60 minutes, or 0 for manual start.");
            if (maxPlayers is < 1 or > 150) return Fail("Maximum players must be between 1 and 150.");
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || name.Any(character => char.IsControl(character) || character is '<' or '>'))
                return Fail("Game name must contain 1 to 80 printable characters without angle brackets.");
            var now = _utcNow();
            // Prefer the longest grant so an older short-lived key does not unexpectedly end a new game.
            var grant = _state.Keys.Where(candidate => candidate.Kind == HostedKeyKind.Host &&
                    SameAccount(candidate.Account, actor) && SameRegion(candidate.Region, configuredRegion) && KeyActive(candidate, now))
                .OrderByDescending(candidate => candidate.ExpiresAt ?? DateTimeOffset.MaxValue).FirstOrDefault();
            if (grant is null) return Fail("Redeem a host key for this region first.");
            var slot = _slots.Values.Where(candidate => SameRegion(candidate.Region, configuredRegion))
                .OrderBy(candidate => candidate.WorldId)
                .FirstOrDefault(candidate => !_state.Games.Any(game => game.WorldId == candidate.WorldId && GameActive(game, now)));
            if (slot is null) return Fail("All hosted slots in this region are occupied.");
            var game = new GameRow(NewId(), slot.WorldId, configuredRegion, gameModeId, name.Trim(), actor, grant.Id, now, false)
            { QueueDurationMinutes = queueDurationMinutes, MaxPlayers = maxPlayers };
            var updated = _state with
            {
                // Seal any expired predecessor as well, so a clock correction cannot revive it.
                Games = _state.Games.Select(previous => previous.WorldId == slot.WorldId ? previous with { Closed = true } : previous)
                    .Append(game).ToList(),
            };
            if (!Commit(updated)) return SaveFailed();
            return new(true, "Private hosted game created.", Game: GameInfo(game, now));
        }
    }

    public HostedGameResult IssuePlayerKey(string actor, bool isAdmin, uint worldId,
        TimeSpan? lifetime = null, string? targetAccount = null, bool moderator = false)
    {
        lock (_gate)
        {
            if (!ValidAccount(actor)) return Fail("An authenticated account is required.");
            var now = _utcNow();
            var game = LatestGame(worldId);
            if (game is null || !GameActive(game, now)) return Fail("This hosted game is unavailable.");
            if (moderator ? !isAdmin && !SameAccount(game.OwnerAccount, actor) : !CanManageLocked(actor, isAdmin, game, now))
                return Fail(moderator ? "Only the game owner or a server administrator can appoint game administrators."
                    : "Only this game's administrators can issue player keys.");
            if (!ValidTarget(targetAccount)) return Fail("The target account is invalid.");
            if (!TryExpiry(now, lifetime, out var expires)) return Fail("Lifetime must be a positive duration.");
            var grant = _state.Keys.Single(candidate => candidate.Id == game.HostKeyId);
            if (grant.ExpiresAt is { } parentExpiry && (expires is null || parentExpiry < expires)) expires = parentExpiry;
            return Issue(new KeyRow(NewId(), moderator ? HostedKeyKind.Moderator : HostedKeyKind.Player, game.Region, actor, targetAccount,
                null, now, expires, false, worldId, game.Id, string.Empty));
        }
    }

    public HostedGameResult RevokeKey(string actor, bool isAdmin, string keyId)
    {
        lock (_gate)
        {
            if (!ValidAccount(actor)) return Fail("An authenticated account is required.");
            var row = _state.Keys.FirstOrDefault(candidate => candidate.Id == keyId);
            if (row is null) return Fail("The key was not found.");
            var game = row.GameId is null ? null : _state.Games.FirstOrDefault(candidate => candidate.Id == row.GameId);
            if (!isAdmin && (row.Kind == HostedKeyKind.Host || game is null ||
                (!SameAccount(game.OwnerAccount, actor) && (row.Kind != HostedKeyKind.Player || !CanManageLocked(actor, false, game, _utcNow())))))
                return Fail("You do not have permission to revoke this key.");
            if (row.Revoked) return new(true, "The key is already revoked.", KeyInfo(row, _utcNow()));
            var revoked = row with { Revoked = true };
            if (!Commit(_state with { Keys = _state.Keys.Select(candidate => candidate.Id == row.Id ? revoked : candidate).ToList() }))
                return SaveFailed();
            return new(true, "Key revoked.", KeyInfo(revoked, _utcNow()));
        }
    }

    public HostedGameResult CloseGame(string actor, bool isAdmin, uint worldId)
    {
        lock (_gate)
        {
            if (!ValidAccount(actor)) return Fail("An authenticated account is required.");
            var game = LatestGame(worldId);
            if (game is null) return Fail("The hosted game was not found.");
            if (!isAdmin && !SameAccount(game.OwnerAccount, actor)) return Fail("Only the game owner or an administrator can close this game.");
            if (game.Closed) return new(true, "The game is already closed.", Game: GameInfo(game, _utcNow()));
            var closed = game with { Closed = true };
            if (!Commit(_state with { Games = _state.Games.Select(candidate => candidate.Id == game.Id ? closed : candidate).ToList() }))
                return SaveFailed();
            return new(true, "Game closed; its player keys no longer grant access.", Game: GameInfo(closed, _utcNow()));
        }
    }

    public HostedGameResult SetMode(string actor, bool isAdmin, uint worldId, uint gameModeId)
    {
        lock (_gate)
        {
            if (!ValidAccount(actor)) return Fail("An authenticated account is required.");
            if (!ValidMode(gameModeId)) return Fail("Choose solo, duos, or fives.");
            var now = _utcNow();
            var game = LatestGame(worldId);
            if (game is null || !GameActive(game, now)) return Fail("This hosted game is unavailable.");
            if (!CanManageLocked(actor, isAdmin, game, now)) return Fail("Only this game's administrators can change this game.");
            if (game.GameModeId == gameModeId) return new(true, "The game already uses this mode.", Game: GameInfo(game, now));
            var changed = game with { GameModeId = gameModeId };
            if (!Commit(_state with { Games = _state.Games.Select(candidate => candidate.Id == game.Id ? changed : candidate).ToList() }))
                return SaveFailed();
            return new(true, "Game mode updated.", Game: GameInfo(changed, now));
        }
    }

    public bool CanManage(string actor, bool isAdmin, uint worldId)
    {
        lock (_gate)
        {
            var game = LatestGame(worldId);
            return ValidAccount(actor) && game is not null && CanManageLocked(actor, isAdmin, game, _utcNow());
        }
    }

    public bool CanEnter(string actor, uint worldId)
    {
        lock (_gate) return ValidAccount(actor) && CanEnterLocked(actor, LatestGame(worldId), _utcNow());
    }

    public HostedGameInfo? GetGame(uint worldId)
    {
        lock (_gate)
        {
            var game = LatestGame(worldId);
            return game is null ? null : GameInfo(game, _utcNow());
        }
    }

    /// <summary>Lists the latest instance in each slot, visible to its owner, admitted players, or administrators.</summary>
    public IReadOnlyList<HostedGameInfo> ListGames(string actor, bool isAdmin = false)
    {
        lock (_gate)
        {
            if (!ValidAccount(actor)) return Array.Empty<HostedGameInfo>();
            var now = _utcNow();
            return _state.Games.GroupBy(game => game.WorldId).Select(group => group.Last())
                .Where(game => isAdmin || SameAccount(game.OwnerAccount, actor) || CanEnterLocked(actor, game, now))
                .Select(game => GameInfo(game, now)).OrderBy(game => game.WorldId).ToArray();
        }
    }

    public IReadOnlyList<HostedKeyInfo> ListKeys(string actor, bool isAdmin = false)
    {
        lock (_gate)
        {
            if (!ValidAccount(actor)) return Array.Empty<HostedKeyInfo>();
            var now = _utcNow();
            return _state.Keys.Where(row => isAdmin || SameAccount(row.Account, actor) || SameAccount(row.TargetAccount, actor) ||
                    (row.Kind != HostedKeyKind.Host && _state.Games.Any(game => game.Id == row.GameId &&
                        (SameAccount(game.OwnerAccount, actor) || row.Kind == HostedKeyKind.Player && CanManageLocked(actor, false, game, now)))))
                .Select(row => KeyInfo(row, now)).ToArray();
        }
    }

    private HostedGameResult Issue(KeyRow row)
    {
        var secret = SecretPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        row = row with { Hash = Hash(secret) };
        if (!Commit(_state with { Keys = _state.Keys.Append(row).ToList() })) return SaveFailed();
        return new(true, "Key issued. Save it now; it cannot be displayed again.", KeyInfo(row, _utcNow()), secret);
    }

    private bool CanManageLocked(string actor, bool isAdmin, GameRow game, DateTimeOffset now) => GameActive(game, now) &&
        (isAdmin || SameAccount(game.OwnerAccount, actor) || _state.Keys.Any(row => row.Kind == HostedKeyKind.Moderator &&
            row.GameId == game.Id && SameAccount(row.Account, actor) && KeyActive(row, now)));

    private bool CanEnterLocked(string actor, GameRow? game, DateTimeOffset now) => game is not null && GameActive(game, now) &&
        (SameAccount(game.OwnerAccount, actor) || _state.Keys.Any(row => row.Kind != HostedKeyKind.Host && row.GameId == game.Id &&
            SameAccount(row.Account, actor) && KeyActive(row, now)));

    private bool KeyActive(KeyRow row, DateTimeOffset now)
    {
        if (row.Revoked || row.ExpiresAt is { } expires && now >= expires || !_regions.ContainsKey(row.Region)) return false;
        if (row.Kind == HostedKeyKind.Host) return true;
        var game = _state.Games.FirstOrDefault(candidate => candidate.Id == row.GameId);
        return game is not null && game.WorldId == row.WorldId && GameActive(game, now);
    }

    private bool GameActive(GameRow game, DateTimeOffset now)
    {
        if (game.Closed || !_slots.TryGetValue(game.WorldId, out var slot) || !SameRegion(slot.Region, game.Region)) return false;
        var grant = _state.Keys.FirstOrDefault(candidate => candidate.Id == game.HostKeyId);
        return grant is not null && grant.Kind == HostedKeyKind.Host && SameRegion(grant.Region, game.Region) &&
            SameAccount(grant.Account, game.OwnerAccount) && KeyActive(grant, now);
    }

    private HostedGameInfo GameInfo(GameRow game, DateTimeOffset now) => new(game.Id, game.WorldId, game.Region, game.GameModeId,
        game.Name, game.OwnerAccount, game.HostKeyId, game.CreatedAt, game.Closed, GameActive(game, now))
        { QueueDurationMinutes = game.QueueDurationMinutes, MaxPlayers = game.MaxPlayers };

    private HostedKeyInfo KeyInfo(KeyRow row, DateTimeOffset now) => new(row.Id, row.Kind, row.Region, row.IssuedBy,
        row.TargetAccount, row.Account, row.CreatedAt, row.ExpiresAt, row.Revoked, KeyActive(row, now), row.WorldId, row.GameId);

    private GameRow? LatestGame(uint worldId) => _state.Games.LastOrDefault(game => game.WorldId == worldId);
    private bool TryRegion(string? region, out string configuredRegion) => _regions.TryGetValue(region?.Trim() ?? string.Empty, out configuredRegion!);
    private static bool SameAccount(string? first, string? second) => first is not null && second is not null && string.Equals(first, second, StringComparison.Ordinal);
    private static bool SameRegion(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static bool ValidAccount(string? account) => !string.IsNullOrWhiteSpace(account) && account.Length <= 256 &&
        account == account.Trim() && !account.Any(char.IsControl);
    private static bool ValidTarget(string? target) => target is null || ValidAccount(target);
    private static bool ValidMode(uint mode) => mode is GameWorldCatalog.SoloGameModeId or GameWorldCatalog.DuosGameModeId or GameWorldCatalog.FivesGameModeId;
    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    private static string NewId() => Guid.NewGuid().ToString("N");
    private static HostedGameResult Fail(string message) => new(false, message);
    private static HostedGameResult SaveFailed() => Fail("The permissions change could not be saved; no access was changed. Contact an administrator.");

    private static bool TryExpiry(DateTimeOffset now, TimeSpan? lifetime, out DateTimeOffset? expires)
    {
        expires = null;
        if (lifetime is null) return true;
        if (lifetime <= TimeSpan.Zero) return false;
        try { expires = now.Add(lifetime.Value); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private bool Commit(State candidate)
    {
        if (_path is not null)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                // A unique sibling preserves any interrupted write for diagnosis. Never read temporary
                // files as permissions: only an atomically committed destination can grant access.
                var temporary = _path + ".tmp-" + NewId();
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, candidate, Json);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return false;
            }
        }
        _state = candidate;
        return true;
    }

    private static bool ValidState(State state)
    {
        if (state.Version != 1 || state.Keys is null || state.Games is null ||
            state.Keys.Any(row => row is null) || state.Games.Any(game => game is null)) return false;
        if (state.Keys.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != state.Keys.Count ||
            state.Keys.Select(row => row.Hash).Distinct(StringComparer.Ordinal).Count() != state.Keys.Count ||
            state.Games.Select(game => game.Id).Distinct(StringComparer.Ordinal).Count() != state.Games.Count) return false;
        foreach (var row in state.Keys)
        {
            if (!Guid.TryParseExact(row.Id, "N", out _) || !Enum.IsDefined(row.Kind) || string.IsNullOrWhiteSpace(row.Region) ||
                !ValidAccount(row.IssuedBy) || !ValidTarget(row.TargetAccount) || !ValidTarget(row.Account) ||
                row.Account is not null && row.TargetAccount is not null && !SameAccount(row.Account, row.TargetAccount) ||
                row.ExpiresAt is { } expiry && expiry <= row.CreatedAt || row.Hash is null || row.Hash.Length != 64 ||
                !row.Hash.All(Uri.IsHexDigit)) return false;
            if (row.Kind == HostedKeyKind.Host && (row.WorldId is not null || row.GameId is not null)) return false;
            if (row.Kind != HostedKeyKind.Host && !state.Games.Any(game => game.Id == row.GameId && game.WorldId == row.WorldId && SameRegion(game.Region, row.Region))) return false;
        }
        foreach (var game in state.Games)
        {
            if (!Guid.TryParseExact(game.Id, "N", out _) || !ValidAccount(game.OwnerAccount) ||
                game.QueueDurationMinutes is < 0 or > 60 || game.MaxPlayers is < 1 or > 150 ||
                !ValidMode(game.GameModeId) || string.IsNullOrWhiteSpace(game.Name) || game.Name.Length > 80 ||
                game.Name.Any(character => char.IsControl(character) || character is '<' or '>') ||
                string.IsNullOrWhiteSpace(game.Region) || !state.Keys.Any(row => row.Id == game.HostKeyId && row.Kind == HostedKeyKind.Host &&
                    SameAccount(row.Account, game.OwnerAccount) && SameRegion(row.Region, game.Region))) return false;
        }
        // Closed historical games may share a world id; two open instances never may.
        return state.Games.Where(game => !game.Closed).GroupBy(game => game.WorldId).All(group => group.Count() == 1);
    }

    private sealed record State
    {
        [JsonRequired]
        public int Version { get; init; } = 1;
        [JsonRequired]
        public List<KeyRow> Keys { get; init; } = [];
        [JsonRequired]
        public List<GameRow> Games { get; init; } = [];
    }

    private sealed record KeyRow(
        [property: JsonRequired] string Id,
        [property: JsonRequired] HostedKeyKind Kind,
        [property: JsonRequired] string Region,
        [property: JsonRequired] string IssuedBy,
        [property: JsonRequired] string? TargetAccount,
        [property: JsonRequired] string? Account,
        [property: JsonRequired] DateTimeOffset CreatedAt,
        [property: JsonRequired] DateTimeOffset? ExpiresAt,
        [property: JsonRequired] bool Revoked,
        [property: JsonRequired] uint? WorldId,
        [property: JsonRequired] string? GameId,
        [property: JsonRequired] string Hash);

    private sealed record GameRow(
        [property: JsonRequired] string Id,
        [property: JsonRequired] uint WorldId,
        [property: JsonRequired] string Region,
        [property: JsonRequired] uint GameModeId,
        [property: JsonRequired] string Name,
        [property: JsonRequired] string OwnerAccount,
        [property: JsonRequired] string HostKeyId,
        [property: JsonRequired] DateTimeOffset CreatedAt,
        [property: JsonRequired] bool Closed)
    {
        // Optional properties preserve version-one games created before configurable lobbies.
        public int QueueDurationMinutes { get; init; }
        public int MaxPlayers { get; init; } = 150;
    }
}
