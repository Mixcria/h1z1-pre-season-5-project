using System.Globalization;
using System.Text;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Match;

namespace Cranberry.Zone.Descent;

/// <summary>
/// Named descent presets and the <c>CRANBERRY_DESCENT_*</c> environment overrides - the same shape
/// as <c>MovementTuning</c> (docs/49 §I2) and <c>EnvironmentPresets</c> (docs/38 §5), and for the
/// same reason: retuning a taste value must cost a restart, not a rebuild.
/// <para>
/// <b>Order of application:</b> preset first, then the per-value overrides on top. Every value is
/// range checked; anything unparsable or out of range is <b>ignored with a note</b> and never
/// throws, so a typo in a launch script cannot brick the drop or stop the host.
/// </para>
/// <para>
/// The rails are not invented. <see cref="MinimumRate"/> and <see cref="MaximumRate"/> are the
/// client's own <c>MoveInfo</c> rows 130/4001 <c>MIN_TERM_VELOCITY</c> / <c>MAX_TERM_VELOCITY</c>
/// for vehicle 13 (docs/56 §2.3) - the descent physically cannot leave that band, so neither may the
/// number the server uses to reason about it.
/// </para>
/// </summary>
public static class DescentTuning
{
    /// <summary>Selects the preset. Unset or unknown ⇒ <see cref="ShippedDefault"/>.</summary>
    public const string PresetVariable = "CRANBERRY_DESCENT_PRESET";

    /// <summary>Overrides <see cref="DescentSettings.TargetDescentSeconds"/>.</summary>
    public const string SecondsVariable = "CRANBERRY_DESCENT_SECONDS";

    /// <summary>Overrides <see cref="DescentSettings.PlannedDescentMetresPerSecond"/>.</summary>
    public const string RateVariable = "CRANBERRY_DESCENT_RATE";

    /// <summary>
    /// D239's near-ground landing guard. <c>0</c> restores the pre-D239 descent deadline exactly -
    /// twice the DIVED expected ride plus 15 s, with no altitude gate - which is the behaviour that
    /// force-dismounted three live players about 600 m in the air on 2026-09-03.
    /// </summary>
    public const string LandingGuardVariable = "CRANBERRY_DESCENT_LANDING_GUARD";

    /// <summary>
    /// <c>MoveInfo</c> 130/4001 <c>MIN_TERM_VELOCITY</c> for vehicle 13 - DERIVED, and since D239
    /// also the number every <b>timeout</b> reasons with: it is the measured hands-off descent rate
    /// (9.81 / 9.85 / 9.82 m/s over three whole rides on 2026-09-03), i.e. the slowest ride the
    /// client can produce.
    /// </summary>
    public const float MinimumRate = Rulings.Descent.MinimumRate;

    /// <summary>
    /// <c>MoveInfo</c> 130/4001 <c>MAX_TERM_VELOCITY</c> for vehicle 13 - DERIVED, and the number
    /// every "too fast" check reasons with. Flown hard the client measures 43.5-45.8 m/s steady.
    /// </summary>
    public const float MaximumRate = Rulings.Descent.MaximumRate;

    /// <summary>
    /// A sanity rail on the seconds knob, not a client fact: at the 40.4 m/s planning mean, 120 s
    /// is 4 848 m of air, well past anything the map or the owner would want.
    /// </summary>
    public const float MaximumSeconds = Rulings.Descent.MaximumSeconds;

