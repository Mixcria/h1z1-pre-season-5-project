using Cranberry.Protocol;
using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Lighting;

/// <summary>
/// The August client's weather/sky struct, one named property per wire field, with the source of
/// every value recorded next to it (docs/38 §3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists next to <see cref="WeatherSettings"/>.</b> <see cref="WeatherSettings"/>
/// carries D18's fixed-grey compromise as its property defaults, and three shipped tests assert the
/// byte-exact output of <c>WeatherSettings.Kotk2017</c>. Changing those defaults in place would
/// rewrite the meaning of every one of them. <see cref="SkySettings"/> is therefore a parallel,
/// richer model whose own defaults are the evidence-sourced August values, plus
/// <see cref="ToWeatherSettings"/> as the adapter that lets the existing packet writers
/// (<c>SendZoneDetails</c>, <c>UpdateWeatherData</c>, <c>ClientBeginZoning</c>) stay untouched.
/// <see cref="WriteTo"/> here and <c>WeatherSettings.WriteTo</c> emit identical bytes for identical
/// values — <c>SkySettingsWireTests</c> is the proof.
/// </para>
/// <para>
/// <b>Layout.</b> Parser <c>FUN_140a482b0</c>: 21 floats, one counted string, 12 floats = 137 bytes
/// with an empty texture name, 152 with <c>sky_Z_clouds.dds</c>. Field names are the client's own,
/// from the hash table at <c>0x143efca2c</c> resolved to struct offsets by <c>FUN_142481b80</c>;
/// the "client default" quoted on each property is the struct constructor <c>FUN_140a7c6d0</c>,
/// decoded byte by byte in docs/38 §3.
/// </para>
/// <para>
/// <b>Status: BUILT, not LIVE-VERIFIED.</b> Every value below is DERIVED from the August client's
/// own shipped data or its own constructor, but no screenshot exists yet. docs/38 §6 is the
/// verification plan.
/// </para>
/// </remarks>
public sealed record SkySettings
{
    /// <summary>
    /// Bytes of the struct on the wire with an empty <see cref="CloudDensityTexture"/>:
    /// 21 floats (84) + the string's i32 length (4) + 12 floats (48) = <b>136</b>.
    /// </summary>
    /// <remarks>
    /// Correction: the class comment on <see cref="WeatherSettings"/> says 137, which is off by
    /// one. The measured length with <c>sky_Z_clouds.dds</c> is 136 + 16 = 152, and that agrees with
    /// the shipped <c>GatewayPacketTests</c> assertion of 153 bytes for <c>UpdateWeatherData</c>
    /// (152 + the 0xCA opcode) and 225 for the minimal <c>SendZoneDetails</c>.
    /// </remarks>
    public const int EmptyTextureWireLength = 136;

    /// <summary>The only cloud density texture this build ships for the Z map.</summary>
    /// <remarks>
    /// <c>sky_Z_clouds.dds</c>, Assets_197.pack, offset 15,454,122, 262,272 bytes, crc
    /// <c>0x3e9657c6</c> (out/pack-index.txt). The author's own <c>sky_Z_Clouds.xml</c> names it in
    /// <c>&lt;DensityTexture File="sky_Z_clouds.dds"/&gt;</c>.
    /// <para>
    /// DERIVED, and a correction to the client's own data: <c>sky_Z_Clouds_RainyDay.xml</c> names
    /// <c>sky_Z_clouds02.dds</c>, which <b>does not exist anywhere in the 50,502-asset pack index of
    /// this build</b>. The overcast/rain/fog presets therefore keep this texture rather than the
    /// name their source XML asks for; see <see cref="EnvironmentPresets.Aug2017CloudyDay"/>.
    /// </para>
    /// </remarks>
    public const string ZCloudDensityTexture = "sky_Z_clouds.dds";

    // ---- wire 0-3: blend and fog ------------------------------------------------------------

    /// <summary>
    /// Wire 0, struct byte 0x00. Seconds for the sky to blend from the previous struct to this one.
    /// Client default 1.0.
    /// </summary>
    /// <remarks>
    /// A <b>divisor</b> in <c>FUN_1424835f0</c> (<c>accum / (to - from)</c>); zero gives 0/0 and a
    /// black frame. <see cref="WriteTo"/> refuses non-positive values.
    /// </remarks>
    public float TransitionTime { get; init; } = 1f;

