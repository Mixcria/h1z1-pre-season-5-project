using System.Collections.Frozen;

namespace Cranberry.Zone.Match;

public sealed record InGameAvatar(string Version, string Pixels);
public sealed record SocialOverlayRequest(string Actor, string Action, string Target, string Text, string ClientId, Action<string> Reply);

/// <summary>Credential-free, immutable friend graph published by the account service.</summary>
public sealed class InGameSocialDirectory
{
    public static InGameSocialDirectory Empty { get; } = new([], []);
    private readonly FrozenDictionary<string, string> _names;
    private readonly FrozenDictionary<string, string[]> _friends;
    private readonly FrozenDictionary<string, InGameAvatar> _avatars;

    public InGameSocialDirectory(IEnumerable<KeyValuePair<string, string>> accounts, IEnumerable<string[]> friendships,
        IReadOnlyDictionary<string, InGameAvatar>? avatars = null)
    {
        _avatars = (avatars ?? new Dictionary<string, InGameAvatar>()).ToFrozenDictionary(StringComparer.Ordinal);
        _names = accounts.ToFrozenDictionary(StringComparer.Ordinal);
        var friends = _names.Keys.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal));
        foreach (var pair in friendships)
        {
            if (pair.Length != 2 || pair[0] == pair[1] || !friends.ContainsKey(pair[0]) || !friends.ContainsKey(pair[1])) continue;
            friends[pair[0]].Add(pair[1]); friends[pair[1]].Add(pair[0]);
        }
        _friends = friends.ToFrozenDictionary(p => p.Key, p => p.Value.OrderBy(id => _names[id], StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public string? Name(string account) => _names.GetValueOrDefault(account);
    public IEnumerable<KeyValuePair<string, string>> Accounts => _names;
    public InGameAvatar? Avatar(string account) => _avatars.GetValueOrDefault(account);
    public IEnumerable<string> Friends(string account) => _friends.TryGetValue(account, out var friends) ? friends.Select(id => id) : [];
    public bool AreFriends(string a, string b) => _friends.TryGetValue(a, out var friends) && Array.IndexOf(friends, b) >= 0;
}
