using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Gas;

/// <summary>
/// The <c>ce 0f</c> countdown widget as a pure function of the match clock, plus the beat that
/// keeps it — and the green <c>ce 02</c> circle — alive.
/// <para>
/// <b>Why this file exists (docs/87 §3.2).</b> Wave 8 sent the countdown on three edges in a
/// 24:50 match. In the owner's 2026-08-30 20:36 session the single <c>ce 0f</c> of the first two
/// minutes landed at 20:37:31.312 and his client then reported — on the wire, client&#8594;server,
/// <c>WallOfData.WindowEvent 9a 05</c> — <c>HudGameModeWindow</c> <c>close</c> at 20:37:31.345 and
/// <c>open</c> at <b>20:37:33.906</b>. The owner's own decompile note for that class
/// (<c>C:\Z1\Server\Zone\ZoneMatch.cs:2334-2342</c>) records that <c>m_brCountdown.visible = false</c>
/// is set in exactly one place in the whole class, <c>enter()</c> — which a window open runs — and
/// that <c>handleCountdownMessageChange</c> early-returns on an empty message without touching
/// visibility. The next <c>ce 0f</c> was 117.6 s later. So the only gas feedback in the first 4:30
/// was almost certainly blank for 97 % of it.
/// </para>
/// <para>
/// Wave 8's reasoning — the 1148 client stores <c>now + ms</c> as an absolute deadline
/// (<c>FUN_140bb9e60</c>) and counts down itself, so a transition-only send suffices — is right
/// about the deadline and says nothing about surviving a UI rebuild. <b>The heal is 19 bytes a
/// second and makes the question moot</b>, which is better than another decompile of a Flash class
/// we cannot prove is identical at 1148. It is also exactly what the owner's blessed build does:
/// his sweep sends the countdown packet every tick (<c>ZoneMatch.cs:1205</c>).
/// </para>
/// <para>
/// <see cref="Countdown"/> is Z1's <c>ZoneMatch.GasCountdown(now, airborne)</c>
/// (<c>ZoneMatch.cs:2217-2273</c>) re-expressed against <see cref="GasSchedule"/>. The
/// <c>(labelId, ms)</c> mapping is wave 8's and is unchanged — only the cadence moves.
/// </para>
/// <para>
/// <b>Lane 2B (docs/104).</b> The three label ids are <see cref="AugustStrings.HudLabels"/>'s,
/// resolved from the client's own <c>CodeStringMappings.txt</c> rows <c>BR.RevealingSafeZone</c>,
/// <c>BR.GasAdvancesIn</c> and <c>BR.GasIsSpreading</c> with their en_us text beside them, rather
/// than <c>GameModeHud</c>'s literals. The values are identical (14153 / 14151 / 14152) and
/// <c>ClientTableMigrationTests</c> pins them against the literals this file used to reach for.
/// </para>
/// </summary>
public static class GasHud
{
    /// <summary>
    /// The widget's <c>(labelId, milliseconds)</c> at a match-clock time. It never returns label 0:
    /// <c>FUN_140bb67b0</c> gates the whole widget on <c>labelId &gt; 0</c>, so a zero label is the
    /// one value that would silently blank the thing this file exists to keep on screen.
    /// </summary>
    /// <param name="settings">Used only when <paramref name="schedule"/> is null (before the drop).</param>
    /// <param name="schedule">This match's plan, or null before it has been built.</param>
    /// <param name="matchClockMs">Milliseconds since the match opened; negatives clamp to 0.</param>
    /// <param name="airborne">
    /// True while the player is still under the canopy, which forces the reveal label — Z1's own
    /// rule (<c>ZoneMatch.cs:2265-2270</c>), on the grounds that the map circle is not what a
    /// parachuting player is looking at. On the shipped ladder it is unreachable: the canopy is gone
    /// long before the 2:00 reveal. It only bites under a compressed ladder
    /// (<c>CRANBERRY_GAS_SCALE=0.2</c> reveals at 0:24), and the 1 Hz heal corrects the widget on
    /// the first pump after the player lands.
    /// </param>
    public static (uint LabelId, uint Milliseconds) Countdown(
        GasSettings settings,
        GasSchedule? schedule,
        long matchClockMs,
        bool airborne)
    {
        ArgumentNullException.ThrowIfNull(settings);
        long clock = Math.Max(0L, matchClockMs);

        if (schedule is null)
        {
            // Before GasSchedule.Create — the drop burst runs a few milliseconds ahead of it.
            return (AugustStrings.HudLabels.RevealingSafeZone, Remaining(settings.FirstRevealDelayMs, clock));
        }

        long firstRevealAt = schedule.Phase(1).RevealAtMs;
        // Population matches reveal during descent and already show their opening wall.
        // Once revealed, their HUD must count to movement instead of sticking at reveal 0.
        if ((airborne && schedule.Settings.PreMoveRing != GasPreMoveRing.Boundary) || clock < firstRevealAt)
        {
            return (AugustStrings.HudLabels.RevealingSafeZone, Remaining(firstRevealAt, clock));
        }

        if (schedule.PhaseAt(clock) is not GasPhase phase)
        {
            return (AugustStrings.HudLabels.RevealingSafeZone, Remaining(firstRevealAt, clock));
        }

        if (clock < phase.ShrinkStartAtMs)
        {
            // A circle is drawn and its ring is still holding: "Gas advances in", counting to the
            // moment it starts moving — NOT to the next reveal. docs/87 §4.3; this is the owner's
            // original label complaint and Wave9GasPresenceTests pins the number.
            return (AugustStrings.HudLabels.GasAdvancesIn, Remaining(phase.ShrinkStartAtMs, clock));
        }

        if (clock < phase.ClosedAtMs)
        {
            return (AugustStrings.HudLabels.GasIsSpreading, Remaining(phase.ClosedAtMs, clock));
        }

        if (phase.Index < schedule.Phases.Count)
        {
            // Only reachable with GasSettings.PhaseHoldMs > 0 (the default chains phases back to
            // back): this phase has closed and the next has not been revealed yet.
            return (AugustStrings.HudLabels.RevealingSafeZone, Remaining(schedule.Phase(phase.Index + 1).RevealAtMs, clock));
        }

        // Parked. "Gas is spreading!" with a zero timer renders as the label and an empty
        // countdown.
        return (AugustStrings.HudLabels.GasIsSpreading, 0u);
    }

    /// <summary>
    /// True when <paramref name="periodMs"/> has elapsed since <paramref name="lastMs"/>, which it
    /// then advances. A zero period is "off" and never fires — that is what
    /// <c>CRANBERRY_GAS_HUD_HEAL_MS=0</c> buys: an A/B against wave 8's transition-only behaviour
    /// without a rebuild. The first call always fires, so a heal goes out on the pump immediately
    /// after the drop rather than one period later.
    /// </summary>
    public static bool DueAt(long nowMs, ref long lastMs, uint periodMs)
    {
        if (periodMs == 0)
        {
            return false;
        }

        if (lastMs != 0L && nowMs - lastMs < periodMs)
        {
            return false;
        }

        lastMs = nowMs == 0L ? 1L : nowMs;
        return true;
    }

    private static uint Remaining(long deadlineMs, long clockMs) =>
        (uint)Math.Max(0L, deadlineMs - clockMs);
}
