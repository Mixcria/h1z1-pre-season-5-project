using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Lighting;

/// <summary>
/// The named looks a host can select (docs/38 §5). Every preset is built out of the August client's
/// own shipped data — the map author's <c>sky_Z*.xml</c> keyframes and the weather-struct
/// constructor <c>FUN_140a7c6d0</c> — with the one exception of
/// <see cref="LegacyD18Grey"/>, which reproduces what Cranberry sent before this lane so the owner
/// has a before/after pair.
/// </summary>
/// <remarks>
/// <b>Status: BUILT, not LIVE-VERIFIED.</b> Nothing here has a screenshot behind it yet; docs/38 §6
/// and docs/50 §5 are the verification plan, and <b>docs/50 §1 resolves the client-side confound
/// docs/38 §8 raised - in the server's favour</b>: <c>LightingQuality</c> has exactly two legal
/// values in this build (<c>0 = Low</c>, <c>1 = High</c>, client default 1), and during the 22:08
/// play-test the owner's <c>UserOptions.ini</c> carried <c>LightingQuality=0</c>,
/// <c>OverallQuality=1</c> (Low) and <c>AO</c> / <c>InteriorLighting</c> / <c>FogShadowsEnable</c> /
/// <c>MaxLocalShadows</c> all off. No preset here can recover a bright frame from that
/// configuration; docs/50 §1.5 is the fix, and no preset should be judged before it is applied.
/// <b>docs/82 §1 - one client key is still wrong, and it is the biggest one left.</b> As of
/// 2026-08-30 19:18 the owner's <c>UserOptions.ini</c> carries every lighting key docs/50 §1.5
/// asked for (<c>OverallQuality=3</c>, <c>LightingQuality=1</c>, <c>AO=1</c>,
/// <c>InteriorLighting=1</c>, <c>FogShadowsEnable=1</c>, <c>ShadowQuality=3</c>,
/// <c>MaxLocalShadows=4</c>) but still reads <c>RenderDistance=500.000000</c> - the absolute
/// minimum of the client's own <c>range=500|6000</c> slider, whose declared default is 1500.
/// An earlier revision of this remark asserted that key was already fixed; it was not.
/// <b>No preset here should be judged against a 500 m draw distance</b>: the far field is then a
/// haze wall at the clip plane whatever fog the server sends.
/// </remarks>
public static class EnvironmentPresets
{
    /// <summary>
    /// <b>The map author's own midday, kept as a named A/B - no longer the default</b> (docs/82 §4;
    /// <see cref="Aug2017KotkClear"/> took the default). A bright, clear, saturated August-2017 KOTK
    /// midday, assembled from the Z map author's own values at his own solar noon, clouds included.
    /// </summary>
    /// <remarks>
    /// Sky: <c>sky_Z_Clouds.xml</c> (Assets_015.pack, 1,667 B, crc <c>0x286bf350</c>)
    /// <c>TextureDensityWeights Time="12:00"</c> 0.0 / 0.005 / 0.15 / 0.1, <c>ShadowAlpha</c> 0.625,
    /// <c>CumulusTiling</c> 1.30, <c>StratusTiling</c> 1.00, <c>CumulusScroll</c> 0.001 / -0.002,
    /// <c>Animation Intensity</c> 0.035, <c>SilverLining</c> 20.5 / 20.0; fog and wind:
    /// <c>sky_Z.xml</c> (Assets_034.pack, crc <c>0xfc0e5711</c>) <c>Distribution Time="11:00"</c>
    /// 0.0001 / 0.05 / floor 0 (raised to 1 for the log-lerp) and <c>Wind</c> -0.6 / 0 / 0.4 at
    /// 2.099; sun axis and rain ramp: the client's own constructor <c>FUN_140a7c6d0</c>
    /// (38 / -15 / 0, ramp 1.0). Clock 12:00 UTC, where the authored sun pitch of 43.8° gives
    /// sin 0.692 against <c>Lighting_Z2.txt</c>'s <c>DayAngle</c> of 0.700.
    /// </remarks>
    public static EnvironmentSettings Aug2017Clear { get; } = new()
    {
        Name = "Aug2017Clear",
        Provenance = "sky_Z.xml + sky_Z_Clouds.xml @12:00, sun axis from FUN_140a7c6d0",
        Sky = new SkySettings(),
        Clock = FrozenSkyClock.SolarNoon,
        LightingFile = LightingTable.Z2,
    };

