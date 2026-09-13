using Cranberry.Zone.Generated;
using Cranberry.Zone.Match;

namespace Cranberry.Zone.Descent;

/// <summary>
/// How long the parachute fall should last - docs/56 §2.5, corrected by docs/115 §2.
/// <para>
/// <b>The one thing to understand before touching this type: Cranberry cannot change how fast the
/// player falls.</b> The August client simulates the parachute itself (Cranberry hands it ownership
/// through the <c>d7</c> owner tail, docs/12 §4) and uses its own <c>MoveInfo</c> rows 130 and 4001
/// for vehicle 13, whose <c>MIN_TERM_VELOCITY 10</c> / <c>MAX_TERM_VELOCITY 56</c> m/s bound the
/// whole descent. The client proves whose data it is using by sending
/// <c>88 27 CurrentMoveMode</c> <c>13</c> at Y ~ 185 m, matching row 4001's
/// <c>LANDING_GEAR_HEIGHT 200</c> (docs/56 §2.3).
/// </para>
/// <para>
/// <b>CORRECTION 2026-09-03 (docs/115 §2, AUDIT-parachute R7).</b> This type used to say the rate
/// was "a constant of the client" at 39.5-42.5 m/s. <b>It is not a constant.</b> It is a
/// <em>player-controlled band</em>: flown hard the steady rate is <b>43.5-45.8 m/s</b>, and
/// <b>hands-off it is 9.8 m/s</b> - <c>MIN_TERM_VELOCITY</c> exactly - proven three separate times
/// on 2026-09-03 (<c>logs/host-20260903-082114.log:165-201</c> 9.81 m/s, <c>-082424.log</c> 9.85,
/// <c>-083422.log</c> 9.82, each with X frozen for the whole 84 s). Every one of the six drops
/// behind the old 39.5-42.5 figure was flown hard. So <see cref="PlannedDescentMetresPerSecond"/>
/// is the mean of <em>dived</em> rides and is a <b>planning</b> number only: every server
/// <b>timeout</b> reasons with <see cref="DescentTuning.MinimumRate"/> and every "too fast" check
/// with <see cref="DescentTuning.MaximumRate"/>.
/// </para>
/// <para>
/// So "I drop down too fast" is not a rate the server can lower - <b>it is a duration</b>. D238
/// ships the release at the client's own <see cref="DropOptions.SkySpawnAltitude"/> of 850 m
/// absolute (<c>Z2Areas.xml</c> <c>KotK.SkySpawn</c>), which is <see cref="Default"/> itself; this
/// type expresses a taste change in the units the owner used - seconds in the air - and turns them
/// back into the only lever there is, the release altitude.
/// </para>
/// </summary>
public sealed record DescentSettings
{
    /// <summary>
    /// The shipped default, and <b>byte-identical to wave 4</b>: <see cref="TargetDescentSeconds"/>
    /// is 0, so <see cref="DropOptionsDescentExtensions.WithDescent"/> returns its input unchanged.
    /// </summary>
    public static readonly DescentSettings Default = new();

    /// <summary>
    /// <b>The planning mean, LIVE-MEASURED, and a measurement rather than a control</b>
    /// (docs/56 §2.2 as corrected by docs/115 §2). <b>Renamed from
    /// <c>ClientDescentMetresPerSecond</c> by D239</b>, because the old name asserted something
    /// false: 40.4 is the mean of the six captured <em>dived</em> rides, not a client constant. The
    /// honest statement is a <b>10-56 m/s player-controlled band</b> -
    /// <see cref="MeasuredHandsOffMetresPerSecond"/> to <see cref="MeasuredFlownMetresPerSecond"/>
    /// in practice. Writing a different number here does not make the client fall at it - it only
    /// changes how much altitude <see cref="TargetDescentSeconds"/> asks for, which is the one job
    /// it still has. Its rails are the client's own <c>MIN_TERM_VELOCITY 10</c> /
    /// <c>MAX_TERM_VELOCITY 56</c>.
    /// </summary>
    public float PlannedDescentMetresPerSecond { get; init; } = Rulings.Descent.PlannedDescentMetresPerSecond;

