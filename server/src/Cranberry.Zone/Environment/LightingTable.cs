namespace Cranberry.Zone.Lighting;

/// <summary>
/// The lighting-table name carried by <c>SendZoneDetails</c> field 13 (packet object +0x48) — the
/// only field in the whole protocol that can activate a colour grade (docs/38 §1, §4).
/// </summary>
/// <remarks>
/// <para>
/// A non-empty string makes the dispatcher's case 0x16 arm call <c>thunk_FUN_1424892c0</c>
/// (<c>0x140af41a0</c>), which reaches <c>LightingData::LoadFromFile</c> = <c>FUN_14248d9a0</c>;
/// the server-supplied name <b>replaces</b> the built-in <see cref="ClientBuiltInDefault"/>. An
/// empty string skips zone lighting entirely.
/// </para>
/// <para>
/// <b>DERIVED, and load-bearing:</b> <c>exeview calls 0x1424892c0</c> yields one thunk and one call
/// site, inside <c>SendZoneDetails</c>. <c>ClientBeginZoning</c> (0x0b, <c>FUN_140afc260</c>)
/// touches neither the lighting manager <c>DAT_143f69e30</c> nor the sky-blend arm
/// <c>FUN_140b88950</c>. docs/50 §3.3 answered the LGT-H3 question that followed from it: the
/// lighting manager <c>DAT_143f69e30</c> is a start-up singleton with two writers in the whole
/// image, so nothing replaces it on a world load and the LoginZone's table survives into Z2. The
/// re-send switch that existed for the A/B was deleted in lane 0D.
/// </para>
/// </remarks>
public static class LightingTable
{
    /// <summary>
    /// <c>Lighting_Z2.txt</c> — Assets_218.pack, offset 3,263,672, 9,239 bytes, crc
    /// <c>0xaf1c566f</c>. The KOTK-era, Z2-authored table, and the only one whose key vocabulary
    /// matches this build's parser (docs/38 §4.2).
    /// </summary>
    /// <remarks>
    /// The string <c>"Lighting_Z2"</c> does not exist in <c>H1Z1.exe</c> and no shipped datasheet
    /// names it, so the file is dead data unless the server names it — D19 is not an optimisation.
    /// </remarks>
    public const string Z2 = "Lighting_Z2.txt";

    /// <summary>
    /// <c>Lighting.txt</c> — the name baked into the executable at <c>0x143635b40</c>
    /// (<c>PTR_s_Lighting_txt_143efcde8</c>) and used when the server sends nothing.
    /// </summary>
    /// <remarks>
    /// <b>Do not select this.</b> It is the pre-KOTK PlanetSide/Just-Survive table: it carries eight
    /// keys this build no longer reads (<c>Ambience</c>, <c>EvComp</c>, <c>PostGrain</c>,
    /// <c>PostBloom</c>, <c>CloudSilverThinness</c>, <c>CloudSilverBrightness</c>,
    /// <c>HazeBrightness</c>, <c>HazeColor</c>), lacks five it does (<c>SunShadows</c>,
    /// <c>LightsDisabled</c>, <c>InteriorProbeWeight</c>, <c>IndoorProbeAlbedoFilename</c>,
    /// <c>IndoorProbeNormalMapFilename</c>), points at the old <c>probe_street2_*.dds</c> street
    /// probe, and grades with <c>colorkey_day.tga</c>. Its <c>DayAngle</c> is 0.4, not 0.7. Kept
    /// here as a named A/B control only.
    /// </remarks>
    public const string ClientBuiltInDefault = "Lighting.txt";

    /// <summary>
    /// The empty name: case 0x16 skips <c>FUN_1424892c0</c> entirely and the scene keeps whatever
    /// lighting state it already had. The flat grey of the first live click test.
    /// </summary>
    public const string None = "";

    /// <summary>
    /// The <c>[Day]</c> block of <c>Lighting_Z2.txt</c>, verbatim — the look the whole lane is aimed
    /// at. Reference values only: the client reads them out of its own pack, the server never sends
    /// them. They are here so a future lane can reason about the target without re-extracting the
    /// asset, and so the acceptance check in docs/38 has numbers to check against.
    /// </summary>
    public static class Z2Day
    {
        /// <summary>Exposure is <b>pinned</b> at EV 4 (<c>IrisClampLow == IrisClampHigh == 4.0</c>).</summary>
        public const float IrisClampLow = 4.0f;

        /// <inheritdoc cref="IrisClampLow"/>
        public const float IrisClampHigh = 4.0f;

        /// <summary><c>SunShadows = 1.0</c> — the sun casts shadows in the Day phase.</summary>
        public const float SunShadows = 1.0f;

        /// <summary><c>LightsDisabled = 1.0</c> — point lights are off in daytime, by the author's design.</summary>
        public const float LightsDisabled = 1.0f;

        /// <summary><c>SunBrightness = 15.58</c>.</summary>
        public const float SunBrightness = 15.58f;

        /// <summary><c>SkyBrightness = SkyProbeBrightness = ParticleBrightness = 14.8</c>.</summary>
        public const float SkyBrightness = 14.8f;

        /// <summary><c>HorizonGlowBrightness = 14.83</c>.</summary>
        public const float HorizonGlowBrightness = 14.83f;

        /// <summary><c>KeyLuminance = 0.18216</c>.</summary>
        public const float KeyLuminance = 0.18216f;

        /// <summary><c>SkyColor = 35.879 58.103 108.533</c> (0-255) = a deep blue, 0.1407/0.2278/0.4256.</summary>
        public static (float R, float G, float B) SkyColor => (35.879f, 58.103f, 108.533f);

        /// <summary><c>SunColor = 255.000 254.947 254.500</c> — a near-white key light.</summary>
        public static (float R, float G, float B) SunColor => (255.000f, 254.947f, 254.500f);

        /// <summary>
        /// <c>ColorGradingFilename = z2_colorkey_day.dds</c>. Measured against an identity probe it
        /// is a neutral contrast lift with <b>no colour cast</b> (all three channels share one
        /// curve): pivot 102→102, shadows marginally down (68→66, 85→83), highlights up
        /// (170→181, 204→215, 238→242); mean luma +3.7, mean chroma +0.020. Every other Z2 phase
        /// LUT is byte-identical to <c>colorkey_identity.tga</c> — docs/38 §4.4, and the strongest
        /// argument for landing the clock on the Day phase.
        /// </summary>
        public const string ColorGradingFilename = "z2_colorkey_day.dds";
    }
}
