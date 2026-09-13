using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Economy;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Loot;

public sealed partial class BodyBagGatewayTests
{
    [Theory]
    [InlineData(2425u)]
    [InlineData(2229u)]
    [InlineData(2170u)]
    [InlineData(2171u)]
    [InlineData(2172u)]
    [InlineData(2271u)]
    [InlineData(4266u)]
    [InlineData(2124u)]
    [InlineData(2112u)]
    [InlineData(2215u)]
    [InlineData(2445u)]
    [InlineData(2256u)]
    [InlineData(2254u)]
    [InlineData(2081u)]
    public void InventoryWindowEventSkinsEquippedWeaponsAndApparel(uint definition)
    {
        using var world = new World(new ZoneOptions { DynamicAppearanceSourcePath = AppearanceSource });
        var player = world.AddPlayer();
        var inventory = EmptyOutfit(player);
        inventory.TryPickUp(definition, 1, out var item);
        Assert.NotNull(item);
        Assert.True(AugustWornSkins.TryGetCategory(definition, out uint category));
        var selected = AugustSkinCatalog.Apparel.Concat(AugustSkinCatalog.Weapons)
            .First(s => s.CategoryPrototypeId == category && s.RewardItemId != definition);
        SetInventoryPreset(player, selected.AccountItemId);
        world.Call("SendInventoryActionState", player.Connection, player.State);
        Assert.Contains(item.Guid.ToString(), LastInventorySkinTargets(player).Split(';'));
        var before = item.ToRecord(player.Guid);
        player.Sent.Clear();
        player.Send(InventoryEvent($"skin:{item.Guid}:selected"));
        Assert.DoesNotContain(player.Sent, p => Is(p, 0xc8, 3));
        Assert.Equal(selected.RewardItemId, item.DisplayDefinitionId);
        var after = item.ToRecord(player.Guid);
        Assert.Equal(before with { DefinitionId = selected.RewardItemId }, after);
        Assert.Contains(player.Sent, p => Is(p, 0x11, 2) && BitConverter.ToUInt32(p, 15) == selected.RewardItemId);
        Assert.Equal(selected, Assert.Single(Get<AugustWardrobeState>(player.State, "Wardrobe").Snapshot()));
        Assert.DoesNotContain(item.Guid.ToString(), LastInventorySkinTargets(player).Split(';'));
        player.Sent.Clear();
        player.Send(InventoryEvent($"skin:{item.Guid}:selected"));
        Assert.Contains(player.Sent, p => Is(p, 0xc8, 3));
        Assert.DoesNotContain(player.Sent, p => Is(p, 0x11, 2) || Is(p, 0x11, 4));
    }

    [Theory]
    [InlineData(CharacterVisuals.Male, 2158u)]
    [InlineData(CharacterVisuals.Female, 2170u)]
    public void HoodWindowEventUpdatesTheMeshAndOppositeActionState(uint gender, uint headDefinition)
    {
        using var world = new World(new ZoneOptions { DynamicAppearanceSourcePath = AppearanceSource });
        var player = world.AddPlayer();
        Set(player.State, "Gender", gender);
        Set(player.State, "Visuals", CharacterVisuals.FromSelection(gender, 1, 0, 0, 5));
        var inventory = EmptyOutfit(player);
        inventory.TryPickUp(4266, 1, out var hoodie);
        inventory.TryPickUp(2124, 1, out _);
        Assert.NotNull(hoodie);
        Assert.False(inventory.HoodUp);
        player.Sent.Clear();
        player.Send(InventoryEvent($"hood:{hoodie.Guid}:1"));
        Assert.True(inventory.HoodUp);
        AssertHoodAppearance(player, hoodie, gender, true);
        player.Sent.Clear();
        player.Send(InventoryEvent($"hood:{hoodie.Guid}:0"));
        Assert.False(inventory.HoodUp);
        AssertHoodAppearance(player, hoodie, gender, false);
        player.Send(InventoryEvent($"hood:{hoodie.Guid}:1"));
        var bag = world.SpawnBag(player, (headDefinition, 1u));
        var (owner, _) = Assert.Single(player.Bags);
        player.Sent.Clear();
        player.Send(Use(player, Assert.Single(bag.Items.Values).ItemGuid, 59, owner));
        Assert.False(inventory.HoodUp);
        AssertHoodAppearance(player, hoodie, gender, false);
        player.Sent.Clear();
        player.Send(InventoryEvent($"hood:{hoodie.Guid}:1"));
        Assert.False(inventory.HoodUp);
        Assert.Contains(player.Sent, p => Is(p, 0xc8, 3));
        player.Send(Use(player, inventory.LoadoutSlots[SurvivorLoadout.Head].Guid, 12));
        player.Sent.Clear();
        player.Send(InventoryEvent($"hood:{hoodie.Guid}:1"));
        Assert.True(inventory.HoodUp);
        AssertHoodAppearance(player, hoodie, gender, true);
    }