    /// <summary>
    /// <b>THE SHIPPED RIDE (D238): the client's own sky spawn, an absolute 850 m.</b>
    /// <para>
    /// The August client's <c>Z2Areas.xml</c> carries exactly one sky-spawn volume —
    /// <c>&lt;AreaDefinition id="429116895" name="KotK.SkySpawn" shape="box" x1="-2000" y1="845"
    /// z1="-2000" x2="2000" y2="855" z2="2000" rotY="-3.141593"/&gt;</c>, a bare 10 m slab with no
    /// <c>&lt;Property&gt;</c> child, <b>y-centre 850.0 absolute</b> (docs/48 §2a,
    /// AUDIT-parachute R1). It is the only altitude the client itself states.
    /// </para>
    /// <para>
    /// <b>The owner's ruling, 2026-09-03: "do whatever retail is; our current dropzone height is a
    /// guess."</b> Retail's own release altitude is <b>unknown</b> — not published, not on disk,
    /// never measured (AUDIT-parachute R3) — so "whatever retail is" resolves to the one number
    /// retail's own level designers left in the client, and 850 m is what ships. It replaces
    /// <see cref="Legacy36"/>'s 1,454 m, which was a design number reverse-engineered from the
    /// friend's emulator's 1,500 m rather than from retail.
    /// </para>
    /// <para>
    /// <b>What it costs and buys, measured.</b> The ride is ~21 s dived and ~85 s hands-off instead
    /// of ~35 s and ~148 s, and the glide budget halves from the measured 587-672 m to about 380 m —
    /// so the drop planner's aim point starts meaning something again (AUDIT-parachute G4).
    /// </para>
    /// <para>
    /// Mechanically this is <see cref="DescentSettings.Default"/> itself, by reference: with
    /// <c>TargetDescentSeconds = 0</c> nothing is raised above <c>DropOptions.SkySpawnAltitude</c>,
    /// so the release <em>is</em> the client's slab. <see cref="Wave4Default"/> is the same instance
    /// under its historical name.
    /// </para>
    /// </summary>
    public static DescentSettings ClientSkySpawn850 => DescentSettings.Default;

    /// <summary>
    /// <b>Wave 4's shipped behaviour, and byte-identical to it:</b> the client's 850 m sky spawn,
    /// ~21 s of fall. The historical name for <see cref="ClientSkySpawn850"/> — the same instance,
    /// kept so every launch script that already says <c>CRANBERRY_DESCENT_PRESET=Wave4Default</c>
    /// keeps working.
    /// </summary>
    public static DescentSettings Wave4Default => DescentSettings.Default;

    /// <summary>
    /// docs/56 §2.5's suggested first try: <b>30 s</b>, about 1 250 m over Pleasant Valley. DESIGN -
    /// chosen to sit between the measured ~21 s of the client's own slab and the measured ~36 s of
    /// the pre-wave-4 altitude. The middle position if 850 m ever feels rushed; nothing in the
    /// August client says 30.
    /// </summary>
    public static DescentSettings Owner30 { get; } = DescentSettings.Default with { TargetDescentSeconds = Rulings.Descent.Owner30Seconds };

    /// <summary>
    /// <b>36 s</b> (~1 454 m of air), which is what the retired <c>ZoneOptions.DropAltitude = 1500f</c>
    /// actually measured (35.2-36.3 s over four captured drops, docs/56 §2.2, and 35.3-35.6 s over
    /// the two 2026-09-03 rides) and what the owner's own Z1 server gives him (docs/88 §2b).
    /// <para>
    /// <b>PROVENANCE CORRECTION (D239, AUDIT-parachute R5 / G12).</b> docs/88 §2b and S4 row D5
    /// attributed the 1 500 m figure to the owner's 2026-08-22 admin capture. <b>That capture has no
    /// parachute in it</b>: its maximum Y anywhere is 506.91, a byte search for <c>0080bb44</c>
    /// (1500.0f) returns zero hits, and both of its <c>d8</c> records are vehicleId 1, model 10060
    /// (an Offroader) on the ground. The 1 500 comes from the earlier decode
    /// <c>C:\Project\out\zonedecode_entities\ops\cPacketIdAddLightweightVehicle.txt:3,5</c> —
    /// two parachutes, vehicleId 13, model 9374, one at y 1499.68. The number is still the owner's
    /// own capture of the friend's server; only the citation was wrong.
    /// </para>
    /// <para>
    /// Wave 9 made this the <see cref="ShippedDefault"/> and <b>D238 took that back</b>: it is an
    /// emulator's altitude, not retail's, and the client states 850 m. It keeps its own name as the
    /// one-word revert, and it is still the better <em>shape</em> than an absolute altitude — the
    /// seconds knob holds the ride constant over terrain that varies by 277 m.
    /// </para>
    /// </summary>
    public static DescentSettings Legacy36 { get; } = DescentSettings.Default with { TargetDescentSeconds = Rulings.Descent.Legacy36Seconds };

