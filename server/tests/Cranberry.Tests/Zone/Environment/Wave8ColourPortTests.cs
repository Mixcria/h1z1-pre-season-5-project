using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Lighting;

namespace Cranberry.Tests.Zone.Lighting;

/// <summary>
/// Wave 8, colour lane (docs/82). What this lane actually changed: the owner's no-clouds ruling
/// became <c>Aug2017KotkClear</c> and the default; <c>Aug2017Vivid</c> rebased onto it and lost
/// docs/50's 0.35 <c>CloudShadows</c> hedge; a 14:00 sibling exists so the owner can overrule the
/// 12:00 argument with a screenshot; and Z1's composite-effect discipline became
/// <see cref="CompositeEffectGate"/>.
/// </summary>
/// <remarks>
/// These are BUILT/TESTED, never LIVE-VERIFIED (D29). The values and the arithmetic are what is
/// pinned here; docs/82 §11 is the owner's verification plan.
/// </remarks>
public sealed class Wave8ColourPortTests
{
    /// <summary>
    /// The whole ruling, as a deviation set: the new default is <c>Aug2017Clear</c> with exactly the
    /// four cloud weights and the cloud-shadow alpha at 0, and <b>nothing else</b>.
    /// </summary>
    [Fact]
    public void Aug2017KotkClearDiffersFromAug2017ClearInExactlyTheFiveCloudFields()
    {
        SkySettings authored = EnvironmentPresets.Aug2017Clear.Sky;
        SkySettings ruled = EnvironmentPresets.Aug2017KotkClear.Sky;

        Assert.Equal(
            authored with
            {
                CloudWeight0 = 0f,
                CloudWeight1 = 0f,
                CloudWeight2 = 0f,
                CloudWeight3 = 0f,
                CloudShadows = 0f,
            },
            ruled);

        // The map author's own 12:00 row, which the ruling overrides.
        Assert.Equal(0f, authored.CloudWeight0);
        Assert.Equal(0.005f, authored.CloudWeight1);
        Assert.Equal(0.15f, authored.CloudWeight2);
        Assert.Equal(0.1f, authored.CloudWeight3);
        Assert.Equal(0.625f, authored.CloudShadows);

        // The ruling: no clouds, and therefore no cloud shadows.
        Assert.Equal(0f, ruled.CloudWeight0 + ruled.CloudWeight1 + ruled.CloudWeight2 + ruled.CloudWeight3);
        Assert.Equal(0f, ruled.CloudShadows);
    }

    /// <summary>
    /// docs/82 §4: the silver lining is deliberately left at the author's 20.5 / 20.0 even though it
    /// is inert with no clouds, so the owner's A/B moves four wire words and not six. Z1 zeroed its
    /// equivalents; that mechanism is not adopted, and this test is why.
    /// </summary>
    [Fact]
    public void TheRulingLeavesTheSilverLiningAndTheFogTripleAlone()
    {
        SkySettings ruled = EnvironmentPresets.Aug2017KotkClear.Sky;

        Assert.Equal(20.5f, ruled.CloudSilverLiningThickness);
        Assert.Equal(20f, ruled.CloudSilverLiningBrightness);

        // "NO fog" cannot be taken literally: density 0 is ln(0) = -inf in the client's log-lerp,
        // and Validate() refuses it. The author's own 11:00-15:00 row stands.
        Assert.Equal(1.0e-4f, ruled.FogDensity);
        Assert.Equal(1f, ruled.FogFloor);
        Assert.Equal(0.05f, ruled.FogGradient);
        Assert.Throws<InvalidOperationException>(() => (ruled with { FogDensity = 0f }).Validate());

        // And Z1's fog triple is NOT adopted: it carries more fog than Cranberry's, not less.
        Assert.Equal(10f, EnvironmentPresets.LegacyD18Grey.Sky.FogFloor);
        Assert.Equal(0.0144f, EnvironmentPresets.LegacyD18Grey.Sky.FogGradient);
        Assert.True(EnvironmentPresets.LegacyD18Grey.Sky.FogFloor > ruled.FogFloor);
    }

