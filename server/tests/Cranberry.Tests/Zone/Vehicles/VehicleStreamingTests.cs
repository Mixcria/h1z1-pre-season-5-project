using System.Numerics;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Vehicles;

/// <summary>
/// docs/61 §2 — the car park was streamed exactly once per match.
///
/// <para><c>logs/host-20260830-090725.log</c> 09:10:03 reads <i>"vehicles: planned 7 of 300 within
/// 250 m"</i> and there is no second such line for the rest of the session, because
/// <c>ZoneService.SpawnNearbyVehicles</c> hangs off the one-way <c>state.VehiclesArmed</c> latch.
/// Wave 4 made exactly this mistake with ground loot and docs/52 fixed it; these tests pin the same
/// fix on the vehicle arm, including the two rules that are <b>not</b> the loot arm: an occupied car
/// is never evicted, and a car that has been driven is judged on where it is now.</para>
/// </summary>
public sealed class VehicleStreamingTests
{
    private static readonly Lazy<VehicleRoster> SharedRoster = new(VehicleRoster.LoadDefault);

    private static readonly VehicleStreamOptions SmallBudget = new()
    {
        StreamRadiusMetres = 250, DespawnRadiusMetres = 350,
        MaxLive = 24, MaxPerRestream = 6, RestreamFraction = 0.25f, RestreamIntervalMs = 3000,
    };

    private const ulong Viewer = 9001;
    private const ulong Other = 9002;

    [Fact]
    public void ChangingTheRadiusRechecksAStationaryExhaustedDisc()
    {
        var stream = new MatchVehicleStream();
        var fleet = FleetAt(new Vector3(500, 0, 0));
        stream.PlanRestream(Vector3.Zero, fleet, SmallBudget);
        Assert.True(stream.DiscExhausted);
        var wider = SmallBudget with { StreamRadiusMetres = 750 };
        Assert.True(stream.ShouldRestream(Vector3.Zero, wider));
        Assert.Single(stream.PlanRestream(Vector3.Zero, fleet, wider).Spawns);
    }

    [Fact]
    public void EveryMapDiscFitsTheDefaultVehicleBudgetIncludingBetweenGridSamples()
    {
        var options = VehicleStreamOptions.Default;
        var anchors = VehicleAnchorSet.LoadDefault().Anchors.ToArray();
        // Every point is within sqrt(2)*64 m of this grid. Inflate the query by
        // that distance, so this bounds all possible centres, not just the samples.
        float radius = options.EffectiveDespawnRadiusMetres + MathF.Sqrt(2) * 64;
        for (int x = -4096; x <= 4096; x += 128)
        for (int z = -4096; z <= 4096; z += 128)
        {
            int nearby = anchors.Count(a => HorizontalSquared(a.Position, new(x, 0, z)) <= radius * radius);
            Assert.True(nearby <= options.MaxLive, $"{nearby} map cars exceed the budget at ({x}, {z})");
        }
    }

    [Fact]
    public void DrivingAcrossZ2KeepsAllCarsInsideTheNativeDrawRangeAlreadyLoaded()
    {
        var defaults = new Cranberry.Zone.ZoneOptions();
        var options = defaults.VehicleStream;
        var positions = VehicleAnchorSet.LoadDefault().Anchors.ToArray().Select(a => a.Position).ToArray();
        var fleet = FleetAt(positions);
        var stream = new MatchVehicleStream();
        Vector3[] stops = [new(-3600, 30, 2800), new(-112, 33, 274), new(1661, 16, 1849),
                           new(3000, 89, -500), new(-3000, 30, -2500)];
        // The initial burst sends the whole local set, paced by the gateway.
        foreach (var car in fleet.Vehicles.Where(v => HorizontalSquared(v.Position, stops[0])
                 <= options.StreamRadiusMetres * options.StreamRadiusMetres))
            stream.NoteSpawned(car.Guid, car.Position);
        stream.NoteStreamed(stops[0]);
        for (int leg = 1; leg < stops.Length; leg++)
        {
            Vector3 from = stops[leg - 1], to = stops[leg];
            int seconds = (int)Math.Ceiling(Vector3.Distance(from, to) / 30); // ~108 km/h.
            for (int second = 0; second <= seconds; second++)
            {
                Vector3 centre = Vector3.Lerp(from, to, (float)second / seconds);
                if (stream.ShouldRestream(centre, options)) stream.PlanRestream(centre, fleet, options);
                foreach (var car in fleet.Vehicles)
                    if (HorizontalSquared(car.Position, centre) <= defaults.VehicleRenderDistance * defaults.VehicleRenderDistance)
                        Assert.True(stream.IsStreamed(car.Guid), $"Visible car {car.Guid} missing at {centre}");
                Assert.InRange(stream.LiveCount, 0, options.MaxLive);
            }
        }
    }

