using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Lighting;

namespace Cranberry.Tests.Zone.Lighting;

/// <summary>
/// Byte-exact tests for the weather/sky struct against the field map in docs/38 §2 and §3.
/// </summary>
/// <remarks>
/// These are the compiler for docs/38: the expected hex strings below are the struct laid out in
/// the parser's own order (<c>FUN_140a482b0</c>: 21 floats, one counted string, 12 floats), so any
/// reordering, any dropped field and any accidental value change shows up as a diff rather than as
/// a grey screenshot three days later.
/// </remarks>
public sealed class SkySettingsWireTests
{
    /// <summary>
    /// <see cref="EnvironmentPresets.Aug2017Clear"/> on the wire, field by field, in the order
    /// docs/38 §3 lists: transition 1.0; fog 1e-4 / 1 / 0.05; precipitation 0; temperature 75;
    /// overcast 0; cloud weights 0 / 0.005 / 0.15 / 0.1; cloud shadows 0.625; sun 38 / -15 / 0;
    /// wind -0.6 / 0 / 0.4 at 2.099; rain 0 / 1.0; "sky_Z_clouds.dds"; cumulus 1.30 / 0.001 /
    /// -0.002 / 1000; stratus 1.00 / 0 / 0.002 / 8000; boil 0.035; clarity 0.25; silver 20.5 / 20.0.
    /// </summary>
    private const string Aug2017ClearHex =
        "0000803F" + "17B7D138" + "0000803F" + "CDCC4C3D" +
        "00000000" + "00009642" + "00000000" +
        "00000000" + "0AD7A33B" + "9A99193E" + "CDCCCC3D" + "0000203F" +
        "00001842" + "000070C1" + "00000000" +
        "9A9919BF" + "00000000" + "CDCCCC3E" + "04560640" +
        "00000000" + "0000803F" +
        "10000000" + "736B795F5A5F636C6F7564732E646473" +
        "6666A63F" + "6F12833A" + "6F1203BB" + "00007A44" +
        "0000803F" + "00000000" + "6F12033B" + "0000FA45" +
        "295C0F3D" + "0000803E" + "0000A441" + "0000A041";

    /// <summary>
    /// <see cref="EnvironmentPresets.LegacyD18Grey"/> on the wire — identical to the bytes the
    /// shipped <c>GatewayPacketTests.LaterStateGatePacketsHaveTheirBinaryDerivedShapes</c> asserts
    /// for <c>UpdateWeatherData</c> minus its 0xCA opcode. This is the "before" half of docs/38 §6.
    /// </summary>
    private const string LegacyD18GreyHex =
        "0000803F" + "CEC03539" + "00002041" + "FAED6B3C" +
        "00000000" + "00009642" + "00000000" +
        "00000000" + "00000000" + "00000000" + "00000000" + "00000000" +
        "00003442" + "00000000" + "00000000" +
        "000080BF" + "CDCC4CBD" + "000080BF" + "00004040" +
        "00000000" + "00000000" +
        "10000000" + "736B795F5A5F636C6F7564732E646473" +
        "9A99993E" + "00000000" + "00000000" + "00007A44" +
        "CDCC4C3E" + "00000000" + "6F12033B" + "0000FA45" +
        "EC51B83D" + "0000803E" + "0000E040" + "00000000";

    [Fact]
    public void Aug2017ClearHasTheAugustAuthoredWireShape()
    {
        using var writer = new PacketWriter();
        EnvironmentPresets.Aug2017Clear.Sky.WriteTo(writer);

        Assert.Equal(Convert.FromHexString(Aug2017ClearHex), writer.Written.ToArray());

        // 21 floats + (i32 len + 16 bytes) + 12 floats.
        Assert.Equal(SkySettings.EmptyTextureWireLength + SkySettings.ZCloudDensityTexture.Length, writer.Position);
        Assert.Equal(152, writer.Position);
    }

