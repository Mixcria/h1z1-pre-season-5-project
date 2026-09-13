namespace Cranberry.Zone.Vehicles;

/// <summary>
/// Which c2s packet asked to get into a car. docs/43 blocker 1 / docs/59 §2.7 question 1 is
/// <i>"does an E-press produce <c>70 01</c> or <c>09 07</c>"</i>, and both arms are already wired
/// into <c>ZoneService</c>. This lane does not guess between them; it makes both safe and makes the
/// first live press <b>answer</b> the question in the log (docs/61 §5).
/// </summary>
public enum VehicleEntrySource
{
    /// <summary><c>70 01 Mount.MountRequest</c>, carrying an explicit seat.</summary>
    MountRequest,

    /// <summary><c>09 07 Command.InteractRequest</c>, no seat — first free seat, driver first.</summary>
    InteractRequest,

    /// <summary><c>09 15 Command.PlayerSelect</c>, the sibling of the interact.</summary>
    PlayerSelect,

    /// <summary>Anything else, including a dev command.</summary>
    Other,
}

/// <summary>What the caller should do with one entry attempt.</summary>
public enum VehicleEntryDecision
{
    /// <summary>Answer it: run the fleet seat arbitration and send the mount burst.</summary>
    Proceed,

    /// <summary>
    /// The <i>other</i> arm already answered this exact press. Send nothing.
    /// <b>This is the one that matters</b>: if the client emits both an interact and a mount request
    /// for one E-press, answering both sends two full mount bursts for one seat.
    /// </summary>
    DuplicateOtherArm,

    /// <summary>The same arm fired twice inside the client own interaction cooldown. Send nothing.</summary>
    DuplicateSameArm,

    /// <summary>
    /// This character is already sitting in this exact car. Send nothing, and in particular do not
    /// treat it as an exit: the client has its own dismount opcodes (<c>70 03</c>, <c>88 18</c>) and
    /// <c>ZoneService</c> already answers both. Inventing an E-toggle here would be a guess.
    /// </summary>
    AlreadySeated,
}

/// <summary>The classification of one attempt.</summary>
/// <param name="Decision">What to do.</param>
/// <param name="Source">The arm that asked.</param>
/// <param name="FirstSourceThisPress">Which arm got there first for this press, when it was not this one.</param>
public readonly record struct VehicleEntryVerdict(
    VehicleEntryDecision Decision,
    VehicleEntrySource Source,
    VehicleEntrySource? FirstSourceThisPress)
{
    /// <summary>Should the caller do the work.</summary>
    public bool Proceed => Decision == VehicleEntryDecision.Proceed;
}

/// <summary>
/// Collapses the two candidate E-press packets into one mount (docs/61 §5).
///
/// <para><b>The problem.</b> <c>ZoneService</c> calls <c>TryEnterVehicle</c> from three places —
/// <c>70 01 MountRequest</c>, <c>09 07 InteractRequest</c> and <c>09 15 PlayerSelect</c> — because
/// nobody has ever pressed E on a parked car and it is unknown which the client sends. That was the
/// right call while the answer was unknown, but it means that if the client sends <i>two</i> of them
/// for one press, the server answers twice. The first answer seats the player and sends the whole
/// burst (<c>0f 3b</c>, <c>88 01</c>, <c>70 02</c>, <c>88 02</c>, <c>88 1b</c> — 239 B); the second
/// reaches <c>VehicleFleet.TryEnter</c>, which returns <c>AlreadyMounted</c>, and is logged as a
/// refusal. That is <i>safe</i> today by accident of the fleet own bookkeeping, not by design, and
/// it reads in the log as an error when it is not one.</para>
///
/// <para><b>The fix, and why it costs nothing.</b> One press is coalesced across all three arms
/// inside the client own <c>VehicleInteractionCooldownMs</c> window. That constant is <b>not</b> a
/// Cranberry number — it is <c>StringHashToValue.Vehicle</c>-family 1,000 ms, the same value the
/// client itself uses for "Too early to exit vehicle", and it is already what
/// <c>VehicleFleet.TryEnter</c> gates on. So the coalescing window and the fleet cooldown are the
/// same window by construction and cannot drift apart.</para>
///
/// <para><b>And it answers the open question.</b> <see cref="FirstObservedSource"/> is set by the
/// first arm that ever reaches a parked car in a session. One E-press, one log line, and docs/43
/// blocker 1 is closed with evidence rather than with a guess — which is the cheapest possible way
/// to settle it, exactly as the lane brief asks.</para>
///
/// <para>It reads no clock; the caller passes the instant in.</para>
/// </summary>
public sealed class VehicleEntryArbiter
{
    private const long Never = long.MinValue;

    private ulong _lastVehicle;
    private VehicleEntrySource _lastSource;
    private long _lastMs = Never;

    /// <summary>
    /// <b>The answer to docs/43 blocker 1</b>: the first arm that ever asked to enter a parked car
    /// in this session, or null before anyone has pressed E. Whatever this reads after one live press
    /// is what an E-press produces.
    /// </summary>
    public VehicleEntrySource? FirstObservedSource { get; private set; }

