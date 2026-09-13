using System.Numerics;
using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Vehicles;

/// <summary>
/// One vehicle the planner decided to place, ready to hand straight to
/// <c>AddLightweightVehicle 0xd7</c>.
/// </summary>
/// <param name="AnchorInstanceId">
/// The authored source location this car sits on. Logged with every spawn, so a run's whole car park
/// can be reconstructed from the host log plus the match seed.
/// </param>
public readonly record struct PlannedVehicle(
    uint VehicleId,
    uint ModelId,
    Vector3 Position,
    float Yaw,
    uint AnchorInstanceId,
    int AreaIndex,
    float Pitch = 0,
    float Roll = 0)
{
    /// <summary>The complete authored orientation in <c>0xd7</c>'s body (Y is up).</summary>
    public Vector4 Rotation
    {
        get
        {
            Quaternion rotation = Quaternion.CreateFromYawPitchRoll(Yaw, Pitch, Roll);
            return new Vector4(rotation.X, rotation.Y, rotation.Z, rotation.W);
        }
    }
}

/// <summary>
/// Ordinary vehicle pads have a 30% chance per round; police stations get one or two cars.
/// Setting SpawnChance to null opts into the older budget and density rules. Population is
/// server policy; vehicle families and the retained placement corrections are unchanged.
/// </summary>
public sealed record VehicleSpawnPlanOptions
{
    /// <summary>Ordinary pad occupancy; defaults to 0.3. Zero/one select none/all; null selects legacy count/spacing.</summary>
    public double? SpawnChance { get; init; } = Rulings.VehiclesPlan.SpawnChance;

    /// <summary>For fractional chances, keep one or two occupied police bays at each identified station.</summary>
    public bool LimitPoliceStationPopulation { get; init; } = true;

    /// <summary>
    /// Legacy cars per match, used only when SpawnChance is null: <b>150</b>. The plan can be
    /// shorter when authored locations cannot meet its spacing constraints. This is a server
    /// population policy, not a measured retail count (docs/vehicle-spawns-20260906.md).
    /// </summary>
    public int Count { get; init; } = Rulings.VehiclesPlan.Count;

    /// <summary>
    /// Fallback relative odds per vehicle id for untyped anchors or RespectAuthoredTypes=false.
    /// The shipped anchors all carry their authored family, so these weights do not change
    /// their vehicle types or represent their spawn percentages. The PoliceCar is the rarest because it is the fastest
    /// (<c>MAX_FORWARD</c> 105 against 85–90); the ATV is common because it is the cheapest to meet
    /// on foot. OffRoader 40 % / PickupTruck 25 % / ATV 20 % / PoliceCar 15 %. [DESIGN]
    /// </summary>
    public IReadOnlyList<VehicleSpawnShare> Mix { get; init; } =
    [
        // Rulings.VehiclesPlan.MixVehicleIds / MixWeights (D35): OffRoader, PickupTruck, ATV,
        // PoliceCar. Paired here rather than zipped at run time so the shares stay readable.
        new(VehicleId: Rulings.VehiclesPlan.MixVehicleIds[0], Weight: Rulings.VehiclesPlan.MixWeights[0]),
        new(VehicleId: Rulings.VehiclesPlan.MixVehicleIds[1], Weight: Rulings.VehiclesPlan.MixWeights[1]),
        new(VehicleId: Rulings.VehiclesPlan.MixVehicleIds[2], Weight: Rulings.VehiclesPlan.MixWeights[2]),
        new(VehicleId: Rulings.VehiclesPlan.MixVehicleIds[3], Weight: Rulings.VehiclesPlan.MixWeights[3]),
    ];

    /// <summary>
    /// Legacy minimum spacing in metres, used only when SpawnChance is null. The owner's Z1
    /// background population uses 50 metres; this is a density rule, not a vehicle collision radius.
    /// </summary>
    public float MinimumSeparationMetres { get; init; } = Rulings.VehiclesPlan.MinimumSeparationMetres;

