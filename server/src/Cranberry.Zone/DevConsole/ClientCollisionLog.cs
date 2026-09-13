using System.Text.RegularExpressions;

namespace Cranberry.Zone.DevConsole;

/// <summary>
/// The runtime backstop for a name the client refuses: a read-only, mtime-cached reader over
/// <c>&lt;ClientLogsPath&gt;\AdminCommands.log</c>.
/// <para>
/// <b>Why it exists at all.</b> <see cref="CommandRegistry.Register"/> already refuses, at start-up,
/// any name whose hash is in the client's own 1,025-entry registry census
/// (<see cref="ClientRegistry1148"/>), and that test is exhaustive for everything the registry
/// holds when the process starts. It cannot see two things: the chat layer's own word table (the
/// <c>say groupsay squadsay</c> run at <c>0x1431fa113</c>, which <c>ConsoleWrapper.ProcessChatCommand</c>
/// consults before the alias does) and names another server aliased earlier in the same client
/// process, which live for the process's lifetime. Both show up the same way - the client's
/// <c>FUN_141280280</c> writes
/// <c>Server sent %s (%s) that conflicts with a local command</c> to this log, registers nothing,
/// and then emits no packet when the name is typed (design §1.4, §4.3).
/// </para>
/// <para>
/// <b>No timer and no background read.</b> The file is read once at boot and then only on the next
/// inbound <c>09 42</c>, and only when its modification time has moved. A poll would be the fourth
/// thing riding the <c>Later</c> chain in a session and would leak into every test pump
/// (refute-2 F4).
/// </para>
/// <para>
/// <b>A missing file is not an error.</b> The client writes this log only when something collides,
/// so its absence is the healthy case and its appearance is itself the signal (R5 §3 B).
/// </para>
/// </summary>
public sealed class ClientCollisionLog
{
    /// <summary>
    /// The name inside the parentheses of the client's own format string. The prefix is matched
    /// loosely - the first <c>%s</c> is <c>__sendworldcommand</c> today, but the tail
    /// (<c>that conflicts with a local command</c>) is the part that carries the meaning.
    /// </summary>
    private static readonly Regex ConflictLine = new(
        @"\(([^)]{1,64})\)\s+that conflicts with a local command",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly string _path;
    private readonly Action<string>? _warn;
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private long _lastLength = -1;
    private IReadOnlyList<string> _names = [];

    /// <summary>Opens a reader over one path. Nothing is read until <see cref="Read"/> is called.</summary>
    public ClientCollisionLog(string path, Action<string>? warn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _warn = warn;
    }

    /// <summary>The file this reader watches.</summary>
    public string Path => _path;

    /// <summary>True when the client has written the log at all - its existence is the signal.</summary>
    public bool Exists => File.Exists(_path);

    /// <summary>The last modification time this reader saw, or <see cref="DateTime.MinValue"/>.</summary>
    public DateTime LastWriteUtc => _lastWriteUtc;

    /// <summary>How many times the file was actually opened, so a test can pin the mtime cache.</summary>
    public int Reads { get; private set; }

    /// <summary>
    /// The names the client refused, newest read first. Re-reads only when the file's modification
    /// time or length has moved since the last call; otherwise it answers the cached list without
    /// touching the disk.
    /// </summary>
    public IReadOnlyList<string> Read()
    {
        FileInfo info;
        try
        {
            info = new FileInfo(_path);
            if (!info.Exists)
            {
                _names = [];
                return _names;
            }

            if (info.LastWriteTimeUtc == _lastWriteUtc && info.Length == _lastLength)
            {
                return _names;
            }

            _lastWriteUtc = info.LastWriteTimeUtc;
            _lastLength = info.Length;
            Reads++;
            _names = Parse(File.ReadLines(_path));
            return _names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The client owns this file and may hold it open. A log we cannot read must never take
            // a packet handler down; the start-up census is still the primary guard.
            _warn?.Invoke($"console: cannot read {_path} ({ex.GetType().Name}: {ex.Message})");
            return _names;
        }
    }

    /// <summary>
    /// The names named by a sequence of log lines, de-duplicated and in first-seen order. Public
    /// and static so the parse can be tested without a file.
    /// </summary>
    public static IReadOnlyList<string> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        List<string> names = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            System.Text.RegularExpressions.Match match = ConflictLine.Match(line);
            if (!match.Success)
            {
                continue;
            }

            string name = match.Groups[1].Value.Trim();
            if (name.Length > 0 && seen.Add(name))
            {
                names.Add(name);
            }
        }

        return names;
    }
}
