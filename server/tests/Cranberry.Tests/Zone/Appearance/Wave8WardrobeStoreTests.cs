using Cranberry.Zone;

namespace Cranberry.Tests.Zone.Appearance;

/// <summary>
/// Wave 8, lane SKINS (D53, docs/80 edits 8-10): durable wardrobe selections, in the shape of the
/// owner's own <c>AccountStore</c>.
/// <para>
/// The four properties under test are his: atomic write, an orphan <c>.tmp</c> renamed aside and
/// <b>never deleted</b> (project law 9), the disk barrier off the receive thread, and nothing ever
/// thrown at the caller. The one place this is stricter than his is asserted too: a restored row
/// goes back through the live validator, so a hand-edited file cannot inject a selection the
/// catalogue would refuse from the client.
/// </para>
/// </summary>
public sealed class Wave8WardrobeStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cranberry-wardrobe-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory that outlives the test is not a failure.
        }
    }

    [Fact]
    public void ANullRootIsANoOpAndNothingReachesTheDisk()
    {
        using var store = new WardrobeStore(root: null);

        Assert.False(store.Enabled);
        Assert.Null(store.Root);

        AugustWardrobeState state = store.Load(0x1234);
        Assert.Empty(state.Snapshot());

        store.Save(0x1234, state);
        store.FlushPending();
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public void ASelectionSurvivesAHostRestart()
    {
        AugustSkinCatalogEntry hat = AugustSkinCatalog.Apparel.First(entry =>
            entry.CategoryPrototypeId == 2158 && entry.AccountItemId != 0);

        using (var writing = new WardrobeStore(_root, coalesceMs: 0))
        {
            AugustWardrobeState state = writing.Load(0xABCD);
            Assert.True(state.TryApply(Selection(hat), out _, out _, out _));
            writing.Save(0xABCD, state);
            writing.FlushPending();
            Assert.Equal(1, writing.Saves);
        }

        Assert.True(File.Exists(Path.Combine(_root, WardrobeStore.FileNameFor(0xABCD))));

        // A different process, reading the same directory: the pick is still there.
        using var reading = new WardrobeStore(_root, coalesceMs: 0);
        AugustWardrobeState restored = reading.Load(0xABCD);
        AugustSkinCatalogEntry only = Assert.Single(restored.Snapshot());
        Assert.Equal(hat.CategoryPrototypeId, only.CategoryPrototypeId);
        Assert.Equal(hat.RewardItemId, only.RewardItemId);
        Assert.Equal(1, reading.Restores);

        // ...and a character with no file is simply empty, not an error.
        Assert.Empty(reading.Load(0xBEEF).Snapshot());
    }

    /// <summary>
    /// Saving the WHOLE snapshot rather than one pick, so a cleared category cannot leave a stale
    /// row on disk - the owner makes exactly this point in his own save path.
    /// </summary>
    [Fact]
    public void ClearingACategoryClearsItOnDiskToo()
    {
        AugustSkinCatalogEntry hat = AugustSkinCatalog.Apparel.First(entry =>
            entry.CategoryPrototypeId == 2158 && entry.AccountItemId != 0);

        using var store = new WardrobeStore(_root, coalesceMs: 0);
        AugustWardrobeState state = store.Load(1);
        Assert.True(state.TryApply(Selection(hat), out _, out _, out _));
        store.Save(1, state);
        store.FlushPending();

        Assert.True(state.TryApply(
            new SkinItemSelectionRequest(
                SkinItemSelectionRequest.RequestUnsetSkinItem,
                Field1: 0, Field2: 0, SlotType: 0, hat.CategoryPrototypeId, ClickedId: 0),
            out _,
            out bool removed,
            out _));
        Assert.True(removed);
        store.Save(1, state);
        store.FlushPending();

        using var reopened = new WardrobeStore(_root, coalesceMs: 0);
        Assert.Empty(reopened.Load(1).Snapshot());
    }

    /// <summary>
    /// <b>Project law 9, in code.</b> A <c>.tmp</c> left by a crash between the write and the
    /// rename is scrap, but it is still a diagnostic — so it is renamed aside with a timestamp and
    /// never deleted. The owner has lost irreplaceable captures to a delete; this is the rule that
    /// says it cannot happen here.
    /// </summary>
    [Fact]
    public void AnOrphanTemporaryIsRenamedAsideAndNeverDeleted()
    {
        Directory.CreateDirectory(_root);
        string orphan = Path.Combine(_root, WardrobeStore.FileNameFor(7) + ".tmp");
        File.WriteAllText(orphan, "{ half-written }");

        using var store = new WardrobeStore(_root, coalesceMs: 0);

        Assert.Equal(1, store.OrphanTemporaries);
        Assert.False(File.Exists(orphan));

        string kept = Assert.Single(Directory.GetFiles(_root, "*.orphan-*"));
        Assert.Equal("{ half-written }", File.ReadAllText(kept));
        Assert.StartsWith(WardrobeStore.FileNameFor(7) + ".tmp.orphan-", Path.GetFileName(kept));
    }

    /// <summary>
    /// Stricter than the owner's store, deliberately: he trusts his file, this replays every row
    /// through <c>TryApply</c>. A row the live catalogue would refuse from the client is refused
    /// from the disk too, and the rest of the file still loads.
    /// </summary>
    [Fact]
    public void AStaleFileCannotInjectARowTheCatalogueRefuses()
    {
        AugustSkinCatalogEntry hat = AugustSkinCatalog.Apparel.First(entry =>
            entry.CategoryPrototypeId == 2158 && entry.AccountItemId != 0);

        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, WardrobeStore.FileNameFor(9)),
            $$"""
            {
              "CharacterGuid": 9,
              "SavedUtc": "2026-08-30T00:00:00Z",
              "Selections": [
                { "CategoryPrototypeId": 999999, "AccountItemId": 888888, "RewardItemId": 777777 },
                { "CategoryPrototypeId": {{hat.CategoryPrototypeId}}, "AccountItemId": {{hat.AccountItemId}}, "RewardItemId": {{hat.RewardItemId}} }
              ]
            }
            """);

        using var store = new WardrobeStore(_root, coalesceMs: 0);
        AugustSkinCatalogEntry only = Assert.Single(store.Load(9).Snapshot());
        Assert.Equal(hat.RewardItemId, only.RewardItemId);
    }

    [Fact]
    public void AnUnreadableFileCostsTheSelectionsAndNothingElse()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, WardrobeStore.FileNameFor(11)), "not json at all");

        var lines = new List<string>();
        using var store = new WardrobeStore(_root, lines.Add, coalesceMs: 0);

        Assert.Empty(store.Load(11).Snapshot());
        Assert.Contains(lines, line => line.Contains("unreadable", StringComparison.Ordinal));

        // The file is left exactly where it is: it is the only evidence of what went wrong.
        Assert.Equal("not json at all", File.ReadAllText(Path.Combine(_root, WardrobeStore.FileNameFor(11))));
    }

    [Fact]
    public void TheFileNameIsFixedWidthHexAndCanNeverBeAPath()
    {
        Assert.Equal("w-0000000000000001.json", WardrobeStore.FileNameFor(1));
        Assert.Equal("w-ffffffffffffffff.json", WardrobeStore.FileNameFor(ulong.MaxValue));
        Assert.Equal("w-00000000abcdef01.json", WardrobeStore.FileNameFor(0xABCDEF01));

        foreach (ulong guid in (ulong[])[0, 1, 0x1234_5678_9ABC_DEF0, ulong.MaxValue])
        {
            string name = WardrobeStore.FileNameFor(guid);
            Assert.Equal(name, Path.GetFileName(name));
            Assert.DoesNotContain("..", name, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The coalescing writer: many saves in a row, one drain, and <c>FlushPending</c> is what makes
    /// the disk state observable. The receive thread never takes the barrier.
    /// </summary>
    [Fact]
    public void ManySavesCoalesceAndFlushMakesThemObservable()
    {
        AugustSkinCatalogEntry hat = AugustSkinCatalog.Apparel.First(entry =>
            entry.CategoryPrototypeId == 2158 && entry.AccountItemId != 0);

        using var store = new WardrobeStore(_root, coalesceMs: WardrobeStore.CoalesceMs);
        AugustWardrobeState state = store.Load(3);
        Assert.True(state.TryApply(Selection(hat), out _, out _, out _));

        for (int index = 0; index < 50; index++)
        {
            store.Save(3, state);
        }

        store.FlushPending();

        // Fifty clicks, at most one file write per drained guid — never fifty fsyncs.
        Assert.InRange(store.Saves, 1, 2);
        Assert.True(File.Exists(Path.Combine(_root, WardrobeStore.FileNameFor(3))));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    private static SkinItemSelectionRequest Selection(AugustSkinCatalogEntry entry) =>
        new(
            SkinItemSelectionRequest.RequestSetSkinItemByItemId,
            Field1: 1,
            Field2: 0,
            SlotType: 1,
            entry.CategoryPrototypeId,
            entry.AccountItemId);
}
