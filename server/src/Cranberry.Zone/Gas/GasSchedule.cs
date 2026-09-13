using System.Numerics;

namespace Cranberry.Zone.Gas;

/// <summary>
/// The whole gas plan of one match, built once from a <see cref="GasSettings"/> and a seed and
/// then answered with pure functions of the match clock. No packet, no wall clock and no state:
/// <see cref="GasController"/> owns the running-match bookkeeping, this type owns the geometry and
/// the timetable. D23: every number here is Cranberry's own design decision — the client has no
/// phase table at all (docs/15 §1a, absence proven).
/// </summary>
public sealed class GasSchedule
{
    private readonly GasPhase[] _phases;

    private GasSchedule(GasSettings settings, ulong seed, GasCircle initial, GasPhase[] phases)
    {
        Settings = settings;
        Seed = seed;
        InitialCircle = initial;
        _phases = phases;
    }

    public GasSettings Settings { get; }

    /// <summary>The seed every centre of this match was drawn from.</summary>
    public ulong Seed { get; }

    /// <summary>
    /// The circle in force before phase 1 is revealed — the whole play area.
    /// <para>
    /// Its radius is always <see cref="GasSettings.InitialRadius"/>. Its centre is
    /// <see cref="GasSettings.PlayAreaCentre"/> under <see cref="GasCentrePlan.Drift"/>, and under
    /// <see cref="GasCentrePlan.PoiDestination"/> that centre <b>led toward this match's
    /// destination</b> by up to <see cref="GasSettings.PlayAreaLeadMetres"/> (D278) — so it is a
    /// per-match value, not a constant, and callers must read it from here rather than from the
    /// settings.
    /// </para>
    /// </summary>
    public GasCircle InitialCircle { get; }

    public IReadOnlyList<GasPhase> Phases => _phases;

    /// <summary>Match clock at which the last phase has fully closed.</summary>
    public long FinishedAtMs => _phases[^1].ClosedAtMs;

    /// <summary>
    /// Match clock at which the gas becomes real: the first moment a <c>ce 01</c> is drawn and the
    /// first moment anything is lethal. 0 under <see cref="GasPreMoveRing.Boundary"/>; phase 1's
    /// <see cref="GasPhase.ShrinkStartAtMs"/> otherwise.
    /// <para>
    /// <b>One field answers both questions on purpose</b> (docs/77 §6). If the draw gate and the
    /// damage gate could come apart, a player outside the play area would be burned by a circle
    /// their client is not drawing — which is the failure mode the owner's own Z1 file warns about
    /// in so many words.
    /// </para>
    /// </summary>
    public long RingLiveFromMs { get; private init; }

    /// <summary>
    /// Which of the client's nine <c>GasWeightArea</c> volumes this match is aimed at, or
    /// <c>-1</c> under <see cref="GasCentrePlan.Drift"/> (D277). Index into
    /// <see cref="Generated.AugustGasWeightAreas.All"/>; <see cref="GasWeightAreas.NameOf"/> turns
    /// it into the client's own name for a log line.
    /// </summary>
    public int DestinationAreaIndex { get; private init; } = -1;

    /// <summary>
    /// The horizontal point inside that volume the walk is aimed at. The final circle's centre lands
    /// exactly here when containment can reach it, and as far along the ray to it as containment
    /// allows when it cannot. <see cref="Vector2.Zero"/> under <see cref="GasCentrePlan.Drift"/>.
    /// </summary>
    public Vector2 Destination { get; private init; }

    /// <summary>Whether the gas may damage anyone at this match-clock time.</summary>
    public bool IsLethalAt(long matchClockMs) => matchClockMs >= RingLiveFromMs;

    /// <summary>
    /// Whether a <c>ce 01</c> ring may be drawn at this match-clock time. Identical to
    /// <see cref="IsLethalAt"/> by construction; both names exist so the two call sites read as what
    /// they are.
    /// </summary>
    public bool IsRingVisibleAt(long matchClockMs) => matchClockMs >= RingLiveFromMs;

