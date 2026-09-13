using System.Numerics;

namespace Cranberry.Zone.World;

/// <summary>
/// What one tick of a world re-stream pump should do. The three arms are deliberately distinct:
/// folding <see cref="Wait"/> into <see cref="Stop"/> is the defect the verify pass of wave 4 found
/// in <c>ZoneService.PumpDoors</c> — <see cref="Stop"/> ends the timer chain for the rest of the
/// match, and only "the match is over", "this feature is switched off" or "the interval is not
/// positive" may do that. "The per-match state does not exist yet" and "the player has no pose yet"
/// are <see cref="Wait"/>, because both are true for the first second or two of every match.
/// </summary>
/// <remarks>
/// Structurally identical to <c>Cranberry.Zone.World.Doors.DoorPumpStep</c>, which predates it.
/// docs/52 §2c asks for one pump serving both doors and ground loot; this is the decision half of
/// it, lifted out of <c>MatchDoors</c> so the loot arm can use it without depending on the door
/// area. <c>WorldStreamPumpTests</c> pins the two against each other arm for arm, so a change to
/// either that is not made to the other fails a test rather than drifting.
/// </remarks>
public enum WorldStreamStep
{
    /// <summary>End the chain. The match is over, or this streamer is switched off.</summary>
    Stop,

    /// <summary>Nothing to do this tick — re-arm and ask again.</summary>
    Wait,

    /// <summary>A burst is due: plan it, send it, then re-arm.</summary>
    Restream,
}

/// <summary>
/// The pure decision a re-stream pump makes once per tick, shared by the door arm and the ground
/// loot arm (docs/52 §2c). It is a free function so it can be pinned by a test rather than by a
/// live match, and so a caller can hold no state at all beyond the predicate it passes in.
/// </summary>
public static class WorldStream
{
    /// <summary>
    /// Decides this tick. <paramref name="shouldRestream"/> is asked <b>only</b> when everything
    /// else is ready, so a predicate that runs a spatial query costs nothing on a tick that was
    /// going to wait anyway.
    /// </summary>
    /// <param name="inMatch">Is the session actually in a match right now.</param>
    /// <param name="enabled">Is this streamer switched on (the <c>Send…</c> option).</param>
    /// <param name="intervalMs">The pump period. Non-positive means "never", i.e. <see cref="WorldStreamStep.Stop"/>.</param>
    /// <param name="hasState">Has the per-match state been created yet — it is built lazily inside
    /// the landing burst, which can land <i>after</i> the first pump tick when the interval is
    /// overridden downwards. <b>This is a wait, never a stop.</b></param>
    /// <param name="centre">The player's pose, or null when no movement packet has arrived yet.</param>
    /// <param name="radius">Streaming radius, handed straight to <paramref name="shouldRestream"/>.</param>
    /// <param name="shouldRestream">The area's own "is a burst due" test.</param>
    public static WorldStreamStep NextStep(
        bool inMatch,
        bool enabled,
        int intervalMs,
        bool hasState,
        Vector3? centre,
        float radius,
        Func<Vector3, float, bool> shouldRestream)
    {
        ArgumentNullException.ThrowIfNull(shouldRestream);

        if (!inMatch || !enabled || intervalMs <= 0)
        {
            return WorldStreamStep.Stop;
        }

        if (!hasState || centre is not Vector3 point)
        {
            return WorldStreamStep.Wait;
        }

        return shouldRestream(point, radius) ? WorldStreamStep.Restream : WorldStreamStep.Wait;
    }
}
