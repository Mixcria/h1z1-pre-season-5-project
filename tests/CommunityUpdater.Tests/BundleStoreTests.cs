using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Cranberry.Launcher.Core;

namespace CommunityUpdater.Tests;

public sealed class BundleStoreTests
{
    [Fact]
    public async Task SignedBundleMayChangeItsDefaultChannelWithoutChangingInstalledRootDiscoveryPolicy()
    {
        using var f = new UpdateFixture();
        var bundle = f.CreateBundle(packageSettings: f.Trust with
            { Sequence = 1, Version = "1.0.0", IncludePrereleases = true });
        string app = await CommunityBundleStore.Stage(bundle.Archive, Path.Combine(f.Root, "versions"), bundle.Release, f.Trust);
        Assert.True(CommunityUpdateSettings.Load(app).IncludePrereleases);
        Assert.False(f.Trust.IncludePrereleases);
        using var feed = new CommunityReleaseFeed(f.Trust,
            ReleaseFeedTests.FeedHandler(new[] { bundle.Release }, preview: _ => true));
        Assert.Null(await feed.Check(0));
    }

    [Fact]
    public async Task StagesCompleteVerifiedBundleAndReusesOnlyRevalidatedVersion()
    {
        using var f = new UpdateFixture();
        var bundle = f.CreateBundle();
        string versions = Path.Combine(f.Root, "versions");
        string app = await CommunityBundleStore.Stage(bundle.Archive, versions, bundle.Release, f.Trust);
        Assert.Equal(Path.Combine(versions, $"1-{bundle.Release.Sha256}", "app"), app);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(app)!, "release.json")));
        Assert.Equal(bundle.Files.Count, Directory.GetFiles(app, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(app, await CommunityBundleStore.Stage(bundle.Archive, versions, bundle.Release, f.Trust));
        await File.WriteAllTextAsync(Path.Combine(app, "Cranberry.Launcher.exe"), "altered");
        await Assert.ThrowsAsync<InvalidDataException>(() => CommunityBundleStore.Stage(bundle.Archive, versions, bundle.Release, f.Trust));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("/absolute.exe")]
    [InlineData("C:/absolute.exe")]
    [InlineData("runtime\\escape.exe")]
    [InlineData("runtime/Cranberry.Host.exe:stream")]
    [InlineData("runtime/CON.txt")]
    [InlineData("runtime/aux")]
    [InlineData("runtime/CONIN$")]
    [InlineData("runtime/CONOUT$.txt")]
    [InlineData("runtime/COM\u00b9.txt")]
    [InlineData("runtime/trailing.")]
    [InlineData("runtime/space ")]
    [InlineData("runtime//empty")]
    [InlineData("runtime/./relative")]
    public async Task SignedArchiveStillCannotEscapeOrUseUnsafeWindowsNames(string name)
    {
        using var f = new UpdateFixture();
        var bundle = f.CreateBundle(extraZip: zip => zip.CreateEntry(name));
        await Assert.ThrowsAsync<InvalidDataException>(() => CommunityBundleStore.Stage(bundle.Archive,
            Path.Combine(f.Root, "versions"), bundle.Release, f.Trust));
        Assert.False(File.Exists(Path.Combine(f.Root, "escape.exe")));
    }

    [Theory]
    [InlineData("CRANBERRY.LAUNCHER.EXE")]
    [InlineData("runtime")]
    [InlineData("runtime/Cranberry.Host.exe/child")]
    public async Task RejectsCaseDuplicatesAndFileDirectoryOverlapBeforeExtraction(string name)
    {
        using var f = new UpdateFixture();
        var bundle = f.CreateBundle(extraZip: zip => zip.CreateEntry(name));
        await Assert.ThrowsAsync<InvalidDataException>(() => CommunityBundleStore.Stage(bundle.Archive,
            Path.Combine(f.Root, "versions"), bundle.Release, f.Trust));
    }

    [Theory]
    [InlineData(unchecked((int)0xa1ff0000))] // Unix symbolic link.
    [InlineData(unchecked((int)0x21ff0000))] // Unix character device.
    [InlineData((int)FileAttributes.ReparsePoint)]
    public async Task RejectsLinksDevicesAndReparseEntries(int attributes)
    {
        using var f = new UpdateFixture();
        var bundle = f.CreateBundle(extraZip: zip =>
        {
            var entry = zip.CreateEntry("linked"); entry.ExternalAttributes = attributes;
            using var stream = entry.Open(); stream.Write(Encoding.UTF8.GetBytes("../elsewhere"));
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => CommunityBundleStore.Stage(bundle.Archive,
            Path.Combine(f.Root, "versions"), bundle.Release, f.Trust));
    }

    [Theory]
    [InlineData("Game/H1Z1.exe")]
    [InlineData("state/account.json")]
    [InlineData("runtime/state/account.json")]
    [InlineData("logs/client.log")]
    [InlineData(".community-updates/active.json")]
    [InlineData("runtime/server.pfx")]
    [InlineData("package/private.pem")]
    [InlineData("launcher-host.json")]
    public async Task NeverAcceptsPersistentStateOrPrivateKeysEvenWhenSigned(string name)
    {
        using var f = new UpdateFixture();
        var bundle = f.CreateBundle(edit: files => files[name] = new byte[] { 9 });
        await Assert.ThrowsAsync<InvalidDataException>(() => CommunityBundleStore.Stage(bundle.Archive,
            Path.Combine(f.Root, "versions"), bundle.Release, f.Trust));
    }

    [Fact]
    public async Task RejectsUnlistedFileMissingRequiredFileAndDifferentPackageIdentity()
    {
        using var f = new UpdateFixture();
        var extra = f.CreateBundle(extraZip: zip => zip.CreateEntry("unlisted.txt"));
        var missing = f.CreateBundle(edit: files => files.Remove("runtime/Cranberry.Host.exe"));
        var identity = f.CreateBundle(packageSettings: f.Trust with { Sequence = 9, Version = "1.0.0" });
        foreach (var bundle in new[] { extra, missing, identity })
            await Assert.ThrowsAsync<InvalidDataException>(() => CommunityBundleStore.Stage(bundle.Archive,
                Path.Combine(f.Root, "versions"), bundle.Release, f.Trust));
    }

    [Fact]
    public async Task ChecksInventoryBytesNotJustEquivalentJsonAndHashesEveryFile()
    {
        using var f = new UpdateFixture();
        var original = f.CreateBundle();
        RewriteZip(original.Archive, CommunityRelease.FilesFileName,
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original.Files[CommunityRelease.FilesFileName]) + " "));
        var inventoryTamper = ResignArchive(f, original.Archive, original.Release);
        await Assert.ThrowsAsync<InvalidDataException>(() => CommunityBundleStore.Stage(original.Archive,
            Path.Combine(f.Root, "versions"), inventoryTamper, f.Trust));
        var corrupted = f.CreateBundle();
        RewriteZip(corrupted.Archive, "Cranberry.Launcher.exe", Encoding.UTF8.GetBytes("corrupt"));
        await Assert.ThrowsAsync<InvalidDataException>(() => CommunityBundleStore.Stage(corrupted.Archive,
            Path.Combine(f.Root, "versions"), ResignArchive(f, corrupted.Archive, corrupted.Release), f.Trust));
    }

    [Fact]
    public async Task RejectsDuplicateInventoryAndUnsupportedDataSchema()
    {
        using var f = new UpdateFixture();
        var bundle = f.CreateBundle();
        var inventory = JsonSerializer.Deserialize<CommunityBundleFile[]>(bundle.Files[CommunityRelease.FilesFileName], CommunityRelease.Json)!;
        byte[] duplicate = JsonSerializer.SerializeToUtf8Bytes(inventory.Concat(new[] { inventory[0] }), CommunityRelease.Json);
        RewriteZip(bundle.Archive, CommunityRelease.FilesFileName, duplicate);
        var release = ResignArchive(f, bundle.Archive, bundle.Release with { FilesSha256 = UpdateFixture.Hash(duplicate) });
        await Assert.ThrowsAsync<InvalidDataException>(() => CommunityBundleStore.Stage(bundle.Archive,
            Path.Combine(f.Root, "versions"), release, f.Trust));
        await Assert.ThrowsAsync<InvalidDataException>(() => CommunityBundleStore.Stage(bundle.Archive,
            Path.Combine(f.Root, "versions"), release with { DataSchema = 2 }, f.Trust));
    }

    [Fact]
    public async Task TruncatedArchiveAndCancellationCannotPromoteVersion()
    {
        using var f = new UpdateFixture();
        var bundle = f.CreateBundle();
        string versions = Path.Combine(f.Root, "versions");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CommunityBundleStore.Stage(bundle.Archive,
            versions, bundle.Release, f.Trust, canceled.Token));
        Assert.Empty(Directory.GetDirectories(versions));
        using (var file = File.OpenWrite(bundle.Archive)) file.SetLength(file.Length - 12);
        await Assert.ThrowsAsync<InvalidDataException>(() => CommunityBundleStore.Stage(bundle.Archive,
            versions, ResignArchive(f, bundle.Archive, bundle.Release), f.Trust));
        Assert.DoesNotContain(Directory.GetDirectories(versions), p => !Path.GetFileName(p).StartsWith(".stage-"));
    }

    [Fact]
    public async Task RejectsExpandedSizeAndEntryCountFromHeadersWithoutExtracting()
    {
        using var f = new UpdateFixture();
        var bundle = f.CreateBundle();
        byte[] zipBytes = File.ReadAllBytes(bundle.Archive);
        int central = Find(zipBytes, new byte[] { 0x50, 0x4b, 0x01, 0x02 });
        Assert.True(central >= 0);
        BitConverter.GetBytes((uint)(CommunityBundleStore.MaximumFileSize + 1)).CopyTo(zipBytes, central + 24);
        File.WriteAllBytes(bundle.Archive, zipBytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => CommunityBundleStore.Stage(bundle.Archive,
            Path.Combine(f.Root, "versions"), ResignArchive(f, bundle.Archive, bundle.Release), f.Trust));
        var many = f.CreateBundle(extraZip: zip =>
        {
            for (int i = 0; i < CommunityBundleStore.MaximumFiles; i++) zip.CreateEntry("empty/" + i);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => CommunityBundleStore.Stage(many.Archive,
            Path.Combine(f.Root, "versions"), many.Release, f.Trust));
    }

    internal static CommunityRelease ResignArchive(UpdateFixture f, string archive, CommunityRelease release) =>
        f.Sign(release with { Size = new FileInfo(archive).Length, Sha256 = UpdateFixture.Hash(File.ReadAllBytes(archive)) });
    internal static void RewriteZip(string archive, string name, byte[] bytes)
    {
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
        zip.GetEntry(name)!.Delete();
        using var output = zip.CreateEntry(name).Open(); output.Write(bytes);
    }
    private static int Find(byte[] data, byte[] needle)
    {
        for (int i = 0; i <= data.Length - needle.Length; i++) if (data.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
