using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

public sealed partial class BodyBagGatewayTests
{
    [Theory]
    [InlineData(2209u, "ground")]
    [InlineData(2215u, "ground")]
    [InlineData(2209u, "bag")]
    [InlineData(2215u, "bag")]
    [InlineData(2209u, "drag")]
    [InlineData(2215u, "drag")]
    public void FootwearUpgradeWithAFullInventoryDropsTheOldPairForBothPlayers(uint definition, string sourceKind)
    {
        using var world = new World();
        var player = world.AddPlayer(shared: true);
        var observer = world.AddPlayer(shared: true);
        var old = player.Inventory.LoadoutSlots[SurvivorLoadout.Feet];
        uint oldDisplay = old.DisplayDefinitionId;
        int space = player.Inventory.Capacity.Max - player.Inventory.Capacity.Used;
        InventoryItemFacts.TryGet(1429, out var ammo);
        player.Inventory.TryStow(player.Inventory.CreateInstance(1429, (uint)(space / ammo.Bulk)));
        var before = player.Inventory.Capacity;
        Assert.Equal(before.Max, before.Used);
        if (sourceKind == "ground")
        {
            var source = player.Loot.Spawn(definition, 1, Vector3.Zero);
            player.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(source.WorldGuid)]);
            player.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(source.WorldGuid)]);
        }
        else
        {
            var source = Assert.Single(world.SpawnBag(player, (definition, 1)).Items.Values);
            ulong owner = Assert.Single(player.Bags).Key;
            player.Send(Move(owner, source.ItemGuid, player.Guid, 1,
                sourceKind == "drag" ? PlayerInventory.EquippedContainerGuid : 0,
                sourceKind == "drag" ? (int)SurvivorLoadout.Feet : -1));
        }
        Assert.Equal(definition, player.Inventory.LoadoutSlots[SurvivorLoadout.Feet].DefinitionId);
        Assert.False(player.Inventory.Items.ContainsKey(old.Guid));
        Assert.Equal(before, player.Inventory.Capacity);
        var dropped = Assert.Single(player.Loot.Items);
        Assert.Equal(old.DefinitionId, dropped.ItemDefinitionId);
        Assert.Equal(oldDisplay, dropped.SkinRewardItemId);
        var burst = new List<Action>();
        world.Call("PlanSharedDrops", observer.Connection, observer.State, burst);
        foreach (var action in burst) action();
        Assert.Equal(old.DefinitionId, Assert.Single(observer.Loot.Items).ItemDefinitionId);
        Assert.DoesNotContain(player.Sent, p => Is(p, 0xc8, 3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EquippingCarriedFootwearDropsThePreviousPairEvenWhenCargoHasRoom(bool fullInventory)
    {
        using var world = new World();
        var player = world.AddPlayer();
        var old = player.Inventory.LoadoutSlots[SurvivorLoadout.Feet];
        var carried = player.Inventory.CreateInstance(2215, 1);
        Assert.True(player.Inventory.TryStow(carried));
        if (fullInventory)
        {
            int space = player.Inventory.Capacity.Max - player.Inventory.Capacity.Used;
            InventoryItemFacts.TryGet(23, out var cloth);
            Assert.True(player.Inventory.TryStow(player.Inventory.CreateInstance(23, (uint)(space / cloth.Bulk))));
            Assert.Equal(player.Inventory.Capacity.Max, player.Inventory.Capacity.Used);
        }
        player.Send(Use(player, carried.Guid, 60));
        Assert.Same(carried, player.Inventory.LoadoutSlots[SurvivorLoadout.Feet]);
        Assert.False(player.Inventory.Items.ContainsKey(old.Guid));
        Assert.Equal(old.DefinitionId, Assert.Single(player.Loot.Items).ItemDefinitionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CamoShirtPickupKeepsAnOccupiedChestAndOnlyEquippedClothingUsesTheSavedSkin(bool bareChest)
    {
        using var world = new World(new ZoneOptions { DynamicAppearanceSourcePath = AppearanceSource });
        var player = world.AddPlayer();
        uint camo = LootTables.LoadDefault().Categories.ToArray().SelectMany(c => c.Entries.ToArray())
            .First(i => i.Name == "Forest Camo T-Shirt").ItemDefinitionId;
        Assert.True(AugustWornSkins.TryGetCategory(camo, out uint category));
        var hoodie = AugustSkinCatalog.Apparel.First(s => s.CategoryPrototypeId == category
            && ItemUseOptionTable.Allows(s.RewardItemId, 96) && ItemUseOptionTable.Allows(s.RewardItemId, 97));
        Assert.True(Get<AugustWardrobeState>(player.State, "Wardrobe")
            .TryApply(new(0x32, 1, 0, 2, category, hoodie.AccountItemId), out _, out _, out _));
        Set(player.State, "Inventory", null!);
        world.Call("EnsureInventory", player.Connection, player.State, "clothing skin test");
        var original = player.Inventory.LoadoutSlots[SurvivorLoadout.Chest];
        Assert.Equal(hoodie.RewardItemId, original.DisplayDefinitionId);
        if (bareChest) player.Inventory.RemoveUnits(original.Guid, 0);
        var source = player.Loot.Spawn(camo, 1, Vector3.Zero);
        player.Sent.Clear();
        player.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(source.WorldGuid)]);
        var received = Assert.Single(player.Inventory.Items.Values, i => i.DefinitionId == camo);
        if (bareChest)
        {
            Assert.Same(received, player.Inventory.LoadoutSlots[SurvivorLoadout.Chest]);
            Assert.Equal(hoodie.RewardItemId, received.DisplayDefinitionId);
        }
        else
        {
            Assert.Same(original, player.Inventory.LoadoutSlots[SurvivorLoadout.Chest]);
            Assert.Equal(hoodie.RewardItemId, original.DisplayDefinitionId);
            Assert.Equal(player.Inventory.BaseBag!.Guid, received.ContainerGuid);
            Assert.Equal(camo, received.DisplayDefinitionId);
            Assert.DoesNotContain(player.Sent, p => Is(p, 0xac, 0x24) || Is(p, 0x94, 1) || Is(p, 0x94, 2));
        }
        Assert.Contains(player.Sent, p => Is(p, 0x11, 2)
            && BitConverter.ToUInt32(p, 15) == received.DisplayDefinitionId);
    }

    [Theory]
    [InlineData(1997u, 9423u, 0u)]
    [InlineData(1718u, 9483u, 0u)]
    [InlineData(1374u, 9286u, 29u)]
    public void GroundPistolsKeepTheirAuthoredMaterialsWhileTintableGunsKeepTheirShader(uint itemId, uint model, uint expected)
    {
        using var world = new World(new ZoneOptions { DynamicAppearanceSourcePath = AppearanceSource, GroundLootShader = true });
        var player = world.AddPlayer();
        player.Sent.Clear();
        var ground = (GroundLootItem)world.Call("SpawnGroundLoot", player.Connection, player.State,
            itemId, model, Vector3.Zero, 1u, 0u, null, 0u, null)!;
        var expectedPacket = new AddLightweightItem(ground.WorldGuid, ground.TransientId, model, Vector3.Zero,
            ShaderGroupId: expected);
        using var writer = new Cranberry.Protocol.PacketWriter();
        expectedPacket.WriteTo(writer);
        Assert.Equal(writer.Written.ToArray(), Assert.Single(player.Sent, p => p[0] == 0xd6));
    }

    [Fact]
    public void NaturalLootDoesNotContainBandagesAndBothPistolsHaveTheirOwnAmmunitionClusters()
    {
        var tables = LootTables.LoadDefault();
        var entries = tables.Categories.ToArray().SelectMany(c => c.Entries.ToArray()).ToArray();
        Assert.DoesNotContain(entries, item => item.ItemDefinitionId == 2423);
        var airdrops = AirdropTables.LoadDefault();
        Assert.DoesNotContain(airdrops.Guaranteed.Concat(airdrops.Rifle).Concat(airdrops.Pool)
            .SelectMany(bundle => bundle.Items), item => item.ItemDefinitionId == 2423);
        foreach (var (gun, model, ammo) in new[] { (1997u, 9423u, 1998u), (1718u, 9483u, 1719u) })
        {
            Assert.Contains(entries, item => item.ItemDefinitionId == gun && item.GroundModelId == model && item.Weight > 0);
            Assert.Contains(tables.Clusters.ToArray(), item => item.WeaponItemDefinitionId == gun && item.ItemDefinitionId == ammo);
        }
    }
}
