using System.Globalization;

namespace Cranberry.Zone;

/// <summary>
/// A client-originated milestone the server can wait for. Every member here is something the
/// <b>client</b> sends or does — never a server send — because a server send-log line proves
/// nothing about the client (docs/32: the four-hour 2026-08-29 outage was invisible precisely
/// because the server kept firing blind <c>Task.Delay</c> timers at a client stuck on a loading
/// screen, and every one of those sends logged as a success).
/// <para>
/// The enum is the slot index of <see cref="ClientProgressWatchdog"/>'s fixed array, so members
/// may be appended but never renumbered.
/// </para>
/// </summary>
public enum ClientMilestone
{
    /// <summary>Not a milestone; the empty slot and the "nothing was armed" answer.</summary>
    None = 0,

    /// <summary>
    /// <c>ClientIsReady (0x04)</c> for the <b>menu</b> zone, after the bootstrap's
    /// <c>ZoneDoneSendingInitialData</c>. Live latency 4,841 ms
    /// (<c>logs/host-20260829-184346.log</c> 18:44:13.163 → 18:44:18.004).
    /// </summary>
    MenuClientIsReady,

    /// <summary>
    /// <c>ClientIsReady (0x04)</c> for the <b>match</b> zone, after <c>ClientBeginZoning</c> +
    /// self record + <c>ZoneDoneSendingInitialData</c>. Live latency 2,052 ms (18:44:29.771 →
    /// 18:44:31.823). This is the milestone whose absence caused the outage: without it the client
    /// is still on the loading screen and every later stage is a monologue.
    /// </summary>
    ZoningClientIsReady,

    /// <summary>
    /// <c>ClientFinishedLoading (0x02)</c>. Live latency 969 ms after <c>ClientIsReady</c>
    /// (18:44:31.823 → 18:44:32.792).
    /// </summary>
    ClientFinishedLoading,

    /// <summary>
    /// <c>WallOfData.WindowEvent</c> with <c>window='LoadingScreenWindow' action='close'</c> — the
    /// client closing its own loading screen, the strongest "I am in the world" evidence short of a
    /// screenshot. Live latency 1 ms after <c>ClientFinishedLoading</c> (18:44:32.793).
    /// </summary>
    LoadingScreenClosed,

    /// <summary>
    /// <c>SynchronizedTeleport.ClientReady (e8 02 00)</c> after the <c>UpdateLocation</c> with
    /// <c>WaitForTeleport</c> at StartMatch. Live latency 1,530 ms (18:45:04.824 → 18:45:06.354).
    /// </summary>
    TeleportClientReady,

    /// <summary>
    /// The client's own <c>Vehicle.AutoMount (88 19)</c> echo, which is its mount-completion
    /// request (docs/12 §3). Live latency 23 ms after <c>SynchronizedTeleport.Release</c>
    /// (18:45:06.356 → 18:45:06.379).
    /// </summary>
    ParachuteAutoMountEcho,

    /// <summary>
    /// <c>Character.FullCharacterDataRequest (0F 45)</c> — the client's answer to an
    /// <c>AddLightweightNpc (0xd6)</c>. Capture analysis measured it at ~150 ms after every spawn
    /// (<c>out/stage1-assessment/unanswered-requests.md</c>:64).
    /// </summary>
    FullCharacterDataRequest,

    /// <summary>
    /// Channel-2 player movement after the drop release — the client proving it is simulating the
    /// character rather than sitting frozen. The 2026-08-29 descent produced 4,618 of these.
    /// </summary>
    DescentMovement,

    /// <summary>
    /// Not armed with <see cref="ClientProgressWatchdog.Expect(ClientMilestone, int, string?, ulong, bool)"/>:
    /// the label the idle watch reports under when <see cref="ClientProgressWatchdog.IdleTimeoutMs"/>
    /// elapses with no client packet of any kind. Keep it last.
    /// </summary>
    ClientTraffic,
}

