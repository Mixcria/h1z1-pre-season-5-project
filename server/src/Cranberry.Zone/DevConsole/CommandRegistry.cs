namespace Cranberry.Zone.DevConsole;

/// <summary>
/// Thrown when a command may not be registered: the client already owns the name, two Cranberry
/// names hash the same, or the name is not something the client can carry.
/// </summary>
public sealed class ConsoleCommandRefusedException : InvalidOperationException
{
    /// <summary>Creates the exception with a message that names the offender and the reason.</summary>
    public ConsoleCommandRefusedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Every command the console knows, keyed the way the client keys them: by
/// <see cref="CommandHash"/> of the name.
/// <para>
/// <b>The registry is also the collision oracle.</b> The client refuses a pushed name that is
/// already in its own registry - <c>FUN_141280280</c> writes
/// <c>Server sent %s (%s) that conflicts with a local command</c> to
/// <c>Client\Logs\AdminCommands.log</c>, registers nothing, and then emits no packet when that name
/// is typed. Because the client's lookup <c>FUN_141e9d680</c> matches by hash and returns without
/// comparing the name whenever the entry's name pointer is null (every one of its 975 static
/// CVars), the test has to be a hash-set membership test, not a name test. <see cref="Register"/>
/// therefore refuses at start-up rather than letting a verb become a silently un-typeable menu row
/// (design §1.4, §4.2).
/// </para>
/// <para>
/// The runtime backstop for what the census cannot see - the chat-word table, names another server
/// aliased earlier in this client process - is the <c>AdminCommands.log</c> reader, whose findings
/// arrive here through <see cref="MarkRefused"/>: the name leaves <see cref="Names"/> so the next
/// burst does not push it, and its menu leaf draws <c>(refused)</c>.
/// </para>
/// </summary>
public sealed class CommandRegistry
{
    /// <summary>The longest name the client's console input line is worth spending on.</summary>
    public const int MaximumNameLength = 16;

    private readonly List<ConsoleCommand> _commands = [];
    private readonly Dictionary<uint, ConsoleCommand> _byHash = [];
    private readonly Dictionary<string, ConsoleCommand> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _nameByHash = [];
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every command, in registration order - which is the order <c>/help</c> lists them.</summary>
    public IReadOnlyList<ConsoleCommand> Commands => _commands;

    /// <summary>Names the client refused last run, from <c>AdminCommands.log</c>.</summary>
    public IReadOnlyCollection<string> Refused => _refused;

    /// <summary>
    /// The names to push with <c>Command.AddWorldCommand</c>: every name and alias of every
    /// registered command, minus anything the client has refused. In registration order, because
    /// the burst is asserted in that order by the integration test.
    /// </summary>
    public IReadOnlyList<string> Names =>
    [
        .. _commands
            .Where(c => c.Registered)
            .SelectMany(c => c.AllNames)
            .Where(n => !_refused.Contains(n)),
    ];

    /// <summary>Every name and alias, refused or not, registered or not - what <c>/help</c> counts.</summary>
    public IReadOnlyList<string> AllNames => [.. _commands.SelectMany(c => c.AllNames)];

    /// <summary>Adds a command. Throws rather than let a name silently fail to reach the client.</summary>
    public CommandRegistry Register(ConsoleCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        foreach (string name in command.AllNames)
        {
            Validate(command, name);
        }

        foreach (string name in command.AllNames)
        {
            uint hash = CommandHash.Compute(name);
            _byHash[hash] = command;
            _nameByHash[hash] = name;
            _byName[name] = command;
        }

        _commands.Add(command);
        return this;
    }

    /// <summary>Adds several commands in order.</summary>
    public CommandRegistry RegisterAll(IEnumerable<ConsoleCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        foreach (ConsoleCommand command in commands)
        {
            Register(command);
        }

        return this;
    }

