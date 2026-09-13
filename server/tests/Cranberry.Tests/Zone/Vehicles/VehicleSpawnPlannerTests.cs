using System.Numerics;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

/// <summary>
/// Where a match's cars go.
///
/// <para>Population is server policy. The whole retail marker catalogue supplies exact X/Z
/// and full orientation; the approved August geometry audit supplies parking height and
/// exclusions. These tests protect reproducibility, full occupancy and captured headings.</para>
/// </summary>
public sealed class VehicleSpawnPlannerTests
{
    [Fact]
    public void CapturedWestPeaksVehiclesRetainRetailXZAndHeadingAtFullOccupancy()
    {
        for (ulong seed = 0; seed < 32; seed++)
        {
            var plan = Plan(seed, new VehicleSpawnPlanOptions { SpawnChance = 1 });
            var jeep = Assert.Single(plan, car => car.AnchorInstanceId == 592682426);
            var atv = Assert.Single(plan, car => car.AnchorInstanceId == 4179259720);
            Assert.Equal(1u, jeep.VehicleId);
            Assert.Equal(5u, atv.VehicleId);
            // Y is explicitly grounded to August, not asserted to be retail's settled Y.
            Assert.Equal(new Vector2(-473.7146911621094f, -3780.408203125f), new(jeep.Position.X, jeep.Position.Z));
            Assert.Equal(new Vector2(-524.2061157226562f, -3721.393310546875f), new(atv.Position.X, atv.Position.Z));
            Assert.InRange(Vector4.Distance(atv.Rotation, new(0, 0.866025447845459f, 0, 0.4999999701976776f)), 0, 0.000001f);
        }
    }

    [Fact]
    public void CapturedSlopedATVKeepsItsMeasuredQuaternionThroughFleetCreation()
    {
        var planned = Assert.Single(Plan(options: new VehicleSpawnPlanOptions { SpawnChance = 1 }),
            car => car.AnchorInstanceId == 822972426);
        // Official capture frame 4874, September 12; independent of our Euler conversion.
        Vector4 captured = new(2.7221884746353453e-09f, 8.73803713830057e-08f,
            -0.031138211488723755f, 0.9995151162147522f);
        Assert.InRange(Vector4.Distance(captured, planned.Rotation), 0, 0.000001f);
        var fleet = new VehicleFleet(Roster.Value);
        var vehicle = Assert.Single(fleet.Populate([planned], i => 1000ul + (ulong)i,
            i => 2000u + (uint)i, Seed));
        Assert.NotNull(vehicle.LastRotation);
        Assert.InRange(Vector4.Distance(captured, vehicle.Rotation), 0, 0.000001f);
        Assert.Equal(planned.Position, vehicle.Position);
    }

    private static readonly Lazy<VehicleAnchorSet> Anchors = new(VehicleAnchorSet.LoadDefault);
    private static readonly Lazy<VehicleRoster> Roster = new(VehicleRoster.LoadDefault);

    private const ulong Seed = 0x0BAD_C0FFEE_1234UL;

    [Fact]
    public void ExplicitFullChanceOccupiesEveryValidatedPadWithItsAuthoredVehicle()
    {
        foreach (ulong seed in new ulong[] { 0, 1, Seed, ulong.MaxValue })
        {
            var plan = Plan(seed, new VehicleSpawnPlanOptions { SpawnChance = 1 });
            Assert.Equal(Anchors.Value.Count, plan.Count);
            Assert.Equal(plan.Count, plan.Select(car => car.AnchorInstanceId).Distinct().Count());
            foreach (var anchor in Anchors.Value.Anchors.ToArray())
            {
                var car = Assert.Single(plan, candidate => candidate.AnchorInstanceId == anchor.InstanceId);
                Assert.Equal(anchor.VehicleId, car.VehicleId);
                Assert.Equal(anchor.Position, car.Position);
                Assert.Equal(anchor.Yaw, car.Yaw);
                Assert.Equal(anchor.Pitch, car.Pitch);
                Assert.Equal(anchor.Roll, car.Roll);
            }
        }
    }

