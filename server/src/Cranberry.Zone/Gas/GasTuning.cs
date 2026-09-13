using System.Globalization;
using System.Numerics;
using System.Text;

namespace Cranberry.Zone.Gas;

/// <summary>
/// Named gas presets and the <c>CRANBERRY_GAS_*</c> environment overrides — docs/53 §Integration 6,
/// the same shape as <c>CRANBERRY_MOVE_*</c>'s <see cref="Movement.MovementTuning"/> (docs/49 §I2)
/// and <c>CRANBERRY_SKY</c>'s <c>EnvironmentPresets</c> (docs/38 §5).
/// <para>
/// The August client does not supply a server phase table. Recorded retail matches and declared
/// reconstruction choices supply the defaults. Operators can tune them without rebuilding;
/// <c>CRANBERRY_GAS_SCALE=0.2</c> plays the same geometry and damage at one fifth of the clock.
/// </para>
/// <para>
/// <b>Order of application:</b> preset, then <c>CRANBERRY_GAS_SCALE</c>, then
/// <c>CRANBERRY_GAS_DAMAGE_SCALE</c>, then each per-value override on top. Every value is range
/// checked; anything unparsable or out of range is <b>ignored with a note</b> and never throws, so a
/// typo in a launch script cannot brick a match or stop the host.
/// </para>
/// </summary>
public static class GasTuning
{
    /// <summary>Selects the preset. Unset or unknown ⇒ <see cref="Aug2017Retail"/>.</summary>
    public const string PresetVariable = "CRANBERRY_GAS_PRESET";

    /// <summary>Selects <see cref="GasSettings.Pacing"/> by name. Unset ⇒ the preset's own value.</summary>
    public const string PacingVariable = "CRANBERRY_GAS_PACING";

    /// <summary>
    /// Selects <see cref="GasSettings.PreMoveRing"/> by name — <c>None</c>, <c>ZeroRadius</c> or
    /// <c>Boundary</c> (docs/77 section 6). Unset ⇒ the preset's own value.
    /// </summary>
    public const string PreMoveRingVariable = "CRANBERRY_GAS_PRE_MOVE_RING";

    /// <summary>
    /// Selects <see cref="GasSettings.CentrePlan"/> by name — <c>PoiDestination</c> (D277) or
    /// <c>Drift</c> (D62's cone walk). Unset ⇒ the preset's own value.
    /// </summary>
    public const string CentrePlanVariable = "CRANBERRY_GAS_CENTRE_PLAN";

    /// <summary>
    /// Selects <see cref="GasSettings.RingBlendMode"/> by name — <c>SendPeriod</c> (D280) or
    /// <c>Fixed</c>. Unset ⇒ the preset's own value.
    /// </summary>
    public const string BlendModeVariable = "CRANBERRY_GAS_BLEND_MODE";

    /// <summary>
    /// Multiplies every value that measures time (<see cref="GasSettings.ScaledBy"/>), leaving the
    /// radii and the damage table alone. <c>0.2</c> plays the configured ten-wave continuation in
    /// 6:06, allowing every reveal to be checked in one short match.
    /// </summary>
    public const string ScaleVariable = "CRANBERRY_GAS_SCALE";

    /// <summary>Multiplies every entry of <see cref="GasSettings.DamagePerPhase"/>.</summary>
    public const string DamageScaleVariable = "CRANBERRY_GAS_DAMAGE_SCALE";

    /// <summary>Smallest and largest accepted <see cref="ScaleVariable"/> / <see cref="DamageScaleVariable"/>.</summary>
    public const float MinimumScale = 0.005f;

    /// <inheritdoc cref="MinimumScale"/>
    public const float MaximumScale = 20f;

    /// <summary>
    /// The default August settings. Recorded matches establish the first safe radius at 2000 m;
    /// the configured radius and timing tables carry the full selected schedule. Client-derived
    /// POI volumes and toxicity are combined with the declared project settings. The preset name
    /// does not establish that every placement and damage rule has been recovered from retail.
    /// </summary>
    public static GasSettings Aug2017Retail { get; } = new();

