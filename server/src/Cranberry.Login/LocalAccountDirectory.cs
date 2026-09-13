using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cranberry.Login;

/// <summary>
/// Stable server-owned local accounts and character membership. The direct launcher generates a
/// fresh session token on every run, so that token must never become a wallet key. Loopback dev
/// login resolves to an explicit persisted local account; provisioned token hashes resolve other
/// accounts. Unknown remote tokens are refused.
/// </summary>
public sealed class LocalAccountDirectory
{
    private readonly object _gate = new();
    private readonly string? _path;
    private AccountFile _file;
    private readonly Dictionary<string, (string Account, DateTimeOffset Expires, int GatewayPort)> _launchTickets = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _tunnelAccounts = [];

    public LocalAccountDirectory(string localAccountId = "local", bool allowLoopbackDevelopment = true)
    {
        ValidateId(localAccountId);
        _file = new AccountFile
        {
            LocalAccountId = localAccountId,
            AllowLoopbackDevelopment = allowLoopbackDevelopment,
            Accounts = [localAccountId],
        };
    }

    private LocalAccountDirectory(string path, AccountFile file) { _path = path; _file = file; }
    public string LocalAccountId => _file.LocalAccountId;

    public static LocalAccountDirectory Load(string path, IEnumerable<ulong> legacyCharacters)
    {
        path = System.IO.Path.GetFullPath(path);
        if (File.Exists(path))
        {
            AccountFile file;
            try
            {
                file = JsonSerializer.Deserialize<AccountFile>(File.ReadAllText(path))
                    ?? throw new InvalidDataException("Local account directory is empty.");
            }
            catch (JsonException ex) { throw new InvalidDataException("Invalid local account directory.", ex); }
            if (file.Version != 1 || file.Accounts is null || file.Accounts.Count == 0
                || file.CharacterOwners is null || file.TokenOwners is null)
                throw new InvalidDataException("Unsupported local account directory.");
            foreach (string id in file.Accounts) ValidateId(id);
            if (!file.Accounts.Contains(file.LocalAccountId)
                || file.CharacterOwners.Keys.Any(guid => guid == 0)
                || file.TokenOwners.Keys.Any(hash => hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)))
                || file.CharacterOwners.Values.Any(owner => !file.Accounts.Contains(owner))
                || file.TokenOwners.Values.Any(owner => !file.Accounts.Contains(owner)))
                throw new InvalidDataException("Local account directory has invalid membership.");
            return new LocalAccountDirectory(path, file);
        }
        var initial = new AccountFile { LocalAccountId = Guid.NewGuid().ToString("N") };
        initial.Accounts.Add(initial.LocalAccountId);
        foreach (ulong guid in legacyCharacters) initial.CharacterOwners.Add(guid, initial.LocalAccountId);
        var directory = new LocalAccountDirectory(path, initial);
        directory.Save(initial);
        return directory;
    }

    public bool TryResolve(string token, bool isLoopback, out string accountId)
    {
        lock (_gate)
        {
            if (token.StartsWith("cb1.", StringComparison.Ordinal))
            {
                if (_launchTickets.TryGetValue(TokenHash(token), out var ticket) && ticket.Expires > DateTimeOffset.UtcNow)
                { accountId = ticket.Account; return true; }
                accountId = string.Empty;
                return false; // Expired launcher credentials never fall back to the development owner.
            }
            if (_file.TokenOwners.TryGetValue(TokenHash(token), out string? registered))
            { accountId = registered; return true; }
            if (isLoopback && _file.AllowLoopbackDevelopment)
            { accountId = _file.LocalAccountId; return true; }
            accountId = string.Empty;
            return false;
        }
    }

    public void EnsureAccount(string accountId)
    {
        ValidateId(accountId);
        lock (_gate)
        {
            if (_file.Accounts.Contains(accountId)) return;
            var next = Clone();
            next.Accounts.Add(accountId);
            Save(next);
            _file = next;
        }
    }

    public string IssueLauncherTicket(string accountId, int gatewayPort, TimeSpan? lifetime = null)
    {
        if (gatewayPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(gatewayPort));
        lock (_gate)
        {
            if (!_file.Accounts.Contains(accountId)) throw new InvalidOperationException("Unknown account.");
            foreach (var key in _launchTickets.Where(p => p.Value.Expires <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray())
                _launchTickets.Remove(key);
            string ticket = "cb1." + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            // The retail client reuses its command-line ticket when returning to character select.
            // Match the maximum authenticated tunnel lifetime so subsequent matches can log in again.
            _launchTickets[TokenHash(ticket)] = (accountId, DateTimeOffset.UtcNow + (lifetime ?? TimeSpan.FromHours(12)), gatewayPort);
            return ticket;
        }
    }

    public bool TryResolveConnection(string token, System.Net.IPEndPoint remote, out string accountId, out string? gatewayOverride)
    {
        lock (_gate)
        {
            gatewayOverride = null;
            bool loopback = System.Net.IPAddress.IsLoopback(remote.Address);
            bool resolved = TryResolve(token, loopback, out accountId);
            if (loopback && _tunnelAccounts.TryGetValue(remote.Port, out string? tunnelAccount)
                && (!token.StartsWith("cb1.", StringComparison.Ordinal) || accountId != tunnelAccount))
            { accountId = string.Empty; return false; }
            if (resolved && _launchTickets.TryGetValue(TokenHash(token), out var ticket))
                gatewayOverride = "127.0.0.1:" + ticket.GatewayPort;
            return resolved;
        }
    }

    public void BindTunnel(int sourcePort, string accountId) { lock (_gate) _tunnelAccounts.Add(sourcePort, accountId); }
    public void UnbindTunnel(int sourcePort)
    {
        lock (_gate)
        {
            if (!_tunnelAccounts.Remove(sourcePort, out var account) || _tunnelAccounts.Values.Contains(account)) return;
            foreach (var key in _launchTickets.Where(t => t.Value.Account == account).Select(t => t.Key).ToArray())
                _launchTickets.Remove(key);
        }
    }

    public string IssueCharacterSelectTicket(string accountId, int directGatewayPort)
    {
        lock (_gate)
        {
            int port = _launchTickets.Values.Where(t => t.Account == accountId && t.Expires > DateTimeOffset.UtcNow)
                .OrderByDescending(t => t.Expires).Select(t => t.GatewayPort).FirstOrDefault(directGatewayPort);
            return IssueLauncherTicket(accountId, port);
        }
    }

    /// <summary>Provision an account using a locally supplied login credential, never a client-selected account ID.</summary>
    public void Register(string accountId, string loginToken)
    {
        ValidateId(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(loginToken);
        lock (_gate)
        {
            var next = Clone();
            string tokenHash = TokenHash(loginToken);
            if (next.TokenOwners.TryGetValue(tokenHash, out string? owner) && owner != accountId)
                throw new InvalidOperationException("Credential already belongs to another account.");
            next.Accounts.Add(accountId);
            next.TokenOwners[tokenHash] = accountId;
            Save(next);
            _file = next;
        }
    }

    public bool Owns(string accountId, ulong characterId)
    {
        lock (_gate) return _file.CharacterOwners.TryGetValue(characterId, out string? owner) && owner == accountId;
    }

    /// <summary>Credential-free boot-time identity mapping for persisted character statistics.</summary>
    public IReadOnlyDictionary<ulong, string> CharacterOwnersSnapshot()
    {
        lock (_gate) return new Dictionary<ulong, string>(_file.CharacterOwners);
    }

    public void BindCharacter(string accountId, ulong characterId)
    {
        if (characterId == 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        lock (_gate)
        {
            if (!_file.Accounts.Contains(accountId)) throw new InvalidOperationException("Unknown account.");
            if (_file.CharacterOwners.TryGetValue(characterId, out string? owner))
            {
                if (owner != accountId) throw new InvalidOperationException("Character belongs to another account.");
                return;
            }
            var next = Clone();
            next.CharacterOwners.Add(characterId, accountId);
            Save(next);
            _file = next;
        }
    }

    private AccountFile Clone() => new()
    {
        LocalAccountId = _file.LocalAccountId,
        AllowLoopbackDevelopment = _file.AllowLoopbackDevelopment,
        Accounts = new(_file.Accounts, StringComparer.Ordinal),
        TokenOwners = new(_file.TokenOwners, StringComparer.Ordinal),
        CharacterOwners = new(_file.CharacterOwners),
    };

    private void Save(AccountFile file)
    {
        if (_path is null) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, file, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _path, overwrite: true);
    }

    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static void ValidateId(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 128
            || accountId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new InvalidDataException("Invalid local account ID.");
    }

    private sealed class AccountFile
    {
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public string LocalAccountId { get; set; } = string.Empty;
        [JsonRequired] public bool AllowLoopbackDevelopment { get; set; } = true;
        [JsonRequired] public HashSet<string> Accounts { get; set; } = new(StringComparer.Ordinal);
        [JsonRequired] public Dictionary<string, string> TokenOwners { get; set; } = new(StringComparer.Ordinal);
        [JsonRequired] public Dictionary<ulong, string> CharacterOwners { get; set; } = [];
    }
}
