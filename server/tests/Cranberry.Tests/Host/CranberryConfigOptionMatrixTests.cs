using System.Globalization;
using Cranberry.Host.Config;

namespace Cranberry.Tests.Host;

/// <summary>
/// The test S1 §2.3 said this project keeps missing: <b>"a boot banner that reads options the wire
/// does not read is the failure mode; a config file does not fix that by itself — a test that
/// asserts per option does"</b>. Twice a switch shipped completely inert —
/// <c>CombatOptions.FromEnvironment</c> was never called for a whole wave (docs/89 §1d) and
/// <c>AllowWielding</c> never reached the wire (docs/93 §0.1) — and nothing failed.
/// <para>
/// So: for <b>every</b> key in <see cref="ConfigKeys"/>, set it (by its legacy name, and again by
/// its generated overlay name, and again through <c>cranberry.json</c>) and assert that the value
/// the server would actually run with <b>moves</b>, and that <b>no other key moves</b> except the
/// ones the row declares it couples to (a preset moves its own block's knobs by definition).
/// </para>
/// <para>
/// This is one xUnit case per key rather than one big loop, so a broken option names itself.
/// </para>
/// </summary>
public sealed class CranberryConfigOptionMatrixTests
{
    public static TheoryData<string> EveryKey
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (ConfigKey key in ConfigKeys.All)
            {
                data.Add(key.Path);
            }

            return data;
        }
    }

    /// <summary>The value to set for a key: its declared flip, or the opposite of a boolean.</summary>
    private static string FlipValue(ConfigKey key, CranberryConfig defaults)
    {
        if (key.Flip is not null)
        {
            return key.Flip;
        }

        Assert.True(
            key.Kind == ConfigKind.Bool,
            $"{key.Path} is {key.Kind} and needs an explicit Flip in ConfigKeys");
        return key.Effective(defaults) is true ? "0" : "1";
    }

    private static CranberryConfig With(string name, string value)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CranberryConfig.PathVariable] = Path.Combine(Path.GetTempPath(), $"cranberry-option-{Guid.NewGuid():N}.json"),
            [name] = value,
        };
        return CranberryConfig.Load([], n => map.TryGetValue(n, out string? v) ? v : null);
    }

    private static string Show(object? value) =>
        value switch
        {
            null => "<null>",
            bool boolean => boolean ? "true" : "false",
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<null>",
        };

    private static void AssertFlipMovesOnlyThisKey(ConfigKey key, CranberryConfig flipped)
    {
        CranberryConfig defaults = CranberryConfig.Defaults();

        Assert.False(
            Show(key.Effective(defaults)) == Show(key.Effective(flipped)),
            $"{key.Path}: setting {key.Legacy}={FlipValue(key, defaults)} did not move the value the "
                + $"server runs with (still {Show(key.Effective(defaults))}) — the switch is inert, "
                + "which is exactly the failure docs/89 §1d and docs/93 §0.1 shipped twice");

        var allowed = new HashSet<string>(key.Couples ?? [], StringComparer.Ordinal);
        var moved = new List<string>();
        foreach (ConfigKey other in ConfigKeys.All)
        {
            if (other.Path == key.Path || allowed.Contains(other.Path))
            {
                continue;
            }

            string before = Show(other.Effective(defaults));
            string after = Show(other.Effective(flipped));
            if (before != after)
            {
                moved.Add($"{other.Path} {before} -> {after}");
            }
        }

        Assert.True(
            moved.Count == 0,
            $"{key.Path} moved something else as well: {string.Join("; ", moved)}");
    }

    /// <summary>The legacy environment name — the one in the owner's launch scripts.</summary>
    [Theory]
    [MemberData(nameof(EveryKey))]
    public void TheLegacyNameMovesExactlyThisOption(string path)
    {
        ConfigKey key = ConfigKeys.ByPath[path];
        AssertFlipMovesOnlyThisKey(key, With(key.Legacy, FlipValue(key, CranberryConfig.Defaults())));
    }

    /// <summary>The generated overlay name <c>CRANBERRY_&lt;BLOCK&gt;_&lt;KEY&gt;</c>.</summary>
    [Theory]
    [MemberData(nameof(EveryKey))]
    public void TheGeneratedOverlayNameMovesExactlyThisOption(string path)
    {
        ConfigKey key = ConfigKeys.ByPath[path];
        AssertFlipMovesOnlyThisKey(key, With(key.Canonical, FlipValue(key, CranberryConfig.Defaults())));
    }

    /// <summary><c>cranberry.json</c> itself, with the value typed as the key's own JSON kind.</summary>
    [Theory]
    [MemberData(nameof(EveryKey))]
    public void TheFileMovesExactlyThisOption(string path)
    {
        ConfigKey key = ConfigKeys.ByPath[path];
        string flip = FlipValue(key, CranberryConfig.Defaults());
        string literal = key.Kind switch
        {
            ConfigKind.Bool => flip == "1" ? "true" : "false",
            ConfigKind.Int or ConfigKind.Float when double.TryParse(
                flip, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                => number.ToString("R", CultureInfo.InvariantCulture),
            _ => System.Text.Json.JsonSerializer.Serialize(flip),
        };

        string json = $$"""{ "{{key.Block}}": { "{{key.Key}}": {{literal}} } }""";
        AssertFlipMovesOnlyThisKey(key, CranberryConfig.Load([], _ => null, json));
    }
}
