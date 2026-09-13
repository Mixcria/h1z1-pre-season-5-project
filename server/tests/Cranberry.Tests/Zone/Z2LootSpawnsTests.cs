using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone;

/// <summary>
/// The shipped Z2 placement data (docs/33) and the two properties a live loot burst depends on:
/// the burst takes the NEAREST markers, and the point it is taken around actually has some.
/// </summary>
public sealed class Z2LootSpawnsTests
{
    private static readonly Lazy<Z2LootSpawns> Spawns = new(Z2LootSpawns.LoadDefault);

    private static Vector3 Position(Vector4 point) => new(point.X, point.Y, point.Z);

    [Fact]
    public void TheShippedPlacementFileLoadsWithItsOwnHeaderGrid()
    {
        Z2LootSpawns spawns = Spawns.Value;

        Assert.Equal(168_322, spawns.Count);
        Assert.Equal(128, spawns.Dimension);
        Assert.Equal(64f, spawns.CellMetres);
        Assert.Equal(-4096f, spawns.OriginX);
        Assert.Equal(-4096f, spawns.OriginZ);
    }

    /// <summary>
    /// <c>Query</c> fills its span in grid-scan order, so a caller that truncates it at a cap keeps
    /// a band at one edge of the disc. Measured on the shipped file at the drop point: the first 64
    /// in grid order are 74.4–119.1 m out, while the nearest 64 are all within 19.9 m — and the
    /// client's own interact range is 3 m, so the difference is the whole feature.
    /// </summary>
    [Fact]
    public void QueryNearestReturnsTheClosestMarkersWhereQueryReturnsAFarEdgeBand()
    {
        Z2LootSpawns spawns = Spawns.Value;
        Vector3 centre = Position(new ZoneOptions().MatchDropSpawn);
        const float radius = 120f;

        Span<int> scanOrder = stackalloc int[64];
        int matchedByQuery = spawns.Query(centre, radius, scanOrder);

        Span<int> nearest = stackalloc int[64];
        int matchedByNearest = spawns.QueryNearest(centre, radius, nearest);

        // Both overloads count the same disc; only the retained set differs.
        Assert.Equal(matchedByQuery, matchedByNearest);
        Assert.True(matchedByQuery > 64);

        float worstNearest = Distance(spawns, nearest[63], centre);
        float bestScanOrder = float.MaxValue;
        foreach (int index in scanOrder)
        {
            bestScanOrder = MathF.Min(bestScanOrder, Distance(spawns, index, centre));
        }

        Assert.True(worstNearest < 25f, $"the nearest 64 reach {worstNearest:F1} m");
        Assert.True(
            bestScanOrder > worstNearest,
            $"grid order's closest marker is {bestScanOrder:F1} m, nearest-64's farthest is {worstNearest:F1} m");

        // Ascending, closest first, and every one inside the radius.
        float previous = 0f;
        foreach (int index in nearest)
        {
            float distance = Distance(spawns, index, centre);
            Assert.True(distance >= previous);
            Assert.True(distance <= radius);
            previous = distance;
        }
    }

    [Fact]
    public void QueryNearestKeepsTheGlobalNearestSetNotJustTheNearestOfEachRow()
    {
        Z2LootSpawns spawns = Spawns.Value;
        Vector3 centre = Position(new ZoneOptions().MatchDropSpawn);

        Span<int> eight = stackalloc int[8];
        spawns.QueryNearest(centre, 120f, eight);

        // The eight nearest of the whole disc must be a prefix of the 64 nearest of the same disc.
        Span<int> sixtyFour = stackalloc int[64];
        spawns.QueryNearest(centre, 120f, sixtyFour);

        for (int i = 0; i < eight.Length; i++)
        {
            Assert.Equal(
                Distance(spawns, sixtyFour[i], centre),
                Distance(spawns, eight[i], centre),
                3);
        }
    }

    [Fact]
    public void QueryNearestDegradesToQueryForAnEmptySpanAndRefusesAnImpossibleRadius()
    {
        Z2LootSpawns spawns = Spawns.Value;
        Vector3 centre = Position(new ZoneOptions().MatchDropSpawn);

        Assert.Equal(
            spawns.Query(centre, 120f, []),
            spawns.QueryNearest(centre, 120f, []));

        Span<int> found = stackalloc int[4];
        Assert.Equal(0, spawns.QueryNearest(centre, 0f, found));
        Assert.Equal(0, spawns.QueryNearest(centre, float.NaN, found));
        Assert.Equal(0, spawns.QueryNearest(centre, 120f, categoryMask: 0, found));
    }