/// <summary>One milestone that did not arrive before its deadline.</summary>
/// <param name="Milestone">What the server was waiting for.</param>
/// <param name="Key">The instance it was waiting on (a loot guid, a vehicle guid); 0 when the milestone is a singleton.</param>
/// <param name="WaitedMs">How long the wait actually lasted before the poll noticed.</param>
/// <param name="TimeoutMs">The deadline it blew.</param>
/// <param name="LastSent">What the server had last sent when the wait was armed.</param>
/// <param name="Suppresses">Whether this miss puts the session into <see cref="ClientProgressWatchdog.IsStalled"/>.</param>
public readonly record struct ClientProgressStall(
    ClientMilestone Milestone,
    ulong Key,
    long WaitedMs,
    int TimeoutMs,
    string LastSent,
    bool Suppresses)
{
    /// <summary>The single loud line this stall is meant to produce; pass it straight to <c>_log.Warn</c>.</summary>
    public override string ToString()
    {
        string instance = Key == 0 ? string.Empty : FormattableString.Invariant($" for {Key}");
        string sent = string.IsNullOrEmpty(LastSent) ? "(not recorded)" : LastSent;
        string tail = Suppresses
            ? "; downstream blind timers suppressed — this match is stalled, not progressing"
            : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"client-progress watchdog: the client never sent {Milestone}{instance} — waited {WaitedMs} ms (deadline {TimeoutMs} ms); last sent: {sent}{tail}");
    }
}

/// <summary>The outcome of an <see cref="ClientProgressWatchdog.Observe"/> call.</summary>
/// <param name="WasExpected">False when nothing was armed for that milestone (an unsolicited or duplicate arrival).</param>
/// <param name="LatencyMs">Milliseconds from the arm to the arrival.</param>
/// <param name="WasLate">True when the arrival came after the deadline — i.e. the stall had already been reported.</param>
/// <param name="KeyMatched">False when the arrival carried a different instance key than the armed one.</param>
public readonly record struct MilestoneArrival(
    bool WasExpected,
    long LatencyMs,
    bool WasLate,
    bool KeyMatched);

/// <summary>
/// Watches for the client going silent. After a server action the caller arms the client-originated
/// milestone it expects next (<see cref="Expect(ClientMilestone, int, string?, ulong, bool)"/>);
/// when the client delivers, the caller calls <see cref="Observe"/>; a poll
/// (<see cref="TryTakeExpired"/>) reports every milestone whose deadline passed, once each, as a
/// <see cref="ClientProgressStall"/> for a single loud <c>WRN</c>.
/// <para>
/// <b>Why.</b> docs/32: on 2026-08-29 the client stopped answering during zoning and nothing
/// noticed — the lobby HUD, StartMatch and the parachute all went out on <c>Task.Delay</c> timers
/// into a loading screen, and the host log recorded four hours of successful sends. The server log
/// records what the server SENT, never what the client DID; this type is the bookkeeping that turns
/// that asymmetry into an alarm.
/// </para>
/// <para>
/// <b>Threading and lifetime.</b> This class owns no timer, no thread and no
/// <see cref="System.Threading.Tasks.Task"/>: it is a passive state machine that must be driven
/// from the listener thread through <c>ZoneService.Post</c> / <c>ZoneService.Later</c>, exactly like
/// the gas pump. That is deliberate — a wave-1 review flagged use-after-close on session-owned
/// timers, and a type that cannot start one cannot leak one. <see cref="Reset"/> disarms
/// everything, so a poll chain guarded by <see cref="NeedsPolling"/> ends on its next pass after the
/// link closes.
/// </para>
/// <para>
/// <b>Allocation.</b> One fixed <see cref="ClientMilestone"/>-indexed array per session, allocated
/// in the constructor; arming, observing and polling allocate nothing (the results are record
/// structs). The only strings are the caller's own <c>lastSent</c> breadcrumbs — pass literals or
/// values built at arm time, never per-tick interpolations.
/// </para>
/// <para>
/// <b>Testing.</b> Inject a clock: <c>new ClientProgressWatchdog(() =&gt; fakeNowMs)</c>. Nothing
/// else in the type reads the wall clock.
/// </para>
/// </summary>
public sealed class ClientProgressWatchdog
{
    /// <summary>One slot per <see cref="ClientMilestone"/> value, including the unused <c>None</c>.</summary>
    private const int SlotCount = (int)ClientMilestone.ClientTraffic + 1;