    /// <summary>
    /// Wire 1, struct byte 0x04. Height-fog density at the floor. Client default 0.0.
    /// Cranberry's value: <c>sky_Z.xml</c> <c>&lt;Fog&gt;&lt;Distribution Time="11:00"…"15:00"
    /// Density="0.0001"&gt;</c> — the map author's own midday air.
    /// </summary>
    /// <remarks>
    /// <b>Log-lerped</b> (<c>expf(lerp(logf(a), logf(b), t))</c>, <c>FUN_142482ee0</c>): must be
    /// &gt; 0 or <c>logf(0)</c> turns the fog shader constant into NaN and the whole frame black.
    /// </remarks>
    public float FogDensity { get; init; } = 1.0e-4f;

    /// <summary>
    /// Wire 2, struct byte 0x08. World height at which fog reaches full density. Client default 0.0.
    /// Cranberry's value: the author's midday <c>Floor="0"</c> is illegal for the log-lerp, so the
    /// smallest safe positive value. Anything in 1-10 stays inside the author's intent — he only
    /// raises the floor to 25-35 at dawn/dusk. <b>Tunable.</b>
    /// </summary>
    public float FogFloor { get; init; } = 1f;

    /// <summary>
    /// Wire 3, struct byte 0x0c. Falloff of fog with height; higher = clearer air above the floor.
    /// Client default 1e-4. Cranberry's value: the author's midday <c>Gradient="0.05"</c>
    /// (<c>sky_Z.xml</c>). D18 sent 0.0144, a 3.5x thicker column — docs/38 cause <b>L5</b>.
    /// </summary>
    public float FogGradient { get; init; } = 0.05f;

    // ---- wire 4-6: precipitation and overcast ------------------------------------------------

    /// <summary>
    /// Wire 4, struct byte 0x48. Master rain/snow switch. Client default 0.0, kept: D16's "no
    /// weather" survives as the default.
    /// </summary>
    /// <remarks>
    /// <c>FUN_142483c50</c>: <c>precip &lt;= 0</c> writes <c>client+0xdc = 0</c>, which disables the
    /// <c>RenderViewMask</c> / <c>RainReadGBuffers</c> / <c>RainWriteGBuffers</c> passes outright.
    /// </remarks>
    public float GlobalPrecipitation { get; init; } = Rulings.Sky.GlobalPrecipitation;

    /// <summary>
    /// Wire 5, struct byte 0x38. Degrees Fahrenheit. Client default 75.0, kept.
    /// </summary>
    /// <remarks>
    /// Not a HUD number — the <b>rain-vs-snow selector</b>. <c>FUN_142483c50</c>:
    /// <c>t = clamp(min(|T|, T - 30) * 0.2, 0, 1)</c>, rain strength scaled by <c>t</c> and snow by
    /// <c>1 - t</c> (<c>DAT_14311d7bc = 30.0</c>, <c>DAT_14312af3c = 0.2</c>). At or above 35 °F it
    /// is all rain; at or below 30 °F all snow.
    /// </remarks>
    public float Temperature { get; init; } = 75f;

    /// <summary>
    /// Wire 6, struct byte 0x20. Overcast blend of the sky dome. Client default 0.0, kept — a clear
    /// day, and the shipped Z sky XMLs carry no authored counterpart for this field.
    /// </summary>
    public float Overcast { get; init; }

    // ---- wire 7-11: cloud layers -------------------------------------------------------------

    /// <summary>
    /// Wire 7, struct byte 0x24. Density weight for channel <b>R</b> of the cloud texture =
    /// Cumulus layer 1. Client default 0.0. Cranberry's value:
    /// <c>sky_Z_Clouds.xml &lt;TextureDensityWeights Time="12:00" R-Cumulus1="0.0"&gt;</c>.
    /// </summary>
    public float CloudWeight0 { get; init; }