    /// <summary>
    /// <b>MEASURED, 2026-09-03.</b> What the ride costs when the player lets go of the controls:
    /// 9.81 / 9.85 / 9.82 m/s over three whole descents, i.e. the client's own
    /// <c>MIN_TERM_VELOCITY 10</c> within 2 %. This is the number every timeout must reason with,
    /// and it is why <see cref="DescentDeadline"/> was rebuilt (docs/115 §3).
    /// </summary>
    public const float MeasuredHandsOffMetresPerSecond = Rulings.Descent.MeasuredHandsOffMetresPerSecond;

    /// <summary>
    /// <b>MEASURED, 2026-09-03.</b> The top of the flown band - the steady windows of the two
    /// 17:50 and 17:59 rides ran 43.5-45.8 m/s. The shortest ride the client's physics can produce.
    /// </summary>
    public const float MeasuredFlownMetresPerSecond = Rulings.Descent.MeasuredFlownMetresPerSecond;

    /// <summary>
    /// <b>DESIGN.</b> Roughly how many seconds the fall should last. <c>0</c> - the default - leaves
    /// the drop exactly as wave 4 shipped it, at the derived 850 m sky spawn. When positive, the air
    /// spawn is raised so that the fall lasts about this long at
    /// <see cref="ClientDescentMetresPerSecond"/>.
    /// <para>
    /// <b>D238 made <c>0</c> the shipped ride</b>: the owner's 2026-09-03 ruling is "do whatever
    /// retail is", retail's own release altitude is unknown, and the only altitude the client
    /// itself states is <c>KotK.SkySpawn</c>'s 850 m. <c>Owner30</c> (30 s, ~1 250 m) and
    /// <c>Legacy36</c> (36 s, ~1 454 m) stay reachable by name as the reverts. Nothing in the
    /// August client says 30 or 36; both are taste values.
    /// </para>
    /// </summary>
    public float TargetDescentSeconds { get; init; } = Rulings.Descent.DefaultTargetDescentSeconds;

    /// <summary>True when this instance would move the air spawn at all.</summary>
    public bool ChangesAnything => TargetDescentSeconds > 0f;

    /// <summary>
    /// The metres of air the fall needs: <see cref="TargetDescentSeconds"/> x
    /// <see cref="PlannedDescentMetresPerSecond"/>, or 0 when the knob is off.
    /// </summary>
    public float RequiredClearanceMetres =>
        ChangesAnything ? TargetDescentSeconds * PlannedDescentMetresPerSecond : 0f;

    /// <summary>
    /// The air-spawn altitude this setting implies over ground at <paramref name="groundY"/> -
    /// docs/56 §2.5's formula, written out so a test can read it without a planner.
    /// <c>DropPlanner</c> computes exactly this, because
    /// <see cref="DropOptionsDescentExtensions.WithDescent"/> expresses the requirement as
    /// <see cref="DropOptions.MinimumClearanceMetres"/>, which the planner already applies as
    /// <c>max(skySpawn, groundY + clearance)</c>.
    /// </summary>
    public float AirSpawnAltitude(float groundY, float skySpawnAltitude, float minimumClearanceMetres) =>
        MathF.Max(skySpawnAltitude, groundY + MathF.Max(minimumClearanceMetres, RequiredClearanceMetres));

    /// <summary>
    /// How long a fall from <paramref name="airY"/> to <paramref name="groundY"/> takes at
    /// <see cref="PlannedDescentMetresPerSecond"/> - i.e. <b>the ride a player who dives gets</b>,
    /// and the number to compare against the <c>SynchronizedTeleport.ClientReady</c> to
    /// <c>Vehicle.Dismiss</c> interval in the host log.
    /// <para>
    /// <b>This is the fast end of the band, not "the" ride</b> (docs/115 §2). Never use it for a
    /// timeout - use <see cref="HandsOffSecondsFor"/>, which is the slow end and therefore the only
    /// honest worst case.
    /// </para>
    /// </summary>
    public float ExpectedSecondsFor(float airY, float groundY) =>
        MathF.Max(0f, airY - groundY) / PlannedDescentMetresPerSecond;

