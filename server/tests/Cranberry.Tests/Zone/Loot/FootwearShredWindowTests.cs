using System.Numerics;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Loot;

public sealed partial class BodyBagGatewayTests
{
    [Theory]
    [InlineData(2209u, "worn")]
    [InlineData(2215u, "worn")]
    [InlineData(2209u, "backpack")]
    [InlineData(2215u, "backpack")]
    [InlineData(2209u, "ground")]
    [InlineData(2215u, "ground")]
    [InlineData(2209u, "body bag")]
    [InlineData(2215u, "body bag")]
    public async Task FootwearMenuClickConsumesOneSourceAndGivesTwoCompositeFabric(uint definition, string location)
    {
        using var world = new World();
        var player = world.AddPlayer();
        var inventory = EmptyOutfit(player);
        inventory.TryPickUp(2124, 1, out _);
        ulong source;
        if (location == "ground") source = player.Loot.Spawn(definition, 1, Vector3.Zero).WorldGuid;
        else if (location == "body bag")
            source = Assert.Single(world.SpawnBag(player, (definition, 1)).Items.Values).ItemGuid;
        else
        {
            inventory.TryPickUp(definition, 1, out var shoes);
            Assert.NotNull(shoes);
            source = shoes.Guid;
            Assert.Equal(source, inventory.LoadoutSlots[SurvivorLoadout.Feet].Guid);
            if (location == "backpack") Assert.True(inventory.TryStow(shoes));
        }
        var posted = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Service.Post = work => posted.TrySetResult(work);
        player.Sent.Clear();
        byte[] click = InventoryEvent($"shred:{source}:1");
        player.Send(click);
        Assert.Contains(player.Sent, p => Is(p, 0xcf, 2));
        Assert.DoesNotContain(player.Sent, p => Is(p, 0xc8, 3));
        Assert.DoesNotContain(inventory.Items.Values, i => i.DefinitionId == CraftingCatalog.CompositeFabric);
        (await posted.Task.WaitAsync(TimeSpan.FromSeconds(4)))();
        Assert.False(inventory.Items.ContainsKey(source));
        Assert.Empty(player.Loot.Items);
        Assert.Empty(player.Bags);
        Assert.Equal(2u, Assert.Single(inventory.Items.Values, i => i.DefinitionId == CraftingCatalog.CompositeFabric).Count);
        Assert.Contains(player.Sent, p => Is(p, 0xcf, 3));
        player.Send(click);
        Assert.Equal(2u, inventory.Items.Values.Single(i => i.DefinitionId == CraftingCatalog.CompositeFabric).Count);
    }

    [Theory]
    [InlineData("dead")]
    [InlineData("busy")]
    [InlineData("menu")]
    [InlineData("missing")]
    [InlineData("other player")]
    [InlineData("stealth")]
    [InlineData("helmet")]
    [InlineData("quantity")]
    [InlineData("trailing data")]
    [InlineData("distant ground")]
    [InlineData("distant body bag")]
    public void InvalidFootwearMenuClickLeavesTheItemAndMaterialsUntouched(string reason)
    {
        using var world = new World();
        var player = world.AddPlayer();
        var sourcePlayer = reason == "other player" ? world.AddPlayer() : player;
        uint definition = reason == "stealth" ? 3711u : reason == "helmet" ? 2172u : 2209u;
        sourcePlayer.Inventory.TryPickUp(definition, 1, out var shoes);
        Assert.NotNull(shoes);
        ulong guid = shoes.Guid;
        if (reason == "missing") guid = ulong.MaxValue;
        if (reason == "dead") Set(player.State, "DeathSent", true);
        if (reason == "busy") Set(player.State, "ShredBusyUntil", Environment.TickCount64 + 60_000);
        if (reason == "menu")
        {
            var match = player.State.GetType().GetProperty("Match")!;
            match.SetValue(player.State, Enum.Parse(match.PropertyType, "Menu"));
        }
        if (reason == "distant ground") guid = player.Loot.Spawn(definition, 1, new(40, 0, 0)).WorldGuid;
        if (reason == "distant body bag")
        {
            guid = Assert.Single(world.SpawnBag(player, (definition, 1)).Items.Values).ItemGuid;
            player.Move(new(40, 0, 0));
        }
        player.Sent.Clear();
        byte[] click = InventoryEvent($"shred:{guid}:{(reason == "quantity" ? "2" : "1")}");
        if (reason == "trailing data") click = [.. click, 1];
        player.Send(click);
        Assert.True(sourcePlayer.Inventory.Items.ContainsKey(shoes.Guid));
        Assert.DoesNotContain(player.Inventory.Items.Values, i => i.DefinitionId == CraftingCatalog.CompositeFabric);
        Assert.DoesNotContain(player.Sent, p => Is(p, 0xcf, 2));
    }
}