    /// <summary>The final circle, which stays lethal for everyone outside it after the match closes.</summary>
    public GasCircle FinalCircle => _phases[^1].Target;

    /// <summary>
    /// Builds the timetable and places every centre. Both plans keep the same two invariants — a
    /// player already inside the current circle can always walk to the next one without crossing
    /// gas, and the drawn ring's leading edge is bounded by
    /// <see cref="GasSettings.LeadingEdgeSpeedCeiling"/> (docs/77 §4) — because both keep every
    /// step inside <c>f · Δr</c>.
    /// <para>
    /// <b><see cref="GasCentrePlan.PoiDestination"/> (D277, the default).</b> The destination is
    /// drawn first: one of the nine <c>GasWeightArea</c> volumes the client itself ships, weighted
    /// by <see cref="GasSettings.PoiWeightExponent"/>, then a point uniform inside that box. The
    /// play area leads toward it by the shortfall containment cannot cover, capped at
    /// <see cref="GasSettings.PlayAreaLeadMetres"/> (D278), and every phase then steps
    /// <c>s · f · Δr</c> along the one heading to it.
    /// </para>
    /// <para>
    /// <b><see cref="GasCentrePlan.Drift"/> (D62).</b> A phase's centre is drawn inside the disc of
    /// admissible centres — <c>d = f · (previousRadius − newRadius) · √u</c>, the area-uniform point
    /// of that disc scaled by <see cref="GasSettings.CentreDriftFraction"/> — at an angle jittered
    /// inside a <see cref="GasSettings.DriftConeDegrees"/> cone about <b>one heading drawn per
    /// match</b>.
    /// </para>
    /// </summary>
    public static GasSchedule Create(GasSettings settings, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        var random = new GasRandom(seed);
        var phases = new GasPhase[settings.PhaseCount];

        // --- where this match's circles are going -------------------------------------------
        //
        // D277. Under GasCentrePlan.PoiDestination the destination is drawn FIRST, out of the nine
        // GasWeightArea volumes the August client itself ships, and the play area may then lead
        // toward it (D278) by exactly the shortfall the drift budget cannot cover. Under
        // GasCentrePlan.Drift neither happens and the draw below is D62's cone walk, unchanged.
        Vector3 playCentre = settings.PlayAreaCentre;
        Vector2 destination = Vector2.Zero;
        int destinationArea = -1;
        double walkScale = 0d;
        double headingX = 0d;
        double headingZ = 0d;
        double heading = 0d;

        if (settings.CentrePlan == GasCentrePlan.PoiDestination)
        {
            destination = GasWeightAreas.DrawDestination(ref random, settings.PoiWeightExponent, out destinationArea);
            if (destinationArea >= 0)
            {
                double budget = (double)settings.CentreDriftFraction
                    * Math.Max(0d, settings.InitialRadius - (double)settings.FinalRadius);
                double dx = destination.X - playCentre.X;
                double dz = destination.Y - playCentre.Z;
                double distance = Math.Sqrt((dx * dx) + (dz * dz));
                if (distance > 0d)
                {
                    headingX = dx / distance;
                    headingZ = dz / distance;

                    // The play area leads only by what containment cannot reach. Four of the nine
                    // volumes sit further out than InitialRadius - FinalRadius, so without this the
                    // endgame provably could not land in them at any rail (docs/118 §3.2).
                    double lead = Math.Clamp(distance - budget, 0d, settings.PlayAreaLeadMetres);
                    playCentre = playCentre with
                    {
                        X = (float)(playCentre.X + (lead * headingX)),
                        Z = (float)(playCentre.Z + (lead * headingZ)),
                    };

                    double remaining = Math.Max(0d, distance - lead);
                    walkScale = budget > 0d ? Math.Min(1d, remaining / budget) : 0d;
                }
            }
        }
        else
        {
            // One heading for the whole match (docs/77 §4.4). A uniform per-phase angle makes the
            // walk a random walk, so a capped drift budget mostly cancels itself out and the endgame
            // ring sits nearly on top of the play-area centre; spending the same budget inside a
            // cone about one heading moves the median final circle from 265 m out to 510 m and lifts
            // the floor from 108 m to 378 m, at the cost of one extra draw and no change to any
            // bound.
            heading = random.NextDouble() * Math.Tau;
        }

        var initial = new GasCircle(playCentre, settings.InitialRadius);
        GasCircle previous = initial;
        long revealAt = settings.FirstRevealDelayMs;
        for (int index = 0; index < phases.Length; index++)
        {
            int number = index + 1;
            float radius = settings.RadiusForPhase(number);
            Vector3 centre = settings.CentrePlan == GasCentrePlan.PoiDestination
                ? StepTowardDestination(previous, radius, settings, walkScale, headingX, headingZ)
                : DrawContainedCentre(ref random, previous, radius, settings, heading);
            GasCircle target = new(centre, radius);

            // Hold then advance (docs/53 §5.4). Both models answer the same two questions — how
            // long the circle sits revealed but still, and how long its ring then travels — so the
            // builder needs no branch of its own; GasSettings.Pacing decides which one answers.
            uint hold = settings.HoldMsForPhase(number);
            uint window = settings.WindowForPhase(number);
            long closedAt = revealAt + window;
            long shrinkStartAt = revealAt + Math.Min(hold, window);

            phases[index] = new GasPhase(
                number,
                previous,
                target,
                revealAt,
                shrinkStartAt,
                closedAt,
                settings.DamageForPhase(number));

            previous = target;
            revealAt = closedAt + settings.PhaseHoldMs;
        }

        return new GasSchedule(settings, seed, initial, phases)
        {
            RingLiveFromMs = settings.PreMoveRing == GasPreMoveRing.Boundary ? 0L : phases[0].ShrinkStartAtMs,
            DestinationAreaIndex = destinationArea,
            Destination = destination,
        };
    }

