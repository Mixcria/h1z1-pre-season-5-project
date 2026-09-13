using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Lighting;

/// <summary>
/// One selectable look: the sky struct, the frozen clock and the lighting table, bundled so a host
/// changes all three together or none of them (docs/38 §5).
/// </summary>
/// <remarks>
/// The three travel together on purpose. The weather struct describes the <em>sky</em>, the lighting
/// table describes the <em>grade</em>, and the clock decides <em>which phase of the table</em> is
/// blended in; picking a cloudy sky while the clock sits on a phase whose LUT is an identity
/// (docs/38 §4.4) is exactly the mismatch that produced the grey scene the owner is complaining
/// about. Apply one with <see cref="ZoneOptionsEnvironmentExtensions.WithEnvironment"/>.
/// </remarks>
public sealed record EnvironmentSettings
{
    /// <summary>Stable identifier used by <see cref="EnvironmentPresets.ByName"/> and by the host log.</summary>
    public required string Name { get; init; }

    /// <summary>One-line description of where the numbers came from, for the host's start-up line.</summary>
    public required string Provenance { get; init; }

    /// <summary>The weather/sky struct sent in <c>SendZoneDetails</c>, <c>UpdateWeatherData</c> and <c>ClientBeginZoning</c>.</summary>
    public SkySettings Sky { get; init; } = new();

    /// <summary>The frozen UTC time of day supplied by <c>GameTimeSync</c>.</summary>
    public FrozenSkyClock Clock { get; init; } = FrozenSkyClock.SolarNoon;

    /// <summary>
    /// The lighting table named in <c>SendZoneDetails</c> field 13. Default
    /// <see cref="LightingTable.Z2"/>.
    /// </summary>
    public string LightingFile { get; init; } = Rulings.Sky.LightingFile;

    /// <summary>Throws if the sky would poison the client's blend or precipitation maths.</summary>
    public void Validate() => Sky.Validate();

    /// <summary>Start-up log line: what was selected and where every number in it came from.</summary>
    public string Describe() =>
        $"{Name}: {Provenance}; clock {Clock.DateUtc:yyyy-MM-dd} {Clock.HourUtc:00}:{Clock.MinuteUtc:00}Z "
        + $"(unix {Clock.FixedUnixTime}, frozen={Clock.Freeze}, authored sun pitch "
        + $"{Clock.AuthoredSunPitchDegrees:0.0}deg, sin {Clock.AuthoredSunElevationSine:0.000} vs DayAngle "
        + $"{ZSunArc.DayAngle:0.00}); lighting '{LightingFile}'";
}