    private static float HorizontalSquared(Vector3 left, Vector3 right) =>
        (left.X - right.X) * (left.X - right.X) + (left.Z - right.Z) * (left.Z - right.Z);

    private static VehicleFleet FleetAt(params Vector3[] positions)
    {
        VehicleRoster roster = SharedRoster.Value;
        var fleet = new VehicleFleet(roster);
        for (int index = 0; index < positions.Length; index++)
        {
            fleet.Add(new MatchVehicle(
                guid: 0x5000UL + (ulong)index,
                transientId: 3_000_000u + (uint)index,
                definition: roster.Require(1),
                position: positions[index],
                yaw: 0f,
                health: fleet.Options.MaxHealth,
                fuel: fleet.Options.MaxFuel));
        }

        return fleet;
    }

    private static Vector3[] Line(int count, float spacingMetres)
    {
        var points = new Vector3[count];
        for (int index = 0; index < count; index++)
        {
            points[index] = new Vector3(index * spacingMetres, 30f, 0f);
        }

        return points;
    }

    // --- the latch this replaces ---------------------------------------------------------------

    /// <summary>
    /// The whole feature in one test: after the landing burst has been adopted, driving forward must
    /// keep producing new cars. Under wave 5 behaviour the second and third plans here are empty.
    /// </summary>
    [Fact]
    public void DrivingForwardKeepsProducingNewCarsInsteadOfOneBurstForTheWholeMatch()
    {
        VehicleFleet fleet = FleetAt(Line(40, 40f));      // 40 cars strung out over 1,560 m
        var stream = new MatchVehicleStream();
        VehicleStreamOptions options = SmallBudget;

        VehicleStreamPlan first = stream.PlanRestream(new Vector3(0, 30, 0), fleet, options, Viewer);
        Assert.Equal(options.MaxPerRestream, first.Spawns.Count);

        int totalSpawned = first.Spawns.Count;
        for (int step = 1; step <= 10; step++)
        {
            VehicleStreamPlan plan = stream.PlanRestream(
                new Vector3(step * 100f, 30, 0), fleet, options, Viewer);
            totalSpawned += plan.Spawns.Count;
        }

        // Eleven ticks of a 6-car budget is 66 slots; the disc only ever holds a few more than
        // MaxLive, so what matters is that it is far more than one burst and that the working set
        // stayed bounded the whole way.
        Assert.True(totalSpawned > options.MaxPerRestream * 3, $"only {totalSpawned} cars ever streamed");
        Assert.True(stream.LiveCount <= options.MaxLive, $"working set grew to {stream.LiveCount}");
    }

    [Fact]
    public void TheWorkingSetNeverExceedsMaxLiveEvenWhenTheDiscIsFullOfCars()
    {
        // 60 cars inside one 250 m disc — denser than any real Z2 anchor cluster.
        var positions = new Vector3[60];
        for (int index = 0; index < positions.Length; index++)
        {
            positions[index] = new Vector3(index * 3f, 30f, 0f);
        }

        VehicleFleet fleet = FleetAt(positions);
        var stream = new MatchVehicleStream();
        VehicleStreamOptions options = SmallBudget;

        for (int tick = 0; tick < 20; tick++)
        {
            VehicleStreamPlan plan = stream.PlanRestream(new Vector3(90, 30, 0), fleet, options, Viewer);
            Assert.True(plan.Spawns.Count <= options.MaxPerRestream);
            Assert.True(stream.LiveCount <= options.MaxLive);
        }

        Assert.Equal(options.MaxLive, stream.LiveCount);
    }