    /// <summary>
    /// <b>The exact wave-4 schedule</b> — five phases, 6 000 → 15 m, revealed at 2:00, moving at
    /// 2:30, an 11:30 match and the linear 90/135/180/225/270 damage curve — so the change this wave
    /// makes can be A/B'd in place against what the owner already play-tested. This is the gas
    /// lane's equivalent of <c>CRANBERRY_SKY=LegacyD18Grey</c> and
    /// <c>CRANBERRY_MOVE_PRESET=Wave3Legacy</c>. Its phase-1 wall travels 27.9 m/s.
    /// </summary>
    public static GasSettings Wave4Legacy { get; } = new()
    {
        Pacing = GasPacing.FixedWindows,
        PhaseCount = 5,
        FinalRadius = 15f,
        DamagePerPhase = [],
        DamageAtFullToxicityPerPhase = [],
        // Wave 8 moved four defaults this preset must NOT inherit, or it stops being wave 4: the
        // 6 000 m play area on the world origin, and the uncapped uniform centre draw. The A/B is
        // only worth having if it is like-for-like. (The per-match drift heading is one extra
        // GasRandom draw, so the circle POSITIONS are one draw out of step with what wave 4 shipped;
        // the timings, the radii and the damage curve — what the A/B exists for — are identical.)
        InitialRadius = 6000f,
        PlayAreaCentre = Vector3.Zero,
        CentreDriftFraction = 1f,
        DriftConeDegrees = 360f,
        PreMoveRing = GasPreMoveRing.Boundary,
        // Wave 4's RadiusForPhase returned the RAW geometric radius; RadiusRoundingMetres = 5 is new
        // this wave and the preset inherits every default it does not override. Without this line
        // "the exact wave-4 schedule" reproduced the timings and the damage to the millisecond but
        // shipped 1810/545/165/50/15 against wave 4's 1810.253/546.169/164.784/49.717/15 — and,
        // because GasSchedule.DrawContainedCentre derives its offset from
        // (previous.Radius - radius), it walked every circle CENTRE off the play-tested positions
        // too. The A/B is only worth having if it is like-for-like (wave-5 verify pass).
        RadiusRoundingMetres = 0f,
        // D285's Z1 radius ladder is a fourth default this preset must not inherit: wave 4 ran its
        // own 6000 -> 15 geometric radii over five phases, so an empty ladder keeps RadiusForPhase
        // on the geometric path here.
        RadiusLadder = [],
        // D276-D280 move three more defaults this preset must not inherit, for the same reason: it
        // is the wave-4 schedule, and wave 4 had no POI destinations, a flat blend constant and no
        // toxicity meter.
        CentrePlan = GasCentrePlan.Drift,
        RingBlendMode = GasRingBlendMode.Fixed,
        SendToxicity = false,
    };

    /// <summary>
    /// The existing foot-speed comparison preset: 4200 to 40 m over ten geometric phases,
    /// first reveal at 2:00, movement at 4:30, 16-second later holds and a 3.866 m/s radius rate.
    /// Its 0.20 centre drift and 5 m/s leading-edge limit keep the original cone walk. These values
    /// are fixed here so subsequent retail-table corrections do not alter the comparison preset.
    /// </summary>
    public static GasSettings FootRail { get; } = new()
    {
        Pacing = GasPacing.SpeedPaced,
        InitialRadius = 4200f,
        FinalRadius = 40f,
        PhaseCount = 10,
        FirstRevealDelayMs = 120_000,
        FirstMoveDelayMs = 270_000,
        InterPhaseHoldMs = 16_000,
        ShrinkSpeedMetresPerSecond = 3.866f,
        TargetMatchLengthMs = 1_490_000,
        RadiusRoundingMetres = 5f,
        AdvanceRoundingMs = 100,
        HoldDurationsMs = [],
        AdvanceDurationsMs = [],
        CentrePlan = GasCentrePlan.Drift,
        CentreDriftFraction = 0.2f,
        MaxEdgeSpeedMetresPerSecond = 5f,
        PlayAreaLeadMetres = 0f,
        RadiusLadder = [],
    };

    /// <summary>
    /// <see cref="Aug2017Retail"/> at one fifth of the clock, preserving the radii and damage.
    /// Identical to <c>CRANBERRY_GAS_SCALE=0.2</c>.
    /// </summary>
    public static GasSettings Sprint { get; } = Aug2017Retail.ScaledBy(0.2f);

    /// <summary>Every preset by name, in the order they are offered to the owner.</summary>
    public static IReadOnlyList<(string Name, GasSettings Settings)> All { get; } =
    [
        ("Aug2017Retail", Aug2017Retail),
        ("Wave4Legacy", Wave4Legacy),
        ("Sprint", Sprint),
        ("FootRail", FootRail),
    ];

