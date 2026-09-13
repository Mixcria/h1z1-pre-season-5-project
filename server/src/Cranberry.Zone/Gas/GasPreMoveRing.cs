namespace Cranberry.Zone.Gas;

/// <summary>
/// What the client is shown — and what the gas is allowed to burn — between the <c>ce 16</c>
/// StartMatch and the moment phase 1's ring first moves (docs/77 §6).
/// <para>
/// <b>The owner's ruling, from his own click-test of the Z1 server:</b> <i>"The gas is already on the
/// map — it shouldn't be on the map until it starts moving and closing in on the circle."</i> His
/// build ships the two levers that implement it, both on.
/// </para>
/// <para>
/// <b>The draw gate and the damage gate are the same predicate</b> (<c>GasSchedule.IsLethalAt</c> /
/// <c>GasSchedule.IsRingVisibleAt</c>), deliberately: if they came apart, a player 4 500 m out would
/// be burned by a circle their client is not drawing.
/// </para>
/// </summary>
public enum GasPreMoveRing
{
    /// <summary>
    /// <b>The default.</b> No <c>ce 01</c> at all and nothing lethal until phase 1's
    /// <c>ShrinkStartAtMs</c> (4:30 on the shipped ladder). The reveal at 2:00 still sends
    /// <c>ce 02</c>, so the green "next safe zone" circle appears on time — what is withheld is the
    /// gas wall itself, which is exactly what the owner reported.
    /// </summary>
    None = 0,

    /// <summary>
    /// One <c>ce 01</c> with <see cref="GasPackets.RingTerminalRadius"/> (0) at match open, then
    /// nothing until the ring moves. Radius 0 is the client's own recognised "no gas" value at 1148
    /// too — <c>FUN_140bbba50</c> clears the volume renderer's enable byte and the smoother returns
    /// immediately (docs/15 §2, docs/18 §1) — so this is the same rule stated positively. It is
    /// here because a radius-0 ring correlated with a white bloom artefact on the owner's own
    /// server, which is why he moved off it; if sending nothing upsets the 1148 HUD, this is the
    /// position to try next.
    /// </summary>
    ZeroRadius = 1,

    /// <summary>
    /// The pre-wave-8 behaviour: a <c>ce 01</c> at <c>GasSettings.InitialRadius</c> from match open,
    /// and the play-area boundary lethal from the first damage tick (docs/23). Kept so the change
    /// can be A/B'd in place from a restart.
    /// </summary>
    Boundary = 2,
}