    /// <summary>
    /// docs/50 §4.2 at the byte level, as amended by docs/82 §4: <c>Aug2017Vivid</c> is now the
    /// <b>default</b>'s wire bytes with exactly <b>two</b> 4-byte words changed - wire 1
    /// <c>FOGDENSITY</c> and wire 2 <c>FOGFLOOR</c>. Wire 11 <c>CLOUDSHADOWS</c> left this preset
    /// because the default now zeroes it on the owner's ruling. The test re-asserts
    /// <c>Aug2017Clear</c>'s shipped hex in the same place, so the A/B control is still pinned.
    /// </summary>
    [Fact]
    public void Aug2017VividChangesExactlyTwoWordsOfTheDefaultsWire()
    {
        using var clearWriter = new PacketWriter();
        EnvironmentPresets.Aug2017Clear.Sky.WriteTo(clearWriter);

        // The A/B control is still byte-for-byte what it shipped as.
        Assert.Equal(Convert.FromHexString(Aug2017ClearHex), clearWriter.Written.ToArray());

        using var defaultWriter = new PacketWriter();
        EnvironmentPresets.Default.Sky.WriteTo(defaultWriter);
        byte[] shipped = defaultWriter.Written.ToArray();

        using var vividWriter = new PacketWriter();
        EnvironmentPresets.Aug2017Vivid.Sky.WriteTo(vividWriter);
        byte[] vivid = vividWriter.Written.ToArray();

        Assert.Equal(shipped.Length, vivid.Length);
        Assert.Equal(0, shipped.Length % 4);

        int[] changed = Enumerable.Range(0, shipped.Length / 4)
            .Where(word => !shipped.AsSpan(word * 4, 4).SequenceEqual(vivid.AsSpan(word * 4, 4)))
            .ToArray();

        Assert.Equal(new[] { 1, 2 }, changed);

        // And each changed word is the DESIGNED float docs/50 §4.2 names.
        Assert.Equal(5.0e-5f, BitConverter.ToSingle(vivid, 1 * 4));
        Assert.Equal(0.25f, BitConverter.ToSingle(vivid, 2 * 4));

        // The retired hedge: 0.35 is nowhere on the wire any more; the ruling's 0 is.
        Assert.Equal(0f, BitConverter.ToSingle(vivid, 11 * 4));
        Assert.Equal(0f, BitConverter.ToSingle(shipped, 11 * 4));
    }

    /// <summary>
    /// The other half of the A/B pair is untouched too: <see cref="EnvironmentPresets.LegacyD18Grey"/>
    /// still serialises to the bytes D18 sent, and the <c>SKYCLARITY</c> sweep moves only that one word.
    /// </summary>
    [Fact]
    public void TheLegacyControlIsUntouchedAndTheClaritySweepMovesOnlySkyClarity()
    {
        using var legacy = new PacketWriter();
        EnvironmentPresets.LegacyD18Grey.Sky.WriteTo(legacy);
        Assert.Equal(Convert.FromHexString(LegacyD18GreyHex), legacy.Written.ToArray());

        using var vividWriter = new PacketWriter();
        EnvironmentPresets.Aug2017Vivid.Sky.WriteTo(vividWriter);
        byte[] vivid = vividWriter.Written.ToArray();

        foreach (EnvironmentSettings step in new[]
        {
            EnvironmentPresets.Aug2017VividClarity50,
            EnvironmentPresets.Aug2017VividClarity75,
            EnvironmentPresets.Aug2017VividClarity100,
        })
        {
            using var stepWriter = new PacketWriter();
            step.Sky.WriteTo(stepWriter);
            byte[] bytes = stepWriter.Written.ToArray();

            int[] changed = Enumerable.Range(0, vivid.Length / 4)
                .Where(word => !vivid.AsSpan(word * 4, 4).SequenceEqual(bytes.AsSpan(word * 4, 4)))
                .ToArray();

            // Word 35: 21 floats + (i32 length + 16 bytes of "sky_Z_clouds.dds") + 9 floats.
            Assert.Equal(new[] { 35 }, changed);
            Assert.Equal(step.Sky.SkyClarity, BitConverter.ToSingle(bytes, 35 * 4));
        }
    }

