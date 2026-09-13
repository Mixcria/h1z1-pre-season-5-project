using System.Numerics;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Loot;

public sealed partial class InteractionFeedbackTests
{
    [Fact]
    public void PickingUpASmallSpareDoesNotUnequipTheMilitaryPackOrFailItsDowngradeCheck()
    {
        using var world = new Fixture();
        world.Inventory.TryPickUp(2124, 1, out var military);
        world.Inventory.TryPickUp(1429, 670, out _);
        int capacity = world.Inventory.Capacity.Max;
        var item = world.Loot.Spawn(2116, 1, Vector3.Zero);

        world.Send([0x09, 0x15, 0, .. BitConverter.GetBytes(0x1001ul), .. BitConverter.GetBytes(item.WorldGuid)]);
        world.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(item.WorldGuid)]);

        var spare = Assert.Single(world.Inventory.Items.Values, held => held.DefinitionId == 2116);
        Assert.Same(military, world.Inventory.LoadoutSlots[SurvivorLoadout.Backpack]);
        Assert.Equal(world.Inventory.BaseBag!.Guid, spare.ContainerGuid);
        Assert.Equal((1490, capacity), world.Inventory.Capacity);
        Assert.False(world.Loot.TryGet(item.WorldGuid, out _));
        Assert.Single(world.Sent, p => Is(p, 0x11, 0x02));
        Assert.Single(world.Sent, p => Is(p, 0x0f, 0x01));
        Assert.Single(world.Sent, p => Is(p, 0x0f, 0x43));
        Assert.DoesNotContain(world.Sent, p => Is(p, 0xc8, 0x03));
        Assert.DoesNotContain(world.Sent, p => Is(p, 0x94, 0x01) || Is(p, 0x94, 0x02));
    }

    [Fact]
    public void ASpareThatExceedsActualRemainingCapacityStaysOnTheGround()
    {
        using var world = new Fixture();
        world.Inventory.TryPickUp(2124, 1, out var military);
        uint rounds = (uint)((world.Inventory.Capacity.Max - 150) / 2 + 1);
        world.Inventory.TryPickUp(1429, rounds, out _);
        var before = world.Inventory.Capacity;
        var item = world.Loot.Spawn(2116, 1, Vector3.Zero);

        world.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(item.WorldGuid)]);

        Assert.True(world.Loot.TryGet(item.WorldGuid, out _));
        Assert.Same(military, world.Inventory.LoadoutSlots[SurvivorLoadout.Backpack]);
        Assert.Equal(before, world.Inventory.Capacity);
        Assert.Contains(world.Sent, p => Is(p, 0xc8, 0x03));
        Assert.DoesNotContain(world.Sent, p => Is(p, 0x11, 0x02) || Is(p, 0x0f, 0x01) || Is(p, 0x0f, 0x43));
    }
}