    /// <summary>
    /// <b>The default.</b> <see cref="Aug2017Clear"/> with the owner's standing no-clouds ruling
    /// applied: the four cloud density weights and the cloud-shadow alpha at 0. Five words of the
    /// 152-byte struct; every other float is still the map author's own 12:00 row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The ruling (rounds 28-29, recorded verbatim in the owner's own <c>Z1\Server\Zone\ZoneSky.cs</c>
    /// class remarks):</b> <i>"KOTK 2017 has NO weather system - the sky is permanently ~2 pm, clear,
    /// NO fog, NO clouds. Fog/clouds/weather are Just Survive 2016 leftovers."</i> D53 adopts the
    /// owner's design, and his ruling outranks the map author's authored weights for the
    /// <em>default</em> look. docs/38 cause L1 ("the sky has no clouds at all") is not wrong - it is
    /// superseded here, and <see cref="Aug2017Clear"/> keeps it as the A/B.
    /// </para>
    /// <para>
    /// <b>It is also the brighter of the two.</b> <c>CLOUDSHADOWS</c> is the only
    /// ground-<em>darkening</em> term in the whole struct (docs/50 §2.6 rank 4), so zeroing it is
    /// docs/50 §4.2's 0.35 "hedge" taken to a number that has a ruling behind it rather than a
    /// designer's guess. That is why <see cref="Aug2017Vivid"/> no longer carries the hedge.
    /// </para>
    /// <para>
    /// <b>What is deliberately NOT zeroed:</b> the silver-lining pair stays at the author's 20.5 /
    /// 20.0. With no cloud weights there is nothing for a silver lining to rim-light, so the two
    /// floats are inert - and leaving them where <see cref="Aug2017Clear"/> has them keeps the two
    /// presets exactly five words apart, so the owner's A/B measures one thing. The fog triple stays
    /// at the author's 1e-4 / 1 / 0.05: the ruling says "NO fog", but fog density 0 is
    /// <c>ln(0)</c> = -inf in the client's own log-lerp, i.e. a black frame, and
    /// <see cref="SkySettings.Validate"/> refuses it. <see cref="Aug2017Vivid"/> is the thin-fog step.
    /// </para>
    /// <para>
    /// <b>Not a black-screen risk.</b> Every session before D30 put exactly these five floats on the
    /// wire at 0 (<see cref="LegacyD18Grey"/> is byte-identical to what shipped then) and the world
    /// rendered - grey, but rendered. CLIENT-PROVEN by that history; the look itself is BUILT, not
    /// LIVE-VERIFIED.
    /// </para>
    /// </remarks>
    public static EnvironmentSettings Aug2017KotkClear { get; } = new()
    {
        Name = Rulings.Sky.DefaultPresetName,
        Provenance =
            "Aug2017Clear + the owner's no-clouds ruling (D53, docs/82 4): cloud weights 0-3 and "
            + "CLOUDSHADOWS at 0; every other float still the sky_Z author's own 12:00 row",
        Sky = Aug2017Clear.Sky with
        {
            CloudWeight0 = Rulings.Sky.KotkClearCloudWeight0,
            CloudWeight1 = Rulings.Sky.KotkClearCloudWeight1,
            CloudWeight2 = Rulings.Sky.KotkClearCloudWeight2,
            CloudWeight3 = Rulings.Sky.KotkClearCloudWeight3,
            CloudShadows = Rulings.Sky.KotkClearCloudShadows,
        },
        Clock = FrozenSkyClock.SolarNoon,
        LightingFile = LightingTable.Z2,
    };

