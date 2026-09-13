using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Gas;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// docs/22 §4.7 and §12 row 5: the grid must contain the live staging spawn and the gas play area,
// and a query is ceil(radius / cellSize) rings — never a fixed 3 x 3.
public sealed class InterestGridTests
{
    private static Vector3 At(float x, float z) => new(x, 506f, z);

    [Fact]
    public void TheLiveStagingSpawnIsInsideTheGrid()
    {
        // ZoneOptions.cs:86 - the spawn the server actually uses today.
        Vector4 spawn = new ZoneOptions().StagingSpawn;
        var position = new Vector3(spawn.X, spawn.Y, spawn.Z);

        Assert.True(MathF.Abs(position.X) < InterestGrid.HalfExtentMetres);
        Assert.True(MathF.Abs(position.Z) < InterestGrid.HalfExtentMetres);

        // Not clamped onto an edge cell: a 128 m grid (+/-4,096 m) would clamp this one.
        ushort cell = InterestGrid.CellOf(position);
        int x = cell % InterestGrid.Dimension;
        int z = cell / InterestGrid.Dimension;
        Assert.InRange(x, 1, InterestGrid.Dimension - 2);
        Assert.InRange(z, 1, InterestGrid.Dimension - 2);
    }

    [Fact]
    public void TheWholeGasPlayAreaFitsInsideTheGrid()
    {
        float radius = new GasSettings().InitialRadius;

        Assert.True(radius <= InterestGrid.HalfExtentMetres);
        Assert.NotEqual(
            InterestGrid.CellOf(At(radius - 1f, 0f)),
            InterestGrid.CellOf(At(-radius + 1f, 0f)));
    }

    [Fact]
    public void RingCountIsCeilingOfRadiusOverCellSize()
    {
        Assert.Equal(0, InterestGrid.RingCount(0f));
        Assert.Equal(1, InterestGrid.RingCount(1f));
        Assert.Equal(1, InterestGrid.RingCount(InterestGrid.CellMetres));
        Assert.Equal(2, InterestGrid.RingCount(InterestGrid.CellMetres + 1f));

        // The two radii this design actually queries with.
        Assert.Equal(2, InterestGrid.RingCount(400f));   // players: 5 x 5 cells
        Assert.Equal(1, InterestGrid.RingCount(80f));    // items:   3 x 3 cells
    }

    [Fact]
    public void QueryAgreesWithABruteForceOracle()
    {
        var grid = new InterestGrid(512);
        var random = new Random(1234);
        var positions = new Vector3[512];

        for (int key = 0; key < positions.Length; key++)
        {
            positions[key] = At(
                (float)((random.NextDouble() * 12_000) - 6_000),
                (float)((random.NextDouble() * 12_000) - 6_000));
            grid.Add(key, InterestGrid.CellOf(positions[key]));
        }

        Span<int> buffer = stackalloc int[512];
        for (int probe = 0; probe < 25; probe++)
        {
            Vector3 centre = positions[random.Next(positions.Length)];
            const float Radius = 400f;

            int found = grid.Query(centre, Radius, buffer);
            var reported = new HashSet<int>();
            for (int i = 0; i < Math.Min(found, buffer.Length); i++)
            {
                reported.Add(buffer[i]);
            }

            // Every key genuinely inside the radius must be reported. (The block query also returns
            // corner keys slightly outside it; the caller filters on exact distance.)
            for (int key = 0; key < positions.Length; key++)
            {
                if (InterestGrid.HorizontalDistanceSquared(centre, positions[key]) <= Radius * Radius)
                {
                    Assert.Contains(key, reported);
                }
            }
        }
    }

    [Fact]
    public void AQueryNarrowerThanOneCellStillFindsTheCentreCell()
    {
        var grid = new InterestGrid(8);
        grid.Add(0, InterestGrid.CellOf(At(10f, 10f)));

        Span<int> buffer = stackalloc int[8];
        Assert.Equal(1, grid.Query(At(12f, 12f), 1f, buffer));
        Assert.Equal(0, buffer[0]);
    }

    [Fact]
    public void MoveRebucketsAKeyAndRemoveDropsIt()
    {
        var grid = new InterestGrid(8);
        ushort from = InterestGrid.CellOf(At(0f, 0f));
        ushort to = InterestGrid.CellOf(At(2_000f, 2_000f));

        grid.Add(0, from);
        Assert.Equal(1, grid.Count);

        grid.Move(0, from, to);
        Span<int> buffer = stackalloc int[8];
        Assert.Equal(0, grid.Query(At(0f, 0f), 100f, buffer));
        Assert.Equal(1, grid.Query(At(2_000f, 2_000f), 100f, buffer));

        grid.Remove(0, to);
        Assert.Equal(0, grid.Count);
        Assert.Equal(0, grid.Query(At(2_000f, 2_000f), 100f, buffer));
    }

    [Fact]
    public void MovingWithinACellIsANoOp()
    {
        var grid = new InterestGrid(8);
        ushort cell = InterestGrid.CellOf(At(0f, 0f));
        grid.Add(0, cell);
        grid.Move(0, cell, cell);

        Span<int> buffer = stackalloc int[8];
        Assert.Equal(1, grid.Query(At(0f, 0f), 10f, buffer));
        Assert.Equal(1, grid.Count);
    }

    [Fact]
    public void QueryReportsTheOverflowCountWithoutWritingPastTheSpan()
    {
        var grid = new InterestGrid(64);
        for (int key = 0; key < 64; key++)
        {
            grid.Add(key, InterestGrid.CellOf(At(0f, 0f)));
        }

        Span<int> small = stackalloc int[4];
        Assert.Equal(64, grid.Query(At(0f, 0f), 10f, small));
    }

    [Fact]
    public void APositionOutsideTheWorldIsClampedRatherThanThrown()
    {
        ushort far = InterestGrid.CellOf(At(500_000f, -500_000f));
        ushort corner = InterestGrid.CellOf(At(InterestGrid.HalfExtentMetres * 2, -InterestGrid.HalfExtentMetres * 2));

        Assert.Equal(corner, far);
        Assert.Equal(InterestGrid.Dimension - 1, far % InterestGrid.Dimension);
        Assert.Equal(0, far / InterestGrid.Dimension);
    }

    [Fact]
    public void ANonFinitePositionLandsInACellInsteadOfThrowing()
    {
        ushort cell = InterestGrid.CellOf(new Vector3(float.NaN, 0f, float.PositiveInfinity));

        Assert.InRange(cell, 0, (InterestGrid.Dimension * InterestGrid.Dimension) - 1);
    }

    [Fact]
    public void AKeyOutsideTheCapacityIsRejected()
    {
        var grid = new InterestGrid(4);

        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Add(4, 0));
    }

    [Fact]
    public void QueryIsFreeOfManagedAllocation()
    {
        var grid = new InterestGrid(128);
        for (int key = 0; key < 128; key++)
        {
            grid.Add(key, InterestGrid.CellOf(At(key * 4f, 0f)));
        }

        Span<int> buffer = stackalloc int[128];
        grid.Query(At(0f, 0f), 400f, buffer);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            grid.Query(At(0f, 0f), 400f, buffer);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
