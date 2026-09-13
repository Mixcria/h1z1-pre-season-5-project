using System.Diagnostics.CodeAnalysis;

namespace Cranberry.Zone.World.Doors;

/// <summary>
/// One door's identity <b>inside a match</b> — the guid the client echoes back and the transient id
/// the <c>0xda</c> and the <c>ea 04</c> name it by.
/// <para>
/// Minted once per door per match by <see cref="SharedMatchDoors"/> and handed to every session that
/// streams that door, which is the whole point: before docs/114 §3 each connection minted its own
/// ids from <see cref="MatchDoors.DefaultWorldGuidBase"/>, so guid <c>0x4400…01</c> named a different
/// door in every session and no <c>0f 0a</c> could ever be addressed to two clients at once.
/// </para>
/// </summary>
/// <param name="WorldGuid">The guid the client names back in <c>Command.InteractRequest</c>.</param>
/// <param name="TransientId">Match key for <c>0xda</c> and owner of the <c>ea 04</c> component.</param>
public readonly record struct DoorIdentity(ulong WorldGuid, uint TransientId);

/// <summary>
/// The door state one match's sessions share: <b>which door is which, and which ones are open</b>.
///
/// <para>
/// <b>Why it exists.</b> AUDIT-doors §6 measured the defect on the wire: two sessions in the same
/// match re-used guids <c>0x4400…01</c>–<c>04</c> for <em>different</em> doors 3 km apart
/// (<c>captures/wire-20260903-174857.txt:21006</c>), and <c>MatchDoors</c> was <c>state.Doors</c> —
/// per connection — so <c>TryToggleDoor</c> answered with a single-recipient <c>SendTunnel</c>. A
/// door opened by one player stayed shut for everyone else, and the second player's <c>[F]</c> then
/// closed a door that was, for him, already closed.
/// </para>
///
/// <para>
/// <b>Shape copied from <see cref="SharedMatchGas"/> deliberately</b> (lane 3C, docs/109 §5): one
/// instance per <c>ZoneService</c>, ref-counted by <see cref="Join"/>/<see cref="Leave"/>, forgotten
/// when the last member goes so the next match starts with every door shut. It shares exactly two
/// things and the honest list matters more than the fix:
/// <list type="number">
/// <item><b>Identity.</b> <see cref="Identify"/> mints one <see cref="DoorIdentity"/> per dataset
/// door and returns the same one for ever, so the same door has the same guid in every session and
/// one <c>0f 0a</c> is addressed to all of them.</item>
/// <item><b>The open bit</b>, keyed by ZONE instance id — not by guid and not by dataset index — so
/// it survives a stream-out/stream-in cycle and a regeneration of <c>z2-doors.bin</c> that reorders
/// it, exactly as <c>MatchDoors</c>' own private set did.</item>
/// </list>
/// The open bit, chosen swing direction and last accepted toggle now share one
/// <c>DoorMotionState</c>. This keeps viewers synchronized and prevents another player's
/// request from reversing an unfinished animation. Stream membership remains per viewer.
/// </para>
///
/// <para>
/// <b>Members register a sink</b> so the fan-out is decidable without a socket — the same reason
/// <see cref="IPeerSink"/> exists (docs/22 §4.9). <see cref="CollectListeners"/> answers "who else
/// has this door on their client", which is the only question the <c>0f 0a</c> broadcast asks.
/// </para>
///
/// <para>Not thread-safe: the listener thread owns it, like everything else on this path.</para>
/// </summary>
public sealed class SharedMatchDoors
{
    /// <summary>
    /// Where a match's door guids start. Deliberately the same base <c>MatchDoors</c> has always
    /// used, so a single-player match's wire is byte-identical to what it was before this type
    /// existed — the switch is provably a no-op with one player.
    /// </summary>
    public const ulong DefaultWorldGuidBase = MatchDoors.DefaultWorldGuidBase;

    /// <summary>Transient-id base; see <see cref="MatchDoors.DefaultTransientIdBase"/>.</summary>
    public const uint DefaultTransientIdBase = MatchDoors.DefaultTransientIdBase;

    private readonly Dictionary<int, DoorIdentity> _identities = [];
    private readonly HashSet<uint> _open = [];
    private readonly Dictionary<uint, DoorMotionState> _motion = [];
    private readonly List<Member> _members = [];

    private ulong _nextWorldGuid = DefaultWorldGuidBase;
    private uint _nextTransientId = DefaultTransientIdBase;

    private readonly record struct Member(MatchDoors Doors, IPeerSink Sink);

    /// <summary>True while at least one session holds this match's doors.</summary>
    public bool Active => Members > 0;

    /// <summary>How many sessions currently hold it.</summary>
    public int Members { get; private set; }

    /// <summary>
    /// How many sessions have adopted a door set that was already running — the number a two-client
    /// run should see go to 1, and the proof the second player's doors are the first player's.
    /// </summary>
    public long Joins { get; private set; }

    /// <summary>How many distinct doors this match has minted an identity for.</summary>
    public int IdentityCount => _identities.Count;

    /// <summary>How many doors this match has left open, spawned or not.</summary>
    public int OpenCount => _open.Count;

    /// <summary><c>0f 0a</c> packets fanned out to a session that did not press the key.</summary>
    public long Fanouts { get; private set; }