    private static readonly Func<long> SystemClock = static () => Environment.TickCount64;

    private readonly Func<long> _clock;
    private readonly Expectation[] _slots = new Expectation[SlotCount];

    private int _armed;
    private long _lastClientPacketAtMs;
    private bool _idleActive;
    private bool _idleReported;
    private bool _stalled;
    private ClientProgressStall _stall;

    /// <param name="clock">
    /// Monotonic millisecond clock; defaults to <see cref="Environment.TickCount64"/>. A test
    /// supplies a mutable counter and drives time by hand.
    /// </param>
    public ClientProgressWatchdog(Func<long>? clock = null) => _clock = clock ?? SystemClock;

    /// <summary>
    /// Milliseconds of total client silence (no packet of any kind) that raise a
    /// <see cref="ClientMilestone.ClientTraffic"/> stall, once per silence episode. 0 (the default)
    /// disables the idle watch. The August client streams channel-2 movement at 20 Hz once it is
    /// Running and sends <c>GameTimeSync</c> periodically before that, so any multi-second gap is
    /// already abnormal; 20 s is the recommended value.
    /// </summary>
    public int IdleTimeoutMs { get; set; }

    /// <summary>How many milestones are currently armed.</summary>
    public int ArmedCount => _armed;

    /// <summary>
    /// True once a milestone marked <c>suppressOnMiss</c> has blown its deadline. The integrator
    /// gates every downstream blind timer on <c>!IsStalled</c>, so a dead client surfaces as a
    /// stalled match instead of a server monologue.
    /// </summary>
    public bool IsStalled => _stalled;

    /// <summary>The stall that set <see cref="IsStalled"/>; default when the session is healthy.</summary>
    public ClientProgressStall Stall => _stall;

    /// <summary>
    /// Whether the poll chain still has anything to do: a milestone is armed, or the idle watch is
    /// enabled and the client has spoken at least once. A self-rescheduling <c>Later</c> pump
    /// re-arms itself while this is true and stops otherwise.
    /// <para>
    /// <b>This is true for the whole life of a live session</b>, and deliberately so: the idle watch
    /// exists to notice a client that stops speaking, which cannot be noticed without polling while
    /// it is still speaking. <see cref="Reset"/> — the link close, or an abandoned match — is what
    /// ends the chain. The cost is one timer continuation per session per poll (one per second at
    /// <c>ZoneService.WatchdogPollMs</c>), which is two orders of magnitude below the 20 Hz movement
    /// stream that same session is already sending. Do not "optimise" it away by clearing
    /// <c>_idleActive</c> on report: a session that went silent, was reported, and then recovered
    /// would never be watched again.
    /// </para>
    /// </summary>
    public bool NeedsPolling => _armed > 0 || (IdleTimeoutMs > 0 && _idleActive);

    /// <summary>The earliest armed deadline in clock terms, or null when nothing is armed.</summary>
    public long? NextDeadlineAtMs
    {
        get
        {
            long? earliest = null;
            foreach (Expectation slot in _slots)
            {
                if (slot.Armed && (earliest is null || slot.DeadlineAtMs < earliest))
                {
                    earliest = slot.DeadlineAtMs;
                }
            }

            if (IdleTimeoutMs > 0 && _idleActive && !_idleReported)
            {
                long idleAt = _lastClientPacketAtMs + IdleTimeoutMs;
                if (earliest is null || idleAt < earliest)
                {
                    earliest = idleAt;
                }
            }

            return earliest;
        }
    }

    /// <summary>Milliseconds since the last client packet; 0 before the first one.</summary>
    public long SilentForMs => _idleActive ? Math.Max(0, _clock() - _lastClientPacketAtMs) : 0;

