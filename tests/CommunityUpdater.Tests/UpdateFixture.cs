using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cranberry.Launcher.Core;

namespace CommunityUpdater.Tests;

internal sealed class UpdateFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cranberry-update-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public CommunityUpdateSettings Trust { get; }

    public UpdateFixture()
    {
        Directory.CreateDirectory(Root);
        Trust = new(1, "example/project", Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()), false, 0, "0.1.0");
    }

    public CommunityRelease Sign(CommunityRelease release) => release with
    {
        Signature = Convert.ToBase64String(_key.SignData(release.SigningPayload(), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
    };

    public (string Archive, CommunityRelease Release, Dictionary<string, byte[]> Files) CreateBundle(long sequence = 1,
        Action<Dictionary<string, byte[]>>? edit = null, Action<ZipArchive>? extraZip = null,
        CommunityUpdateSettings? packageSettings = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Cranberry.Launcher.exe"] = Encoding.UTF8.GetBytes("test GUI executable"),
            ["runtime/Cranberry.Host.exe"] = Encoding.UTF8.GetBytes("test host executable"),
            ["package/game-manifest.json"] = Encoding.UTF8.GetBytes("{\"test\":true}"),
            ["package/download-host.json"] = Encoding.UTF8.GetBytes("{}"),
            ["package/gameplay.defaults.json"] = Encoding.UTF8.GetBytes("{}"),
            ["package/environment.defaults.json"] = Encoding.UTF8.GetBytes("{}"),
            ["runtime/Data/dynamicAppearance.bin"] = new byte[] { 1, 2, 3, 4 },
            [CommunityUpdateSettings.FileName] = JsonSerializer.SerializeToUtf8Bytes(
                packageSettings ?? Trust with { Sequence = sequence, Version = "1.0.0" }, CommunityRelease.Json)
        };
        files["local-edition.json"] = JsonSerializer.SerializeToUtf8Bytes(new LocalEdition.Release(1, "1.0.0-local-test",
            Hash(files["package/game-manifest.json"]), Hash(files["runtime/Data/dynamicAppearance.bin"])), CommunityRelease.Json);
        edit?.Invoke(files);
        files[CommunityRelease.FilesFileName] = JsonSerializer.SerializeToUtf8Bytes(files.Select(p =>
            new CommunityBundleFile(p.Key, p.Value.Length, Hash(p.Value))).ToArray(), CommunityRelease.Json);
        string archive = Path.Combine(Root, "bundle-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                using var stream = zip.CreateEntry(file.Key, CompressionLevel.Optimal).Open();
                stream.Write(file.Value);
            }
            extraZip?.Invoke(zip);
        }
        var release = Sign(new(1, sequence, "1.0.0", "win-x64", 1, 1, new FileInfo(archive).Length,
            Hash(File.ReadAllBytes(archive)), Hash(files[CommunityRelease.FilesFileName]), ""));
        return (archive, release, files);
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public void Dispose()
    {
        _key.Dispose();
        // Delete only individual regular files in this fixture's freshly allocated root; never follow links.
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(Root);
        while (pending.TryPop(out string? directory))
        {
            directories.Add(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path);
                    else File.Delete(path);
                }
                else if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else File.Delete(path);
            }
        }
        foreach (string directory in directories.AsEnumerable().Reverse()) Directory.Delete(directory);
    }
}
