using Cranberry.Launcher.Core;
using Cranberry.Launcher.Service;
using Cranberry.Login;

namespace Cranberry.Launcher.Tests;

public sealed class SocialTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-social-" + Guid.NewGuid().ToString("N"));
    private static readonly string Join = new('A', 24), Owner = new('B', 48);
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private SocialStore Store(LocalAccountDirectory accounts) => new(Path.Combine(_root, "social.json"), Join, Owner, accounts, () => _now);
    private static Credentials New(string name, string? code = null) => new(name, "test-password-123", code ?? Join);

    [Fact]
    public void AcceptedFriendsPublishAnImmutableCredentialFreeGameDirectoryAndSurviveRestart()
    {
        var accounts = new LocalAccountDirectory();
        var store = Store(accounts);
        var a = store.Register(New("Alice")); var b = store.Register(New("Bob"));
        var original = store.InGameDirectory;
        store.AddFriend(a.AccountId, "Bob");
        Assert.False(store.InGameDirectory.AreFriends(a.AccountId, b.AccountId));
        store.Respond(b.AccountId, new(Assert.Single(store.State(b.AccountId, []).Invites).Id, true));
        var accepted = store.InGameDirectory;
        Assert.True(accepted.AreFriends(a.AccountId, b.AccountId));
        Assert.True(accepted.AreFriends(b.AccountId, a.AccountId));
        Assert.False(original.AreFriends(a.AccountId, b.AccountId));
        Assert.Equal("Bob", accepted.Name(b.AccountId));
        Assert.True(Store(accounts).InGameDirectory.AreFriends(a.AccountId, b.AccountId));
        store.RemoveFriend(a.AccountId, b.AccountId);
        Assert.False(store.InGameDirectory.AreFriends(a.AccountId, b.AccountId));
        Assert.True(accepted.AreFriends(a.AccountId, b.AccountId));
    }

    [Fact]
    public void TwoFriendsAcceptPartyAndReadyBeforeLeaderCanQueue()
    {
        var store = Store(new());
        var alice = store.Register(New("Alice")); var bob = store.Register(New("Bob")); var eve = store.Register(New("Eve"));
        store.AddFriend(alice.AccountId, "bob");
        var request = Assert.Single(store.State(bob.AccountId, []).Invites);
        Assert.Throws<InvalidOperationException>(() => store.Respond(eve.AccountId, new(request.Id, true)));
        store.Respond(bob.AccountId, new(request.Id, true));
        Assert.Single(store.State(alice.AccountId, []).Friends);
        store.Invite(alice.AccountId, bob.AccountId);
        var invite = Assert.Single(store.State(bob.AccountId, []).Invites);
        store.Respond(bob.AccountId, new(invite.Id, true));
        Assert.Throws<InvalidOperationException>(() => store.Queue(alice.AccountId));
        store.Ready(alice.AccountId, true); store.Ready(bob.AccountId, true);
        Assert.Throws<InvalidOperationException>(() => store.Queue(bob.AccountId));
        Assert.Equal(new[] { alice.AccountId, bob.AccountId }, store.Queue(alice.AccountId).Accounts);
        store.Mode(alice.AccountId, "Fives");
        Assert.Throws<InvalidOperationException>(() => store.Queue(alice.AccountId));
        store.Ready(alice.AccountId, true); store.Ready(bob.AccountId, true);
        _now = _now.AddSeconds(46);
        store.Authenticate(alice.Token);
        Assert.Throws<InvalidOperationException>(() => store.Queue(alice.AccountId));
    }

    [Fact]
    public void OwnerClaimPreservesCharacterAndFriendsPersistWithoutPlaintextPasswords()
    {
        var accounts = new LocalAccountDirectory("legacy"); accounts.BindCharacter("legacy", 4097);
        var store = Store(accounts);
        var owner = store.Register(New("Host", Owner));
        Assert.Equal("legacy", owner.AccountId); Assert.True(accounts.Owns(owner.AccountId, 4097));
        Assert.Throws<InvalidOperationException>(() => store.Register(New("SecondOwner", Owner)));
        Assert.Throws<InvalidOperationException>(() => store.Register(New("BadCode", "bad")));
        var friend = store.Register(New("Friend")); store.AddFriend(owner.AccountId, "Friend");
        store.Respond(friend.AccountId, new(Assert.Single(store.State(friend.AccountId, []).Invites).Id, true));
        var reloaded = Store(accounts);
        var session = reloaded.Login(New("hOsT"));
        Assert.Single(reloaded.State(session.AccountId, []).Friends);
        Assert.Throws<UnauthorizedAccessException>(() => reloaded.Authenticate(owner.Token));
        Assert.Throws<InvalidOperationException>(() => reloaded.Login(new("Host", "incorrect")));
        string json = File.ReadAllText(Path.Combine(_root, "social.json"));
        Assert.DoesNotContain("test-password-123", json); Assert.DoesNotContain(owner.Token, json);
    }

    [Fact]
    public void ExpiredInvitationsAndSessionsAreRejected()
    {
        var store = Store(new()); var a = store.Register(New("Alice")); var b = store.Register(New("Bobby"));
        store.AddFriend(a.AccountId, "Bobby"); store.Respond(b.AccountId, new(Assert.Single(store.State(b.AccountId, []).Invites).Id, true));
        store.Invite(a.AccountId, b.AccountId);
        string id = Assert.Single(store.State(b.AccountId, []).Invites).Id;
        _now = _now.AddMinutes(3);
        Assert.Throws<InvalidOperationException>(() => store.Respond(b.AccountId, new(id, true)));
        _now = _now.AddHours(13); Assert.Throws<UnauthorizedAccessException>(() => store.Authenticate(a.Token));
    }

    [Fact]
    public void TunnelCannotUseDevelopmentFallbackOrAnotherAccountsLaunchTicket()
    {
        var accounts = new LocalAccountDirectory("owner"); accounts.EnsureAccount("alice"); accounts.EnsureAccount("bob");
        accounts.BindTunnel(40001, "alice");
        var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 40001);
        Assert.False(accounts.TryResolveConnection("made-up-local-ticket", remote, out _, out _));
        string bob = accounts.IssueLauncherTicket("bob", 41002);
        Assert.False(accounts.TryResolveConnection(bob, remote, out _, out _));
        string alice = accounts.IssueLauncherTicket("alice", 41003);
        Assert.True(accounts.TryResolveConnection(alice, remote, out var actor, out var gateway));
        Assert.Equal("alice", actor); Assert.Equal("127.0.0.1:41003", gateway);
        string returned = accounts.IssueCharacterSelectTicket("alice", 20043);
        Assert.True(accounts.TryResolveConnection(returned, remote, out actor, out gateway));
        Assert.Equal("alice", actor); Assert.Equal("127.0.0.1:41003", gateway);
        string expired = accounts.IssueLauncherTicket("alice", 41003, TimeSpan.FromSeconds(-1));
        Assert.False(accounts.TryResolve(expired, true, out _));
        accounts.UnbindTunnel(40001);
        Assert.False(accounts.TryResolve(alice, true, out _));
        Assert.False(accounts.TryResolve(returned, true, out _));
    }

    [Fact]
    public void MixCannotBeClaimedWithDifferentCapitalization()
    {
        var store = Store(new()); store.Register(New("Mix"));
        Assert.Throws<InvalidOperationException>(() => store.Register(New("mix")));
        var roster = new CharacterRosterStore();
        var character = new CharacterCreatePayload(0, 3, 270, 2, "Mix", 664, 2, 0, "", "", "", "");
        Assert.True(roster.TryCreateUnique(1, character, out _));
        Assert.False(roster.TryCreateUnique(1, character with { Name = "mix" }, out _));
        Assert.False(roster.TryCreateUnique(1, character with { Name = "MIX" }, out _));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