    // --- the rule that is not the loot arm ------------------------------------------------------

    [Fact]
    public void AnUnoccupiedCarCanStreamOutEvenWhenTheViewerStillSimulatesIt()
    {
        var fleet = FleetAt(new Vector3(0, 30, 0));
        var stream = new MatchVehicleStream();
        stream.PlanRestream(new Vector3(0, 30, 0), fleet, SmallBudget, Viewer);
        fleet.TryEnter(0x5000UL, Viewer, 0, 1000, out var car, out _);
        fleet.TryExit(Viewer, 3000, 0, out _, out _);
        Assert.Equal(Viewer, car!.CoastingOwnerGuid);
        var plan = stream.PlanRestream(new Vector3(9000, 30, 0), fleet, SmallBudget, Viewer);
        Assert.Equal(new[] { car.Guid }, plan.Evictions);
        Assert.Equal(0, plan.ProtectedFromEviction);
    }

    /// <summary>
    /// <b>The regression that would end a drive.</b> Destroying the vehicle actor a player is
    /// sitting in unparents their character and orphans the managed object the client is simulating,
    /// so the eviction pass must skip an occupied car at any distance — including the car the viewer
    /// is themselves driving, whose distance from the viewer <i>character</i> pose is exactly what
    /// grows while they drive.
    /// </summary>
    [Fact]
    public void AnOccupiedCarIsNeverEvictedHoweverFarAwayItGets()
    {
        VehicleFleet fleet = FleetAt(new Vector3(0, 30, 0), new Vector3(20, 30, 0));
        var stream = new MatchVehicleStream();
        VehicleStreamOptions options = SmallBudget;

        stream.PlanRestream(new Vector3(0, 30, 0), fleet, options, Viewer);
        Assert.Equal(2, stream.LiveCount);

        Assert.Equal(
            VehicleActionResult.Ok,
            fleet.TryEnter(0x5000UL, Viewer, 0, 1_000, out MatchVehicle? driven, out _));
        Assert.NotNull(driven);

        // Both cars are now 5 km away from the streaming centre. The empty one goes; the driven one
        // stays, and is counted as protected rather than silently skipped.
        VehicleStreamPlan plan = stream.PlanRestream(new Vector3(5_000, 30, 0), fleet, options, Viewer);
        Assert.Equal(new ulong[] { 0x5001UL }, plan.Evictions);
        Assert.Equal(1, plan.ProtectedFromEviction);
        Assert.True(stream.IsStreamed(0x5000UL));
    }

    /// <summary>A car somebody else is sitting in is protected too — not just the viewer own.</summary>
    [Fact]
    public void ACarWithAnyOccupantIsProtectedNotJustTheViewerOwnRide()
    {
        VehicleFleet fleet = FleetAt(new Vector3(0, 30, 0));
        var stream = new MatchVehicleStream();

        stream.PlanRestream(new Vector3(0, 30, 0), fleet, SmallBudget, Viewer);
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(0x5000UL, Other, 0, 1_000, out _, out _));