    /// <summary>Every arm seen this session — non-singleton means the client sends more than one.</summary>
    public IReadOnlySet<VehicleEntrySource> ObservedSources => _observed;

    private readonly HashSet<VehicleEntrySource> _observed = [];

    /// <summary>Attempts collapsed as duplicates, for the log line.</summary>
    public int Coalesced { get; private set; }

    /// <summary>
    /// Classifies one attempt. <b>It records nothing</b> - the caller calls
    /// <see cref="NoteEntered"/> once the mount has actually happened.
    /// <para>
    /// <b>Why the stamp is not here.</b> A <see cref="VehicleEntryDecision.Proceed"/> is a
    /// permission to try, not an answer: the caller's very next step is
    /// <c>VehicleFleet.TryEnter</c>, which can still return <c>SeatOccupied</c>, <c>Cooldown</c> or
    /// <c>Destroyed</c> and send nothing at all. Stamping here remembered that refused press as
    /// answered, so a legitimate retry on the same car inside the 1,000 ms window - the passenger
    /// vacates, the player presses E again - was classified <see cref="VehicleEntryDecision.DuplicateSameArm"/>
    /// and silently swallowed. Wave 5 had no such window: <c>VehicleFleet.TryEnter</c> arms
    /// <c>LastInteractionMs</c> only on success.
    /// </para>
    /// <para>
    /// Coalescing the two arms of one press still works without the early stamp, by two independent
    /// mechanisms: the second arm arrives 1-2 ms after the first arm's <em>successful</em> mount, by
    /// which time <paramref name="alreadySeatedHere"/> is true (<see cref="VehicleEntryDecision.AlreadySeated"/>)
    /// and <see cref="NoteEntered"/> has run (<see cref="VehicleEntryDecision.DuplicateOtherArm"/>).
    /// </para>
    /// </summary>
    /// <param name="source">Which arm asked.</param>
    /// <param name="vehicleGuid">The car.</param>
    /// <param name="alreadySeatedHere">Is the asking character already an occupant of this car.</param>
    /// <param name="nowMs">The caller monotonic instant.</param>
    /// <param name="coalesceWindowMs">
    /// The window inside which two attempts are one press. Pass
    /// <c>VehicleRoster.Constants.InteractionCooldownMs</c>, which is the client own 1,000 ms and is
    /// also what <c>VehicleFleet.TryEnter</c> gates on, so the two can never disagree.
    /// </param>
    public VehicleEntryVerdict Classify(
        VehicleEntrySource source,
        ulong vehicleGuid,
        bool alreadySeatedHere,
        long nowMs,
        int coalesceWindowMs)
    {
        _observed.Add(source);
        FirstObservedSource ??= source;

        if (alreadySeatedHere)
        {
            return new VehicleEntryVerdict(VehicleEntryDecision.AlreadySeated, source, null);
        }

        // The sentinel is compared and never subtracted: nowMs - long.MinValue overflows and wraps
        // negative, which is the trap VehicleFleet.IsCoolingDown documents — there it would have made
        // an untouched car read as permanently on cooldown, here it would make the very first press
        // of a session read as a duplicate of a press that never happened.
        bool recent = _lastMs != Never
            && _lastVehicle == vehicleGuid
            && nowMs >= _lastMs
            && nowMs - _lastMs < Math.Max(0, coalesceWindowMs);

        if (recent)
        {
            Coalesced++;
            VehicleEntryDecision decision = _lastSource == source
                ? VehicleEntryDecision.DuplicateSameArm
                : VehicleEntryDecision.DuplicateOtherArm;
            return new VehicleEntryVerdict(decision, source, _lastSource);
        }

        return new VehicleEntryVerdict(VehicleEntryDecision.Proceed, source, null);
    }

    /// <summary>
    /// Records that an attempt <b>actually seated the character</b> - the answer for this press.
    /// Call it only after the fleet returned <c>Ok</c>; a refused attempt must leave the memory
    /// untouched so the player can press E again.
    /// </summary>
    public void NoteEntered(VehicleEntrySource source, ulong vehicleGuid, long nowMs)
    {
        _lastVehicle = vehicleGuid;
        _lastSource = source;
        _lastMs = nowMs;
    }

    /// <summary>
    /// Clears the coalescing memory without clearing the observation. Called on a dismount, so that
    /// getting straight back into the same car is a fresh press rather than a duplicate — otherwise
    /// a bail-and-re-enter inside one second would be silently ignored.
    /// </summary>
    public void NoteExited()
    {
        _lastVehicle = 0;
        _lastMs = Never;
    }

    /// <summary>A one-line summary for the host log — the thing that closes docs/43 blocker 1.</summary>
    public string Describe() => FirstObservedSource is VehicleEntrySource first
        ? $"first entry arm was {first}; observed [{string.Join(", ", _observed.OrderBy(s => s))}]; "
            + $"{Coalesced} duplicate attempt(s) coalesced"
        : "no vehicle entry attempt has been made yet (docs/43 blocker 1 still open)";

    /// <summary>Drops everything — a match reset.</summary>
    public void Clear()
    {
        _lastVehicle = 0;
        _lastSource = VehicleEntrySource.Other;
        _lastMs = Never;
    }
}
