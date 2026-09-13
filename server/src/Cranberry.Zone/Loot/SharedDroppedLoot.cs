using System.Numerics;

namespace Cranberry.Zone.Loot;

/// <summary>Match-owned player drops indexed in 64-metre cells; client object ids stay local.</summary>
public sealed class SharedDroppedLoot
{
    private ulong _next = 0x7000_0000_0000_0001;
    private readonly Dictionary<(int X, int Z), Dictionary<LootStreamKey, GroundLootItem>> _cells = [];
    private readonly PriorityQueue<(LootStreamKey Key, Vector3 Position), long> _expiry = new();
    /// <summary>Unique shared drops, not per-viewer replicas. Read on the world owner thread.</summary>
    public int Count => _cells.Values.Sum(cell => cell.Count);
    public LootStreamKey Add(GroundLootItem item, long expiresAtMs = 0)
    {
        var key = LootStreamKey.ForDropped(_next++);
        var cell = Cell(item.Position);
        if (!_cells.TryGetValue(cell, out var items)) _cells.Add(cell, items = []);
        items.Add(key, item);
        if (expiresAtMs > 0) _expiry.Enqueue((key, item.Position), expiresAtMs);
        return key;
    }

    public IEnumerable<LootStreamKey> Expire(long nowMs)
    {
        while (_expiry.TryPeek(out var entry, out long due) && due <= nowMs)
        {
            _expiry.Dequeue();
            if (!_cells.TryGetValue(Cell(entry.Position), out var items) || !items.ContainsKey(entry.Key)) continue;
            Remove(entry.Key, entry.Position);
            yield return entry.Key;
        }
    }
    public void Remove(LootStreamKey key, Vector3 position)
    {
        var cell = Cell(position);
        if (!_cells.TryGetValue(cell, out var items)) return;
        items.Remove(key);
        if (items.Count == 0) _cells.Remove(cell);
    }
    public IEnumerable<KeyValuePair<LootStreamKey, GroundLootItem>> Nearby(Vector3 position, float radius)
    {
        if (!float.IsFinite(radius) || radius <= 0 || radius > 1000) yield break;
        var low = Cell(position - new Vector3(radius, 0, radius));
        var high = Cell(position + new Vector3(radius, 0, radius));
        for (int x = low.X; x <= high.X; x++)
        for (int z = low.Z; z <= high.Z; z++)
        {
            if (!_cells.TryGetValue((x, z), out var items)) continue;
            foreach (var item in items)
            {
                Vector3 delta = item.Value.Position - position;
                if (delta.X * delta.X + delta.Z * delta.Z <= radius * radius) yield return item;
            }
        }
    }
    private static (int X, int Z) Cell(Vector3 position) =>
        ((int)MathF.Floor(position.X / 64), (int)MathF.Floor(position.Z / 64));
}
