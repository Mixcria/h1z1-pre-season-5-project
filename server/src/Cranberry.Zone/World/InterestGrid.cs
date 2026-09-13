using System.Numerics;

namespace Cranberry.Zone.World;

/// <summary>
/// Uniform grid over the match zone in X/Z (Y is world height). Head/next buckets over caller-owned
/// integer keys: no per-cell list, no allocation after construction, and an entity only changes
/// bucket when its cell index changes.
/// <para>
/// Sizing is derived, not guessed (docs/22 §4.7, §12 row 5): the live Z2 staging spawn is
/// <c>(−233.83, 506.36, −4892.03)</c> (<c>ZoneOptions.cs:86</c>) and the gas play area is a 6,000 m
/// radius circle on the origin (<c>GasSettings.InitialRadius</c>), so the grid must cover at least
/// ±6,000 m. 64 × 64 cells of 256 m cover ±8,192 m and contain both; 128 m cells (±4,096 m) do not
/// contain the spawn the server uses today.
/// </para>
/// </summary>
public sealed class InterestGrid
{
    public const float CellMetres = 256f;

    /// <summary>64 × 256 m = 16,384 m across, i.e. ±8,192 m from the origin.</summary>
    public const int Dimension = 64;

    public const float HalfExtentMetres = Dimension * CellMetres / 2f;

    private readonly int[] _head;
    private readonly int[] _next;
    private readonly int _capacity;

    public InterestGrid(int keyCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keyCapacity);

        _capacity = keyCapacity;
        _head = new int[Dimension * Dimension];
        _next = new int[keyCapacity];
        Array.Fill(_head, -1);
        Array.Fill(_next, -1);
    }

    public int CellCount => _head.Length;

    public int KeyCapacity => _capacity;

    /// <summary>Keys currently bucketed.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Cell index for a position, clamped at the edges so a pose outside the world still lands in a
    /// cell instead of throwing. Y is ignored.
    /// </summary>
    public static ushort CellOf(in Vector3 position)
    {
        int x = AxisIndex(position.X);
        int z = AxisIndex(position.Z);
        return (ushort)((z * Dimension) + x);
    }

    /// <summary>Cell rings a query of this radius must scan: <c>ceil(radius / CellMetres)</c>.
    /// A fixed 3 × 3 is wrong for any radius above one cell (docs/22 §12 row 5).</summary>
    public static int RingCount(float radius)
    {
        if (!float.IsFinite(radius) || radius <= 0f)
        {
            return 0;
        }

        int rings = (int)MathF.Ceiling(radius / CellMetres);
        return Math.Min(rings, Dimension);
    }

    public void Add(int key, ushort cell)
    {
        CheckKey(key);
        _next[key] = _head[cell];
        _head[cell] = key;
        Count++;
    }

    public void Move(int key, ushort fromCell, ushort toCell)
    {
        if (fromCell == toCell)
        {
            return;
        }

        Remove(key, fromCell);
        Add(key, toCell);
    }

    public void Remove(int key, ushort cell)
    {
        CheckKey(key);

        int current = _head[cell];
        int previous = -1;
        while (current >= 0)
        {
            if (current == key)
            {
                if (previous < 0)
                {
                    _head[cell] = _next[current];
                }
                else
                {
                    _next[previous] = _next[current];
                }

                _next[key] = -1;
                Count--;
                return;
            }

            previous = current;
            current = _next[current];
        }
    }

    /// <summary>
    /// Fills <paramref name="into"/> with the keys in the (2r+1)² cell block around the centre,
    /// r = <see cref="RingCount"/>. No allocation; the caller owns the span. Returns the number of
    /// keys found, which may exceed the span — in which case the span is filled and the excess is
    /// still counted so the caller can widen its buffer.
    /// </summary>
    public int Query(in Vector3 centre, float radius, Span<int> into)
    {
        int rings = RingCount(radius);
        int centreX = AxisIndex(centre.X);
        int centreZ = AxisIndex(centre.Z);

        int minX = Math.Max(0, centreX - rings);
        int maxX = Math.Min(Dimension - 1, centreX + rings);
        int minZ = Math.Max(0, centreZ - rings);
        int maxZ = Math.Min(Dimension - 1, centreZ + rings);

        int found = 0;
        for (int z = minZ; z <= maxZ; z++)
        {
            int rowBase = z * Dimension;
            for (int x = minX; x <= maxX; x++)
            {
                int key = _head[rowBase + x];
                while (key >= 0)
                {
                    if (found < into.Length)
                    {
                        into[found] = key;
                    }

                    found++;
                    key = _next[key];
                }
            }
        }

        return found;
    }

    /// <summary>Keys in one cell only; the cheapest possible query.</summary>
    public int QueryCell(ushort cell, Span<int> into)
    {
        int found = 0;
        int key = _head[cell];
        while (key >= 0)
        {
            if (found < into.Length)
            {
                into[found] = key;
            }

            found++;
            key = _next[key];
        }

        return found;
    }

    public void Clear()
    {
        Array.Fill(_head, -1);
        Array.Fill(_next, -1);
        Count = 0;
    }

    /// <summary>Squared X/Z distance — the comparison every interest test actually wants.</summary>
    public static float HorizontalDistanceSquared(in Vector3 a, in Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz);
    }

    private static int AxisIndex(float metres)
    {
        if (!float.IsFinite(metres))
        {
            return Dimension / 2;
        }

        int index = (int)MathF.Floor((metres + HalfExtentMetres) / CellMetres);
        return Math.Clamp(index, 0, Dimension - 1);
    }

    private void CheckKey(int key)
    {
        if ((uint)key >= (uint)_capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(key), $"Grid key {key} outside 0..{_capacity - 1}.");
        }
    }
}
