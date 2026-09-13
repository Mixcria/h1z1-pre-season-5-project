using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Movement;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Inventory;

public sealed class FootwearTests
{
    public static IEnumerable<object[]> FootwearYields() => FootwearItems.Tiers.Select(pair =>
        new object[] { pair.Key, pair.Value == FootwearTier.Stealth ? 23u : 3500u });

    [Theory]
    [MemberData(nameof(FootwearYields))]
    public void EveryFootwearVariantHasTheCorrectYieldIncludingLegacyMode(uint item, uint output)
    {
        foreach (bool everyOfferedItem in new[] { false, true })
        {
            Assert.True(Footwear.AllowsShred(item, 63));
            Assert.True(ShredTable.IsShreddable(item, out var yield, everyOfferedItem));
            Assert.Equal(new RecipeIngredient(output, 2), yield);
        }
    }

    [Theory]
    [InlineData(2209u)]
    [InlineData(2215u)]
    public void WornFootwearShredConsumesOnePairAndGrantsTwoCompositeFabric(uint definition)
    {
        var inventory = Create();
        inventory.TryPickUp(2124, 1, out _);
        inventory.TryPickUp(definition, 1, out var shoes);
        Assert.Equal(SurvivorLoadout.Feet, shoes!.LoadoutSlotId);
        ShredTable.Shred(inventory, shoes.Guid);
        Assert.False(inventory.Items.ContainsKey(shoes.Guid));
        Assert.False(inventory.LoadoutSlots.ContainsKey(SurvivorLoadout.Feet));
        Assert.Equal(2u, Assert.Single(inventory.Items.Values,
            item => item.DefinitionId == CraftingCatalog.CompositeFabric).Count);
    }

    private static PlayerInventory Create()
    {
        ulong guid = 500;
        var inventory = new PlayerInventory(100, () => ++guid);
        inventory.Bootstrap();
        return inventory;
    }

    [Fact]
    public void StarterIsStealthAndOnlyBetterPickupsReplaceItWithAGroundDisplacement()
    {
        var inventory = Create();
        Assert.Equal(InventoryPlacementKind.LoadoutSlot, inventory.TryPickUp(2124, 1, out _).Kind);
        Assert.Equal(3711u, inventory.LoadoutSlots[SurvivorLoadout.Feet].DefinitionId);
        ulong stealth = inventory.LoadoutSlots[SurvivorLoadout.Feet].Guid;
        var sturdyPlan = inventory.TryPickUp(2209, 1, out var sturdy);
        Assert.Equal(InventoryPlacementKind.LoadoutSlot, sturdyPlan.Kind);
        Assert.Equal(3711u, sturdyPlan.GroundDrop!.DefinitionId);
        Assert.False(inventory.Items.ContainsKey(stealth));
        Assert.Equal(FootwearTier.Sturdy, Footwear.Equipped(inventory));
        Assert.Equal(InventoryPlacementKind.Container, inventory.TryPickUp(2563, 1, out _).Kind);
        Assert.Equal(FootwearTier.Sturdy, Footwear.Equipped(inventory));
        var fastPlan = inventory.TryPickUp(2215, 1, out var fast);
        Assert.Equal(InventoryPlacementKind.LoadoutSlot, fastPlan.Kind);
        Assert.Equal(2209u, fastPlan.GroundDrop!.DefinitionId);
        Assert.False(inventory.Items.ContainsKey(sturdy!.Guid));
        Assert.Equal(FootwearTier.Fast, Footwear.Equipped(inventory));
        Assert.Equal(InventoryPlacementKind.Container, inventory.TryPickUp(2209, 1, out _).Kind);
        Assert.Equal(InventoryPlacementKind.Container, inventory.TryPickUp(2215, 1, out _).Kind);
        inventory.Unbind(fast!);
        Assert.Equal(FootwearTier.Barefoot, Footwear.Equipped(inventory));
    }

    [Theory]
    [InlineData(2209u, 63u)]
    [InlineData(2215u, 63u)]
    [InlineData(3711u, 6u)]
    public void TheInventoryDispatcherAllowsSalvagingFootwear(uint itemId, uint option)
    {
        var inventory = Create();
        inventory.TryPickUp(2124, 1, out _);
        inventory.TryPickUp(itemId, 1, out var item);
        var request = new RequestUseItem(0, option, inventory.CharacterGuid, 0, 0, item!.Guid, true, 1, 0);
        Assert.Equal(ItemActionKind.Shred, InventoryActions.Resolve(inventory, request).Kind);
    }

    [Theory]
    [InlineData(2144u, 23u, 4u)]
    [InlineData(2065u, 23u, 4u)]
    [InlineData(2324u, 23u, 1u)]
    [InlineData(2148u, 23u, 1u)]
    [InlineData(2172u, 3499u, 2u)]
    [InlineData(2209u, 3500u, 2u)]
    [InlineData(2215u, 3500u, 2u)]
    [InlineData(3711u, 23u, 2u)]
    public void SalvageProducesTheRequestedMaterials(uint item, uint output, uint count)
    {
        Assert.True(ShredTable.IsShreddable(item, out var yield));
        Assert.Equal(new RecipeIngredient(output, count), yield);
    }

    [Theory]
    [InlineData(FootwearTier.Barefoot, 1f, "Barefoot")]
    [InlineData(FootwearTier.Stealth, 1.05f, "Silent")]
    [InlineData(FootwearTier.Sturdy, 1.12f, "Boot")]
    [InlineData(FootwearTier.Fast, 1.16f, "Sneaker")]
    public void MovementAndNativeAudioFollowTheEquippedTier(FootwearTier tier, float multiplier, string sound)
    {
        var baseline = MovementProfile.Default;
        var adjusted = Footwear.Apply(baseline, tier);
        Assert.Equal(baseline.WalkSpeed * multiplier, adjusted.WalkSpeed, 4);
        Assert.Equal(baseline.SprintSpeed * multiplier, adjusted.SprintSpeed, 4);
        Assert.Equal(adjusted, Footwear.Apply(baseline, tier)); // repeated refreshes never compound
        var reader = new PacketReader(Footwear.AudioPacket(100, tier));
        Assert.Equal(0xdc, reader.ReadByte());
        Assert.Equal(2, reader.ReadByte());
        Assert.Equal(100UL, reader.ReadUInt64());
        Assert.Equal("ShoeType", reader.ReadString());
        Assert.Equal(sound, reader.ReadString());
        Assert.True(reader.AtEnd);
    }

    [Fact]
    public void ServerEngineCommandCannotBeSuppressedAsALocalPlayerEcho()
    {
        var engine = VehicleEngine.ServerIssued(200, true);
        using var writer = new PacketWriter();
        engine.WriteTo(writer);
        Assert.Equal(0UL, BitConverter.ToUInt64(writer.Written[2..10]));
        Assert.Equal(200UL, BitConverter.ToUInt64(writer.Written[10..18]));
        Assert.Equal(1, writer.Written[18]);
    }
}
