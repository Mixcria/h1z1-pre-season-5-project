using Cranberry.Zone.Inventory;

namespace Cranberry.Zone.Loot;

/// <summary>Shared death loot. Mutated only on the gateway listener thread.</summary>
public sealed class BodyBag
{
    // August Models.txt: Common_Props_LootBagLRG, "Bag left behind when a player is killed".
    public const uint ModelId = 9581;
    public const uint ItemDefinitionId = 1501;
    public const uint ContainerDefinitionId = 51;
    public System.Numerics.Vector3 Position { get; set; }
    public Dictionary<ulong, InventoryItem> Items { get; } = [];

    private readonly Dictionary<ulong, uint> _gameplayDefinitions = [];

    public void Add(ulong guid, uint definition, uint count, uint skinDefinition = 0)
    {
        if (count == 0 || definition is PlayerInventory.SurvivorFistsItemDefinitionId or 3156) return;
        if (definition is PlayerInventory.StarterBandageItemDefinitionId or 2424)
        {
            var existing = Items.Values.FirstOrDefault(item => GameplayDefinitionFor(item.ItemGuid) == definition);
            if (existing is not null && (ulong)existing.Count + count <= 9999)
            {
                Items[existing.ItemGuid] = existing with { Count = existing.Count + count };
                return;
            }
        }
        Items.Add(guid, new InventoryItem(skinDefinition == 0 ? definition : skinDefinition, guid, count, 0, 0,
            ContainerDefinitionId, (uint)Items.Count + 1));
        _gameplayDefinitions.Add(guid, definition);
    }

    public uint GameplayDefinitionFor(ulong guid) => _gameplayDefinitions.GetValueOrDefault(guid, Items[guid].DefinitionId);

    public void Remove(ulong guid)
    {
        Items.Remove(guid);
        _gameplayDefinitions.Remove(guid);
    }

    public InventoryItem[] Records(ulong owner) => Items.Values.Select(item => item with
    {
        OwnerGuid = owner, ContainerGuid = owner,
    }).ToArray();

    public InitContainers Containers(ulong owner)
    {
        var records = Records(owner);
        return new(owner, [new(43, new(owner, ContainerDefinitionId, owner,
            records.Length == 0 ? 1 : records.Max(i => i.SlotId), records.Select(i => new ContainerItemEntry(i.SlotId, i)).ToArray(),
            uint.MaxValue, 0))]);
    }
}
