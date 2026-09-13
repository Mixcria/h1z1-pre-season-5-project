using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Cranberry.Launcher.Core;

public static class LauncherProfile
{
    public static string SettingsPathForProfile(string preferencesDirectory, string packagePath)
    {
        var package = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(packagePath))
            ?? throw new InvalidDataException("Invalid launcher profile.");
        string identity = LauncherSettings.ValidateServer(package.ServerUrl).AbsoluteUri
            + "\n" + package.CertificateSha256.ToUpperInvariant();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(preferencesDirectory, "profiles", key + ".json");
    }

    public static LauncherSettings Load(string settingsPath, string packagePath)
    {
        LauncherSettings Read(string path) => JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Invalid launcher settings.");
        var settings = File.Exists(settingsPath) ? Read(settingsPath)
            : File.Exists(packagePath) ? Read(packagePath) : new();
        // Saved preferences deliberately omit registration codes. Recover the code from the
        // distributed profile only when it belongs to the same server and certificate.
        if (string.IsNullOrWhiteSpace(settings.JoinCode) && File.Exists(packagePath))
        {
            var package = Read(packagePath);
            if (LauncherSettings.ValidateServer(settings.ServerUrl) == LauncherSettings.ValidateServer(package.ServerUrl)
                && string.Equals(settings.CertificateSha256, package.CertificateSha256, StringComparison.OrdinalIgnoreCase))
                settings = settings with { JoinCode = package.JoinCode };
        }
        return settings;
    }

    public static void Save(string path, LauncherSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings with { JoinCode = "" },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