    /// <summary>The preset names, for a log line or an error message.</summary>
    public static IReadOnlyList<string> PresetNames { get; } = All.Select(p => p.Name).ToArray();

    /// <summary>
    /// The per-value overrides of docs/53 §Integration 6. Each is a number in invariant culture with
    /// an inclusive range; the ranges are sanity rails, not client facts — D23 proved the client
    /// constrains none of these.
    /// </summary>
    public static IReadOnlyList<GasKnob> Knobs { get; } =
    [
        new("CRANBERRY_GAS_PHASES", nameof(GasSettings.PhaseCount), 1f, 20f,
            (s, v) => s with { PhaseCount = (int)MathF.Round(v) }, s => s.PhaseCount),
        new("CRANBERRY_GAS_FIRST_REVEAL_MS", nameof(GasSettings.FirstRevealDelayMs), 0f, 3_600_000f,
            (s, v) => s with { FirstRevealDelayMs = Ms(v) }, s => s.FirstRevealDelayMs),
        new("CRANBERRY_GAS_FIRST_MOVE_MS", nameof(GasSettings.FirstMoveDelayMs), 0f, 3_600_000f,
            (s, v) => s with { FirstMoveDelayMs = Ms(v) }, s => s.FirstMoveDelayMs),
        new("CRANBERRY_GAS_HOLD_MS", nameof(GasSettings.InterPhaseHoldMs), 0f, 600_000f,
            (s, v) => s with { InterPhaseHoldMs = Ms(v) }, s => s.InterPhaseHoldMs),
        new("CRANBERRY_GAS_WALL_SPEED", nameof(GasSettings.ShrinkSpeedMetresPerSecond), 0.1f, 100f,
            (s, v) => s with { ShrinkSpeedMetresPerSecond = v }, s => s.ShrinkSpeedMetresPerSecond),
        new("CRANBERRY_GAS_FINAL_RADIUS_M", nameof(GasSettings.FinalRadius), 1f, 3_000f,
            (s, v) => s with { FinalRadius = v }, s => s.FinalRadius),

        // docs/77 section 8. The play area is a first-class knob at last: until wave 8
        // CRANBERRY_GAS_INITIAL_RADIUS_M was hand-rolled in Program.cs and IGNORED unless
        // CRANBERRY_GAS_CENTRE_ON_SPAWN was also set, because alone it shrank a circle still pinned
        // to the world origin and left the drop outside it. PlayAreaCentre being tunable removes
        // that reason. Re-solve the wall speed when either moves, exactly as CRANBERRY_GAS_WALL_SPEED
        // warns: GasSettings.SolvedShrinkSpeedMetresPerSecond() is the identity.
        new("CRANBERRY_GAS_INITIAL_RADIUS_M", nameof(GasSettings.InitialRadius), 100f, 10_000f,
            (s, v) => s with { InitialRadius = v }, s => s.InitialRadius),
        new("CRANBERRY_GAS_CENTRE_X", "PlayAreaCentre.X", -8_192f, 8_192f,
            (s, v) => s with { PlayAreaCentre = s.PlayAreaCentre with { X = v } }, s => s.PlayAreaCentre.X),
        new("CRANBERRY_GAS_CENTRE_Z", "PlayAreaCentre.Z", -8_192f, 8_192f,
            (s, v) => s with { PlayAreaCentre = s.PlayAreaCentre with { Z = v } }, s => s.PlayAreaCentre.Z),
        new("CRANBERRY_GAS_DRIFT", nameof(GasSettings.CentreDriftFraction), 0.01f, 1f,
            (s, v) => s with { CentreDriftFraction = v }, s => s.CentreDriftFraction),
        new("CRANBERRY_GAS_DRIFT_CONE", nameof(GasSettings.DriftConeDegrees), 1f, 360f,
            (s, v) => s with { DriftConeDegrees = v }, s => s.DriftConeDegrees),
        new("CRANBERRY_GAS_MAX_EDGE", nameof(GasSettings.MaxEdgeSpeedMetresPerSecond), 1f, 50f,
            (s, v) => s with { MaxEdgeSpeedMetresPerSecond = v }, s => s.MaxEdgeSpeedMetresPerSecond),
        new("CRANBERRY_GAS_RADIUS_ROUND_M", nameof(GasSettings.RadiusRoundingMetres), 0f, 100f,
            (s, v) => s with { RadiusRoundingMetres = v }, s => s.RadiusRoundingMetres),

        // The inherited table rescales between requested radius bounds; changing the phase
        // count selects geometric sizing. 0 always selects geometric sizing, while 1 retains
        // the preset's mode. The startup summary reports the effective target radii.
        new("CRANBERRY_GAS_RADIUS_LADDER", nameof(GasSettings.RadiusLadder), 0f, 1f,
            (s, v) => v >= 0.5f ? s : s with { RadiusLadder = [] },
            s => s.RadiusLadder.Count > 0 ? 1f : 0f),
        new("CRANBERRY_GAS_TICK_MS", nameof(GasSettings.TickPeriodMs), 100f, 10_000f,
            (s, v) => s with { TickPeriodMs = Ms(v) }, s => s.TickPeriodMs),
        new("CRANBERRY_GAS_UPDATE_MS", nameof(GasSettings.SafeZoneUpdateIntervalMs), 50f, 5_000f,
            (s, v) => s with { SafeZoneUpdateIntervalMs = Ms(v) }, s => s.SafeZoneUpdateIntervalMs),
        new("CRANBERRY_GAS_BLEND_MS", nameof(GasSettings.RingBlendMs), 1f, 60_000f,
            (s, v) => s with { RingBlendMs = Ms(v) }, s => s.RingBlendMs),

        // Wave 9, docs/87 §7 edit 6. Both heals accept 0 = OFF, which is the point: setting
        // CRANBERRY_GAS_HUD_HEAL_MS=0 restores wave 8's transition-only countdown for a controlled
        // A/B against the session that reported no gas, without a rebuild. They are reverts, not
        // opt-ins — the shipped defaults are 1 000 ms and 15 000 ms.
        new("CRANBERRY_GAS_HUD_HEAL_MS", nameof(GasSettings.HudHealIntervalMs), 0f, 60_000f,
            (s, v) => s with { HudHealIntervalMs = Ms(v) }, s => s.HudHealIntervalMs),
        new("CRANBERRY_GAS_SAFEZONE_HEAL_MS", nameof(GasSettings.SafeZoneHealIntervalMs), 0f, 600_000f,
            (s, v) => s with { SafeZoneHealIntervalMs = Ms(v) }, s => s.SafeZoneHealIntervalMs),
        new("CRANBERRY_GAS_BANNERS", nameof(GasSettings.SendBanners), 0f, 1f,
            (s, v) => s with { SendBanners = v >= 0.5f }, s => s.SendBanners ? 1f : 0f),
        new("CRANBERRY_GAS_POPULATION_ADAPTIVE", nameof(GasSettings.PopulationAdaptive), 0f, 1f,
            (s, v) => s with { PopulationAdaptive = v >= 0.5f }, s => s.PopulationAdaptive ? 1f : 0f),

        // D277/D278, docs/118 §3. The exponent decides how hard the nine client volumes are
        // weighted by their own size (0 = flat), and the lead decides how far the play area may move
        // toward a destination containment cannot otherwise reach. Both accept 0, and 0 on the lead
        // is exactly "give the four corner volumes up".
        new("CRANBERRY_GAS_POI_EXPONENT", nameof(GasSettings.PoiWeightExponent), 0f, 4f,
            (s, v) => s with { PoiWeightExponent = v }, s => s.PoiWeightExponent),
        new("CRANBERRY_GAS_PLAY_AREA_LEAD_M", nameof(GasSettings.PlayAreaLeadMetres), 0f, 4_000f,
            (s, v) => s with { PlayAreaLeadMetres = v }, s => s.PlayAreaLeadMetres),

        // D279, docs/118 §4. CRANBERRY_GAS_TOXICITY=0 is the revert: with it off not one extra byte
        // goes on the wire.
        new("CRANBERRY_GAS_TOXICITY", nameof(GasSettings.SendToxicity), 0f, 1f,
            (s, v) => s with { SendToxicity = v >= 0.5f }, s => s.SendToxicity ? 1f : 0f),
        new("CRANBERRY_GAS_TOXICITY_DRAIN", nameof(GasSettings.ToxicityDrainPerSecond), 0f, 180_000f,
            (s, v) => s with { ToxicityDrainPerSecond = Ms(v) }, s => s.ToxicityDrainPerSecond),
    ];

