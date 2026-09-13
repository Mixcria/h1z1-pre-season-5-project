using System.Globalization;
using System.Text;

namespace Cranberry.Zone.Movement;

/// <summary>
/// Named movement presets and the <c>CRANBERRY_MOVE_*</c> environment overrides — docs/49 §I2, the
/// same shape as <c>CRANBERRY_SKY</c>'s <c>EnvironmentPresets</c> (docs/38 §5).
/// <para>
/// <b>Why this exists.</b> The August client ships no speed value of any kind, twice confirmed
/// exhaustively (docs/49 §5), so the server has to choose every number. Wave 4 added
/// <i>measurement</i> — the client reports its own horizontal speed back on channel 2, and every
/// wave-3 value was seen landing exactly as sent (docs/49 §2.2). Wave 8 added <i>provenance</i>:
/// the defaults are now the owner's own Z1 values (docs/76). What is left is taste, and taste is
/// his, so retuning must cost a restart and not a rebuild.
/// </para>
/// <para>
/// <b>Order of application:</b> preset first, then per-stat overrides on top. Every value is range
/// checked; anything unparsable or out of range is <b>ignored with a note</b> and never throws, so a
/// typo in a launch script cannot brick movement or stop the host.
/// </para>
/// </summary>
public static class MovementTuning
{
    /// <summary>Selects the preset. Unset or unknown ⇒ <see cref="Aug2017Default"/>.</summary>
    public const string PresetVariable = "CRANBERRY_MOVE_PRESET";

    /// <summary>
    /// <b>The default — the owner's own Z1 movement, docs/76.</b> Base <b>4.10 m/s</b> (his
    /// <c>ZoneMovement.RetailJogSpeed</c>), sprint ×1.40 ⇒ <b>5.74 m/s</b>, walk ×0.30, crouch ×0.70,
    /// backpedal ×0.75, strafe ×0.75, water ×0.80, prone ×0.40, plus his eight blend times
    /// (0.60 / 0 / 0.50 / 0.50 / 0.50 / 0.20 / 0 / 0) — heavier to wind up, and a dead stop on
    /// everything but a forward run. <see cref="Wave5Legacy"/> is the set the owner last played, for
    /// an in-place A/B.
    /// <para>
    /// This is <see cref="MovementProfile.Default"/> itself, by reference and on purpose: the zone's
    /// stat-burst send short-circuits on
    /// <c>ReferenceEquals(tracker.Profile, options.Movement)</c>, and a second equal-but-distinct
    /// default instance would re-arm the <c>0f 40</c> burst on every resync (docs/49 §I3).
    /// </para>
    /// </summary>
    public static MovementProfile Aug2017Default => MovementProfile.Default;

    /// <summary>
    /// The <b>exact wave-3 burst</b> — base 5.50, sprint 1.45, backpedal 0.65, strafe 0.80 — so the
    /// capture docs/49 is derived from (<c>captures\wire-20260829-220829.txt</c>) can be reproduced
    /// byte-for-byte. This is the movement lane's equivalent of <c>CRANBERRY_SKY=LegacyD18Grey</c>.
    /// <para>
    /// <b>Every field is stated absolutely, and must stay that way.</b> It used to name only the
    /// three fields that differed from the then-default and inherit the other fifteen; wave 8 moved
    /// all fifteen, which would silently have turned this preset into "wave 3's three numbers on
    /// wave 8's base" and quietly broken the one capture the project can replay
    /// (<c>MovementPostureTests.EachPlateauPostureNamesTheModifierThatProducedItsPlateau</c> is the
    /// guard). A historical preset inheriting from a live default is a bug waiting for the next
    /// retune — docs/76 §7.3.
    /// </para>
    /// </summary>
    public static MovementProfile Wave3Legacy { get; } = MovementProfile.Default with
    {
        MaxMovementSpeed = 5.50f,
        SprintSpeedModifier = 1.45f,
        WalkSpeedModifier = 0.45f,
        CrouchSpeedModifier = 0.55f,
        BackpedalSpeedModifier = 0.65f,
        StrafeSpeedModifier = 0.80f,
        ProneSpeedModifier = 0.25f,
        ProneRollSpeedModifier = 0.50f,
        WaterSpeedModifier = 0.75f,
        SprintAccelerationTime = 0.35f,
        SprintDecelerationTime = 0.25f,
        ForwardAccelerationTime = 0.20f,
        ForwardDecelerationTime = 0.15f,
        BackAccelerationTime = 0.20f,
        BackDecelerationTime = 0.15f,
        StrafeAccelerationTime = 0.15f,
        StrafeDecelerationTime = 0.15f,
    };

