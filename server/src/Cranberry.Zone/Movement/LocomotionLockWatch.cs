using System.Runtime.CompilerServices;

namespace Cranberry.Zone.Movement;

/// <summary>
/// One <see cref="LocomotionLockDetector"/> per live session, kept beside the session object rather
/// than inside it.
///
/// <para><b>Why it is not a field.</b> The detector's hook lives in
/// <c>ZoneService.HandlePlayerMovement</c>, and the wave-11 lanes agreed that file takes exactly one
/// edit per lane while three of them are open in it at once (S7 §3). A
/// <see cref="ConditionalWeakTable{TKey, TValue}"/> keyed on the connection buys the same per-session
/// state for a single call site and no field, and it releases the detector when the connection is
/// collected, so a disconnect needs no teardown. When <c>ZoneService</c> is split (0D) this becomes an
/// ordinary field on the session state and the class goes away.</para>
/// </summary>
public static class LocomotionLockWatch
{
    private static readonly ConditionalWeakTable<object, LocomotionLockDetector> Sessions = new();

    /// <summary>The detector for one session, created on first sight.</summary>
    public static LocomotionLockDetector For(object session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Sessions.GetValue(session, static _ => new LocomotionLockDetector());
    }

    /// <summary>
    /// Feeds one decoded channel-2 record and returns the host-log line on a verdict change, or
    /// null. Safe to call on the 20 Hz path: the common case adds one list append and one sweep of
    /// a window that never holds more than about a hundred samples.
    /// </summary>
    public static string? Observe(object session, ClientMovementUpdate update, long timestampMs) =>
        For(session).Observe(update, timestampMs);

    /// <summary>Drops a session's window — a teleport, a respawn, or the end of a match.</summary>
    public static void Reset(object session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Sessions.TryGetValue(session, out LocomotionLockDetector? detector))
        {
            detector.Reset();
        }
    }
}
