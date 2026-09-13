namespace Cranberry.Zone.Gas;

/// <summary>
/// How a match's phase timetable is built out of <see cref="GasSettings"/> (docs/53 §5).
/// <para>
/// Every model describes <c>reveal → hold → advance → closed</c>. <see cref="FixedWindows"/>
/// derives phase windows from the legacy shortening formula; <see cref="SpeedPaced"/> derives
/// movement durations from the radii and a fixed speed; <see cref="PhaseTable"/> supplies each
/// hold and movement duration explicitly, independently of the radius table.
/// </para>
/// </summary>
public enum GasPacing
{
    /// <summary>
    /// The wall closes at a fixed
    /// <see cref="GasSettings.ShrinkSpeedMetresPerSecond"/>, so each phase's moving part is
    /// <c>(previousRadius − targetRadius) / speed</c> and a phase that has further to travel simply
    /// takes longer. Phase 1 holds until <see cref="GasSettings.FirstMoveDelayMs"/> on the match
    /// clock; every later phase holds <see cref="GasSettings.InterPhaseHoldMs"/> after its reveal.
    /// <see cref="GasSettings.FirstPhaseWindowMs"/>, <see cref="GasSettings.PhaseWindowShorteningMs"/>,
    /// <see cref="GasSettings.MinimumPhaseWindowMs"/> and <see cref="GasSettings.ShrinkWarningMs"/>
    /// are <b>not read</b> in this mode.
    /// </summary>
    SpeedPaced = 0,

    /// <summary>
    /// The wave-4 model, kept verbatim so a schedule can be A/B'd against what the owner already
    /// play-tested and so a compressed bring-up match can still be written as literal windows. Each
    /// phase's reveal → closed window is <see cref="GasSettings.WindowForPhase"/>'s
    /// shorten-with-a-floor formula and the hold is a single
    /// <see cref="GasSettings.ShrinkWarningMs"/>. <see cref="GasSettings.ShrinkSpeedMetresPerSecond"/>,
    /// <see cref="GasSettings.FirstMoveDelayMs"/> and <see cref="GasSettings.InterPhaseHoldMs"/> are
    /// <b>not read</b> in this mode.
    /// </summary>
    FixedWindows = 1,

    /// <summary>
    /// Exact per-phase holds and movement durations from <see cref="GasSettings.HoldDurationsMs"/>
    /// and <see cref="GasSettings.AdvanceDurationsMs"/>. The first reveal still occurs at
    /// <see cref="GasSettings.FirstRevealDelayMs"/>. The other pacing models' scalar timing and
    /// speed values are not used; radius changes affect wall speed without changing the timetable.
    /// </summary>
    PhaseTable = 2,
}