    [Fact]
    public void EveryValidatedPadGetsAnIndependentEightyPercentChance()
    {
        var counts = Anchors.Value.Anchors.ToArray().ToDictionary(a => a.InstanceId, _ => 0);
        for (ulong seed = 0; seed < 1000; seed++)
        {
            var plan = Plan(seed, new VehicleSpawnPlanOptions { SpawnChance = 0.8, LimitPoliceStationPopulation = false });
            Assert.Equal(plan.Count, plan.Select(car => car.AnchorInstanceId).Distinct().Count());
            foreach (var car in plan) counts[car.AnchorInstanceId]++;
        }
        // Validate each pad, including nearby pads in the same town; an aggregate rate could
        // conceal permanently excluded locations behind a budget, area cap or density rule.
        Assert.All(counts.Values, count => Assert.InRange(count, 750, 850));
        Assert.InRange(counts.Values.Sum() / (1000.0 * counts.Count), 0.795, 0.805);
    }

    [Fact]
    public void ChanceBoundariesIncludeAllPadsOrNoneWithoutDensityCaps()
    {
        Assert.Empty(Plan(options: new VehicleSpawnPlanOptions { SpawnChance = 0 }));
        var full = Plan(options: new VehicleSpawnPlanOptions { SpawnChance = 1, Count = 1, MaxPerArea = 1 });
        Assert.Equal(Anchors.Value.Count, full.Count);
        foreach (var anchor in Anchors.Value.Anchors.ToArray())
        {
            var car = Assert.Single(full, v => v.AnchorInstanceId == anchor.InstanceId);
            Assert.Equal(anchor.Position, car.Position);
            Assert.Equal(anchor.VehicleId, car.VehicleId);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(options: new VehicleSpawnPlanOptions { SpawnChance = double.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(options: new VehicleSpawnPlanOptions { SpawnChance = 1.01 }));
    }

    private static IReadOnlyList<PlannedVehicle> Plan(ulong seed = Seed, VehicleSpawnPlanOptions? options = null) =>
        VehicleSpawnPlanner.Plan(Anchors.Value, Roster.Value, seed, options);

    [Fact]
    public void DefaultPopulationVariesAcrossTheMapAndUsesEveryConfirmedLocation()
    {
        Assert.Equal(0.3, new VehicleSpawnPlanOptions().SpawnChance);
        var anchors = Anchors.Value.Anchors.ToArray().ToDictionary(a => a.InstanceId);
        var counts = anchors.Keys.ToDictionary(id => id, _ => 0);
        var sizes = new HashSet<int>();
        const int rounds = 4096;
        for (ulong seed = 0; seed < rounds; seed++)
        {
            var plan = Plan(seed);
            sizes.Add(plan.Count);
            Assert.Equal(plan.Count, plan.Select(car => car.AnchorInstanceId).Distinct().Count());
            foreach (var car in plan)
            {
                counts[car.AnchorInstanceId]++;
                var anchor = anchors[car.AnchorInstanceId];
                Assert.Equal(anchor.VehicleId, car.VehicleId);
                Assert.Equal(anchor.Position, car.Position);
                Assert.Equal((anchor.Yaw, anchor.Pitch, anchor.Roll), (car.Yaw, car.Pitch, car.Roll));
            }
        }

        Assert.True(sizes.Count > 20);
        Assert.All(counts.Values, count => Assert.True(count > 0));
        var ordinary = anchors.Values.Where(a => !IsStationBay(a.VehicleId, a.Position)).ToArray();
        Assert.Equal(542, ordinary.Length);
        Assert.All(ordinary, a => Assert.InRange(counts[a.InstanceId] / (double)rounds, 0.26, 0.34));
        Assert.InRange(ordinary.Sum(a => counts[a.InstanceId]) / (double)(rounds * ordinary.Length), 0.298, 0.302);
    }

    [Theory]
    [InlineData(-102.2939f, 252.25f)]
    [InlineData(1681.75f, 1859.25f)]
    public void EachPoliceStationHasOneCarSometimesTwoAndRotatesThroughAllFourBays(float x, float z)
    {
        var bays = Anchors.Value.Anchors.ToArray()
            .Where(a => a.VehicleId == 3 && Vector2.Distance(new(a.Position.X, a.Position.Z), new(x, z)) < 100)
            .Select(a => a.InstanceId).ToHashSet();
        Assert.Equal(4, bays.Count);
        var counts = bays.ToDictionary(id => id, _ => 0);
        int doubles = 0;
        const int rounds = 4096;
        for (ulong seed = 0; seed < rounds; seed++)
        {
            var cars = Plan(seed).Where(car => bays.Contains(car.AnchorInstanceId)).ToArray();
            Assert.InRange(cars.Length, 1, 2);
            if (cars.Length == 2) doubles++;
            foreach (var car in cars) counts[car.AnchorInstanceId]++;
        }

        Assert.InRange(doubles / (double)rounds, 0.32, 0.38);
        // None of the station's four confirmed bays is permanently favored or excluded.
        Assert.All(counts.Values, count => Assert.InRange(count / (double)rounds, 0.30, 0.38));
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.3)]
    [InlineData(0.8)]
    [InlineData(0.99)]
    public void StationLimitsLeaveEveryOrdinaryPadRollUnchanged(double chance)
    {
        for (ulong seed = 0; seed < 128; seed++)
        {
            var options = new VehicleSpawnPlanOptions { SpawnChance = chance };
            var grouped = Plan(seed, options);
            var independent = Plan(seed, options with { LimitPoliceStationPopulation = false });
            Assert.Equal(independent.Where(car => !IsStationBay(car.VehicleId, car.Position)),
                grouped.Where(car => !IsStationBay(car.VehicleId, car.Position)));
            foreach (var center in new Vector2[] { new(-102.2939f, 252.25f), new(1681.75f, 1859.25f) })
                Assert.InRange(grouped.Count(car => car.VehicleId == 3 &&
                    Vector2.Distance(new(car.Position.X, car.Position.Z), center) < 100), 1, 2);
        }
    }

    private static bool IsStationBay(uint vehicleId, Vector3 position) => vehicleId == 3 &&
        (Vector2.Distance(new(position.X, position.Z), new(-102.2939f, 252.25f)) < 100 ||
         Vector2.Distance(new(position.X, position.Z), new(1681.75f, 1859.25f)) < 100);

    [Fact]
    public void Z1PopulationBudgetFillsAcrossSeedsWithoutForcingEveryPadToSpawn()
    {
        Dictionary<uint, VehicleAnchor> anchors = Anchors.Value.Anchors.ToArray().ToDictionary(a => a.InstanceId);
        for (ulong seed = 0; seed < 32; seed++)
        {
            IReadOnlyList<PlannedVehicle> plan = Plan(seed, new VehicleSpawnPlanOptions { SpawnChance = null });
            Assert.Equal(150, plan.Count);
            Assert.Equal(4, plan.Select(v => v.VehicleId).Distinct().Count());
            Assert.True(plan.Count < anchors.Count);
            foreach (PlannedVehicle car in plan)
            {
                VehicleAnchor anchor = anchors[car.AnchorInstanceId];
                Assert.Equal(anchor.VehicleId, car.VehicleId);
                Assert.Equal(anchor.Position, car.Position);
                foreach (PlannedVehicle other in plan.Where(v => v.AnchorInstanceId > car.AnchorInstanceId))
                {
                    float dx = car.Position.X - other.Position.X;
                    float dz = car.Position.Z - other.Position.Z;
                    Assert.True(dx * dx + dz * dz >= 50 * 50);
                }
            }
        }
    }

    /// <summary>
    /// A car park has to be reconstructable from the host log plus the match seed, exactly as a loot
    /// layout is — so the same seed must give the same plan in the same order, and a different seed
    /// must give a different one.
    /// </summary>
    [Fact]
    public void ThePlanIsReproducibleFromTheSeedAndDiffersBetweenSeeds()
    {
        IReadOnlyList<PlannedVehicle> first = Plan();
        IReadOnlyList<PlannedVehicle> again = Plan();

        Assert.Equal(first.Count, again.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i], again[i]);
        }

