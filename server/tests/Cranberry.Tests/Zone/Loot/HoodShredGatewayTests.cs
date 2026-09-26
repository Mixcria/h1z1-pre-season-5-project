using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Loot;

public sealed partial class BodyBagGatewayTests
{
    [Fact]
    public async Task RefusedHoodShredRetainsNativeAppearanceAndStopsItsCast()
    {
        using var world = new World(new ZoneOptions { DynamicAppearanceSourcePath = AppearanceSource });
        var player = world.AddPlayer();
        var inventory = new PlayerInventory(player.Guid, player.Loot.NextItemGuid,
            new InventoryOptions { StarterOutfit = [3405], BaseCarryBulk = 1 });
        inventory.Bootstrap();
        Set(player.State, "Inventory", inventory);
        var hoodie = inventory.LoadoutSlots[SurvivorLoadout.Chest];
        player.Send(Use(player, hoodie.Guid, 96));
        Assert.True(inventory.HoodUp);
        var originalChest = player.Sent.Where(p => Is(p, 0x94, 1) || Is(p, 0x94, 2))
            .SelectMany(ReadAttachments).Last(a => a.Slot == BodySlots.Chest);
        Assert.Contains("_Hoodie_Up", originalChest.Model);
        var itemsBefore = inventory.Items.Keys.Order().ToArray();
        player.Sent.Clear();
        var posted = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Service.Post = work => posted.TrySetResult(work);

        player.Send(Use(player, hoodie.Guid, 6));
        Assert.Single(player.Sent, p => Is(p, 0xcf, 2));
        Assert.NotNull(Get<object?>(player.State, "PendingShred"));
        (await posted.Task.WaitAsync(TimeSpan.FromSeconds(4)))();

        Assert.Null(Get<object?>(player.State, "PendingShred"));
        Assert.Equal(0, Get<long>(player.State, "ShredBusyUntil"));
        Assert.Contains(player.Sent, p => Is(p, 0xc8, 3));
        Assert.Contains(player.Sent, p => Is(p, 0xcf, 3));
        Assert.DoesNotContain(player.Sent, p => Is(p, 0x11, 2) || Is(p, 0x11, 4));
        Assert.DoesNotContain(player.Sent, p => Is(p, 0x94, 1) || Is(p, 0x94, 2));
        Assert.Equal(itemsBefore, inventory.Items.Keys.Order());
        Assert.Equal(1u, hoodie.Count);
        Assert.Same(hoodie, inventory.LoadoutSlots[SurvivorLoadout.Chest]);
        Assert.Same(hoodie, inventory.EquipmentSlots[BodySlots.Chest]);
        Assert.True(inventory.HoodUp);

        // An ordinary refresh is identical and correctly suppressed after the refused shred.
        player.Sent.Clear();
        var dress = Get<AugustDressSuppressor>(player.State, "Dress");
        long suppressed = dress.Suppressed;
        world.Call("SendCharacterAppearance", player.Connection, player.State, "after refused shred");
        Assert.Equal(suppressed + 1, dress.Suppressed);

        // Forget only the outbound baseline, as actor recreation does, to inspect a full rebuild.
        dress.Forget();
        world.Call("SendCharacterAppearance", player.Connection, player.State, "rebuilt actor baseline");
        var retainedChest = player.Sent.Where(p => Is(p, 0x94, 1) || Is(p, 0x94, 2))
            .SelectMany(ReadAttachments).Last(a => a.Slot == BodySlots.Chest);
        Assert.Equal(originalChest.Model, retainedChest.Model);
        Assert.Equal(originalChest.Rows, retainedChest.Rows);

        // Refusal releases the cast; retrying with capacity can consume the same source normally.
        Assert.Equal(InventoryPlacementKind.LoadoutSlot,
            inventory.TryPickUp(2124, 1, out var backpack).Kind);
        Assert.NotNull(backpack);
        posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        player.Sent.Clear();
        player.Send(Use(player, hoodie.Guid, 6));
        (await posted.Task.WaitAsync(TimeSpan.FromSeconds(4)))();
        Assert.Null(Get<object?>(player.State, "PendingShred"));
        Assert.DoesNotContain(player.Sent, p => Is(p, 0xc8, 3));
        Assert.Contains(player.Sent, p => Is(p, 0xcf, 3));
        Assert.DoesNotContain(hoodie.Guid, inventory.Items.Keys);
        Assert.False(inventory.HoodUp);
        Assert.True(ShredTable.IsShreddable(3405, out var yield));
        Assert.Equal(yield.Quantity, Assert.Single(inventory.Items.Values,
            i => i.DefinitionId == yield.ItemDefinitionId).Count);
    }
}