    /// <summary>
    /// <see cref="Aug2017KotkClear"/> on the owner's <em>literal</em> "~2 pm" clock, so his ruling
    /// and docs/82 §5's counter-argument can be settled by two screenshots instead of by prose.
    /// </summary>
    /// <remarks>
    /// The owner ruled "permanently ~2 pm" and Z1 delivers it with
    /// <c>ZoneSky.TwoPmSeconds = 50_400</c>. docs/82 §5 recommends 12:00 at 1148 instead, because
    /// <c>Lighting_Z2.txt</c>'s <c>[SunAngleTimes] DayAngle = 0.700</c> is matched by the Z map's
    /// authored 12:00 sun pitch of 43.8° (sin 0.692) and missed at 14:00 (36.4°, sin 0.593), and
    /// <c>z2_colorkey_day.dds</c> is the only non-identity colour grade in the whole table - so 14:00
    /// would under-blend the one grade that adds punch. That argument carries an open UNCERTAIN
    /// (docs/50 §3.2: the elevation the packet's <c>SUNAXIS</c> + hour triple actually produces was
    /// never computed), which is exactly why this preset exists rather than an assertion.
    /// <c>CRANBERRY_SKY=Aug2017KotkClear1400</c>, one restart, his eyes decide.
    /// </remarks>
    public static EnvironmentSettings Aug2017KotkClear1400 { get; } = Aug2017KotkClear with
    {
        Name = "Aug2017KotkClear1400",
        Provenance =
            "Aug2017KotkClear on the owner's literal ~2pm clock (Z1 ZoneSky.TwoPmSeconds = 50400); "
            + "docs/82 5 argues for 12:00 at 1148 - this preset is the one-restart A/B that settles it",
        Clock = FrozenSkyClock.LegacyD18Afternoon,
    };

    /// <summary>
    /// <b>DESIGNED, not derived - the vibrancy knob.</b> <see cref="Aug2017KotkClear"/> with a
    /// thinner fog column. <b>Two</b> floats; everything else is composed from
    /// <see cref="Aug2017KotkClear"/> so the default, <see cref="Aug2017Clear"/> and
    /// <see cref="LegacyD18Grey"/> all stay byte-identical to what they were.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read docs/50 §0 first.</b> The dullness the owner reported is <em>not</em> on the wire:
    /// during the 22:08 play-test the client itself was rendering at <c>LightingQuality=0</c> (Low,
    /// the minimum of the two values this build accepts), <c>OverallQuality=1</c> (Low), with
    /// <c>AO</c>, <c>InteriorLighting</c>, <c>FogShadowsEnable</c> and <c>MaxLocalShadows</c> all
    /// off, while <see cref="Aug2017Clear"/> was correctly in force. docs/50 §1.5 is the fix; this
    /// preset is a small, reversible extra and must not be shipped as the answer.
    /// </para>
    /// <para>
    /// Every deviation, with its provenance (docs/50 §4.2):
    /// <list type="bullet">
    /// <item><description>
    /// <c>FogDensity</c> 1e-4 -> <b>5e-5</b>. DESIGNED - half the author's own <c>sky_Z.xml</c>
    /// <c>Density="0.0001"</c>. Aerial perspective is the strongest desaturator in the struct
    /// (docs/50 §2.6 rank 1), so halving it is the largest legitimate saturation gain the server
    /// has. Still &gt; 0, so the log-lerp in <c>FUN_142482ee0</c> stays safe.
    /// </description></item>
    /// <item><description>
    /// <c>FogFloor</c> 1.0 -> <b>0.25</b>. DESIGNED - a step toward the author's literal
    /// <c>Floor="0"</c>, which the log-lerp forbids. Lowers the band of full-density ground haze
    /// that flattens near-field colour.
    /// </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>docs/50 §4.2's third float is gone (docs/82 §4).</b> That bullet was a DESIGNED hedge -
    /// <c>CloudShadows</c> 0.625 -> 0.35 - carried while the owner's client was rendering at
    /// <c>ShadowQuality=Low</c>, and it was tagged "revert once docs/50 §1.5 is done". §1.5 is now
    /// done, and the owner's own no-clouds ruling supplies <b>0</b> rather than either number, so
    /// the field is set by <see cref="Aug2017KotkClear"/> and this preset inherits it. Rebasing onto
    /// the new default also keeps this preset a clean one-axis (fog) A/B against it.
    /// </para>
    /// <para>
    /// <c>FogGradient</c> stays at the author's 0.05 - his <em>highest</em> daytime value, so there
    /// is no headroom - and <c>SkyClarity</c> ships unchanged at the client constructor's 0.25: it
    /// is the one wire field named for the quality being asked for, but nothing in the client's data
    /// authors an alternative and its consumer was not located, so its direction of effect is
    /// unknown. See <see cref="Aug2017VividClarity100"/> for the sweep rather than a guessed value.
    /// </para>
    /// </remarks>
    public static EnvironmentSettings Aug2017Vivid { get; } = new()
    {
        Name = "Aug2017Vivid",
        Provenance = "Aug2017KotkClear + DESIGNED fog thinning (docs/50 4.2); not from client data",
        Sky = Aug2017KotkClear.Sky with
        {
            FogDensity = 5.0e-5f,
            FogFloor = 0.25f,
        },
        Clock = FrozenSkyClock.SolarNoon,
        LightingFile = LightingTable.Z2,
    };