    /// <summary>
    /// Wire 8, struct byte 0x28. Channel <b>G</b> = Cumulus 2. Client default 0.0. Cranberry's
    /// value: the same 12:00 row, <c>G-Cumulus2="0.005"</c>.
    /// </summary>
    public float CloudWeight1 { get; init; } = 0.005f;

    /// <summary>
    /// Wire 9, struct byte 0x2c. Channel <b>B</b> = Cumulus 3. Client default 0.0. Cranberry's
    /// value: the same 12:00 row, <c>B-Cumulus3="0.15"</c>.
    /// </summary>
    public float CloudWeight2 { get; init; } = 0.15f;

    /// <summary>
    /// Wire 10, struct byte 0x30. Channel <b>A</b> = Stratus. Client default 0.0. Cranberry's
    /// value: the same 12:00 row, <c>A-Stratus="0.1"</c>.
    /// </summary>
    /// <remarks>
    /// docs/38 cause <b>L1</b>: D18 sent all four weights as 0, i.e. a sky with no clouds in it at
    /// all, which is most of why the scene reads as flat grey.
    /// </remarks>
    public float CloudWeight3 { get; init; } = 0.1f;

    /// <summary>
    /// Wire 11, struct byte 0x34. Opacity of cloud shadows cast on the world. Client default
    /// <b>0.5</b>. Cranberry's value: the author's <c>&lt;ShadowAlpha Value="0.625"/&gt;</c>.
    /// D18 sent 0 — no moving shadows on the terrain (docs/38 cause L1).
    /// </summary>
    public float CloudShadows { get; init; } = 0.625f;

    // ---- wire 12-14: sun basis ---------------------------------------------------------------

    /// <summary>
    /// Wire 12, struct byte 0x3c, degrees. Client default <b>38.0</b>, adopted.
    /// </summary>
    /// <remarks>
    /// Read at <c>weather+0x3c</c> in <c>FUN_142483c50</c> and converted with
    /// <c>DAT_14311d670 = pi/180</c>, then used as the angle of the second of three composed
    /// axis-angle rotations building the sun basis (the first is the hour, <c>hours*2*pi/24</c>).
    /// </remarks>
    public float SunAxisX { get; init; } = 38f;

    /// <summary>
    /// Wire 13, struct byte 0x40, degrees. Client default <b>-15.0</b>, adopted. Third rotation of
    /// the sun basis (docs/02: <c>SunAxisY + 90</c>).
    /// </summary>
    /// <remarks>
    /// docs/38 cause <b>L3</b>: D18 sent <c>45 / 0 / 0</c>, a triple the client itself never ships,
    /// and D16's own derivation row already flagged it as never live-verified.
    /// </remarks>
    public float SunAxisY { get; init; } = -15f;

    /// <summary>Wire 14, struct byte 0x44, degrees. Client default 0.0, kept.</summary>
    public float SunAxisZ { get; init; }

    // ---- wire 15-18: wind --------------------------------------------------------------------

    /// <summary>
    /// Wire 15, struct byte 0x10. Wind vector X — drives foliage sway and cloud drift. Client
    /// default 1.0. Cranberry's value: <c>sky_Z.xml &lt;Wind X="-0.600000" …&gt;</c>.
    /// </summary>
    public float WindDirX { get; init; } = -0.6f;

    /// <summary>Wire 16, struct byte 0x14. Client default 0.0. <c>sky_Z.xml Y="0.000000"</c>.</summary>
    public float WindDirY { get; init; }

    /// <summary>Wire 17, struct byte 0x18. Client default 0.0. <c>sky_Z.xml Z="0.400000"</c>.</summary>
    public float WindDirZ { get; init; } = 0.4f;

    /// <summary>Wire 18, struct byte 0x1c. Client default 0.0. <c>sky_Z.xml Scale="2.099000"</c>.</summary>
    public float WindSpeed { get; init; } = 2.099f;

    // ---- wire 19-20: rain ramp ---------------------------------------------------------------

    /// <summary>
    /// Wire 19, struct byte 0x4c. Floor applied to the ramped rain strength
    /// (<c>FUN_142483c50</c>: <c>if (s &gt; 0) s = max(s, weather+0x4c)</c>). Client default 0.0.
    /// </summary>
    public float RainMinStrength { get; init; }

