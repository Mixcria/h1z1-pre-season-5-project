using Cranberry.Login;

namespace Cranberry.Tests.Login;

public sealed class LocalAccountDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-local-accounts-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_root, "accounts.json");

    [Fact]
    public void LocalLauncherTokensChangeButTheSavedAccountAndCharacterMembershipDoNot()
    {
        var directory = LocalAccountDirectory.Load(FilePath, [4097, 4098]);
        Assert.True(directory.TryResolve("ephemeral-launch-1", true, out string first));
        Assert.True(directory.Owns(first, 4097));
        Assert.True(directory.Owns(first, 4098));
        var reloaded = LocalAccountDirectory.Load(FilePath, [9999]);
        Assert.True(reloaded.TryResolve("ephemeral-launch-2", true, out string second));
        Assert.Equal(first, second);
        Assert.False(reloaded.Owns(first, 9999));
    }

    [Fact]
    public void ProvisionedAccountsCannotClaimEachOthersCharactersAndCredentialsSurviveReload()
    {
        var directory = LocalAccountDirectory.Load(FilePath, []);
        directory.Register("alice", "alice-test-token");
        directory.Register("bob", "bob-test-token");
        directory.BindCharacter("alice", 501);
        directory.BindCharacter("bob", 502);
        Assert.Throws<InvalidOperationException>(() => directory.BindCharacter("bob", 501));
        Assert.Throws<InvalidOperationException>(() => directory.Register("bob", "alice-test-token"));
        var loaded = LocalAccountDirectory.Load(FilePath, []);
        Assert.True(loaded.TryResolve("alice-test-token", false, out string alice));
        Assert.True(loaded.Owns(alice, 501));
        Assert.False(loaded.Owns(alice, 502));
        Assert.False(loaded.TryResolve("unprovisioned", false, out _));
        Assert.DoesNotContain("alice-test-token", File.ReadAllText(FilePath));
    }

    [Fact]
    public void CorruptDirectoryIsNotReplacedWithAFreshAccount()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(FilePath, "{broken");
        Assert.Throws<InvalidDataException>(() => LocalAccountDirectory.Load(FilePath, [4097]));
        Assert.Equal("{broken", File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData("Version")]
    [InlineData("LocalAccountId")]
    [InlineData("AllowLoopbackDevelopment")]
    [InlineData("Accounts")]
    [InlineData("TokenOwners")]
    [InlineData("CharacterOwners")]
    public void MissingSavedFieldsCannotResetIdentityOrEnableFallback(string field)
    {
        LocalAccountDirectory.Load(FilePath, [4097]);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
        json.Remove(field);
        string damaged = json.ToJsonString();
        File.WriteAllText(FilePath, damaged);
        Assert.Throws<InvalidDataException>(() => LocalAccountDirectory.Load(FilePath, [9999]));
        Assert.Equal(damaged, File.ReadAllText(FilePath));
    }

    [Fact]
    public void FailedMembershipSaveDoesNotPublishAnUnsavedOwner()
    {
        var directory = LocalAccountDirectory.Load(FilePath, []);
        File.Move(FilePath, FilePath + ".original");
        Directory.CreateDirectory(FilePath);
        Exception? failure = Record.Exception(() => directory.BindCharacter(directory.LocalAccountId, 701));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.False(directory.Owns(directory.LocalAccountId, 701));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