    /// <summary>
    /// <b>The preset an unset environment gets: <see cref="ClientSkySpawn850"/>.</b>
    /// <para>
    /// D238, 2026-09-03. The owner ruled "do whatever retail is; our current dropzone height is a
    /// guess", and the audit answered that retail's own release altitude is unknown while the client
    /// itself states exactly one: <c>KotK.SkySpawn</c>'s 850 m absolute. So the shipped release is
    /// the client's, and every design altitude above it — <see cref="Owner30"/>'s 1 212 m and
    /// <see cref="Legacy36"/>'s 1 454 m — is now opt-in.
    /// </para>
    /// <para>
    /// <b>This reverses wave 9's D125</b>, which made <see cref="Legacy36"/> the default on the
    /// strength of the friend's server releasing at an absolute 1 500 m. That is a real capture of a
    /// real server, but it is an <em>emulator's</em> choice — Z1's own source says so — and the
    /// owner's ruling ranks the client's own data above it. The revert is one word:
    /// <c>CRANBERRY_DESCENT_PRESET=Legacy36</c>.
    /// </para>
    /// </summary>
    public static DescentSettings ShippedDefault => ClientSkySpawn850;

    /// <summary>Every preset by name, in the order they are offered to the owner.</summary>
    public static IReadOnlyList<(string Name, DescentSettings Settings)> All { get; } =
    [
        ("ClientSkySpawn850", ClientSkySpawn850),
        ("Wave4Default", Wave4Default),
        ("Owner30", Owner30),
        ("Legacy36", Legacy36),
    ];

    /// <summary>The preset names, for a log line or an error message.</summary>
    public static IReadOnlyList<string> PresetNames { get; } = All.Select(preset => preset.Name).ToArray();

    /// <summary>
    /// The preset called <paramref name="name"/> (case-insensitive), or <see cref="ShippedDefault"/>
    /// when the name is null, empty or unknown. Never throws - an unknown name must not stop a host.
    /// </summary>
    public static DescentSettings FromNameOrDefault(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ShippedDefault;
        }

        foreach ((string presetName, DescentSettings settings) in All)
        {
            if (string.Equals(presetName, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return settings;
            }
        }

        return ShippedDefault;
    }

