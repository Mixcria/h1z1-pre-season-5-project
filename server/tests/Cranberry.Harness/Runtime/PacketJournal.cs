namespace Cranberry.Harness.Runtime;

/// <summary>Which way a journalled packet went, from the harness's point of view.</summary>
public enum PacketDirection
{
    /// <summary>Harness (playing the client) to server.</summary>
    ToServer,

    /// <summary>Server to harness.</summary>
    FromServer,
}

/// <summary>One application message as the journal remembers it.</summary>
public sealed record JournalEntry(
    TimeSpan At,
    string Link,
    PacketDirection Direction,
    string Name,
    byte[] Bytes,
    string? Note = null)
{
    /// <summary>Hex of the first <paramref name="max"/> bytes, uppercase, with an ellipsis when cut.</summary>
    public string Hex(int max = 48) =>
        Bytes.Length <= max
            ? Convert.ToHexString(Bytes)
            : Convert.ToHexString(Bytes.AsSpan(0, max)) + $"… (+{Bytes.Length - max} B)";

    public string ToLine(int hexBytes = 48)
    {
        string arrow = Direction == PacketDirection.ToServer ? "c2s" : "s2c";
        string note = Note is null ? string.Empty : $"  ({Note})";
        return $"{HarnessClock.Format(At)} | {Link,-20} | {arrow} | {Bytes.Length,7} | {Name}{note}\n"
            + $"          |                      |     |         | {Hex(hexBytes)}";
    }
}

/// <summary>
/// A bounded ring of the most recent application messages both ways. It is the difference between
/// a failure that says "expected ClientIsReady" and one that shows what the server actually said
/// in the seconds before the client gave up — which is the whole reason this harness exists.
/// Thread-safe: the transport pump and the scenario runner both write to it.
/// </summary>
public sealed class PacketJournal(int capacity = 60)
{
    private readonly object _gate = new();
    private readonly Queue<JournalEntry> _entries = new();

    public int Capacity { get; } = Math.Max(4, capacity);

    public long TotalRecorded { get; private set; }

    public void Record(JournalEntry entry)
    {
        lock (_gate)
        {
            TotalRecorded++;
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    public IReadOnlyList<JournalEntry> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }

    /// <summary>The last <paramref name="count"/> entries, oldest first, formatted for a failure message.</summary>
    public string Tail(int count = 20, int hexBytes = 48)
    {
        IReadOnlyList<JournalEntry> all = Snapshot();
        int skip = Math.Max(0, all.Count - count);
        IEnumerable<JournalEntry> tail = all.Skip(skip);
        var lines = new List<string>
        {
            $"last {all.Count - skip} of {TotalRecorded} application message(s):",
            "     time | link                 | dir |  length | packet",
        };
        lines.AddRange(tail.Select(e => e.ToLine(hexBytes)));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The most recent entry from the server, or null when the server has said nothing.</summary>
    public JournalEntry? LastFromServer() =>
        Snapshot().LastOrDefault(e => e.Direction == PacketDirection.FromServer);
}
