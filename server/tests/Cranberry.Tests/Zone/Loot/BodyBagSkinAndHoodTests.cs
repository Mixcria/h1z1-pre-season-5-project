using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

[Collection(Cranberry.Tests.Zone.Appearance.AppearanceStaticsCollection.Name)]
public sealed partial class BodyBagGatewayTests
{
    private const string AppearanceSource = @"C:\Z1\Server\Data\dynamicAppearanceFriend.bin";

    [Theory]
    [InlineData(2229u, 2600u)]
    [InlineData(2124u, 2778u)]
    [InlineData(2170u, 2170u)]
    [InlineData(3405u, 3405u)]
    public void DeathLootKeepsItsAppearanceThroughRightClickPickupAndAnotherDrop(uint definition, uint display)
    {
        using var world = new World();
        var donor = world.AddPlayer(shared: true);
        var recipient = world.AddPlayer(shared: true);
        var choices = AugustSkinCatalog.Apparel.Concat(AugustSkinCatalog.Weapons).ToArray();
        Assert.True(AugustWornSkins.TryGetCategory(definition, out uint category));
        var other = choices.First(s => s.CategoryPrototypeId == category && s.RewardItemId != display);
        var wardrobe = Get<AugustWardrobeState>(recipient.State, "Wardrobe");
        Assert.True(wardrobe.TryApply(new(0x32, 1, 0, 2, category, other.AccountItemId), out _, out _, out _));
        // Use the production inventory initializer, including its saved-skin resolver.
        Set(recipient.State, "Inventory", null!);
        world.Call("EnsureInventory", recipient.Connection, recipient.State, "skin transfer test");
        donor.Inventory.TryPickUp(definition, 1, out var original);
        Assert.NotNull(original);
        Assert.True(donor.Inventory.TrySetItemSkin(original.Guid, display));
        world.Call("DropPlayerBodyBag", donor.Connection, donor.State);
        var (owner, bag) = Assert.Single(recipient.Bags);
        Assert.Equal(display, bag.Items[original.Guid].DefinitionId);
        Assert.Equal(definition, bag.GameplayDefinitionFor(original.Guid));
        Assert.Equal(display, bag.Records(owner).Single(i => i.ItemGuid == original.Guid).DefinitionId);

        recipient.Sent.Clear();
        recipient.Send(Use(recipient, original.Guid, 59, owner));
        var received = Assert.Single(recipient.Inventory.Items.Values, i => i.DefinitionId == definition);
        Assert.Equal(display, received.DisplayDefinitionId);
        Assert.Equal(other.AccountItemId, Assert.Single(wardrobe.Snapshot(), s => s.CategoryPrototypeId == category).AccountItemId);
        Assert.Contains(recipient.Sent, p => Is(p, 0x11, 2) && BitConverter.ToUInt32(p, 15) == display);
        Assert.DoesNotContain(recipient.Sent, p => Is(p, 0xc8, 3));
        recipient.Send(Use(recipient, original.Guid, 59, owner));
        Assert.Single(recipient.Inventory.Items.Values, i => i.DefinitionId == definition);

        recipient.Send(Use(recipient, received.Guid, 4));
        var dropped = Assert.Single(recipient.Loot.Items, i => i.ItemDefinitionId == definition);
        Assert.Equal(display, dropped.SkinRewardItemId);
        recipient.Send(Move(dropped.WorldGuid, dropped.WorldGuid, recipient.Guid, 1));
        var repicked = Assert.Single(recipient.Inventory.Items.Values, i => i.DefinitionId == definition);
        Assert.Equal(display, repicked.DisplayDefinitionId);
    }