    /// <summary>
    /// Wire 20, struct byte 0x50. Rain ramp length in seconds. Client default <b>1.0</b>, adopted.
    /// </summary>
    /// <remarks>
    /// Used as a <b>divisor</b>: <c>i = (int)(value * 1000); f = accum / (float)i</c>. D18 sent 0,
    /// which is harmless only while <see cref="GlobalPrecipitation"/> is 0 (the branch is gated) and
    /// is a latent divide-by-zero the moment precipitation is ever enabled. <see cref="WriteTo"/>
    /// now refuses that combination outright.
    /// </remarks>
    public float RainRampUpTimeSeconds { get; init; } = 1f;

    /// <summary>
    /// Counted string between wire 20 and wire 21, struct byte +0x58. Loaded by
    /// <c>FUN_142481740</c>; an empty name is tolerated. Default
    /// <see cref="ZCloudDensityTexture"/>.
    /// </summary>
    public string CloudDensityTexture { get; init; } = ZCloudDensityTexture;

    // ---- wire 21-28: cumulus and stratus layers ----------------------------------------------

    /// <summary>
    /// Wire 21, struct byte 0x70. UV frequency of the cumulus layer. Client default 0.2.
    /// Cranberry's value: <c>sky_Z_Clouds.xml &lt;CumulusTiling Frequency="1.30"/&gt;</c>.
    /// </summary>
    public float CumulusCloudTiling { get; init; } = 1.30f;

    /// <summary>
    /// Wire 22, struct byte 0x74. Client default <b>-0.002</b>. Cranberry's value:
    /// <c>&lt;CumulusScroll U="0.001" V="-0.002"/&gt;</c>.
    /// </summary>
    public float CumulusCloudScrollU { get; init; } = 0.001f;

    /// <summary>Wire 23, struct byte 0x78. Client default 0.0. Same authored row, <c>V="-0.002"</c>.</summary>
    public float CumulusCloudScrollV { get; init; } = -0.002f;

    /// <summary>Wire 24, struct byte 0x7c. Client default 1000.0, kept — no authored counterpart.</summary>
    public float CumulusCloudHeight { get; init; } = 1000f;

    /// <summary>
    /// Wire 25, struct byte 0x80. Client default 0.2. Cranberry's value:
    /// <c>&lt;StratusTiling Frequency="1.00"/&gt;</c>.
    /// </summary>
    public float StratusCloudTiling { get; init; } = 1.00f;

    /// <summary>Wire 26, struct byte 0x84. Client default 0.0, kept.</summary>
    public float StratusCloudScrollU { get; init; }

    /// <summary>Wire 27, struct byte 0x88. Client default 0.002, kept.</summary>
    public float StratusCloudScrollV { get; init; } = 0.002f;

    /// <summary>Wire 28, struct byte 0x8c. Client default 8000.0, kept — no authored counterpart.</summary>
    public float StratusCloudHeight { get; init; } = 8000f;

    // ---- wire 29-32: cloud animation and silver lining ---------------------------------------

    /// <summary>
    /// Wire 29, struct byte 0x90. Cloud "boil" rate. Client default <b>0.002</b>. Cranberry's
    /// value: <c>&lt;Animation Intensity="0.035"/&gt;</c>. D18 sent 0.09 — 2.6x the author's rate
    /// and 45x the struct default.
    /// </summary>
    public float CloudAnimationSpeed { get; init; } = 0.035f;

    /// <summary>Wire 30, struct byte 0x94. Client default 0.25, kept — no authored alternative exists.</summary>
    public float SkyClarity { get; init; } = 0.25f;

    /// <summary>
    /// Wire 31, struct byte 0x98. Client default 7.0. Cranberry's value:
    /// <c>&lt;SilverLining Thickness="20.50" …/&gt;</c>.
    /// </summary>
    public float CloudSilverLiningThickness { get; init; } = 20.5f;

    /// <summary>
    /// Wire 32, struct byte 0x9c. Brightness of the sun-lit cloud rim. Client default <b>0.5</b>.
    /// Cranberry's value: <c>&lt;SilverLining … Brightness="20.0"/&gt;</c>.
    /// </summary>
    /// <remarks>
    /// docs/38 cause <b>L2</b>: D18 sent 0, removing the single brightest feature of a KOTK sky.
    /// </remarks>
    public float CloudSilverLiningBrightness { get; init; } = 20f;

