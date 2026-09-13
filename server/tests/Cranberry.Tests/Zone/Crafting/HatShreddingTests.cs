using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Crafting;

public sealed class HatShreddingTests
{
    [Theory]
    [InlineData(2046u, 23u, 1u)] // Outback hat
    [InlineData(2060u, 23u, 1u)] // Cowboy hat
    [InlineData(2096u, 23u, 1u)] // Beanie
    [InlineData(2168u, 3499u, 2u)] // Motorcycle helmet
    [InlineData(2172u, 3499u, 2u)] // Protective helmet variant
    public void HeadwearYieldsClothUnlessItIsAProtectiveHelmet(uint item, uint material, uint count)
    {
        Assert.True(ShredTable.IsShreddable(item, out var yield));
        Assert.Equal(new RecipeIngredient(material, count), yield);
        Assert.True(ShredTable.IsShreddable(item, out var legacy, everyOfferedItem: false));
        Assert.Equal(yield, legacy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShreddingAWornOrBaggedHatRemovesItAndGrantsCloth(bool bagged)
    {
        ulong next = 0x6000;
        var inventory = new PlayerInventory(0x1001, () => ++next,
            new InventoryOptions { StarterOutfit = [], BaseCarryBulk = 1000 });
        inventory.Bootstrap();
        inventory.TryPickUp(2060, 1, out var worn);
        var hat = worn!;
        if (bagged)
        {
            inventory.TryPickUp(2060, 1, out var spare);
            hat = spare!;
            Assert.Equal(inventory.BaseBag!.Guid, hat.ContainerGuid);
        }
        else
        {
            Assert.Same(hat, inventory.LoadoutSlots[SurvivorLoadout.Head]);
        }

        var request = new RequestUseItem(0, 6, inventory.CharacterGuid, 0, 0, hat.Guid, true, 1, 0);
        Assert.Equal(ItemActionKind.Shred, InventoryActions.Resolve(inventory, request).Kind);
        CraftOutcome result = ShredTable.Shred(inventory, hat.Guid);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(hat.Guid, inventory.Items.Keys);
        Assert.Equal(1u, CraftingService.CountAvailable(inventory, CraftingCatalog.ScrapOfCloth));
        Assert.Equal(0u, CraftingService.CountAvailable(inventory, CraftingCatalog.ArmorScrap));
        if (bagged)
        {
            Assert.Same(worn, inventory.LoadoutSlots[SurvivorLoadout.Head]);
            Assert.Equal(0u, result.ClearedLoadoutSlotId);
        }
        else
        {
            Assert.False(inventory.LoadoutSlots.ContainsKey(SurvivorLoadout.Head));
            Assert.False(inventory.EquipmentSlots.ContainsKey(BodySlots.Head));
            Assert.Equal(SurvivorLoadout.Head, result.ClearedLoadoutSlotId);
        }
    }
}
