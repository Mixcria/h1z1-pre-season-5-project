using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Lighting;

namespace Cranberry.Tests.Zone.Lighting;

/// <summary>
/// The preset table, the frozen clock and the <see cref="ZoneOptions"/> adapter, checked against
/// docs/38 §4.5, §5.1 and §5.2.
/// </summary>
public sealed class EnvironmentPresetTests
{
    [Fact]
    public void EveryPresetIsWireSafeAndNamed()
    {
        Assert.NotEmpty(EnvironmentPresets.All);
        Assert.Equal(EnvironmentPresets.All.Count, EnvironmentPresets.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.All(EnvironmentPresets.All, preset =>
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name));
            Assert.False(string.IsNullOrWhiteSpace(preset.Provenance));
            preset.Validate();

            using var writer = new PacketWriter();
            preset.Sky.WriteTo(writer);
            Assert.Equal(
                SkySettings.EmptyTextureWireLength + preset.Sky.CloudDensityTexture.Length,
                writer.Position);

            // Every preset names a texture that actually ships in this build's packs: only
            // sky_Z_clouds.dds exists (sky_Z_Clouds_RainyDay.xml's sky_Z_clouds02.dds does not).
            Assert.Equal(SkySettings.ZCloudDensityTexture, preset.Sky.CloudDensityTexture);

            // The lighting table must be the one whose key vocabulary matches this build's parser.
            Assert.Equal(LightingTable.Z2, preset.LightingFile);

            // docs/38 §7: the zoning-burst change stays opt-in on every shipped preset.
        });
    }

    [Fact]
    public void PresetLookupIsCaseInsensitiveAndFallsBackToTheDefault()
    {
        Assert.Same(EnvironmentPresets.Aug2017Clear, EnvironmentPresets.ByName("aug2017clear"));
        Assert.Same(EnvironmentPresets.Aug2017CloudyDay, EnvironmentPresets.ByName("Aug2017CloudyDay"));
        Assert.Same(EnvironmentPresets.Aug2017Vivid, EnvironmentPresets.ByName("aug2017vivid"));
        Assert.Same(EnvironmentPresets.Aug2017VividClarity50, EnvironmentPresets.ByName("Aug2017VividClarity50"));
        Assert.Same(EnvironmentPresets.Aug2017VividClarity75, EnvironmentPresets.ByName("aug2017vividclarity75"));
        Assert.Same(EnvironmentPresets.Aug2017VividClarity100, EnvironmentPresets.ByName("AUG2017VIVIDCLARITY100"));

        // Every preset in the table resolves by its own name - the CRANBERRY_SKY contract.
        Assert.All(
            EnvironmentPresets.All,
            preset => Assert.Same(preset, EnvironmentPresets.ByName(preset.Name)));
        Assert.Same(EnvironmentPresets.Default, EnvironmentPresets.FromNameOrDefault(null));
        Assert.Same(EnvironmentPresets.Default, EnvironmentPresets.FromNameOrDefault("nonsense"));
        Assert.False(EnvironmentPresets.TryByName("nonsense", out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => EnvironmentPresets.ByName("nonsense"));
    }

    /// <summary>
    /// docs/38 §5.1: the five reasons the old scene was grey, each one now a different value.
    /// </summary>
    [Fact]
    public void Aug2017ClearFixesEachOfTheFiveDocumentedGreyCauses()
    {
        SkySettings clear = EnvironmentPresets.Aug2017Clear.Sky;
        SkySettings grey = EnvironmentPresets.LegacyD18Grey.Sky;

        // L1: the sky had no clouds and cast no cloud shadows.
        Assert.Equal(0f, grey.CloudWeight0 + grey.CloudWeight1 + grey.CloudWeight2 + grey.CloudWeight3);
        Assert.Equal(0f, grey.CloudShadows);
        Assert.True(clear.CloudWeight1 + clear.CloudWeight2 + clear.CloudWeight3 > 0f);
        Assert.Equal(0.625f, clear.CloudShadows);

        // L2: the silver lining was switched off (client default 0.5, author's value 20.0).
        Assert.Equal(0f, grey.CloudSilverLiningBrightness);
        Assert.Equal(20f, clear.CloudSilverLiningBrightness);
        Assert.Equal(20.5f, clear.CloudSilverLiningThickness);

        // L3: the sun axis was a triple the client never ships.
        Assert.Equal(45f, grey.SunAxisX);
        Assert.Equal(0f, grey.SunAxisY);
        Assert.Equal(ClientWeatherDefaults.SunAxisX, clear.SunAxisX);
        Assert.Equal(ClientWeatherDefaults.SunAxisY, clear.SunAxisY);
        Assert.Equal(ClientWeatherDefaults.SunAxisZ, clear.SunAxisZ);

        // L4: the clock sat two hours past the authored solar noon.
        Assert.Equal(14, EnvironmentPresets.LegacyD18Grey.Clock.HourUtc);
        Assert.Equal(12, EnvironmentPresets.Aug2017Clear.Clock.HourUtc);

        // L5: the fog column was 3.5x the author's midday gradient.
        Assert.Equal(0.0144f, grey.FogGradient);
        Assert.Equal(0.05f, clear.FogGradient);
        Assert.Equal(1.0e-4f, clear.FogDensity);
    }

    /// <summary>
    /// docs/38 §5.1: the cloud presets carry <c>sky_Z_Clouds_RainyDay.xml</c>'s authored values,
    /// and the rain preset differs from the cloudy one in exactly one field.
    /// </summary>
    [Fact]
    public void CloudPresetsCarryTheAuthoredRainyDayCloudValues()
    {
        SkySettings cloudy = EnvironmentPresets.Aug2017CloudyDay.Sky;

        Assert.Equal(0.015f, cloudy.CloudWeight0);
        Assert.Equal(0.5f, cloudy.CloudWeight1);
        Assert.Equal(0.5f, cloudy.CloudWeight2);
        Assert.Equal(0.1f, cloudy.CloudWeight3);
        Assert.Equal(4.00f, cloudy.CumulusCloudTiling);
        Assert.Equal(4.00f, cloudy.StratusCloudTiling);
        Assert.Equal(0.003f, cloudy.CumulusCloudScrollU);
        Assert.Equal(-0.006f, cloudy.CumulusCloudScrollV);
        Assert.Equal(0.01f, cloudy.CloudAnimationSpeed);
        Assert.Equal(10.5f, cloudy.CloudSilverLiningThickness);
        Assert.Equal(1.0f, cloudy.CloudSilverLiningBrightness);
        Assert.Equal(0f, cloudy.GlobalPrecipitation);

        Assert.Equal(cloudy with { GlobalPrecipitation = 1f }, EnvironmentPresets.Aug2017RainyDay.Sky);

        // TEMPERATURE >= 35F puts the client's rain/snow selector fully on rain (docs/38 §3 wire 5).
        Assert.True(EnvironmentPresets.Aug2017RainyDay.Sky.Temperature >= 35f);
        Assert.True(EnvironmentPresets.Aug2017RainyDay.Sky.RainRampUpTimeSeconds > 0f);

        // The foggy preset differs only in its fog triple and its clock hour.
        SkySettings foggy = EnvironmentPresets.Aug2017FoggyDay.Sky;
        Assert.Equal(cloudy with { FogDensity = 0.005f, FogFloor = 75f, FogGradient = 0.05f }, foggy);
        Assert.Equal(14, EnvironmentPresets.Aug2017FoggyDay.Clock.HourUtc);
    }

    /// <summary>
    /// docs/50 §4.2 as amended by docs/82 §4: <c>Aug2017Vivid</c> is now <c>Aug2017KotkClear</c> -
    /// the new default - plus <b>exactly two</b> DESIGNED fog floats. The third, the
    /// <c>CloudShadows</c> 0.35 hedge, is retired: the owner's ruling supplies 0 and the default
    /// carries it. Pinned as an equality against a <c>with</c>-expression so a later edit cannot
    /// widen the deviation set without this failing.
    /// </summary>
    [Fact]
    public void Aug2017VividDiffersFromTheDefaultInExactlyTwoFogFields()
    {
        EnvironmentSettings baseline = EnvironmentPresets.Aug2017KotkClear;
        EnvironmentSettings vivid = EnvironmentPresets.Aug2017Vivid;

        Assert.Equal(
            baseline.Sky with
            {
                FogDensity = 5.0e-5f,
                FogFloor = 0.25f,
            },
            vivid.Sky);

        // The two, spelled out, with the value each one moved away from.
        Assert.Equal(1.0e-4f, baseline.Sky.FogDensity);
        Assert.Equal(5.0e-5f, vivid.Sky.FogDensity);
        Assert.Equal(1f, baseline.Sky.FogFloor);
        Assert.Equal(0.25f, vivid.Sky.FogFloor);

        // The retired hedge: the default now zeroes it on the owner's ruling, and the vivid preset
        // simply inherits that rather than carrying a designed 0.35 of its own.
        Assert.Equal(0f, baseline.Sky.CloudShadows);
        Assert.Equal(0f, vivid.Sky.CloudShadows);
        Assert.Equal(0.625f, EnvironmentPresets.Aug2017Clear.Sky.CloudShadows);

        // docs/50 §2.6 rank 1: FogGradient has no headroom left - it is the author's own daytime
        // maximum - so the vivid preset must not touch it. Nor the silver lining, already his
        // brightest authored highlight.
        Assert.Equal(0.05f, vivid.Sky.FogGradient);
        Assert.Equal(20f, vivid.Sky.CloudSilverLiningBrightness);
        Assert.Equal(20.5f, vivid.Sky.CloudSilverLiningThickness);

        // docs/50 §4.2: SkyClarity ships at the client constructor's own value, not a guess.
        Assert.Equal(ClientWeatherDefaults.SkyClarity, vivid.Sky.SkyClarity);

        // Clock, lighting table and the zoning-burst flag stay exactly where the default has them.
        Assert.Equal(baseline.Clock, vivid.Clock);
        Assert.Equal(LightingTable.Z2, vivid.LightingFile);
        Assert.Contains("DESIGNED", vivid.Provenance, StringComparison.Ordinal);
    }

    /// <summary>
    /// The brief's real guard: adding <c>Aug2017Vivid</c> must leave the two A/B controls alone.
    /// <c>SkySettingsWireTests</c> pins both to their shipped hex; this pins them structurally, and
    /// pins that the default did not move.
    /// </summary>
    [Fact]
    public void Aug2017VividLeavesTheTwoAbControlsAndTheDefaultUntouched()
    {
        // Aug2017Clear is still exactly the SkySettings defaults - nothing was hoisted out of it
        // into the new preset.
        Assert.Equal(new SkySettings(), EnvironmentPresets.Aug2017Clear.Sky);
        Assert.Equal(FrozenSkyClock.SolarNoon, EnvironmentPresets.Aug2017Clear.Clock);

        // LegacyD18Grey, the "before" half of the pair, is untouched in the fields the new preset
        // moves.
        SkySettings grey = EnvironmentPresets.LegacyD18Grey.Sky;
        Assert.Equal(BitConverter.Int32BitsToSingle(0x3935C0CE), grey.FogDensity);
        Assert.Equal(10f, grey.FogFloor);
        Assert.Equal(0f, grey.CloudShadows);
        Assert.Equal(1502805600UL, EnvironmentPresets.LegacyD18Grey.Clock.FixedUnixTime);

        // docs/50 I2 as amended by docs/82 §4: the default is still never the DESIGNED preset. It
        // moved from Aug2017Clear to Aug2017KotkClear, which is Aug2017Clear plus an owner ruling,
        // not a designer's guess - and Aug2017Vivid is still not it.
        Assert.Same(EnvironmentPresets.Aug2017KotkClear, EnvironmentPresets.Default);
        Assert.Same(EnvironmentPresets.Aug2017KotkClear, EnvironmentPresets.FromNameOrDefault(null));
        Assert.NotSame(EnvironmentPresets.Aug2017Vivid, EnvironmentPresets.Default);
    }

    /// <summary>
    /// docs/50 §3 wire guards: 5e-5 and 0.25 are both finite and positive, so the log-lerp guard in
    /// <see cref="SkySettings.Validate"/> passes unchanged and the struct is still 152 bytes.
    /// </summary>
    [Fact]
    public void Aug2017VividPassesTheFogPositivityGuard()
    {
        EnvironmentPresets.Aug2017Vivid.Validate();

        using var writer = new PacketWriter();
        EnvironmentPresets.Aug2017Vivid.Sky.WriteTo(writer);
        Assert.Equal(152, writer.Position);

        // The guard still bites if the thinning is ever taken all the way to the author's literal 0.
        Assert.Throws<InvalidOperationException>(
            () => (EnvironmentPresets.Aug2017Vivid.Sky with { FogFloor = 0f }).Validate());
    }

    /// <summary>
    /// docs/50 §5 step 5: the <c>SKYCLARITY</c> sweep. Each sibling differs from
    /// <c>Aug2017Vivid</c> in that one field and in nothing else, so four screenshots from one fixed
    /// camera measure one field.
    /// </summary>
    [Theory]
    [InlineData(0.50f, "Aug2017VividClarity50")]
    [InlineData(0.75f, "Aug2017VividClarity75")]
    [InlineData(1.00f, "Aug2017VividClarity100")]
    public void SkyClaritySweepMovesOnlySkyClarity(float clarity, string name)
    {
        EnvironmentSettings step = EnvironmentPresets.ByName(name);
        EnvironmentSettings vivid = EnvironmentPresets.Aug2017Vivid;

        Assert.Equal(clarity, step.Sky.SkyClarity);
        Assert.Equal(vivid.Sky with { SkyClarity = clarity }, step.Sky);
        Assert.Equal(vivid.Clock, step.Clock);
        Assert.Equal(vivid.LightingFile, step.LightingFile);
        Assert.Contains("SKYCLARITY", step.Provenance, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sweep's first step is the shipped preset itself, at the client's own 0.25, so the four
    /// screenshots are 0.25 / 0.50 / 0.75 / 1.00 and no number was invented.
    /// </summary>
    [Fact]
    public void TheSkyClaritySweepStartsAtTheClientsOwnDefault()
    {
        Assert.Equal(0.25f, ClientWeatherDefaults.SkyClarity);
        Assert.Equal(0.25f, EnvironmentPresets.Aug2017Vivid.Sky.SkyClarity);

        float[] sweep = EnvironmentPresets.All
            .Where(preset => preset.Name.StartsWith("Aug2017Vivid", StringComparison.Ordinal))
            .Select(preset => preset.Sky.SkyClarity)
            .ToArray();

        Assert.Equal(new[] { 0.25f, 0.50f, 0.75f, 1.00f }, sweep);
    }

    /// <summary>
    /// docs/38 §5.2: 12:00Z is <c>1502798400</c>, and D18's 14:00Z was <c>1502805600</c> — the
    /// value currently hard-coded in <see cref="ZoneOptions.FixedUnixTime"/>, which is the
    /// cross-check that the date arithmetic here means what D16 meant.
    /// </summary>
    [Theory]
    [InlineData(9, 1502787600UL)]
    [InlineData(12, 1502798400UL)]
    [InlineData(14, 1502805600UL)]
    [InlineData(17, 1502816400UL)]
    public void FrozenClockProducesTheDocumentedEpochForEachSweepHour(int hourUtc, ulong expected)
    {
        Assert.Equal(expected, new FrozenSkyClock { HourUtc = hourUtc }.FixedUnixTime);
    }

    [Fact]
    public void DefaultClockIsSolarNoonAndD18sClockIsStillReproducible()
    {
        Assert.Equal(12, FrozenSkyClock.SolarNoon.HourUtc);
        Assert.Equal(1502798400UL, FrozenSkyClock.SolarNoon.FixedUnixTime);
        Assert.True(FrozenSkyClock.SolarNoon.Freeze);

        Assert.Equal(1502805600UL, FrozenSkyClock.LegacyD18Afternoon.FixedUnixTime);
        Assert.Equal(new ZoneOptions().FixedUnixTime, FrozenSkyClock.LegacyD18Afternoon.FixedUnixTime);
    }

    /// <summary>
    /// docs/38 §4.5, computed rather than asserted: of the 24 authored <c>sky_Z.xml</c> sun
    /// keyframes, 12:00 is the hour whose elevation sine sits closest to
    /// <c>Lighting_Z2.txt</c>'s <c>DayAngle = 0.700</c>. This is the whole argument for moving the
    /// frozen clock, so it is a test rather than a comment.
    /// </summary>
    [Fact]
    public void TwelveHundredIsTheHourClosestToTheAuthoredDayAngle()
    {
        Assert.Equal(12, ZSunArc.HourClosestToDayAngle());
        Assert.Equal(43.8f, ZSunArc.PitchDegreesAtHour(12));
        Assert.Equal(36.4f, ZSunArc.PitchDegreesAtHour(14));

        Assert.Equal(0.692, ZSunArc.ElevationSineAtHour(12), 3);
        Assert.Equal(0.593, ZSunArc.ElevationSineAtHour(14), 3);

        // 14:00 is measurably short of the pure [Day] grade; 12:00 is within 0.01 of it.
        Assert.True(
            Math.Abs(ZSunArc.ElevationSineAtHour(12) - ZSunArc.DayAngle)
            < Math.Abs(ZSunArc.ElevationSineAtHour(14) - ZSunArc.DayAngle));
        Assert.True(Math.Abs(ZSunArc.ElevationSineAtHour(12) - ZSunArc.DayAngle) < 0.01f);

        Assert.Equal(43.8f, EnvironmentPresets.Aug2017Clear.Clock.AuthoredSunPitchDegrees);
        Assert.Throws<ArgumentOutOfRangeException>(() => ZSunArc.PitchDegreesAtHour(24));
        Assert.Throws<ArgumentOutOfRangeException>(() => ZSunArc.PitchDegreesAtHour(-1));
    }

    /// <summary>
    /// The one call an integrator makes. It must move exactly four fields and leave the rest of
    /// <see cref="ZoneOptions"/> — the zoning, loot and equipment settings docs/32 warns about —
    /// untouched.
    /// </summary>
    [Fact]
    public void WithEnvironmentMovesOnlyTheFourEnvironmentFields()
    {
        var baseline = new ZoneOptions();
        ZoneOptions applied = baseline.WithEnvironment(EnvironmentPresets.Aug2017Clear);

        Assert.Equal(1502798400UL, applied.FixedUnixTime);
        Assert.True(applied.FreezeClock);
        Assert.Equal(LightingTable.Z2, applied.LightingFile);

        using var expected = new PacketWriter();
        EnvironmentPresets.Aug2017Clear.Sky.WriteTo(expected);
        using var actual = new PacketWriter();
        applied.Weather.WriteTo(actual);
        Assert.Equal(expected.Written.ToArray(), actual.Written.ToArray());

        // Nothing else moved.
        Assert.Equal(
            baseline with
            {
                Weather = applied.Weather,
                FixedUnixTime = applied.FixedUnixTime,
                FreezeClock = applied.FreezeClock,
                LightingFile = applied.LightingFile,
            },
            applied);
    }

    [Fact]
    public void WithEnvironmentAcceptsAPresetNameAndFallsBackToTheDefault()
    {
        var baseline = new ZoneOptions();

        Assert.Equal(
            baseline.WithEnvironment(EnvironmentPresets.Aug2017CloudyDay).LightingFile,
            baseline.WithEnvironment("aug2017cloudyday").LightingFile);
        Assert.Equal(
            baseline.WithEnvironment(EnvironmentPresets.Default).FixedUnixTime,
            baseline.WithEnvironment((string?)null).FixedUnixTime);
    }

    [Fact]
    public void WithEnvironmentRefusesASkyThatWouldBlackTheFrame()
    {
        var poisoned = EnvironmentPresets.Aug2017Clear with
        {
            Sky = EnvironmentPresets.Aug2017Clear.Sky with { FogDensity = 0f },
        };

        Assert.Throws<InvalidOperationException>(() => new ZoneOptions().WithEnvironment(poisoned));
    }

    [Fact]
    public void DescribeNamesThePresetAndTheClockItIsLitBy()
    {
        string description = EnvironmentPresets.Aug2017Clear.Describe();

        Assert.Contains("Aug2017Clear", description, StringComparison.Ordinal);
        Assert.Contains("2017-08-15 12:00Z", description, StringComparison.Ordinal);
        Assert.Contains("1502798400", description, StringComparison.Ordinal);
        Assert.Contains("Lighting_Z2.txt", description, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reference tables that carry docs/38's evidence into code: the client's own weather-struct
    /// constructor defaults (four of which docs/02 was missing) and the <c>Lighting_Z2.txt</c>
    /// <c>[Day]</c> block.
    /// </summary>
    [Fact]
    public void ReferenceTablesMatchTheDerivedClientValues()
    {
        Assert.Equal(0.5f, ClientWeatherDefaults.CloudShadows);
        Assert.Equal(-0.002f, ClientWeatherDefaults.CumulusCloudScrollU);
        Assert.Equal(0.002f, ClientWeatherDefaults.CloudAnimationSpeed);
        Assert.Equal(0.5f, ClientWeatherDefaults.CloudSilverLiningBrightness);
        Assert.Equal(1f, ClientWeatherDefaults.RainRampUpTimeSeconds);

        // The client's own defaults leave the fog triple unusable, which is why they are constants
        // and not a preset.
        Assert.Equal(0f, ClientWeatherDefaults.FogDensity);
        Assert.Equal(0f, ClientWeatherDefaults.FogFloor);

        Assert.Equal(LightingTable.Z2Day.IrisClampLow, LightingTable.Z2Day.IrisClampHigh);
        Assert.Equal(1.0f, LightingTable.Z2Day.SunShadows);
        Assert.Equal(15.58f, LightingTable.Z2Day.SunBrightness);
        Assert.Equal("z2_colorkey_day.dds", LightingTable.Z2Day.ColorGradingFilename);
        Assert.Equal("Lighting.txt", LightingTable.ClientBuiltInDefault);
        Assert.Empty(LightingTable.None);
    }
}
