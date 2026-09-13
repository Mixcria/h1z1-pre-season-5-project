using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Descent;

/// <summary>What, if anything, the descent guard wants done about a ride that is still running.</summary>
public enum DescentHandover
{
    /// <summary>Nothing. The overwhelmingly normal answer, and the only one a flying player gets.</summary>
    None,

    /// <summary>
    /// The chute's last reported altitude is within <see cref="DescentDeadline.GroundProximityMetres"/>
    /// of the drop's ground and the ride is past its deadline: the player has landed and the
    /// <c>88 18 Vehicle.Dismiss</c> was lost. Forcing the handover here costs nothing.
    /// </summary>
    Landed,

    /// <summary>
    /// The chute's channel-3 pose stream has been silent for
    /// <see cref="DescentDeadline.StreamSilenceSeconds"/> and the ride is past its deadline: nobody
    /// is flying this canopy, so nothing live can be interrupted.
    /// </summary>
    ClientSilent,

    /// <summary>
    /// The last-resort absolute cap — <see cref="DescentDeadline.StuckClientSeconds"/>. Not
    /// reachable by any ride the client's own physics can produce; it exists so a client that
    /// streams forever without descending cannot wedge the match.
    /// </summary>
    StuckClient,

    /// <summary>
    /// The pre-D239 behaviour, reachable only with the guard turned off
    /// (<c>CRANBERRY_DESCENT_LANDING_GUARD=0</c>): twice the <em>dived</em> expected ride plus 15 s,
    /// with no altitude gate at all. Kept as the one-word revert, and as the thing docs/115 §3 exists
    /// to explain.
    /// </summary>
    LegacyDeadline,
}

/// <summary>
/// The latest a chute ride may still be running before the landing handover is forced — docs/88 §4b,
/// <b>rebuilt by D239 / docs/115 §3</b>.
/// <para>
/// <b>What went wrong, and it is the whole reason this file was rewritten.</b> The first version
/// reasoned about a real elapsed time with <see cref="DescentSettings.PlannedDescentMetresPerSecond"/>
/// — the mean of <em>dived</em> rides — and had no altitude gate. Against the 1,454 m release
/// <c>Legacy36</c> shipped, that is a deadline of <b>87.0 s</b> for a ride that honestly lasts
/// <b>~148 s</b> when the player takes his hands off the controls. On 2026-09-03 it fired three
/// times — <c>host-20260903-082114</c>, <c>-082424</c>, <c>-083422</c> — and each time it
/// <b>force-dismounted a live player about 600 m in the air</b>: the starter weapon was granted
/// mid-air, the practice NPC spawned at y 756, the doors and vehicles burst around a mid-air point,
/// and the player then free-fell with no canopy. It was not a robustness guard, it was the single
/// most match-breaking thing in the drop.
/// </para>
/// <para>
/// <b>The rule now: a live chute in the air is NEVER dismounted by this server.</b> Three things had
/// to change and all three are here.
/// </para>
/// <list type="number">
/// <item>
/// The clock is computed at <see cref="DescentTuning.MinimumRate"/> — the client's own
/// <c>MIN_TERM_VELOCITY 10</c>, measured hands-off at 9.81 / 9.85 / 9.82 m/s over three whole
/// descents on 2026-09-03 — so it describes the slowest ride the client can produce rather than the
/// fastest. At the shipped 850 m release that is ~85 s of honest ride against a
/// <see cref="DeadlineSeconds"/> of ~158 s.
/// </item>
/// <item>
/// Past that clock the handover is still only forced when the chute is <b>near the ground</b>
/// (<see cref="GroundProximityMetres"/>, twice the client's own <c>Vehicles.txt</c> row 13
/// <c>LANDING_HEIGHT 20</c>) or when its <b>pose stream has gone silent</b>
/// (<see cref="StreamSilenceSeconds"/>) — the five archived rides this guard was built for are
/// exactly the silent ones.
/// </item>
/// <item>
/// <see cref="StuckClientSeconds"/> is a generous absolute backstop for a client that streams
/// forever without ever descending. It cannot be reached by a real ride.
/// </item>
/// </list>
/// <para>
/// <b>Why it exists at all.</b> Across 318 host logs, 36 of 36 rides the client survived completed
/// their handover; the 14 non-landings classify as 4 logouts, 2 abandoned matches, 3 capture-replay
/// harness runs and 5 sessions where the client went silent mid-descent. It is only that last group
/// this guards, and in those five the server kept <c>ChuteGuid</c> and <c>MountRequested</c> set —
/// which makes the rider immune to gas and the match unendable — for the whole 120 s idle timeout.
/// </para>
/// <para>
/// <b>Ported idea, not ported number</b> (D53). Z1 guards the same window with
/// <c>ZoneMatchFlow.DropPhaseMaxSeconds = 180</c>, whose own comment says "this only stops a lost
/// dismount from wedging the phase machine".
/// </para>
/// </summary>
public static class DescentDeadline
{
    /// <summary>
    /// <b>SUPERSEDED, kept as the revert.</b> The multiple the pre-D239 deadline applied to the
    /// <em>dived</em> expected ride. Reachable only through <see cref="LegacyDeadlineSeconds(float, float)"/>.
    /// </summary>
    public const double ExpectedRideMultiple = Rulings.Descent.ExpectedRideMultiple;