    /// <summary>
    /// One environment-overridable value: which variable names it, which property it writes, and the
    /// inclusive range outside which it is refused.
    /// </summary>
    /// <param name="Variable">The environment variable, e.g. <c>CRANBERRY_GAS_WALL_SPEED</c>.</param>
    /// <param name="Property">The <see cref="GasSettings"/> property it writes.</param>
    /// <param name="Minimum">Smallest accepted value, inclusive.</param>
    /// <param name="Maximum">Largest accepted value, inclusive.</param>
    /// <param name="Apply">Returns a copy of the settings with this value replaced.</param>
    /// <param name="Read">Reads the current value out of a settings record.</param>
    public sealed record GasKnob(
        string Variable,
        string Property,
        float Minimum,
        float Maximum,
        Func<GasSettings, float, GasSettings> Apply,
        Func<GasSettings, float> Read);

    /// <summary>
    /// The preset called <paramref name="name"/> (case-insensitive), or <see cref="Aug2017Retail"/>
    /// when the name is null, empty or unknown. Never throws — an unknown name must not stop a host.
    /// </summary>
    public static GasSettings FromNameOrDefault(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Aug2017Retail;
        }

        foreach ((string presetName, GasSettings settings) in All)
        {
            if (string.Equals(presetName, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return settings;
            }
        }

        return Aug2017Retail;
    }

