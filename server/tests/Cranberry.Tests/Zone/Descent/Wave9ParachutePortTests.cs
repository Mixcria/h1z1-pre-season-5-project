using Cranberry.Zone.Descent;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.ParachuteDescent;

/// <summary>
/// Wave 9, docs/88 — "can we just port the parachute over from my server?"
/// <para>
/// <b>The honest answer these tests pin: the packets are already ported; the NUMBER is not.</b>
/// Measured this wave, the wire is a clean base -1 that Cranberry already emits, the descent rate is
/// the client's own <c>MoveInfo</c> rows 130/4001 and Z1 carries no constant for it either, and
/// 36 of 36 rides the client survived across 318 host logs completed their landing handover. The one
/// player-visible difference is that his ride lasts ~36 s (41.8 s in the emulator footage) and ours
/// measured 21.8 s on 2026-08-30 20:37.
/// </para>
/// <para>
/// So this file pins two things and refuses a third: the shipped ride is now his, the descent
/// deadline closes the one measured robustness gap, and Z1's extra mount packets stay out.
/// </para>
/// <para>
/// <b>SUPERSEDED IN PART, 2026-09-03 (D238/D239, docs/115).</b> Two of wave 9's conclusions did not
/// survive the parachute audit. Wave 9 made <c>Legacy36</c> the SHIPPED default on the strength of
/// the friend's server's 1,500 m; the owner has since ruled "do whatever retail is", and the only
/// altitude retail's own client states is <c>KotK.SkySpawn</c>'s 850 m — so <c>Legacy36</c> is now
/// the revert and this file tests it under that name. And the descent deadline wave 9 called
/// "designed never to fire" fired three times on 2026-09-03, each time dismounting a live player
/// about 600 m in the air; its formula lives on here only as
/// <c>DescentDeadline.LegacyDeadlineSeconds</c>. <c>ParachuteRetailTests</c> is the current
/// statement; this file is kept because everything else in it is still true.
/// </para>
/// </summary>
public sealed class Wave9ParachutePortTests
{
    // -------------------------------------------------------------- the ported value (docs/88 §2)

    /// <summary>
    /// The owner's own drop, replayed as arithmetic. <c>host-20260830-203615.log</c>: air
    /// <c>&lt;-2820.5, 850.0, 3187.1&gt;</c> to ground <c>-5.0</c> is an 855 m fall, and the mount
    /// burst at 20:37:33.874 to <c>Vehicle.Dismiss</c> at 20:37:55.639 is 21.765 s — 39.3 m/s, inside
    /// the client's measured 39.5-42.5 m/s band. Wave 4's 850 m sky spawn is what makes it short.
    /// </summary>
    [Fact]
    public void TheOwnersMeasuredRideIsWhatTheOldDefaultPredicts()
    {
        DescentSettings wave4 = DescentTuning.Wave4Default;

        Assert.Equal(21.2f, wave4.ExpectedSecondsFor(850f, -5f), 1);
        Assert.InRange(855f / 21.765f, DescentTuning.MinimumRate, DescentTuning.MaximumRate);
    }

    /// <summary>
    /// D53 — "take what I built and make it better". His number (a 36 s ride, from an absolute 1500 m
    /// release on Z1 <c>ZoneMatchFlow.RetailDropAltitude</c>) expressed in Cranberry's better shape:
    /// seconds, so the ride is the same over terrain that varies by 277 m. A flat 1500 m would give
    /// ~38 s over Z2's lowest anchor and ~31 s over its highest.
    /// </summary>
    [Fact]
    public void TheShippedRideIsHisRideAndIsFlatterThanHisAbsoluteAltitudeWouldBe()
    {
        // D238: this is Legacy36 by name now, not ShippedDefault. The SHAPE argument below is what
        // survives - seconds hold the ride constant over terrain, an absolute altitude does not.
        DescentSettings shipped = DescentTuning.Legacy36;
        Assert.Equal(36f, shipped.TargetDescentSeconds);

        const float lowest = -34.9f;
        const float highest = 241.9f;

        static float Seconds(DescentSettings settings, float groundY) =>
            settings.ExpectedSecondsFor(
                settings.AirSpawnAltitude(
                    groundY, DropOptions.Default.SkySpawnAltitude, DropOptions.Default.MinimumClearanceMetres),
                groundY);

        Assert.Equal(Seconds(shipped, lowest), Seconds(shipped, highest), 2);

        // Z1's own shape, for the contrast: an absolute release, so the ride varies with the ground.
        float z1Low = (1500f - lowest) / shipped.PlannedDescentMetresPerSecond;
        float z1High = (1500f - highest) / shipped.PlannedDescentMetresPerSecond;
        Assert.True(z1Low - z1High > 6f, $"Z1's absolute 1500 varies by {z1Low - z1High:F1} s");
    }

