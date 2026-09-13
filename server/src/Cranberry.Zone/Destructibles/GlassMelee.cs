using System.Numerics;
using System.Text.Json;
using Cranberry.Zone.Combat;

namespace Cranberry.Zone.Destructibles;

public readonly record struct GlassMeleeHit(uint ObjectId, float Distance);

/// <summary>Actual planar window bounds, transformed by each August Z2 placement.</summary>
public readonly record struct GlassPane(uint ObjectId, Vector3 Center, Vector3 Normal, Vector3 Up,
    float HalfHeight, float HalfWidth)
{
    public const float FistRadius = 0.12f; // Server contact tolerance, not a retail constant.

    public float? Contact(Vector3 origin, Vector3 direction)
    {
        float denominator = Vector3.Dot(direction, Normal);
        if (!float.IsFinite(denominator) || MathF.Abs(denominator) < 0.0001f) return null;
        float distance = Vector3.Dot(Center - origin, Normal) / denominator;
        if (!float.IsFinite(distance) || distance < 0 || distance > MeleeArm.MeleeRange) return null;
        Vector3 offset = origin + direction * distance - Center;
        return MathF.Abs(Vector3.Dot(offset, Up)) <= HalfHeight + FistRadius
            && MathF.Abs(Vector3.Dot(offset, Vector3.Cross(Normal, Up))) <= HalfWidth + FistRadius
            ? distance : null;
    }
}

public sealed class GlassMeleeCatalog
{
    private const float CellSize = 16;
    public const float StrikeHeight = 1.2f; // Server chest-height contact policy.
    private static readonly Lazy<GlassMeleeCatalog> DefaultData = new(LoadDefault);
    public static GlassMeleeCatalog Default => DefaultData.Value;
    private readonly Dictionary<(int X, int Z), List<GlassPane>> _cells = [];
    public int Count { get; }

    public GlassMeleeCatalog(IEnumerable<GlassPane> panes)
    {
        foreach (var pane in panes)
        {
            Count++;
            Vector3 right = Vector3.Cross(pane.Normal, pane.Up);
            Vector3 extent = Vector3.Abs(pane.Up) * pane.HalfHeight + Vector3.Abs(right) * pane.HalfWidth;
            extent += new Vector3((float)MeleeArm.MeleeRange + GlassPane.FistRadius);
            var min = Cell(pane.Center - extent);
            var max = Cell(pane.Center + extent);
            for (int x = min.X; x <= max.X; x++)
                for (int z = min.Z; z <= max.Z; z++)
                {
                    if (!_cells.TryGetValue((x, z), out var list)) _cells[(x, z)] = list = [];
                    list.Add(pane);
                }
        }
    }

    private static (int X, int Z) Cell(Vector3 position) =>
        ((int)MathF.Floor(position.X / CellSize), (int)MathF.Floor(position.Z / CellSize));

    public GlassMeleeHit? Find(Vector3 feet, float? heading, DestructibleWorld world, float pitch = 0)
    {
        if (!float.IsFinite(feet.LengthSquared()) || heading is not float yaw || !float.IsFinite(yaw)
            || !float.IsFinite(pitch) || MathF.Abs(pitch) > MathF.PI / 2 + 0.01f
            || !_cells.TryGetValue(Cell(feet), out var panes)) return null;
        var origin = feet + Vector3.UnitY * StrikeHeight;
        // August lookInfo uses signed pitch, not a quaternion. The same 2 m ray now
        // reaches sloped warehouse panes below the player's feet when looking down.
        pitch = Math.Clamp(pitch, -MathF.PI / 2, MathF.PI / 2);
        float horizontal = MathF.Cos(pitch);
        var direction = new Vector3(MathF.Sin(yaw) * horizontal, MathF.Sin(pitch), MathF.Cos(yaw) * horizontal);
        GlassMeleeHit? best = null;
        foreach (var pane in panes)
            if (!world.IsDestroyed(pane.ObjectId) && pane.Contact(origin, direction) is float distance
                && (best is null || distance < best.Value.Distance)) best = new(pane.ObjectId, distance);
        return best;
    }

    private static GlassMeleeCatalog LoadDefault()
    {
        const string relative = "Data/Destructibles/z2-glass-panes.json";
        string path = Path.Combine(AppContext.BaseDirectory, relative);
        if (!File.Exists(path))
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "src/Cranberry.Zone", relative);
                if (File.Exists(candidate)) { path = candidate; break; }
            }
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var panes = new List<GlassPane>();
        foreach (var p in doc.RootElement.GetProperty("panes").EnumerateArray())
            panes.Add(new(p[0].GetUInt32(), new(p[1].GetSingle(), p[2].GetSingle(), p[3].GetSingle()),
                new(p[4].GetSingle(), p[5].GetSingle(), p[6].GetSingle()),
                new(p[7].GetSingle(), p[8].GetSingle(), p[9].GetSingle()), p[10].GetSingle(), p[11].GetSingle()));
        return new(panes);
    }
}
