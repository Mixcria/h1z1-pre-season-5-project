using Cranberry.Zone;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Inventory;

public sealed class GrenadeSlotTests
{
    private static PlayerInventory Fresh()
    {
        ulong guid = 0x8000;
        var inventory = new PlayerInventory(0x1234, () => ++guid,
            new InventoryOptions { StarterOutfit = [], WieldFirstWeapon = false });
        inventory.Bootstrap();
        return inventory;
    }

    [Theory]
    [InlineData(65u)]
    [InlineData(2235u)]
    [InlineData(2236u)]
    [InlineData(2237u)]
    [InlineData(14u)]
    public void ThrowableCanBePickedUpAndDrawnAlongsideThreeGuns(uint definition)
    {
        var inventory = Fresh();
        foreach (uint gun in new uint[] { 1889, 2229, 1374 })
            Assert.Equal(InventoryPlacementKind.LoadoutSlot, inventory.TryPickUp(gun, 1, out _).Kind);
        var guns = SurvivorLoadout.WeaponWheelOrder.ToArray()
            .Select(slot => inventory.LoadoutSlots[slot].Guid).ToArray();

        var placement = inventory.TryPickUp(definition, 1, out var grenade);
        Assert.Equal(InventoryPlacementKind.LoadoutSlot, placement.Kind);
        Assert.Equal(SurvivorLoadout.Grenades, placement.LoadoutSlotId);
        Assert.True(inventory.TrySelectLoadoutSlot(SurvivorLoadout.Grenades, out _));
        Assert.Equal(grenade!.Guid, inventory.WieldedItemGuid);
        Assert.Equal(SurvivorLoadout.Grenades, inventory.ToLoadoutSlots().CurrentSlotId);
        Assert.Equal(guns, SurvivorLoadout.WeaponWheelOrder.ToArray()
            .Select(slot => inventory.LoadoutSlots[slot].Guid).ToArray());
        Assert.Equal(new uint[] { SurvivorLoadout.Grenades },
            InventoryAutoAssign.SupportingLoadoutSlots(definition));

        // The slot change must still deliver a resolvable native throwable fire mode.
        var session = new WeaponSession(WeaponStageOptions.Default);
        Assert.NotNull(session.CreateTail(definition));
        Assert.True(AugustThrowables.TryGet(definition, out _));
    }

    [Fact]
    public void FinalThrowClearsOnlyItsSlotAndPreservesTheCarriedReserve()
    {
        var inventory = Fresh();
        inventory.TryPickUp(65, 1, out var grenade);
        Assert.Equal(InventoryPlacementKind.Container, inventory.TryPickUp(65, 1, out var reserve).Kind);
        Assert.Equal(1u, grenade!.Count); // August grenades have MAX_STACK_SIZE=1.
        Assert.NotEqual(grenade.Guid, reserve!.Guid);
        Assert.True(inventory.TrySelectLoadoutSlot(SurvivorLoadout.Grenades, out _));
        Assert.Equal(1u, inventory.RemoveUnits(grenade.Guid, 1));
        Assert.False(inventory.LoadoutSlots.ContainsKey(SurvivorLoadout.Grenades));
        Assert.Equal(1u, reserve.Count);
        Assert.Equal(inventory.BaseBag!.Guid, reserve.ContainerGuid);
        Assert.Equal(SurvivorLoadout.Grenades,
            ItemVerbs.Equip(inventory, reserve, ItemUseOptionKind.EquipItem).BoundLoadoutSlotId);
        Assert.Equal(SurvivorLoadout.Fists, inventory.CurrentLoadoutSlotId);
        Assert.Equal(0ul, inventory.WieldedItemGuid);
        Assert.True(inventory.TrySelectLoadoutSlot(SurvivorLoadout.Binoculars, out _));
        Assert.Equal(PlayerInventory.SurvivorBinocularsItemDefinitionId,
            inventory.LoadoutSlots[SurvivorLoadout.Binoculars].DefinitionId);
        Assert.True(inventory.TrySelectLoadoutSlot(SurvivorLoadout.Fists, out _));
        Assert.False(inventory.EquipmentSlots.ContainsKey(BodySlots.RightHand));
    }

    [Fact]
    public void HotkeysRetainAugustRequiredFistsAndSeparateOptics()
    {
        Assert.True(LoadoutSlotTable.TryGet(17, SurvivorLoadout.Grenades, out var grenade));
        Assert.True(LoadoutSlotTable.TryGet(17, SurvivorLoadout.Binoculars, out var binoculars));
        Assert.True(LoadoutSlotTable.TryGet(17, SurvivorLoadout.Fists, out var fists));
        Assert.Equal("Slot4", grenade.SlotInputAction);
        Assert.Equal("Slot5", binoculars.SlotInputAction);
        Assert.Equal("Slot6", fists.SlotInputAction);
        Assert.True(fists.Required);
        Assert.Equal(85u, fists.ItemId);
        Assert.False(fists.Visible);
    }
}
