namespace Cranberry.Zone.Loot;

/// <summary>One match's consumed map markers. Mutated on the gateway listener thread.</summary>
public sealed class SharedLootClaims
{
    private readonly HashSet<LootStreamKey> _taken = [];
    private readonly Dictionary<LootStreamKey, uint> _remaining = [];
    public int Count => _taken.Count;
    public bool IsTaken(in LootStreamKey key) => _taken.Contains(key);
    public bool TryClaim(in LootStreamKey key)
    {
        _remaining.Remove(key);
        return _taken.Add(key);
    }

    public uint RemainingCount(in LootStreamKey key, uint original) =>
        IsTaken(key) ? 0 : _remaining.GetValueOrDefault(key, original);

    public void NoteRemaining(in LootStreamKey key, uint remaining)
    {
        if (remaining == 0 || IsTaken(key)
            || (_remaining.TryGetValue(key, out uint current) && remaining > current))
            throw new ArgumentOutOfRangeException(nameof(remaining));
        _remaining[key] = remaining;
    }
}
