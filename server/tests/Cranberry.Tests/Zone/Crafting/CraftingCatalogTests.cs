using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Crafting;

/// <summary>
/// The six retail recipes and the shape rules they must keep (docs/62 §4, docs/116 §1).
/// <para>
/// The set is no longer design: it is the decode of the owner's own admin capture (D256), so these
/// tests pin ids, counts and order <b>hard</b>. The legacy four survive behind
/// <c>CRANBERRY_CRAFT_RECIPES=0</c> and keep their old, looser guarantees.
/// </para>
/// </summary>
public sealed class CraftingCatalogTests
{
    /// <summary>
    /// Exactly the six recipes the friend server's 936-byte <c>26 09</c> carries, in the order it
    /// writes them (D256). Item 93 Crafted Backpack is NOT one of them - retail's container recipe
    /// is the Satchel.
    /// </summary>
    [Fact]
    public void TheCatalogueIsTheRetailSix()
    {
        Assert.Equal(6, CraftingCatalog.Recipes.Count);
        Assert.Equal(
            [2125u, 1434u, 3378u, 138u, 3375u, 2423u],
            CraftingCatalog.Recipes.Select(r => r.OutputItemDefinitionId).ToArray());
        Assert.DoesNotContain(93u, CraftingCatalog.RetailById.Keys);

        // the captured sort ordinals, a permutation of 1..6
        Assert.Equal(
            [1u, 2u, 3u, 4u, 5u, 6u],
            CraftingCatalog.Recipes.Select(r => r.SortOrdinal).Order().ToArray());
        Assert.Equal(
            [2423u, 3378u, 3375u, 138u, 1434u, 2125u],
            CraftingCatalog.Recipes.OrderBy(r => r.SortOrdinal).Select(r => r.RecipeId).ToArray());
    }

    /// <summary>The wave-6 four are still there, unchanged, as the one-word revert.</summary>
    [Fact]
    public void TheLegacyFourAreStillTheRevert()
    {
        Assert.Equal(4, CraftingCatalog.LegacyRecipes.Count);
        Assert.Equal(
            [93u, 2423u, 3375u, 3378u],
            CraftingCatalog.LegacyRecipes.Select(r => r.OutputItemDefinitionId).Order().ToArray());
        Assert.Same(CraftingCatalog.LegacyRecipes, CraftingCatalog.For(retailRecipes: false));
        Assert.Same(CraftingCatalog.RetailRecipes, CraftingCatalog.For(retailRecipes: true));
    }

    /// <summary>
    /// <b>The one place the wire and the game deliberately disagree.</b> Flaming Arrow's
    /// <c>BundleCount</c> is 0 in both captured packets and stays 0 on the wire; a completed craft
    /// grants the owner's own 5. Everything else has the two equal.
    /// </summary>
    [Fact]
    public void OnlyFlamingArrowSeparatesTheWireCountFromTheGrantedCount()
    {
        foreach (RecipeDefinition recipe in CraftingCatalog.RetailRecipes)
        {
            if (recipe.RecipeId == CraftingCatalog.FlamingArrow)
            {
                Assert.Equal(0u, recipe.BundleCount);
                Assert.Equal(5u, recipe.OutputCount);
                continue;
            }

            Assert.Equal(recipe.OutputCount, recipe.BundleCount);
        }

        Assert.Equal(5u, CraftingCatalog.RetailById[CraftingCatalog.ExplosiveArrow].BundleCount);
    }

    /// <summary>Every retail ingredient list is the capture's, count for count.</summary>
    [Fact]
    public void TheRetailIngredientsAreTheCapturedOnes()
    {
        Assert.Equal(
            [(23u, 2u)],
            Flatten(CraftingCatalog.FieldBandage));
        Assert.Equal(
            [(2423u, 10u), (2424u, 1u)],
            Flatten(CraftingCatalog.Procoagulant));
        Assert.Equal(
            [(134u, 1u), (3499u, 2u), (3500u, 4u)],
            Flatten(CraftingCatalog.MakeshiftArmor));
        Assert.Equal(
            [(23u, 6u)],
            Flatten(CraftingCatalog.Satchel));
        Assert.Equal(
            [(65u, 1u), (112u, 5u), (134u, 1u), (1511u, 12u)],
            Flatten(CraftingCatalog.ExplosiveArrow));
        Assert.Equal(
            [(14u, 1u), (112u, 5u), (134u, 1u), (1511u, 5u)],
            Flatten(CraftingCatalog.FlamingArrow));

        static (uint, uint)[] Flatten(uint recipeId) =>
        [
            .. CraftingCatalog.RetailById[recipeId].Ingredients
                .Select(i => (i.ItemDefinitionId, i.Quantity)),
        ];
    }

    /// <summary>
    /// The recipe id is the output item id. <c>FUN_141518250</c> arm 3 refuses <c>recipeId &lt; 1</c>
    /// and <c>FUN_14143a3e0</c> refuses to send id 0, so a zero id would be unaddressable in both
    /// directions.
    /// </summary>
    [Fact]
    public void RecipeIdsAreTheOutputItemIdsAndAreNeverZero()
    {
        foreach (RecipeDefinition recipe in CraftingCatalog.Recipes)
        {
            Assert.Equal(recipe.OutputItemDefinitionId, recipe.RecipeId);
            Assert.NotEqual(0u, recipe.RecipeId);
        }

        Assert.Equal(CraftingCatalog.Recipes.Count, CraftingCatalog.ById.Count);
        foreach (RecipeDefinition legacy in CraftingCatalog.LegacyRecipes)
        {
            Assert.Equal(legacy.OutputItemDefinitionId, legacy.RecipeId);
        }
    }