    /// <summary>Builds the timetable from <see cref="GasSettings.Seed"/>.</summary>
    public static GasSchedule Create(GasSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Create(settings, settings.Seed);
    }

    /// <summary>
    /// 1-based index of the phase currently revealed; 0 before the first reveal. It stays at
    /// <see cref="GasSettings.PhaseCount"/> once the last phase has closed.
    /// </summary>
    public int PhaseIndexAt(long matchClockMs)
    {
        int index = 0;
        for (int i = 0; i < _phases.Length; i++)
        {
            if (matchClockMs >= _phases[i].RevealAtMs)
            {
                index = _phases[i].Index;
            }
            else
            {
                break;
            }
        }

        return index;
    }

    /// <summary>The revealed phase at a match-clock time, or null before the first reveal.</summary>
    public GasPhase? PhaseAt(long matchClockMs)
    {
        int index = PhaseIndexAt(matchClockMs);
        return index == 0 ? null : _phases[index - 1];
    }

    /// <summary>Phase by 1-based number.</summary>
    public GasPhase Phase(int index) => _phases[index - 1];

    /// <summary>
    /// The circle the client has been shown as the next safe zone — the current phase's target,
    /// or the whole play area before the first reveal.
    /// </summary>
    public GasCircle RevealedCircleAt(long matchClockMs) =>
        PhaseAt(matchClockMs) is GasPhase phase ? phase.Target : InitialCircle;

    /// <summary>
    /// The active circle: what the server damages against. Before the first reveal it is the whole
    /// play area; during a phase it is <see cref="GasPhase.CircleAt"/>; after the last phase has
    /// closed it stays on the final circle.
    /// </summary>
    public GasCircle ActiveCircleAt(long matchClockMs) =>
        PhaseAt(matchClockMs) is GasPhase phase ? phase.CircleAt(matchClockMs) : InitialCircle;