    /// <summary><see cref="Aug2017Vivid"/> with <c>SKYCLARITY</c> at 0.50 - sweep step 2 of 4.</summary>
    /// <remarks>See <see cref="Aug2017VividClarity100"/> for why these three exist.</remarks>
    public static EnvironmentSettings Aug2017VividClarity50 { get; } = WithSkyClarity(Aug2017Vivid, 0.50f);

    /// <summary><see cref="Aug2017Vivid"/> with <c>SKYCLARITY</c> at 0.75 - sweep step 3 of 4.</summary>
    /// <remarks>See <see cref="Aug2017VividClarity100"/> for why these three exist.</remarks>
    public static EnvironmentSettings Aug2017VividClarity75 { get; } = WithSkyClarity(Aug2017Vivid, 0.75f);

    /// <summary><see cref="Aug2017Vivid"/> with <c>SKYCLARITY</c> at 1.00 - sweep step 4 of 4.</summary>
    /// <remarks>
    /// <c>SKYCLARITY</c> (struct byte 0x94) is the only wire field named for the quality the brief
    /// asks for, and it is the only one with <b>no authored value anywhere in the client's data</b>:
    /// the constructor <c>FUN_140a7c6d0</c> supplies 0.25 and nothing else does (docs/50 §2.6
    /// rank 6). Its consumer was not located, so <em>the direction of its effect is not known</em>
    /// and guessing a number would be dressing a guess as a value. These three siblings exist so
    /// docs/50 §5 step 5 - four screenshots from one fixed camera at 0.25 / 0.50 / 0.75 / 1.00 -
    /// can be run by restarting the host with <c>CRANBERRY_SKY</c> and nothing else, and so the
    /// shipped <see cref="Aug2017Vivid"/> can keep the client's own 0.25.
    /// </remarks>
    public static EnvironmentSettings Aug2017VividClarity100 { get; } = WithSkyClarity(Aug2017Vivid, 1.00f);

    /// <summary>
    /// The map's own overcast dressing: heavy cumulus, dull silver lining, thick low fog. No
    /// precipitation.
    /// </summary>
    /// <remarks>
    /// Clouds: <c>sky_Z_Clouds_RainyDay.xml</c> (Assets_205.pack, 2,286 B, crc <c>0x7127abfe</c>) —
    /// the file <c>sky_Z_CloudyDay.xml</c> and <c>sky_Z_FoggyDay.xml</c> both <c>&lt;Include&gt;</c>.
    /// Its weights are constant at every keyframe (0.015 / 0.5 / 0.5 / 0.1), tiling 4.00 / 4.00,
    /// scroll 0.003 / -0.006, <c>Animation Intensity</c> 0.01, <c>SilverLining</c> 10.5 / 1.0,
    /// <c>ShadowAlpha</c> 0.625. Fog: <c>sky_Z_CloudyDay.xml &lt;Distribution Time="7:00"
    /// Density="0.004" Gradient="0.05" Floor="25"&gt;</c> — its nearest authored daytime row; that
    /// file has no 12:00 keyframe, so no interpolation is invented here.
    /// <para>
    /// <b>Deviation from the source, DERIVED:</b> <c>sky_Z_Clouds_RainyDay.xml</c> names
    /// <c>&lt;DensityTexture File="sky_Z_clouds02.dds"/&gt;</c>, and that asset is <b>absent from
    /// this build's 50,502-entry pack index</b> (only <c>sky_Z_clouds.dds</c> ships). The preset
    /// keeps the shipped texture rather than naming a file the client cannot open.
    /// </para>
    /// </remarks>
    public static EnvironmentSettings Aug2017CloudyDay { get; } = new()
    {
        Name = "Aug2017CloudyDay",
        Provenance = "sky_Z_Clouds_RainyDay.xml clouds + sky_Z_CloudyDay.xml 07:00 fog",
        Sky = CloudySky,
        Clock = FrozenSkyClock.SolarNoon,
        LightingFile = LightingTable.Z2,
    };

