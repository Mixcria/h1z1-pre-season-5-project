using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cranberry.Login;

/// <summary>
/// Thread-safe roster shared by login connections. Optionally persisted as one JSON file
/// (<see cref="Load"/>): every create/delete rewrites it, so characters survive host restarts.
/// Account persistence is a later boundary; this store owns only ids, snapshots, and validation
/// of what Cranberry has actually advertised.
/// </summary>
public sealed class CharacterRosterStore
{
    private readonly object _gate = new();
    private readonly List<CharacterEntry> _characters = [];
    private readonly string? _path;
    private long _nextEntityKey = 0x1000;

    public CharacterRosterStore()
    {
    }

    private CharacterRosterStore(string path)
    {
        _path = path;
    }

    /// <summary>Path of the JSON file this roster is persisted to, if any.</summary>
    public string? Path => _path;

    /// <summary>
    /// Opens (or creates on first save) the roster file at <paramref name="path"/>. A missing or
    /// unreadable file yields an empty roster; the caller decides whether to seed it.
    /// </summary>
    public static CharacterRosterStore Load(string path)
    {
        var store = new CharacterRosterStore(path);
        RenameOrphanTemporary(path);
        if (!File.Exists(path))
        {
            return store;
        }

        RosterFile? file = JsonSerializer.Deserialize<RosterFile>(File.ReadAllText(path), JsonOptions);
        if (file is null)
        {
            return store;
        }

        store._nextEntityKey = file.NextEntityKey;
        foreach (RosterCharacter row in file.Characters)
        {
            store._characters.Add(new CharacterEntry(
                EntityKey: row.EntityKey,
                ServerId: row.ServerId,
                Field3: row.Field3,
                Status: row.Status,
                Payload: Convert.FromBase64String(row.Payload),
                Name: row.Name,
                Gender: row.Gender));
        }

        return store;
    }

    public CharacterEntry Create(ulong serverId, CharacterCreatePayload payload)
    {
        lock (_gate)
        {
            return CreateLocked(serverId, payload);
        }
    }

    /// <summary>
    /// Creates a character only if no existing roster row has the same name, ignoring case. The
    /// check and insert share one lock so two simultaneous create requests cannot both win.
    /// </summary>
    public bool TryCreateUnique(
        ulong serverId,
        CharacterCreatePayload payload,
        out CharacterEntry character)
    {
        lock (_gate)
        {
            if (_characters.Any(existing =>
                string.Equals(existing.Name, payload.Name, StringComparison.OrdinalIgnoreCase)))
            {
                character = null!;
                return false;
            }

            character = CreateLocked(serverId, payload);
            return true;
        }
    }

    public bool ContainsName(string name)
    {
        lock (_gate)
        {
            return _characters.Any(character =>
                string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool TryGet(ulong entityKey, ulong serverId, out CharacterEntry character)
    {
        lock (_gate)
        {
            CharacterEntry? found = _characters.FirstOrDefault(entry =>
                entry.EntityKey == entityKey && entry.ServerId == serverId);
            character = found!;
            return found is not null;
        }
    }

    /// <summary>Removes the character with <paramref name="entityKey"/>; false when no such character exists.</summary>
    public bool Remove(ulong entityKey)
    {
        lock (_gate)
        {
            bool removed = _characters.RemoveAll(character => character.EntityKey == entityKey) > 0;
            if (removed)
            {
                SaveLocked();
            }

            return removed;
        }
    }

    public CharacterEntry[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _characters];
        }
    }

    public bool ContainsAvailable(ulong entityKey, ulong serverId)
    {
        lock (_gate)
        {
            return _characters.Any(character =>
                character.EntityKey == entityKey
                && character.ServerId == serverId
                && character.Status == CharacterEntry.StatusAvailable);
        }
    }

    /// <summary>
    /// A <c>roster.json.tmp</c> found at open means a crash between the write and the rename. The
    /// real roster is intact by construction, so the temporary is scrap - but <b>a diagnostic is
    /// never deleted, it is renamed aside</b> (project law 9, docs/80 edit 10). Never throws: a
    /// failure here must not cost a boot.
    /// </summary>
    private static void RenameOrphanTemporary(string path)
    {
        string temporary = path + ".tmp";
        try
        {
            if (File.Exists(temporary))
            {
                File.Move(
                    temporary,
                    $"{temporary}.orphan-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    overwrite: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            // Nothing to do and nothing to lose: the roster itself is unaffected either way.
        }
    }

    private void SaveLocked()
    {
        if (_path is null)
        {
            return;
        }

        var file = new RosterFile(
            Interlocked.Read(ref _nextEntityKey),
            _characters.Select(c => new RosterCharacter(c.EntityKey, c.ServerId, c.Field3, c.Status, Convert.ToBase64String(c.Payload), c.Name, c.Gender)).ToList());
        string? directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // docs/80 edit 10 (D53). This used to be WriteAllText + Move, which is atomic against a
        // torn read but NOT against a power cut: the bytes can still be in the OS cache when the
        // rename lands, and the roster is the one thing in this tree whose loss costs the owner his
        // characters. The barrier before the rename is what the owner own AccountStore does, and it
        // is the difference between losing the last change and losing the file.
        string temporary = _path + ".tmp";
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions);
        using (var stream = new FileStream(
            temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.None))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    private CharacterEntry CreateLocked(ulong serverId, CharacterCreatePayload payload)
    {
        // August's CreateNewOrder checks the character GUID's low nibble is 1
        // (native 141214e40 and 141435ee0). Incrementing by one only made the
        // first character compatible. Keep the persisted value as a high-water
        // mark, including legacy rows whose GUIDs have a different low nibble.
        ulong highWater = Math.Max(0x1000UL, checked((ulong)_nextEntityKey));
        foreach (CharacterEntry existing in _characters)
        {
            highWater = Math.Max(highWater, existing.EntityKey);
        }
        ulong entityKey = checked((highWater & ~0xFUL) + 1);
        if (entityKey <= highWater)
        {
            entityKey = checked(entityKey + 0x10);
        }
        // The on-disk high-water mark is signed. Exhaustion must fail before
        // changing any roster state instead of wrapping and reusing an ID.
        long nextEntityKey = checked((long)entityKey);
        var character = new CharacterEntry(
            EntityKey: entityKey,
            ServerId: serverId,
            Field3: 0,
            Status: CharacterEntry.StatusAvailable,
            Payload: CharacterSelectionPayload.FromCreate(payload).ToArray(),
            Name: payload.Name,
            Gender: payload.Gender);
        _nextEntityKey = nextEntityKey;
        _characters.Add(character);
        SaveLocked();
        return character;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record RosterFile(long NextEntityKey, List<RosterCharacter> Characters);

    private sealed record RosterCharacter(
        ulong EntityKey,
        ulong ServerId,
        ulong Field3,
        uint Status,
        string Payload,
        string Name,
        uint Gender);
}