    /// <summary>
    /// The struct byte at 0xa0 (client default 0.3) is never carried on the wire; it is recorded in
    /// docs/38 §3 so a future lane does not go looking for a 34th field.
    /// </summary>
    public const float UnsentStructTailDefault = 0.3f;

    /// <summary>
    /// Writes the struct in the parser's own order (<c>FUN_140a482b0</c>), byte-for-byte identical
    /// to <c>WeatherSettings.WriteTo</c> for identical values.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The values would poison the client's own maths: a non-positive or non-finite transition time
    /// or fog triple (black frame, docs/38 §3), or precipitation enabled with a zero rain ramp
    /// (divide by zero in <c>FUN_142483c50</c>).
    /// </exception>
    public void WriteTo(PacketWriter writer)
    {
        Validate();

        writer.WriteSingle(TransitionTime);
        writer.WriteSingle(FogDensity);
        writer.WriteSingle(FogFloor);
        writer.WriteSingle(FogGradient);
        writer.WriteSingle(GlobalPrecipitation);
        writer.WriteSingle(Temperature);
        writer.WriteSingle(Overcast);
        writer.WriteSingle(CloudWeight0);
        writer.WriteSingle(CloudWeight1);
        writer.WriteSingle(CloudWeight2);
        writer.WriteSingle(CloudWeight3);
        writer.WriteSingle(CloudShadows);
        writer.WriteSingle(SunAxisX);
        writer.WriteSingle(SunAxisY);
        writer.WriteSingle(SunAxisZ);
        writer.WriteSingle(WindDirX);
        writer.WriteSingle(WindDirY);
        writer.WriteSingle(WindDirZ);
        writer.WriteSingle(WindSpeed);
        writer.WriteSingle(RainMinStrength);
        writer.WriteSingle(RainRampUpTimeSeconds);
        writer.WriteString(CloudDensityTexture);
        writer.WriteSingle(CumulusCloudTiling);
        writer.WriteSingle(CumulusCloudScrollU);
        writer.WriteSingle(CumulusCloudScrollV);
        writer.WriteSingle(CumulusCloudHeight);
        writer.WriteSingle(StratusCloudTiling);
        writer.WriteSingle(StratusCloudScrollU);
        writer.WriteSingle(StratusCloudScrollV);
        writer.WriteSingle(StratusCloudHeight);
        writer.WriteSingle(CloudAnimationSpeed);
        writer.WriteSingle(SkyClarity);
        writer.WriteSingle(CloudSilverLiningThickness);
        writer.WriteSingle(CloudSilverLiningBrightness);
    }

    /// <summary>
    /// Throws unless the struct is safe for the client's blend and precipitation maths. Called by
    /// <see cref="WriteTo"/>; public so a host can fail fast at start-up rather than mid-zoning.
    /// </summary>
    public void Validate()
    {
        if (!IsFinitePositive(TransitionTime)
            || !IsFinitePositive(FogDensity)
            || !IsFinitePositive(FogFloor)
            || !IsFinitePositive(FogGradient))
        {
            throw new InvalidOperationException(
                "Sky transition time and fog density/floor/gradient must be finite and positive: the "
                + "blend divides by the transition time and log-lerps the fog triple (docs/38 §3).");
        }

        if (GlobalPrecipitation > 0 && !IsFinitePositive(RainRampUpTimeSeconds))
        {
            throw new InvalidOperationException(
                "Precipitation is enabled while the rain ramp is zero: FUN_142483c50 divides by "
                + "RainRampUpTimeSeconds * 1000 (docs/38 §3 wire 20).");
        }
    }

    private static bool IsFinitePositive(float value) => float.IsFinite(value) && value > 0;

