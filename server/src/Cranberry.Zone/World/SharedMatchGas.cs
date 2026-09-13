namespace Cranberry.Zone.World;

/// <summary>
/// The one piece of match state lane 3C shares between two connections: <b>which circle they are
/// both playing inside</b>.
///
/// <para>
/// <b>Why this is enough, and why it is only enough.</b> S1 §5.4 / D67 record the real problem —
/// match state today belongs to the connection, so two players in "one match" have two of
/// everything. The plan's proper fix is lanes 1C/1D (a <c>Match</c> that owns the gas, the loot,
/// the doors and the vehicles, with the sessions as its members). This type is the minimum that
/// makes the shared thing the players can actually <em>see</em> agree, and it needs no new gas API
/// at all: <c>GasController</c> is documented as deterministic — "everything it decides is a
/// function of the schedule, the start time and the tick time" — and its schedule is
/// <c>GasSchedule.Create(settings, seed)</c>, a pure function. So two controllers opened with the
/// same <see cref="Seed"/> and the same <see cref="StartedAtMs"/> produce the same phase, the same
/// centre, the same radius and the same timers, for ever, with no synchronisation traffic between
/// them.
/// </para>
///
/// <para>
/// <b>What it does NOT share</b> — and the honest list matters more than the fix: ground loot
/// (each session rolls and streams its own <c>LootWorld</c>, so two players can pick up "the same"
/// rifle), doors (each has its own <c>MatchDoors</c>, so one player's open door is another's
/// closed one), vehicles (each has its own <c>VehicleFleet</c>; only the <em>pose</em> of a driven
/// car crosses, through <c>VehiclePoseBroadcast</c>), inventory, health and the alive counter. Two
/// players will see each other, see each other move, and burn in the same gas; everything else is
/// still two private worlds. docs/109 §6 says so in one table.
/// </para>
///
/// <para>Not thread-safe: the listener thread owns it, like everything else on this path.</para>
/// </summary>
public sealed class SharedMatchGas
{
    /// <summary>True while at least one session is playing this plan.</summary>
    public bool Active => Members > 0;

    /// <summary>The seed every member's <c>GasSchedule.Create</c> was given.</summary>
    public ulong Seed { get; private set; }

    /// <summary>Match clock zero — the value every member's <c>GasController.Start</c> was given.</summary>
    public long StartedAtMs { get; private set; }
    public long? PausedAtMs { get; private set; }

    /// <summary>Keep late joiners on the same owner-controlled gas clock.</summary>
    public void SynchronizeClock(long startedAtMs, long? pausedAtMs)
    {
        StartedAtMs = startedAtMs;
        PausedAtMs = pausedAtMs;
    }

    /// <summary>How many sessions currently hold this plan.</summary>
    public int Members { get; private set; }

    /// <summary>
    /// How many sessions have adopted a plan that was already running — the number a two-client
    /// run should see go to 1, and the proof the second player is in the first player's match
    /// rather than one of his own.
    /// </summary>
    public long Joins { get; private set; }

    /// <summary>
    /// Opens a plan, or joins the one already running.
    /// <paramref name="seed"/> and <paramref name="startedAtMs"/> are the caller's own draw and are
    /// used only when no plan is active; on a join they are replaced by the running plan's, which
    /// is the whole point.
    /// </summary>
    /// <returns>True when an existing plan was adopted (this session is the second or later).</returns>
    public bool Join(ref ulong seed, ref long startedAtMs)
    {
        if (Active)
        {
            seed = Seed;
            startedAtMs = StartedAtMs;
            Members++;
            Joins++;
            return true;
        }

        Seed = seed;
        StartedAtMs = startedAtMs;
        PausedAtMs = null;
        Members = 1;
        return false;
    }

    /// <summary>
    /// Drops one member. The plan is forgotten when the last one goes, so the next match draws a
    /// fresh circle rather than inheriting a finished one — and a host that never has two players
    /// at once behaves exactly as it did before this type existed.
    /// </summary>
    public void Leave()
    {
        if (Members <= 0)
        {
            return;
        }

        Members--;
        if (Members == 0)
        {
            Seed = 0;
            StartedAtMs = 0;
            PausedAtMs = null;
        }
    }

    /// <summary>Forgets the plan outright — the host's own reset, not a member leaving.</summary>
    public void Clear()
    {
        Members = 0;
        Seed = 0;
        StartedAtMs = 0;
        PausedAtMs = null;
    }

    public override string ToString() =>
        Active
            ? $"shared gas plan seed {Seed:x16} from {StartedAtMs} ms, {Members} member(s), {Joins} join(s)"
            : "no shared gas plan";
}