    /// <summary>True when <paramref name="name"/> names a preset.</summary>
    public static bool IsKnownPreset(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && All.Any(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The name of a preset by value, or "custom" once an override has moved anything.</summary>
    public static string NameOf(GasSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach ((string name, GasSettings preset) in All)
        {
            if (SamePresetValues(preset, settings))
            {
                return name;
            }
        }

        return "custom";
    }

    private static bool SamePresetValues(GasSettings preset, GasSettings settings) =>
        preset == settings
        || (preset.HoldDurationsMs.SequenceEqual(settings.HoldDurationsMs)
            && preset.AdvanceDurationsMs.SequenceEqual(settings.AdvanceDurationsMs)
            && preset == settings with
            {
                HoldDurationsMs = preset.HoldDurationsMs,
                AdvanceDurationsMs = preset.AdvanceDurationsMs,
            });

    /// <summary>
    /// The settings a host should run: the preset first, then <see cref="ScaleVariable"/>, then
    /// <see cref="DamageScaleVariable"/>, then every <see cref="Knobs"/> override the environment
    /// sets, on top.
    /// <para>
    /// <paramref name="note"/> is null when the environment asked for nothing (so a quiet default
    /// boot logs nothing extra); otherwise it is a one-line summary naming the preset, every applied
    /// override, and every rejected one with the reason. <b>Nothing here throws</b> — a bad value in
    /// a launch script is reported and skipped, and a combination that
    /// <see cref="GasSettings.Validate"/> refuses falls back to the preset rather than taking the
    /// host down.
    /// </para>
    /// </summary>
    /// <param name="read">Usually <c>Environment.GetEnvironmentVariable</c>.</param>
    /// <param name="note">A human-readable summary for the host log, or null when nothing was set.</param>
    public static GasSettings FromEnvironment(Func<string, string?> read, out string? note)
    {
        ArgumentNullException.ThrowIfNull(read);

        string? presetName = read(PresetVariable);
        GasSettings settings = FromNameOrDefault(presetName);

        var applied = new List<string>();
        var rejected = new List<string>();
        GasPacing? explicitPacing = null;
        bool firstRevealApplied = false;
        bool firstMoveApplied = false;
        bool holdApplied = false;

        bool presetRequested = !string.IsNullOrWhiteSpace(presetName);
        if (presetRequested && !IsKnownPreset(presetName))
        {
            rejected.Add($"{PresetVariable}='{presetName!.Trim()}' (unknown; known: {string.Join(", ", PresetNames)})");
        }

        string? pacingName = read(PacingVariable);
        if (!string.IsNullOrWhiteSpace(pacingName))
        {
            if (Enum.TryParse(pacingName.Trim(), ignoreCase: true, out GasPacing pacing)
                && Enum.IsDefined(pacing))
            {
                settings = settings with { Pacing = pacing };
                explicitPacing = pacing;
                applied.Add($"{nameof(GasSettings.Pacing)}={pacing}");
            }
            else
            {
                rejected.Add(
                    $"{PacingVariable}='{pacingName.Trim()}' (unknown; known: "
                    + $"{nameof(GasPacing.SpeedPaced)}, {nameof(GasPacing.FixedWindows)}, {nameof(GasPacing.PhaseTable)})");
            }
        }

        string? preMoveName = read(PreMoveRingVariable);
        if (!string.IsNullOrWhiteSpace(preMoveName))
        {
            if (Enum.TryParse(preMoveName.Trim(), ignoreCase: true, out GasPreMoveRing preMove)
                && Enum.IsDefined(preMove))
            {
                settings = settings with { PreMoveRing = preMove };
                applied.Add($"{nameof(GasSettings.PreMoveRing)}={preMove}");
            }
            else
            {
                rejected.Add(
                    $"{PreMoveRingVariable}='{preMoveName.Trim()}' (unknown; known: "
                    + $"{nameof(GasPreMoveRing.None)}, {nameof(GasPreMoveRing.ZeroRadius)}, "
                    + $"{nameof(GasPreMoveRing.Boundary)})");
            }
        }

        string? centrePlanName = read(CentrePlanVariable);
        if (!string.IsNullOrWhiteSpace(centrePlanName))
        {
            if (Enum.TryParse(centrePlanName.Trim(), ignoreCase: true, out GasCentrePlan centrePlan)
                && Enum.IsDefined(centrePlan))
            {
                settings = settings with { CentrePlan = centrePlan };
                applied.Add($"{nameof(GasSettings.CentrePlan)}={centrePlan}");
            }
            else
            {
                rejected.Add(
                    $"{CentrePlanVariable}='{centrePlanName.Trim()}' (unknown; known: "
                    + $"{nameof(GasCentrePlan.PoiDestination)}, {nameof(GasCentrePlan.Drift)})");
            }
        }

        string? blendModeName = read(BlendModeVariable);
        if (!string.IsNullOrWhiteSpace(blendModeName))
        {
            if (Enum.TryParse(blendModeName.Trim(), ignoreCase: true, out GasRingBlendMode blendMode)
                && Enum.IsDefined(blendMode))
            {
                settings = settings with { RingBlendMode = blendMode };
                applied.Add($"{nameof(GasSettings.RingBlendMode)}={blendMode}");
            }
            else
            {
                rejected.Add(
                    $"{BlendModeVariable}='{blendModeName.Trim()}' (unknown; known: "
                    + $"{nameof(GasRingBlendMode.SendPeriod)}, {nameof(GasRingBlendMode.Fixed)})");
            }
        }

        if (TryReadScale(read, ScaleVariable, applied, rejected, out float scale))
        {
            settings = settings.ScaledBy(scale);
        }

        if (TryReadScale(read, DamageScaleVariable, applied, rejected, out float damageScale)
            && settings.DamagePerPhase is { Count: > 0 } damage)
        {
            uint[] scaled = new uint[damage.Count];
            for (int index = 0; index < damage.Count; index++)
            {
                scaled[index] = (uint)Math.Clamp(
                    Math.Round(damage[index] * (double)damageScale, MidpointRounding.AwayFromZero),
                    0d,
                    uint.MaxValue);
            }

            settings = settings with { DamagePerPhase = scaled };

            uint[] toxic = new uint[settings.DamageAtFullToxicityPerPhase.Count];
            for (int index = 0; index < toxic.Length; index++)
            {
                toxic[index] = (uint)Math.Clamp(
                    Math.Round(settings.DamageAtFullToxicityPerPhase[index] * (double)damageScale,
                        MidpointRounding.AwayFromZero), 0d, uint.MaxValue);
            }

            settings = settings with { DamageAtFullToxicityPerPhase = toxic };
        }

        foreach (GasKnob knob in Knobs)
        {
            string? raw = read(knob.Variable);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                || !float.IsFinite(value))
            {
                rejected.Add($"{knob.Variable}='{raw.Trim()}' (not a number)");
                continue;
            }

            if (value < knob.Minimum || value > knob.Maximum)
            {
                rejected.Add(
                    $"{knob.Variable}={Number(value)} (outside {Number(knob.Minimum)}..{Number(knob.Maximum)})");
                continue;
            }

            bool changesPhaseCount = knob.Property == nameof(GasSettings.PhaseCount)
                && (int)MathF.Round(value) != settings.PhaseCount;
            bool changesWallSpeed = knob.Property == nameof(GasSettings.ShrinkSpeedMetresPerSecond)
                && (explicitPacing != GasPacing.PhaseTable || value != settings.ShrinkSpeedMetresPerSecond);
            bool selectsGeometricRadii = knob.Property == nameof(GasSettings.RadiusLadder)
                && value < 0.5f && settings.RadiusLadder.Count > 0;
            // Generated configuration files contain every scalar default. Repeating the current
            // representative hold must preserve a varying table rather than flattening it.
            bool changesLaterHold = knob.Property == nameof(GasSettings.InterPhaseHoldMs)
                && Ms(value) != settings.InterPhaseHoldMs;
            if (settings.Pacing == GasPacing.PhaseTable
                && (changesPhaseCount || changesWallSpeed || selectsGeometricRadii))
            {
                if (explicitPacing == GasPacing.PhaseTable)
                {
                    rejected.Add($"{knob.Variable}={Number(value)} conflicts with explicit {nameof(GasPacing.PhaseTable)} pacing; the table is retained");
                    continue;
                }

                settings = settings with { Pacing = GasPacing.SpeedPaced };
                if (changesPhaseCount)
                {
                    settings = settings with { RadiusLadder = [] };
                }

                applied.Add($"{nameof(GasSettings.Pacing)}={nameof(GasPacing.SpeedPaced)} ({knob.Variable} selects derived timing)");
            }

            settings = knob.Apply(settings, value);
            firstRevealApplied |= knob.Property == nameof(GasSettings.FirstRevealDelayMs);
            firstMoveApplied |= knob.Property == nameof(GasSettings.FirstMoveDelayMs);
            holdApplied |= changesLaterHold;
            applied.Add($"{knob.Property}={Number(value)}");
        }

        // A knob can only move one value inside its own rail, but the combination can still be
        // impossible — a first-move earlier than the reveal, a final radius above the initial one.
        // Fall back to the preset rather than letting a launch script stop the host.
        try
        {
            if (settings.Pacing == GasPacing.PhaseTable && (firstRevealApplied || firstMoveApplied || holdApplied))
            {
                if (settings.HoldDurationsMs.Count != settings.PhaseCount)
                {
                    throw new ArgumentException("The selected phase table does not match the phase count.", nameof(GasSettings.HoldDurationsMs));
                }

                uint[] holds = settings.HoldDurationsMs.ToArray();
                if (firstRevealApplied || firstMoveApplied)
                {
                    if (settings.FirstMoveDelayMs < settings.FirstRevealDelayMs)
                    {
                        throw new ArgumentOutOfRangeException(nameof(GasSettings.FirstMoveDelayMs),
                            "The first movement cannot occur before the first safe-zone reveal.");
                    }

                    holds[0] = settings.FirstMoveDelayMs - settings.FirstRevealDelayMs;
                }

                if (holdApplied)
                {
                    Array.Fill(holds, settings.InterPhaseHoldMs, 1, holds.Length - 1);
                }

                settings = settings with { HoldDurationsMs = holds };
            }

            settings.Validate();
        }
        catch (ArgumentException ex)
        {
            rejected.Add($"the tuned schedule was refused ({ex.Message}); the preset is used unchanged");
            settings = FromNameOrDefault(presetName);
            applied.Clear();
        }

        // Scaling table durations creates new arrays. Reuse a matching preset so preset identity
        // and the effective-config name remain stable for e.g. SCALE=0.2 versus PRESET=Sprint.
        foreach ((string _, GasSettings preset) in All)
        {
            if (SamePresetValues(preset, settings))
            {
                settings = preset;
                break;
            }
        }

        note = BuildNote(presetRequested, presetName, applied, rejected, settings);
        return settings;
    }

    /// <summary>
    /// The one-line "what a match will actually do" summary, worth logging beside the first
    /// <c>ce 01</c>. Includes the effective timetable and the complete revealed-radius sequence.
    /// </summary>
    public static string DescribeSchedule(GasSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        int phases = Math.Max(settings.PhaseCount, 1);
        long reveal = settings.FirstRevealDelayMs;
        long firstMove = reveal + settings.HoldMsForPhase(1);
        long finish = reveal;
        for (int phase = 1; phase <= phases; phase++)
        {
            finish += settings.WindowForPhase(phase);
            if (phase < phases)
            {
                finish += settings.PhaseHoldMs;
            }
        }

        uint advance = settings.AdvanceMsForPhase(1);
        double wall = advance == 0
            ? 0d
            : (settings.InitialRadius - (double)settings.RadiusForPhase(1)) / (advance / 1000d);

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} waves {1:0.#}→{2:0.#} m at ({3:0.#}, {4:0.#}) ({5}); reveal {6}, first move {7}, "
            + "wall {8:0.00} m/s, leading edge <= {9:0.00} m/s, match {10}; damage {11}→{12} per "
            + "{13} ms tick; pre-move ring {14}; hud heal {15}, safe-zone heal {16}, banners {17}; "
            + "centres {18} (budget {19:0} m, POI weight ^{20:0.##}, play-area lead {21:0} m); "
            + "ce 01 blend {22}; toxicity {23}; first safe radius {24:0.###} m "
            + "(diameter {25:0.###} m); target radii [{26}] m ({27})",
            phases,
            settings.InitialRadius,
            settings.FinalRadius,
            settings.PlayAreaCentre.X,
            settings.PlayAreaCentre.Z,
            settings.Pacing,
            Clock(reveal),
            Clock(firstMove),
            wall,
            settings.LeadingEdgeSpeedCeiling(),
            Clock(finish),
            settings.DamageForPhase(1),
            settings.DamageForPhase(phases),
            settings.TickPeriodMs,
            settings.PreMoveRing,
            settings.HudHealIntervalMs == 0 ? "off" : settings.HudHealIntervalMs + " ms",
            settings.SafeZoneHealIntervalMs == 0 ? "off" : settings.SafeZoneHealIntervalMs + " ms",
            settings.SendBanners ? "on" : "off",
            settings.CentrePlan,
            settings.CentreDriftFraction * (settings.InitialRadius - (double)settings.FinalRadius),
            settings.PoiWeightExponent,
            settings.PlayAreaLeadMetres,
            settings.RingBlendMode == GasRingBlendMode.SendPeriod
                ? $"send period ({settings.SafeZoneUpdateIntervalMs} ms advancing, {settings.RingBlendMs} ms otherwise)"
                : $"fixed {settings.RingBlendMs} ms",
            settings.SendToxicity
                ? $"on (resource {settings.ToxicityResourceId}, {settings.ToxicityMaxValue} full, "
                  + $"+{settings.ToxicityFillPerSecond}/s in gas, -{settings.ToxicityDrainPerSecond}/s out)"
                : "off",
            settings.RadiusForPhase(1),
            settings.RadiusForPhase(1) * 2f,
            string.Join(", ", Enumerable.Range(1, phases).Select(p => Number(settings.RadiusForPhase(p)))),
            settings.RadiusLadder.Count > 0 ? "table" : "geometric");
    }