    [Fact]
    public void SkinChooserChangesOnlyTheClickedWeaponAndCannotRunOnABackpackWeapon()
    {
        using var world = new World(new ZoneOptions { DynamicAppearanceSourcePath = AppearanceSource });
        var player = world.AddPlayer();
        player.Inventory.TryPickUp(2124, 1, out _);
        player.Inventory.TryPickUp(2229, 1, out var first);
        player.Inventory.TryPickUp(2229, 1, out var second);
        Assert.NotNull(first); Assert.NotNull(second);
        Assert.True(player.Inventory.TrySetItemSkin(first.Guid, 2662));
        Assert.True(player.Inventory.TrySetItemSkin(second.Guid, 2662));
        player.Send(Use(player, first.Guid, 88));
        player.Send(Skin(2229, 2599));
        Assert.Equal(2600u, first.DisplayDefinitionId);
        Assert.Equal(2662u, second.DisplayDefinitionId);
        Assert.True(AugustDynamicAppearanceTable.TryLoad(AppearanceSource, out var table, out string status), status);
        var attachments = player.Sent.Where(p => Is(p, 0x94, 1) || Is(p, 0x94, 2)).SelectMany(ReadAttachments).ToArray();
        Assert.Equal(table!.AppearanceRowsFor(2600, CharacterVisuals.Male),
            attachments.Last(a => a.Slot == first.EquipmentSlotId).Rows);
        Assert.Equal(table.AppearanceRowsFor(2662, CharacterVisuals.Male),
            attachments.Last(a => a.Slot == second.EquipmentSlotId).Rows);
        Assert.Empty(Get<AugustWardrobeState>(player.State, "Wardrobe").Snapshot());
        Assert.True(player.Inventory.TryStow(second));
        player.Sent.Clear();
        player.Send(Use(player, second.Guid, 88));
        player.Send(Skin(2229, 2599));
        Assert.Equal(2662u, second.DisplayDefinitionId);
        Assert.Contains(player.Sent, p => Is(p, 0xc8, 3));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stowed")]
    [InlineData("expired")]
    [InlineData("category")]
    [InlineData("dead")]
    [InlineData("unknown skin")]
    public void InvalidOrStaleSkinRequestsLeaveTheItemUnchanged(string reason)
    {
        using var world = new World();
        var player = world.AddPlayer();
        player.Inventory.TryPickUp(2124, 1, out _);
        player.Inventory.TryPickUp(2229, 1, out var gun);
        Assert.NotNull(gun);
        if (reason != "missing") player.Send(Use(player, gun.Guid, 88));
        if (reason == "stowed") Assert.True(player.Inventory.TryStow(gun));
        if (reason == "expired") Set(player.State, "InventorySkinTargetExpiresAtMs", 0L);
        if (reason == "dead") Set(player.State, "DeathSent", true);
        player.Sent.Clear();
        player.Send(Skin(reason == "category" ? 10u : 2229u, reason == "unknown skin" ? uint.MaxValue : 2599u));
        Assert.Equal(2229u, gun.DisplayDefinitionId);
        Assert.Contains(player.Sent, p => Is(p, 0xc8, 3));
        Assert.DoesNotContain(player.Sent, p => Is(p, 0x11, 2) || Is(p, 0x11, 4));
        Assert.Empty(Get<AugustWardrobeState>(player.State, "Wardrobe").Snapshot());
    }

    [Theory]
    [InlineData(2158u, CharacterVisuals.Male, 3405u)]
    [InlineData(2170u, CharacterVisuals.Male, 3405u)]
    [InlineData(2158u, CharacterVisuals.Female, 3405u)]
    [InlineData(2170u, CharacterVisuals.Female, 3405u)]
    [InlineData(2158u, CharacterVisuals.Male, 4266u)]
    [InlineData(2170u, CharacterVisuals.Male, 4266u)]
    [InlineData(2158u, CharacterVisuals.Female, 4266u)]
    [InlineData(2170u, CharacterVisuals.Female, 4266u)]
    public void HoodStartsDownTogglesAndHeadPickupForcesItDown(uint headDefinition, uint gender, uint hoodDefinition)
    {
        Assert.True(File.Exists(AppearanceSource));
        using var world = new World(new ZoneOptions { DynamicAppearanceSourcePath = AppearanceSource });
        var player = world.AddPlayer();
        Set(player.State, "Gender", gender);
        Set(player.State, "Visuals", CharacterVisuals.FromSelection(gender, 1, 0, 0, 5));
        var inventory = new PlayerInventory(player.Guid, player.Loot.NextItemGuid,
            new InventoryOptions { StarterOutfit = [hoodDefinition] });
        inventory.Bootstrap(); Set(player.State, "Inventory", inventory);
        inventory.TryPickUp(2124, 1, out _); // Room to take off either kind of headwear.
        var hoodie = inventory.LoadoutSlots[SurvivorLoadout.Chest];
        world.Call("SendCharacterAppearance", player.Connection, player.State, "hood test");
        Assert.False(inventory.HoodUp);
        AssertHoodAppearance(player, hoodie, gender, up: false);

        player.Sent.Clear(); player.Send(Use(player, hoodie.Guid, 96));
        Assert.True(inventory.HoodUp);
        AssertHoodAppearance(player, hoodie, gender, up: true);

        var bag = world.SpawnBag(player, (headDefinition, 1u));
        var (owner, _) = Assert.Single(player.Bags);
        var head = Assert.Single(bag.Items.Values);
        player.Sent.Clear(); player.Send(Use(player, head.ItemGuid, 59, owner));
        Assert.False(inventory.HoodUp);
        AssertHoodAppearance(player, hoodie, gender, up: false);
        player.Sent.Clear(); player.Send(Use(player, hoodie.Guid, 96));
        Assert.False(inventory.HoodUp);
        Assert.Contains(player.Sent, p => Is(p, 0xc8, 3));

        var equippedHead = inventory.LoadoutSlots[SurvivorLoadout.Head];
        player.Send(Use(player, equippedHead.Guid, 12));
        Assert.False(inventory.HoodUp);
        Assert.Equal(0u, equippedHead.LoadoutSlotId); // A hat carried in the bag does not block the hood.
        player.Sent.Clear(); player.Send(Use(player, hoodie.Guid, 96));
        Assert.True(inventory.HoodUp);
        AssertHoodAppearance(player, hoodie, gender, up: true);
        player.Sent.Clear(); player.Send(Use(player, hoodie.Guid, 97));
        Assert.False(inventory.HoodUp);
        AssertHoodAppearance(player, hoodie, gender, up: false);
    }

    private static void AssertHoodAppearance(Player player, InventoryItemInstance hoodie, uint gender, bool up)
    {
        Assert.True(AugustDynamicAppearanceTable.TryLoad(AppearanceSource, out var table, out string status), status);
        var chest = player.Sent.Where(p => Is(p, 0x94, 1) || Is(p, 0x94, 2))
            .SelectMany(ReadAttachments).Last(a => a.Slot == BodySlots.Chest);
        Assert.Contains((gender == CharacterVisuals.Male ? "SurvivorMale" : "SurvivorFemale")
            + "_Chest_Hoodie_" + (up ? "Up" : "Down"), chest.Model);
        Assert.Equal(table!.AppearanceRowsForHood(hoodie.DisplayDefinitionId, up, gender), chest.Rows);
        var setting = new PacketReader(player.Sent.Last(p => p[0] == ZoneOpcodes.UpdateStringHashToValueManager));
        Assert.Equal(0xfc, setting.ReadByte());
        Assert.Equal("Cranberry.Inventory.Hood", setting.ReadString());
        Assert.Equal($"{hoodie.Guid}:{(up ? 1 : 0)}", setting.ReadString());
        Assert.False(setting.ReadBool());
    }

    private static List<(uint Slot, string Model, uint[] Rows)> ReadAttachments(byte[] packet)
    {
        var reader = new PacketReader(packet);
        reader.ReadByte(); byte sub = reader.ReadByte(); reader.ReadUInt32(); reader.ReadUInt64();
        if (sub == 1)
        {
            reader.ReadUInt32(); reader.ReadString(); reader.ReadString();
            int slots = reader.ReadInt32();
            for (int i = 0; i < slots; i++)
            { reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt64(); reader.ReadString(); reader.ReadString(); }
        }
        else { reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt64(); reader.ReadString(); reader.ReadString(); }
        int count = sub == 1 ? reader.ReadInt32() : 1;
        var result = new List<(uint, string, uint[])>();
        for (int i = 0; i < count; i++)
        {
            string model = reader.ReadString(); reader.ReadString(); reader.ReadString(); reader.ReadString();
            reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt32();
            uint slot = reader.ReadUInt32(); reader.ReadUInt32();
            var rows = new uint[reader.ReadInt32()];
            for (int j = 0; j < rows.Length; j++) rows[j] = reader.ReadUInt32();
            reader.ReadByte();
            result.Add((slot, model, rows));
        }
        return result;
    }

    private static byte[] Use(Player player, ulong item, uint option, ulong source = 0)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(0xac); writer.WriteByte(0x2c); writer.WriteUInt64(1); writer.WriteUInt32(option);
        writer.WriteUInt64(player.Guid); writer.WriteUInt64(source == 0 ? player.Guid : source);
        writer.WriteUInt64(player.Guid); writer.WriteUInt64(item); writer.WriteByte(1);
        return writer.Written.ToArray();
    }

    private static byte[] Skin(uint category, uint account)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(0xac); writer.WriteByte(0x32); writer.WriteUInt32(1); writer.WriteUInt32(0);
        writer.WriteUInt32(2); writer.WriteUInt32(category); writer.WriteUInt32(account);
        return writer.Written.ToArray();
    }
}
