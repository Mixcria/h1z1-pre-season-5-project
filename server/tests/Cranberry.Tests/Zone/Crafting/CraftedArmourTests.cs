using Cranberry.Zone;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Crafting;

public sealed class CraftedArmourTests
{
    // August item facts; ArmourModel documents the makeshift and laminated bands.
    private const uint LaminatedArmour = 2271;

    private static RecipeDefinition MakeshiftRecipe =>
        CraftingCatalog.RetailRecipes.Single(r => r.OutputItemDefinitionId == CraftingCatalog.MakeshiftArmor);

    private static PlayerInventory Inventory(int carryBulk = 1000)
    {
        ulong next = 0x5000;
        var inventory = new PlayerInventory(0x1001, () => ++next, new InventoryOptions
        {
            StarterOutfit = [],
            BaseCarryBulk = carryBulk,
            QuickUseConsumables = false,
        });
        inventory.Bootstrap();
        return inventory;
    }

    private static void GiveIngredients(PlayerInventory inventory, uint crafts = 1)
    {
        foreach (RecipeIngredient ingredient in MakeshiftRecipe.Ingredients)
        {
            inventory.TryPickUp(ingredient.ItemDefinitionId, ingredient.Quantity * crafts, out var item);
            Assert.NotNull(item);
        }
    }

    [Theory]
    [InlineData(LaminatedArmour)]
    [InlineData(2204u)]
    [InlineData(2205u)]
    [InlineData(CraftingCatalog.MakeshiftArmor)]
    public void CraftingSpareArmourPreservesTheWornInstanceAndCarriesTheOutput(uint wornDefinitionId)
    {
        // Fits the crafted vest's 325 bulk, but not the laminated vest's 1000 bulk.
        PlayerInventory inventory = Inventory(carryBulk: 325);
        inventory.TryPickUp(wornDefinitionId, 1, out var worn);
        Assert.NotNull(worn);
        GiveIngredients(inventory);

        CraftOutcome outcome = CraftingService.Craft(inventory, MakeshiftRecipe, 1);

        Assert.True(outcome.Succeeded);
        Assert.Equal(CraftRefusal.None, outcome.Refusal);
        Assert.Same(worn, inventory.LoadoutSlots[SurvivorLoadout.ChestArmor]);
        Assert.Same(worn, inventory.EquipmentSlots[BodySlots.ChestArmor]);
        Assert.Equal(0ul, worn.ContainerGuid);
        var granted = Assert.Single(outcome.Changes, c => c.Kind == CraftItemChangeKind.Granted).Item!;
        Assert.Equal(CraftingCatalog.MakeshiftArmor, granted.DefinitionId);
        Assert.NotEqual(worn.Guid, granted.Guid);
        Assert.Equal(0u, granted.LoadoutSlotId);
        Assert.Equal(0u, granted.EquipmentSlotId);
        Assert.Same(granted, inventory.BaseBag!.Slots[granted.ContainerSlotId]);
        Assert.Equal(inventory.BaseBag.Guid, granted.ToRecord(inventory.CharacterGuid).ContainerGuid);
        Assert.DoesNotContain(outcome.Changes, c => c.ItemGuid == worn.Guid);
        Assert.Equal(325, inventory.Capacity.Used);
    }

    [Fact]
    public void CraftMaxEquipsTheFirstArmourAndCarriesTheRemainingVests()
    {
        PlayerInventory inventory = Inventory();
        GiveIngredients(inventory, crafts: 3);

        CraftOutcome outcome = CraftingService.Craft(inventory, MakeshiftRecipe, 3);

        Assert.Equal(3u, outcome.Crafted);
        var grants = outcome.Changes.Where(c => c.Kind == CraftItemChangeKind.Granted).ToArray();
        Assert.Equal(3, grants.Length);
        Assert.Same(grants[0].Item, inventory.LoadoutSlots[SurvivorLoadout.ChestArmor]);
        Assert.Same(grants[0].Item, inventory.EquipmentSlots[BodySlots.ChestArmor]);
        Assert.Equal(2u, CraftingService.CountAvailable(inventory, CraftingCatalog.MakeshiftArmor));
        Assert.All(grants.Skip(1), c => Assert.Equal(inventory.BaseBag!.Guid, c.Item!.ContainerGuid));
    }

    [Fact]
    public void CraftingWithoutEnoughCargoBulkRestoresIngredientsAndPreservesWornArmour()
    {
        PlayerInventory inventory = Inventory(carryBulk: 324);
        inventory.TryPickUp(LaminatedArmour, 1, out var worn);
        GiveIngredients(inventory);
        var before = inventory.Items.Values.OrderBy(i => i.Guid).Select(i => i.ToRecord(inventory.CharacterGuid)).ToArray();

        CraftOutcome outcome = CraftingService.Craft(inventory, MakeshiftRecipe, 1);

        Assert.False(outcome.Succeeded);
        Assert.Equal(CraftRefusal.NoRoom, outcome.Refusal);
        Assert.Equal(ContainerErrorCode.InteractionValidationFailed, outcome.ContainerError);
        Assert.Empty(outcome.Changes);
        Assert.Same(worn, inventory.LoadoutSlots[SurvivorLoadout.ChestArmor]);
        Assert.Same(worn, inventory.EquipmentSlots[BodySlots.ChestArmor]);
        Assert.Equal(before, inventory.Items.Values.OrderBy(i => i.Guid).Select(i => i.ToRecord(inventory.CharacterGuid)).ToArray());
    }

    [Fact]
    public void CraftMaxRetainsFirstEquippedVestWhenTheNextOneCannotFit()
    {
        PlayerInventory inventory = Inventory(carryBulk: 324);
        GiveIngredients(inventory, crafts: 2);

        CraftOutcome outcome = CraftingService.Craft(inventory, MakeshiftRecipe, 2);

        Assert.Equal(1u, outcome.Crafted);
        Assert.Equal(CraftRefusal.NoRoom, outcome.Refusal);
        var granted = Assert.Single(outcome.Changes, c => c.Kind == CraftItemChangeKind.Granted).Item!;
        Assert.Same(granted, inventory.LoadoutSlots[SurvivorLoadout.ChestArmor]);
        Assert.Equal(0u, CraftingService.CountAvailable(inventory, CraftingCatalog.MakeshiftArmor));
        foreach (RecipeIngredient ingredient in MakeshiftRecipe.Ingredients)
            Assert.Equal(ingredient.Quantity, CraftingService.CountAvailable(inventory, ingredient.ItemDefinitionId));
    }
}