    /// <summary>
    /// <b>The set the owner actually played through waves 4-7</b> — base 5.50, sprint ×1.20
    /// (6.60 m/s), walk ×0.45, crouch ×0.55, backpedal ×0.50, strafe ×0.75, and the wave-3/4 blend
    /// times. This is the one-word revert for wave 8's port: <c>CRANBERRY_MOVE_PRESET=Wave5Legacy</c>
    /// puts movement back exactly as he last had it, with no rebuild, so "his Z1 speeds" and "what we
    /// shipped before" can be compared in two restarts (docs/76 §7.3).
    /// <para>
    /// Stated absolutely, for the reason on <see cref="Wave3Legacy"/>.
    /// </para>
    /// </summary>
    public static MovementProfile Wave5Legacy { get; } = MovementProfile.Default with
    {
        MaxMovementSpeed = 5.50f,
        SprintSpeedModifier = 1.20f,
        WalkSpeedModifier = 0.45f,
        CrouchSpeedModifier = 0.55f,
        BackpedalSpeedModifier = 0.50f,
        StrafeSpeedModifier = 0.75f,
        ProneSpeedModifier = 0.25f,
        ProneRollSpeedModifier = 0.50f,
        WaterSpeedModifier = 0.75f,
        SprintAccelerationTime = 0.35f,
        SprintDecelerationTime = 0.25f,
        ForwardAccelerationTime = 0.20f,
        ForwardDecelerationTime = 0.15f,
        BackAccelerationTime = 0.20f,
        BackDecelerationTime = 0.15f,
        StrafeAccelerationTime = 0.15f,
        StrafeDecelerationTime = 0.15f,
    };

    /// <summary>
    /// One step slower than the default all round — the preset to try if the owner's next report is
    /// "everything is too fast" rather than "sprint is too fast". Base <b>3.70 m/s</b>, sprint ×1.30
    /// (4.81 m/s); every other modifier and every blend time is the default's, i.e. the owner's.
    /// <para>
    /// <b>Re-pointed in wave 8.</b> It used to be base 5.00 / sprint ×1.25, which was "one step
    /// slower" only against the old 5.50 default — against 4.10 it was a step <i>faster</i>, and its
    /// doc-comment said the opposite of what it did. The base and sprint are restated here; the rest
    /// deliberately tracks the default, because this preset means "the owner's movement, dialled
    /// down", not "a frozen historical set" (docs/76 §7.3).
    /// </para>
    /// </summary>
    public static MovementProfile Grounded { get; } = MovementProfile.Default with
    {
        MaxMovementSpeed = 3.70f,
        SprintSpeedModifier = 1.30f,
    };

    /// <summary>Every preset by name, in the order they are offered to the owner.</summary>
    public static IReadOnlyList<(string Name, MovementProfile Profile)> All { get; } =
    [
        ("Aug2017Default", Aug2017Default),
        ("Wave5Legacy", Wave5Legacy),
        ("Wave3Legacy", Wave3Legacy),
        ("Grounded", Grounded),
    ];

    /// <summary>The preset names, for a log line or an error message.</summary>
    public static IReadOnlyList<string> PresetNames { get; } = All.Select(p => p.Name).ToArray();

    /// <summary>
    /// The preset called <paramref name="name"/> (case-insensitive), or <see cref="Aug2017Default"/>
    /// when the name is null, empty or unknown. Never throws — an unknown name must not stop a host.
    /// </summary>
    public static MovementProfile FromNameOrDefault(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Aug2017Default;
        }