    /// <summary>
    /// The same deviation set at the byte level - and the correction docs/82's research phase got
    /// wrong. The ruling <em>assigns</em> five fields, but <c>CLOUDWEIGHT0</c> was <b>already</b> 0
    /// in the map author's own 12:00 row (<c>R-Cumulus1="0.0"</c>), so exactly <b>four</b> 4-byte
    /// words of the 152-byte struct actually move: wire 8, 9, 10 (<c>CLOUDWEIGHT1..3</c>) and
    /// wire 11 (<c>CLOUDSHADOWS</c>). Wire 7 is unchanged.
    /// </summary>
    [Fact]
    public void Aug2017KotkClearChangesExactlyFourWordsOfAug2017ClearsWire()
    {
        using var authoredWriter = new PacketWriter();
        EnvironmentPresets.Aug2017Clear.Sky.WriteTo(authoredWriter);
        byte[] authored = authoredWriter.Written.ToArray();

        using var ruledWriter = new PacketWriter();
        EnvironmentPresets.Aug2017KotkClear.Sky.WriteTo(ruledWriter);
        byte[] ruled = ruledWriter.Written.ToArray();

        Assert.Equal(152, ruled.Length);
        Assert.Equal(authored.Length, ruled.Length);

        int[] changed = Enumerable.Range(0, authored.Length / 4)
            .Where(word => !authored.AsSpan(word * 4, 4).SequenceEqual(ruled.AsSpan(word * 4, 4)))
            .ToArray();

        Assert.Equal(new[] { 8, 9, 10, 11 }, changed);

        foreach (int word in changed)
        {
            Assert.Equal(0f, BitConverter.ToSingle(ruled, word * 4));
        }

        // Wire 7 is not in the list because the author had already written 0 there.
        Assert.Equal(0f, BitConverter.ToSingle(authored, 7 * 4));
        Assert.Equal(0f, BitConverter.ToSingle(ruled, 7 * 4));
    }

    /// <summary>
    /// The default moved, and it moved to the ruling rather than to the DESIGNED preset. Every
    /// <c>CRANBERRY_SKY</c> name still resolves, including the two new ones.
    /// </summary>
    [Fact]
    public void TheDefaultIsTheRulingAndBothNewPresetsResolveByName()
    {
        Assert.Same(EnvironmentPresets.Aug2017KotkClear, EnvironmentPresets.Default);
        Assert.Same(EnvironmentPresets.Aug2017KotkClear, EnvironmentPresets.FromNameOrDefault(null));
        Assert.Same(EnvironmentPresets.Aug2017KotkClear, EnvironmentPresets.FromNameOrDefault("nonsense"));

        Assert.Same(EnvironmentPresets.Aug2017KotkClear, EnvironmentPresets.ByName("aug2017kotkclear"));
        Assert.Same(
            EnvironmentPresets.Aug2017KotkClear1400, EnvironmentPresets.ByName("AUG2017KOTKCLEAR1400"));

        // The sweep order puts the default first and keeps both A/B controls in the table.
        Assert.Equal("Aug2017KotkClear", EnvironmentPresets.All[0].Name);
        Assert.Equal("Aug2017KotkClear1400", EnvironmentPresets.All[1].Name);
        Assert.Contains(EnvironmentPresets.Aug2017Clear, EnvironmentPresets.All);
        Assert.Contains(EnvironmentPresets.LegacyD18Grey, EnvironmentPresets.All);
    }

    /// <summary>
    /// docs/82 §5: the 14:00 sibling differs from the default in the clock and nothing else, so the
    /// owner's "~2 pm" A/B measures the hour alone.
    /// </summary>
    [Fact]
    public void TheFourteenHundredSiblingMovesOnlyTheClock()
    {
        EnvironmentSettings noon = EnvironmentPresets.Aug2017KotkClear;
        EnvironmentSettings afternoon = EnvironmentPresets.Aug2017KotkClear1400;

        Assert.Equal(noon.Sky, afternoon.Sky);
        Assert.Equal(noon.LightingFile, afternoon.LightingFile);

        Assert.Equal(12, noon.Clock.HourUtc);
        Assert.Equal(14, afternoon.Clock.HourUtc);
        Assert.Equal(1502798400UL, noon.Clock.FixedUnixTime);

        // Z1's ZoneSky.TwoPmSeconds = 50_400 s of day; 14:00Z on D16's date is 1502805600.
        Assert.Equal(1502805600UL, afternoon.Clock.FixedUnixTime);
        Assert.Equal(50_400UL, afternoon.Clock.FixedUnixTime % 86_400UL);
        Assert.True(afternoon.Clock.Freeze);
    }

