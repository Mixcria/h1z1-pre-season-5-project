using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Crafting;

/// <summary>
/// The staging rules and the shred table (docs/62 §7, §8).
/// <para>
/// The staging exists for one reason: <c>0x26 09 Recipe.List</c> and the self record's <c>0x11a</c>
/// field carry identical bytes, but a mistake in the first costs a missing crafting tab and a
/// mistake in the second costs a client that cannot log in. The order is enforced here rather than
/// left to a call site to remember.
/// </para>
/// </summary>
public sealed class CraftingStageTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] set)
    {
        var map = set.ToDictionary(p => p.Name, p => p.Value);
        return name => map.GetValueOrDefault(name);
    }

    [Fact]
    public void TheDefaultSendsTheRecipeListTheSelfRecordListAndAllowsCrafting()
    {
        CraftingOptions options = CraftingOptions.FromEnvironment(Env()).Effective;

        Assert.True(options.SendRecipeList);
        Assert.True(options.AllowCrafting);
        // D289: stage 2 is the only delivery that populates the crafting window, so it defaults ON.
        Assert.True(options.SendRecipesInSelfRecord);
        Assert.False(options.SendComponentCounts);
        Assert.False(options.SentinelFields);
        Assert.False(options.SeedIngredients);
    }

    /// <summary>
    /// <b>The ordering rule.</b> Stage 2 puts recipe bytes inside the login burst, where the
    /// loader's length assertion turns any error into <c>FUN_1409e1080</c> writing to address zero.
    /// It is refused unless stage 1 - the same bytes, in a packet that can only fail harmlessly -
    /// is also on.
    /// </summary>
    [Fact]
    public void TheSelfRecordStageIsRefusedWithoutTheRecipeListStage()
    {
        var options = new CraftingOptions { SendRecipeList = false, SendRecipesInSelfRecord = true };

        Assert.True(options.SelfRecordRefusedForMissingList);
        Assert.False(options.Effective.SendRecipesInSelfRecord);
        Assert.Contains("IGNORED", options.Describe());
    }

    /// <summary>Answering a craft request for recipes the client was never sent would change an
    /// inventory its crafting window knows nothing about.</summary>
    [Fact]
    public void CraftingIsRefusedWhenNothingDeliversRecipes()
    {
        // Stage 2 now defaults ON (D289), so "nothing delivers recipes" has to switch it off too —
        // otherwise the self record is a delivery and crafting is correctly NOT refused.
        var options = new CraftingOptions
        {
            SendRecipeList = false,
            SendRecipesInSelfRecord = false,
            AllowCrafting = true,
        };

        Assert.True(options.CraftingRefusedForMissingRecipes);
        Assert.False(options.Effective.AllowCrafting);
    }

    /// <summary>Only the exact strings <c>"1"</c> and <c>"0"</c> move a switch, so a typo cannot
    /// silently enable the stage that can break a login.</summary>
    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("", true)]
    public void OnlyOneAndZeroMoveASwitch(string value, bool expected)
    {
        // CRANBERRY_CRAFT (stage 3) is the surviving switch; its default is ON, so only the exact
        // "0" turns it off and every typo leaves it alone. Lane 0D deleted the four experiment
        // switches this case used to exercise through CRANBERRY_CRAFTING_SELFRECORD.
        CraftingOptions options = CraftingOptions.FromEnvironment(
            Env((CraftingOptions.CraftVariable, value)));

        Assert.Equal(expected, options.AllowCrafting);
    }

    [Fact]
    public void AllOffIsTheWave5Behaviour()
    {
        CraftingOptions options = CraftingOptions.AllOff.Effective;

        Assert.False(options.SendRecipeList);
        Assert.False(options.SendRecipesInSelfRecord);
        Assert.False(options.AllowCrafting);
    }

    /// <summary>The boot line has to name every stage, or a play-test is guessing which ran.</summary>
    [Fact]
    public void TheBootLineNamesEveryStage()
    {
        string line = CraftingOptions.Default.Describe();

        Assert.Contains("recipeList=ON", line);
        Assert.Contains("selfRecord=ON", line);
        Assert.Contains("craft=ON", line);
        Assert.Contains("6 recipes", line);
        Assert.Contains("retailRecipes=ON", line);
        Assert.Contains("castBar=ON", line);
        Assert.Contains("interactionStop=ON", line);
    }

    // -----------------------------------------------------------------------------------------
    // Shred - live since wave 9, complete since D260 (docs/62 §8, docs/116 §5).
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Body armour has no <c>SalvageItem</c> row in <c>ItemUseOptions.txt</c>, which is exactly why
    /// the client describes Armor Scrap as the remnant of a destroyed <em>helmet</em> rather than of
    /// shredded armour. Making armour shreddable would let a player loop armour into more armour.
    /// </summary>
    [Fact]
    public void BodyArmourIsNeverShreddable()
    {
        Assert.False(ShredTable.IsShreddable(CraftingCatalog.MakeshiftArmor, out _));
        Assert.DoesNotContain(25041u, ShredTable.Yields.Keys);
    }

    /// <summary>The two yields the client's own item descriptions pin: cloth items give scrap
    /// cloth, large backpacks give composite fabric.</summary>
    [Fact]
    public void TheClientsOwnWordingIsHonoured()
    {
        Assert.True(ShredTable.IsShreddable(93, out RecipeIngredient backpack));
        Assert.Equal(CraftingCatalog.CompositeFabric, backpack.ItemDefinitionId);

        // A shirt (item class 25002) - the client's "a second t-shirt doesn't do you any good".
        uint shirt = InventoryItemFacts.All.First(f => f.ItemClass == 25002).DefinitionId;

        Assert.True(ShredTable.IsShreddable(shirt, out RecipeIngredient cloth));
        Assert.Equal(CraftingCatalog.ScrapOfCloth, cloth.ItemDefinitionId);
    }

    /// <summary>
    /// Every shred output that CAN feed a recipe does - a yield nothing consumes is bulk the player
    /// carries for no reason. Two D260 rows are named exceptions and are listed here rather than
    /// hidden: Gunpowder (11) from a salvaged round and Wood Stick (111) from a Broken Wooden Item
    /// are the products the client's own catalogue and text name, and this build ships no recipe
    /// that eats either. They exist so those items are never refused, which is the defect D260 was
    /// written to close; if a future recipe consumes them, this test tightens by deletion.
    /// </summary>
    [Fact]
    public void EveryShredYieldFeedsARecipeExceptTheTwoNamedOnes()
    {
        var ingredients = CraftingCatalog.Recipes
            .SelectMany(r => r.Ingredients.Select(i => i.ItemDefinitionId))
            .ToHashSet();
        uint[] unconsumed = [CraftingCatalog.Gunpowder, CraftingCatalog.WoodStick];

        foreach (RecipeIngredient yield in ShredTable.Yields.Values
            .Concat(ShredTable.ItemOverrides.Values))
        {
            if (unconsumed.Contains(yield.ItemDefinitionId))
            {
                continue;
            }

            Assert.Contains(yield.ItemDefinitionId, ingredients);
        }

        // The wave-9 seven had no exception at all, and still do not.
        foreach (RecipeIngredient yield in ShredTable.LegacyYields.Values)
        {
            Assert.Contains(yield.ItemDefinitionId, ingredients);
        }
    }

    /// <summary>
    /// <b>D260 - the thirteen refusals.</b> Every item whose own use-option group offers
    /// <c>SalvageItem</c> now has a yield, and the four classes that used to fall through are the
    /// ones a player actually meets. With the switch off, the wave-9 refusals come back exactly.
    /// </summary>
    [Theory]
    [InlineData(94u)]      // Boots, ITEM_CLASS 25005 - the one a player meets in a real match
    [InlineData(1444u)]    // Improvised Compass, 25010
    [InlineData(1706u)]    // Blackberry Pie, 16050
    [InlineData(1428u)]    // .45 Round, 16053, option 87
    [InlineData(2325u)]    // 7.62x39 Round, 16053, option 87
    [InlineData(1694u)]    // Broken Wooden Item, 16053 - the item override
    public void EveryItemWhoseMenuOffersShredNowShreds(uint itemDefinitionId)
    {
        Assert.True(
            ShredTable.IsShreddable(itemDefinitionId, out RecipeIngredient yield),
            $"item {itemDefinitionId} is offered Shred by the client and must have a yield");
        Assert.True(yield.Quantity >= 1);
        Assert.NotEqual(0u, yield.ItemDefinitionId);

        Assert.False(ShredTable.IsShreddable(itemDefinitionId, out _, everyOfferedItem: false));
    }

    /// <summary>
    /// The one item whose own description names a product different from its class-mates':
    /// <em>"This item is beyond repair, but can be dismantled for a wooden stick"</em>. It is
    /// <c>ITEM_CLASS</c> 16053 alongside the eight ammunition rounds, so the class rule alone would
    /// turn it into gunpowder.
    /// </summary>
    [Fact]
    public void TheBrokenWoodenItemBecomesAWoodenStickAndNotGunpowder()
    {
        Assert.True(ShredTable.IsShreddable(1694, out RecipeIngredient stick));
        Assert.Equal(CraftingCatalog.WoodStick, stick.ItemDefinitionId);

        Assert.True(ShredTable.IsShreddable(1428, out RecipeIngredient round));
        Assert.Equal(CraftingCatalog.Gunpowder, round.ItemDefinitionId);
    }

    /// <summary>The busy window is the client's own <c>BUSY_MSEC</c>: 1,000 ms on rows 6 and 63,
    /// the two <c>SalvageItem</c> rows the client offers on apparel, and 0 on the other five - the
    /// one number in this lane that is derived rather than designed.</summary>
    [Fact]
    public void TheShredBusyWindowIsTheClientsOwn()
    {
        Assert.Equal(1000, ShredTable.BusyMilliseconds);
    }

    /// <summary>
    /// A bagged shirt shreds into two scraps of cloth. The first shirt a player picks up is
    /// <em>worn</em>, so it is the second one - the client's own "a second t-shirt doesn't do you
    /// any good" - that reaches the bag and can be shredded.
    /// </summary>
    [Fact]
    public void ABaggedShirtShredsIntoScrapCloth()
    {
        ulong next = 0x6000;
        var inventory = new PlayerInventory(
            0xfeed, () => ++next, new InventoryOptions { StarterOutfit = [], BaseCarryBulk = 1000 });
        inventory.Bootstrap();

        uint shirt = InventoryItemFacts.All.First(f => f.ItemClass == 25002).DefinitionId;
        inventory.TryPickUp(shirt, 1, out InventoryItemInstance? worn);
        inventory.TryPickUp(shirt, 1, out InventoryItemInstance? spare);
        Assert.NotNull(worn);
        Assert.NotNull(spare);

        InventoryItemInstance bagged = spare.ContainerGuid != 0 ? spare : worn;
        Assert.NotEqual(0ul, bagged.ContainerGuid);

        CraftOutcome outcome = ShredTable.Shred(inventory, bagged.Guid);

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, outcome.Changes.Count);
        Assert.Equal(4u, CraftingService.CountAvailable(inventory, CraftingCatalog.ScrapOfCloth));
    }

    /// <summary>
    /// <b>WAVE 9 - this assertion is INVERTED, and deliberately kept rather than deleted.</b> It
    /// used to read "a worn item is refused rather than shredded", and its own summary said the
    /// unbind "is step 3 of the list in <see cref="ShredTable"/> and belongs with the wiring". The
    /// wiring landed (<c>Inventory.ItemVerbs.Salvage</c> -&gt;
    /// <c>ZoneService.RunShred</c>), so the behaviour it pinned is now the defect: the owner's four
    /// refused shreds on 30 Aug 2026 were every one of them on item 2144, his WORN starter shirt.
    /// A worn item is unbound and shredded; what stays refused is a worn CONTAINER, which
    /// <see cref="AWornContainerIsStillRefused"/> pins.
    /// </summary>
    [Fact]
    public void AWornItemIsShreddedAndVacatesItsLoadoutSlot()
    {
        ulong next = 0x6800;
        var inventory = new PlayerInventory(
            0xfeed, () => ++next, new InventoryOptions { StarterOutfit = [], BaseCarryBulk = 1000 });
        inventory.Bootstrap();

        uint shirt = InventoryItemFacts.All.First(f => f.ItemClass == 25002).DefinitionId;
        inventory.TryPickUp(shirt, 1, out InventoryItemInstance? worn);
        Assert.NotNull(worn);

        if (worn.ContainerGuid != 0)
        {
            // Auto-assign bagged it rather than wearing it; nothing to prove here.
            return;
        }

        uint wasInSlot = worn.LoadoutSlotId;
        Assert.NotEqual(0u, wasInSlot);

        CraftOutcome outcome = ShredTable.Shred(inventory, worn.Guid);

        Assert.True(outcome.Succeeded);
        Assert.Equal(wasInSlot, outcome.ClearedLoadoutSlotId);
        Assert.False(inventory.LoadoutSlots.ContainsKey(wasInSlot));
        Assert.DoesNotContain(worn.Guid, inventory.Items.Keys);
        Assert.Equal(4u, CraftingService.CountAvailable(inventory, CraftingCatalog.ScrapOfCloth));
    }

    /// <summary>
    /// The one thing a worn item may still not be: the bag. Shredding the carrying container would
    /// strand every item pointing at its guid, and there is no second container to move them to.
    /// </summary>
    [Fact]
    public void AWornContainerIsStillRefused()
    {
        ulong next = 0x6900;
        var inventory = new PlayerInventory(
            0xfeed, () => ++next, new InventoryOptions { StarterOutfit = [], BaseCarryBulk = 1000 });
        inventory.Bootstrap();

        InventoryContainer bag = Assert.IsType<InventoryContainer>(inventory.BaseBag);

        CraftOutcome outcome = ShredTable.Shred(inventory, bag.Guid);

        Assert.False(outcome.Succeeded);
        Assert.NotNull(inventory.BaseBag);
    }

    /// <summary>Shredding something with no yield changes nothing and answers in the client's own
    /// error vocabulary.</summary>
    [Fact]
    public void ShreddingSomethingWithNoYieldIsRefused()
    {
        ulong next = 0x7000;
        var inventory = new PlayerInventory(
            0xfeed, () => ++next, new InventoryOptions { StarterOutfit = [] });
        inventory.Bootstrap();
        inventory.TryPickUp(CraftingCatalog.DuctTape, 1, out InventoryItemInstance? tape);
        Assert.NotNull(tape);

        CraftOutcome outcome = ShredTable.Shred(inventory, tape.Guid);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ContainerErrorCode.WrongItemType, outcome.ContainerError);
        Assert.Equal(1u, CraftingService.CountAvailable(inventory, CraftingCatalog.DuctTape));
    }
}