    [Theory]
    [InlineData("stowed")]
    [InlineData("removed")]
    [InlineData("dead")]
    [InlineData("wrong category")]
    [InlineData("reward id")]
    [InlineData("trailing data")]
    [InlineData("no preset")]
    [InlineData("already personal")]
    [InlineData("menu")]
    public void InvalidInventoryWindowEventCannotChangeEquipment(string reason)
    {
        using var world = new World();
        var player = world.AddPlayer();
        player.Inventory.TryPickUp(2124, 1, out _);
        player.Inventory.TryPickUp(2229, 1, out var item);
        player.Inventory.TryPickUp(2229, 1, out var other);
        Assert.NotNull(item); Assert.NotNull(other);
        if (reason != "no preset") SetInventoryPreset(player, reason == "wrong category" ? 2602u : 2599u);
        if (reason == "already personal") Assert.True(player.Inventory.TrySetItemSkin(item.Guid, 2600));
        if (reason == "stowed") Assert.True(player.Inventory.TryStow(item));
        if (reason == "removed") player.Inventory.RemoveUnits(item.Guid, 1);
        if (reason == "dead") Set(player.State, "DeathSent", true);
        if (reason == "menu")
        {
            var match = player.State.GetType().GetProperty("Match")!;
            match.SetValue(player.State, Enum.Parse(match.PropertyType, "Menu"));
        }
        uint before = item.DisplayDefinitionId;
        byte[] packet = InventoryEvent($"skin:{item.Guid}:{(reason == "reward id" ? "2600" : "selected")}");
        if (reason == "trailing data") packet = [.. packet, 1];
        player.Sent.Clear();
        player.Send(packet);
        Assert.Equal(before, item.DisplayDefinitionId);
        Assert.Equal(2229u, other.DisplayDefinitionId);
        Assert.DoesNotContain(player.Sent, p => Is(p, 0x11, 2));
    }