        VehicleStreamPlan plan = stream.PlanRestream(
            new Vector3(9_000, 30, 0), fleet, SmallBudget, Viewer);
        Assert.Empty(plan.Evictions);
        Assert.Equal(1, plan.ProtectedFromEviction);
    }

    /// <summary>
    /// A car is judged on where it <i>is</i>, not on the parking anchor it spawned at — otherwise a
    /// car someone drove past you would be evicted from your client while it sat in front of you,
    /// and one driven away would linger forever.
    /// </summary>
    [Fact]
    public void ADrivenCarIsJudgedOnItsCurrentPoseNotItsSpawnAnchor()
    {
        VehicleFleet fleet = FleetAt(new Vector3(0, 30, 0));
        var stream = new MatchVehicleStream();
        VehicleStreamOptions options = SmallBudget;

        stream.PlanRestream(new Vector3(0, 30, 0), fleet, options, Viewer);
        Assert.True(stream.IsStreamed(0x5000UL));

        // Somebody drives it 2 km away and gets out; the viewer never moves.
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(0x5000UL, Other, 0, 1_000, out _, out _));
        Assert.True(fleet.TryApplyOwnerPose(
            3_000_000u, Other, new Vector3(0, 30, 60), 0f, 1_100, out _));
        Assert.True(fleet.TryApplyOwnerPose(
            3_000_000u, Other, new Vector3(0, 30, 2_000), 0f, 1_000_000, out _));
        Assert.Equal(VehicleActionResult.Ok, fleet.TryExit(Other, 2_000_000, 0f, out _, out _));

        VehicleStreamPlan plan = stream.PlanRestream(new Vector3(0, 30, 0), fleet, options, Viewer);
        Assert.Equal(new ulong[] { 0x5000UL }, plan.Evictions);
        Assert.False(stream.IsStreamed(0x5000UL));
    }

    [Fact]
    public void ACarThatLeftTheFleetEntirelyIsEvicted()
    {
        VehicleFleet fleet = FleetAt(new Vector3(0, 30, 0));
        var stream = new MatchVehicleStream();

        stream.PlanRestream(new Vector3(0, 30, 0), fleet, SmallBudget, Viewer);
        fleet.Clear();

        VehicleStreamPlan plan = stream.PlanRestream(
            new Vector3(0, 30, 0), fleet, SmallBudget, Viewer);
        Assert.Equal(new ulong[] { 0x5000UL }, plan.Evictions);
        Assert.Equal(0, stream.LiveCount);
    }

    // --- hysteresis and adoption ----------------------------------------------------------------

    /// <summary>
    /// Without the despawn/stream gap a car sitting on the disc edge is spawned and destroyed on
    /// alternate ticks forever — 515 B of churn per car per tick for a player who has not moved.
    /// </summary>
    [Fact]
    public void ACarOnTheDiscEdgeDoesNotFlickerBecauseTheDespawnRadiusIsLarger()
    {
        VehicleFleet fleet = FleetAt(new Vector3(249f, 30f, 0f));
        var stream = new MatchVehicleStream();
        VehicleStreamOptions options = SmallBudget;

        stream.PlanRestream(Vector3.Zero, fleet, options, Viewer);
        Assert.True(stream.IsStreamed(0x5000UL));

        for (int tick = 0; tick < 5; tick++)
        {
            VehicleStreamPlan plan = stream.PlanRestream(Vector3.Zero, fleet, options, Viewer);
            Assert.Empty(plan.Evictions);
            Assert.Empty(plan.Spawns);
        }
    }

    /// <summary>
    /// A despawn radius set inside the stream radius inverts the hysteresis and makes every tick
    /// spawn a car and immediately evict it. The effective radius clamps it away.
    /// </summary>
    [Fact]
    public void ADespawnRadiusInsideTheStreamRadiusIsClampedRatherThanObeyed()
    {
        var options = new VehicleStreamOptions { StreamRadiusMetres = 250f, DespawnRadiusMetres = 10f };
        Assert.Equal(250f, options.EffectiveDespawnRadiusMetres);

        VehicleFleet fleet = FleetAt(new Vector3(200f, 30f, 0f));
        var stream = new MatchVehicleStream();
        stream.PlanRestream(Vector3.Zero, fleet, options, Viewer);
        VehicleStreamPlan second = stream.PlanRestream(Vector3.Zero, fleet, options, Viewer);
        Assert.Empty(second.Evictions);
    }

    /// <summary>
    /// The landing burst does not go through <see cref="MatchVehicleStream.PlanRestream"/>. If it is
    /// not adopted, the first re-stream tick re-offers every car it just sent and the client keeps
    /// two actors per guid forever — the vehicle twin of docs/52 §I3.
    /// </summary>
    [Fact]
    public void AdoptingTheLandingBurstStopsTheFirstRestreamTickFromDuplicatingIt()
    {
        VehicleFleet fleet = FleetAt(Line(5, 10f));
        var stream = new MatchVehicleStream();

        foreach (MatchVehicle parked in fleet.Vehicles)
        {
            Assert.True(stream.NoteSpawned(parked.Guid, parked.Position));
        }

        Assert.Equal(5, stream.LiveCount);
        VehicleStreamPlan plan = stream.PlanRestream(Vector3.Zero, fleet, SmallBudget, Viewer);
        Assert.Empty(plan.Spawns);
        Assert.Equal(5, plan.InRadius);
    }

    [Fact]
    public void NoteSpawnedRefusesASecondCopyOfTheSameGuid()
    {
        var stream = new MatchVehicleStream();
        Assert.True(stream.NoteSpawned(7, Vector3.Zero));
        Assert.False(stream.NoteSpawned(7, Vector3.One));
        Assert.Equal(1, stream.LiveCount);
        Assert.Equal(1, stream.StreamedCount);
    }

    // --- the pump decision ----------------------------------------------------------------------

    /// <summary>
    /// Every one of these is a <see cref="WorldStreamStep.Wait"/> and not a
    /// <see cref="WorldStreamStep.Stop"/>: <c>Stop</c> ends the timer chain for the rest of the
    /// match, and the wave-4 verify pass found exactly this defect in the door pump.
    /// </summary>
    [Fact]
    public void NoStateAndNoPoseAreWaitsAndOnlyTheTerminalConditionsAreStops()
    {
        VehicleStreamOptions options = SmallBudget;
        var stream = new MatchVehicleStream();
        Vector3 somewhere = new(1, 2, 3);

        Assert.Equal(
            WorldStreamStep.Wait,
            MatchVehicleStream.NextPumpStep(true, true, null, somewhere, options, fleetPlanned: true));
        Assert.Equal(
            WorldStreamStep.Wait,
            MatchVehicleStream.NextPumpStep(true, true, stream, somewhere, options, fleetPlanned: false));
        Assert.Equal(
            WorldStreamStep.Wait,
            MatchVehicleStream.NextPumpStep(true, true, stream, null, options, fleetPlanned: true));

        Assert.Equal(
            WorldStreamStep.Stop,
            MatchVehicleStream.NextPumpStep(false, true, stream, somewhere, options, true));
        Assert.Equal(
            WorldStreamStep.Stop,
            MatchVehicleStream.NextPumpStep(true, false, stream, somewhere, options, true));
        Assert.Equal(
            WorldStreamStep.Stop,
            MatchVehicleStream.NextPumpStep(
                true, true, stream, somewhere, new VehicleStreamOptions { Enabled = false }, true));
        Assert.Equal(
            WorldStreamStep.Stop,
            MatchVehicleStream.NextPumpStep(
                true, true, stream, somewhere, new VehicleStreamOptions { RestreamIntervalMs = 0 }, true));

        Assert.Equal(
            WorldStreamStep.Restream,
            MatchVehicleStream.NextPumpStep(true, true, stream, somewhere, options, true));
    }

    [Fact]
    public void TheMovementArmFiresOnlyAfterAQuarterRadiusAndTheBackfillArmRepairsATruncatedBurst()
    {
        VehicleStreamOptions options = SmallBudget;
        VehicleFleet fleet = FleetAt(Line(40, 5f));
        var stream = new MatchVehicleStream();

        Assert.True(stream.ShouldRestream(Vector3.Zero, options));    // nothing streamed yet
        stream.PlanRestream(Vector3.Zero, fleet, options, Viewer);

        // The disc is far from exhausted (6 of 40 taken), so the backfill arm keeps it true.
        Assert.False(stream.DiscExhausted);
        Assert.True(stream.ShouldRestream(Vector3.Zero, options));

        // Fill it to MaxLive, then the backfill arm stands down and only movement wakes it.
        for (int tick = 0; tick < 10; tick++)
        {
            stream.PlanRestream(Vector3.Zero, fleet, options, Viewer);
        }

        Assert.Equal(options.MaxLive, stream.LiveCount);
        Assert.False(stream.ShouldRestream(new Vector3(10, 0, 0), options));
        Assert.True(stream.ShouldRestream(new Vector3(100, 0, 0), options));   // > 62.5 m
    }

    /// <summary>Height must not trip the movement arm; a car drives over hills.</summary>
    [Fact]
    public void TheMovementArmIgnoresHeight()
    {
        VehicleStreamOptions options = SmallBudget;
        VehicleFleet fleet = FleetAt(new Vector3(0, 30, 0));
        var stream = new MatchVehicleStream();
        stream.PlanRestream(Vector3.Zero, fleet, options, Viewer);

        Assert.True(stream.DiscExhausted);
        Assert.False(stream.ShouldRestream(new Vector3(0, 800, 0), options));
    }

    [Fact]
    public void ANonPositiveRestreamFractionIsTheExplicitStreamOncePerMatchSetting()
    {
        var options = new VehicleStreamOptions { RestreamFraction = 0f };
        VehicleFleet fleet = FleetAt(new Vector3(0, 30, 0));
        var stream = new MatchVehicleStream();
        stream.PlanRestream(Vector3.Zero, fleet, options, Viewer);

        Assert.False(stream.ShouldRestream(new Vector3(100_000, 0, 0), options));
    }

    // --- the byte budget ------------------------------------------------------------------------

    /// <summary>
    /// The streaming discipline wave 5 established: 300 cars must not bury a 512-byte-MTU channel.
    /// One spawn is <c>d7</c> 229 B + <c>db</c> 274 B, so the arm own ceiling is
    /// <c>MaxPerRestream</c> × 503 per <c>RestreamIntervalMs</c>.
    /// </summary>
    [Fact]
    public void OneRestreamTickCostsAboutOneKilobytePerSecondAndNeverMoreThanTheLootArm()
    {
        VehicleStreamOptions options = SmallBudget;
        int worstTickBytes = VehicleStreamOptions.EstimateTickBytes(
            options.MaxPerRestream, options.MaxLive);

        // The spawn half is the part that repeats every tick; the eviction half can only be as large
        // as the working set and only once.
        int steadyTickBytes = VehicleStreamOptions.EstimateTickBytes(options.MaxPerRestream, 0);
        Assert.Equal(3_018, steadyTickBytes);

        double bytesPerSecond = steadyTickBytes * 1000.0 / options.RestreamIntervalMs;
        Assert.True(bytesPerSecond < 1_100, $"{bytesPerSecond:F0} B/s");

        // Even the pathological tick — a full working set evicted while a full budget spawns — stays
        // under 4 KB, which is a quarter of the ground-loot arm's own 32 x 477 = 15,264 B tick.
        Assert.True(worstTickBytes < 4_000, $"{worstTickBytes} B");
        Assert.True(worstTickBytes < 32 * 477);
    }

    [Fact]
    public void APlanReportsTheBytesItWillCost()
    {
        VehicleFleet fleet = FleetAt(Line(3, 10f));
        var stream = new MatchVehicleStream();
        VehicleStreamPlan plan = stream.PlanRestream(Vector3.Zero, fleet, SmallBudget, Viewer);

        Assert.Equal(3, plan.Spawns.Count);
        Assert.Equal(3 * VehicleStreamOptions.SpawnBytes, plan.EstimatedBytes);
        Assert.True(plan.ChangedTheWorld);
        Assert.True(plan.DiscExhausted);
    }

    [Fact]
    public void SpawnsComeOutNearestFirst()
    {
        VehicleFleet fleet = FleetAt(
            new Vector3(200, 30, 0), new Vector3(10, 30, 0), new Vector3(90, 30, 0));
        var stream = new MatchVehicleStream();
        VehicleStreamPlan plan = stream.PlanRestream(Vector3.Zero, fleet, SmallBudget, Viewer);

        Assert.Equal(new ulong[] { 0x5001UL, 0x5002UL, 0x5000UL }, plan.Spawns.Select(entry => entry.Vehicle.Guid).ToArray());
    }

    [Fact]
    public void ClearDropsTheWorkingSetSoAMatchResetDoesNotLeakTheOldWorld()
    {
        VehicleFleet fleet = FleetAt(Line(3, 10f));
        var stream = new MatchVehicleStream();
        stream.PlanRestream(Vector3.Zero, fleet, SmallBudget, Viewer);
        Assert.Equal(3, stream.LiveCount);

        stream.Clear();
        Assert.Equal(0, stream.LiveCount);
        Assert.False(stream.HasStreamed);
        Assert.True(stream.ShouldRestream(Vector3.Zero, SmallBudget));
    }
}
