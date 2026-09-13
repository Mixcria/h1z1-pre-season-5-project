using System.Numerics;
using Cranberry.Tests.Zone.MatchDrop;
using Cranberry.Zone.Descent;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.ParachuteDescent;

/// <summary>
/// The parachute descent knob - docs/56 §2.5.
/// <para>
/// <b>The honest framing these tests exist to keep.</b> Cranberry cannot change how fast the client
/// falls: the descent comes from the client's own <c>MoveInfo</c> rows 130/4001 for vehicle 13, and
/// it is a <b>player-controlled 10-56 m/s band</b> — 43.5-45.8 m/s flown, 9.8 m/s hands-off
/// (docs/115 §2 corrects this file's earlier "39.5-42.5 m/s constant"). What the server owns is the
/// <em>duration</em>, through the release altitude. So the first and most important test here is
/// that the default changes nothing at all — and since D238 the default is the client's own 850 m
/// <c>KotK.SkySpawn</c> slab, so "changes nothing" and "ships retail's own number" are one statement.
/// </para>
/// </summary>
public sealed class DescentTuningTests
{
    private static string? Nothing(string _) => null;

    private static Func<string, string?> Env(params (string Key, string Value)[] pairs) =>
        key => pairs.FirstOrDefault(pair => pair.Key == key).Value;

    // ------------------------------------------------------- the default changes nothing

    /// <summary>
    /// <c>TargetDescentSeconds = 0</c> must be byte-identical to wave 4 - docs/56 I2 asks for this to
    /// be pinned, because the drop feeds the live-proven zoning burst of regression guard 4.
    /// <c>Assert.Same</c> rather than <c>Assert.Equal</c>: the options instance is not even copied.
    /// </summary>
    [Fact]
    public void TheDefaultLeavesTheDropOptionsUntouched()
    {
        DropOptions options = DropOptions.Default;

        Assert.False(DescentSettings.Default.ChangesAnything);
        Assert.Same(options, options.WithDescent(DescentSettings.Default));
        Assert.Same(options, options.WithDescent(DescentTuning.Wave4Default));
        Assert.Same(DescentSettings.Default, DescentTuning.Wave4Default);

        // Wave 9 (docs/88 E1) moved the SHIPPED default off this preset; D238 (docs/115 §1) moved
        // it back, because Legacy36's 1,454 m is an emulator's altitude and 850 m is the client's.
        Assert.Same(DescentTuning.ClientSkySpawn850, DescentTuning.ShippedDefault);
        Assert.Same(DescentSettings.Default, DescentTuning.ShippedDefault);
    }

    /// <summary>
    /// <b>D238, docs/115 §1 — and it reverses wave 9's D125.</b> The owner ruled "do whatever
    /// retail is; our current dropzone height is a guess". Retail's own release altitude is not
    /// published, not on disk and never measured, so the shipped release is the only altitude the
    /// client itself states: <c>Z2Areas.xml</c>'s <c>KotK.SkySpawn</c> slab, 850 m absolute. The
    /// 1,454 m wave 9 shipped came from the friend's emulator's 1,500 m, not from retail.
    /// </summary>
    [Fact]
    public void AnUnsetEnvironmentIsTheShippedDefault()
    {
        DescentSettings settings = DescentTuning.FromEnvironment(Nothing, out string? note);

        Assert.Same(DescentTuning.ShippedDefault, settings);
        Assert.Equal(0f, settings.TargetDescentSeconds);
        Assert.Null(note);
        Assert.Equal("ClientSkySpawn850", DescentTuning.NameOf(settings));
    }

