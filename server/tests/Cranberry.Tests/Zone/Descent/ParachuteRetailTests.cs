using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Tests.Zone.MatchDrop;
using Cranberry.Zone;
using Cranberry.Zone.Descent;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.ParachuteDescent;

/// <summary>
/// <b>docs/115 — the parachute, made retail by the only evidence there is.</b>
/// <para>
/// The owner's ruling of 2026-09-03 was "do whatever retail is; our current dropzone height is a
/// guess". Retail's own release altitude is not published, not on disk and has never been measured,
/// so "whatever retail is" resolves to the one altitude the August client itself states:
/// <c>Z2Areas.xml</c>'s <c>KotK.SkySpawn</c> slab, y-centre <b>850 m absolute</b>. These tests pin
/// that, the descent guard that stopped dismounting live players in mid-air, the jitter floor that
/// stopped throwing away a rich place's loot, and the three parachute skins the client ships.
/// </para>
/// </summary>
public sealed class ParachuteRetailTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    // ============================================================ D238 — the release altitude

    /// <summary>
    /// <b>The ruling, as arithmetic.</b> An unset environment releases at the client's own 850 m
    /// slab, and it does so by being <see cref="DescentSettings.Default"/> itself — so the shipped
    /// drop is not merely equal to the old <c>Wave4Default</c>, it is the same instance, and
    /// <c>WithDescent</c> cannot even in principle copy the options record.
    /// </summary>
    [Fact]
    public void TheShippedReleaseIsTheClientsOwnSkySpawnSlab()
    {
        DescentSettings shipped = DescentTuning.FromEnvironment(_ => null, out string? note);

        Assert.Null(note);
        Assert.Same(DescentTuning.ClientSkySpawn850, shipped);
        Assert.Same(DescentSettings.Default, shipped);
        Assert.Same(DescentTuning.Wave4Default, shipped);
        Assert.Equal(0f, shipped.TargetDescentSeconds);
        Assert.False(shipped.ChangesAnything);
        Assert.Equal("ClientSkySpawn850", DescentTuning.NameOf(shipped));

        // The whole point: nothing is raised above the slab, so the release IS 850 m absolute.
        Assert.Same(DropOptions.Default, DropOptions.Default.WithDescent(shipped));
        Assert.Equal(850f, DropOptions.Default.SkySpawnAltitude);
    }

    /// <summary>
    /// End to end through the planner: every match releases at exactly 850.0 absolute, whatever the
    /// ground under the marker it drew. The gas circle is deliberately null (the planner's own
    /// "gas disabled" path) so the gas lane retuning phase 1 cannot perturb this.
    /// </summary>
    [Fact]
    public void EveryPlannedDropReleasesAtEightHundredAndFiftyAbsolute()
    {
        DropOptions shipped = DropOptions.Default.WithDescent(DescentTuning.ShippedDefault);

        for (ulong seed = 1; seed <= 60; seed++)
        {
            DropPlan plan = Assert.IsType<DropPlan>(DropPlanner.Plan(DropFixture.Places, null, shipped, seed));

            Assert.Equal(850f, plan.AirPosition.Y);
            Assert.Equal(
                new Vector2(plan.Position.X, plan.Position.Z),
                new Vector2(plan.AirPosition.X, plan.AirPosition.Z));
        }
    }

    /// <summary>
    /// <b>The ride the owner is about to feel, in both directions of the band.</b> The audit's own
    /// menu (section 5, [U-1]) for the shipped release: ~21 s if he dives, ~85 s if he lets go, and
    /// a glide budget of roughly 380 m instead of the 587-672 m measured at 1,454 m.
    /// </summary>
    [Theory]
    [InlineData(-34.9f)]   // RubyLakeCampgroundNorth, the lowest anchor seen in a live drop
    [InlineData(-5.0f)]    // DoubleHFarms
    [InlineData(241.9f)]   // Z2's highest anchor
    public void TheShippedRideIsAboutTwentyOneSecondsDivedAndEightyFiveHandsOff(float groundY)
    {
        DescentSettings shipped = DescentTuning.ShippedDefault;
        float airY = shipped.AirSpawnAltitude(
            groundY, DropOptions.Default.SkySpawnAltitude, DropOptions.Default.MinimumClearanceMetres);

        Assert.Equal(850f, airY);

        float fall = airY - groundY;
        Assert.InRange(fall, 608f, 892f);

        float dived = fall / DescentSettings.MeasuredFlownMetresPerSecond;
        float handsOff = DescentSettings.HandsOffSecondsFor(airY, groundY);

        Assert.InRange(dived, 13f, 20f);
        Assert.InRange(handsOff, 60f, 90f);

        // The glide budget follows the ride: 16.6-18.9 m/s whole-ride horizontal mean, measured.
        Assert.InRange(dived * 18f, 230f, 360f);
    }

    /// <summary>
    /// The reverts stay one word away and still mean what they meant. <c>Legacy36</c> is the ride
    /// wave 9 shipped; <c>Owner30</c> is the middle position; both raise the release above the slab
    /// rather than replacing it.
    /// </summary>
    [Fact]
    public void TheOldRidesAreStillReachableByName()
    {
        Assert.Equal(
            ["ClientSkySpawn850", "Wave4Default", "Owner30", "Legacy36"],
            DescentTuning.PresetNames);

        Assert.Equal(36f, DescentTuning.FromNameOrDefault("Legacy36").TargetDescentSeconds);
        Assert.Equal(30f, DescentTuning.FromNameOrDefault("owner30").TargetDescentSeconds);
        Assert.Same(DescentSettings.Default, DescentTuning.FromNameOrDefault("wave4default"));
        Assert.Same(DescentSettings.Default, DescentTuning.FromNameOrDefault("ClientSkySpawn850"));

        DropOptions legacy = DropOptions.Default.WithDescent(DescentTuning.Legacy36);
        Assert.Equal(850f, legacy.SkySpawnAltitude);
        Assert.Equal(1454.4f, legacy.MinimumClearanceMetres, 1);
    }

    /// <summary>
    /// <b>AUDIT-parachute G10.</b> The boot line used to say "from 850 m" while releasing 1,454 m
    /// above the ground — the one line the owner reads at boot, understating the altitude by 604 m.
    /// The description now names the real release in both cases and states the rate as the
    /// player-controlled band it is (G2), never as a single client constant.
    /// </summary>
    [Fact]
    public void TheBootDescriptionNamesTheRealReleaseAndTheHonestBand()
    {
        string shipped = DescentTuning.Describe(DescentTuning.ShippedDefault);
        Assert.Contains("850 m ABSOLUTE", shipped, StringComparison.Ordinal);
        Assert.Contains("KotK.SkySpawn", shipped, StringComparison.Ordinal);
        Assert.Contains("hands-off", shipped, StringComparison.Ordinal);
        Assert.Contains("PLAYER's, 10-56 m/s", shipped, StringComparison.Ordinal);
        Assert.DoesNotContain("the client's 40.4", shipped, StringComparison.Ordinal);

        string raised = DescentTuning.Describe(DescentTuning.Legacy36);
        Assert.Contains("max(850 m absolute, ground + 1454 m)", raised, StringComparison.Ordinal);
        Assert.Contains("hands-off", raised, StringComparison.Ordinal);
        Assert.Contains("planning mean", raised, StringComparison.Ordinal);
    }

    // ============================================================ D239 — the descent deadline

    /// <summary>
    /// <b>The regression, replayed.</b> The three force-dismounts of 2026-09-03 —
    /// <c>host-20260903-082114</c>, <c>-082424</c>, <c>-083422</c> — all fired at <b>87.0 s</b> into
    /// a ride from the 1,454 m <c>Legacy36</c> release, dismounting a working client roughly 600 m in
    /// the air. This walks a real 9.8 m/s hands-off descent second by second and asserts that the
    /// guard asks for <b>no handover at any point before touchdown</b>.
    /// </summary>
    [Fact]
    public void AHandsOffDescentIsNeverDismountedInTheAir()
    {
        const float groundY = -5f;
        float airY = DescentTuning.Legacy36.AirSpawnAltitude(
            groundY, DropOptions.Default.SkySpawnAltitude, DropOptions.Default.MinimumClearanceMetres);

        Assert.Equal(1449.4f, airY, 1);

        // The pre-D239 deadline, for the record: this is the 87.0 s that fired three times.
        Assert.Equal(87.0d, DescentDeadline.LegacyDeadlineSeconds(airY, groundY), 1);

        float chuteY = airY;
        for (int second = 1; chuteY > groundY; second++)
        {
            chuteY = MathF.Max(groundY, airY - (second * DescentSettings.MeasuredHandsOffMetresPerSecond));
            bool touchedDown = chuteY <= groundY;

            // The client is streaming its pose every tick, as it did in all three sessions.
            DescentHandover verdict = DescentDeadline.Evaluate(
                guard: true,
                elapsedMs: second * 1000L,
                silentMs: 0L,
                airY: airY,
                groundY: groundY,
                chuteY: chuteY);

            if (!touchedDown && chuteY - groundY > DescentDeadline.GroundProximityMetres)
            {
                Assert.Equal(DescentHandover.None, verdict);
            }

            Assert.True(second < 400, "the descent never reached the ground");
        }

        // ~148 s of honest hands-off ride, and every second of it left alone.
        Assert.InRange(airY / DescentSettings.MeasuredHandsOffMetresPerSecond, 140f, 155f);
    }

    /// <summary>
    /// The same replay against the shipped 850 m release: an ~85 s hands-off ride, comfortably
    /// inside a ~158 s deadline, and never interrupted.
    /// </summary>
    [Fact]
    public void TheShippedRideIsNowhereNearItsOwnDeadline()
    {
        const float airY = 850f;
        const float groundY = -5f;

        Assert.Equal(85.5f, DescentSettings.HandsOffSecondsFor(airY, groundY), 1);
        Assert.Equal(158.25d, DescentDeadline.DeadlineSeconds(airY, groundY), 2);
        Assert.Equal(316.5d, DescentDeadline.StuckClientSeconds(airY, groundY), 2);

        // 300 m up at 100 s — overdue by no measure, and streaming.
        Assert.Equal(
            DescentHandover.None,
            DescentDeadline.Evaluate(true, 100_000L, 0L, airY, groundY, 300f));

        // Still 300 m up at 200 s: past the deadline, but flying. The server leaves it alone.
        Assert.Equal(
            DescentHandover.None,
            DescentDeadline.Evaluate(true, 200_000L, 0L, airY, groundY, 300f));
    }

    /// <summary>
    /// The three states in which forcing the handover is safe, and the one in which it is not.
    /// </summary>
    [Theory]
    // elapsed, silent, chute Y, expected
    [InlineData(0L, 0L, 20f, DescentHandover.None)]                 // no ride
    [InlineData(-500L, 0L, 20f, DescentHandover.None)]              // a clock that has not moved
    [InlineData(21_765L, 0L, 20f, DescentHandover.None)]            // a completed 850 m ride
    [InlineData(150_000L, 0L, 20f, DescentHandover.None)]           // inside the deadline, on the ground
    [InlineData(160_000L, 0L, 20f, DescentHandover.Landed)]         // landed, Dismiss lost
    [InlineData(160_000L, 0L, 34f, DescentHandover.Landed)]         // 39 m up: inside 2 x LANDING_HEIGHT
    [InlineData(160_000L, 0L, 600f, DescentHandover.None)]          // 605 m up and streaming: FLYING
    [InlineData(160_000L, 25_000L, 600f, DescentHandover.ClientSilent)]  // 605 m up and gone
    [InlineData(160_000L, 5_000L, 600f, DescentHandover.None)]      // a 5 s gap is not silence
    [InlineData(400_000L, 0L, 600f, DescentHandover.StuckClient)]   // the absolute backstop
    public void TheGuardForcesTheHandoverOnlyWhenNothingLiveCanBeInterrupted(
        long elapsedMs, long silentMs, float chuteY, DescentHandover expected)
    {
        Assert.Equal(
            expected,
            DescentDeadline.Evaluate(guard: true, elapsedMs, silentMs, airY: 850f, groundY: -5f, chuteY));
    }

    /// <summary>
    /// A chute whose pose stream has never carried a position cannot satisfy the near-ground gate,
    /// so the ride clock itself measures the silence — which is the honest reading of "no records at
    /// all" and the only path left for a client that vanished before its first managed record.
    /// </summary>
    [Fact]
    public void AChuteThatNeverReportedAPositionFallsThroughToTheSilenceGate()
    {
        Assert.Equal(
            DescentHandover.None,
            DescentDeadline.Evaluate(true, 100_000L, -1L, 850f, -5f, null));

        Assert.Equal(
            DescentHandover.ClientSilent,
            DescentDeadline.Evaluate(true, 160_000L, -1L, 850f, -5f, null));

        Assert.False(DescentDeadline.IsNearGround(float.NaN, -5f));
        Assert.False(DescentDeadline.IsNearGround(20f, float.NaN));
        Assert.True(DescentDeadline.IsNearGround(34.9f, -5f));
        Assert.False(DescentDeadline.IsNearGround(35.1f, -5f));
    }

    /// <summary>
    /// The revert, and the thing it reverts to. <c>CRANBERRY_DESCENT_LANDING_GUARD=0</c> restores the
    /// pre-D239 deadline exactly: no altitude gate, computed at the dived mean, expiring at 87.0 s on
    /// a ride that lasts 148 s. It is kept so the owner can bisect, and it is labelled as the defect.
    /// </summary>
    [Fact]
    public void TurningTheGuardOffRestoresThePreD239DeadlineExactly()
    {
        const float groundY = -5f;
        const float airY = 1449.4f;

        Assert.Equal(
            DescentHandover.None,
            DescentDeadline.Evaluate(false, 86_000L, 0L, airY, groundY, 600f));

        // 87.0 s, 600 m up, client streaming — the exact regression.
        Assert.Equal(
            DescentHandover.LegacyDeadline,
            DescentDeadline.Evaluate(false, 87_100L, 0L, airY, groundY, 600f));

        // And with the guard on, the very same inputs are left alone.
        Assert.Equal(
            DescentHandover.None,
            DescentDeadline.Evaluate(true, 87_100L, 0L, airY, groundY, 600f));

        Assert.Equal(2d, DescentDeadline.ExpectedRideMultiple);
        Assert.Equal(15d, DescentDeadline.SlackSeconds);
        Assert.Throws<ArgumentNullException>(() => DescentDeadline.LegacyDeadlineSeconds(null!, 850f, 0f));
    }

    /// <summary>
    /// The deadline is built out of the client's own numbers: the fall over
    /// <c>MIN_TERM_VELOCITY 10</c>, and a proximity gate of twice <c>Vehicles.txt</c> row 13's
    /// <c>LANDING_HEIGHT 20</c>.
    /// </summary>
    [Fact]
    public void TheDeadlineIsBuiltFromTheClientsOwnNumbers()
    {
        Assert.Equal(10f, DescentTuning.MinimumRate);
        Assert.Equal(56f, DescentTuning.MaximumRate);
        Assert.Equal(40f, DescentDeadline.GroundProximityMetres);
        Assert.Equal(1.5d, DescentDeadline.HandsOffRideMultiple);
        Assert.Equal(30d, DescentDeadline.HandsOffSlackSeconds);
        Assert.Equal(20d, DescentDeadline.StreamSilenceSeconds);
        Assert.Equal(2d, DescentDeadline.StuckClientMultiple);

        // The hands-off worst case is the client's, not the planning knob's: a tuned rate must not
        // be able to move it, which is the exact class of bug D239 fixes.
        Assert.Equal(
            (850f + 5f) / DescentTuning.MinimumRate,
            DescentSettings.HandsOffSecondsFor(850f, -5f),
            3);
        Assert.Equal(9.8f, DescentSettings.MeasuredHandsOffMetresPerSecond);
        Assert.Equal(45.8f, DescentSettings.MeasuredFlownMetresPerSecond);
        Assert.InRange(
            DescentSettings.MeasuredHandsOffMetresPerSecond,
            DescentTuning.MinimumRate - 0.25f,
            DescentTuning.MinimumRate);
    }

    // ============================================================ D240 — the jitter floor

    /// <summary>
    /// <b>The two drops of 2026-09-03 17:50 and 17:59, as arithmetic.</b> Drop 2 chose
    /// PalaminoTrailsCampground, whose anchor has <b>472</b> markers within 80 m (docs/48 §4.5
    /// row 57), and then accepted a jittered marker with <b>74</b> — a 6.4x density loss bought
    /// purely by an absolute floor of 64. Drop 1 chose FZMAStationBravo (anchor <b>718</b>) and drew
    /// a marker with <b>622</b>, which was always fine. The relative floor rejects the first and
    /// keeps the second.
    /// </summary>
    [Theory]
    [InlineData(472, 236)]   // PalaminoTrailsCampground — 74 is now refused
    [InlineData(718, 359)]   // FZMAStationBravo — 622 still passes
    [InlineData(122, 64)]    // JayWildernessCamp, the poorest anchor in the game: unchanged
    [InlineData(0, 64)]      // a degenerate place still gets the absolute floor
    public void TheJitterFloorIsRelativeToThePlacesOwnAnchor(int anchorNeighbours, int expected)
    {
        Assert.Equal(expected, DropOptions.Default.JitterFloorFor(anchorNeighbours));
    }

    /// <summary>The measured numbers, stated as the accept/reject decisions they are.</summary>
    [Fact]
    public void TheTwoMeasuredDropsAreDecidedTheWayTheAuditAsks()
    {
        DropOptions options = DropOptions.Default;

        Assert.Equal(0.5f, options.JitterFloorFraction);
        Assert.Equal(64, options.MinimumMarkers);

        // Drop 2, 17:59:06 — PalaminoTrailsCampground.
        Assert.True(74 >= options.MinimumMarkers, "74 cleared the old absolute floor, which was the defect");
        Assert.True(74 < options.JitterFloorFor(472), "74 must now be refused for a 472-marker anchor");

        // Drop 1, 17:50:43 — FZMAStationBravo.
        Assert.True(622 >= options.JitterFloorFor(718), "622 must still be accepted");
    }

    /// <summary>
    /// The revert restores the pre-D240 behaviour exactly, and the knob is range checked rather than
    /// trusted: <c>CRANBERRY_DROP_JITTER_FLOOR=0</c>.
    /// </summary>
    [Fact]
    public void TheJitterFloorIsSwitchableAndRangeChecked()
    {
        DropOptions off = DropTuning.FromEnvironment(
            key => key == DropTuning.JitterFloorVariable ? "0" : null, out string? note);

        Assert.NotNull(note);
        Assert.Equal(0f, off.JitterFloorFraction);
        Assert.Equal(64, off.JitterFloorFor(472));

        DropOptions defaults = DropTuning.FromEnvironment(_ => null, out string? quiet);
        Assert.Null(quiet);
        Assert.Same(DropOptions.Default, defaults);

        DropOptions refused = DropTuning.FromEnvironment(
            key => key == DropTuning.JitterFloorVariable ? "9" : null, out string? complaint);
        Assert.NotNull(complaint);
        Assert.Contains("IGNORED", complaint, StringComparison.Ordinal);
        Assert.Same(DropOptions.Default, refused);

        DropOptions nonsense = DropTuning.FromEnvironment(
            key => key == DropTuning.JitterFloorVariable ? "banana" : null, out string? typo);
        Assert.NotNull(typo);
        Assert.Contains("not a number", typo, StringComparison.Ordinal);
        Assert.Same(DropOptions.Default, nonsense);
    }

    /// <summary>
    /// Through the planner: with the floor on, no drawn marker is ever poorer than half its own
    /// place's anchor — and the anchor fallback, which is what a fussy place falls back to, always
    /// satisfies its own floor.
    /// </summary>
    [Fact]
    public void NoPlannedDropEverLandsOnAMarkerPoorerThanHalfItsAnchor()
    {
        DropOptions options = DropOptions.Default;

        for (ulong seed = 1; seed <= 200; seed++)
        {
            DropPlan plan = Assert.IsType<DropPlan>(DropPlanner.Plan(DropFixture.Places, null, options, seed));
            int floor = options.JitterFloorFor(plan.Poi.AnchorNeighbours);

            if (plan.UsedAnchor)
            {
                Assert.Equal(plan.Poi.AnchorNeighbours, plan.MarkersWithinLootRadius);
                Assert.True(
                    plan.Poi.AnchorNeighbours >= floor || plan.Poi.AnchorNeighbours < options.MinimumMarkers,
                    $"seed {seed}: {plan.Poi.Area}'s anchor cannot fail its own floor");
            }
            else
            {
                Assert.True(
                    plan.MarkersWithinLootRadius >= floor,
                    $"seed {seed}: {plan.Poi.Area} accepted {plan.MarkersWithinLootRadius} against a floor of {floor}");
            }
        }
    }

    // ============================================================ D241 — the parachute skins

    /// <summary>
    /// The client's own table, transcribed and cross-checked against the item catalogue rather than
    /// against itself: <c>VehicleSkinMods</c> rows 11/12/13 point vehicle 13, mod point 1, at items
    /// 4055/4056/4057, and <c>ClientItemDefinitions.PARAM1</c> gives them shader groups 484, 492 and
    /// 491. The pairing is deliberately non-monotonic — Blue 4056 is 492 and Tan 4057 is 491 — which
    /// is exactly the sort of thing a test has to hold still.
    /// </summary>
    [Theory]
    [InlineData(ParachuteSkin.GreenItemId, 484u)]
    [InlineData(ParachuteSkin.BlueItemId, 492u)]
    [InlineData(ParachuteSkin.TanItemId, 491u)]
    public void EachParachuteSkinResolvesToTheClientsOwnShaderGroup(uint itemId, uint shaderGroup)
    {
        Assert.True(ParachuteSkin.IsParachuteSkin(itemId));
        Assert.Equal(shaderGroup, ParachuteSkin.ShaderParameterGroupFor(itemId));

        Assert.True(InventoryItemFacts.TryGet(itemId, out InventoryItemFact fact));
        Assert.Equal(ItemCodeFactory.VehicleSkinShaderParameterGroupId, fact.CodeFactory);
        Assert.Equal(shaderGroup, fact.Param1);
    }

    /// <summary>Nothing outside the client's three rows is a parachute skin, and 0 is the default canopy.</summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(4054u)]
    [InlineData(4058u)]
    [InlineData(2423u)]   // the field bandage
    public void NothingElseIsAParachuteSkin(uint itemId)
    {
        Assert.False(ParachuteSkin.IsParachuteSkin(itemId));
        Assert.Equal(0u, ParachuteSkin.ShaderParameterGroupFor(itemId));
    }

    /// <summary>The switch takes a colour or an id, and a typo can never stop a host.</summary>
    [Theory]
    [InlineData(null, 0u)]
    [InlineData("", 0u)]
    [InlineData("   ", 0u)]
    [InlineData("0", 0u)]
    [InlineData("off", 0u)]
    [InlineData("banana", 0u)]
    [InlineData("-1", 0u)]
    [InlineData("4054", 0u)]
    [InlineData("green", ParachuteSkin.GreenItemId)]
    [InlineData("GREEN", ParachuteSkin.GreenItemId)]
    [InlineData("Blue", ParachuteSkin.BlueItemId)]
    [InlineData(" tan ", ParachuteSkin.TanItemId)]
    [InlineData("4055", ParachuteSkin.GreenItemId)]
    [InlineData("4057", ParachuteSkin.TanItemId)]
    public void TheParachuteSkinSwitchNeverThrowsAndDefaultsToTheDefaultCanopy(string? text, uint expected)
    {
        Assert.Equal(expected, ParachuteSkin.Parse(text));
        Assert.Equal(expected, DropTuning.ParachuteSkinFromEnvironment(
            key => key == DropTuning.ParachuteSkinVariable ? text : null));
    }

    /// <summary>
    /// <b>The shipped drop's bytes do not move.</b> With no skin selected the chute record is
    /// byte-identical to the 227-byte form <c>VehiclePacketTests</c> has pinned since wave 1 — which
    /// is the whole reason the carrier can be an experiment at all, because the field it uses
    /// (<c>+0x1c8</c>) is one docs/12 §6 still lists as open.
    /// </summary>
    [Fact]
    public void TheDefaultCanopyIsByteIdenticalAndASelectedSkinMovesExactlyFourBytes()
    {
        var position = new Vector3(863f, 850f, -2327.5f);
        var rotation = new Vector4(0, 0, 0, 1);

        byte[] plain = Bytes(w => new AddLightweightVehicle(
            0x2001, 2, 9374, position, rotation, 13, OwnerGuid: 0x1001).WriteTo(w));
        byte[] green = Bytes(w => new AddLightweightVehicle(
            0x2001, 2, 9374, position, rotation, 13, OwnerGuid: 0x1001,
            ShaderParameterGroupId: ParachuteSkin.ShaderParameterGroupFor(ParachuteSkin.GreenItemId)).WriteTo(w));

        Assert.Equal(AddLightweightVehicle.MinimalLength, plain.Length);
        Assert.Equal(plain.Length, green.Length);

        // Exactly one dword differs, and it carries 484 little-endian.
        var moved = new List<int>();
        for (int index = 0; index < plain.Length; index++)
        {
            if (plain[index] != green[index])
            {
                moved.Add(index);
            }
        }

        Assert.Equal(2, moved.Count);
        Assert.Equal(moved[0] + 1, moved[1]);
        Assert.Equal(484u, BitConverter.ToUInt32(green, moved[0]));
        Assert.Equal(0u, BitConverter.ToUInt32(plain, moved[0]));
    }

    /// <summary>
    /// The client's own keying, pinned so a later lane cannot re-derive it: vehicle 13, mod point 1,
    /// and the wardrobe static view the client actually opened in the 2026-09-03 17:55 session.
    /// </summary>
    [Fact]
    public void TheSkinsAreKeyedToTheParachuteVehicleAndItsWardrobeView()
    {
        Assert.Equal(13u, ParachuteSkin.VehicleId);
        Assert.Equal(13u, new ZoneOptions().ParachuteVehicleId);
        Assert.Equal(1u, ParachuteSkin.ModPoint);
        Assert.Equal("kotkappearancevehiclesparachute", ParachuteSkin.StaticViewLocation);
        Assert.Equal([4055u, 4056u, 4057u], ParachuteSkin.ItemIds);

        // Shipped: no skin, and therefore no new byte on the proven drop path.
        Assert.Equal(0u, new ZoneOptions().ParachuteShaderParameterGroupId);
        Assert.True(new ZoneOptions().DescentLandingGuard);
    }
}
