using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cranberry.Launcher.Core;
using Cranberry.Login;
using Cranberry.Zone;
using Cranberry.Zone.Match;

namespace Cranberry.Launcher.Service;

/// <summary>All access is serialized by LauncherHost. Credentials/friends persist; presence and parties expire.</summary>
public sealed partial class SocialStore
{
    private sealed record User(string Id, string Name, string Salt, string PasswordHash, string Avatar = "");
    private sealed record FriendRequest(string Id, string From, string To);
    private sealed record PartyInvite(string Id, string From, string To, string Party, DateTimeOffset Expires);
    private sealed record Session(string Account, DateTimeOffset Expires);
    private sealed class Lobby(string leader)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public List<string> Members { get; } = [leader];
        public HashSet<string> Ready { get; } = [];
        public string Mode { get; set; } = "Duos";
    }
    private sealed record Data(int Version, List<User> Users, List<string[]> Friends, List<FriendRequest> Requests);
    private readonly string _path;
    private readonly string _joinCode;
    private readonly string _ownerCode;
    private readonly LocalAccountDirectory _accounts;
    private Data _data;
    private InGameSocialDirectory _inGameDirectory = InGameSocialDirectory.Empty;
    public InGameSocialDirectory InGameDirectory => Volatile.Read(ref _inGameDirectory);

    private void PublishInGameDirectory() => Volatile.Write(ref _inGameDirectory, new InGameSocialDirectory(
        _data.Users.Select(u => KeyValuePair.Create(u.Id, u.Name)), _data.Friends,
        _data.Users.Where(u => u.Avatar.Length > 0).ToDictionary(u => u.Id, u => new InGameAvatar(AvatarPixels.Version(u.Avatar), u.Avatar))));
    private readonly Dictionary<string, Session> _sessions = [];
    private readonly Dictionary<string, DateTimeOffset> _presence = [];
    private readonly Dictionary<string, Lobby> _lobbies = [];
    private readonly List<PartyInvite> _partyInvites = [];
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset _nextPrune;
    private Dictionary<string, User> _users = [];

    public SocialStore(string path, string joinCode, string ownerCode, LocalAccountDirectory accounts,
        Func<DateTimeOffset>? clock = null)
    {
        _path = path; _joinCode = joinCode; _ownerCode = ownerCode; _accounts = accounts;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _data = File.Exists(path) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Invalid launcher accounts.") : new(1, [], [], []);
        if (_data.Version != 1 || _data.Users is null || _data.Friends is null || _data.Requests is null)
            throw new InvalidDataException("Invalid launcher accounts.");
        PublishInGameDirectory();
        _users = _data.Users.ToDictionary(u => u.Id);
        LoadMessages();
    }

    public AuthSession Register(Credentials credentials)
    {
        string name = credentials.Name?.Trim() ?? "";
        bool owner = SecretEquals(credentials.JoinCode, _ownerCode);
        if (!owner && !SecretEquals(credentials.JoinCode, _joinCode)) throw new InvalidOperationException("The join code is incorrect.");
        if (!Regex.IsMatch(name, "^[a-zA-Z0-9_]{3,24}$")) throw new InvalidOperationException("Name must be 3–24 letters, digits, or underscores.");
        PasswordPolicy.Validate(credentials.Password, name);
        if (_data.Users.Any(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("That name is already registered.");
        string id = owner ? _accounts.LocalAccountId : Guid.NewGuid().ToString("N");
        if (_data.Users.Any(u => u.Id == id)) throw new InvalidOperationException("The owner account has already been claimed. Sign in instead.");
        string salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var user = new User(id, name, salt, HashPassword(credentials.Password, salt));
        _accounts.EnsureAccount(id);
        Commit(_data with { Users = [.. _data.Users, user] });
        return SignIn(user);
    }

    public AuthSession Login(Credentials credentials)
    {
        if (credentials.Password is null || credentials.Password.Length > 128) throw new InvalidOperationException("Incorrect name or password.");
        var user = _data.Users.FirstOrDefault(u => u.Name.Equals(credentials.Name?.Trim(), StringComparison.OrdinalIgnoreCase));
        string hash = HashPassword(credentials.Password, user?.Salt ?? "00000000000000000000000000000000");
        if (user is null || !SecretEquals(hash, user.PasswordHash)) throw new InvalidOperationException("Incorrect name or password.");
        return SignIn(user);
    }

    public void ChangePassword(string token, ChangePasswordRequest request)
    {
        string actor = Authenticate(token);
        var user = Find(actor);
        if (request.CurrentPassword is null || request.CurrentPassword.Length > PasswordPolicy.MaximumLength
            || !SecretEquals(HashPassword(request.CurrentPassword, user.Salt), user.PasswordHash))
            throw new InvalidOperationException("Your current password is incorrect.");
        PasswordPolicy.Validate(request.NewPassword, user.Name);
        if (SecretEquals(request.CurrentPassword, request.NewPassword))
            throw new InvalidOperationException("Choose a different password from your current one.");
        string salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var updated = user with { Salt = salt, PasswordHash = HashPassword(request.NewPassword, salt) };
        // Publish only after durable storage succeeds. Keep this session valid if the response is lost.
        Commit(_data with { Users = _data.Users.Select(u => u.Id == actor ? updated : u).ToList() });
        string current = Digest(token);
        foreach (string key in _sessions.Where(p => p.Value.Account == actor && p.Key != current).Select(p => p.Key).ToArray())
            _sessions.Remove(key);
    }

    private AuthSession SignIn(User user)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[Digest(token)] = new(user.Id, _clock().AddHours(12));
        _presence[user.Id] = _clock();
        return new(token, user.Id, user.Name);
    }

    public string Authenticate(string token)
    {
        Prune();
        if (!_sessions.TryGetValue(Digest(token), out var session) || session.Expires <= _clock()) throw new UnauthorizedAccessException("Sign in again.");
        _presence[session.Account] = _clock();
        return session.Account;
    }

    public void Logout(string token) { _sessions.Remove(Digest(token)); }
    private bool Online(string id) => _presence.TryGetValue(id, out var seen) && seen > _clock().AddSeconds(-45);
    private User Find(string id) => _users[id];
    private Lobby? Party(string id) => _lobbies.Values.FirstOrDefault(p => p.Members.Contains(id));
    private bool Friends(string a, string b) => _data.Friends.Any(p => p.Contains(a) && p.Contains(b));

    public LauncherState State(string actor, IReadOnlyList<LauncherGamePresence> game)
        => State(actor, id => game.FirstOrDefault(g => g.AccountId == id)?.Status ?? "Not running");

    public LauncherState State(string actor, Func<string, string> status)
    {
        Prune();
        string Status(string id) => status(id);
        Person Person(string id) => new(id, Find(id).Name, Online(id), Status(id), AvatarPixels.Version(Find(id).Avatar));
        var invites = _data.Requests.Where(r => r.To == actor).Select(r => new SocialInvite(r.Id, r.From, Find(r.From).Name, "Friend"))
            .Concat(_partyInvites.Where(r => r.To == actor).Select(r => new SocialInvite(r.Id, r.From, Find(r.From).Name, "Party"))).ToArray();
        var party = Party(actor);
        var lobby = party is null ? null : new LobbyView(party.Id, party.Members[0], party.Mode,
            party.Members.Select(id => new LobbyMember(id, Find(id).Name, party.Ready.Contains(id), Online(id), Status(id))).ToArray());
        return new(Person(actor), _data.Friends.Where(p => p.Contains(actor)).Select(p => Person(p.Single(id => id != actor))).ToArray(), invites, lobby);
    }

    public void AddFriend(string actor, string name)
    {
        var target = _data.Users.FirstOrDefault(u => u.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (target is null || target.Id == actor) throw new InvalidOperationException("Enter another registered player's name.");
        if (Friends(actor, target.Id)) return;
        if (_data.Requests.Any(r => r.From == actor && r.To == target.Id)) return;
        if (_data.Requests.Count(r => r.From == actor || r.To == target.Id) >= 100) throw new InvalidOperationException("Too many pending friend requests.");
        Commit(_data with { Requests = [.. _data.Requests, new(Guid.NewGuid().ToString("N"), actor, target.Id)] });
    }

    public void RemoveFriend(string actor, string target)
    {
        Commit(_data with { Friends = _data.Friends.Where(p => !(p.Contains(actor) && p.Contains(target))).ToList(),
            Requests = _data.Requests.Where(r => !(r.From == actor && r.To == target || r.From == target && r.To == actor)).ToList() });
        _partyInvites.RemoveAll(i => i.From == actor && i.To == target || i.From == target && i.To == actor);
    }

    public void Invite(string actor, string target)
    {
        Prune();
        if (!Friends(actor, target) || !Online(target)) throw new InvalidOperationException("Select an online friend.");
        var party = Party(actor);
        if (party is null) { party = new(actor); _lobbies.Add(party.Id, party); }
        if (party.Members[0] != actor || party.Members.Count >= 5) throw new InvalidOperationException("Only the leader can invite; parties hold five players.");
        if (Party(target) is not null) throw new InvalidOperationException("Your friend is already in a party.");
        _partyInvites.RemoveAll(i => i.To == target && i.From == actor);
        _partyInvites.Add(new(Guid.NewGuid().ToString("N"), actor, target, party.Id, _clock().AddMinutes(2)));
    }

    public void Respond(string actor, RespondRequest request)
    {
        Prune();
        if (_data.Requests.FirstOrDefault(r => r.Id == request.Id && r.To == actor) is { } friend)
        {
            var friends = _data.Friends.ToList();
            if (request.Accept && !Friends(actor, friend.From)) friends.Add([friend.From, actor]);
            Commit(_data with { Friends = friends, Requests = _data.Requests.Where(r => r.Id != request.Id
                && !(r.From == actor && r.To == friend.From)).ToList() });
            return;
        }
        var invite = _partyInvites.FirstOrDefault(i => i.Id == request.Id && i.To == actor && i.Expires > _clock())
            ?? throw new InvalidOperationException("That invitation has expired.");
        if (!request.Accept) { _partyInvites.Remove(invite); return; }
        if (Party(actor) is not null || !_lobbies.TryGetValue(invite.Party, out var party)
            || party.Members[0] != invite.From || party.Members.Count >= 5)
            throw new InvalidOperationException("That party is full or unavailable. Leave your current party first.");
        party.Members.Add(actor);
        party.Ready.Clear();
        if (party.Members.Count > 2) party.Mode = "Fives";
        _partyInvites.RemoveAll(i => i.To == actor);
    }

    public void Ready(string actor, bool ready)
    {
        var party = Party(actor);
        if (party is null) { party = new(actor); _lobbies.Add(party.Id, party); }
        if (ready) party.Ready.Add(actor); else party.Ready.Remove(actor);
    }

    public void Mode(string actor, string mode)
    {
        if (mode is not ("Solo" or "Duos" or "Fives")) throw new InvalidOperationException("Unknown game mode.");
        var party = Party(actor);
        if (party is null) { party = new(actor); _lobbies.Add(party.Id, party); }
        if (party.Members[0] != actor) throw new InvalidOperationException("Only the party leader chooses the mode.");
        if (party.Mode != mode) { party.Mode = mode; party.Ready.Clear(); }
    }

    public (string[] Accounts, string Mode) Queue(string actor)
    {
        Prune();
        var party = Party(actor) ?? throw new InvalidOperationException("Press Ready before queueing.");
        if (party.Members[0] != actor) throw new InvalidOperationException("Only the party leader can queue.");
        if (party.Members.Any(m => !Online(m) || !party.Ready.Contains(m))) throw new InvalidOperationException("Every party member must be online and ready.");
        return (party.Members.ToArray(), party.Mode);
    }

    public void Leave(string actor)
    {
        var party = Party(actor);
        if (party is null) return;
        party.Members.Remove(actor); party.Ready.Clear();
        _partyInvites.RemoveAll(i => i.From == actor || i.To == actor);
        if (party.Members.Count == 0) _lobbies.Remove(party.Id);
    }

    private void Prune()
    {
        if (_clock() < _nextPrune) return;
        _nextPrune = _clock().AddSeconds(1);
        foreach (var pair in _sessions.Where(s => s.Value.Expires <= _clock()).ToArray()) _sessions.Remove(pair.Key);
        _partyInvites.RemoveAll(i => i.Expires <= _clock());
        foreach (var party in _lobbies.Values)
            party.Ready.RemoveWhere(id => !Online(id));
        foreach (var party in _lobbies.Values.Where(p => p.Members.All(id =>
            !_presence.TryGetValue(id, out var seen) || seen < _clock().AddMinutes(-15))).ToArray()) _lobbies.Remove(party.Id);
    }

    private void Commit(Data next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, next); stream.Flush(true); }
        File.Move(temporary, _path, true);
        _data = next;
        _users = next.Users.ToDictionary(u => u.Id);
        PublishInGameDirectory();
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private static bool SecretEquals(string? a, string b) => a is not null &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Digest(a)), Convert.FromHexString(Digest(b)));
    private static string HashPassword(string password, string salt) => Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(
        password, Convert.FromHexString(salt), 210_000, HashAlgorithmName.SHA512, 32));
}