    /// <summary>
    /// The breadcrumb <see cref="Expect(ClientMilestone, int, string?, ulong, bool)"/> uses when
    /// the caller passes no <c>lastSent</c> of its own. Update it with <see cref="NoteSent"/> at the
    /// handful of sends worth naming in a stall line.
    /// </summary>
    public string LastSent { get; private set; } = string.Empty;

    /// <summary>Records what the server just sent, for the next stall line.</summary>
    public void NoteSent(string what) => LastSent = what ?? string.Empty;

    /// <summary>
    /// Records that a packet arrived from the client. Call it once per inbound tunnel packet
    /// (including the high-rate movement channels): it is one field write plus a clock read, and it
    /// is what makes the idle watch mean "the client is silent" rather than "no milestone is due".
    /// </summary>
    public void NoteClientPacket()
    {
        _lastClientPacketAtMs = _clock();
        _idleActive = true;
        _idleReported = false;
    }

    /// <summary>
    /// Arms (or re-arms, resetting the deadline) the milestone the server expects next. Re-arming
    /// pushes the deadline out, so for a repeated send use
    /// <see cref="ExpectFirst(ClientMilestone, int, string?, ulong, bool)"/> instead.
    /// </summary>
    /// <param name="milestone">What the client must send next.</param>
    /// <param name="timeoutMs">Deadline in milliseconds; see <see cref="DefaultTimeoutMs"/>.</param>
    /// <param name="lastSent">What the server just sent; null uses <see cref="LastSent"/>.</param>
    /// <param name="key">The instance being waited on (a loot or vehicle guid); 0 for a singleton.</param>
    /// <param name="suppressOnMiss">Whether missing this deadline sets <see cref="IsStalled"/>.</param>
    public void Expect(
        ClientMilestone milestone,
        int timeoutMs,
        string? lastSent = null,
        ulong key = 0,
        bool suppressOnMiss = false) =>
        Arm(milestone, timeoutMs, lastSent, key, suppressOnMiss, replace: true);

    /// <summary>Arms a milestone with the deadline and suppression of <see cref="DefaultTimeoutMs"/> / <see cref="DefaultSuppresses"/>.</summary>
    public void Expect(ClientMilestone milestone, string? lastSent = null, ulong key = 0) =>
        Arm(milestone, DefaultTimeoutMs(milestone), lastSent, key, DefaultSuppresses(milestone), replace: true);

    /// <summary>
    /// Arms the milestone only if it is not already armed, keeping the <b>oldest</b> outstanding
    /// deadline. This is the form a repeated send must use: arming per send would push the deadline
    /// forward on every one, and a client that answers none of them would never trip the watchdog.
    /// </summary>
    public void ExpectFirst(
        ClientMilestone milestone,
        int timeoutMs,
        string? lastSent = null,
        ulong key = 0,
        bool suppressOnMiss = false) =>
        Arm(milestone, timeoutMs, lastSent, key, suppressOnMiss, replace: false);

    /// <summary>Arms the milestone if not already armed, using the default deadline and suppression.</summary>
    public void ExpectFirst(ClientMilestone milestone, string? lastSent = null, ulong key = 0) =>
        Arm(milestone, DefaultTimeoutMs(milestone), lastSent, key, DefaultSuppresses(milestone), replace: false);