    /// <summary>
    /// <c>new SkySettings()</c> is still exactly <c>Aug2017Clear</c> - docs/82 §4 moved the
    /// <em>default preset</em>, not the struct's own property defaults, so nothing that constructs a
    /// bare <see cref="SkySettings"/> changed shape.
    /// </summary>
    [Fact]
    public void DefaultSkySettingsIsStillTheAug2017ClearPresetButTheDefaultPresetMoved()
    {
        using var fromDefault = new PacketWriter();
        new SkySettings().WriteTo(fromDefault);

        using var fromPreset = new PacketWriter();
        EnvironmentPresets.Aug2017Clear.Sky.WriteTo(fromPreset);

        Assert.Equal(fromPreset.Written.ToArray(), fromDefault.Written.ToArray());

        // docs/82 §4: the shipped default is now the owner's no-clouds preset.
        Assert.Same(EnvironmentPresets.Aug2017KotkClear, EnvironmentPresets.Default);
    }

    /// <summary>
    /// Every wire slot decoded back out of the packet and compared against docs/38 §3 by index.
    /// A pure byte comparison would pass even if two adjacent fields were swapped in both the
    /// writer and the expected hex; this one names each slot.
    /// </summary>
    [Fact]
    public void EveryWireSlotOfAug2017ClearMatchesTheDocumentedFieldMap()
    {
        using var writer = new PacketWriter();
        SkySettings sky = EnvironmentPresets.Aug2017Clear.Sky;
        sky.WriteTo(writer);
        byte[] bytes = writer.Written.ToArray();

        Assert.Equal(1f, Float(bytes, 0));                      // 0  TRANSITIONTIME
        Assert.Equal(1.0e-4f, Float(bytes, 1));                 // 1  FOGDENSITY
        Assert.Equal(1f, Float(bytes, 2));                      // 2  FOGFLOOR
        Assert.Equal(0.05f, Float(bytes, 3));                   // 3  FOGGRADIENT
        Assert.Equal(0f, Float(bytes, 4));                      // 4  GLOBALPRECIPITATION
        Assert.Equal(75f, Float(bytes, 5));                     // 5  TEMPERATURE (deg F)
        Assert.Equal(0f, Float(bytes, 6));                      // 6  OVERCAST
        Assert.Equal(0f, Float(bytes, 7));                      // 7  CLOUDWEIGHT0 (R/Cumulus1)
        Assert.Equal(0.005f, Float(bytes, 8));                  // 8  CLOUDWEIGHT1 (G/Cumulus2)
        Assert.Equal(0.15f, Float(bytes, 9));                   // 9  CLOUDWEIGHT2 (B/Cumulus3)
        Assert.Equal(0.1f, Float(bytes, 10));                   // 10 CLOUDWEIGHT3 (A/Stratus)
        Assert.Equal(0.625f, Float(bytes, 11));                 // 11 CLOUDSHADOWS
        Assert.Equal(38f, Float(bytes, 12));                    // 12 SUNAXISX
        Assert.Equal(-15f, Float(bytes, 13));                   // 13 SUNAXISY
        Assert.Equal(0f, Float(bytes, 14));                     // 14 SUNAXISZ
        Assert.Equal(-0.6f, Float(bytes, 15));                  // 15 WINDDIRX
        Assert.Equal(0f, Float(bytes, 16));                     // 16 WINDDIRY
        Assert.Equal(0.4f, Float(bytes, 17));                   // 17 WINDDIRZ
        Assert.Equal(2.099f, Float(bytes, 18));                 // 18 WINDSPEED
        Assert.Equal(0f, Float(bytes, 19));                     // 19 RAINMINSTRENGTH
        Assert.Equal(1f, Float(bytes, 20));                     // 20 RAINRAMPUPTIMESECONDS

        // The counted string sits between wire 20 and wire 21 (struct byte +0x58).
        Assert.Equal(SkySettings.ZCloudDensityTexture.Length, BitConverter.ToInt32(bytes, 21 * 4));
        Assert.Equal(
            SkySettings.ZCloudDensityTexture,
            System.Text.Encoding.ASCII.GetString(bytes, (21 * 4) + 4, SkySettings.ZCloudDensityTexture.Length));

        int tail = (21 * 4) + 4 + SkySettings.ZCloudDensityTexture.Length;
        Assert.Equal(1.30f, FloatAt(bytes, tail, 0));           // 21 CUMULUSCLOUDTILING
        Assert.Equal(0.001f, FloatAt(bytes, tail, 1));          // 22 CUMULUSCLOUDSCROLLU
        Assert.Equal(-0.002f, FloatAt(bytes, tail, 2));         // 23 CUMULUSCLOUDSCROLLV
        Assert.Equal(1000f, FloatAt(bytes, tail, 3));           // 24 CUMULUSCLOUDHEIGHT
        Assert.Equal(1.00f, FloatAt(bytes, tail, 4));           // 25 STRATUSCLOUDTILING
        Assert.Equal(0f, FloatAt(bytes, tail, 5));              // 26 STRATUSCLOUDSCROLLU
        Assert.Equal(0.002f, FloatAt(bytes, tail, 6));          // 27 STRATUSCLOUDSCROLLV
        Assert.Equal(8000f, FloatAt(bytes, tail, 7));           // 28 STRATUSCLOUDHEIGHT
        Assert.Equal(0.035f, FloatAt(bytes, tail, 8));          // 29 CLOUDANIMATIONSPEED
        Assert.Equal(0.25f, FloatAt(bytes, tail, 9));           // 30 SKYCLARITY
        Assert.Equal(20.5f, FloatAt(bytes, tail, 10));          // 31 CLOUDSILVERLININGTHICKNESS
        Assert.Equal(20f, FloatAt(bytes, tail, 11));            // 32 CLOUDSILVERLININGBRIGHTNESS

        Assert.Equal(tail + (12 * 4), bytes.Length);

        // And the properties agree with the bytes they produced.
        Assert.Equal(sky.CloudSilverLiningBrightness, FloatAt(bytes, tail, 11));
        Assert.Equal(sky.SunAxisY, Float(bytes, 13));
    }