    /// <summary>
    /// <b>docs/48 retired <c>ZoneOptions.DropAltitude = 1500f</c> as unsourced, and it must stay
    /// retired.</b> Z1's own <c>ZoneRetailSpawn.cs:140-152</c> says 1500 is the friend's emulator's
    /// number, not retail's. The ride is reached through the seconds knob instead, and the derived
    /// 850 m <c>KotK.SkySpawn</c> stays the floor rather than being overwritten.
    /// </summary>
    [Fact]
    public void TheShippedRideDoesNotReintroduceTheRetiredAbsoluteAltitude()
    {
        Assert.Equal(850f, DropOptions.Default.SkySpawnAltitude);

        DropOptions shipped = DropOptions.Default.WithDescent(DescentTuning.Legacy36);

        Assert.Equal(850f, shipped.SkySpawnAltitude);
        Assert.Equal(1454.4f, shipped.MinimumClearanceMetres, 1);
        shipped.Validate();
    }

    /// <summary>
    /// A shipped default is only shipped if a default boot gets it. This walks the same path
    /// <c>Program.cs</c> does — <c>FromEnvironment</c> with nothing set — and then through
    /// <c>WithDescent</c> exactly as the host wires it.
    /// </summary>
    /// <summary>
    /// <b>D238 inverted this.</b> Wave 9 asserted that a default boot RAISES the drop; the owner's
    /// ruling is that it must not, because 1,454 m is a design number and 850 m is the client's. The
    /// statement kept is the mechanical one: naming <c>Legacy36</c> raises it, and an unset
    /// environment leaves the release on the client's own slab.
    /// </summary>
    [Fact]
    public void ADefaultBootDoesNotRaiseTheDropAndNamingLegacy36Does()
    {
        DescentSettings descent = DescentTuning.FromEnvironment(_ => null, out string? note);

        Assert.Null(note);
        Assert.False(descent.ChangesAnything);
        Assert.Same(DropOptions.Default, DropOptions.Default.WithDescent(descent));

        DropOptions drop = DropOptions.Default.WithDescent(DescentTuning.Legacy36);
        Assert.NotSame(DropOptions.Default, drop);
        Assert.True(drop.MinimumClearanceMetres > DropOptions.Default.MinimumClearanceMetres);
    }

    // ------------------------------------------------------- the descent deadline (docs/88 §4b)

