namespace Cranberry.Zone.Gas;

/// <summary>
/// How a match decides <b>where its circles go</b> (docs/118 §3).
/// <para>
/// <b>The owner's ruling, 2026-09-03:</b> <i>"let the nine client <c>GasWeightArea</c> POIs drive
/// the final-circle centre … the endgame must be able to land in any of them"</i> and <i>"the first
/// centre may land anywhere the nine areas weight it"</i>. Retail rebuilt ring placement on
/// 2017-06-29 so that <i>"matches can now end almost anywhere on the map rather than just the
/// middle"</i>, and the nine volumes are what that rework shipped into this client's own
/// <c>Z2.zone</c> (<see cref="Generated.AugustGasWeightAreas"/>).
/// </para>
/// </summary>
public enum GasCentrePlan
{
    /// <summary>
    /// <b>The default (D277).</b> Draw the match's <i>destination</i> first — one of the nine
    /// client volumes, weighted by its own footprint
    /// (<see cref="GasSettings.PoiWeightExponent"/>), then a point inside that box — and walk every
    /// phase centre along the straight line to it, spending each phase's share of the drift budget
    /// in proportion to its own radius drop.
    /// <para>
    /// <b>Why proportional.</b> Phase <c>i</c>'s step is <c>s · f · Δrᵢ</c> with
    /// <c>f = </c><see cref="GasSettings.CentreDriftFraction"/> and <c>s ≤ 1</c>, so it is never
    /// larger than the drift cap the leading-edge rail was solved against — every circle stays
    /// contained in its predecessor and <see cref="GasSettings.LeadingEdgeSpeedCeiling"/> is
    /// unchanged. The whole walk sums to <c>s · f · (InitialRadius − FinalRadius)</c>, which lands
    /// exactly on the destination when it is reachable and as close to it as containment allows
    /// when it is not.
    /// </para>
    /// </summary>
    PoiDestination = 0,

    /// <summary>
    /// D62's walk, kept verbatim for an A/B: one heading drawn per match, then per phase an
    /// area-uniform distance inside <c>f · Δr</c> at that heading ±<see cref="GasSettings.DriftConeDegrees"/>/2.
    /// It reads none of the client's volumes, and with D62's own 0.20 cap it holds every phase-1
    /// centre inside 330 m of <see cref="GasSettings.PlayAreaCentre"/> and every final circle inside
    /// 787 m of it — which is the "every match ends in the middle" the audit measured.
    /// <para>Set <c>CRANBERRY_GAS_CENTRE_PLAN=Drift</c> to get it back.</para>
    /// </summary>
    Drift = 1,
}
