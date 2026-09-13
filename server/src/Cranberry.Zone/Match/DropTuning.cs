using System.Globalization;
using Cranberry.Zone.Descent;

namespace Cranberry.Zone.Match;

/// <summary>
/// The <c>CRANBERRY_DROP_*</c> environment overrides — the same shape as <c>DescentTuning</c>
/// (docs/56 §I2) and <c>MovementTuning</c> (docs/49 §I2), and for the same reason: retuning a taste
/// value must cost a restart, not a rebuild.
/// <para>
/// AUDIT-parachute G9: before D240 <c>DropOptions</c> had <b>no environment override of any kind</b>
/// — <c>SkySpawnAltitude</c>, <c>RingFactor</c>, <c>PoiWeightExponent</c>, <c>MinimumMarkers</c> and
/// <c>MinimumEdgeMetres</c> were all compile-time only, so every drop question needed a rebuild to
/// taste-test. This block opens the one knob D240 introduced; the rest stay literals until somebody
/// has a measurement that wants moving.
/// </para>
/// <para>
/// Every value is range checked; anything unparsable or out of range is <b>ignored with a note</b>
/// and never throws, so a typo in a launch script cannot brick the drop or stop the host.
/// </para>
/// </summary>
public static class DropTuning
{
    /// <summary>
    /// Overrides <see cref="DropOptions.JitterFloorFraction"/>. <c>0</c> restores the pre-D240
    /// absolute-only jitter floor.
    /// </summary>
    public const string JitterFloorVariable = "CRANBERRY_DROP_JITTER_FLOOR";

    /// <summary>
    /// Selects the parachute skin every drop is dressed in — one of the client's own
    /// <c>VehicleSkinMods</c> rows 11/12/13 for vehicle 13, mod point 1: item <b>4055</b> Green,
    /// <b>4056</b> Blue, <b>4057</b> Tan. Also accepts the colour name. Unset or <c>0</c> is the
    /// default canopy, which is what a character with no parachute skin selected gets.
    /// </summary>
    public const string ParachuteSkinVariable = "CRANBERRY_PARACHUTE_SKIN";

    /// <summary>
    /// The drop options a host should run: <see cref="DropOptions.Default"/> with whatever the
    /// environment asked for on top.
    /// <para>
    /// <paramref name="note"/> is null when the environment asked for nothing, so a quiet default
    /// boot logs nothing extra. <b>Nothing here throws.</b>
    /// </para>
    /// </summary>
    public static DropOptions FromEnvironment(Func<string, string?> read, out string? note)
    {
        ArgumentNullException.ThrowIfNull(read);

        DropOptions options = DropOptions.Default;
        var applied = new List<string>();
        var rejected = new List<string>();

        if (TryReadFloat(read, JitterFloorVariable, 0f, 1f, applied, rejected, nameof(DropOptions.JitterFloorFraction), out float floor))
        {
            options = options with { JitterFloorFraction = floor };
        }

        try
        {
            options.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            rejected.Add($"the tuned drop was refused ({ex.Message}); the defaults are used unchanged");
            options = DropOptions.Default;
            applied.Clear();
        }

        if (applied.Count == 0 && rejected.Count == 0)
        {
            note = null;
            return options;
        }

        var text = new System.Text.StringBuilder("drop tuning:");
        if (applied.Count > 0)
        {
            text.Append(' ').Append(string.Join(", ", applied));
        }

        if (rejected.Count > 0)
        {
            text.Append(" IGNORED ").Append(string.Join(", ", rejected));
        }

        note = text.ToString();
        return options;
    }

    /// <summary>
    /// The parachute skin the environment asked for, as a client item id, or <c>0</c> for the
    /// default canopy. Never throws; an unrecognised value is <c>0</c>.
    /// </summary>
    public static uint ParachuteSkinFromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return ParachuteSkin.Parse(read(ParachuteSkinVariable));
    }

    private static bool TryReadFloat(
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
}