    /// <summary>
    /// <see cref="Aug2017CloudyDay"/> with the rain switched on:
    /// <c>GLOBALPRECIPITATION = 1</c>, temperature 75 °F so the rain/snow selector is fully rain.
    /// </summary>
    /// <remarks>
    /// The rain ramp is the client's own 1.0 and must stay positive — <c>FUN_142483c50</c> divides
    /// by <c>RAINRAMPUPTIMESECONDS * 1000</c>, so D18's 0 would be a divide-by-zero here.
    /// <see cref="SkySettings.Validate"/> refuses the combination.
    /// <b>Unverified:</b> no live run has ever enabled precipitation on this server; this preset is
    /// the first one that would, and it should be tried only after <see cref="Aug2017Clear"/> has a
    /// screenshot.
    /// </remarks>
    public static EnvironmentSettings Aug2017RainyDay { get; } = new()
    {
        Name = "Aug2017RainyDay",
        Provenance = "Aug2017CloudyDay + GLOBALPRECIPITATION 1.0 (TEMPERATURE 75F = all rain)",
        Sky = CloudySky with { GlobalPrecipitation = 1f },
        Clock = FrozenSkyClock.SolarNoon,
        LightingFile = LightingTable.Z2,
    };

    /// <summary>
    /// The map's own fog dressing, at the one hour its author wrote a fog keyframe for.
    /// </summary>
    /// <remarks>
    /// Fog: <c>sky_Z_FoggyDay.xml &lt;Distribution Time="14:00" Density="0.005" Gradient="0.05"
    /// Floor="75"&gt;</c> — an exact authored row, which is why this preset's clock is 14:00 and not
    /// 12:00. Clouds and wind as <see cref="Aug2017CloudyDay"/> (that file includes the same clouds).
    /// Note the trade-off recorded in docs/38 §4.5: at 14:00 the authored sun pitch is 36.4°
    /// (sin 0.593), short of <c>DayAngle</c> 0.700, so the <c>[Day]</c> grade may not be fully
    /// engaged. Fog is the point of this preset; the grade is not.
    /// </remarks>
    public static EnvironmentSettings Aug2017FoggyDay { get; } = new()
    {
        Name = "Aug2017FoggyDay",
        Provenance = "sky_Z_Clouds_RainyDay.xml clouds + sky_Z_FoggyDay.xml 14:00 fog",
        Sky = CloudySky with { FogDensity = 0.005f, FogFloor = 75f, FogGradient = 0.05f },
        Clock = new FrozenSkyClock { HourUtc = 14 },
        LightingFile = LightingTable.Z2,
    };

    /// <summary>
    /// <b>The A/B control, not a recommendation.</b> Byte-for-byte what Cranberry sent before this
    /// lane: D16/D18's fixed-grey compromise (<c>WeatherSettings.Kotk2017</c>) on D18's 14:00 clock.
    /// </summary>
    /// <remarks>
    /// Kept so the owner's "before" screenshot can be reproduced on demand rather than from memory,
    /// and so a regression in the new presets can be isolated in one restart. Its five defects are
    /// docs/38 §0 L1-L5: no clouds at all, no silver lining, a sun axis the client never ships, a
    /// clock two hours past the authored solar noon, and a fog column 3.5x the authored midday one.
    /// <c>EnvironmentPresetTests</c> asserts it is byte-identical to <c>WeatherSettings.Kotk2017</c>.
    /// </remarks>
    public static EnvironmentSettings LegacyD18Grey { get; } = new()
    {
        Name = "LegacyD18Grey",
        Provenance = "D16/D18 fixed-grey compromise, reproduced for the before/after pair",
        Sky = new SkySettings
        {
            TransitionTime = 1f,
            // The exact float bits D18 shipped (0x3935C0CE ~ 1.7334e-4), preserved rather than
            // re-rounded so the A/B is byte-identical to the old wire.
            FogDensity = BitConverter.Int32BitsToSingle(0x3935C0CE),
            FogFloor = 10f,
            FogGradient = 0.0144f,
            GlobalPrecipitation = 0f,
            Temperature = 75f,
            Overcast = 0f,
            CloudWeight0 = 0f,
            CloudWeight1 = 0f,
            CloudWeight2 = 0f,
            CloudWeight3 = 0f,
            CloudShadows = 0f,
            SunAxisX = 45f,
            SunAxisY = 0f,
            SunAxisZ = 0f,
            WindDirX = -1f,
            WindDirY = -0.05f,
            WindDirZ = -1f,
            WindSpeed = 3f,
            RainMinStrength = 0f,
            RainRampUpTimeSeconds = 0f,
            CloudDensityTexture = SkySettings.ZCloudDensityTexture,
            CumulusCloudTiling = 0.3f,
            CumulusCloudScrollU = 0f,
            CumulusCloudScrollV = 0f,
            CumulusCloudHeight = 1000f,
            StratusCloudTiling = 0.2f,
            StratusCloudScrollU = 0f,
            StratusCloudScrollV = 0.002f,
            StratusCloudHeight = 8000f,
            CloudAnimationSpeed = 0.09f,
            SkyClarity = 0.25f,
            CloudSilverLiningThickness = 7f,
            CloudSilverLiningBrightness = 0f,
        },
        Clock = FrozenSkyClock.LegacyD18Afternoon,
        LightingFile = LightingTable.Z2,
    };

