namespace Cranberry.Zone.Lighting;

/// <summary>
/// Applies an <see cref="EnvironmentSettings"/> to <see cref="ZoneOptions"/> in one call, so the
/// three fields that have to move together (sky struct, frozen clock, lighting table) cannot drift
/// apart in a host's start-up code.
/// </summary>
/// <remarks>
/// This lives as an extension rather than as members of <see cref="ZoneOptions"/> because this lane
/// creates new files only; the integration note in docs/38 records the one-line host change.
/// </remarks>
public static class ZoneOptionsEnvironmentExtensions
{
    /// <summary>
    /// Returns a copy of <paramref name="options"/> whose <c>Weather</c>, <c>FixedUnixTime</c>,
    /// <c>FreezeClock</c> and <c>LightingFile</c> come from <paramref name="environment"/>.
    /// </summary>
    /// <remarks>
    /// Nothing else on <see cref="ZoneOptions"/> is touched — in particular the zoning flow, the
    /// loot options and the equipment options are left exactly as they were, which is what keeps
    /// this change A/B-able against <c>captures/wire-20260829-150206.txt</c> (docs/32).
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The preset's sky would poison the client's blend or precipitation maths
    /// (<see cref="SkySettings.Validate"/>) — failed here, at start-up, rather than mid-zoning.
    /// </exception>
    public static ZoneOptions WithEnvironment(this ZoneOptions options, EnvironmentSettings environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        environment.Validate();

        return options with
        {
            Weather = environment.Sky.ToWeatherSettings(),
            FixedUnixTime = environment.Clock.FixedUnixTime,
            FreezeClock = environment.Clock.Freeze,
            LightingFile = environment.LightingFile,
        };
    }

    /// <summary>
    /// Convenience for a host switch such as
    /// <c>options.WithEnvironment(Environment.GetEnvironmentVariable("CRANBERRY_SKY"))</c>: an unset
    /// or unknown name falls back to <see cref="EnvironmentPresets.Default"/>.
    /// </summary>
    public static ZoneOptions WithEnvironment(this ZoneOptions options, string? presetName) =>
        options.WithEnvironment(EnvironmentPresets.FromNameOrDefault(presetName));
}