    /// <summary>A match-clock millisecond value as <c>m:ss</c>, for a log line or a doc table.</summary>
    public static string Clock(long milliseconds)
    {
        long totalSeconds = milliseconds / 1000;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}:{1:00}",
            totalSeconds / 60,
            totalSeconds % 60);
    }

    private static uint Ms(float value) => (uint)Math.Max(0d, Math.Round(value, MidpointRounding.AwayFromZero));

    private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool TryReadScale(
        Func<string, string?> read,
        string variable,
        List<string> applied,
        List<string> rejected,
        out float scale)
    {
        scale = 1f;
        string? raw = read(variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            || !float.IsFinite(value))
        {
            rejected.Add($"{variable}='{raw.Trim()}' (not a number)");
            return false;
        }

        if (value < MinimumScale || value > MaximumScale)
        {
            rejected.Add($"{variable}={Number(value)} (outside {Number(MinimumScale)}..{Number(MaximumScale)})");
            return false;
        }

        if (value == 1f)
        {
            return false;
        }

        scale = value;
        applied.Add($"{variable}={Number(value)}");
        return true;
    }

    private static string CanonicalName(string name)
    {
        foreach ((string presetName, GasSettings _) in All)
        {
            if (string.Equals(presetName, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return presetName;
            }
        }

        return "Aug2017Retail";
    }

    private static string? BuildNote(
        bool presetRequested,
        string? presetName,
        List<string> applied,
        List<string> rejected,
        GasSettings settings)
    {
        if (!presetRequested && applied.Count == 0 && rejected.Count == 0)
        {
            return null;
        }

        string basePreset = IsKnownPreset(presetName) ? CanonicalName(presetName!) : "Aug2017Retail";
        var note = new StringBuilder("gas tuning: preset ").Append(basePreset);
        if (applied.Count > 0)
        {
            note.Append(" + overrides (").Append(NameOf(settings)).Append(')');
            note.Append("; overrides ").Append(string.Join(", ", applied));
        }

        if (rejected.Count > 0)
        {
            note.Append("; IGNORED ").Append(string.Join(", ", rejected));
        }

        return note.Append(" — ").Append(DescribeSchedule(settings)).ToString();
    }
}