    /// <summary>
    /// The preset a host gets when it selects nothing: <see cref="Aug2017KotkClear"/> (docs/82 §4 -
    /// the owner's no-clouds ruling; it was <see cref="Aug2017Clear"/> under D30).
    /// </summary>
    public static EnvironmentSettings Default => Aug2017KotkClear;

    /// <summary>Every preset, in the order a sweep should try them.</summary>
    public static IReadOnlyList<EnvironmentSettings> All { get; } =
    [
        Aug2017KotkClear,
        Aug2017KotkClear1400,
        Aug2017Clear,
        Aug2017Vivid,
        Aug2017VividClarity50,
        Aug2017VividClarity75,
        Aug2017VividClarity100,
        Aug2017CloudyDay,
        Aug2017RainyDay,
        Aug2017FoggyDay,
        LegacyD18Grey,
    ];

    /// <summary>Preset names, for a host's help text.</summary>
    public static IReadOnlyList<string> Names { get; } = All.Select(preset => preset.Name).ToArray();

    /// <summary>Looks a preset up by <see cref="EnvironmentSettings.Name"/>, case-insensitively.</summary>
    public static bool TryByName(string? name, out EnvironmentSettings preset)
    {
        foreach (EnvironmentSettings candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                preset = candidate;
                return true;
            }
        }

        preset = Default;
        return false;
    }

    /// <summary>Looks a preset up by name; throws when the name is not one of <see cref="Names"/>.</summary>
    public static EnvironmentSettings ByName(string name) =>
        TryByName(name, out EnvironmentSettings preset)
            ? preset
            : throw new ArgumentOutOfRangeException(
                nameof(name), name, $"Unknown sky preset. Known presets: {string.Join(", ", Names)}.");

    /// <summary>
    /// Host-friendly selection: an unset or unknown name falls back to <see cref="Default"/> rather
    /// than throwing, which is what an environment-variable switch wants
    /// (<c>CRANBERRY_SKY=Aug2017CloudyDay</c>).
    /// </summary>
    public static EnvironmentSettings FromNameOrDefault(string? name)
    {
        TryByName(name, out EnvironmentSettings preset);
        return preset;
    }

    /// <summary>
    /// One sweep step: <paramref name="preset"/> with a different <c>SKYCLARITY</c> and a name that
    /// says so. Nothing else moves, so the sweep measures one field and only that field.
    /// </summary>
    private static EnvironmentSettings WithSkyClarity(EnvironmentSettings preset, float skyClarity) =>
        preset with
        {
            Name = FormattableString.Invariant($"{preset.Name}Clarity{skyClarity * 100f:00}"),
            Provenance = FormattableString.Invariant(
                $"{preset.Provenance}; SKYCLARITY {skyClarity:0.00} (DESIGNED sweep, docs/50 5 step 5)"),
            Sky = preset.Sky with { SkyClarity = skyClarity },
        };

    /// <summary>
    /// The clouds shared by <see cref="Aug2017CloudyDay"/>, <see cref="Aug2017RainyDay"/> and
    /// <see cref="Aug2017FoggyDay"/>, all of which trace to <c>sky_Z_Clouds_RainyDay.xml</c>.
    /// </summary>
    private static SkySettings CloudySky => new()
    {
        FogDensity = 0.004f,
        FogFloor = 25f,
        FogGradient = 0.05f,
        CloudWeight0 = 0.015f,
        CloudWeight1 = 0.5f,
        CloudWeight2 = 0.5f,
        CloudWeight3 = 0.1f,
        CloudShadows = 0.625f,
        CumulusCloudTiling = 4.00f,
        CumulusCloudScrollU = 0.003f,
        CumulusCloudScrollV = -0.006f,
        StratusCloudTiling = 4.00f,
        CloudAnimationSpeed = 0.01f,
        CloudSilverLiningThickness = 10.5f,
        CloudSilverLiningBrightness = 1.0f,
    };
}

