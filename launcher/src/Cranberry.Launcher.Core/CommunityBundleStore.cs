using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cranberry.Launcher.Core;

public sealed record CommunityBundleFile(string Path, long Size, string Sha256);

/// <summary>Extracts only verified immutable application files. User state is never part of a bundle.</summary>
public static class CommunityBundleStore
{
    public const int MaximumFiles = 20_000;
    public const int MaximumInventorySize = 8 * 1024 * 1024;
    public const long MaximumFileSize = 512L * 1024 * 1024;
    public const long MaximumExpandedSize = 4L * 1024 * 1024 * 1024;
    private static readonly string[] RequiredFiles =
    [
        "Cranberry.Launcher.exe", "runtime/Cranberry.Host.exe", "local-edition.json",
        CommunityUpdateSettings.FileName, "package/game-manifest.json", "package/download-host.json",
        "package/gameplay.defaults.json", "package/environment.defaults.json", "runtime/Data/dynamicAppearance.bin"
    ];
    private static readonly HashSet<string> StateRoots = new(StringComparer.OrdinalIgnoreCase)
    { "Game", "data", "state", "logs", "launcher-release", ".cranberry-updates", ".community-updates", "updates" };
    private static readonly HashSet<string> StateNames = new(StringComparer.OrdinalIgnoreCase)
    { "launcher.json", "launcher-defaults.json", "launcher-host.json", "local-ports.json", "cranberry.json",
        "environment.json", "local-edition.lock", "community-launch.lock", "launch.lock" };
    private static readonly HashSet<string> SecretExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".key", ".pem", ".pfx", ".p12" };

    public static async Task<string> Stage(string archive, string versionsDir, CommunityRelease release,
        CommunityUpdateSettings trust, CancellationToken ct = default)
    {
        trust.Validate();
        release.Verify(trust.PublicKey);
        GameInstaller.SafePath(versionsDir, ".path-check");
        Directory.CreateDirectory(versionsDir);
        string versionName = $"{release.Sequence}-{release.Sha256}";
        string destination = GameInstaller.SafePath(versionsDir, versionName);
        if (Directory.Exists(destination))
        {
            string existing = GameInstaller.SafePath(destination, "app");
            await ValidatePackage(existing, trust, release, ct);
            await VerifyReceipt(destination, release, ct);
            return existing;
        }
        string stage = GameInstaller.SafePath(versionsDir, ".stage-" + Guid.NewGuid().ToString("N"));
        string app = GameInstaller.SafePath(stage, "app");
        // Keep failed stages inert for inspection. Never recursively remove an untrusted archive tree.
        await using var archiveStream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (archiveStream.Length != release.Size
            || Convert.ToHexString(await SHA256.HashDataAsync(archiveStream, ct)) != release.Sha256)
            throw new InvalidDataException("The community archive does not match its signed identity.");
        archiveStream.Position = 0;
        using (var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true))
        {
            ValidateEntries(zip, app);
            Directory.CreateDirectory(app);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                bool directory = entry.FullName.EndsWith('/');
                string name = directory ? entry.FullName[..^1] : entry.FullName;
                string target = GameInstaller.SafePath(app, name);
                if (directory) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                GameInstaller.SafePath(app, name);
                await using var input = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] buffer = new byte[128 * 1024];
                long count = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    count += read;
                    if (count > entry.Length) throw new InvalidDataException("ZIP entry exceeds its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (count != entry.Length) throw new InvalidDataException("ZIP entry is truncated.");
            }
        }
        await ValidatePackage(app, trust, release, ct);
        await File.WriteAllBytesAsync(GameInstaller.SafePath(stage, "release.json"),
            JsonSerializer.SerializeToUtf8Bytes(release, CommunityRelease.Json), ct);
        ct.ThrowIfCancellationRequested();
        GameInstaller.SafePath(versionsDir, versionName);
        Directory.Move(stage, destination);
        return GameInstaller.SafePath(destination, "app");
    }

    public static async Task ValidatePackage(string bundle, CommunityUpdateSettings trust,
        CommunityRelease release, CancellationToken ct = default)
    {
        trust.Validate();
        release.Verify(trust.PublicKey);
        GameInstaller.SafePath(bundle, CommunityRelease.FilesFileName);
        var actual = EnumerateFiles(bundle, ct);
        string inventoryPath = GameInstaller.SafePath(bundle, CommunityRelease.FilesFileName);
        if (!actual.TryGetValue(CommunityRelease.FilesFileName, out string? inventoryName)
            || inventoryName != CommunityRelease.FilesFileName
            || new FileInfo(inventoryPath).Length is < 1 or > MaximumInventorySize)
            throw new InvalidDataException("Missing or oversized community file inventory.");
        byte[] inventory = await File.ReadAllBytesAsync(inventoryPath, ct);
        if (Convert.ToHexString(SHA256.HashData(inventory)) != release.FilesSha256)
            throw new InvalidDataException("The community file inventory is not the signed inventory.");
        CommunityBundleFile[] files;
        try
        {
            files = JsonSerializer.Deserialize<CommunityBundleFile[]>(inventory, CommunityRelease.Json)
                ?? throw new InvalidDataException("Missing community file inventory.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid community file inventory.", ex); }
        if (files.Length is < 1 or >= MaximumFiles)
            throw new InvalidDataException("Community file inventory exceeds the file-count limit.");
        var expected = new Dictionary<string, CommunityBundleFile>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (file is null) throw new InvalidDataException("Null community inventory entry.");
            string path = ValidatePath(bundle, file.Path);
            if (file.Path.Equals(CommunityRelease.FilesFileName, StringComparison.OrdinalIgnoreCase)
                || !expected.TryAdd(file.Path, file) || file.Size is < 0 or > MaximumFileSize
                || !CommunityRelease.ValidHash(file.Sha256))
                throw new InvalidDataException("Invalid or duplicate community file inventory entry.");
            total += file.Size;
            if (total > MaximumExpandedSize) throw new InvalidDataException("Community package exceeds its expanded-size limit.");
            if (!actual.TryGetValue(file.Path, out string? actualName) || actualName != file.Path
                || !await GameInstaller.Matches(path, new GameFile(file.Path, file.Size, file.Sha256), ct))
                throw new InvalidDataException($"Community package file failed verification: {file.Path}");
        }
        if (actual.Count != expected.Count + 1 || RequiredFiles.Any(p => !expected.ContainsKey(p)))
            throw new InvalidDataException("Community package contains unlisted files or is missing required files.");
        var settings = CommunityUpdateSettings.Load(bundle);
        if (settings.Repository != trust.Repository || settings.PublicKey != trust.PublicKey
            || settings.Sequence != release.Sequence
            || settings.Version != release.Version)
            throw new InvalidDataException("Community package update identity does not match its trusted release.");
        string editionPath = GameInstaller.SafePath(bundle, "local-edition.json");
        if (new FileInfo(editionPath).Length > 64 * 1024)
            throw new InvalidDataException("Local edition metadata is too large.");
        LocalEdition.Release edition;
        try
        {
            edition = JsonSerializer.Deserialize<LocalEdition.Release>(await File.ReadAllBytesAsync(editionPath, ct),
                CommunityRelease.Json) ?? throw new InvalidDataException("Missing local edition metadata.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid local edition metadata.", ex); }
        if (edition.Version != 1 || string.IsNullOrWhiteSpace(edition.ReleaseId)
            || !string.Equals(edition.GameManifestSha256, expected["package/game-manifest.json"].Sha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(edition.AppearanceSha256, expected["runtime/Data/dynamicAppearance.bin"].Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Local edition content identity does not match its signed inventory.");
    }

    private static void ValidateEntries(ZipArchive zip, string root)
    {
        if (zip.Entries.Count is < 1 or > MaximumFiles)
            throw new InvalidDataException("ZIP file-count limit exceeded.");
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            bool directory = entry.FullName.EndsWith('/');
            string name = directory ? entry.FullName[..^1] : entry.FullName;
            ValidatePath(root, name);
            int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if ((entry.ExternalAttributes & (int)(FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                || unixType != 0 && unixType != (directory ? 0x4000 : 0x8000)
                || directory && entry.Length != 0 || entry.Length > MaximumFileSize
                || name.Equals(CommunityRelease.FilesFileName, StringComparison.OrdinalIgnoreCase) && entry.Length > MaximumInventorySize
                || !paths.TryAdd(name, directory))
                throw new InvalidDataException("Unsafe, duplicate or oversized ZIP entry.");
            total += entry.Length;
            if (total > MaximumExpandedSize) throw new InvalidDataException("ZIP expanded-size limit exceeded.");
        }
        foreach (string name in paths.Keys)
        {
            int slash = name.IndexOf('/');
            while (slash >= 0)
            {
                if (paths.TryGetValue(name[..slash], out bool directory) && !directory)
                    throw new InvalidDataException("ZIP file and directory paths overlap.");
                slash = name.IndexOf('/', slash + 1);
            }
        }
    }

    private static string ValidatePath(string root, string relative)
    {
        string full = GameInstaller.SafePath(root, relative);
        string[] parts = relative.Split('/');
        // Windows also recognizes these console aliases and superscript device digits.
        if (parts.Any(p => IsAdditionalDevice(p.Split('.')[0])))
            throw new InvalidDataException("Community package path uses a reserved Windows device name.");
        if (StateRoots.Contains(parts[0]) || parts.Skip(1).Any(p => StateRoots.Contains(p) && !p.Equals("data", StringComparison.OrdinalIgnoreCase))
            || StateNames.Contains(Path.GetFileName(relative))
            || SecretExtensions.Contains(Path.GetExtension(relative)))
            throw new InvalidDataException("Community packages cannot contain persistent state or private keys.");
        return full;
    }

    private static bool IsAdditionalDevice(string name)
    {
        name = name.ToUpperInvariant();
        return name is "CONIN$" or "CONOUT$" || name.Length == 4
            && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
            && name[3] is '\u00b9' or '\u00b2' or '\u00b3';
    }

    private static Dictionary<string, string> EnumerateFiles(string root, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        int entries = 0;
        long total = 0;
        while (pending.TryPop(out string? directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > MaximumFiles * 2) throw new InvalidDataException("Community package has too many paths.");
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                ValidatePath(root, relative);
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new InvalidDataException("Community packages cannot contain links or devices.");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                long length = new FileInfo(path).Length;
                total += length;
                if (length > MaximumFileSize || total > MaximumExpandedSize || result.Count >= MaximumFiles
                    || !result.TryAdd(relative, relative))
                    throw new InvalidDataException("Community package exceeds its limits or contains duplicate names.");
            }
        }
        return result;
    }

    private static async Task VerifyReceipt(string directory, CommunityRelease expected, CancellationToken ct)
    {
        string path = GameInstaller.SafePath(directory, "release.json");
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Oversized staged release receipt.");
        try
        {
            var actual = JsonSerializer.Deserialize<CommunityRelease>(await File.ReadAllBytesAsync(path, ct), CommunityRelease.Json);
            if (actual != expected) throw new InvalidDataException("Staged release receipt does not match the requested release.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid staged release receipt.", ex); }
    }
}
