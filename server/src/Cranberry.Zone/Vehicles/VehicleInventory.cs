using Cranberry.Protocol;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;

namespace Cranberry.Zone.Vehicles;

/// <summary>Persistent per-car components and cargo, using August loadout/container definitions.</summary>
public sealed class VehicleInventory
{
    private readonly Dictionary<ulong, InventoryItem> _items = [];
    private ulong _nextGuid;
    public ulong CharacterGuid { get; }
    public uint LoadoutId { get; }
    public ulong CargoGuid { get; }
    public ContainerDefinition CargoDefinition { get; }
    public IReadOnlyCollection<InventoryItem> Items => _items.Values;
    public bool HasEngineParts => HasSlot(19) && HasSlot(33) && HasSlot(34);
    public bool HasTurbo => HasSlot(17);
    public bool HasSlot(uint slot) => _items.Values.Any(i => IsEquipped(i) && i.SlotId == slot);
    private static bool IsEquipped(InventoryItem item) => item.ContainerGuid == PlayerInventory.EquippedContainerGuid;
    public static bool IsComponentSlot(uint slot) => slot is 17 or 18 or 19 or 33 or 34 or 35 or 36;
    public static bool IsInstalledComponent(InventoryItem item) => IsEquipped(item) && IsComponentSlot(item.SlotId);

    public VehicleInventory(ulong characterGuid, uint transientId, uint vehicleId)
    {
        CharacterGuid = characterGuid;
        _nextGuid = 0x5600_0000_0000_0000UL | ((ulong)transientId << 20);
        LoadoutId = vehicleId switch { 1 => 4, 2 => 6, 3 => 8, 5 => 14, _ => throw new ArgumentOutOfRangeException(nameof(vehicleId)) };
        foreach (var slot in LoadoutSlotTable.Slots(LoadoutId))
            if (slot.ItemId != 0) AddEquipped(slot.ItemId, slot.SlotId);
        var cargo = _items.Values.Single(i => i.SlotId == 32);
        CargoGuid = cargo.ItemGuid;
        InventoryItemFacts.TryGet(cargo.DefinitionId, out var cargoFact);
        if (!ContainerDefinitionTable.TryGet(cargoFact.Param1, out var definition))
            throw new InvalidOperationException("Vehicle cargo definition missing");
        CargoDefinition = definition;
        AddEquipped(vehicleId switch { 1 => 9u, 2 => 1728u, 3 => 1730u, _ => 2595u }, 18);
        AddEquipped(vehicleId switch { 1 => 90u, 2 => 1729u, 3 => 1731u, _ => 2727u }, 17);
        AddEquipped(1696, 33);
        AddEquipped(1701, 34);
        AddEquipped(1735, 36);
        if (vehicleId != 5)
        {
            AddCargo(3460, 1); // Removable vehicle key; 3717 is the 9999-bulk variant.
            AddCargo(73, 1);   // Spare biofuel, separate from the tank's resource 50.
        }
    }

    private void AddEquipped(uint definitionId, uint slot)
    {
        var item = new InventoryItem(definitionId, ++_nextGuid, 1, CharacterGuid,
            PlayerInventory.EquippedContainerGuid, PlayerInventory.LoadoutContainerDefinitionId, slot);
        _items.Add(item.ItemGuid, item);
    }

    private void AddCargo(uint definitionId, uint count)
    {
        uint slot = 1;
        while (_items.Values.Any(i => !IsEquipped(i) && i.SlotId == slot)) slot++;
        var item = new InventoryItem(definitionId, ++_nextGuid, count, CharacterGuid,
            CargoGuid, CargoDefinition.Id, slot);
        _items.Add(item.ItemGuid, item);
    }

    public bool TryGet(ulong guid, out InventoryItem? item) => _items.TryGetValue(guid, out item);

    public bool ConsumeFuelCan(ulong guid, out InventoryItem? remaining)
    {
        remaining = null;
        if (!_items.TryGetValue(guid, out var item) || IsEquipped(item) || item.Count == 0
            || !AugustFuelFacts.IsRefuelItem(item.DefinitionId)) return false;
        if (item.Count == 1) _items.Remove(guid);
        else _items[guid] = remaining = item with { Count = item.Count - 1 };
        return true;
    }

    public bool TakeTo(PlayerInventory player, ulong guid, uint count, out InventoryItemInstance? received)
    {
        received = null;
        if (!_items.TryGetValue(guid, out var item) || count == 0 || count > item.Count
            || (IsEquipped(item) && !IsComponentSlot(item.SlotId))) return false;
        // TryPickUp performs capacity validation before mutation. Only debit the vehicle on success.
        player.TryPickUp(item.DefinitionId, count, out received);
        if (received is null) return false;
        if (count == item.Count) _items.Remove(guid);
        else _items[guid] = item with { Count = item.Count - count };
        return true;
    }