    /// <summary>
    /// Records that the client delivered a milestone. Any arrival also counts as client traffic, so
    /// the idle watch is reset too. A late arrival clears <see cref="IsStalled"/> — the client is
    /// demonstrably alive again — and reports <see cref="MilestoneArrival.WasLate"/> so the caller
    /// can decide whether to resume the stage it skipped; stages already skipped are never replayed
    /// by this type.
    /// </summary>
    /// <param name="milestone">What arrived.</param>
    /// <param name="key">
    /// The instance the client named, if any. Keys are diagnostic, not routing: an arrival clears
    /// the slot whatever key it carries (one answer proves the client is answering that milestone at
    /// all), and reports the mismatch through <see cref="MilestoneArrival.KeyMatched"/>.
    /// </param>
    public MilestoneArrival Observe(ClientMilestone milestone, ulong key = 0)
    {
        int index = IndexOf(milestone);
        NoteClientPacket();

        // The unstall MUST come before the armed check: TryTakeExpired disarms the slot *before* it
        // sets _stalled, so by the time a late answer arrives there is nothing armed left to match
        // on. Clearing it only in the armed branch confined recovery to the ≤1 s window between the
        // deadline and the poll that reported it; any later arrival left the session suppressed for
        // its whole life (nothing else clears _stalled but Reset), so a merely slow client — one
        // that took >15 s from ClientIsReady to ClientFinishedLoading — permanently lost its lobby
        // HUD, StartMatch, parachute and gas even after it recovered.
        long stalledForMs = 0;
        bool unstalled = false;
        if (_stalled && _stall.Milestone == milestone)
        {
            stalledForMs = _stall.WaitedMs;
            _stalled = false;
            _stall = default;
            unstalled = true;
        }

        ref Expectation slot = ref _slots[index];
        if (!slot.Armed)
        {
            // A late arrival for a milestone the poll already took and reported. It WAS expected —
            // the caller needs to hear that the client recovered — and the best latency available is
            // the wait the stall recorded, which understates by at most one poll interval.
            return unstalled
                ? new MilestoneArrival(WasExpected: true, LatencyMs: stalledForMs, WasLate: true, KeyMatched: true)
                : default;
        }

        long now = _clock();
        var arrival = new MilestoneArrival(
            WasExpected: true,
            LatencyMs: Math.Max(0, now - slot.ArmedAtMs),
            WasLate: now > slot.DeadlineAtMs,
            KeyMatched: key == 0 || slot.Key == 0 || slot.Key == key);

        slot = default;
        _armed--;

        return arrival;
    }

    /// <summary>
    /// Takes one expired milestone, removing it so it is reported exactly once. Drain it in a
    /// <c>while</c> loop from the poll pump; each result is one <c>WRN</c> line
    /// (<see cref="ClientProgressStall.ToString"/>).
    /// </summary>
    public bool TryTakeExpired(out ClientProgressStall stall)
    {
        long now = _clock();
        for (int index = 1; index < _slots.Length; index++)
        {
            ref Expectation slot = ref _slots[index];
            if (!slot.Armed || now < slot.DeadlineAtMs)
            {
                continue;
            }

            stall = new ClientProgressStall(
                (ClientMilestone)index,
                slot.Key,
                Math.Max(0, now - slot.ArmedAtMs),
                slot.TimeoutMs,
                slot.LastSent,
                slot.Suppresses);
            slot = default;
            _armed--;

            if (stall.Suppresses && !_stalled)
            {
                _stalled = true;
                _stall = stall;
            }

            return true;
        }

        if (IdleTimeoutMs > 0
            && _idleActive
            && !_idleReported
            && now - _lastClientPacketAtMs >= IdleTimeoutMs)
        {
            _idleReported = true;
            stall = new ClientProgressStall(
                ClientMilestone.ClientTraffic,
                Key: 0,
                WaitedMs: now - _lastClientPacketAtMs,
                TimeoutMs: IdleTimeoutMs,
                LastSent: LastSent,
                Suppresses: false);
            return true;
        }

        stall = default;
        return false;
    }

    /// <summary>Disarms one milestone without reporting it — the stage was cancelled, not missed.</summary>
    public void Clear(ClientMilestone milestone)
    {
        ref Expectation slot = ref _slots[IndexOf(milestone)];
        if (slot.Armed)
        {
            slot = default;
            _armed--;
        }
    }

    /// <summary>True when the milestone is currently being waited for.</summary>
    public bool IsArmed(ClientMilestone milestone) => _slots[IndexOf(milestone)].Armed;