    [Fact]
    public void InventorySkinRequiresTheAccountsOwnCosmeticAndPreservesOtherCopies()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-inventory-skins", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AccountEconomyStore(root);
            store.GetOrCreate("inventory-test", new(Items: [new(123, 2599, 2600, 1, "test")]));
            using var world = new World(new ZoneOptions { EconomyStoreRoot = root });
            var player = world.AddPlayer();
            Set(player.State, "AccountId", "inventory-test");
            player.Inventory.TryPickUp(2229, 1, out var first);
            player.Inventory.TryPickUp(2229, 1, out var second);
            Assert.NotNull(first); Assert.NotNull(second);
            var unowned = AugustSkinCatalog.Weapons.First(s => s.CategoryPrototypeId == 2229 && s.AccountItemId != 2599);
            SetInventoryPreset(player, unowned.AccountItemId);
            world.Call("SendInventoryActionState", player.Connection, player.State);
            Assert.Equal("", LastInventorySkinTargets(player));
            player.Send(InventoryEvent($"skin:{first.Guid}:selected"));
            Assert.Equal(2229u, first.DisplayDefinitionId);
            SetInventoryPreset(player, 2599);
            player.Send(InventoryEvent($"skin:{first.Guid}:selected"));
            Assert.Equal(2600u, first.DisplayDefinitionId);
            Assert.Equal(2229u, second.DisplayDefinitionId);
            Assert.Equal(second.Guid.ToString(), LastInventorySkinTargets(player));
            Assert.Single(store.GetOrCreate("inventory-test").Items);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(2425u)] // The skinless AR-15 carried by a bot.
    [InlineData(2601u)] // A donor's different AR-15 cosmetic.
    [InlineData(4073u)] // The same cosmetic as the recipient's saved choice.
    public void BodyBagAr15KeepsDonorAppearanceUntilSavedSkinIsClicked(uint donorDisplay)
    {
        using var world = new World(new ZoneOptions { DynamicAppearanceSourcePath = AppearanceSource });
        var donor = world.AddPlayer(shared: true);
        var recipient = world.AddPlayer(shared: true);
        SetInventoryPreset(recipient, 4076); // This player's live saved AR-15 -> 4073.
        Set(recipient.State, "Inventory", null!);
        world.Call("EnsureInventory", recipient.Connection, recipient.State, "body bag saved skin test");
        donor.Inventory.TryPickUp(2425, 1, out var original);
        Assert.NotNull(original);
        Assert.True(donor.Inventory.TrySetItemSkin(original.Guid, donorDisplay));
        world.Call("DropPlayerBodyBag", donor.Connection, donor.State);
        var (owner, _) = Assert.Single(recipient.Bags);
        recipient.Sent.Clear();
        recipient.Send(Use(recipient, original.Guid, 59, owner));
        var received = Assert.Single(recipient.Inventory.Items.Values, i => i.DefinitionId == 2425);
        Assert.Equal(donorDisplay, received.DisplayDefinitionId);
        Assert.Equal(donorDisplay != 4073, LastInventorySkinTargets(recipient).Split(';').Contains(received.Guid.ToString()));
        var before = received.ToRecord(recipient.Guid);
        recipient.Sent.Clear();
        recipient.Send(InventoryEvent($"skin:{received.Guid}:selected"));
        Assert.Equal(4073u, received.DisplayDefinitionId);
        if (donorDisplay == 4073)
        {
            Assert.Contains(recipient.Sent, p => Is(p, 0xc8, 3));
            Assert.DoesNotContain(recipient.Sent, p => Is(p, 0x11, 2));
        }
        else
        {
            Assert.Equal(before with { DefinitionId = 4073 }, received.ToRecord(recipient.Guid));
            Assert.Contains(recipient.Sent, p => Is(p, 0x11, 2) && BitConverter.ToUInt32(p, 15) == 4073);
            Assert.DoesNotContain(received.Guid.ToString(), LastInventorySkinTargets(recipient).Split(';'));
            Assert.True(AugustDynamicAppearanceTable.TryLoad(AppearanceSource, out var table, out string status), status);
            var attachments = recipient.Sent.Where(p => Is(p, 0x94, 1) || Is(p, 0x94, 2)).SelectMany(ReadAttachments);
            Assert.Equal(table!.AppearanceRowsFor(4073, CharacterVisuals.Male),
                attachments.Last(a => a.Slot == received.EquipmentSlotId).Rows);
        }
        Assert.Equal(4076u, Assert.Single(Get<AugustWardrobeState>(recipient.State, "Wardrobe").Snapshot()).AccountItemId);
    }

    [Fact]
    public void PersonalPickupNeverOffersSkinAndLegacyRequestsCannotChangeIt()
    {
        using var world = new World();
        var player = world.AddPlayer();
        SetInventoryPreset(player, 4076);
        Set(player.State, "Inventory", null!);
        world.Call("EnsureInventory", player.Connection, player.State, "personal pickup test");
        player.Inventory.TryPickUp(2425, 1, out var item);
        Assert.NotNull(item);
        Assert.Equal(4073u, item.DisplayDefinitionId);
        world.Call("SendInventoryActionState", player.Connection, player.State);
        Assert.DoesNotContain(item.Guid.ToString(), LastInventorySkinTargets(player).Split(';'));
        player.Send(Use(player, item.Guid, 88));
        player.Send(Skin(10, 2602));
        Assert.Equal(4073u, item.DisplayDefinitionId);
        // Replacing the complete native string table must publish both action values again.
        player.Sent.Clear();
        Set(player.State, "InventoryActionUiState", null!);
        world.Call("SendInventoryActionState", player.Connection, player.State);
        Assert.Equal("", LastInventorySkinTargets(player));
        Assert.Equal(2, player.Sent.Count(p => p[0] == ZoneOpcodes.UpdateStringHashToValueManager));
    }

    private static void SetInventoryPreset(Player player, uint accountId)
    {
        Assert.True(AugustWardrobeCatalog.TryResolveClicked(accountId, out var selected));
        Assert.True(Get<AugustWardrobeState>(player.State, "Wardrobe").TryApply(
            new(0x32, 1, 0, 2, selected.CategoryPrototypeId, accountId), out _, out _, out _));
    }

    private static string LastInventorySkinTargets(Player player)
    {
        foreach (var packet in player.Sent.AsEnumerable().Reverse())
        {
            if (packet[0] != ZoneOpcodes.UpdateStringHashToValueManager) continue;
            var reader = new PacketReader(packet);
            reader.ReadByte();
            if (reader.ReadString() == "Cranberry.Inventory.SkinTargets") return reader.ReadString();
        }
        throw new InvalidOperationException("No authoritative skin targets were sent");
    }

    private static PlayerInventory EmptyOutfit(Player player)
    {
        var inventory = new PlayerInventory(player.Guid, player.Loot.NextItemGuid,
            new InventoryOptions { StarterOutfit = [] });
        inventory.Bootstrap();
        Set(player.State, "Inventory", inventory);
        return inventory;
    }

    private static byte[] InventoryEvent(string action)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(ZoneOpcodes.WallOfDataBase); writer.WriteByte(5);
        writer.WriteString("Cranberry.Inventory"); writer.WriteString(action); writer.WriteUInt32(0);
        return writer.Written.ToArray();
    }
}