    public bool DepositFrom(PlayerInventory player, ulong guid, uint count, ulong destination, int slot)
    {
        if (!player.Items.TryGetValue(guid, out var item) || count == 0 || count > item.Count
            || item.Fact.CodeFactory != ItemCodeFactory.Generic) return false;
        if (destination == PlayerInventory.EquippedContainerGuid)
        {
            if (count != 1 || !IsComponentSlot((uint)slot) || HasSlot((uint)slot)
                || !LoadoutSlotTable.ItemClasses(LoadoutId, (uint)slot).Contains(item.Fact.ItemClass)) return false;
            player.RemoveUnits(guid, count);
            AddEquipped(item.DefinitionId, (uint)slot);
            return true;
        }
        if (destination != CargoGuid || count > InventoryStacking.Maximum(item.Fact)) return false;
        var cargo = _items.Values.Where(i => !IsEquipped(i)).ToArray();
        if (cargo.Length >= CargoDefinition.MaximumSlots) return false;
        long bulk = cargo.Sum(i => InventoryItemFacts.TryGet(i.DefinitionId, out var f) ? (long)f.Bulk * i.Count : 0);
        if (CargoDefinition.MaxBulk > 0 && bulk + (long)item.Fact.Bulk * count > CargoDefinition.MaxBulk) return false;
        player.RemoveUnits(guid, count);
        AddCargo(item.DefinitionId, count);
        return true;
    }

    public bool MoveWithin(ulong guid, uint count, ulong destination, int slot)
    {
        if (!_items.TryGetValue(guid, out var item) || count != item.Count
            || (IsEquipped(item) && !IsComponentSlot(item.SlotId))) return false;
        if (destination == PlayerInventory.EquippedContainerGuid)
        {
            if (count != 1 || !IsComponentSlot((uint)slot) || HasSlot((uint)slot)
                || !InventoryItemFacts.TryGet(item.DefinitionId, out var fact)
                || !LoadoutSlotTable.ItemClasses(LoadoutId, (uint)slot).Contains(fact.ItemClass)) return false;
            _items[guid] = item with { ContainerGuid = destination, ContainerDefinitionId = 101, SlotId = (uint)slot };
            return true;
        }
        if (destination != CargoGuid || !IsEquipped(item)) return false;
        long bulk = _items.Values.Where(i => !IsEquipped(i)).Sum(i =>
            InventoryItemFacts.TryGet(i.DefinitionId, out var f) ? (long)f.Bulk * i.Count : 0);
        if (!InventoryItemFacts.TryGet(item.DefinitionId, out var removedFact)
            || CargoDefinition.MaxBulk > 0 && bulk + (long)removedFact.Bulk * count > CargoDefinition.MaxBulk)
            return false;
        uint free = 1;
        while (_items.Values.Any(i => !IsEquipped(i) && i.SlotId == free)) free++;
        if (free > CargoDefinition.MaximumSlots) return false;
        _items[guid] = item with { ContainerGuid = CargoGuid, ContainerDefinitionId = CargoDefinition.Id, SlotId = free };
        return true;
    }

    public SetLoadoutSlots ToLoadout() => new(CharacterGuid, LoadoutId,
        [.. _items.Values.Where(IsEquipped).OrderBy(i => i.SlotId).Select(i =>
            new LoadoutSlotEntry(i.SlotId, new(LoadoutId, i.SlotId, i.ItemGuid, ItemDefinitionId: i.DefinitionId)))], 0);

    public InitContainers ToContainers() => new(CharacterGuid,
        [new ContainerEntry(32, new ContainerRecord(CargoGuid, CargoDefinition.Id, CharacterGuid,
            (uint)CargoDefinition.MaximumSlots,
            [.. _items.Values.Where(i => !IsEquipped(i)).OrderBy(i => i.SlotId).Select(i => new ContainerItemEntry(i.DefinitionId, i))],
            (uint)Math.Max(0, CargoDefinition.MaxBulk),
            (uint)_items.Values.Where(i => !IsEquipped(i)).Sum(i => InventoryItemFacts.TryGet(i.DefinitionId, out var f) ? f.Bulk * i.Count : 0))) ]);

    /// <summary>a0/06: native 140cb77c0 -> 140a55420 -> 140a2c090.
    /// Slot 18 is headlights (key 10); slot 19 is the motor (key 11), per August LoadoutSlots.</summary>
    public byte[] AbilityManager(bool driver)
    {
        using var writer = new PacketWriter(256);
        writer.WriteByte(ZoneOpcodes.AbilitiesBase);
        writer.WriteByte(6);
        var entries = driver ? _items.Values.Where(i => IsEquipped(i)
            && i.SlotId is 18 or 19 or 17 or 36 or 35 && AugustAbilityFacts.AbilityIdOf(i.DefinitionId) != 0).ToArray() : [];
        writer.WriteUInt32((uint)entries.Length);
        foreach (var item in entries)
        {
            uint key = item.SlotId switch { 18 => 10, 19 => 11, 17 => 12, 36 => 13, _ => 14 };
            uint ability = AugustAbilityFacts.AbilityIdOf(item.DefinitionId);
            writer.WriteUInt32(key);
            writer.WriteUInt32(key);
            writer.WriteUInt32(1);
            writer.WriteUInt32(ability);
            writer.WriteUInt32(ability);
            writer.WriteUInt32(0);
            writer.WriteUInt32(2);
            writer.WriteUInt32(item.DefinitionId);
            writer.WriteByte(0xc0);
        }
        return writer.Written.ToArray();
    }
}