    /// <summary><b>SUPERSEDED, kept as the revert.</b> The additive half of the pre-D239 deadline.</summary>
    public const double SlackSeconds = Rulings.Descent.DeadlineSlackSeconds;

    /// <summary>
    /// The multiple applied to the <b>hands-off</b> ride. 1.5 rather than 2, because the hands-off
    /// ride is already the worst case the client's own <c>MIN_TERM_VELOCITY</c> allows; the extra
    /// half covers the 14.8-22.0 m/s canopy-opening seconds and a hover.
    /// </summary>
    public const double HandsOffRideMultiple = Rulings.Descent.HandsOffRideMultiple;

    /// <summary>
    /// Slack on top, in seconds: the mount burst → first movement latency (the AutoMount echo
    /// measured +2.147 s and +2.522 s on 2026-09-03) and the 250 ms pump granularity.
    /// </summary>
    public const double HandsOffSlackSeconds = Rulings.Descent.HandsOffSlackSeconds;

    /// <summary>
    /// <b>The gate that makes this guard safe.</b> Twice the client's own <c>Vehicles.txt</c> row 13
    /// <c>LANDING_HEIGHT 20</c> (the binary reads that column name at <c>0x14360ddc0</c>). A chute
    /// whose last reported Y is more than this above the drop's ground is flying, whatever the clock
    /// says. The doubling is Cranberry's, to absorb the terrain difference between the planned marker
    /// and where the player actually glided to — 587 m and 672 m on the two measured rides.
    /// </summary>
    public const float GroundProximityMetres = Rulings.Descent.GroundProximityMetres;

    /// <summary>
    /// How long the chute's channel-3 pose stream must have been silent before the handover may be
    /// forced without an altitude. Twenty seconds is ~200 pump ticks and ~200 missed 10 Hz records.
    /// </summary>
    public const double StreamSilenceSeconds = Rulings.Descent.StreamSilenceSeconds;

    /// <summary>The multiple of <see cref="DeadlineSeconds"/> at which the absolute backstop fires.</summary>
    public const double StuckClientMultiple = Rulings.Descent.StuckClientMultiple;

    /// <summary>
    /// How long after the mount burst a ride from <paramref name="airY"/> down to
    /// <paramref name="groundY"/> is <b>overdue</b> — computed at the hands-off
    /// <see cref="DescentTuning.MinimumRate"/>, so it is the slowest honest ride and not the fastest.
    /// <para>
    /// Being overdue is <b>not</b> on its own a reason to dismount anybody; see
    /// <see cref="Evaluate"/>. At the shipped 850 m release over Z2's median ground this is ~158 s
    /// against an ~85 s hands-off ride and a ~21 s dived one.
    /// </para>
    /// </summary>
    public static double DeadlineSeconds(float airY, float groundY) =>
        (DescentSettings.HandsOffSecondsFor(airY, groundY) * HandsOffRideMultiple) + HandsOffSlackSeconds;

    /// <summary>
    /// The absolute backstop: <see cref="StuckClientMultiple"/> x <see cref="DeadlineSeconds"/>.
    /// ~316 s at the shipped release. Nothing the client's physics can produce reaches it.
    /// </summary>
    public static double StuckClientSeconds(float airY, float groundY) =>
        DeadlineSeconds(airY, groundY) * StuckClientMultiple;