    /// <summary>
    /// Adapter to the type the shipped packet writers already take
    /// (<c>SendZoneDetails</c>, <c>UpdateWeatherData</c>, <c>ClientBeginZoning</c>,
    /// <c>ZoneOptions.Weather</c>). Field-for-field; the two <c>WriteTo</c>s agree byte for byte.
    /// </summary>
    public WeatherSettings ToWeatherSettings() => new()
    {
        TransitionTime = TransitionTime,
        FogDensity = FogDensity,
        FogFloor = FogFloor,
        FogGradient = FogGradient,
        GlobalPrecipitation = GlobalPrecipitation,
        Temperature = Temperature,
        Overcast = Overcast,
        CloudWeight0 = CloudWeight0,
        CloudWeight1 = CloudWeight1,
        CloudWeight2 = CloudWeight2,
        CloudWeight3 = CloudWeight3,
        CloudShadows = CloudShadows,
        SunAxisX = SunAxisX,
        SunAxisY = SunAxisY,
        SunAxisZ = SunAxisZ,
        WindDirX = WindDirX,
        WindDirY = WindDirY,
        WindDirZ = WindDirZ,
        WindSpeed = WindSpeed,
        RainMinStrength = RainMinStrength,
        RainRampUpTimeSeconds = RainRampUpTimeSeconds,
        CloudDensityTexture = CloudDensityTexture,
        CumulusCloudTiling = CumulusCloudTiling,
        CumulusCloudScrollU = CumulusCloudScrollU,
        CumulusCloudScrollV = CumulusCloudScrollV,
        CumulusCloudHeight = CumulusCloudHeight,
        StratusCloudTiling = StratusCloudTiling,
        StratusCloudScrollU = StratusCloudScrollU,
        StratusCloudScrollV = StratusCloudScrollV,
        StratusCloudHeight = StratusCloudHeight,
        CloudAnimationSpeed = CloudAnimationSpeed,
        SkyClarity = SkyClarity,
        CloudSilverLiningThickness = CloudSilverLiningThickness,
        CloudSilverLiningBrightness = CloudSilverLiningBrightness,
    };

    /// <summary>
    /// Inverse adapter, so an existing <see cref="WeatherSettings"/> (D18's palette, or anything a
    /// host has already tuned) can be lifted into this model for an A/B without retyping it.
    /// </summary>
    public static SkySettings FromWeatherSettings(WeatherSettings weather)
    {
        ArgumentNullException.ThrowIfNull(weather);

        return new SkySettings
        {
            TransitionTime = weather.TransitionTime,
            FogDensity = weather.FogDensity,
            FogFloor = weather.FogFloor,
            FogGradient = weather.FogGradient,
            GlobalPrecipitation = weather.GlobalPrecipitation,
            Temperature = weather.Temperature,
            Overcast = weather.Overcast,
            CloudWeight0 = weather.CloudWeight0,
            CloudWeight1 = weather.CloudWeight1,
            CloudWeight2 = weather.CloudWeight2,
            CloudWeight3 = weather.CloudWeight3,
            CloudShadows = weather.CloudShadows,
            SunAxisX = weather.SunAxisX,
            SunAxisY = weather.SunAxisY,
            SunAxisZ = weather.SunAxisZ,
            WindDirX = weather.WindDirX,
            WindDirY = weather.WindDirY,
            WindDirZ = weather.WindDirZ,
            WindSpeed = weather.WindSpeed,
            RainMinStrength = weather.RainMinStrength,
            RainRampUpTimeSeconds = weather.RainRampUpTimeSeconds,
            CloudDensityTexture = weather.CloudDensityTexture,
            CumulusCloudTiling = weather.CumulusCloudTiling,
            CumulusCloudScrollU = weather.CumulusCloudScrollU,
            CumulusCloudScrollV = weather.CumulusCloudScrollV,
            CumulusCloudHeight = weather.CumulusCloudHeight,
            StratusCloudTiling = weather.StratusCloudTiling,
            StratusCloudScrollU = weather.StratusCloudScrollU,
            StratusCloudScrollV = weather.StratusCloudScrollV,
            StratusCloudHeight = weather.StratusCloudHeight,
            CloudAnimationSpeed = weather.CloudAnimationSpeed,
            SkyClarity = weather.SkyClarity,
            CloudSilverLiningThickness = weather.CloudSilverLiningThickness,
            CloudSilverLiningBrightness = weather.CloudSilverLiningBrightness,
        };
    }
}