    /// <summary>
    /// Legacy first-pass cap for a named place, used only when SpawnChance is null. [DESIGN]
    /// </summary>
    public int MaxPerArea { get; init; } = Rulings.VehiclesPlan.MaxPerArea;

    /// <summary>
    /// Metres added to the authored vehicle origin. Defaults to zero: these are vehicle locations,
    /// not parking-slab mesh origins that need an invented clearance. [DESIGN]
    /// </summary>
    public float GroundClearanceMetres { get; init; } = Rulings.VehiclesPlan.GroundClearanceMetres;

    /// <summary>
    /// Whether a legacy shortfall pass may relax the per-area cap. Its separation is always enforced.
    /// </summary>
    public bool RelaxWhenShort { get; init; } = true;

    /// <summary>Keep the authored family, so a full-size car does not replace an ATV in its smaller spot.</summary>
    public bool RespectAuthoredTypes { get; init; } = true;
}

/// <summary>One entry of <see cref="VehicleSpawnPlanOptions.Mix"/>.</summary>
public readonly record struct VehicleSpawnShare(uint VehicleId, int Weight);

/// <summary>
/// Chooses where a match's vehicles go, from the derived anchor set (<see cref="VehicleAnchorSet"/>)
/// under an explicitly-ours policy (<see cref="VehicleSpawnPlanOptions"/>).
///
/// <para><b>Deterministic.</b> The same anchor file, options and match seed always produce the same
/// plan, in the same order — so a car park is reproducible from the host log alone, exactly as
/// <c>Z2LootSpawns</c> makes a loot layout reproducible. The two random streams (which anchors, and
/// which vehicle goes on each) are seeded separately from the match seed, so changing
/// <see cref="VehicleSpawnPlanOptions.Mix"/> does not reshuffle the chosen anchors.</para>
///
/// <para><b>Authored families are retained by default.</b> The fallback type multiset for untyped
/// anchors or RespectAuthoredTypes=false is built by largest-remainder from the weights and then
/// shuffled. Ordinary pads use independent seeded rolls; identified police stations get one or two cars.
/// Explicit chances of zero or one still select none or all. Setting SpawnChance to null
/// opts into the legacy count, spacing and per-area limits.</para>
///
/// <para><b>Not respawned within a match.</b> <c>Vehicles.txt RESPAWN_TIME</c> is 0 on every row and
/// a BR round is short, so the plan is produced once at match start (docs/43 §7.3 item 6).</para>
/// </summary>
public static class VehicleSpawnPlanner
{
    /// <summary>
    /// Plans a match's car park using independent pad rolls, or the optional legacy budget.
    /// </summary>
    public static IReadOnlyList<PlannedVehicle> Plan(
        VehicleAnchorSet anchors,
        VehicleRoster roster,
        ulong matchSeed,
        VehicleSpawnPlanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(roster);
        options ??= new VehicleSpawnPlanOptions();

        if (options.SpawnChance is double chance && (!double.IsFinite(chance) || chance < 0 || chance > 1))
            throw new ArgumentOutOfRangeException(nameof(options), "SpawnChance must be between zero and one.");

        if (options.SpawnChance is null && options.Count <= 0)
        {
            return [];
        }

        // Validate requested types even when no position can be selected.
        _ = BuildTypeSequence(options with { Count = 1 }, roster, matchSeed);
        int[] order = ShuffledAnchorOrder(anchors.Count, matchSeed);

        var chosen = new List<int>(anchors.Count);

        if (options.SpawnChance is double probability)
        {
            // Roll each confirmed location separately; station groups then bound their population.
            foreach (int index in order)
            {
                var roll = new SplitMix64(matchSeed ^ ((ulong)anchors[index].InstanceId << 32) ^ 0x504144535041574EUL);
                if ((roll.Next() >> 11) * (1.0 / (1UL << 53)) < probability) chosen.Add(index);
            }

            if (probability is > 0 and < 1 && options.LimitPoliceStationPopulation)
                VehiclePopulationGroups.LimitPoliceStations(anchors, order, chosen);
        }
        else
        {
            ArgumentOutOfRangeException.ThrowIfNegative(options.MinimumSeparationMetres);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxPerArea);
            var perArea = new int[anchors.AreaCount];
            var separation = new SeparationGrid(options.MinimumSeparationMetres, Math.Min(options.Count, anchors.Count));
            var taken = new bool[anchors.Count];
            Select(anchors, order, taken, chosen, perArea, separation, options.Count, options.MaxPerArea, useSeparation: true);

            if (options.RelaxWhenShort && chosen.Count < options.Count)
            {
                // Legacy shortfall pass may relax area density, but retains separation.
                Select(anchors, order, taken, chosen, perArea, separation, options.Count, int.MaxValue, useSeparation: true);
            }
        }