    [Fact]
    public void LegacyPresetReproducesTheD18WireExactly()
    {
        using var legacy = new PacketWriter();
        EnvironmentPresets.LegacyD18Grey.Sky.WriteTo(legacy);

        using var shipped = new PacketWriter();
        WeatherSettings.Kotk2017.WriteTo(shipped);

        Assert.Equal(Convert.FromHexString(LegacyD18GreyHex), legacy.Written.ToArray());
        Assert.Equal(shipped.Written.ToArray(), legacy.Written.ToArray());
    }

    /// <summary>
    /// The adapter is the whole point of this model: whatever the richer type says, the bytes that
    /// reach the client must come out of <c>WeatherSettings.WriteTo</c> unchanged.
    /// </summary>
    [Theory]
    [InlineData("Aug2017Clear")]
    [InlineData("Aug2017CloudyDay")]
    [InlineData("Aug2017RainyDay")]
    [InlineData("Aug2017FoggyDay")]
    [InlineData("LegacyD18Grey")]
    public void AdapterIsByteIdenticalInBothDirections(string presetName)
    {
        SkySettings sky = EnvironmentPresets.ByName(presetName).Sky;

        using var direct = new PacketWriter();
        sky.WriteTo(direct);

        using var adapted = new PacketWriter();
        sky.ToWeatherSettings().WriteTo(adapted);

        using var roundTripped = new PacketWriter();
        SkySettings.FromWeatherSettings(sky.ToWeatherSettings()).WriteTo(roundTripped);

        Assert.Equal(direct.Written.ToArray(), adapted.Written.ToArray());
        Assert.Equal(direct.Written.ToArray(), roundTripped.Written.ToArray());
        Assert.Equal(sky, SkySettings.FromWeatherSettings(sky.ToWeatherSettings()));
    }

