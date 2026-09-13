using System.Globalization;
using System.Numerics;

namespace Cranberry.Zone.DevConsole.Commands;

/// <summary>
/// The few formatting rules every command answer shares, in one place so a position reads the same
/// whether it came from <c>/where</c>, <c>/car</c> or <c>/loot find</c>.
/// <para>
/// One decimal on a coordinate is deliberate: the owner pastes a <c>/where</c> answer straight back
/// as a <c>/tp</c> line, and the console pane is 46 columns wide (design §2.6, §2.7).
/// </para>
/// </summary>
internal static class ConsoleFormat
{
    /// <summary>A world position as <c>348.0 32.5 130.8</c>.</summary>
    public static string Position(Vector3 position) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{position.X:0.0} {position.Y:0.0} {position.Z:0.0}");

    /// <summary>A world position from the wire's <c>Vector4</c> form, W dropped.</summary>
    public static string Position(Vector4 position) => Position(new Vector3(position.X, position.Y, position.Z));

    /// <summary>The XYZ of a <c>Vector4</c>. The W column is the wire's padding, never a coordinate.</summary>
    public static Vector3 Xyz(Vector4 position) => new(position.X, position.Y, position.Z);

    /// <summary>A distance in metres, one decimal.</summary>
    public static string Metres(float metres) =>
        string.Create(CultureInfo.InvariantCulture, $"{metres:0.0} m");

    /// <summary>A match clock in <c>m:ss</c>, which is what the client's own HUD shows.</summary>
    public static string Clock(long milliseconds)
    {
        if (milliseconds < 0)
        {
            milliseconds = 0;
        }

        long seconds = milliseconds / 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }

    /// <summary>An integer with no culture surprises.</summary>
    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A percentage, no decimals.</summary>
    public static string Percent(float fraction) =>
        string.Create(CultureInfo.InvariantCulture, $"{fraction * 100f:0}%");
}
