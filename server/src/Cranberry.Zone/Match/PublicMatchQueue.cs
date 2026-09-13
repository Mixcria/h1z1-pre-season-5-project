using System.Security.Cryptography;

namespace Cranberry.Zone.Match;

public enum PublicMatchPhase
{
    QUEUE_OPEN, MATCH_ALLOCATING, PRE_GAME, ROSTER_FROZEN, STARTING, ACTIVE, ENDING, RESULTS, COMPLETE
}

/// <summary>Host policy, not native packet fields. The production host always supplies these options.</summary>
public sealed record PublicQueueOptions
{
    public int WaitMs { get; init; } = 180_000;
    public int MaxPlayers { get; init; } = 150;
    public int MinPlayers { get; init; } = 2;
    /// <summary>Local practice may start without an opposing team. Public hosts keep this off.</summary>
    public bool AllowSinglePlayer { get; init; }
    public int MaxAllocatedMatches { get; init; } = 2;
    public int AcceptTimeoutMs { get; init; } = 60_000;
    public int LoadTimeoutMs { get; init; } = 180_000;

    // Apply after saved settings, including preview.1's multiplayer minimum and three-minute wait.
    public PublicQueueOptions ForLocalPlay() => this with
    { AllowSinglePlayer = true, MinPlayers = 1, WaitMs = 5_000 };

    public static PublicQueueOptions FromEnvironment(Func<string, string?> read)
    {
        int maximum = Read(read, "CRANBERRY_QUEUE_MAX_PLAYERS", 150, 6, 150);
        return new()
        {
        WaitMs = Read(read, "CRANBERRY_QUEUE_WAIT_MS", 180_000, 0, 900_000),
        MaxPlayers = maximum,
        MinPlayers = Read(read, "CRANBERRY_QUEUE_MIN_PLAYERS", 2, 2, maximum),
        MaxAllocatedMatches = Read(read, "CRANBERRY_QUEUE_MAX_MATCHES", 2, 1, 32),
        AcceptTimeoutMs = Read(read, "CRANBERRY_QUEUE_ACCEPT_TIMEOUT_MS", 60_000, 10_000, 180_000),
        LoadTimeoutMs = Read(read, "CRANBERRY_QUEUE_LOAD_TIMEOUT_MS", 180_000, 30_000, 600_000)
        };
    }

    private static int Read(Func<string, string?> read, string key, int fallback, int min, int max) =>
        int.TryParse(read(key), out int value) ? Math.Clamp(value, min, max) : fallback;
}

public sealed record PublicMatchSnapshot(ulong MatchId, uint WorldId, MatchMode Mode,
    PublicMatchPhase Phase, long OpenedAtMs, IReadOnlyList<ulong> Roster, int PresentPlayers,
    long? CountdownDeadlineMs = null, int ReadyPlayers = 0);

/// <summary>
/// Gateway-thread-owned public admission. Groups are reserved atomically, including their seats;
/// the frozen roster never changes. No transport state or protocol bytes live in this allocator.
/// </summary>
public sealed class PublicMatchQueue(PublicQueueOptions options)
{
    private sealed class Round(ulong id, uint world, MatchMode mode, long opened)
    {
        public ulong Id { get; } = id;
        public uint World { get; } = world;
        public MatchMode Mode { get; } = mode;
        public long Opened { get; } = opened;
        public PublicMatchPhase Phase { get; set; }
        public List<ulong[]> Groups { get; } = [];
        public HashSet<ulong> Present { get; } = [];
        public HashSet<ulong> Ready { get; } = [];
        public long? CountdownDeadline { get; set; }
        public IReadOnlyList<ulong>? FrozenRoster { get; set; }
    }

    private readonly Dictionary<uint, Round> _open = [];
    private readonly Dictionary<ulong, Round> _rounds = [];
    private readonly Dictionary<ulong, Round> _members = [];
    public event Action<ulong, PublicMatchPhase>? Transitioned;
    public bool HasWaitingPlayers => _rounds.Values.Any(r => r.Present.Count != 0 && r.Phase < PublicMatchPhase.ACTIVE);
    public int AllocatedMatches => _rounds.Values.Count(r => r.Phase is > PublicMatchPhase.QUEUE_OPEN and < PublicMatchPhase.COMPLETE);
    public IReadOnlyList<PublicMatchSnapshot> Snapshots => _rounds.Values.Select(Snapshot).ToArray();

    public bool TryReserve(uint world, MatchMode mode, IReadOnlyList<ulong> group, long now, out ulong matchId)
    {
        matchId = 0;
        int teamSize = mode switch { MatchMode.Solo => 1, MatchMode.Duos => 2, MatchMode.Fives => 5, _ => 0 };
        if (world == 0 || teamSize == 0 || group.Count == 0 || group.Count > teamSize
            || group.Count > options.MaxPlayers || group.Any(g => g == 0) || group.Distinct().Count() != group.Count) return false;
        if (_members.TryGetValue(group[0], out var existing))
        {
            // Preparing the remaining members of the same atomic reservation is idempotent.
            if (existing.World != world || existing.Mode != mode || existing.FrozenRoster is not null
                || !existing.Groups.Any(g => g.SequenceEqual(group))) return false;
            matchId = existing.Id;
            return true;
        }
        if (group.Any(_members.ContainsKey)) return false;
        if (_open.TryGetValue(world, out var round) && round.Mode != mode) return false;
        if (round is not null && round.Present.Count + group.Count > options.MaxPlayers)
        {
            // Do not split the party or overbook the last seats. A launchable cohort can close early.
            if (!TryFreeze(round, now)) return false;
            round = null;
        }
        if (round is null)
        {
            ulong id;
            do { id = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & long.MaxValue; }
            while (id == 0 || _rounds.ContainsKey(id));
            round = new(id, world, mode, now);
            _rounds.Add(id, round);
            _open.Add(world, round);
            Transitioned?.Invoke(id, PublicMatchPhase.QUEUE_OPEN);
        }
        ulong[] reserved = group.ToArray();
        round.Groups.Add(reserved);
        foreach (ulong member in reserved) { round.Present.Add(member); _members.Add(member, round); }
        matchId = round.Id;
        return true;
    }

