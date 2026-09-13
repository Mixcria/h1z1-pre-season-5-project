using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Inventory;

/// <summary>
/// The client-originated hotbar selection path: a loadout binding stays in its tile while its
/// equipment binding moves between the passive stow peg and RHand. Fists is a loadout-only
/// empty-hand selection: the August client supplies its baseline hand attachment itself.
/// </summary>
public sealed class LoadoutSelectionTests
{
    private const uint Ar15 = 10;

    private static PlayerInventory Fresh()
    {
        ulong next = 0xA000;
        var inventory = new PlayerInventory(
            0xDEAD_BEEF,
            () => ++next,
            new InventoryOptions
            {
                StarterOutfit = [],
                WieldFirstWeapon = false,
            });
        inventory.Bootstrap();
        return inventory;
    }

    [Fact]
    public void BootstrapKeepsFistsLoadoutOnlyAndSelectsTheirTile()
    {
        PlayerInventory inventory = Fresh();

        InventoryItemInstance fists = inventory.LoadoutSlots[SurvivorLoadout.Fists];
        Assert.Equal(PlayerInventory.SurvivorFistsItemDefinitionId, fists.DefinitionId);
        Assert.Equal(0u, fists.EquipmentSlotId);
        Assert.Equal(0ul, inventory.WieldedItemGuid);
        Assert.Equal(SurvivorLoadout.Fists, inventory.CurrentLoadoutSlotId);
        Assert.False(inventory.EquipmentSlots.ContainsKey(BodySlots.RightHand));
        Assert.False(inventory.HandIsFists);
    }

    [Fact]
    public void SelectingAStowedRifleMovesItIntoTheActiveHand()
    {
        PlayerInventory inventory = Fresh();
        InventoryPlacement placement = inventory.TryPickUp(Ar15, 1, out InventoryItemInstance? rifle);

        Assert.Equal(InventoryPlacementKind.LoadoutSlot, placement.Kind);
        Assert.NotNull(rifle);
        Assert.NotEqual(BodySlots.RightHand, rifle!.EquipmentSlotId);
        Assert.True(inventory.TrySelectLoadoutSlot(rifle.LoadoutSlotId, out LoadoutSelection? selection));

        Assert.NotNull(selection);
        Assert.Equal(SurvivorLoadout.Fists, selection!.PreviousLoadoutSlotId);
        Assert.Equal(rifle.LoadoutSlotId, selection.SelectedLoadoutSlotId);
        Assert.Null(selection.PreviousHandItem);
        Assert.Same(rifle, selection.SelectedItem);
        Assert.Equal(0u, selection.PreviousHandStowedBodySlot);
        Assert.Equal(rifle.Guid, inventory.WieldedItemGuid);
        Assert.Equal(rifle.LoadoutSlotId, inventory.CurrentLoadoutSlotId);
        Assert.Equal(BodySlots.RightHand, rifle.EquipmentSlotId);
        Assert.Equal(rifle, inventory.LoadoutSlots[rifle.LoadoutSlotId]);
        Assert.False(inventory.HandIsFists);
    }

    [Fact]
    public void SelectingFistsAgainStowsTheFormerRifleAndRestoresTheEmptyHand()
    {
        PlayerInventory inventory = Fresh();
        InventoryItemInstance fists = inventory.LoadoutSlots[SurvivorLoadout.Fists];
        inventory.TryPickUp(Ar15, 1, out InventoryItemInstance? rifle);
        Assert.NotNull(rifle);
        Assert.True(inventory.TrySelectLoadoutSlot(rifle!.LoadoutSlotId, out _));

        Assert.True(inventory.TrySelectLoadoutSlot(SurvivorLoadout.Fists, out LoadoutSelection? selection));

        Assert.NotNull(selection);
        Assert.Equal(rifle.Guid, selection!.PreviousHandItem!.Guid);
        Assert.Equal(SurvivorLoadout.Fists, selection.SelectedLoadoutSlotId);
        Assert.Equal(76u, selection.PreviousHandStowedBodySlot);
        Assert.Equal(SurvivorLoadout.Fists, inventory.CurrentLoadoutSlotId);
        Assert.Equal(0ul, inventory.WieldedItemGuid);
        Assert.Equal(0u, fists.EquipmentSlotId);
        Assert.False(inventory.EquipmentSlots.ContainsKey(BodySlots.RightHand));
        Assert.Equal(76u, rifle.EquipmentSlotId);
        Assert.Equal(rifle, inventory.EquipmentSlots[76]);
        Assert.False(inventory.HandIsFists);
    }

    [Fact]
    public void RemovingTheDrawnRifleReturnsTheHandAndCurrentSlotToFists()
    {
        PlayerInventory inventory = Fresh();
        InventoryItemInstance fists = inventory.LoadoutSlots[SurvivorLoadout.Fists];
        inventory.TryPickUp(Ar15, 1, out InventoryItemInstance? rifle);
        Assert.NotNull(rifle);
        Assert.True(inventory.TrySelectLoadoutSlot(rifle!.LoadoutSlotId, out _));

        Assert.Equal(1u, inventory.RemoveUnits(rifle.Guid, 1));

        Assert.Equal(SurvivorLoadout.Fists, inventory.CurrentLoadoutSlotId);
        Assert.Equal(0ul, inventory.WieldedItemGuid);
        Assert.Equal(0u, fists.EquipmentSlotId);
        Assert.False(inventory.EquipmentSlots.ContainsKey(BodySlots.RightHand));
        Assert.False(inventory.HandIsFists);
    }

    [Fact]
    public void SelectingAnEmptyOrInvalidTilePreservesTheCurrentHand()
    {
        PlayerInventory inventory = Fresh();
        ulong beforeGuid = inventory.WieldedItemGuid;
        uint beforeSlot = inventory.CurrentLoadoutSlotId;

        Assert.False(inventory.TrySelectLoadoutSlot(SurvivorLoadout.Wheel1, out LoadoutSelection? empty));
        Assert.Null(empty);
        Assert.False(inventory.TrySelectLoadoutSlot(9999, out LoadoutSelection? invalid));
        Assert.Null(invalid);

        Assert.Equal(beforeGuid, inventory.WieldedItemGuid);
        Assert.Equal(beforeSlot, inventory.CurrentLoadoutSlotId);
        Assert.False(inventory.EquipmentSlots.ContainsKey(BodySlots.RightHand));
        Assert.False(inventory.HandIsFists);
    }

    [Theory]
    [InlineData(10u, "Weapon_M16A4_3P.adr")]
    [InlineData(1991u, "Weapon_Pistol_380Auto_3P.adr")]
    [InlineData(2229u, "Weapon_AK47_3P.adr")]
    public void TheHeldMeshCatalogResolvesTheHotbarGunVariants(uint itemDefinitionId, string expectedModel)
    {
        Assert.True(InventoryItemFacts.TryGet(itemDefinitionId, out InventoryItemFact fact));

        Assert.True(AugustWornVisuals.TryResolveMesh(
            itemDefinitionId,
            CharacterVisuals.Male,
            fact.ModelName,
            out string model,
            out uint shaderParameterGroupId));

        Assert.Equal(expectedModel, model);
        Assert.NotEqual(0u, shaderParameterGroupId);
    }
}