    /// <summary>
    /// The A/B the owner is owed: the old ride stays one environment variable away, and it is still
    /// the exact same instance it always was, so <c>Wave4Default</c> cannot drift into "nearly".
    /// </summary>
    [Fact]
    public void TheOldRideIsStillReachableByNameAndIsStillAnExactNoOp()
    {
        Assert.Same(DescentTuning.ShippedDefault, DescentTuning.FromNameOrDefault(null));
        Assert.Same(DescentTuning.ShippedDefault, DescentTuning.FromNameOrDefault("   "));
        Assert.Same(DescentTuning.ShippedDefault, DescentTuning.FromNameOrDefault("nonsense"));
        Assert.Same(DescentTuning.Wave4Default, DescentTuning.FromNameOrDefault("Wave4Default"));

        DescentSettings restored = DescentTuning.FromEnvironment(
            Env((DescentTuning.PresetVariable, "Wave4Default")), out string? note);

        Assert.Same(DescentSettings.Default, restored);
        Assert.Same(DropOptions.Default, DropOptions.Default.WithDescent(restored));
        Assert.NotNull(note);
        Assert.Contains("Wave4Default", note, StringComparison.Ordinal);
        Assert.DoesNotContain("IGNORED", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>D125's ride, kept as the revert.</b> <c>Legacy36</c> still asks for ~1 454 m of air and
    /// still gives 36 s of it over Z2's lowest anchor and its highest alike — which is the whole
    /// reason the seconds knob was kept over Z1's absolute 1500 m (docs/88 §2e). It is simply no
    /// longer what an unset environment gets (D238).
    /// </summary>
    [Theory]
    [InlineData(-34.9f)]   // RubyLakeCampgroundNorth, the lowest anchor seen in a live drop
    [InlineData(-5.0f)]    // DoubleHFarms, the owner's own drop on 2026-08-30
    [InlineData(241.9f)]   // Z2's highest anchor
    public void TheLegacyRideIsStillThirtySixSecondsOverEveryAnchor(float groundY)
    {
        DescentSettings legacy = DescentTuning.Legacy36;

        Assert.NotSame(legacy, DescentTuning.ShippedDefault);
        Assert.Equal(1454.4f, legacy.RequiredClearanceMetres, 1);

        float airY = legacy.AirSpawnAltitude(
            groundY, DropOptions.Default.SkySpawnAltitude, DropOptions.Default.MinimumClearanceMetres);

        Assert.True(airY > groundY);
        Assert.Equal(36f, legacy.ExpectedSecondsFor(airY, groundY), 1);
    }

    /// <summary>
    /// The planning rate is a measurement, not a control - it must stay inside the client's own
    /// authored envelope, and it must stay at the mean of the six captured DIVED drops until a
    /// seventh changes it. docs/115 §2: it is not the rate, it is the fast end of a band.
    /// </summary>
    [Fact]
    public void TheRateIsTheMeasuredOneAndSitsInsideTheClientsOwnEnvelope()
    {
        Assert.Equal(40.4f, DescentSettings.Default.PlannedDescentMetresPerSecond);
        Assert.InRange(DescentSettings.Default.PlannedDescentMetresPerSecond, DescentTuning.MinimumRate, DescentTuning.MaximumRate);
        Assert.Equal(10f, DescentTuning.MinimumRate);   // MoveInfo 130/4001 MIN_TERM_VELOCITY
        Assert.Equal(56f, DescentTuning.MaximumRate);   // MoveInfo 130/4001 MAX_TERM_VELOCITY
    }

    // ------------------------------------------------------------------ the arithmetic

    [Fact]
    public void ThirtySecondsAsksForThirtySecondsWorthOfAir()
    {
        DescentSettings settings = DescentTuning.Owner30;

        Assert.True(settings.ChangesAnything);
        Assert.Equal(30f * 40.4f, settings.RequiredClearanceMetres, 3);
        Assert.Equal(1212f, settings.RequiredClearanceMetres, 1);
    }

    /// <summary>
    /// docs/56 §2.5's formula: <c>max(skySpawn, groundY + max(clearance, seconds * rate))</c>. The
    /// derived 850 m sky spawn stays the floor, so a knob smaller than the existing clearance cannot
    /// lower the drop.
    /// </summary>
    [Theory]
    [InlineData(0f, 40f, 850f)]         // off: the derived sky spawn
    [InlineData(30f, 40f, 1252f)]       // Pleasant Valley-ish ground, docs/56 2.5's ~1250 m
    [InlineData(30f, 241.9f, 1453.9f)]  // Z2's highest anchor
    [InlineData(5f, 40f, 850f)]         // 202 m of air is less than the 400 m clearance floor
    public void TheAirSpawnFollowsTheDocumentedFormula(float seconds, float groundY, float expected)
    {
        var settings = DescentSettings.Default with { TargetDescentSeconds = seconds };

        Assert.Equal(
            expected,
            settings.AirSpawnAltitude(groundY, DropOptions.Default.SkySpawnAltitude, DropOptions.Default.MinimumClearanceMetres),
            1);
    }

    [Fact]
    public void TheExpectedFallTimeInvertsTheAltitude()
    {
        Assert.Equal(30f, DescentTuning.Owner30.ExpectedSecondsFor(1252f, 40f), 2);
        Assert.Equal(0f, DescentSettings.Default.ExpectedSecondsFor(40f, 850f));
    }

    // --------------------------------------------------------------- through the planner

    /// <summary>
    /// The end-to-end statement: the knob really does raise the air spawn the planner produces, and
    /// leaving it off really does leave every plan alone. The gas circle is deliberately null (the
    /// planner's documented "gas disabled" path) so this test cannot be perturbed by the gas lane
    /// retuning phase 1.
    /// </summary>
    [Fact]
    public void ThePlannerReleasesHigherWithTheKnobOnAndIdenticallyWithItOff()
    {
        DropOptions off = DropOptions.Default;
        DropOptions on = off.WithDescent(DescentTuning.Owner30);

        for (ulong seed = 1; seed <= 40; seed++)
        {
            DropPlan low = Assert.IsType<DropPlan>(DropPlanner.Plan(DropFixture.Places, null, off, seed));
            DropPlan high = Assert.IsType<DropPlan>(DropPlanner.Plan(DropFixture.Places, null, on, seed));

            // Same match, same place, same landing point - only the release altitude moves.
            Assert.Equal(low.Poi.Area, high.Poi.Area);
            Assert.Equal(low.Position, high.Position);

            Assert.Equal(DropOptions.Default.SkySpawnAltitude, low.AirPosition.Y);
            Assert.True(high.AirPosition.Y > low.AirPosition.Y, $"seed {seed}: {high.AirPosition.Y} <= {low.AirPosition.Y}");
            Assert.Equal(
                DescentTuning.Owner30.AirSpawnAltitude(
                    high.Position.Y, off.SkySpawnAltitude, off.MinimumClearanceMetres),
                high.AirPosition.Y,
                1);

            // And the whole point: about thirty seconds in the air, not about twenty.
            Assert.InRange(DescentTuning.Owner30.ExpectedSecondsFor(high.AirPosition.Y, high.Position.Y), 29.9f, 30.1f);
            Assert.InRange(DescentTuning.Owner30.ExpectedSecondsFor(low.AirPosition.Y, low.Position.Y), 14f, 23f);
        }
    }

    [Fact]
    public void ThePlanStillLandsWherePlannedWhenTheKnobIsOn()
    {
        DropOptions on = DropOptions.Default.WithDescent(DescentTuning.Legacy36);
        DropPlan plan = Assert.IsType<DropPlan>(DropPlanner.Plan(DropFixture.Places, null, on, 12345));

        Assert.Equal(new Vector2(plan.Position.X, plan.Position.Z), new Vector2(plan.AirPosition.X, plan.AirPosition.Z));
        Assert.True(plan.AirPosition.Y > plan.Position.Y);
        on.Validate();
    }

    // ------------------------------------------------------------------ the environment

    [Fact]
    public void ThePresetsAreTheThreeTheOwnerIsOffered()
    {
        string[] expected = ["ClientSkySpawn850", "Wave4Default", "Owner30", "Legacy36"];
        Assert.Equal(expected, DescentTuning.PresetNames);
        Assert.Equal(0f, DescentTuning.FromNameOrDefault("clientskyspawn850").TargetDescentSeconds);
        Assert.Equal(0f, DescentTuning.FromNameOrDefault("wave4default").TargetDescentSeconds);
        Assert.Equal(30f, DescentTuning.FromNameOrDefault("OWNER30").TargetDescentSeconds);
        Assert.Equal(36f, DescentTuning.FromNameOrDefault("Legacy36").TargetDescentSeconds);
    }

    [Fact]
    public void AnUnknownPresetIsIgnoredAndReported()
    {
        DescentSettings settings = DescentTuning.FromEnvironment(
            Env((DescentTuning.PresetVariable, "Slow")), out string? note);

        Assert.Same(DescentTuning.ShippedDefault, settings);
        Assert.NotNull(note);
        Assert.Contains("IGNORED", note, StringComparison.Ordinal);
        Assert.Contains("Slow", note, StringComparison.Ordinal);
        Assert.Contains("Legacy36", note, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSecondsOverrideBeatsThePreset()
    {
        DescentSettings settings = DescentTuning.FromEnvironment(
            Env((DescentTuning.PresetVariable, "Owner30"), (DescentTuning.SecondsVariable, "24.5")),
            out string? note);

        Assert.Equal(24.5f, settings.TargetDescentSeconds);
        Assert.NotNull(note);
        Assert.Contains("Owner30", note, StringComparison.Ordinal);
        Assert.Contains("TargetDescentSeconds=24.5", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// A typo in a launch script must never stop a host, and must never silently half-apply. Every
    /// rejected value leaves the preset in place and says so.
    /// </summary>
    [Theory]
    [InlineData("thirty")]
    [InlineData("-5")]
    [InlineData("999")]
    [InlineData("NaN")]
    public void ABadSecondsValueIsIgnoredWithANoteRatherThanThrowing(string raw)
    {
        DescentSettings settings = DescentTuning.FromEnvironment(
            Env((DescentTuning.SecondsVariable, raw)), out string? note);

        Assert.Equal(DescentTuning.ShippedDefault.TargetDescentSeconds, settings.TargetDescentSeconds);
        Assert.NotNull(note);
        Assert.Contains("IGNORED", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rate's rails are the client's own terminal velocities, so a value outside them is not a
    /// preference - it is a claim about the client that the captures contradict.
    /// </summary>
    [Theory]
    [InlineData("9.9")]
    [InlineData("56.1")]
    [InlineData("0")]
    public void ARateOutsideTheClientsEnvelopeIsRefused(string raw)
    {
        DescentSettings settings = DescentTuning.FromEnvironment(
            Env((DescentTuning.RateVariable, raw)), out string? note);

        Assert.Equal(40.4f, settings.PlannedDescentMetresPerSecond);
        Assert.NotNull(note);
        Assert.Contains("IGNORED", note, StringComparison.Ordinal);
    }

    [Fact]
    public void ARateInsideTheEnvelopeIsAccepted()
    {
        DescentSettings settings = DescentTuning.FromEnvironment(
            Env((DescentTuning.SecondsVariable, "30"), (DescentTuning.RateVariable, "42.5")),
            out string? note);

        Assert.Equal(42.5f, settings.PlannedDescentMetresPerSecond);
        Assert.Equal(1275f, settings.RequiredClearanceMetres, 1);
        Assert.NotNull(note);
        Assert.DoesNotContain("IGNORED", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// D239 (docs/115 §2): the note must say the rate is the PLAYER's and name the band. It used to
    /// say "the client's own", which is true of the envelope and false of the rate.
    /// </summary>
    [Fact]
    public void TheNoteSaysWhoOwnsTheRate()
    {
        string raised = DescentTuning.Describe(DescentTuning.Owner30);
        Assert.Contains("the PLAYER's, 10-56 m/s", raised, StringComparison.Ordinal);
        Assert.Contains("planning mean", raised, StringComparison.Ordinal);

        string shipped = DescentTuning.Describe(DescentSettings.Default);
        Assert.Contains("850 m ABSOLUTE", shipped, StringComparison.Ordinal);
        Assert.Contains("the PLAYER's, 10-56 m/s", shipped, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationRefusesWhatTheEnvironmentWouldHaveRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (DescentSettings.Default with { TargetDescentSeconds = -1f }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (DescentSettings.Default with { PlannedDescentMetresPerSecond = 100f }).Validate());
        DescentTuning.Owner30.Validate();
        DescentTuning.Legacy36.Validate();
    }
}
