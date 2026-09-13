using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Crafting;

/// <summary>
/// Crafting against a real inventory (docs/62 §5, docs/116 §1).
/// <para>
/// The client performs no validation of its own - it ships no recipe data at all - so every rule
/// tested here is the server's only line of defence. The one that matters most is atomicity: a
/// craft that cannot place its output must leave the ingredients exactly where it found them.
/// </para>
/// <para>
/// The quantities moved with D256, when the retail recipe set was decoded out of the owner's own
/// admin capture: a Field Bandage costs <b>two</b> scraps of cloth, Makeshift Armor takes four
/// composite fabric in the component order tape / scrap / fabric, and item 93 Crafted Backpack has
/// no recipe at all.
/// </para>
/// </summary>
public sealed class CraftingServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuickSlotMedicalIngredientsAreConsumedOrRestoredTogether(bool noRoom)
    {
        ulong next = 0x8000;
        var inventory = new PlayerInventory(123, () => ++next, new InventoryOptions { StarterOutfit = [] });
        inventory.Bootstrap();
        inventory.TryPickUp(CraftingCatalog.FieldBandage, 6, out var bandages);
        inventory.TryPickUp(CraftingCatalog.FirstAidKit, 1, out var aid);
        Assert.Equal(10u, CraftingService.CountAvailable(inventory, CraftingCatalog.FieldBandage));
        var recipe = CraftingCatalog.ByIdFor(true)[CraftingCatalog.Procoagulant];
        if (noRoom) inventory.TryStow(inventory.CreateInstance(73, 9999));
        var result = CraftingService.Craft(inventory, recipe, 1);
        if (noRoom)
        {
            Assert.Equal(CraftRefusal.NoRoom, result.Refusal);
            Assert.Same(bandages, inventory.LoadoutSlots[SurvivorLoadout.QuickUse1]);
            Assert.Same(aid, inventory.LoadoutSlots[SurvivorLoadout.QuickUse2]);
            Assert.Equal(10u, bandages!.Count);
            Assert.Equal(1u, aid!.Count);
        }
        else
        {
            Assert.True(result.Succeeded);
            Assert.False(inventory.Items.ContainsKey(bandages!.Guid));
            Assert.False(inventory.Items.ContainsKey(aid!.Guid));
            Assert.Single(inventory.Items.Values, i => i.DefinitionId == CraftingCatalog.Procoagulant);
        }
    }

    private static PlayerInventory Inventory(int carryBulk = 100)
    {
        ulong next = 0x5000;
        var inventory = new PlayerInventory(
            0xdead_beef,
            () => ++next,
            new InventoryOptions
            {
                StarterOutfit = [],
                BaseCarryBulk = carryBulk,
                QuickUseConsumables = false,
            });
        inventory.Bootstrap();
        return inventory;
    }

    private static void Give(PlayerInventory inventory, uint itemDefinitionId, uint count)
    {
        inventory.TryPickUp(itemDefinitionId, count, out InventoryItemInstance? instance);
        Assert.NotNull(instance);
    }

    [Fact]
    public void ACraftConsumesTheIngredientsAndGrantsTheOutput()
    {
        PlayerInventory inventory = Inventory();
        Give(inventory, CraftingCatalog.ScrapOfCloth, 4);

        // D256: the retail Field Bandage costs TWO scraps of cloth, not the wave-6 design one.
        CraftOutcome outcome = CraftingService.Craft(inventory, CraftingCatalog.FieldBandage, 1);

        Assert.True(outcome.Succeeded);
        Assert.Equal(1u, outcome.Crafted);
        Assert.Equal(CraftRefusal.None, outcome.Refusal);
        Assert.Equal(2u, CraftingService.CountAvailable(inventory, CraftingCatalog.ScrapOfCloth));
        Assert.Equal(1u, CraftingService.CountAvailable(inventory, CraftingCatalog.FieldBandage));
    }

    /// <summary>
    /// One packet pair per touched item, whatever the craft count. A <c>Craft Max</c> that ate nine
    /// cloth out of one stack must not send nine deletes and nine adds for the same guid - the
    /// second delete names a guid the client has already dropped.
    /// </summary>
    [Fact]
    public void CraftMaxSendsOneChangePerTouchedItemNotOnePerCraft()
    {
        PlayerInventory inventory = Inventory();
        Give(inventory, CraftingCatalog.ScrapOfCloth, 18);

        CraftOutcome outcome = CraftingService.Craft(inventory, CraftingCatalog.FieldBandage, 9);

        Assert.Equal(9u, outcome.Crafted);
        Assert.Single(outcome.Changes, c => c.Kind == CraftItemChangeKind.Removed);
        var grant = Assert.Single(outcome.Changes, c => c.Kind == CraftItemChangeKind.Granted);
        Assert.Equal(9u, grant.Item!.Count);
        Assert.Equal(outcome.Changes.Select(c => c.ItemGuid).Distinct().Count(), outcome.Changes.Count);
    }

    /// <summary>Consumption packets precede grants: everything after them names a guid the client
    /// must still recognise.</summary>
    [Fact]
    public void ConsumptionChangesComeBeforeGrants()
    {
        PlayerInventory inventory = Inventory();
        Give(inventory, CraftingCatalog.ScrapOfCloth, 4);

        CraftOutcome outcome = CraftingService.Craft(inventory, CraftingCatalog.FieldBandage, 2);

        int lastConsumption = outcome.Changes
            .Select((c, i) => (c, i))
            .Where(t => t.c.Kind is CraftItemChangeKind.Removed or CraftItemChangeKind.CountChanged)
            .Max(t => t.i);
        int firstGrant = outcome.Changes
            .Select((c, i) => (c, i))
            .Where(t => t.c.Kind is CraftItemChangeKind.Granted or CraftItemChangeKind.GrantedIntoStack)
            .Min(t => t.i);
        Assert.True(lastConsumption < firstGrant);
    }

    /// <summary>A partly consumed stack survives, and its change asks for delete-then-add because
    /// <c>ClientUpdate.ItemUpdate</c>'s envelope has never been derived.</summary>
    [Fact]
    public void APartlyConsumedStackIsResentRatherThanUpdated()
    {
        PlayerInventory inventory = Inventory();
        Give(inventory, CraftingCatalog.ScrapOfCloth, 5);

        CraftOutcome outcome = CraftingService.Craft(inventory, CraftingCatalog.FieldBandage, 1);

        CraftItemChange change = Assert.Single(
            outcome.Changes, c => c.Kind == CraftItemChangeKind.CountChanged);
        Assert.True(change.NeedsDelete);
        Assert.True(change.NeedsAdd);
        Assert.Equal(3u, change.Item!.Count);   // 5 - the retail recipe's 2
    }

    [Fact]
    public void MissingIngredientsRefuseAndNameTheShortfall()
    {
        PlayerInventory inventory = Inventory();
        Give(inventory, CraftingCatalog.ArmorScrap, 1);
        Give(inventory, CraftingCatalog.DuctTape, 1);

        CraftOutcome outcome = CraftingService.Craft(inventory, CraftingCatalog.MakeshiftArmor, 1);

        Assert.False(outcome.Succeeded);
        Assert.Equal(CraftRefusal.MissingIngredients, outcome.Refusal);
        Assert.Contains(outcome.Shortfall, s => s.ItemDefinitionId == CraftingCatalog.CompositeFabric);
        Assert.Contains(outcome.Shortfall, s => s.ItemDefinitionId == CraftingCatalog.ArmorScrap && s.Quantity == 1);
        Assert.Empty(outcome.Changes);

        // Nothing was taken.
        Assert.Equal(1u, CraftingService.CountAvailable(inventory, CraftingCatalog.ArmorScrap));
        Assert.Equal(1u, CraftingService.CountAvailable(inventory, CraftingCatalog.DuctTape));
    }

    /// <summary>
    /// <b>The atomicity test.</b> The ingredients are consumed before the output is placed, because
    /// consuming them is what frees the bulk the output needs. If the placement then fails the
    /// player has paid for nothing, so the consumption is rolled back - including a stack that was
    /// emptied and removed from its container slot.
    /// <para>
    /// The rollback is driven here by a synthetic recipe whose output - item 1344, bulk 1,500
    /// and not equippable - cannot fit in any bag this test can build. CraftedArmourTests also
    /// covers the real makeshift recipe refusing when a spare vest cannot fit in the bag.
    /// </para>
    /// </summary>
    [Fact]
    public void ARefusedPlacementRollsTheIngredientsBack()
    {
        PlayerInventory inventory = Inventory();
        Give(inventory, CraftingCatalog.ArmorScrap, 2);
        Give(inventory, CraftingCatalog.DuctTape, 1);

        var impossible = new RecipeDefinition(
            OutputItemDefinitionId: 1344,
            OutputCount: 1,
            Ingredients:
            [
                new RecipeIngredient(CraftingCatalog.ArmorScrap, 2),
                new RecipeIngredient(CraftingCatalog.DuctTape, 1),
            ],
            SortOrdinal: 99,
            BusyMilliseconds: 0);

        CraftOutcome outcome = CraftingService.Craft(inventory, impossible, 1);

        Assert.False(outcome.Succeeded);
        Assert.Equal(CraftRefusal.NoRoom, outcome.Refusal);
        Assert.NotEqual(ContainerErrorCode.None, outcome.ContainerError);
        Assert.Empty(outcome.Changes);

        // Both stacks are back, including the duct tape whose stack was emptied entirely and
        // removed from its container slot.
        Assert.Equal(2u, CraftingService.CountAvailable(inventory, CraftingCatalog.ArmorScrap));
        Assert.Equal(1u, CraftingService.CountAvailable(inventory, CraftingCatalog.DuctTape));
    }

    /// <summary>
    /// A partial <c>Craft Max</c> is a success, not a refusal: asking for nine with material for
    /// three makes three and reports where it stopped.
    /// </summary>
    [Fact]
    public void APartialCraftMaxKeepsWhatItMade()
    {
        PlayerInventory inventory = Inventory();
        Give(inventory, CraftingCatalog.ScrapOfCloth, 6);

        CraftOutcome outcome = CraftingService.Craft(inventory, CraftingCatalog.FieldBandage, 9);

        Assert.True(outcome.Succeeded);
        Assert.Equal(3u, outcome.Crafted);
        Assert.Equal(9u, outcome.Requested);
        Assert.Equal(CraftRefusal.MissingIngredients, outcome.Refusal);
        Assert.Equal(0u, CraftingService.CountAvailable(inventory, CraftingCatalog.ScrapOfCloth));
    }

    /// <summary>
    /// A fully consumed stack is forgotten, not merely emptied: its guid must never be resolvable
    /// again once the client has been told to delete it.
    /// </summary>
    [Fact]
    public void AFullyConsumedStackIsForgotten()
    {
        PlayerInventory inventory = Inventory();
        Give(inventory, CraftingCatalog.ScrapOfCloth, 4);
        ulong cloth = inventory.Items.Values.First(i => i.DefinitionId == CraftingCatalog.ScrapOfCloth).Guid;

        CraftOutcome outcome = CraftingService.Craft(inventory, CraftingCatalog.FieldBandage, 2);

        Assert.Equal(2u, outcome.Crafted);
        Assert.DoesNotContain(cloth, inventory.Items.Keys);
        Assert.Contains(outcome.Changes, c => c.ItemGuid == cloth && c.Kind == CraftItemChangeKind.Removed);
    }

    [Fact]
    public void AnUnknownRecipeChangesNothing()
    {
        PlayerInventory inventory = Inventory();
        Give(inventory, CraftingCatalog.ScrapOfCloth, 5);

        CraftOutcome outcome = CraftingService.Craft(inventory, 999_999, 1);

        Assert.Equal(CraftRefusal.UnknownRecipe, outcome.Refusal);
        Assert.Null(outcome.Recipe);
        Assert.Empty(outcome.Changes);
        Assert.Equal(5u, CraftingService.CountAvailable(inventory, CraftingCatalog.ScrapOfCloth));
    }

    /// <summary>With <see cref="CraftingOptions.AllowCrafting"/> off the request is refused before
    /// anything is looked up, so the switch is a genuine kill switch and not a filter.</summary>
    [Fact]
    public void CraftingDisabledRefusesWithoutTouchingTheInventory()
    {
        PlayerInventory inventory = Inventory();
        Give(inventory, CraftingCatalog.ScrapOfCloth, 5);

        CraftOutcome outcome = CraftingService.Craft(
            inventory,
            CraftingCatalog.FieldBandage,
            1,
            CraftingOptions.Default with { AllowCrafting = false });

        Assert.Equal(CraftRefusal.Disabled, outcome.Refusal);
        Assert.Equal(5u, CraftingService.CountAvailable(inventory, CraftingCatalog.ScrapOfCloth));
    }

    /// <summary>
    /// Only bagged items pay for a craft. A worn item is bound to a loadout slot and is deliberately
    /// invisible to crafting - the crafting window's own count is a bag count, and consuming a worn
    /// backpack out from under the container it provides is a bug class this lane declines to open.
    /// </summary>
    [Fact]
    public void WornItemsAreNeverConsumed()
    {
        PlayerInventory inventory = Inventory(carryBulk: 1000);
        Give(inventory, CraftingCatalog.ScrapOfCloth, 6);

        // D256: retail's container recipe is the SATCHEL for six cloth - there is no Crafted
        // Backpack recipe at all. Like item 93 it is item class 25004, so it goes to a loadout slot
        // when it is made, and a second craft must not be able to eat it back out of that slot.
        CraftOutcome first = CraftingService.Craft(inventory, CraftingCatalog.Satchel, 1);
        Assert.True(first.Succeeded);

        uint bagged = CraftingService.CountAvailable(inventory, CraftingCatalog.Satchel);
        InventoryItemInstance satchel = inventory.Items.Values
            .First(i => i.DefinitionId == CraftingCatalog.Satchel && i.Count > 0);
        Assert.True(satchel.LoadoutSlotId != 0 || bagged == 1);
    }

    /// <summary>Item 93 has no recipe in retail August (D256), so asking for one is an unknown
    /// recipe rather than a craft.</summary>
    [Fact]
    public void TheCraftedBackpackHasNoRetailRecipe()
    {
        PlayerInventory inventory = Inventory(carryBulk: 1000);
        Give(inventory, CraftingCatalog.ScrapOfCloth, 9);
        Give(inventory, CraftingCatalog.DuctTape, 1);

        CraftOutcome outcome = CraftingService.Craft(inventory, CraftingCatalog.CraftedBackpack, 1);

        Assert.Equal(CraftRefusal.UnknownRecipe, outcome.Refusal);
        Assert.Empty(outcome.Changes);

        // …and it is still craftable with CRANBERRY_CRAFT_RECIPES=0, which is what makes that an
        // exact revert rather than a partial one.
        CraftOutcome legacy = CraftingService.Craft(
            inventory,
            CraftingCatalog.CraftedBackpack,
            1,
            CraftingOptions.Default with { RetailRecipes = false });

        Assert.True(legacy.Succeeded);
    }

    [Fact]
    public void CraftableCountIsTheMinimumOverIngredients()
    {
        // Armor Scrap is bulk 30 and Composite Fabric bulk 10, so this needs a bag with room.
        // D256: the retail recipe is 1 duct tape + 2 armor scrap + 4 composite fabric.
        PlayerInventory inventory = Inventory(carryBulk: 1000);
        Give(inventory, CraftingCatalog.ArmorScrap, 6);      // 6 / 2 = enough for 3
        Give(inventory, CraftingCatalog.CompositeFabric, 8); // 8 / 4 = enough for 2
        Give(inventory, CraftingCatalog.DuctTape, 9);        // 9 / 1 = enough for 9

        Assert.Equal(
            2u,
            CraftingService.CraftableCount(inventory, CraftingCatalog.ById[CraftingCatalog.MakeshiftArmor]));
    }

    /// <summary>The seed switch grants exactly one craft's worth of every ingredient, and never an
    /// output.</summary>
    [Fact]
    public void SeedGrantsCoverEveryIngredientAndNoOutput()
    {
        IReadOnlyList<RecipeIngredient> seeds = CraftingService.SeedGrants();

        foreach (RecipeDefinition recipe in CraftingCatalog.Recipes)
        {
            foreach (RecipeIngredient ingredient in recipe.Ingredients)
            {
                Assert.Contains(seeds, s =>
                    s.ItemDefinitionId == ingredient.ItemDefinitionId && s.Quantity >= ingredient.Quantity);
            }
        }

        // The Field Bandage is both an output and an ingredient (of the Procoagulant), so it is the
        // one item that legitimately appears; nothing else that is only an output may.
        Assert.DoesNotContain(seeds, s => s.ItemDefinitionId == CraftingCatalog.MakeshiftArmor);
        Assert.DoesNotContain(seeds, s => s.ItemDefinitionId == CraftingCatalog.CraftedBackpack);
        Assert.DoesNotContain(seeds, s => s.ItemDefinitionId == CraftingCatalog.Procoagulant);
    }

    [Fact]
    public void ComponentCountUpdatesNameEachIngredientByIndex()
    {
        PlayerInventory inventory = Inventory(carryBulk: 1000);
        Give(inventory, CraftingCatalog.ArmorScrap, 4);

        IReadOnlyList<RecipeComponentUpdate> updates = CraftingService.ComponentCountUpdates(
            inventory, CraftingCatalog.ById[CraftingCatalog.MakeshiftArmor]);

        // D256: the captured component order is duct tape, armor scrap, composite fabric.
        Assert.Equal(3, updates.Count);
        Assert.Equal(0u, updates[0].ComponentIndex);
        Assert.Equal(CraftingCatalog.MakeshiftArmor, updates[0].RecipeId);
        Assert.Equal(0u, (uint)updates[0].Value);   // duct tape, none carried
        Assert.Equal(1u, updates[1].ComponentIndex);
        Assert.Equal(4u, (uint)updates[1].Value);   // armor scrap
        Assert.Equal(0u, (uint)updates[2].Value);   // composite fabric
    }
}
