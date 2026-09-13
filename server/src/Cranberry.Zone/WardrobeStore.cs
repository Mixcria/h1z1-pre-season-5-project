using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cranberry.Zone;

/// <summary>
/// Durable wardrobe selections, one file per character.
/// <para>
/// <b>D53 / docs/80 edits 8-9.</b> Ported in <em>shape</em> from the owner own
/// <c>Login\AccountStore.cs</c>, which is clean own work and is the one thing this tree genuinely
/// lacked: before this, <c>ZoneService._wardrobes</c> was a <c>ConcurrentDictionary</c> that was
/// never written to disk, never read back, and never evicted an entry, so every skin the owner
/// picked was gone the next time the host restarted and every character guid that had ever
/// connected was retained for the life of the process.
/// </para>
/// <para>Four properties, all of them his:</para>
/// <list type="number">
/// <item><b>Atomic.</b> Write <c>&lt;file&gt;.tmp</c>, <c>Flush(flushToDisk: true)</c>, then
/// <c>File.Move(..., overwrite: true)</c>. A crash mid-write costs the last change, never the
/// file.</item>
/// <item><b>A <c>.tmp</c> found at boot is renamed <c>&lt;name&gt;.tmp.orphan-yyyyMMddHHmmss</c>
/// and NEVER deleted</b> - project law 9 written down in code, and the single best line in his
/// file.</item>
/// <item><b>The disk barrier is off the receive thread.</b> Saves are marked dirty under the lock
/// and drained by a background writer after a coalescing delay, because a 150-player match reset
/// would otherwise be 150 back-to-back fsyncs on the one thread that answers the clients.</item>
/// <item><b>Nothing throws.</b> Every failure is caught and logged as "the previous file on disk
/// is intact; this change lives only in memory".</item>
/// </list>
/// <para>
/// One place it is deliberately <b>stricter</b> than his: a restored selection is replayed through
/// <see cref="AugustWardrobeState.TryApply"/> own validation rather than written into the
/// dictionary, so a stale or hand-edited file can never inject a row the live catalogue rejects.
/// </para>
/// <para>
/// Cranberry needs none of his key hashing: his account key is a wire-supplied string that can
/// contain a path separator, while a character guid is a fixed-width unsigned integer. The file
/// name is <c>w-{guid:x16}.json</c> and the guid is repeated inside the file.
/// </para>
/// </summary>
public sealed class WardrobeStore : IDisposable
{
    /// <summary>Milliseconds a save waits for its neighbours before the disk barrier.</summary>
    public const int CoalesceMs = 200;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string? _root;
    private readonly Action<string> _log;
    private readonly int _coalesceMs;
    private readonly ConcurrentDictionary<ulong, AugustWardrobeState> _dirty = new();
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly object _drainGate = new();
    private Task _writer = Task.CompletedTask;
    private bool _disposed;

    /// <summary>Saves that reached the disk.</summary>
    public long Saves { get; private set; }

    /// <summary>Files read back at login.</summary>
    public long Restores { get; private set; }

    /// <summary>Orphan <c>.tmp</c> files renamed aside at open. Never deleted.</summary>
    public int OrphanTemporaries { get; private set; }

    /// <summary>True when this store is a no-op because no root was configured.</summary>
    public bool Enabled => _root is not null;

    /// <summary>The directory this store writes to, or <c>null</c>.</summary>
    public string? Root => _root;

    /// <summary>
    /// Opens (creating if necessary) the store at <paramref name="root"/>. A null or blank root, or
    /// a directory that cannot be created, yields a disabled store: every method below becomes a
    /// no-op and the host keeps running with selections in memory only, exactly as before wave 8.
    /// </summary>
    public WardrobeStore(string? root, Action<string>? log = null, int coalesceMs = CoalesceMs)
    {
        _log = log ?? (_ => { });
        _coalesceMs = Math.Max(0, coalesceMs);
        if (string.IsNullOrWhiteSpace(root))
        {
            _root = null;
            return;
        }

        string full = Path.GetFullPath(root);
        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            _log($"wardrobe store: cannot create \"{full}\" ({exception.Message}) - running "
                + "in-memory only, NOTHING WILL PERSIST this run");
            _root = null;
            return;
        }