    /// <summary>
    /// docs/38 §2: the lighting-table name is field 13, immediately after five u32s, a u64 and a
    /// bool, and the whole packet must keep the shape the August parser reads in order.
    /// </summary>
    [Fact]
    public void SendZoneDetailsCarriesThePresetSkyAndLightingTableInTheDocumentedOrder()
    {
        EnvironmentSettings preset = EnvironmentPresets.Aug2017Clear;

        using var writer = new PacketWriter();
        new SendZoneDetails(
            "LoginZone",
            Weather: preset.Sky.ToWeatherSettings(),
            LightingFile: preset.LightingFile).WriteTo(writer);
        byte[] bytes = writer.Written.ToArray();

        byte[] prefix = Convert.FromHexString("16" + "09000000" + "4C6F67696E5A6F6E65" + "04000000");
        Assert.Equal(prefix, bytes.AsSpan(0, prefix.Length).ToArray());
        Assert.Equal(0, bytes[prefix.Length]);                                      // field 4, the bool

        int weatherStart = prefix.Length + 1;
        Assert.Equal(
            Convert.FromHexString(Aug2017ClearHex),
            bytes.AsSpan(weatherStart, Aug2017ClearHex.Length / 2).ToArray());

        int tail = weatherStart + (Aug2017ClearHex.Length / 2);
        Assert.All(
            bytes.AsSpan(tail, (5 * sizeof(uint)) + sizeof(ulong) + 1).ToArray(),
            value => Assert.Equal(0, value));                                       // fields 6-12

        int lightingStart = tail + (5 * sizeof(uint)) + sizeof(ulong) + 1;
        Assert.Equal(
            Convert.FromHexString("0F0000004C69676874696E675F5A322E747874"),        // field 13
            bytes.AsSpan(lightingStart, 4 + LightingTable.Z2.Length).ToArray());
        Assert.Equal(LightingTable.Z2, SendZoneDetails.KotkLightingFile);

        // Fields 14/15 (IsInvitational, isDevServer) then the empty StringHashToValue list.
        Assert.Equal(lightingStart + 4 + LightingTable.Z2.Length + 2 + 4, bytes.Length);
    }

    [Fact]
    public void UpdateWeatherDataCarriesThePresetSkyBehindItsOpcode()
    {
        using var writer = new PacketWriter();
        new UpdateWeatherData(EnvironmentPresets.Aug2017Clear.Sky.ToWeatherSettings()).WriteTo(writer);

        Assert.Equal(Convert.FromHexString("CA" + Aug2017ClearHex), writer.Written.ToArray());
        Assert.Equal(153, writer.Position);
    }

    /// <summary>
    /// docs/38 §3: the four values the client's own maths cannot survive, plus the latent
    /// divide-by-zero that only bites once precipitation is enabled.
    /// </summary>
    [Fact]
    public void SkyRejectsValuesThatWouldPoisonTheClientBlend()
    {
        SkySettings[] invalid =
        [
            new SkySettings { TransitionTime = 0 },
            new SkySettings { TransitionTime = float.NaN },
            new SkySettings { FogDensity = 0 },
            new SkySettings { FogDensity = float.NaN },
            new SkySettings { FogFloor = 0 },
            new SkySettings { FogFloor = -1 },
            new SkySettings { FogGradient = 0 },
            new SkySettings { FogGradient = float.PositiveInfinity },
            new SkySettings { GlobalPrecipitation = 1f, RainRampUpTimeSeconds = 0f },
        ];

        Assert.All(invalid, sky =>
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                using var writer = new PacketWriter();
                sky.WriteTo(writer);
            });
            Assert.Throws<InvalidOperationException>(sky.Validate);
        });
    }

    [Fact]
    public void RainRampGuardOnlyBitesWhenPrecipitationIsOn()
    {
        // D18 shipped ramp 0 with precipitation 0 for months: legal, and still legal.
        new SkySettings { GlobalPrecipitation = 0f, RainRampUpTimeSeconds = 0f }.Validate();
        EnvironmentPresets.LegacyD18Grey.Validate();
    }

    private static float Float(byte[] bytes, int slot) => BitConverter.ToSingle(bytes, slot * 4);

    private static float FloatAt(byte[] bytes, int offset, int slot) =>
        BitConverter.ToSingle(bytes, offset + (slot * 4));
}