    /// <summary>
    /// True when the chute's last reported altitude <paramref name="chuteY"/> is within
    /// <see cref="GroundProximityMetres"/> of the drop's ground <paramref name="groundY"/>. A
    /// non-finite altitude is never near the ground — a chute whose position was never decoded must
    /// fall through to the silence path, not to a dismount.
    /// </summary>
    public static bool IsNearGround(float chuteY, float groundY) =>
        float.IsFinite(chuteY) && float.IsFinite(groundY) && chuteY - groundY <= GroundProximityMetres;

    /// <summary>
    /// <b>SUPERSEDED, kept as the revert (D239).</b> The pre-D239 deadline: twice the
    /// <em>dived</em> expected ride plus 15 s. It is what fired at 87.0 s three times on 2026-09-03.
    /// </summary>
    public static double LegacyDeadlineSeconds(DescentSettings descent, float airY, float groundY)
    {
        ArgumentNullException.ThrowIfNull(descent);
        return (ExpectedRideMultiple * descent.ExpectedSecondsFor(airY, groundY)) + SlackSeconds;
    }

    /// <summary><see cref="LegacyDeadlineSeconds(DescentSettings, float, float)"/> against the measured planning rate.</summary>
    public static double LegacyDeadlineSeconds(float airY, float groundY) =>
        LegacyDeadlineSeconds(DescentSettings.Default, airY, groundY);

    /// <summary>
    /// True when a chute mounted <paramref name="elapsedMs"/> ago has outlived
    /// <see cref="DeadlineSeconds"/>. False for a non-positive elapsed time, so a clock that has not
    /// moved yet — or a <c>MountedAtMs</c> that was never set — can never force a handover.
    /// </summary>
    public static bool HasExpired(long elapsedMs, float airY, float groundY) =>
        elapsedMs > 0 && elapsedMs >= DeadlineSeconds(airY, groundY) * 1000d;

    /// <summary>
    /// <b>The whole decision, in one pure function.</b> Given the ride's clock, the last thing the
    /// chute's pose stream said and how long ago it said it, decide whether the landing handover may
    /// be forced.
    /// </summary>
    /// <param name="guard">
    /// D239's near-ground guard. False restores the pre-D239 behaviour exactly — the dived deadline,
    /// no altitude gate — which is the one-word revert
    /// (<c>CRANBERRY_DESCENT_LANDING_GUARD=0</c>).
    /// </param>
    /// <param name="elapsedMs">Milliseconds since the mount burst; 0 or negative means "no ride".</param>
    /// <param name="silentMs">
    /// Milliseconds since the last channel-3 record for this chute, or a negative number when none
    /// has ever arrived (in which case the ride clock itself measures the silence).
    /// </param>
    /// <param name="airY">The release altitude.</param>
    /// <param name="groundY">The drop's planned ground.</param>
    /// <param name="chuteY">
    /// The chute's last reported altitude, or <c>null</c> / non-finite when the pose stream has never
    /// carried one.
    /// </param>
    public static DescentHandover Evaluate(
        bool guard,
        long elapsedMs,
        long silentMs,
        float airY,
        float groundY,
        float? chuteY)
    {
        if (elapsedMs <= 0)
        {
            return DescentHandover.None;
        }

        if (!guard)
        {
            return elapsedMs >= LegacyDeadlineSeconds(airY, groundY) * 1000d
                ? DescentHandover.LegacyDeadline
                : DescentHandover.None;
        }

        double elapsedSeconds = elapsedMs / 1000d;
        if (elapsedSeconds >= StuckClientSeconds(airY, groundY))
        {
            return DescentHandover.StuckClient;
        }

        if (elapsedSeconds < DeadlineSeconds(airY, groundY))
        {
            return DescentHandover.None;
        }

        if (chuteY is float reported && IsNearGround(reported, groundY))
        {
            return DescentHandover.Landed;
        }

        // A ride that has never produced a single pose is silent for its whole length.
        double silentSeconds = silentMs >= 0 ? silentMs / 1000d : elapsedSeconds;
        return silentSeconds >= StreamSilenceSeconds
            ? DescentHandover.ClientSilent
            : DescentHandover.None;
    }
}
