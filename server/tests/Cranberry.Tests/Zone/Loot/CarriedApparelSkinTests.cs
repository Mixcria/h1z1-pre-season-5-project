using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Loot;

public sealed partial class BodyBagGatewayTests
{
    [Theory]
    [InlineData(2170u, true)]
    [InlineData(2171u, true)]
    [InlineData(2172u, true)]
    [InlineData(2124u, false)]
    public void BodyBagHelmetsInCargoAndWornMilitaryBackpackAcceptSavedSkin(uint definition, bool cargo)
    {
        using var world = new World(new ZoneOptions { DynamicAppearanceSourcePath = AppearanceSource });
        var donor = world.AddPlayer(shared: true);
        var recipient = world.AddPlayer(shared: true);
        var inventory = EmptyOutfit(recipient);
        if (cargo) inventory.TryPickUp(2124, 1, out _);
        if (cargo) inventory.TryPickUp(2170, 1, out _);
        Assert.True(AugustWornSkins.TryGetCategory(definition, out uint category));
        var selected = AugustSkinCatalog.Apparel.First(s => s.CategoryPrototypeId == category && s.RewardItemId != definition);
        SetInventoryPreset(recipient, selected.AccountItemId);
        donor.Inventory.TryPickUp(definition, 1, out var original);
        Assert.NotNull(original);
        world.Call("DropPlayerBodyBag", donor.Connection, donor.State);
        var (owner, _) = Assert.Single(recipient.Bags);
        var beforeIds = inventory.Items.Keys.ToHashSet();
        recipient.Send(Use(recipient, original.Guid, 59, owner));
        var received = Assert.Single(inventory.Items.Values, i => !beforeIds.Contains(i.Guid) && i.DefinitionId == definition);
        if (cargo && received.LoadoutSlotId != 0) Assert.True(inventory.TryStow(received));
        Assert.Equal(cargo, received.LoadoutSlotId == 0);
        world.Call("SendInventoryActionState", recipient.Connection, recipient.State);
        Assert.Contains(received.Guid.ToString(), LastInventorySkinTargets(recipient).Split(';'));
        var before = received.ToRecord(recipient.Guid);
        recipient.Sent.Clear();
        recipient.Send(InventoryEvent($"skin:{received.Guid}:selected"));
        Assert.DoesNotContain(recipient.Sent, p => Is(p, 0xc8, 3));
        Assert.Equal(before with { DefinitionId = selected.RewardItemId }, received.ToRecord(recipient.Guid));
        Assert.DoesNotContain(received.Guid.ToString(), LastInventorySkinTargets(recipient).Split(';'));
    }
}