        uint[] types = BuildTypeSequence(options with { Count = chosen.Count }, roster, matchSeed);
        var plan = new List<PlannedVehicle>(chosen.Count);
        for (int i = 0; i < chosen.Count; i++)
        {
            ref readonly VehicleAnchor anchor = ref anchors[chosen[i]];
            VehicleDefinition definition = roster.Require(options.RespectAuthoredTypes && anchor.VehicleId != 0
                ? anchor.VehicleId : types[i]);
            plan.Add(new PlannedVehicle(
                VehicleId: definition.VehicleId,
                ModelId: definition.ModelId,
                Position: anchor.Position with { Y = anchor.Position.Y + options.GroundClearanceMetres },
                Yaw: anchor.Yaw,
                AnchorInstanceId: anchor.InstanceId,
                AreaIndex: anchor.AreaIndex,
                Pitch: anchor.Pitch,
                Roll: anchor.Roll));
        }

        return plan;
    }

    private static void Select(
        VehicleAnchorSet anchors,
        int[] order,
        bool[] taken,
        List<int> chosen,
        int[] perArea,
        SeparationGrid separation,
        int wanted,
        int maxPerArea,
        bool useSeparation)
    {
        foreach (int index in order)
        {
            if (chosen.Count >= wanted)
            {
                return;
            }

            if (taken[index])
            {
                continue;
            }

            ref readonly VehicleAnchor anchor = ref anchors[index];

            // One vehicle per placement regardless of the bays its mesh paints: occupying every one
            // of a 17-space lot looks wrong and costs 17 entities in one interest cell.
            if (anchor.HasArea && perArea[anchor.AreaIndex] >= maxPerArea)
            {
                continue;
            }

            if (useSeparation && !separation.IsClear(anchor.Position))
            {
                continue;
            }

            taken[index] = true;
            chosen.Add(index);
            separation.Add(anchor.Position);
            if (anchor.HasArea)
            {
                perArea[anchor.AreaIndex]++;
            }
        }
    }

    /// <summary>
    /// The exact type multiset for <paramref name="options"/>, shuffled. Largest-remainder
    /// apportionment: floor each share, then hand the leftover slots to the largest remainders, so
    /// the totals sum to <see cref="VehicleSpawnPlanOptions.Count"/> exactly and every listed type
    /// with a positive weight appears if the count allows.
    /// </summary>
    private static uint[] BuildTypeSequence(
        VehicleSpawnPlanOptions options,
        VehicleRoster roster,
        ulong matchSeed)
    {
        List<VehicleSpawnShare> mix = [.. options.Mix.Where(share => share.Weight > 0)];
        if (mix.Count == 0)
        {
            throw new ArgumentException("The vehicle mix has no entry with a positive weight.", nameof(options));
        }

        foreach (VehicleSpawnShare share in mix)
        {
            // Fail here, at plan time, rather than at the first spawn packet.
            _ = roster.Require(share.VehicleId);
        }

        int totalWeight = mix.Sum(share => share.Weight);
        var counts = new int[mix.Count];
        var remainders = new double[mix.Count];
        int assigned = 0;

        for (int i = 0; i < mix.Count; i++)
        {
            double exact = (double)options.Count * mix[i].Weight / totalWeight;
            counts[i] = (int)Math.Floor(exact);
            remainders[i] = exact - counts[i];
            assigned += counts[i];
        }

        for (int slot = assigned; slot < options.Count; slot++)
        {
            int best = 0;
            for (int i = 1; i < mix.Count; i++)
            {
                if (remainders[i] > remainders[best])
                {
                    best = i;
                }
            }

            counts[best]++;
            remainders[best] = double.NegativeInfinity;
        }

        var types = new uint[options.Count];
        int cursor = 0;
        for (int i = 0; i < mix.Count; i++)
        {
            for (int n = 0; n < counts[i]; n++)
            {
                types[cursor++] = mix[i].VehicleId;
            }
        }

        var random = new SplitMix64(matchSeed ^ 0x5645_4849_434C_4553UL);   // "SELCIHEV"
        for (int i = types.Length - 1; i > 0; i--)
        {
            int j = (int)random.NextBounded((uint)(i + 1));
            (types[i], types[j]) = (types[j], types[i]);
        }

        return types;
    }

    private static int[] ShuffledAnchorOrder(int count, ulong matchSeed)
    {
        var order = new int[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
        }

        var random = new SplitMix64(matchSeed ^ 0x5352_4F48_434E_4141UL);   // "AANCHORS"
        for (int i = count - 1; i > 0; i--)
        {
            int j = (int)random.NextBounded((uint)(i + 1));
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order;
    }

    /// <summary>
    /// A uniform hash grid whose cell edge is the separation radius, so a candidate only has to be
    /// compared against the nine cells around it rather than every car already placed. At 300 cars
    /// that is the difference between ~45,000 distance tests and a few hundred.
    /// </summary>
    private sealed class SeparationGrid(float radiusMetres, int capacity)
    {
        private readonly Dictionary<(int X, int Z), List<Vector3>> _cells = new(capacity);
        private readonly float _radius = radiusMetres;
        private readonly float _radiusSquared = radiusMetres * radiusMetres;

        public bool IsClear(Vector3 position)
        {
            if (_radius <= 0f)
            {
                return true;
            }

            (int cx, int cz) = Cell(position);
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!_cells.TryGetValue((cx + dx, cz + dz), out List<Vector3>? bucket))
                    {
                        continue;
                    }

                    foreach (Vector3 placed in bucket)
                    {
                        // Horizontal only: two bays stacked on a multi-storey car park are still
                        // two different parking spaces.
                        float ddx = placed.X - position.X;
                        float ddz = placed.Z - position.Z;
                        if ((ddx * ddx) + (ddz * ddz) < _radiusSquared)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        public void Add(Vector3 position)
        {
            if (_radius <= 0f)
            {
                return;
            }

            (int cx, int cz) = Cell(position);
            if (!_cells.TryGetValue((cx, cz), out List<Vector3>? bucket))
            {
                bucket = [];
                _cells[(cx, cz)] = bucket;
            }

            bucket.Add(position);
        }

        private (int X, int Z) Cell(Vector3 position) =>
            ((int)MathF.Floor(position.X / _radius), (int)MathF.Floor(position.Z / _radius));
    }
}

/// <summary>
/// SplitMix64 — a small, exactly-reproducible generator. The framework's <see cref="Random"/> is
/// explicitly not guaranteed stable across runtime versions, and a car park that moves when the
/// runtime is patched is not reproducible from a log.
/// </summary>
internal struct SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        unchecked
        {
            _state += 0x9E37_79B9_7F4A_7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Uniform in <c>[0, bound)</c>, rejection-sampled so the low bits stay unbiased.</summary>
    public uint NextBounded(uint bound)
    {
        ArgumentOutOfRangeException.ThrowIfZero(bound);

        uint limit = uint.MaxValue - (uint.MaxValue % bound) - 1;
        while (true)
        {
            uint value = (uint)(Next() >> 32);
            if (value <= limit)
            {
                return value % bound;
            }
        }
    }
}