    /// <summary>
    /// The drop point must be somewhere the map's own markers are. A vertical fall from
    /// <see cref="ZoneOptions.StagingSpawn"/> lands 1,127.5 m from the nearest of the 168,322
    /// markers, which made the real ground loot a silent no-op for every match.
    /// </summary>
    [Fact]
    public void TheMatchDropSpawnHasMarkersAroundItAndTheStagingSpawnHasNone()
    {
        Z2LootSpawns spawns = Spawns.Value;
        var options = new ZoneOptions();

        Span<int> found = stackalloc int[128];
        int atDrop = spawns.QueryNearest(Position(options.MatchDropSpawn), options.GroundLootRadius, found);
        Assert.True(
            atDrop >= options.GroundLootMaxPerBurst,
            $"the drop point has only {atDrop} marker(s) within {options.GroundLootRadius} m");

        Span<int> none = stackalloc int[8];
        Assert.Equal(0, spawns.QueryNearest(Position(options.StagingSpawn), options.GroundLootRadius, none));
    }

    /// <summary>
    /// Most markers near the drop point stay <b>empty</b>, and that is the fix, not a fault: the
    /// per-marker spawn gate (docs/39 §4) is what turns 64 items inside a 20 m circle into the
    /// 0-6-per-room floor the owner counted in retail footage. What every marker that <i>does</i>
    /// roll must still be is a real item, deterministically.
    /// </summary>
    [Fact]
    public void MostMarkersNearTheDropPointStayEmptyAndTheRestRollRealItems()
    {
        Z2LootSpawns spawns = Spawns.Value;
        LootTables tables = LootTables.LoadDefault();
        Vector3 centre = Position(new ZoneOptions().MatchDropSpawn);

        Span<int> found = stackalloc int[64];
        int matched = spawns.QueryNearest(centre, 120f, found);
        Assert.True(matched > 0);

        int rolled = 0;
        foreach (int index in found)
        {
            if (!spawns.TryRoll(index, tables, matchSeed: 1, out LootSpawnRoll roll))
            {
                continue;
            }

            Assert.NotEqual(0u, roll.ItemDefinitionId);
            Assert.NotEqual(0u, roll.GroundModelId);
            Assert.True(roll.Count >= 1);

            // Deterministic: the same seed and the same marker give the same item.
            Assert.True(spawns.TryRoll(index, tables, matchSeed: 1, out LootSpawnRoll again));
            Assert.Equal(roll, again);
            rolled++;
        }

        // A quarter of 64, give or take. A run that spawns all 64 means the gate has been lost.
        Assert.InRange(rolled, 4, 32);
    }

    /// <summary>
    /// The gate is a pure function of <c>(matchSeed, instance id)</c> and independent of the item
    /// pick's own stream, so retuning a table's odds never re-shuffles which markers are occupied.
    /// </summary>
    [Fact]
    public void TheSpawnGateIsSeededDeterministicAndBoundedByItsProbability()
    {
        Assert.False(Z2LootSpawns.PassesGate(1, 4242, 0.0));
        Assert.True(Z2LootSpawns.PassesGate(1, 4242, 1.0));

        for (uint instance = 1; instance < 50; instance++)
        {
            Assert.Equal(
                Z2LootSpawns.PassesGate(7, instance, 0.25),
                Z2LootSpawns.PassesGate(7, instance, 0.25));
        }

        Assert.NotEqual(Z2LootSpawns.SeedFor(1, 4242), Z2LootSpawns.GateSeedFor(1, 4242));

        int live = 0;
        for (uint instance = 0; instance < 20_000; instance++)
        {
            if (Z2LootSpawns.PassesGate(9, instance, 0.25))
            {
                live++;
            }
        }

        Assert.InRange(live / 20_000.0, 0.235, 0.265);
    }

    private static float Distance(Z2LootSpawns spawns, int index, Vector3 centre)
    {
        ref readonly LootSpawnPoint point = ref spawns[index];
        float dx = point.X - centre.X;
        float dz = point.Z - centre.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}
