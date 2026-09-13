using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed class VehicleInventoryTests
{
    private static PlayerInventory Player(bool backpack = true)
    {
        ulong guid = 500;
        var inventory = new PlayerInventory(1001, () => ++guid);
        inventory.Bootstrap();
        if (backpack) inventory.TryPickUp(2124, 1, out _);
        return inventory;
    }

    [Theory]
    [InlineData(1u, 4u, 1111141u)]
    [InlineData(2u, 6u, 1111292u)]
    [InlineData(3u, 8u, 1111294u)]
    [InlineData(5u, 14u, 1111611u)]
    public void EachFamilyHasCompatibleComponentsCargoAndNativeTurboEntry(uint family, uint loadout, uint turbo)
    {
        var inventory = new VehicleInventory(100, 1, family);
        Assert.Equal(loadout, inventory.LoadoutId);
        Assert.True(inventory.HasEngineParts);
        Assert.True(inventory.HasTurbo);
        foreach (var item in inventory.Items.Where(i => i.ContainerGuid == PlayerInventory.EquippedContainerGuid))
        {
            Assert.True(InventoryItemFacts.TryGet(item.DefinitionId, out var fact));
            Assert.Contains(fact.ItemClass, LoadoutSlotTable.ItemClasses(loadout, item.SlotId));
        }
        if (family == 5)
            Assert.DoesNotContain(inventory.Items, i => i.ContainerGuid == inventory.CargoGuid);
        else
        {
            Assert.Contains(inventory.Items, i => i.DefinitionId == 3460 && i.ContainerGuid == inventory.CargoGuid);
            Assert.Contains(inventory.Items, i => i.DefinitionId == 73 && i.ContainerGuid == inventory.CargoGuid);
        }
        var reader = new PacketReader(inventory.AbilityManager(true));
        Assert.Equal(0xa0, reader.ReadByte());
        Assert.Equal(6, reader.ReadByte());
        uint count = reader.ReadUInt32();
        bool found = false;
        for (int index = 0; index < count; index++)
        {
            uint key = reader.ReadUInt32();
            Assert.Equal(key, reader.ReadUInt32());
            Assert.Equal(1u, reader.ReadUInt32());
            uint ability = reader.ReadUInt32();
            Assert.Equal(ability, reader.ReadUInt32());
            Assert.Equal(0u, reader.ReadUInt32());
            Assert.Equal(2u, reader.ReadUInt32());
            reader.ReadUInt32();
            Assert.Equal(0xc0, reader.ReadByte());
            if (key == 12) { Assert.Equal(turbo, ability); found = true; }
        }
        Assert.True(found);
        Assert.Equal(new byte[] { 0xa0, 6, 0, 0, 0, 0 }, inventory.AbilityManager(false));
    }

    [Fact]
    public void TakingAndReinstallingBatteryIsAtomicAndNeverDuplicatesTheOldGuid()
    {
        var player = Player();
        var vehicle = new VehicleInventory(100, 1, 3);
        var battery = Assert.Single(vehicle.Items, i => i.SlotId == 33);
        Assert.True(vehicle.TakeTo(player, battery.ItemGuid, 1, out var received));
        Assert.False(vehicle.HasEngineParts);
        Assert.False(vehicle.TakeTo(player, battery.ItemGuid, 1, out _));
        Assert.True(vehicle.DepositFrom(player, received!.Guid, 1, PlayerInventory.EquippedContainerGuid, 33));
        Assert.True(vehicle.HasEngineParts);
        Assert.DoesNotContain(received.Guid, player.Items.Keys);
        Assert.Single(vehicle.Items, i => i.DefinitionId == 1696);
    }

    [Fact]
    public void FirstAidStackCanBeStoredInVehicleAndReturnsToTheDedicatedSlot()
    {
        var player = Player();
        var vehicle = new VehicleInventory(100, 1, 3);
        player.TryPickUp(2424, 3, out var kits);
        Assert.True(vehicle.DepositFrom(player, kits!.Guid, 2, vehicle.CargoGuid, -1));
        Assert.Equal(1u, player.LoadoutSlots[SurvivorLoadout.QuickUse2].Count);
        var cargo = Assert.Single(vehicle.Items, item => item.DefinitionId == 2424);
        Assert.Equal(2u, cargo.Count);
        Assert.True(vehicle.TakeTo(player, cargo.ItemGuid, 2, out var returned));
        Assert.Same(kits, returned);
        Assert.Equal(3u, returned!.Count);
        Assert.Single(player.Items.Values, item => item.DefinitionId == 2424);
        Assert.DoesNotContain(player.BaseBag!.Slots.Values, item => item.DefinitionId == 2424);
        Assert.False(vehicle.TakeTo(player, cargo.ItemGuid, 2, out _));
    }

    [Fact]
    public void FullPlayerBagAndInvalidInstallLeaveBothInventoriesIntact()
    {
        var player = Player(false); // 200 bulk, less than the 300-bulk battery.
        var vehicle = new VehicleInventory(100, 1, 3);
        var battery = Assert.Single(vehicle.Items, i => i.SlotId == 33);
        Assert.False(vehicle.TakeTo(player, battery.ItemGuid, 1, out _));
        Assert.True(vehicle.TryGet(battery.ItemGuid, out _));
        var key = Assert.Single(vehicle.Items, i => i.DefinitionId == 3460);
        Assert.True(vehicle.TakeTo(player, key.ItemGuid, 1, out var received));
        Assert.False(vehicle.DepositFrom(player, received!.Guid, 1, PlayerInventory.EquippedContainerGuid, 17));
        Assert.True(player.Items.ContainsKey(received.Guid));
        Assert.True(vehicle.HasTurbo);
    }

    [Fact]
    public void TurboCanBeMovedToCargoAndBackAndRemovedEntryIsNotAdvertised()
    {
        var vehicle = new VehicleInventory(100, 1, 3);
        var turbo = Assert.Single(vehicle.Items, i => i.SlotId == 17);
        int length = vehicle.AbilityManager(true).Length;
        Assert.True(vehicle.MoveWithin(turbo.ItemGuid, 1, vehicle.CargoGuid, -1));
        Assert.False(vehicle.HasTurbo);
        Assert.Equal(length - 33, vehicle.AbilityManager(true).Length);
        Assert.True(vehicle.MoveWithin(turbo.ItemGuid, 1, PlayerInventory.EquippedContainerGuid, 17));
        Assert.True(vehicle.HasTurbo);
        Assert.Equal(length, vehicle.AbilityManager(true).Length);
    }

    [Theory]
    [InlineData(1u, 9u, 99998u, 1344u, 1111153u)]
    [InlineData(2u, 1728u, 1111291u, 1712u, 1111285u)]
    [InlineData(3u, 1730u, 1111293u, 1722u, 1111288u)]
    [InlineData(5u, 2595u, 1111608u, 2594u, 1111607u)]
    public void NativeManagerKeysDistinguishRemovableHeadlightsFromTheDefaultMotor(
        uint family, uint headlightsItem, uint headlightsAbility, uint motorItem, uint motorAbility)
    {
        var inventory = new VehicleInventory(100, 1, family);
        var headlights = Assert.Single(inventory.Items, item => item.SlotId == 18);
        var motor = Assert.Single(inventory.Items, item => item.SlotId == 19);
        Assert.Equal(headlightsItem, headlights.DefinitionId);
        Assert.Equal(motorItem, motor.DefinitionId);

        var reader = new PacketReader(inventory.AbilityManager(true));
        reader.Skip(2);
        uint count = reader.ReadUInt32();
        var entries = new Dictionary<uint, (uint Ability, uint Item)>();
        for (int index = 0; index < count; index++)
        {
            uint key = reader.ReadUInt32();
            Assert.Equal(key, reader.ReadUInt32());
            Assert.Equal(1u, reader.ReadUInt32());
            uint ability = reader.ReadUInt32();
            Assert.Equal(ability, reader.ReadUInt32());
            Assert.Equal(0u, reader.ReadUInt32());
            Assert.Equal(2u, reader.ReadUInt32());
            entries.Add(key, (ability, reader.ReadUInt32()));
            Assert.Equal(0xc0, reader.ReadByte());
        }
        Assert.True(reader.AtEnd);
        Assert.Equal((headlightsAbility, headlightsItem), entries[10]);
        Assert.Equal((motorAbility, motorItem), entries[11]);

        int originalLength = inventory.AbilityManager(true).Length;
        Assert.True(inventory.MoveWithin(headlights.ItemGuid, 1, inventory.CargoGuid, -1));
        Assert.False(inventory.HasSlot(18));
        Assert.True(inventory.HasEngineParts);
        Assert.Equal(originalLength - 33, inventory.AbilityManager(true).Length);
        Assert.True(inventory.MoveWithin(headlights.ItemGuid, 1, PlayerInventory.EquippedContainerGuid, 18));
        Assert.True(inventory.HasSlot(18));
        Assert.True(inventory.HasEngineParts);
        Assert.Equal(originalLength, inventory.AbilityManager(true).Length);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void FirstVehicleKeyFillsTheNativeKeySlotEvenWithAFullBag(uint family)
    {
        var player = Player(false);
        player.TryPickUp(1429, (uint)(player.Capacity.Max / 2), out _);
        Assert.Equal(player.Capacity.Max, player.Capacity.Used);
        var vehicle = new VehicleInventory(100, 1, family);
        var key = Assert.Single(vehicle.Items, i => i.DefinitionId == AugustIgnitionFacts.KeyItemId);

        Assert.True(vehicle.TakeTo(player, key.ItemGuid, 1, out var received));

        Assert.NotNull(received);
        Assert.Same(received, player.LoadoutSlots[SurvivorLoadout.VehicleKey]);
        Assert.Equal(0UL, received.ContainerGuid);
        Assert.Equal(0u, received.EquipmentSlotId);
        Assert.Equal(SurvivorLoadout.Fists, player.CurrentLoadoutSlotId);
        Assert.False(vehicle.TryGet(key.ItemGuid, out _));
        Assert.Equal(player.Capacity.Max, player.Capacity.Used);
        var itemRecord = received.ToRecord(player.CharacterGuid);
        Assert.Equal(PlayerInventory.EquippedContainerGuid, itemRecord.ContainerGuid);
        Assert.Equal(48u, itemRecord.SlotId);
        var slot = Assert.Single(player.ToLoadoutSlots().Slots, s => s.Key == 48).Slot;
        Assert.Equal(received.Guid, slot.ItemGuid);
        Assert.Equal(AugustIgnitionFacts.KeyItemId, slot.ItemDefinitionId);
    }

    [Fact]
    public void TakingASpareVehicleKeyKeepsTheFirstEquippedAndPutsTheSpareInTheBag()
    {
        var player = Player(false);
        player.TryPickUp(AugustIgnitionFacts.KeyItemId, 1, out var first);
        var vehicle = new VehicleInventory(100, 1, 3);
        var key = Assert.Single(vehicle.Items, i => i.DefinitionId == AugustIgnitionFacts.KeyItemId);
        int previousBulk = player.Capacity.Used;

        Assert.True(vehicle.TakeTo(player, key.ItemGuid, 1, out var spare));

        Assert.Same(first, player.LoadoutSlots[SurvivorLoadout.VehicleKey]);
        Assert.NotNull(spare);
        Assert.NotEqual(first!.Guid, spare.Guid);
        Assert.Equal(player.BaseBag!.Guid, spare.ContainerGuid);
        Assert.Equal(0u, spare.LoadoutSlotId);
        Assert.Equal(previousBulk + 1, player.Capacity.Used);
        Assert.Equal(2, player.Items.Values.Count(i => i.DefinitionId == AugustIgnitionFacts.KeyItemId));
        Assert.False(vehicle.TakeTo(player, key.ItemGuid, 1, out _));
    }

    [Fact]
    public void ASpareKeyStaysInTheVehicleWhenThePlayersBagIsFull()
    {
        var player = Player(false);
        player.TryPickUp(AugustIgnitionFacts.KeyItemId, 1, out var first);
        player.TryPickUp(1429, (uint)(player.Capacity.Max / 2), out _);
        Assert.Equal(player.Capacity.Max, player.Capacity.Used);
        var vehicle = new VehicleInventory(100, 1, 3);
        var key = Assert.Single(vehicle.Items, i => i.DefinitionId == AugustIgnitionFacts.KeyItemId);
        int count = player.Items.Count;

        Assert.False(vehicle.TakeTo(player, key.ItemGuid, 1, out var refused));

        Assert.Null(refused);
        Assert.Equal(count, player.Items.Count);
        Assert.Same(first, player.LoadoutSlots[SurvivorLoadout.VehicleKey]);
        Assert.True(vehicle.TryGet(key.ItemGuid, out var unchanged));
        Assert.Equal(key, unchanged);
    }

    [Fact]
    public void DifferentCarsCannotShareItemGuidsAndTheirAccessPayloadContainsAllItems()
    {
        var first = new VehicleInventory(100, 1, 3);
        var second = new VehicleInventory(101, 2, 3);
        Assert.Empty(first.Items.Select(i => i.ItemGuid).Intersect(second.Items.Select(i => i.ItemGuid)));
        using var writer = new PacketWriter();
        new BeginCharacterAccess(100, 1001, Items: first.Items).WriteTo(writer);
        var bytes = writer.Written.ToArray();
        Assert.Equal(32 + first.Items.Count * 63, bytes.Length);
        Assert.Equal((uint)first.Items.Count, BitConverter.ToUInt32(bytes, 24));
        Assert.Equal(92u, BitConverter.ToUInt32(bytes, bytes.Length - 4));
    }

    [Fact]
    public void MissingSparkPlugsBlockIgnitionEvenWhenKeyRequirementIsOff()
    {
        var car = new MatchVehicle(100, 1, VehicleRoster.LoadDefault().Require(3), Vector3.Zero, 0, 100000, 5000);
        var fleet = new VehicleFleet(VehicleRoster.LoadDefault());
        fleet.Add(car);
        fleet.TryEnter(100, 1001, 0, 0, out _, out _);
        var plugs = Assert.Single(car.Inventory.Items, i => i.SlotId == 34);
        Assert.True(car.Inventory.MoveWithin(plugs.ItemGuid, 1, car.Inventory.CargoGuid, -1));
        var ignition = new VehicleIgnition();
        var outcome = ignition.TryStart(car, 1001, false, 1000, 1, new(), new() { Required = false });
        Assert.Equal(VehicleIgnitionResult.MissingComponents, outcome.Result);
        Assert.False(car.EngineOn);
    }
}
