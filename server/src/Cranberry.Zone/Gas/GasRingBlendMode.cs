namespace Cranberry.Zone.Gas;

/// <summary>
/// What goes into <c>ce 01</c>'s first trailing <c>u32</c> — the client's own blend time constant
/// at gas object <c>+0x54</c> (docs/18 §1, G-07 closed).
/// <para>
/// The client's per-frame smoother <c>FUN_140bbed00</c> computes
/// <c>alpha = min(frameDeltaMs, 1000) / blendMs</c> and eases the <b>drawn</b> centre and radius
/// toward the last received ones. So the field is not decoration: it is how far behind the drawn
/// wall runs. A server that re-states the ring every 500 ms while telling the client to ease with a
/// 1 000 ms time constant has, by construction, a wall that never catches the circle it is burning
/// against — the owner's G-h.
/// </para>
/// </summary>
public enum GasRingBlendMode
{
    /// <summary>
    /// <b>The default (D280).</b> The blend constant <b>is the server's own send period for that
    /// state</b>: <see cref="GasSettings.SafeZoneUpdateIntervalMs"/> while the ring is travelling
    /// (the only time a <c>ce 01</c> is followed by another one), and
    /// <see cref="GasSettings.RingBlendMs"/> for every other send — the reveal, the opening ring and
    /// the terminal one, none of which is followed by anything.
    /// <para>
    /// This reduces the exponential lag but does not eliminate it: the client does not finish
    /// a step after one time constant. It also clamps frame delta, not alpha, so a frame longer
    /// than a sub-second blend time can overshoot. These values remain project tuning.
    /// </para>
    /// </summary>
    SendPeriod = 0,

    /// <summary>
    /// The pre-D280 behaviour: every <c>ce 01</c> carries a flat
    /// <see cref="GasSettings.RingBlendMs"/>, whatever the send cadence is. Kept as a one-word
    /// revert (<c>CRANBERRY_GAS_BLEND_MODE=Fixed</c>).
    /// </summary>
    Fixed = 1,
}
