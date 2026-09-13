using System.Text.Json;
using Cranberry.Launcher.Core;

namespace Cranberry.Launcher.Tests;

public sealed class ProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-profile-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_root, "preferences", "launcher-settings.json");
    private string PackagePath => Path.Combine(_root, "launcher-profile.json");
    private static LauncherSettings Package => new()
    {
        ServerUrl = "https://game.example.invalid:20040/",
        CertificateSha256 = new string('A', 64),
        JoinCode = "test-package-registration-code",
        Name = "PackageDefault",
        InstallDirectory = @"C:\PackageDefault\Game"
    };

    private void WritePackage(LauncherSettings settings)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(PackagePath, JsonSerializer.Serialize(settings));
    }

    [Fact]
    public void RegistrationCanBeRetriedAfterSavingAndReopeningWithoutLosingPreferences()
    {
        WritePackage(Package);
        var firstOpen = LauncherProfile.Load(SettingsPath, PackagePath);
        Assert.Equal(Package.JoinCode, firstOpen.JoinCode);

        var preferences = firstOpen with { Name = "ReturningPlayer", InstallDirectory = @"D:\My Games\Cranberry" };
        LauncherProfile.Save(SettingsPath, preferences);
        Assert.Equal("", JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(SettingsPath))!.JoinCode);

        var reopened = LauncherProfile.Load(SettingsPath, PackagePath);
        Assert.Equal(Package.JoinCode, reopened.JoinCode);
        Assert.Equal(preferences.Name, reopened.Name);
        Assert.Equal(preferences.InstallDirectory, reopened.InstallDirectory);
        Assert.Equal(preferences.ServerUrl, reopened.ServerUrl);
        Assert.Equal(preferences.CertificateSha256, reopened.CertificateSha256);
    }

    [Theory]
    [InlineData("test-friend-join-code")]
    [InlineData("test-private-owner-code")]
    public void SavingPreferencesNeverPersistsRegistrationCodes(string code)
    {
        var settings = Package with { JoinCode = code, Name = "Player" };

        LauncherProfile.Save(SettingsPath, settings);

        var json = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain(code, json);
        var saved = JsonSerializer.Deserialize<LauncherSettings>(json)!;
        Assert.Equal(settings with { JoinCode = "" }, saved);
        Assert.Equal(code, settings.JoinCode);
    }

    [Theory]
    [InlineData("https://different.example.invalid:20040/", false)]
    [InlineData("https://game.example.invalid:20041/", false)]
    [InlineData("https://game.example.invalid:20040/another-server/", false)]
    [InlineData("https://game.example.invalid:20040/", true)]
    public void AProfileForAnotherServerOrCertificateCannotSupplyARegistrationCode(string serverUrl, bool differentCertificate)
    {
        WritePackage(Package);
        var preferences = Package with
        {
            ServerUrl = serverUrl,
            CertificateSha256 = differentCertificate ? new string('B', 64) : Package.CertificateSha256,
            Name = "MyPlayer",
            InstallDirectory = @"D:\My Games\Cranberry",
            JoinCode = ""
        };
        LauncherProfile.Save(SettingsPath, preferences);

        Assert.Equal(preferences, LauncherProfile.Load(SettingsPath, PackagePath));
    }

    [Fact]
    public void EquivalentServerUrlsAndCertificateCaseCanRecoverThePackageCode()
    {
        WritePackage(Package);
        var preferences = Package with
        {
            ServerUrl = "https://GAME.example.invalid:20040",
            CertificateSha256 = Package.CertificateSha256.ToLowerInvariant(),
            JoinCode = ""
        };
        LauncherProfile.Save(SettingsPath, preferences);

        Assert.Equal(preferences with { JoinCode = Package.JoinCode }, LauncherProfile.Load(SettingsPath, PackagePath));
    }

    [Fact]
    public void ExplicitServerProfilesKeepLocalAndCloudPreferencesSeparate()
    {
        WritePackage(Package);
        string directory = Path.Combine(_root, "preferences");
        string cloud = LauncherProfile.SettingsPathForProfile(directory, PackagePath);
        LauncherProfile.Save(cloud, Package with { Name = "CloudPlayer" });
        WritePackage(Package with { ServerUrl = "https://127.0.0.1:20040/" });
        string local = LauncherProfile.SettingsPathForProfile(directory, PackagePath);
        Assert.NotEqual(cloud, local);
        Assert.NotEqual(SettingsPath, cloud);
        Assert.Equal("https://127.0.0.1:20040/", LauncherProfile.Load(local, PackagePath).ServerUrl);
        WritePackage(Package);
        Assert.Equal("CloudPlayer", LauncherProfile.Load(cloud, PackagePath).Name);
    }

    [Fact]
    public void ProfilePreferencesFollowServerIdentityAndCertificate()
    {
        WritePackage(Package);
        string first = LauncherProfile.SettingsPathForProfile(_root, PackagePath);
        WritePackage(Package with { ServerUrl = "https://GAME.example.invalid:20040", CertificateSha256 = new string('a', 64) });
        Assert.Equal(first, LauncherProfile.SettingsPathForProfile(_root, PackagePath));
        WritePackage(Package with { CertificateSha256 = new string('B', 64) });
        Assert.NotEqual(first, LauncherProfile.SettingsPathForProfile(_root, PackagePath));
    }

    [Fact]
    public void SavedPreferencesStillLoadWhenThePackageProfileIsUnavailable()
    {
        var preferences = Package with { Name = "MyPlayer", InstallDirectory = @"D:\My Games\Cranberry", JoinCode = "" };
        LauncherProfile.Save(SettingsPath, preferences);

        Assert.Equal(preferences, LauncherProfile.Load(SettingsPath, PackagePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
