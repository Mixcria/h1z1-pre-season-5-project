using System.Security.Cryptography;
using System.Text.Json;
using Cranberry.Launcher.Core;
using Cranberry.Launcher.Service;
using Cranberry.Login;

namespace Cranberry.Launcher.Tests;

public sealed class PasswordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-password-" + Guid.NewGuid().ToString("N"));
    private const string Old = "test-password-123", New = "velvet canoe orchard moon";
    private SocialStore Store() => new(Path.Combine(_root, "social.json"), "join", "owner", new LocalAccountDirectory("legacy"));

    [Theory]
    [InlineData(null)]
    [InlineData("ShortPwd!23")]
    [InlineData("               ")]
    [InlineData("aaaaaaaaaaaaaaa")]
    [InlineData("123456789012345")]
    [InlineData("password123456789")]
    [InlineData("Alice1234567890123")]
    [InlineData("cranberry12345678")]
    [InlineData("qwertyuiopasdfghjkl")]
    [InlineData("abcdabcdabcdabcd")]
    public void RejectsWeakNewCredentialsAtTheServer(string? password)
    {
        var store = Store();
        Assert.NotNull(PasswordPolicy.Error(password, "Alice"));
        Assert.Throws<InvalidOperationException>(() => store.Register(new("Alice", password!, "join")));
        Assert.Empty(store.InGameDirectory.Friends("Alice"));
    }

    [Theory]
    [InlineData("velvet canoe orchard moon")]
    [InlineData("9u)T4_#rV!2aQ@7p")]
    [InlineData(" a lantern floats above lakes ")]
    [InlineData("prairie café lantern river")]
    public void AllowsLongPhrasesAndGeneratedPasswordsWithoutMandatorySymbols(string password)
        => Assert.Null(PasswordPolicy.Error(password, "Alice"));

    [Fact]
    public void ChangeRequiresCurrentSecretPersistsAndRevokesOnlyOtherSessionsForThatAccount()
    {
        var store = Store(); var alice = store.Register(new("Alice", Old, "owner"));
        var other = store.Login(new("Alice", Old)); var bob = store.Register(new("Bobby", Old, "join"));
        Assert.Throws<InvalidOperationException>(() => store.ChangePassword(alice.Token, new("wrong", New)));
        Assert.Throws<InvalidOperationException>(() => store.ChangePassword(alice.Token, new(Old, "weak")));
        Assert.Throws<InvalidOperationException>(() => store.ChangePassword(alice.Token, new(Old, Old)));
        Assert.Equal(alice.AccountId, store.Authenticate(other.Token));
        store.ChangePassword(alice.Token, new(Old, New));
        Assert.Equal("legacy", store.Authenticate(alice.Token));
        Assert.Equal(bob.AccountId, store.Authenticate(bob.Token));
        Assert.Throws<UnauthorizedAccessException>(() => store.Authenticate(other.Token));
        Assert.Throws<UnauthorizedAccessException>(() => store.ChangePassword(other.Token, new(New, Old)));
        Assert.Throws<InvalidOperationException>(() => store.Login(new("Alice", Old)));
        Assert.Equal(alice.AccountId, Store().Login(new("Alice", New)).AccountId);
        string json = File.ReadAllText(Path.Combine(_root, "social.json"));
        Assert.DoesNotContain(New, json); Assert.DoesNotContain(Old, json); Assert.DoesNotContain(alice.Token, json);
    }

    [Fact]
    public void OldWeakPasswordCanStillSignInAndBeChangedWithoutLosingIdentity()
    {
        Directory.CreateDirectory(_root);
        string salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        string hash = Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2("old-secret", Convert.FromHexString(salt), 210_000, HashAlgorithmName.SHA512, 32));
        File.WriteAllText(Path.Combine(_root, "social.json"), JsonSerializer.Serialize(new { Version = 1,
            Users = new[] { new { Id = "legacy", Name = "Alice", Salt = salt, PasswordHash = hash } }, Friends = Array.Empty<string[]>(), Requests = Array.Empty<object>() }));
        var store = Store(); var login = store.Login(new("Alice", "old-secret"));
        store.ChangePassword(login.Token, new("old-secret", New));
        Assert.Equal("legacy", Store().Login(new("Alice", New)).AccountId);
    }

    [Fact]
    public void FailedPersistenceKeepsTheOldPasswordAndSessions()
    {
        var store = Store(); var a = store.Register(new("Alice", Old, "join")); var other = store.Login(new("Alice", Old));
        Directory.CreateDirectory(Path.Combine(_root, "social.json.tmp"));
        var error = Record.Exception(() => store.ChangePassword(a.Token, new(Old, New)));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(a.AccountId, store.Authenticate(other.Token));
        Assert.Equal(a.AccountId, store.Login(new("Alice", Old)).AccountId);
        Assert.Throws<InvalidOperationException>(() => store.Login(new("Alice", New)));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