        foreach ((string presetName, MovementProfile profile) in All)
        {
            if (string.Equals(presetName, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        return Aug2017Default;
    }

    /// <summary>True when <paramref name="name"/> names a preset.</summary>
    public static bool IsKnownPreset(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && All.Any(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The per-stat overrides of docs/49 §I2, plus wave 8's three ramp knobs (docs/76 §7.3). Each is
    /// a float in invariant culture with an inclusive range; the ranges are sanity rails, not client
    /// facts — the base speed's ceiling is the client's own
    /// <c>Vehicle.DefaultMaxDismountSpeed = 12.0</c>, the only same-unit speed the August client
    /// ships (docs/49 §5.4).
    /// <para>
    /// The two a play-test of wave 8 will reach for first are <c>CRANBERRY_MOVE_BASE=4.00</c> (which
    /// reproduces the owner's own client-reported plateaus to the decimal, docs/76 §2.5) and
    /// <c>CRANBERRY_MOVE_BACK=0.67</c> (which restores the 2.75 m/s backpedal he accepted in wave 4,
    /// docs/76 §2.6). The three <c>*_ACCEL</c> knobs exist because wave 8 tripled the ramp times and
    /// "too sluggish" must be answerable without a rebuild. Their rail starts at <b>0</b>, which is
    /// legal and means "snap instantly" — it is the client's own default and what the owner ships for
    /// three of the four decelerations.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MovementKnob> Knobs { get; } =
    [
        new("CRANBERRY_MOVE_BASE", nameof(MovementProfile.MaxMovementSpeed), 0.5f, 12.0f,
            (p, v) => p with { MaxMovementSpeed = v }, p => p.MaxMovementSpeed),
        new("CRANBERRY_MOVE_SPRINT", nameof(MovementProfile.SprintSpeedModifier), 0.05f, 3.0f,
            (p, v) => p with { SprintSpeedModifier = v }, p => p.SprintSpeedModifier),
        new("CRANBERRY_MOVE_WALK", nameof(MovementProfile.WalkSpeedModifier), 0.05f, 3.0f,
            (p, v) => p with { WalkSpeedModifier = v }, p => p.WalkSpeedModifier),
        new("CRANBERRY_MOVE_CROUCH", nameof(MovementProfile.CrouchSpeedModifier), 0.05f, 3.0f,
            (p, v) => p with { CrouchSpeedModifier = v }, p => p.CrouchSpeedModifier),
        new("CRANBERRY_MOVE_BACK", nameof(MovementProfile.BackpedalSpeedModifier), 0.05f, 3.0f,
            (p, v) => p with { BackpedalSpeedModifier = v }, p => p.BackpedalSpeedModifier),
        new("CRANBERRY_MOVE_STRAFE", nameof(MovementProfile.StrafeSpeedModifier), 0.05f, 3.0f,
            (p, v) => p with { StrafeSpeedModifier = v }, p => p.StrafeSpeedModifier),
        new("CRANBERRY_MOVE_SWIM", nameof(MovementProfile.SwimSpeedModifier), 0.05f, 3.0f,
            (p, v) => p with { SwimSpeedModifier = v }, p => p.SwimSpeedModifier),
        new("CRANBERRY_MOVE_WATER", nameof(MovementProfile.WaterSpeedModifier), 0.05f, 3.0f,
            (p, v) => p with { WaterSpeedModifier = v }, p => p.WaterSpeedModifier),
        new("CRANBERRY_MOVE_SPRINT_ACCEL", nameof(MovementProfile.SprintAccelerationTime), 0f, 5.0f,
            (p, v) => p with { SprintAccelerationTime = v }, p => p.SprintAccelerationTime),
        new("CRANBERRY_MOVE_SPRINT_DECEL", nameof(MovementProfile.SprintDecelerationTime), 0f, 5.0f,
            (p, v) => p with { SprintDecelerationTime = v }, p => p.SprintDecelerationTime),
        new("CRANBERRY_MOVE_FWD_ACCEL", nameof(MovementProfile.ForwardAccelerationTime), 0f, 5.0f,
            (p, v) => p with { ForwardAccelerationTime = v }, p => p.ForwardAccelerationTime),
        new("CRANBERRY_MOVE_BACK_ACCEL", nameof(MovementProfile.BackAccelerationTime), 0f, 5.0f,
            (p, v) => p with { BackAccelerationTime = v }, p => p.BackAccelerationTime),
        new("CRANBERRY_MOVE_STRAFE_ACCEL", nameof(MovementProfile.StrafeAccelerationTime), 0f, 5.0f,
            (p, v) => p with { StrafeAccelerationTime = v }, p => p.StrafeAccelerationTime),
    ];

    /// <summary>
    /// One environment-overridable value: which variable names it, which property it writes, and the
    /// inclusive range outside which it is refused.
    /// </summary>
    /// <param name="Variable">The environment variable, e.g. <c>CRANBERRY_MOVE_SPRINT</c>.</param>
    /// <param name="Property">The <see cref="MovementProfile"/> property it writes.</param>
    /// <param name="Minimum">Smallest accepted value, inclusive.</param>
    /// <param name="Maximum">Largest accepted value, inclusive.</param>
    /// <param name="Apply">Returns a copy of the profile with this value replaced.</param>
    /// <param name="Read">Reads the current value out of a profile.</param>
    public sealed record MovementKnob(
        string Variable,
        string Property,
        float Minimum,
        float Maximum,
        Func<MovementProfile, float, MovementProfile> Apply,
        Func<MovementProfile, float> Read);

    /// <summary>
    /// The profile a host should run: <see cref="PresetVariable"/> first, then every
    /// <see cref="Knobs"/> override the environment sets, on top.
    /// <para>
    /// <paramref name="note"/> is null when the environment asked for nothing (so a quiet default
    /// boot logs nothing extra); otherwise it is a one-line summary naming the preset, every applied
    /// override, and every rejected one with the reason. <b>Nothing here throws</b> — a bad value in
    /// a launch script is reported and skipped.
    /// </para>
    /// </summary>
    /// <param name="read">Usually <c>Environment.GetEnvironmentVariable</c>.</param>
    /// <param name="note">A human-readable summary for the host log, or null when nothing was set.</param>
    public static MovementProfile FromEnvironment(Func<string, string?> read, out string? note)
    {
        ArgumentNullException.ThrowIfNull(read);

        string? presetName = read(PresetVariable);
        MovementProfile profile = FromNameOrDefault(presetName);

        var applied = new List<string>();
        var rejected = new List<string>();

        bool presetRequested = !string.IsNullOrWhiteSpace(presetName);
        if (presetRequested && !IsKnownPreset(presetName))
        {
            rejected.Add($"{PresetVariable}='{presetName!.Trim()}' (unknown; known: {string.Join(", ", PresetNames)})");
        }

        foreach (MovementKnob knob in Knobs)
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
                    $"{knob.Variable}={value.ToString("0.###", CultureInfo.InvariantCulture)} "
                    + $"(outside {knob.Minimum.ToString("0.###", CultureInfo.InvariantCulture)}"
                    + $"..{knob.Maximum.ToString("0.###", CultureInfo.InvariantCulture)})");
                continue;
            }

            profile = knob.Apply(profile, value);
            applied.Add($"{knob.Property}={value.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        // A knob can only move one value inside its own rail, so this cannot fail — but the profile
        // is about to be turned into wire bytes, so it is checked rather than assumed. If a future
        // rail is widened wrongly, fall back to the preset instead of taking the host down.
        try
        {
            profile.Validate();
        }
        catch (InvalidOperationException ex)
        {
            rejected.Add($"the tuned profile was refused ({ex.Message}); the preset is used unchanged");
            profile = FromNameOrDefault(presetName);
            applied.Clear();
        }

        note = BuildNote(presetRequested, presetName, applied, rejected, profile);
        return profile;
    }

    /// <summary>
    /// The one-line "what a match will actually move at" summary, used by the note and worth logging
    /// beside the stat burst. On the wave-8 default that reads <c>run 4.10, sprint 5.74, walk 1.23,
    /// crouch 2.87, strafe 3.08, backpedal 3.08 m/s</c>.
    /// </summary>
    public static string DescribeSpeeds(MovementProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return string.Format(
            CultureInfo.InvariantCulture,
            "run {0:F2}, sprint {1:F2}, walk {2:F2}, crouch {3:F2}, strafe {4:F2}, backpedal {5:F2} m/s",
            profile.RunSpeed,
            profile.SprintSpeed,
            profile.WalkSpeed,
            profile.CrouchSpeed,
            profile.StrafeSpeed,
            profile.BackpedalSpeed);
    }

    /// <summary>The name of a preset by value, or "custom" once an override has moved anything.</summary>
    public static string NameOf(MovementProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        foreach ((string name, MovementProfile preset) in All)
        {
            if (preset == profile)
            {
                return name;
            }
        }

        return "custom";
    }

    private static string CanonicalName(string name)
    {
        foreach ((string presetName, MovementProfile _) in All)
        {
            if (string.Equals(presetName, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return presetName;
            }
        }

        return "Aug2017Default";
    }

    private static string? BuildNote(
        bool presetRequested,
        string? presetName,
        List<string> applied,
        List<string> rejected,
        MovementProfile profile)
    {
        if (!presetRequested && applied.Count == 0 && rejected.Count == 0)
        {
            return null;
        }

        string basePreset = IsKnownPreset(presetName) ? CanonicalName(presetName!) : "Aug2017Default";
        var note = new StringBuilder("movement tuning: preset ").Append(basePreset);
        if (applied.Count > 0)
        {
            note.Append(" + overrides (").Append(NameOf(profile)).Append(')');
        }

        if (applied.Count > 0)
        {
            note.Append("; overrides ").Append(string.Join(", ", applied));
        }

        if (rejected.Count > 0)
        {
            note.Append("; IGNORED ").Append(string.Join(", ", rejected));
        }

        return note.Append(" — ").Append(DescribeSpeeds(profile)).ToString();
    }
}