    /// <summary>
    /// Disarms everything and clears the stall and the idle watch. Call it at the link close and
    /// wherever a match is abandoned: <see cref="NeedsPolling"/> then goes false and the poll chain
    /// ends on its next pass instead of outliving the session.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_slots);
        _armed = 0;
        _stalled = false;
        _stall = default;
        _idleActive = false;
        _idleReported = false;
        _lastClientPacketAtMs = 0;
    }

    /// <summary>
    /// The milestone table's deadline, in milliseconds (docs/35). Every value is a multiple of the
    /// latency the same milestone showed in the live-verified 2026-08-29 session, chosen so a real
    /// client never trips it and a dead one trips it before the next blind timer fires.
    /// </summary>
    public static int DefaultTimeoutMs(ClientMilestone milestone) => milestone switch
    {
        // 4,841 ms live; the menu bootstrap has no downstream timer to protect, so it is generous.
        ClientMilestone.MenuClientIsReady => 30_000,

        // 2,052 ms live. Held under the 15,000 ms lobby-HUD arm on purpose: the suppression has to
        // land before the first blind send of the match flow, which is exactly what docs/32 lost.
        ClientMilestone.ZoningClientIsReady => 12_000,

        // 969 ms live, armed when ClientIsReady arrives (≈2 s in), so it expires ≈17 s into zoning —
        // after the lobby HUD but before StartMatch (armed +20 s), which it therefore suppresses.
        ClientMilestone.ClientFinishedLoading => 15_000,

        // 1 ms live. Informative: the client closing its own loading screen is the strongest
        // "in the world" evidence we get on the wire, so it is worth a warning but not a suppression.
        ClientMilestone.LoadingScreenClosed => 20_000,

        // 1,530 ms live. Nothing downstream is on a timer — the release is driven by this very
        // packet — so a miss is a loud warning and the drop simply never happens.
        ClientMilestone.TeleportClientReady => 15_000,

        // 23 ms live after the release. Short because the echo is the client's own request and the
        // mount burst is the only thing waiting on it.
        ClientMilestone.ParachuteAutoMountEcho => 5_000,

        // ~150 ms measured over 96 captures (out/stage1-assessment/unanswered-requests.md:64).
        ClientMilestone.FullCharacterDataRequest => 3_000,

        // The descent produced 4,618 channel-2 packets; the first arrives within a second of the
        // release. A miss means the client is frozen in the air.
        ClientMilestone.DescentMovement => 10_000,

        // Total silence. The client streams movement at 20 Hz once Running.
        ClientMilestone.ClientTraffic => 20_000,

        _ => throw new ArgumentOutOfRangeException(nameof(milestone), milestone, "No default deadline for this milestone."),
    };

    /// <summary>
    /// Whether missing this milestone should suppress the downstream blind timers (docs/35). Only
    /// the two zoning milestones do: they are the ones whose absence turns the whole match flow into
    /// a monologue, and they are the two the outage lost.
    /// </summary>
    public static bool DefaultSuppresses(ClientMilestone milestone) =>
        milestone is ClientMilestone.ZoningClientIsReady or ClientMilestone.ClientFinishedLoading;

    private void Arm(
        ClientMilestone milestone,
        int timeoutMs,
        string? lastSent,
        ulong key,
        bool suppressOnMiss,
        bool replace)
    {
        int index = IndexOf(milestone);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);

        ref Expectation slot = ref _slots[index];
        if (slot.Armed && !replace)
        {
            return;
        }

        if (!slot.Armed)
        {
            _armed++;
        }

        long now = _clock();
        slot = new Expectation(
            Armed: true,
            Key: key,
            ArmedAtMs: now,
            DeadlineAtMs: now + timeoutMs,
            TimeoutMs: timeoutMs,
            LastSent: lastSent ?? LastSent,
            Suppresses: suppressOnMiss);
    }

    private static int IndexOf(ClientMilestone milestone)
    {
        int index = (int)milestone;
        if (index <= 0 || index >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(milestone), milestone, "Not a waitable client milestone.");
        }

        return index;
    }

    private readonly record struct Expectation(
        bool Armed,
        ulong Key,
        long ArmedAtMs,
        long DeadlineAtMs,
        int TimeoutMs,
        string LastSent,
        bool Suppresses);
}