    /// <summary>True while the active circle is between two radii (the ring is travelling).</summary>
    public bool IsClosingAt(long matchClockMs) =>
        PhaseAt(matchClockMs) is GasPhase phase
        && matchClockMs >= phase.ShrinkStartAtMs
        && matchClockMs < phase.ClosedAtMs;

    /// <summary>Damage per tick in force at a match-clock time (phase 1's value before the first reveal).</summary>
    public uint DamagePerTickAt(long matchClockMs) =>
        PhaseAt(matchClockMs) is GasPhase phase ? phase.DamagePerTick : Settings.DamageForPhase(1);

    /// <summary>Whether a position is inside the damaging circle at a match-clock time.</summary>
    public bool IsInsideSafeZone(long matchClockMs, Vector3 position) =>
        ActiveCircleAt(matchClockMs).Contains(position);

    /// <summary>
    /// The next match-clock time at which anything changes — a reveal, a shrink start, or a close.
    /// <see cref="long.MaxValue"/> once the last phase has closed, which is what a caller uses to
    /// stop asking. This is a scheduling hint only: the damage tick and the rate-limited safe-zone
    /// update run off their own periods in <see cref="GasController"/>.
    /// </summary>
    public long NextEventAtMs(long matchClockMs)
    {
        foreach (GasPhase phase in _phases)
        {
            if (matchClockMs < phase.RevealAtMs)
            {
                return phase.RevealAtMs;
            }

            if (matchClockMs < phase.ShrinkStartAtMs)
            {
                return phase.ShrinkStartAtMs;
            }

            if (matchClockMs < phase.ClosedAtMs)
            {
                return phase.ClosedAtMs;
            }
        }

        return long.MaxValue;
    }

    /// <summary>
    /// D277's step: move this phase's centre along the match's one heading by its own share of the
    /// drift budget, <c>s · f · Δr</c>.
    /// <para>
    /// The step is never larger than <c>f · Δr</c> — <c>s ≤ 1</c> by construction — so it satisfies
    /// exactly the bound <see cref="GasSettings.LeadingEdgeSpeedCeiling"/> is computed against and
    /// exactly the bound <see cref="GasCircle.Contains(GasCircle)"/> needs. Summed over the ladder
    /// it is <c>s · f · (InitialRadius − FinalRadius)</c>, which is the destination when it is
    /// reachable and the closest point on the ray to it when it is not.
    /// </para>
    /// </summary>
    private static Vector3 StepTowardDestination(
        GasCircle previous,
        float radius,
        GasSettings settings,
        double walkScale,
        double headingX,
        double headingZ)
    {
        double step = (previous.Radius - (double)radius) * settings.CentreDriftFraction * walkScale;
        if (!(step > 0d))
        {
            return previous.Centre;
        }

        return previous.Centre with
        {
            X = (float)(previous.Centre.X + (step * headingX)),
            Z = (float)(previous.Centre.Z + (step * headingZ)),
        };
    }

    private static Vector3 DrawContainedCentre(
        ref GasRandom random,
        GasCircle previous,
        float radius,
        GasSettings settings,
        double heading)
    {
        // The cap is the whole of the wave-8 fix: the edge speed is v·(1 + d/Δr), so bounding d at
        // f·Δr bounds the edge at v·(1+f). It only ever tightens the admissible disc, so containment
        // is unaffected (docs/77 §4.1).
        float slack = (previous.Radius - radius) * settings.CentreDriftFraction;
        if (slack <= 0f)
        {
            return previous.Centre;
        }

        double halfCone = double.DegreesToRadians(settings.DriftConeDegrees) * 0.5d;
        double angle = heading + (((random.NextDouble() * 2d) - 1d) * halfCone);
        double distance = slack * Math.Sqrt(random.NextDouble());
        return previous.Centre with
        {
            X = (float)(previous.Centre.X + (distance * Math.Cos(angle))),
            Z = (float)(previous.Centre.Z + (distance * Math.Sin(angle))),
        };
    }
}