        IReadOnlyList<PlannedVehicle> other = Plan(Seed + 1);
        Assert.NotEqual(
            first.Select(v => v.AnchorInstanceId).ToArray(),
            other.Select(v => v.AnchorInstanceId).ToArray());
    }

    /// <summary>
    /// The mix is apportioned, not sampled: 300 cars at 40/25/20/15 are exactly 120/75/60/45. A
    /// per-anchor weighted draw would leave the ratio to luck.
    /// </summary>
    [Fact]
    public void TheMixIsExactRatherThanSampled()
    {
        IReadOnlyList<PlannedVehicle> plan = Plan(options: new VehicleSpawnPlanOptions { SpawnChance = null, Count = 200, RespectAuthoredTypes = false });

        Assert.Equal(200, plan.Count);
        Assert.Equal(80, plan.Count(v => v.VehicleId == 1));
        Assert.Equal(50, plan.Count(v => v.VehicleId == 2));
        Assert.Equal(40, plan.Count(v => v.VehicleId == 5));
        Assert.Equal(30, plan.Count(v => v.VehicleId == 3));

        // …and the model id travels with the type, because that is what 0xd7 actually carries.
        Assert.All(plan, v => Assert.Equal(Roster.Value.Require(v.VehicleId).ModelId, v.ModelId));
    }

    /// <summary>
    /// Largest-remainder apportionment, so an awkward count still sums exactly and still contains
    /// every listed type.
    /// </summary>
    [Fact]
    public void AnAwkwardCountStillSumsExactlyAndKeepsEveryType()
    {
        IReadOnlyList<PlannedVehicle> plan = Plan(options: new VehicleSpawnPlanOptions { SpawnChance = null, Count = 7, RespectAuthoredTypes = false });

        Assert.Equal(7, plan.Count);
        Assert.Equal(4, plan.Select(v => v.VehicleId).Distinct().Count());
    }

    /// <summary>
    /// One vehicle per placement and 20 m apart: occupying every bay of a 17-space lot looks wrong
    /// and puts 17 vehicle actors in one interest cell.
    /// </summary>
    [Fact]
    public void NoTwoCarsShareAnAnchorOrComeWithinTheSeparationRadius()
    {
        var options = new VehicleSpawnPlanOptions { SpawnChance = null };
        IReadOnlyList<PlannedVehicle> plan = Plan(options: options);

        Assert.Equal(plan.Count, plan.Select(v => v.AnchorInstanceId).Distinct().Count());

        float worst = float.MaxValue;
        for (int i = 0; i < plan.Count; i++)
        {
            for (int j = i + 1; j < plan.Count; j++)
            {
                float dx = plan[i].Position.X - plan[j].Position.X;
                float dz = plan[i].Position.Z - plan[j].Position.Z;
                worst = MathF.Min(worst, MathF.Sqrt((dx * dx) + (dz * dz)));
            }
        }

        Assert.True(
            worst >= options.MinimumSeparationMetres,
            $"the closest pair of cars is {worst:F1} m apart, under the {options.MinimumSeparationMetres} m minimum");
    }

    /// <summary>
    /// PVCommercialEast alone holds 937 of the 7,689 bays, so without the cap it would absorb a
    /// seventh of the map's cars.
    /// </summary>
    [Fact]
    public void NoNamedPlaceExceedsThePerAreaCap()
    {
        var options = new VehicleSpawnPlanOptions { SpawnChance = null };
        IReadOnlyList<PlannedVehicle> plan = Plan(options: options);

        Dictionary<int, int> perArea = [];
        foreach (PlannedVehicle vehicle in plan.Where(v => v.AreaIndex != VehicleAnchorSet.NoArea))
        {
            perArea[vehicle.AreaIndex] = perArea.GetValueOrDefault(vehicle.AreaIndex) + 1;
        }

        Assert.NotEmpty(perArea);
        Assert.All(perArea.Values, count => Assert.True(count <= options.MaxPerArea));

        // And the rural half is not car-free: the 791 open-road bays get used too.
        Assert.Contains(plan, v => v.AreaIndex == VehicleAnchorSet.NoArea);
    }

    /// <summary>
    /// The anchor's own world Y plus a small clearance, so the first driver's client settles the car
    /// onto the slab rather than starting it interpenetrating — and no terrain sampler is involved.
    /// </summary>
    [Fact]
    public void EveryCarSitsOnItsAnchorsGroundHeightPlusTheClearance()
    {
        var options = new VehicleSpawnPlanOptions { SpawnChance = null, Count = 40 };
        IReadOnlyList<PlannedVehicle> plan = Plan(options: options);

        Dictionary<uint, VehicleAnchor> byInstance = [];
        foreach (VehicleAnchor anchor in Anchors.Value.Anchors)
        {
            byInstance[anchor.InstanceId] = anchor;
        }

        foreach (PlannedVehicle vehicle in plan)
        {
            VehicleAnchor anchor = byInstance[vehicle.AnchorInstanceId];
            Assert.Equal(anchor.Position.X, vehicle.Position.X);
            Assert.Equal(anchor.Position.Z, vehicle.Position.Z);
            Assert.Equal(anchor.Position.Y + options.GroundClearanceMetres, vehicle.Position.Y, 0.001f);
            Assert.Equal(anchor.Yaw, vehicle.Yaw);
            Assert.Equal(anchor.VehicleId, vehicle.VehicleId);
        }
    }

    /// <summary>The yaw becomes the quaternion <c>0xd7</c>'s body carries, about the up axis.</summary>
    [Fact]
    public void TheAnchorYawBecomesAUnitQuaternionAboutTheUpAxis()
    {
        var planned = new PlannedVehicle(1, 7225, Vector3.Zero, MathF.PI / 2f, 0, VehicleAnchorSet.NoArea);

        Vector4 rotation = planned.Rotation;

        Assert.Equal(0f, rotation.X, 0.0001f);
        Assert.Equal(MathF.Sin(MathF.PI / 4f), rotation.Y, 0.0001f);
        Assert.Equal(0f, rotation.Z, 0.0001f);
        Assert.Equal(MathF.Cos(MathF.PI / 4f), rotation.W, 0.0001f);
        Assert.Equal(1f, rotation.Length(), 0.0001f);
    }

    /// <summary>
    /// With relaxation off, a count the anchor set cannot satisfy under the constraints comes back
    /// short rather than quietly breaking the spacing.
    /// </summary>
    [Fact]
    public void AnImpossibleCountComesBackShortWhenRelaxationIsOff()
    {
        var strict = new VehicleSpawnPlanOptions
        {
            SpawnChance = null,
            Count = 3_000,
            MinimumSeparationMetres = 400f,
            MaxPerArea = 1,
            RelaxWhenShort = false,
        };

        IReadOnlyList<PlannedVehicle> plan = Plan(options: strict);

        Assert.NotEmpty(plan);
        Assert.True(plan.Count < strict.Count, $"{plan.Count} cars fitted, which is not short of {strict.Count}");
        Assert.Equal(plan.Count, plan.Select(v => v.AnchorInstanceId).Distinct().Count());
    }

    [Fact]
    public void EvenRelaxedPlansNeverWaiveSpacingOrRaiseAuthoredHeight()
    {
        var options = new VehicleSpawnPlanOptions { SpawnChance = null, Count = 3000, MinimumSeparationMetres = 400, RelaxWhenShort = true };
        var plan = Plan(options: options);
        Assert.NotEmpty(plan);
        Assert.True(plan.Count < options.Count);
        foreach (var car in plan)
        {
            var anchor = Anchors.Value.Anchors.ToArray().Single(a => a.InstanceId == car.AnchorInstanceId);
            Assert.Equal(anchor.Position.Y, car.Position.Y);
            foreach (var other in plan.Where(p => p.AnchorInstanceId != car.AnchorInstanceId))
                Assert.True(Vector2.Distance(new(car.Position.X, car.Position.Z), new(other.Position.X, other.Position.Z)) >= 400);
        }
    }

    [Fact]
    public void ACountOfZeroOrAnUnknownVehicleIdIsHandledAtPlanTime()
    {
        Assert.Empty(Plan(options: new VehicleSpawnPlanOptions { SpawnChance = null, Count = 0 }));

        // 15 is an Ignition match-mode variant, not a BR car: fail here, not at the first 0xd7.
        Assert.Throws<KeyNotFoundException>(() => Plan(options: new VehicleSpawnPlanOptions
        {
            SpawnChance = null,
            Count = 4,
            Mix = [new VehicleSpawnShare(15, 1)],
        }));

        Assert.Throws<ArgumentException>(() => Plan(options: new VehicleSpawnPlanOptions
        {
            SpawnChance = null,
            Count = 4,
            Mix = [new VehicleSpawnShare(1, 0)],
        }));
    }
}