    /// <summary>Finds the command the client's hash names. This is the whole inbound lookup.</summary>
    public bool TryResolve(uint hash, out ConsoleCommand command) => _byHash.TryGetValue(hash, out command!);

    /// <summary>Finds a command by one of its names.</summary>
    public ConsoleCommand? ByName(string? name) =>
        name is not null && _byName.TryGetValue(name, out ConsoleCommand? command) ? command : null;

    /// <summary>The command this hash names, or null.</summary>
    public ConsoleCommand? ByHash(uint hash) => _byHash.GetValueOrDefault(hash);

    /// <summary>
    /// Which of a command's names the caller actually typed. The client sends only a hash, so this
    /// is the only way to tell <c>/d</c> from <c>/m</c> when both reach the same command.
    /// </summary>
    public string? NameOf(uint hash) => _nameByHash.GetValueOrDefault(hash);

    /// <summary>Commands grouped for <c>/help</c>, groups in first-seen order.</summary>
    public IReadOnlyList<IGrouping<string, ConsoleCommand>> ByGroup() =>
    [
        .. _commands.GroupBy(c => c.Group, StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>
    /// Records that the client refused a name (design §4.3). The name leaves <see cref="Names"/>,
    /// so the next burst does not push it again, and every menu leaf bound to it draws
    /// <c>(refused)</c>.
    /// </summary>
    public bool MarkRefused(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _refused.Add(name.Trim());
    }

    /// <summary>True when the client refused this exact name.</summary>
    public bool IsNameRefused(string? name) => name is not null && _refused.Contains(name);

    /// <summary>True when the client refused this command's primary name.</summary>
    public bool IsRefused(ConsoleCommand? command) => command is not null && _refused.Contains(command.Name);

    /// <summary>How many commands are LATER rows.</summary>
    public int NotYetCount => _commands.Count(c => c.NotYet is not null);

    /// <summary>The registry half of the boot banner (design §4.7).</summary>
    public string Summary =>
        $"{_commands.Count} commands ({_commands.Count - NotYetCount} live, {NotYetCount} not-yet), "
        + $"{Names.Count} names, 0 collide with the client registry ({ClientRegistry1148.Entries.Count} hashes), "
        + $"{_refused.Count} refused last run";

    private void Validate(ConsoleCommand command, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ConsoleCommandRefusedException(
                $"console command '{command.Name}' has an empty name or alias");
        }

        if (name.Length > MaximumNameLength)
        {
            throw new ConsoleCommandRefusedException(
                $"console name '{name}' is {name.Length} characters; the limit is {MaximumNameLength}");
        }

        foreach (char c in name)
        {
            if (c is < ' ' or > '~' || c == '/' || c == ' ' || char.IsUpper(c))
            {
                throw new ConsoleCommandRefusedException(
                    $"console name '{name}' must be printable lower-case ASCII with no slash or space");
            }
        }

        uint hash = CommandHash.Compute(name);

        // A command that is never pushed with AddWorldCommand cannot collide with anything: the
        // client only refuses names a server tries to register. `help` is the one such command -
        // it is reached through the client's own HELP catch-all hash (design §1.4).
        if (command.Registered && ClientRegistry1148.Contains(hash))
        {
            throw new ConsoleCommandRefusedException(
                $"console name '{name}' (0x{hash:x8}) collides with the client's own registry: "
                + $"{ClientRegistry1148.Describe(hash)} - the client would log it to AdminCommands.log "
                + "and never send the command; rename it (design §1.4)");
        }

        if (_byHash.TryGetValue(hash, out ConsoleCommand? existing))
        {
            throw new ConsoleCommandRefusedException(
                $"console name '{name}' (0x{hash:x8}) hashes the same as '{existing.Name}'; "
                + "one of the two would be unreachable");
        }

        if (_byName.ContainsKey(name))
        {
            throw new ConsoleCommandRefusedException($"console name '{name}' is registered twice");
        }
    }
}
