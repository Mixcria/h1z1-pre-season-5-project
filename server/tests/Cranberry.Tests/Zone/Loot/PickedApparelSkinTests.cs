using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Loot;

public sealed partial class BodyBagGatewayTests
{
    [Theory]
    [InlineData(2124u)] // military backpack
    [InlineData(2170u)] // tactical helmet
    [InlineData(2171u)] // tactical helmet alias
    [InlineData(2172u)] // motorcycle helmet
    [InlineData(2271u)] // shirt
    [InlineData(4266u)] // hoodie
    [InlineData(2112u)] // trousers
    [InlineData(2256u)] // boots
    public void PickedApparelCanApplyPresetEvenWhenTheLiveDisplayResolverAlreadyProjectsIt(uint definition)
    {
        using var world = new World(new ZoneOptions
        {
            DynamicAppearanceSourcePath = AppearanceSource,
            Inventory = new() { StarterOutfit = [] },
        });
        var player = world.AddPlayer();
        Assert.True(AugustWornSkins.TryGetCategory(definition, out uint category));
        var selected = AugustSkinCatalog.Apparel.First(s => s.CategoryPrototypeId == category && s.RewardItemId != definition);
        SetInventoryPreset(player, selected.AccountItemId);
        // Exercise the real initialization, including its wardrobe display callback. The older
        // fixtures constructed a bare inventory and missed this live-only eligibility failure.
        Set(player.State, "Inventory", null!);
        Assert.Equal(true, world.Call("EnsureInventory", player.Connection, player.State, "apparel regression"));
        var inventory = player.Inventory;
        inventory.TryPickUp(definition, 1, out var item);
        Assert.NotNull(item);
        Assert.Null(item.SkinOverrideDefinitionId);
        Assert.Equal(item.LoadoutSlotId == 0 && definition == 4266 ? definition : selected.RewardItemId, item.DisplayDefinitionId);
        var before = item.ToRecord(player.Guid);

        world.Call("SendInventoryActionState", player.Connection, player.State);
        Assert.Contains(item.Guid.ToString(), LastInventorySkinTargets(player).Split(';'));
        player.Sent.Clear();
        player.Send(InventoryEvent($"skin:{item.Guid}:selected"));
        Assert.DoesNotContain(player.Sent, p => Is(p, 0xc8, 3));
        Assert.Equal(selected.RewardItemId, item.SkinOverrideDefinitionId);
        Assert.Equal(before with { DefinitionId = selected.RewardItemId }, item.ToRecord(player.Guid));
        Assert.Contains(player.Sent, p => Is(p, 0x11, 2) && BitConverter.ToUInt32(p, 15) == selected.RewardItemId);
        Assert.DoesNotContain(item.Guid.ToString(), LastInventorySkinTargets(player).Split(';'));
        Assert.Equal(selected, Assert.Single(Get<AugustWardrobeState>(player.State, "Wardrobe").Snapshot()));
    }
}
