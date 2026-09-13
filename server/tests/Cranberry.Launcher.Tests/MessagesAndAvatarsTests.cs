using Cranberry.Launcher.Core;
using Cranberry.Launcher.Service;
using Cranberry.Login;

namespace Cranberry.Launcher.Tests;

public sealed class MessagesAndAvatarsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-messages-" + Guid.NewGuid().ToString("N"));
    private readonly LocalAccountDirectory _accounts = new("owner");
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private SocialStore Store() => new(Path.Combine(_root, "social.json"), "join", "owner", _accounts, () => _now);
    private static AuthSession Register(SocialStore store, string name) => store.Register(new(name, "test-password-123", "join"));
    private static void Befriend(SocialStore store, AuthSession a, AuthSession b)
    { store.AddFriend(a.AccountId, b.Name); store.Respond(b.AccountId, new(Assert.Single(store.State(b.AccountId, []).Invites).Id, true)); }
    private static MessageRequest Message(string friend, string text = "Hello | ; <friend> 🌲") => new(friend, text, Guid.NewGuid().ToString("N"));

    [Fact]
    public void OfflineDeliveryReadStateAndRetriesSurviveRestartWithoutDuplicatingMessages()
    {
        var store = Store(); var a = Register(store, "Alice"); var b = Register(store, "Bobby"); Befriend(store, a, b);
        _now += TimeSpan.FromMinutes(2);
        var request = Message(b.AccountId); var sent = store.SendMessage(a.AccountId, request);
        Assert.False(store.State(a.AccountId, []).Friends[0].Online);
        Assert.Equal(sent, store.SendMessage(a.AccountId, request));
        Assert.Equal(1, Assert.Single(store.Unread(b.AccountId)).Count);
        var restarted = Store();
        Assert.Equal(sent, restarted.SendMessage(a.AccountId, request));
        Assert.Equal(sent, Assert.Single(restarted.History(b.AccountId, a.AccountId).Messages));
        restarted.MarkRead(b.AccountId, a.AccountId, sent.Id);
        Assert.Empty(Store().Unread(b.AccountId));
        Assert.Equal(a.AccountId, sent.From); Assert.Equal(b.AccountId, sent.To);
    }

    [Fact]
    public void PendingRemovedAndUnrelatedAccountsCannotReadOrSendAndIdsCannotBeReusedForNewText()
    {
        var store = Store(); var a = Register(store, "Alice"); var b = Register(store, "Bobby"); var e = Register(store, "Evelyn");
        store.AddFriend(a.AccountId, b.Name);
        Assert.Throws<InvalidOperationException>(() => store.SendMessage(a.AccountId, Message(b.AccountId)));
        store.Respond(b.AccountId, new(Assert.Single(store.State(b.AccountId, []).Invites).Id, true));
        var request = Message(b.AccountId); store.SendMessage(a.AccountId, request);
        Assert.Throws<InvalidOperationException>(() => store.History(e.AccountId, a.AccountId));
        Assert.Throws<InvalidOperationException>(() => store.SendMessage(a.AccountId, request with { Text = "Changed" }));
        Assert.Throws<InvalidOperationException>(() => store.SendMessage(a.AccountId, Message(a.AccountId)));
        store.RemoveFriend(a.AccountId, b.AccountId);
        Assert.Throws<InvalidOperationException>(() => store.History(b.AccountId, a.AccountId));
        Assert.Throws<InvalidOperationException>(() => store.SendMessage(a.AccountId, request));
        Assert.Empty(store.Unread(b.AccountId));
    }

    [Fact]
    public void ValidationRateLimitsAndReadClampingDoNotDropFutureMessages()
    {
        var store = Store(); var a = Register(store, "Alice"); var b = Register(store, "Bobby"); Befriend(store, a, b);
        foreach (string body in new[] { "", " ", new string('a', 1001), "bad\0body" })
            Assert.Throws<InvalidOperationException>(() => store.SendMessage(a.AccountId, Message(b.AccountId, body)));
        Assert.Throws<InvalidOperationException>(() => store.SendMessage(a.AccountId, Message(b.AccountId) with { ClientId = "spoof" }));
        for (int i = 0; i < 30; i++) store.SendMessage(a.AccountId, Message(b.AccountId));
        Assert.Throws<InvalidOperationException>(() => store.SendMessage(a.AccountId, Message(b.AccountId)));
        store.MarkRead(b.AccountId, a.AccountId, long.MaxValue);
        _now += TimeSpan.FromMinutes(1);
        store.SendMessage(a.AccountId, Message(b.AccountId));
        Assert.Equal(1, Assert.Single(store.Unread(b.AccountId)).Count);
    }

    [Fact]
    public void AvatarUploadIsBoundedPrivateVersionedAndPublishedAfterCommit()
    {
        var store = Store(); var a = Register(store, "Alice"); var b = Register(store, "Bobby"); var e = Register(store, "Evelyn"); Befriend(store, a, b);
        var before = store.InGameDirectory;
        string pixels = string.Concat(Enumerable.Repeat("aabbcc", AvatarPixels.Size * AvatarPixels.Size));
        var picture = store.SetAvatar(a.AccountId, pixels);
        Assert.Null(before.Avatar(a.AccountId));
        Assert.Equal(picture.Pixels, store.InGameDirectory.Avatar(a.AccountId)!.Pixels);
        Assert.Equal(picture.Version, store.State(b.AccountId, []).Friends[0].AvatarVersion);
        Assert.Equal(picture, Store().Avatar(b.AccountId, a.AccountId));
        Assert.Throws<InvalidOperationException>(() => store.Avatar(e.AccountId, a.AccountId));
        foreach (var invalid in new[] { "rgb", new string('0', AvatarPixels.HexLength + 1), new string('Z', AvatarPixels.HexLength), null })
            Assert.Throws<InvalidOperationException>(() => store.SetAvatar(a.AccountId, invalid!));
        var published = store.InGameDirectory;
        store.SetAvatar(a.AccountId, "");
        Assert.Null(Store().InGameDirectory.Avatar(a.AccountId));
        Assert.Equal(picture.Version, published.Avatar(a.AccountId)!.Version);
    }

    [Fact]
    public void FailedMessageCommitDoesNotAcknowledgeOrPublishAnUnstoredMessage()
    {
        var store = Store(); var a = Register(store, "Alice"); var b = Register(store, "Bobby"); Befriend(store, a, b);
        Directory.CreateDirectory(Path.Combine(_root, "messages.json.tmp"));
        var error = Record.Exception(() => store.SendMessage(a.AccountId, Message(b.AccountId)));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(store.History(b.AccountId, a.AccountId).Messages);
        Assert.Empty(store.Unread(b.AccountId));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