    /// <summary>
    /// The argument for keeping 12:00, computed rather than asserted: 12:00's authored elevation
    /// sine is nearer <c>DayAngle</c> than 14:00's, so taking Z1's hour would <em>under</em>-blend
    /// <c>z2_colorkey_day.dds</c> - the only non-identity grade in the table - and dull the frame.
    /// </summary>
    [Fact]
    public void TakingZ1sFourteenHundredWouldUnderBlendTheOnlyNonIdentityGrade()
    {
        float noon = ZSunArc.ElevationSineAtHour(12);
        float afternoon = ZSunArc.ElevationSineAtHour(14);

        Assert.Equal(0.692, noon, 3);
        Assert.Equal(0.593, afternoon, 3);
        Assert.True(noon < ZSunArc.DayAngle);
        Assert.True(afternoon < noon);
        Assert.Equal(12, ZSunArc.HourClosestToDayAngle());

        // z2_colorkey_day.dds is the [Day] phase's grade, and [Day] is only fully engaged at
        // DayAngle - so the nearer hour is the more graded one.
        Assert.Equal("z2_colorkey_day.dds", LightingTable.Z2Day.ColorGradingFilename);
        Assert.Equal(LightingTable.Z2, EnvironmentPresets.Aug2017KotkClear.LightingFile);
    }

    /// <summary>
    /// The whole point of the change, stated as an inequality: the new default removes the only
    /// ground-darkening term in the struct, so it cannot be darker than what it replaced.
    /// </summary>
    [Fact]
    public void TheNewDefaultRemovesTheStructsOnlyGroundDarkeningTerm()
    {
        Assert.Equal(0.625f, EnvironmentPresets.Aug2017Clear.Sky.CloudShadows);
        Assert.Equal(0f, EnvironmentPresets.Default.Sky.CloudShadows);
        Assert.True(EnvironmentPresets.Default.Sky.CloudShadows < EnvironmentPresets.Aug2017Clear.Sky.CloudShadows);

        // And the fog - the strongest desaturator - was not made heavier to pay for it.
        Assert.Equal(EnvironmentPresets.Aug2017Clear.Sky.FogDensity, EnvironmentPresets.Default.Sky.FogDensity);
        Assert.Equal(EnvironmentPresets.Aug2017Clear.Sky.FogFloor, EnvironmentPresets.Default.Sky.FogFloor);
        Assert.Equal(EnvironmentPresets.Aug2017Clear.Sky.FogGradient, EnvironmentPresets.Default.Sky.FogGradient);
    }

    /// <summary>
    /// The host applies the whole look in one call, and the new default reaches
    /// <see cref="ZoneOptions"/> intact - the four fields that must move together, and nothing else.
    /// </summary>
    [Fact]
    public void WithEnvironmentCarriesTheNewDefaultOntoZoneOptions()
    {
        ZoneOptions applied = new ZoneOptions().WithEnvironment((string?)null);

        Assert.Equal(1502798400UL, applied.FixedUnixTime);
        Assert.True(applied.FreezeClock);
        Assert.Equal(LightingTable.Z2, applied.LightingFile);

        Assert.Equal(0f, applied.Weather.CloudWeight1);
        Assert.Equal(0f, applied.Weather.CloudWeight2);
        Assert.Equal(0f, applied.Weather.CloudWeight3);
        Assert.Equal(0f, applied.Weather.CloudShadows);

        using var expected = new PacketWriter();
        EnvironmentPresets.Aug2017KotkClear.Sky.WriteTo(expected);
        using var actual = new PacketWriter();
        applied.Weather.WriteTo(actual);
        Assert.Equal(expected.Written.ToArray(), actual.Written.ToArray());
    }

