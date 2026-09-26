using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cranberry.Launcher.Core;

return Publisher.Run(args);

internal static class Publisher
{
    private const long MaximumFileSize = CommunityBundleStore.MaximumFileSize;
    private const long MaximumExpandedSize = CommunityBundleStore.MaximumExpandedSize;
    private const long MaximumArchiveSize = CommunityRelease.MaximumSize;
    private const int MaximumFiles = CommunityBundleStore.MaximumFiles, MaximumInventorySize = CommunityBundleStore.MaximumInventorySize;
    private static readonly JsonSerializerOptions Json = new(CommunityRelease.Json) { WriteIndented = true };
    private sealed record PackageFile(string Path, long Size, string Sha256);
    private static readonly string[] Required =
    [
        "Cranberry.Launcher.exe", "runtime/Cranberry.Host.exe", "runtime/Data/dynamicAppearance.bin",
        "local-edition.json", "community-update.json", "package/game-manifest.json",
        "package/download-host.json", "package/gameplay.defaults.json", "package/environment.defaults.json",
    ];
    private static readonly HashSet<string> PrivateRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Game", "data", "state", "logs", "launcher-release", ".cranberry-updates", ".community-updates", "updates",
    };
    private static readonly HashSet<string> PrivateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "launcher.json", "launcher-host.json", "launcher-defaults.json", "local-ports.json", "local-accounts.json",
        "cranberry.json", "environment.json", ".env", "community-release.json",
    };

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException(Usage);
            var options = ReadOptions(args.Skip(1).ToArray());
            switch (args[0])
            {
                case "keygen":
                    Allowed(options, "private-key", "public-key");
                    Keygen(RequiredOption(options, "private-key"), RequiredOption(options, "public-key"));
                    break;
                case "inventory":
                    Allowed(options, "package");
                    Inventory(RequiredOption(options, "package"));
                    break;
                case "sign":
                    Allowed(options, "package", "output", "private-key", "sequence", "version", "minimum-updater-version", "data-schema");
                    Sign(RequiredOption(options, "package"), RequiredOption(options, "output"),
                        RequiredOption(options, "private-key"), Positive(options, "sequence"), RequiredOption(options, "version"),
                        checked((int)Positive(options, "minimum-updater-version", "1")), checked((int)Positive(options, "data-schema", "1")));
                    break;
                default: throw new ArgumentException(Usage);
            }
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException
            or UnauthorizedAccessException or CryptographicException or JsonException or OverflowException or FormatException)
        {
            Console.Error.WriteLine("Community release: " + ex.Message);
            return 1;
        }
    }

    private const string Usage = "Use keygen --private-key <new.pem> --public-key <new-public.pem>; " +
        "inventory --package <built-folder>; or sign --package <built-folder> --output <new-folder> " +
        "--private-key <private.pem> --sequence <positive-number> --version <version> " +
        "[--minimum-updater-version 1] [--data-schema 1]. This tool never publishes or contacts a network service.";

    private static Dictionary<string, string> ReadOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal)
                || args[i + 1].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i][2..], args[i + 1]))
                throw new ArgumentException("Options must be unique --name value pairs. " + Usage);
        }
        return options;
    }
    private static void Allowed(Dictionary<string, string> options, params string[] names)
    {
        if (options.Keys.Any(k => !names.Contains(k, StringComparer.Ordinal)))
            throw new ArgumentException("Unknown option. " + Usage);
    }
    private static string RequiredOption(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException("Missing --" + name + ".");
    private static long Positive(Dictionary<string, string> options, string name, string? fallback = null)
    {
        string value = options.GetValueOrDefault(name) ?? fallback ?? RequiredOption(options, name);
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long result) && result > 0
            ? result : throw new ArgumentException("--" + name + " must be a positive integer.");
    }

    private static void Keygen(string privateFile, string publicFile)
    {
        string secret = Path.GetFullPath(privateFile), identity = Path.GetFullPath(publicFile);
        if (secret.Equals(identity, StringComparison.OrdinalIgnoreCase) || File.Exists(secret) || File.Exists(identity)
            || Directory.Exists(secret) || Directory.Exists(identity))
            throw new IOException("Choose two different new key files; existing files are never overwritten.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WriteNew(secret, Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem() + "\n"));
        WriteNew(identity, Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem() + "\n"));
        Console.WriteLine("Created a dedicated community P-256 signing key and public key. Keep the private file offline and outside source/package folders.");
    }

    private static void Inventory(string packageDirectory)
    {
        string root = ExistingDirectory(packageDirectory);
        string destination = GameInstaller.SafePath(root, CommunityRelease.FilesFileName);
        if (File.Exists(destination)) throw new IOException("The inventory already exists. Build a fresh package rather than replacing its recorded contents.");
        PackageFile[] files = Scan(root);
        var settings = ReadSettings(root);
        ValidateSettings(settings);
        var edition = JsonSerializer.Deserialize<LocalEdition.Release>(File.ReadAllText(Path.Combine(root, "local-edition.json")))
            ?? throw new InvalidDataException("The local edition metadata is empty.");
        if (edition.Version != 1) throw new InvalidDataException("Unsupported local edition schema.");
        RequireHash(root, "package/game-manifest.json", edition.GameManifestSha256);
        RequireHash(root, "runtime/Data/dynamicAppearance.bin", edition.AppearanceSha256);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(files, Json);
        if (json.Length > MaximumInventorySize || files.Sum(f => f.Size) + json.Length > MaximumExpandedSize)
            throw new InvalidDataException("The package inventory or expanded package is too large.");
        WriteNew(destination, json);
        Console.WriteLine($"Recorded {files.Length} package files ({files.Sum(f => f.Size):N0} bytes). No local player data was included.");
    }

    private static void Sign(string packageDirectory, string outputDirectory, string privateFile,
        long sequence, string version, int minimumUpdaterVersion, int dataSchema)
    {
        string root = ExistingDirectory(packageDirectory), output = Path.GetFullPath(outputDirectory);
        if (Contains(root, output) || Contains(output, root))
            throw new IOException("Choose an output directory separate from the built package.");
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Choose a new signing output directory.");
        string secret = Path.GetFullPath(privateFile);
        if (Contains(root, secret) || Contains(output, secret)) throw new IOException("The signing key cannot be inside a package or release output directory.");
        var settings = ReadSettings(root); ValidateSettings(settings);
        if (settings.Sequence != sequence || settings.Version != version || sequence <= 0)
            throw new InvalidDataException("Signed sequence/version must match the package's enabled community update settings.");
        string inventoryPath = GameInstaller.SafePath(root, CommunityRelease.FilesFileName);
        if (new FileInfo(inventoryPath).Length > MaximumInventorySize) throw new InvalidDataException("The package inventory is too large.");
        byte[] inventory = File.ReadAllBytes(inventoryPath);
        PackageFile[] expected = JsonSerializer.Deserialize<PackageFile[]>(inventory, Json)
            ?? throw new InvalidDataException("The package inventory is empty.");
        PackageFile[] actual = Scan(root);
        if (!expected.SequenceEqual(actual)) throw new InvalidDataException("Package files differ from the recorded inventory. Build a fresh package before signing.");
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(secret));
        EnsureP256(key);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        if (publicKey != settings.PublicKey) throw new InvalidDataException("The private key does not match this package's community public key.");
        // Validate every signed metadata field before creating output files.
        _ = new CommunityRelease(1, sequence, version, "win-x64", minimumUpdaterVersion, dataSchema,
            1, new string('0', 64), Convert.ToHexString(SHA256.HashData(inventory)), "").SigningPayload();
        Directory.CreateDirectory(output);
        string archivePath = Path.Combine(output, CommunityRelease.ArchiveFileName);
        using (var archiveStream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (PackageFile file in expected)
                {
                    var entry = archive.CreateEntry(file.Path, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var source = File.Open(GameInstaller.SafePath(root, file.Path), FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var target = entry.Open();
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = new byte[128 * 1024]; long length = 0; int read;
                    while ((read = source.Read(buffer)) > 0)
                    {
                        length += read;
                        if (length > file.Size) throw new InvalidDataException("A package file changed while creating the archive.");
                        digest.AppendData(buffer, 0, read); target.Write(buffer, 0, read);
                    }
                    if (length != file.Size || Convert.ToHexString(digest.GetHashAndReset()) != file.Sha256)
                        throw new InvalidDataException("A package file changed while creating the archive.");
                }
                var inventoryEntry = archive.CreateEntry(CommunityRelease.FilesFileName, CompressionLevel.Optimal);
                inventoryEntry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var inventoryStream = inventoryEntry.Open(); inventoryStream.Write(inventory);
            }
            archiveStream.Flush(flushToDisk: true);
        }
        using var archiveInput = File.OpenRead(archivePath);
        if (archiveInput.Length > MaximumArchiveSize) throw new InvalidDataException("The update archive exceeds two GiB.");
        var unsigned = new CommunityRelease(1, sequence, version, "win-x64", minimumUpdaterVersion, dataSchema,
            archiveInput.Length, Convert.ToHexString(SHA256.HashData(archiveInput)), Convert.ToHexString(SHA256.HashData(inventory)), "");
        var signed = unsigned with { Signature = Convert.ToBase64String(key.SignData(unsigned.SigningPayload(),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
        signed.Verify(settings.PublicKey);
        CommunityBundleStore.ValidatePackage(root, settings, signed).GetAwaiter().GetResult();
        WriteNew(Path.Combine(output, CommunityRelease.ManifestFileName), JsonSerializer.SerializeToUtf8Bytes(signed, Json));
        Console.WriteLine($"Signed community version {version}, sequence {sequence}: {expected.Length} files, {signed.Size:N0} archive bytes.");
        Console.WriteLine("Created " + CommunityRelease.ArchiveFileName + " and " + CommunityRelease.ManifestFileName + ". Nothing was published.");
    }

    private static PackageFile[] Scan(string root)
    {
        var files = new List<PackageFile>(); var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0; int entries = 0;
        void Visit(string directory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > MaximumFiles * 2) throw new InvalidDataException("The package contains too many paths.");
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Package links and junctions are forbidden.");
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                GameInstaller.SafePath(root, relative);
                string name = Path.GetFileName(path);
                string[] parts = relative.Split('/');
                if (parts.Any(p => AdditionalDevice(p.Split('.')[0])))
                    throw new InvalidDataException("Package paths cannot use reserved Windows device names.");
                if (PrivateRoots.Contains(parts[0])
                    || parts.Skip(1).Any(p => PrivateRoots.Contains(p) && !p.Equals("data", StringComparison.OrdinalIgnoreCase))
                    || PrivateNames.Contains(name)
                    || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
                    || new[] { ".pem", ".key", ".pfx", ".p12", ".lock", ".part" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("Private state, credentials, or release metadata must not be packaged: " + relative);
                if ((attributes & FileAttributes.Directory) != 0) { Visit(path); continue; }
                if (relative.Equals(CommunityRelease.FilesFileName, StringComparison.Ordinal)) continue;
                if (!unique.Add(relative) || files.Count >= MaximumFiles - 1) throw new InvalidDataException("Package contains duplicate paths or too many files.");
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > MaximumFileSize || (total += stream.Length) > MaximumExpandedSize)
                    throw new InvalidDataException("Package file sizes exceed updater limits.");
                files.Add(new(relative, stream.Length, Convert.ToHexString(SHA256.HashData(stream))));
            }
        }
        Visit(root);
        foreach (string path in Required)
            if (!unique.Contains(path)) throw new InvalidDataException("Required package file is missing: " + path);
        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }

    private static bool AdditionalDevice(string name)
    {
        name = name.ToUpperInvariant();
        return name is "CONIN$" or "CONOUT$" || name.Length == 4
            && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
            && name[3] is '\u00b9' or '\u00b2' or '\u00b3';
    }

    private static CommunityUpdateSettings ReadSettings(string root) => CommunityUpdateSettings.Load(root);
    private static void ValidateSettings(CommunityUpdateSettings settings)
    {
        settings.Validate();
        if (settings.Repository != "Mixcria/h1z1-pre-season-5-project")
            throw new InvalidDataException("The package must use the community release repository.");
    }
    private static void EnsureP256(ECDsa key)
    {
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new InvalidDataException("Use a dedicated NIST P-256 community signing key.");
    }
    private static void RequireHash(string root, string relative, string hash)
    {
        using var stream = File.OpenRead(GameInstaller.SafePath(root, relative));
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Local edition metadata does not match " + relative + ".");
    }
    private static string ExistingDirectory(string path)
    {
        string root = Path.GetFullPath(path);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The built package directory does not exist.");
        GameInstaller.SafePath(root, "package-check"); // Also reject a reparse point in the root/ancestor chain.
        return root;
    }
    private static bool Contains(string directory, string path) => path.Equals(directory, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void WriteNew(string path, byte[] contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(contents); stream.Flush(flushToDisk: true);
    }
}