    /// <summary>
    /// <b>The worst case the client's own physics allow</b>: the same fall at
    /// <see cref="DescentTuning.MinimumRate"/> (<c>MIN_TERM_VELOCITY</c> 10 m/s), which is what a
    /// player who never touches the controls actually gets - 9.8 m/s measured three times on
    /// 2026-09-03. At the shipped 850 m release that is ~85 s against ~21 s dived.
    /// <para>
    /// It deliberately does not read <see cref="PlannedDescentMetresPerSecond"/>: the hands-off
    /// worst case is a property of the client, not of this server's planning knob, and a lane that
    /// reasons about a timeout with the planning knob is exactly the defect docs/115 §3 fixes.
    /// </para>
    /// </summary>
    public static float HandsOffSecondsFor(float airY, float groundY) =>
        MathF.Max(0f, airY - groundY) / DescentTuning.MinimumRate;

    /// <summary>Throws when a value is outside the range the drop can honour.</summary>
    public void Validate()
    {
        Require(
            PlannedDescentMetresPerSecond >= DescentTuning.MinimumRate
                && PlannedDescentMetresPerSecond <= DescentTuning.MaximumRate,
            nameof(PlannedDescentMetresPerSecond),
            $"must be within the client's own MIN_TERM_VELOCITY {DescentTuning.MinimumRate:F0} .. "
                + $"MAX_TERM_VELOCITY {DescentTuning.MaximumRate:F0} m/s");
        Require(
            TargetDescentSeconds >= 0f
                && TargetDescentSeconds <= DescentTuning.MaximumSeconds
                && float.IsFinite(TargetDescentSeconds),
            nameof(TargetDescentSeconds),
            $"must be finite and within 0 .. {DescentTuning.MaximumSeconds:F0} s");
    }

    private static void Require(bool condition, string name, string requirement)
    {
        if (!condition)
        {
            throw new ArgumentOutOfRangeException(name, $"DescentSettings.{name} {requirement}.");
        }
    }
}

/// <summary>
/// Applies a <see cref="DescentSettings"/> to the drop's own options.
/// <para>
/// This is an extension rather than two more fields on <see cref="DropOptions"/> because that record
/// belongs to the drop lane; the one-line host change is written out in docs/56 <c>## Integration</c>.
/// </para>
/// </summary>
public static class DropOptionsDescentExtensions
{
    /// <summary>
    /// Returns <paramref name="options"/> with enough <see cref="DropOptions.MinimumClearanceMetres"/>
    /// for <paramref name="descent"/>'s target time in the air.
    /// <para>
    /// <b>Why the clearance and not the altitude.</b> <c>DropPlanner</c> already computes
    /// <c>air.Y = max(SkySpawnAltitude, groundY + MinimumClearanceMetres)</c>, which is
    /// docs/56 §2.5's formula exactly - so asking for "1 212 m of air under the player" is the same
    /// statement as "raise the release point to <c>groundY + 30 s x 40.4 m/s</c>", and it leaves the
    /// client's own <see cref="DropOptions.SkySpawnAltitude"/> of 850 m untouched as the floor. Over
    /// Z2's highest anchor (241.9 m) 30 s gives 1 453.9 m; over its lowest, 1 212 m.
    /// </para>
    /// <para>
    /// With <see cref="DescentSettings.TargetDescentSeconds"/> at 0 this returns the <em>same
    /// instance</em> - which since D238 is what a default boot does, so the shipped drop releases at
    /// the client's own 850 m slab and the match-zoning regression guard cannot be touched by wiring
    /// this in.
    /// </para>
    /// </summary>
    public static DropOptions WithDescent(this DropOptions options, DescentSettings descent)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(descent);
        descent.Validate();

        if (!descent.ChangesAnything)
        {
            return options;
        }

        float clearance = MathF.Max(options.MinimumClearanceMetres, descent.RequiredClearanceMetres);
        if (clearance == options.MinimumClearanceMetres)
        {
            return options;
        }

        return options with { MinimumClearanceMetres = clearance };
    }
}