    // ------------------------------------------------------------------------------------------
    // docs/82 §6 - the composite-effect gate
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The deny list is exactly what the August client itself reported: id 0, and nothing carried
    /// over from Z1's 1087 build.
    /// <para>
    /// Lane 2B (docs/104) turned the gate itself into an allow list over the client's own
    /// <c>AugustEffectCatalog</c>; this list survives as the evidence trail. The two agree on 0.
    /// </para>
    /// </summary>
    [Fact]
    public void TheDenyListIsOnlyWhatThisBuildReportedMissing()
    {
        Assert.Equal(new uint[] { 0 }, CompositeEffectGate.DeniedInThisBuild.Select(row => row.Id).ToArray());
        Assert.True(CompositeEffectGate.IsDenied(0));

        // Z1's ids are 1087-build-specific and are deliberately NOT carried over (D53). They are
        // not on the deny list - but 5836 and 5904 are not in this build's table either, so the
        // allow list refuses them anyway, which is the whole point of D53.
        Assert.False(CompositeEffectGate.IsDenied(5836));
        Assert.False(CompositeEffectGate.IsDenied(5840));
        Assert.False(CompositeEffectGate.IsDenied(5904));

        // Every row carries the client-originated line that put it there (D29).
        Assert.All(CompositeEffectGate.DeniedInThisBuild, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Meaning));
            Assert.Contains("PlayClient", row.Evidence, StringComparison.Ordinal);
        });

        Assert.Throws<ArgumentOutOfRangeException>(() => CompositeEffectGate.DenialFor(5836));
        Assert.Equal(0u, CompositeEffectGate.DenialFor(0).Id);
    }

    /// <summary>
    /// The sentinel is suppressed, an id the client's own table defines passes, and - since lane 2B
    /// (docs/104) - an id the table does NOT define is suppressed too. <c>uint.MaxValue</c> used to
    /// pass, which was the deny list's whole weakness.
    /// </summary>
    [Fact]
    public void TheGateSuppressesTheSentinelAndAnythingThisBuildCannotResolve()
    {
        var lines = new List<string>();
        var gate = new CompositeEffectGate(enabled: true, lines.Add);

        Assert.False(gate.Allowed(0, "unit test"));
        Assert.False(gate.Allowed(0, "unit test"));
        Assert.False(gate.Allowed(0, "unit test"));

        // 5140 is a real row of the client's ActorCompositeEffectDefinitions.xml.
        Assert.True(AugustEffectCatalog.Contains(5140));
        Assert.True(gate.Allowed(5140, "unit test"));

        // uint.MaxValue is not, and no longer passes.
        Assert.False(AugustEffectCatalog.Contains(uint.MaxValue));
        Assert.False(gate.Allowed(uint.MaxValue, "unit test"));

        Assert.Equal(4, gate.SuppressedCount);
        Assert.Equal(3, gate.SuppressedCountFor(0));
        Assert.Equal(1, gate.SuppressedCountFor(uint.MaxValue));
        Assert.Equal(0, gate.SuppressedCountFor(5140));

        // Reported once per id, then silent, so a firefight cannot bury the log.
        Assert.Equal(2, lines.Count);
        Assert.Contains("suppressed composite effect 0 from unit test", lines[0], StringComparison.Ordinal);
        Assert.Contains("fix the origin", lines[0], StringComparison.Ordinal);
        Assert.Contains("ActorCompositeEffectDefinitions.xml", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheDisabledGateIsAPureBisectRowThatChangesNothing()
    {
        var lines = new List<string>();
        var gate = new CompositeEffectGate(enabled: false, lines.Add);

        Assert.True(gate.Allowed(0, "bisect"));
        Assert.Equal(0, gate.SuppressedCount);
        Assert.Empty(lines);
        Assert.Contains("gate OFF", gate.Describe(), StringComparison.Ordinal);

        // The deny list is still readable when the gate is off - the switch gates the action, not
        // the evidence.
        Assert.True(CompositeEffectGate.IsDenied(0));
    }

    [Fact]
    public void TheShippedGateIsOnAndSoIsItsZoneOptionsSwitch()
    {
        Assert.True(CompositeEffectGate.Default.Enabled);
        Assert.Contains("gate ON", CompositeEffectGate.Default.Describe(), StringComparison.Ordinal);
        Assert.Contains("allow list", CompositeEffectGate.Default.Describe(), StringComparison.Ordinal);
        Assert.True(new ZoneOptions().GateCompositeEffectIds);

        // The switch is the only environment-lane field the lane added to ZoneOptions, and turning
        // it off must not disturb anything else.
        var baseline = new ZoneOptions();
        Assert.Equal(baseline with { GateCompositeEffectIds = false }, baseline with { GateCompositeEffectIds = false });
        Assert.NotEqual(baseline, baseline with { GateCompositeEffectIds = false });
    }
}