    /// <summary>
    /// Adds one session's <see cref="MatchDoors"/> to the match, or re-registers it. Returns true
    /// when a set was already running, i.e. this session is the second or later — the same answer
    /// <see cref="SharedMatchGas.Join"/> gives, and for the same reason: it is the one line the log
    /// needs to prove two players are in one match rather than two.
    /// </summary>
    public bool Join(MatchDoors doors, IPeerSink sink)
    {
        ArgumentNullException.ThrowIfNull(doors);
        ArgumentNullException.ThrowIfNull(sink);

        bool joined = Active;
        for (int index = 0; index < _members.Count; index++)
        {
            if (ReferenceEquals(_members[index].Doors, doors))
            {
                // Re-registration: a session that re-enters a world builds a new sink but keeps its
                // MatchDoors. Replace rather than append, the rule SessionRegistry.Register follows.
                _members[index] = new Member(doors, sink);
                return joined;
            }
        }

        _members.Add(new Member(doors, sink));
        Members++;
        if (joined)
        {
            Joins++;
        }

        return joined;
    }

    /// <summary>
    /// Drops one member. The identities and the open bits are forgotten when the last one goes, so
    /// the next match mints a fresh guid space with every door shut — and a host that never has two
    /// players at once behaves exactly as it did before this type existed.
    /// </summary>
    public void Leave(MatchDoors doors)
    {
        ArgumentNullException.ThrowIfNull(doors);

        for (int index = 0; index < _members.Count; index++)
        {
            if (!ReferenceEquals(_members[index].Doors, doors))
            {
                continue;
            }

            _members.RemoveAt(index);
            Members--;
            break;
        }

        if (Members <= 0)
        {
            Clear();
        }
    }

    /// <summary>Forgets the match outright — the host's own reset, not a member leaving.</summary>
    public void Clear()
    {
        _members.Clear();
        _identities.Clear();
        _open.Clear();
        _motion.Clear();
        Members = 0;
        _nextWorldGuid = DefaultWorldGuidBase;
        _nextTransientId = DefaultTransientIdBase;
    }

    /// <summary>
    /// The ids this match knows <paramref name="doorIndex"/> by, minting them on first ask.
    /// Idempotent for the life of the match, which is what makes one <c>0f 0a</c> addressable to
    /// every session — and what lets a door be streamed out of one session and back in without
    /// changing the guid the other session still holds.
    /// </summary>
    public DoorIdentity Identify(int doorIndex)
    {
        if (_identities.TryGetValue(doorIndex, out DoorIdentity identity))
        {
            return identity;
        }

        identity = new DoorIdentity(_nextWorldGuid++, _nextTransientId++);
        _identities.Add(doorIndex, identity);
        return identity;
    }

    /// <summary>The ids already minted for that dataset door, if any.</summary>
    public bool TryGetIdentity(int doorIndex, out DoorIdentity identity) =>
        _identities.TryGetValue(doorIndex, out identity);

    /// <summary>Whether the door with that ZONE instance id is open, spawned or not.</summary>
    public bool IsOpen(uint instanceId) => _open.Contains(instanceId);

    /// <summary>Records a door's open bit. Returns true when the bit actually moved.</summary>
    public bool SetOpen(uint instanceId, bool isOpen)
    {
        MotionFor(instanceId).IsOpen = isOpen;
        return isOpen ? _open.Add(instanceId) : _open.Remove(instanceId);
    }

    internal DoorMotionState MotionFor(uint instanceId)
    {
        if (!_motion.TryGetValue(instanceId, out var motion))
            _motion.Add(instanceId, motion = new DoorMotionState { IsOpen = _open.Contains(instanceId) });
        return motion;
    }

    /// <summary>
    /// Every OTHER member that currently has <paramref name="worldGuid"/> spawned on its client,
    /// with the sink to write to. <paramref name="origin"/> — the presser — is skipped, because
    /// <c>ZoneService</c> answers it separately and with a prompt as well.
    /// <para>
    /// A closed link is skipped and the member is swept, the same lazy rule
    /// <see cref="SessionRegistry.SweepClosed"/> uses; there is deliberately no hook on the
    /// link-close path, because a door fan-out that depended on one would go silently wrong the
    /// first time a session died without running it.
    /// </para>
    /// <paramref name="into"/> is cleared first and is expected to be a caller-owned buffer, so a
    /// toggle allocates nothing.
    /// </summary>
    public void CollectListeners(MatchDoors origin, ulong worldGuid, ICollection<IPeerSink> into)
    {
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();
        for (int index = _members.Count - 1; index >= 0; index--)
        {
            Member member = _members[index];
            if (!member.Sink.IsOpen)
            {
                _members.RemoveAt(index);
                Members--;
                continue;
            }

            if (ReferenceEquals(member.Doors, origin) || !member.Doors.TryGet(worldGuid, out _))
            {
                continue;
            }

            into.Add(member.Sink);
        }
    }

    /// <summary>Counts a fanned-out <c>0f 0a</c>, for the log line.</summary>
    public void NoteFanout(int recipients)
    {
        if (recipients > 0)
        {
            Fanouts += recipients;
        }
    }

    /// <summary>The door instance that member holds for <paramref name="worldGuid"/>, if any.</summary>
    public bool TryFindAnywhere(ulong worldGuid, [NotNullWhen(true)] out DoorInstance? instance)
    {
        foreach (Member member in _members)
        {
            if (member.Doors.TryGet(worldGuid, out instance))
            {
                return true;
            }
        }

        instance = null;
        return false;
    }

    public override string ToString() =>
        Active
            ? $"shared match doors: {IdentityCount} identity(s) from guid "
                + $"{DefaultWorldGuidBase:x16}, {OpenCount} open, {Members} member(s), "
                + $"{Joins} join(s), {Fanouts} fanned-out 0f 0a"
            : "no shared match doors";
}