/// <summary>
/// The weather struct's own constructor defaults, decoded byte by byte from
/// <c>FUN_140a7c6d0</c> (docs/38 §3). Reference values, deliberately <b>not</b> a preset: the
/// client's own defaults leave fog density and floor at 0, which the log-lerp in
/// <c>FUN_142482ee0</c> turns into a black frame, so they can never go on the wire as they stand.
/// </summary>
/// <remarks>
/// Four of these were missing from docs/02's 2026-08-28 row and are new as of docs/38:
/// <see cref="CloudShadows"/>, <see cref="CumulusCloudScrollU"/>,
/// <see cref="CloudAnimationSpeed"/> and <see cref="CloudSilverLiningBrightness"/>.
/// </remarks>
public static class ClientWeatherDefaults
{
    /// <summary>0x00 = 1.0.</summary>
    public const float TransitionTime = 1f;

    /// <summary>0x04 = 0.0 — not wire-safe (log-lerp).</summary>
    public const float FogDensity = 0f;

    /// <summary>0x08 = 0.0 — not wire-safe (log-lerp).</summary>
    public const float FogFloor = 0f;

    /// <summary>0x0c = 1e-4.</summary>
    public const float FogGradient = 1e-4f;

    /// <summary>0x48 = 0.0.</summary>
    public const float GlobalPrecipitation = 0f;

    /// <summary>0x38 = 75.0 °F.</summary>
    public const float Temperature = 75f;

    /// <summary>0x20 = 0.0.</summary>
    public const float Overcast = 0f;

    /// <summary>0x24 / 0x28 / 0x2c / 0x30 all 0.0.</summary>
    public const float CloudWeight = 0f;

    /// <summary>0x34 = <b>0.5</b> (new in docs/38; docs/02's earlier row omitted it).</summary>
    public const float CloudShadows = 0.5f;

    /// <summary>0x3c = 38.0 degrees.</summary>
    public const float SunAxisX = 38f;

    /// <summary>0x40 = -15.0 degrees.</summary>
    public const float SunAxisY = -15f;

    /// <summary>0x44 = 0.0 degrees.</summary>
    public const float SunAxisZ = 0f;

    /// <summary>0x10 / 0x14 / 0x18 = 1 / 0 / 0, speed 0x1c = 0.0.</summary>
    public static (float X, float Y, float Z, float Speed) Wind => (1f, 0f, 0f, 0f);

    /// <summary>0x4c = 0.0.</summary>
    public const float RainMinStrength = 0f;

    /// <summary>0x50 = 1.0 — a divisor, so never send 0 with precipitation on.</summary>
    public const float RainRampUpTimeSeconds = 1f;

    /// <summary>0x70 = 0.2.</summary>
    public const float CumulusCloudTiling = 0.2f;

    /// <summary>0x74 = <b>-0.002</b> (new in docs/38).</summary>
    public const float CumulusCloudScrollU = -0.002f;

    /// <summary>0x78 = 0.0.</summary>
    public const float CumulusCloudScrollV = 0f;

    /// <summary>0x7c = 1000.0.</summary>
    public const float CumulusCloudHeight = 1000f;

    /// <summary>0x80 = 0.2.</summary>
    public const float StratusCloudTiling = 0.2f;

    /// <summary>0x84 = 0.0.</summary>
    public const float StratusCloudScrollU = 0f;

    /// <summary>0x88 = 0.002.</summary>
    public const float StratusCloudScrollV = 0.002f;

    /// <summary>0x8c = 8000.0.</summary>
    public const float StratusCloudHeight = 8000f;

    /// <summary>0x90 = <b>0.002</b> (new in docs/38).</summary>
    public const float CloudAnimationSpeed = 0.002f;

    /// <summary>0x94 = 0.25.</summary>
    public const float SkyClarity = 0.25f;

    /// <summary>0x98 = 7.0.</summary>
    public const float CloudSilverLiningThickness = 7f;

    /// <summary>0x9c = <b>0.5</b> (new in docs/38; Cranberry has been sending 0).</summary>
    public const float CloudSilverLiningBrightness = 0.5f;
}
