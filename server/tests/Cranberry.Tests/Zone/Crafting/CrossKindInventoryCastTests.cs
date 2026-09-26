using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Crafting;

public sealed partial class PendingInventoryCastTests
{
    private static readonly string[] CastKinds = ["craft", "carried-shred", "ground-shred", "consume"];

    public static IEnumerable<object[]> CrossKindCastPairs()
    {
        foreach (string first in CastKinds)
            foreach (string second in CastKinds)
                if (first != second) yield return [first, second];
    }

    [Theory]
    [MemberData(nameof(CrossKindCastPairs))]
    public async Task CrossKindCast_QueuedCompletionRetainsTheSingleInteractionTimer(string first, string second)
    {
        using var player = new Player();
        Action startFirst = PrepareCast(player, first);
        Action startSecond = PrepareCast(player, second);
        var before = Snapshot(player.Inventory);
        startFirst();
        object? owner = player.Get<object?>(CastProperty(first));
        Assert.NotNull(owner);
        Action completion = await player.NextCompletion();
        Assert.True(player.Get<long>(CastDeadline(first)) <= Environment.TickCount64);

        // The real timer has elapsed, but its listener callback is deliberately held.
        // A different verb must not replace the bar or commit an independent timer.
        startSecond();
        Assert.Same(owner, player.Get<object?>(CastProperty(first)));
        Assert.Single(player.Sent, p => Is(p, 0xcf, 2));
        Assert.DoesNotContain(player.Sent, p => Is(p, 0xcf, 3));
        Assert.Contains(player.Sent, p => Is(p, 0xc8, 3));
        Assert.Equal(before, Snapshot(player.Inventory));
        if (CastProperty(second) != CastProperty(first))
            Assert.Null(player.Get<object?>(CastProperty(second)));

        completion();
        Assert.Null(player.Get<object?>(CastProperty(first)));
        Assert.Contains(player.Sent, p => Is(p, 0xcf, 3));
        Assert.False(before.SequenceEqual(Snapshot(player.Inventory)));
        var afterFirst = Snapshot(player.Inventory);
        startSecond();
        object? next = player.Get<object?>(CastProperty(second));
        Assert.NotNull(next);
        Assert.Equal(2, player.Sent.Count(p => Is(p, 0xcf, 2)));
        Assert.Equal(afterFirst, Snapshot(player.Inventory));

        // The old callback also cannot stop the new owner if delivered again.
        int messages = player.Sent.Count;
        completion();
        Assert.Same(next, player.Get<object?>(CastProperty(second)));
        Assert.Equal(messages, player.Sent.Count);
        Assert.Equal(afterFirst, Snapshot(player.Inventory));
    }

    [Theory]
    [InlineData("craft")]
    [InlineData("carried-shred")]
    [InlineData("ground-shred")]
    [InlineData("consume")]
    public void CrossKindCast_FailedSchedulingLeavesTheTimerAvailable(string kind)
    {
        using var player = new Player();
        Action start = PrepareCast(player, kind);
        var before = Snapshot(player.Inventory);
        player.DisableDispatcher();
        start();
        Assert.Null(player.Get<object?>(CastProperty(kind)));
        Assert.Equal(0, player.Get<long>(CastDeadline(kind)));
        Assert.DoesNotContain(player.Sent, p => Is(p, 0xcf, 2));
        Assert.Contains(player.Sent, p => Is(p, 0xc8, 3));
        Assert.Equal(before, Snapshot(player.Inventory));

        player.EnableDispatcher();
        start();
        Assert.NotNull(player.Get<object?>(CastProperty(kind)));
        Assert.Single(player.Sent, p => Is(p, 0xcf, 2));
        Assert.Equal(before, Snapshot(player.Inventory));
    }

    [Theory]
    [InlineData("craft")]
    [InlineData("carried-shred")]
    [InlineData("ground-shred")]
    [InlineData("consume")]
    public void CrossKindCast_ImmediateHoodActionDoesNotCompeteForTheTimer(string kind)
    {
        using var player = new Player();
        Assert.NotEqual(InventoryPlacementKind.Refused, player.Inventory.TryPickUp(3405, 1, out var hoodie).Kind);
        Assert.NotNull(hoodie);
        Assert.False(player.Inventory.HoodUp);
        Action start = PrepareCast(player, kind);
        start();
        object? owner = player.Get<object?>(CastProperty(kind));
        Assert.NotNull(owner);
        uint up = ItemUseOptionTable.OptionsForItem(3405)
            .First(id => ItemUseOptionTable.KindOf(id) == ItemUseOptionKind.HoodieUp);
        player.Use(hoodie.Guid, up);

        Assert.True(player.Inventory.HoodUp);
        Assert.Same(owner, player.Get<object?>(CastProperty(kind)));
        Assert.Single(player.Sent, p => Is(p, 0xcf, 2));
        Assert.DoesNotContain(player.Sent, p => Is(p, 0xcf, 3));
        Assert.DoesNotContain(player.Sent, p => Is(p, 0xc8, 3));
    }