    /// <summary>
    /// <b>SUPERSEDED BY D239 — this is the formula that fired three times.</b> Wave 9's deadline was
    /// Z1's <c>DropPhaseMaxSeconds = 180</c> idea in Cranberry's units: twice the expected ride plus
    /// 15 s. The flaw is the third column of the middle row — <b>87.0 s</b> against a 1,449 m release
    /// whose honest hands-off ride is ~148 s — and on 2026-09-03 it force-dismounted three live
    /// players in mid-air. It is kept as the revert and pinned here so the revert cannot drift.
    /// </summary>
    [Theory]
    [InlineData(850f, -5f, 57.3f)]        // the owner's 2026-08-30 drop, expected 21.2 s dived
    [InlineData(1449.4f, -5f, 87.0f)]     // THE REGRESSION: 87.0 s against a ~148 s hands-off ride
    [InlineData(2006f, -4.9f, 114.5f)]    // the highest release in the capture archive, ~43 s dived
    public void TheLegacyDeadlineIsAlwaysFarAboveTheDivedRide(float airY, float groundY, float expected)
    {
        double deadline = DescentDeadline.LegacyDeadlineSeconds(airY, groundY);

        Assert.Equal(expected, deadline, 1);
        Assert.True(
            deadline > 2d * DescentSettings.Default.ExpectedSecondsFor(airY, groundY),
            $"{deadline:F1} s is not clear of the dived ride");

        // And the point of D239: the rebuilt deadline is always above the HANDS-OFF ride, which the
        // legacy one is not - at 1,449 m it is 87 s against 145 s.
        Assert.True(
            DescentDeadline.DeadlineSeconds(airY, groundY)
                > DescentSettings.HandsOffSecondsFor(airY, groundY),
            "the rebuilt deadline must clear the hands-off ride");
    }

    /// <summary>
    /// The three states a real ride passes through, against D239's rebuilt clock: 158.3 s from the
    /// shipped 850 m release, which is 1.5x the ~85 s a hands-off player takes plus 30 s. Nothing
    /// that completes normally can trip it, and an unstarted clock (<c>MountedAtMs == 0</c>, so a
    /// zero or negative elapsed time) never can. <b>Being expired is not on its own a dismount</b> —
    /// see <c>ParachuteRetailTests</c> for the altitude and silence gates.
    /// </summary>
    [Theory]
    [InlineData(0L, false)]          // the mount clock was never started
    [InlineData(-500L, false)]       // a clock that has not moved forward
    [InlineData(21_765L, false)]     // the owner's own completed ride
    [InlineData(85_000L, false)]     // a whole hands-off ride, which the old formula killed at 57 s
    [InlineData(158_000L, false)]    // still inside the deadline
    [InlineData(159_000L, true)]     // past it
    [InlineData(400_000L, true)]
    public void TheDeadlineOnlyFiresOnARideThatIsAlreadyBroken(long elapsedMs, bool expired)
    {
        Assert.Equal(expired, DescentDeadline.HasExpired(elapsedMs, 850f, -5f));
    }

    /// <summary>
    /// <b>The planning rate is a measurement, not a setting.</b> <c>CRANBERRY_DESCENT_RATE</c>
    /// changes only how much altitude the seconds knob asks for, so a deadline must never read it.
    /// D239 goes further than wave 9 did and takes the rate out of the deadline's reach altogether:
    /// the rebuilt clock is computed at the client's own <c>MIN_TERM_VELOCITY</c> and there is no
    /// overload that accepts a <c>DescentSettings</c> at all.
    /// </summary>
    [Fact]
    public void TheDeadlineUsesTheClientsOwnRateAndNotATunedOne()
    {
        DescentSettings tuned = DescentSettings.Default with { PlannedDescentMetresPerSecond = 10f };
        float airY = tuned.AirSpawnAltitude(
            0f, DropOptions.Default.SkySpawnAltitude, DropOptions.Default.MinimumClearanceMetres);

        // The legacy formula could be misled by a tuned rate; the rebuilt one structurally cannot.
        double measured = DescentDeadline.LegacyDeadlineSeconds(airY, 0f);
        double wrong = DescentDeadline.LegacyDeadlineSeconds(tuned, airY, 0f);
        Assert.True(wrong > measured * 2d, $"tuned {wrong:F1} s vs measured {measured:F1} s");

        Assert.Equal(
            (airY / DescentTuning.MinimumRate * DescentDeadline.HandsOffRideMultiple)
                + DescentDeadline.HandsOffSlackSeconds,
            DescentDeadline.DeadlineSeconds(airY, 0f),
            3);
    }

    [Fact]
    public void TheDeadlineRefusesANullSettings()
    {
        Assert.Throws<ArgumentNullException>(() => DescentDeadline.LegacyDeadlineSeconds(null!, 850f, 0f));
    }
}