    /// <summary>True when <paramref name="name"/> names a preset.</summary>
    public static bool IsKnownPreset(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && All.Any(preset => string.Equals(preset.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The settings a host should run: <see cref="PresetVariable"/> first, then
    /// <see cref="SecondsVariable"/> and <see cref="RateVariable"/> on top.
    /// <para>
    /// <paramref name="note"/> is null when the environment asked for nothing, so a quiet default
    /// boot logs nothing extra; otherwise it is a one-line summary naming the preset, every applied
    /// override and every rejected one with its reason. <b>Nothing here throws.</b>
    /// </para>
    /// </summary>
    /// <param name="read">Usually <c>Environment.GetEnvironmentVariable</c>.</param>
    /// <param name="note">A human-readable summary for the host log, or null when nothing was set.</param>
    public static DescentSettings FromEnvironment(Func<string, string?> read, out string? note)
    {
        ArgumentNullException.ThrowIfNull(read);

        string? presetName = read(PresetVariable);
        DescentSettings settings = FromNameOrDefault(presetName);

        var applied = new List<string>();
        var rejected = new List<string>();

        bool presetRequested = !string.IsNullOrWhiteSpace(presetName);
        if (presetRequested && !IsKnownPreset(presetName))
        {
            rejected.Add($"{PresetVariable}='{presetName!.Trim()}' (unknown; known: {string.Join(", ", PresetNames)})");
        }

        if (TryRead(read, SecondsVariable, 0f, MaximumSeconds, applied, rejected, "TargetDescentSeconds", out float seconds))
        {
            settings = settings with { TargetDescentSeconds = seconds };
        }

        if (TryRead(read, RateVariable, MinimumRate, MaximumRate, applied, rejected, "PlannedDescentMetresPerSecond", out float rate))
        {
            settings = settings with { PlannedDescentMetresPerSecond = rate };
        }

        // Both knobs are range checked above, so this cannot fail - but the result decides where a
        // player is spawned, so it is checked rather than assumed. A widened rail falls back to the
        // preset instead of taking the host down.
        try
        {
            settings.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            rejected.Add($"the tuned descent was refused ({ex.Message}); the preset is used unchanged");
            settings = FromNameOrDefault(presetName);
            applied.Clear();
        }

        note = BuildNote(presetRequested, presetName, applied, rejected, settings);
        return settings;
    }

    /// <summary>
    /// The one-line "what a drop will actually do" summary. <b>D238/D239 made it tell the truth
    /// twice over</b> (AUDIT-parachute G2, G10): it names the <em>real</em> release, and it states
    /// the ride as the player-controlled band it is rather than as a single client constant.
    /// </summary>
    public static string Describe(DescentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.ChangesAnything)
        {
            float altitude = DropOptions.Default.SkySpawnAltitude;
            return string.Format(
                CultureInfo.InvariantCulture,
                "descent: release = {0:F0} m ABSOLUTE (the client's own KotK.SkySpawn slab, Z2Areas.xml) "
                    + "=> ~{1:F0} s dived / ~{2:F0} s hands-off over ground at 0 m "
                    + "(the rate is the PLAYER's, {3:F0}-{4:F0} m/s, docs/115 2)",
                altitude,
                altitude / DescentSettings.MeasuredFlownMetresPerSecond,
                altitude / MinimumRate,
                MinimumRate,
                MaximumRate);
        }

        float clearance = settings.RequiredClearanceMetres;
        return string.Format(
            CultureInfo.InvariantCulture,
            "descent: {0:F0} s target => release = max({1:F0} m absolute, ground + {2:F0} m) "
                + "=> ~{3:F0} s dived / ~{4:F0} s hands-off "
                + "(the rate is the PLAYER's, {5:F0}-{6:F0} m/s, docs/115 2; {7:F1} m/s is the planning mean only)",
            settings.TargetDescentSeconds,
            DropOptions.Default.SkySpawnAltitude,
            clearance,
            clearance / DescentSettings.MeasuredFlownMetresPerSecond,
            clearance / MinimumRate,
            MinimumRate,
            MaximumRate,
            settings.PlannedDescentMetresPerSecond);
    }

    /// <summary>The name of a preset by value, or "custom" once an override has moved anything.</summary>
    public static string NameOf(DescentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach ((string name, DescentSettings preset) in All)
        {
            if (preset == settings)
            {
                return name;
            }
        }

        return "custom";
    }

    private static bool TryRead(
        Func<string, string?> read,
        string variable,
        float minimum,
        float maximum,
        List<string> applied,
        List<string> rejected,
        string property,
        out float value)
    {
        value = 0f;
        string? raw = read(variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            || !float.IsFinite(parsed))
        {
            rejected.Add($"{variable}='{raw.Trim()}' (not a number)");
            return false;
        }

        if (parsed < minimum || parsed > maximum)
        {
            rejected.Add(
                $"{variable}={parsed.ToString("0.###", CultureInfo.InvariantCulture)} "
                + $"(outside {minimum.ToString("0.###", CultureInfo.InvariantCulture)}"
                + $"..{maximum.ToString("0.###", CultureInfo.InvariantCulture)})");
            return false;
        }

        value = parsed;
        applied.Add($"{property}={parsed.ToString("0.###", CultureInfo.InvariantCulture)}");
        return true;
    }

    private static string CanonicalName(string name)
    {
        foreach ((string presetName, DescentSettings _) in All)
        {
            if (string.Equals(presetName, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return presetName;
            }
        }

        return NameOf(ShippedDefault);
    }

    private static string? BuildNote(
        bool presetRequested,
        string? presetName,
        List<string> applied,
        List<string> rejected,
        DescentSettings settings)
    {
        if (!presetRequested && applied.Count == 0 && rejected.Count == 0)
        {
            return null;
        }

        string basePreset = IsKnownPreset(presetName) ? CanonicalName(presetName!) : NameOf(ShippedDefault);
        var note = new StringBuilder("descent tuning: preset ").Append(basePreset);
        if (applied.Count > 0)
        {
            note.Append(" + overrides (").Append(NameOf(settings)).Append(')');
            note.Append("; overrides ").Append(string.Join(", ", applied));
        }

        if (rejected.Count > 0)
        {
            note.Append("; IGNORED ").Append(string.Join(", ", rejected));
        }

        return note.Append(" - ").Append(Describe(settings)).ToString();
    }
}