    [Theory]
    [InlineData("craft")]
    [InlineData("carried-shred")]
    [InlineData("ground-shred")]
    [InlineData("consume")]
    public void CrossKindCast_VehicleRemovalOwnsTheTimerUntilItCompletes(string kind)
    {
        using var player = new Player();
        var session = player.VehicleSession;
        MatchVehicle car = session.EnterMatchWithCar();
        var part = Assert.Single(car.Inventory.Items, i => i.SlotId == 33);
        Action start = PrepareCast(player, kind);
        StartComponentRemoval(player, car, part.ItemGuid);
        long due = Assert.IsType<long>(session.ComponentRemovalDueMs);
        var before = Snapshot(player.Inventory);
        start();
        Assert.Null(player.Get<object?>(CastProperty(kind)));
        Assert.Equal(due, session.ComponentRemovalDueMs);
        Assert.True(car.Inventory.HasSlot(33));
        Assert.Single(player.Sent, p => Is(p, 0xcf, 2));
        Assert.Contains(player.Sent, p => Is(p, 0xc8, 3));
        Assert.Equal(before, Snapshot(player.Inventory));

        // Existing vehicle seam supplies the completion instant; requests still use
        // the actual gateway. This does not claim a real ten-second wall-clock run.
        session.CompleteComponentRemoval(due);
        Assert.Null(session.ComponentRemovalDueMs);
        Assert.False(car.Inventory.HasSlot(33));
        start();
        Assert.NotNull(player.Get<object?>(CastProperty(kind)));
        Assert.Equal(2, player.Sent.Count(p => Is(p, 0xcf, 2)));
    }

    [Theory]
    [InlineData("craft")]
    [InlineData("carried-shred")]
    [InlineData("ground-shred")]
    [InlineData("consume")]
    public async Task CrossKindCast_VehicleRemovalWaitsForThePreviousCallback(string kind)
    {
        using var player = new Player();
        var session = player.VehicleSession;
        MatchVehicle car = session.EnterMatchWithCar();
        var part = Assert.Single(car.Inventory.Items, i => i.SlotId == 33);
        PrepareCast(player, kind)();
        Action completion = await player.NextCompletion();
        object? owner = player.Get<object?>(CastProperty(kind));
        Assert.NotNull(owner);
        StartComponentRemoval(player, car, part.ItemGuid);
        Assert.Null(session.ComponentRemovalDueMs);
        Assert.Same(owner, player.Get<object?>(CastProperty(kind)));
        Assert.True(car.Inventory.HasSlot(33));
        Assert.Single(player.Sent, p => Is(p, 0xcf, 2));
        Assert.Contains(player.Sent, p => Is(p, 0xc8, 3));
        completion();
        StartComponentRemoval(player, car, part.ItemGuid);
        Assert.NotNull(session.ComponentRemovalDueMs);
        Assert.Equal(2, player.Sent.Count(p => Is(p, 0xcf, 2)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossKindCast_StaleMedicalOwnerDoesNotBlockOrStopTheNewContext(bool replaceInventory)
    {
        using var player = new Player();
        PrepareCast(player, "consume")();
        Action stale = await player.NextCompletion();
        if (replaceInventory) player.ReplaceInventory();
        else player.Set("WorldGeneration", player.Get<int>("WorldGeneration") + 1);
        Action craft = PrepareCast(player, "craft");
        player.Sent.Clear();
        craft();
        object? owner = player.Get<object?>("PendingCraft");
        Assert.NotNull(owner);
        Assert.Null(player.Get<object?>("PendingMedicalCast"));
        Assert.Equal(0, player.Get<long>("ConsumeBusyUntil"));
        Assert.Single(player.Sent, p => Is(p, 0xcf, 2));
        Assert.DoesNotContain(player.Sent, p => Is(p, 0xcf, 3));
        var before = Snapshot(player.Inventory);
        int messages = player.Sent.Count;
        stale();
        Assert.Same(owner, player.Get<object?>("PendingCraft"));
        Assert.Equal(before, Snapshot(player.Inventory));
        Assert.Equal(messages, player.Sent.Count);
    }

    private static Action PrepareCast(Player player, string kind)
    {
        switch (kind)
        {
            case "craft":
                Assert.NotEqual(InventoryPlacementKind.Refused,
                    player.Inventory.TryPickUp(CraftingCatalog.ScrapOfCloth, 2, out _).Kind);
                return player.CraftBandage;
            case "carried-shred":
                Assert.NotEqual(InventoryPlacementKind.Refused,
                    player.Inventory.TryPickUp(2144, 1, out var shirt).Kind);
                Assert.NotNull(shirt);
                return () => player.Shred(shirt.Guid);
            case "ground-shred":
                var ground = player.Loot.Spawn(2144, 9249, Vector3.Zero);
                return () => player.Shred(ground.WorldGuid);
            case "consume":
                Assert.NotEqual(InventoryPlacementKind.Refused,
                    player.Inventory.TryPickUp(CraftingCatalog.FieldBandage, 1, out var bandage).Kind);
                Assert.NotNull(bandage);
                uint option = ItemUseOptionTable.OptionsForItem(bandage.DefinitionId)
                    .First(id => ItemUseOptionTable.KindOf(id) == ItemUseOptionKind.ConsumeItem);
                return () => player.Use(bandage.Guid, option);
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static string CastProperty(string kind) => kind switch
    {
        "craft" => "PendingCraft",
        "consume" => "PendingMedicalCast",
        _ => "PendingShred",
    };

    private static string CastDeadline(string kind) => kind switch
    {
        "craft" => "CraftBusyUntil",
        "consume" => "ConsumeBusyUntil",
        _ => "ShredBusyUntil",
    };

    private static void StartComponentRemoval(Player player, MatchVehicle vehicle, ulong item)
    {
        using var packet = new PacketWriter();
        packet.WriteByte(0xac); packet.WriteByte(0x2c);
        packet.WriteUInt32(1); packet.WriteUInt32(0); packet.WriteUInt32(12);
        packet.WriteUInt64(player.VehicleSession.Guid); packet.WriteUInt64(player.VehicleSession.Guid);
        packet.WriteUInt64(vehicle.Guid); packet.WriteUInt64(item); packet.WriteByte(1);
        player.VehicleSession.Deliver(packet.Written.ToArray());
    }
}