    public void Poll(long now)
    {
        // Allocate a physical lobby on first arrival. The gameplay countdown belongs to ready
        // players in that lobby, not the time spent alone in a menu or loading screen.
        foreach (var round in _open.Values.OrderBy(r => r.Opened).ToArray())
        {
            if (round.Phase == PublicMatchPhase.QUEUE_OPEN && AllocatedMatches < options.MaxAllocatedMatches)
                Advance(round.Id, PublicMatchPhase.MATCH_ALLOCATING);
            if (round.CountdownDeadline is long deadline
                && (now >= deadline || round.Present.Count >= options.MaxPlayers)) TryFreeze(round, now);
        }
    }

    public void SetReadyPlayers(ulong matchId, IReadOnlyCollection<ulong> players, long now)
    {
        if (!_rounds.TryGetValue(matchId, out var round) || round.FrozenRoster is not null
            || round.Phase == PublicMatchPhase.QUEUE_OPEN) return;
        round.Ready.Clear();
        round.Ready.UnionWith(players.Where(round.Present.Contains));
        if (round.Ready.Count != 0) Advance(matchId, PublicMatchPhase.PRE_GAME);
        if (round.Ready.Count < MinimumPlayers(round)) round.CountdownDeadline = null;
        else round.CountdownDeadline ??= now + options.WaitMs;
    }

    private int MinimumPlayers(Round round) => options.AllowSinglePlayer ? Math.Max(1, options.MinPlayers) : Math.Max(options.MinPlayers,
        (round.Mode switch { MatchMode.Duos => 2, MatchMode.Fives => 5, _ => 1 }) + 1);

    /// <summary>Explicit host override; the caller must enforce owner authorization and readiness.</summary>
    public bool TryForceFreeze(ulong matchId, long now)
    {
        if (!_rounds.TryGetValue(matchId, out var round) || round.Phase != PublicMatchPhase.PRE_GAME
            || round.FrozenRoster is not null || round.Ready.Count == 0) return false;
        FreezeRoster(round, now);
        return true;
    }

    private bool TryFreeze(Round round, long now)
    {
        // More than one full team's worth guarantees opponents even with auto-filled teams.
        if (round.Ready.Count < MinimumPlayers(round) || round.Phase == PublicMatchPhase.QUEUE_OPEN) return false;
        FreezeRoster(round, now);
        return true;
    }

    private void FreezeRoster(Round round, long now)
    {
        round.FrozenRoster = Array.AsReadOnly(round.Groups.SelectMany(g => g).ToArray());
        round.CountdownDeadline = Math.Min(round.CountdownDeadline ?? now, now);
        _open.Remove(round.World); // Next reservation opens the next generation immediately.
        Advance(round.Id, PublicMatchPhase.ROSTER_FROZEN);
    }

    public bool CanAccept(ulong player, ulong matchId) => _members.TryGetValue(player, out var round)
        && round.Id == matchId && round.Phase is >= PublicMatchPhase.MATCH_ALLOCATING and < PublicMatchPhase.ENDING;

    public IReadOnlyList<ulong> GroupFor(ulong player, ulong matchId) =>
        _members.TryGetValue(player, out var round) && round.Id == matchId
            ? Array.AsReadOnly(round.Groups.Single(g => g.Contains(player))) : Array.Empty<ulong>();

    public int Position(ulong player) => _members.TryGetValue(player, out var round)
        ? Array.IndexOf(round.Groups.SelectMany(g => g).ToArray(), player) + 1 : 0;

    public void Advance(ulong matchId, PublicMatchPhase phase)
    {
        if (!_rounds.TryGetValue(matchId, out var round) || phase <= round.Phase) return;
        round.Phase = phase;
        Transitioned?.Invoke(matchId, phase);
    }

    public void Leave(ulong player)
    {
        if (!_members.Remove(player, out var round)) return;
        round.Present.Remove(player);
        round.Ready.Remove(player);
        if (round.FrozenRoster is null)
        {
            // Before freezing a party withdrawal releases its entire reservation.
            ulong[] group = round.Groups.Single(g => g.Contains(player));
            round.Groups.Remove(group);
            foreach (ulong member in group) { round.Present.Remove(member); round.Ready.Remove(member); _members.Remove(member); }
            if (round.Ready.Count < MinimumPlayers(round)) round.CountdownDeadline = null;
        }
        if (round.Present.Count != 0) return;
        if (_open.GetValueOrDefault(round.World) == round) _open.Remove(round.World);
        Advance(round.Id, PublicMatchPhase.COMPLETE);
        _rounds.Remove(round.Id); // No unbounded history; transition logs retain completed IDs.
    }

    private static PublicMatchSnapshot Snapshot(Round round) => new(round.Id, round.World, round.Mode,
        round.Phase, round.Opened, round.FrozenRoster ?? Array.AsReadOnly(round.Groups.SelectMany(g => g).ToArray()),
        round.Present.Count, round.CountdownDeadline, round.Ready.Count);
}