    /// <summary>
    /// <b>The framing disambiguation depends on this.</b> <c>RecipeStartRequest</c> tells the u16-sub
    /// and u8-sub framings of <c>09 1a</c> apart by the third payload byte, which is <c>0x00</c> in
    /// the u16 framing and the recipe id's low byte in the u8 framing. A recipe id that were a
    /// multiple of 256 would make the two framings decode identically and the reader would have to
    /// guess. None is - and if a future recipe were, this test fails before the ambiguity ships.
    /// </summary>
    [Fact]
    public void NoRecipeIdIsAMultipleOf256()
    {
        foreach (RecipeDefinition recipe in CraftingCatalog.Recipes)
        {
            Assert.NotEqual(0u, recipe.RecipeId % 256);
        }
    }

    /// <summary>The crafting window renders four ingredient slots (<c>ingredientsItem_1..4</c>), so
    /// a fifth ingredient would simply not be drawn.</summary>
    [Fact]
    public void NoRecipeHasMoreThanFourIngredients()
    {
        foreach (RecipeDefinition recipe in CraftingCatalog.Recipes)
        {
            Assert.InRange(recipe.Ingredients.Count, 1, 4);
            Assert.All(recipe.Ingredients, i => Assert.True(i.Quantity >= 1));
            Assert.True(recipe.OutputCount >= 1);   // what a craft GRANTS, never zero
        }
    }

    /// <summary>
    /// Every id in the catalogue is a real <c>ClientItemDefinitions</c> row. The client resolves the
    /// panel's icon, name and bulk from its own item definition given the wire's <c>ItemId</c>
    /// (<c>FUN_140db5680</c> publishes an <c>Items</c> datasource row per referenced id), so an id
    /// the client does not know draws an empty slot.
    /// </summary>
    [Fact]
    public void EveryReferencedItemIsARealItemDefinition()
    {
        foreach (uint id in CraftingCatalog.ReferencedItems)
        {
            Assert.True(InventoryItemFacts.TryGet(id, out InventoryItemFact fact), $"item {id} is not a client item");
            Assert.NotEqual(0u, fact.NameId);
        }
    }

    /// <summary>
    /// A recipe may not consume itself, and no ingredient may be an output of the same recipe -
    /// either would let a craft succeed by eating its own product.
    /// </summary>
    [Fact]
    public void NoRecipeConsumesItsOwnOutput()
    {
        foreach (RecipeDefinition recipe in CraftingCatalog.Recipes)
        {
            Assert.DoesNotContain(recipe.Ingredients, i => i.ItemDefinitionId == recipe.OutputItemDefinitionId);
        }
    }

    /// <summary>
    /// The projection carries the derived field mapping: the output id in
    /// <see cref="RecipeRecord.OutputItemDefinitionId"/> (the <c>ItemId</c> column), the output's
    /// own name id in <see cref="RecipeRecord.ItemNameStringId"/> (a string id, never an item id -
    /// <c>FUN_141518250</c> arm 1 resolves it through the localisation manager), and each
    /// ingredient's required count in <see cref="RecipeComponentRecord.RequiredCount"/>.
    /// </summary>
    [Fact]
    public void TheProjectionPutsItemIdsAndCountsInTheDerivedFields()
    {
        RecipeDefinition armor = CraftingCatalog.ById[CraftingCatalog.MakeshiftArmor];
        RecipeRecord record = armor.ToRecord();

        InventoryItemFacts.TryGet(CraftingCatalog.MakeshiftArmor, out InventoryItemFact output);
        Assert.Equal(CraftingCatalog.MakeshiftArmor, record.RecipeId);
        Assert.Equal(CraftingCatalog.MakeshiftArmor, record.OutputItemDefinitionId);
        Assert.Equal(output.NameId, record.ItemNameStringId);
        Assert.Equal(armor.BundleCount, record.BundleCount);
        Assert.Equal(armor.Ingredients.Count, record.Components.Count);

        for (int i = 0; i < armor.Ingredients.Count; i++)
        {
            RecipeIngredient ingredient = armor.Ingredients[i];
            RecipeComponentRecord component = record.Components[i];
            Assert.Equal(ingredient.ItemDefinitionId, component.ItemDefinitionId);
            Assert.Equal(ingredient.ItemDefinitionId, component.Key);
            Assert.Equal(ingredient.Quantity, component.RequiredCount);
            InventoryItemFacts.TryGet(ingredient.ItemDefinitionId, out InventoryItemFact fact);
            Assert.Equal(fact.NameId, component.ItemNameStringId);
        }
    }

    /// <summary>
    /// The sentinel mode leaves the identity fields real and marks only the ones whose role is not
    /// proven - a sentinel that overwrote the recipe id would make the run unreadable, because the
    /// panel could no longer be matched to a recipe.
    /// </summary>
    [Fact]
    public void SentinelModeKeepsTheIdentityFieldsReal()
    {
        RecipeRecord sentinel = CraftingCatalog.ById[CraftingCatalog.FieldBandage].ToSentinelRecord();

        Assert.Equal(CraftingCatalog.FieldBandage, sentinel.RecipeId);
        Assert.Equal(CraftingCatalog.FieldBandage, sentinel.OutputItemDefinitionId);
        Assert.Equal(0xA1u, sentinel.ItemNameStringId);
        Assert.Equal(CraftingCatalog.ScrapOfCloth, sentinel.Components[0].ItemDefinitionId);
        Assert.Equal(2u, sentinel.Components[0].RequiredCount);   // the retail count (D256)
    }
}