        _root = full;
        RenameOrphanTemporaries();
    }

    /// <summary>
    /// The file name for one character guid. Fixed width, lower-case hex, no wire-supplied text,
    /// so it can never be a path traversal.
    /// </summary>
    public static string FileNameFor(ulong characterGuid) => $"w-{characterGuid:x16}.json";

    /// <summary>
    /// The state for <paramref name="characterGuid"/>, restored from disk when a file exists.
    /// Never throws and never returns null: an unreadable or corrupt file yields a fresh state and
    /// one log line, because losing a wardrobe must not cost a login.
    /// </summary>
    public AugustWardrobeState Load(ulong characterGuid)
    {
        var state = new AugustWardrobeState();
        if (_root is null)
        {
            return state;
        }

        string file = Path.Combine(_root, FileNameFor(characterGuid));
        if (!File.Exists(file))
        {
            return state;
        }

        WardrobeFile? saved;
        try
        {
            saved = JsonSerializer.Deserialize<WardrobeFile>(File.ReadAllText(file), Json);
        }
        catch (Exception exception) when (exception is IOException or JsonException
            or UnauthorizedAccessException)
        {
            _log($"wardrobe store: {FileNameFor(characterGuid)} unreadable ({exception.Message}) - "
                + "the character starts with no selections and the file is left on disk untouched");
            return state;
        }

        if (saved is null)
        {
            return state;
        }

        int applied = 0;
        int rejected = 0;
        foreach (WardrobeSelection selection in saved.Selections)
        {
            // Replayed through the live validator, not written into the dictionary. A stale file
            // cannot inject a row this catalogue would refuse from the client.
            var request = new SkinItemSelectionRequest(
                SkinItemSelectionRequest.RequestSetSkinItem,
                Field1: 0,
                Field2: 0,
                SlotType: 0,
                selection.CategoryPrototypeId,
                selection.ClickedId);
            if (state.TryApply(request, out _, out _, out string reason))
            {
                applied++;
            }
            else
            {
                rejected++;
                _log($"wardrobe store: {FileNameFor(characterGuid)} row "
                    + $"{selection.CategoryPrototypeId}/{selection.ClickedId} refused ({reason})");
            }
        }

        Restores++;
        _log($"wardrobe store: restored {applied} selection(s) for character "
            + $"{characterGuid} from {FileNameFor(characterGuid)}"
            + (rejected == 0 ? string.Empty : $"; {rejected} stale row(s) refused"));
        return state;
    }

    /// <summary>
    /// Records the WHOLE of <paramref name="state"/> for later writing. The whole snapshot and
    /// never one pick, so that clearing a category cannot leave a stale row behind - the owner
    /// makes the same point in his own save path.
    /// </summary>
    public void Save(ulong characterGuid, AugustWardrobeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_root is null || _disposed)
        {
            return;
        }

        _dirty[characterGuid] = state;
        ScheduleDrain();
    }

    /// <summary>
    /// Writes every pending save on the calling thread and returns when the disk has them. Called
    /// at shutdown and by tests; never on the receive path.
    /// </summary>
    public void FlushPending()
    {
        if (_root is null)
        {
            return;
        }

        Task writer;
        lock (_drainGate)
        {
            writer = _writer;
        }

        try
        {
            writer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Drain() never throws; this is belt and braces around Wait itself.
        }

        Drain();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        FlushPending();
        _writerGate.Dispose();
    }

    private void ScheduleDrain()
    {
        lock (_drainGate)
        {
            if (!_writer.IsCompleted)
            {
                // A drain is already pending; it will pick this entry up when it runs.
                return;
            }

            _writer = Task.Run(async () =>
            {
                if (_coalesceMs > 0)
                {
                    await Task.Delay(_coalesceMs).ConfigureAwait(false);
                }

                Drain();
            });
        }
    }

    private void Drain()
    {
        if (_root is null)
        {
            return;
        }

        _writerGate.Wait();
        try
        {
            foreach (ulong guid in _dirty.Keys)
            {
                if (_dirty.TryRemove(guid, out AugustWardrobeState? state))
                {
                    WriteOne(guid, state);
                }
            }
        }
        finally
        {
            _writerGate.Release();
        }
    }

    private void WriteOne(ulong characterGuid, AugustWardrobeState state)
    {
        string file = Path.Combine(_root!, FileNameFor(characterGuid));
        string temporary = file + ".tmp";
        try
        {
            var payload = new WardrobeFile(
                characterGuid,
                DateTime.UtcNow,
                [
                    .. state.Snapshot().Select(entry => new WardrobeSelection(
                        entry.CategoryPrototypeId,
                        entry.AccountItemId,
                        entry.RewardItemId)),
                ]);

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
            using (var stream = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.None))
            {
                stream.Write(bytes);
                // The barrier that makes the rename below meaningful. It is why this runs here and
                // not on the thread that answered the click.
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, file, overwrite: true);
            Saves++;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or NotSupportedException)
        {
            _log($"wardrobe store: SAVE FAILED for character {characterGuid} "
                + $"({exception.Message}). The previous file on disk is intact; this change lives "
                + "only in memory.");
        }
    }

    /// <summary>
    /// A <c>.tmp</c> left behind means a crash between the write and the rename. The real file is
    /// intact by construction - the rename never started - so the <c>.tmp</c> is scrap. It is
    /// still a diagnostic, and <b>a diagnostic is never deleted, it is renamed aside</b> (project
    /// law 9; the owner has lost irreplaceable captures to a delete).
    /// </summary>
    private void RenameOrphanTemporaries()
    {
        string[] orphans;
        try
        {
            orphans = Directory.GetFiles(_root!, "*.tmp");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log($"wardrobe store: cannot scan \"{_root}\" for orphan temporaries "
                + $"({exception.Message})");
            return;
        }

        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        foreach (string orphan in orphans)
        {
            try
            {
                File.Move(orphan, $"{orphan}.orphan-{stamp}");
                OrphanTemporaries++;
                _log($"wardrobe store: orphan temporary {Path.GetFileName(orphan)} renamed aside as "
                    + $"{Path.GetFileName(orphan)}.orphan-{stamp} (never deleted - project law 9)");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log($"wardrobe store: orphan temporary {orphan}: {exception.Message}");
            }
        }
    }

    /// <summary>One saved wardrobe. The guid is inside the file as well as in its name.</summary>
    internal sealed record WardrobeFile(
        ulong CharacterGuid,
        DateTime SavedUtc,
        List<WardrobeSelection> Selections);

    /// <summary>
    /// One selection. <see cref="RewardItemId"/> is redundant with <see cref="AccountItemId"/> for
    /// the restore - <c>TryResolveClicked</c> accepts either - and is written so the file can be
    /// read by a human and cross-checked against the catalogue.
    /// </summary>
    internal sealed record WardrobeSelection(
        uint CategoryPrototypeId,
        uint AccountItemId,
        uint RewardItemId)
    {
        /// <summary>What the client would have clicked to make this selection.</summary>
        [JsonIgnore]
        public uint ClickedId => AccountItemId != 0 ? AccountItemId : RewardItemId;
    }
}
