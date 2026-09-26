using System.Numerics;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class MedicalMovementTests
{
    // UseFuelMenu sends the actual ac2c requester/target/source header. These tests
    // exercise explicit selection, not a direct call to the RunConsume plan seam.
    [Theory]
    [InlineData(false, "unknown")]
    [InlineData(false, "distant")]
    [InlineData(false, "destroyed")]
    [InlineData(true, "unknown")]
    [InlineData(true, "distant")]
    [InlineData(true, "destroyed")]
    public void TargetedRefuel_InvalidSelectionDoesNotFallBackToAnotherCar(bool fromCargo, string invalid)
    {
        using var world = new Fixture(73);
        var fleet = fromCargo ? world.Seat(true) : new VehicleFleet(VehicleRoster.LoadDefault());
        var near = fromCargo ? Assert.Single(fleet.Vehicles) : TargetVehicle(fleet, 500, 99, 1);
        if (!fromCargo) fleet.Add(near);
        var clicked = TargetVehicle(fleet, 501, 100, invalid == "distant" ? 20 : 3);
        if (invalid == "destroyed") clicked.Health = 0;
        if (invalid != "unknown") fleet.Add(clicked);
        world.Set("Fleet", fleet);
        ulong sourceItem = fromCargo
            ? Assert.Single(near.Inventory.Items, item => item.DefinitionId == 73).ItemGuid
            : world.Item.Guid;
        world.Sent.Clear();

        world.UseFuelMenu(sourceItem, fromCargo ? near.Guid : 0x1001, clicked.Guid);

        Assert.Null(world.Cast);
        Assert.Equal(0, world.Get<long>("ConsumeBusyUntil"));
        Assert.True(world.Inventory.Items.ContainsKey(world.Item.Guid));
        Assert.Equal(1u, world.Inventory.Items[world.Item.Guid].Count);
        if (fromCargo)
        {
            Assert.True(near.Inventory.TryGet(sourceItem, out var retained));
            Assert.NotNull(retained);
            Assert.Equal(1u, retained.Count);
        }
        Assert.Equal(5000f, near.Fuel);
        Assert.Equal(5000f, clicked.Fuel);
        Assert.DoesNotContain(world.Sent, p => p.Length >= 2 && p[0] == 0xcf && p[1] == 2);
        Assert.Contains(world.Sent, p => p.Length == ContainerError.Length && p[0] == 0xc8 && p[1] == 3
            && BitConverter.ToUInt32(p, 11) == (uint)ContainerErrorCode.InteractionValidationFailed);
        Assert.DoesNotContain(world.Sent, IsVehicleFuelUpdate);
    }

    [Theory]
    [InlineData(false, 0ul)]
    [InlineData(false, 0x1001ul)]
    [InlineData(true, 0ul)]
    [InlineData(true, 0x1001ul)]
    public void TargetedRefuel_DefaultTargetStillUsesTheOccupiedCar(bool fromCargo, ulong target)
    {
        using var world = new Fixture(73);
        var car = Assert.Single(world.Seat(true).Vehicles);
        ulong sourceItem = fromCargo
            ? Assert.Single(car.Inventory.Items, item => item.DefinitionId == 73).ItemGuid
            : world.Item.Guid;
        world.Sent.Clear();
        world.UseFuelMenu(sourceItem, fromCargo ? car.Guid : 0x1001, target);
        Assert.NotNull(world.Cast);
        Action complete = world.TakeTimer();
        complete();
        complete();

        Assert.Null(world.Cast);
        Assert.Equal(7500f, car.Fuel);
        Assert.Equal(fromCargo, world.Inventory.Items.ContainsKey(world.Item.Guid));
        if (fromCargo) Assert.False(car.Inventory.TryGet(sourceItem, out _));
        Assert.Single(world.Sent, IsVehicleFuelUpdate);
    }

    [Theory]
    [InlineData("distant")]
    [InlineData("destroyed")]
    [InlineData("replaced")]
    public void TargetedRefuel_SelectionMustRemainValidAtCompletion(string invalid)
    {
        using var world = new Fixture(73);
        var fleet = new VehicleFleet(VehicleRoster.LoadDefault());
        var near = TargetVehicle(fleet, 500, 99, 1);
        var clicked = TargetVehicle(fleet, 501, 100, 3);
        fleet.Add(near); fleet.Add(clicked);
        world.Set("Fleet", fleet);
        world.UseFuelMenu(world.Item.Guid, 0x1001, clicked.Guid);
        Assert.NotNull(world.Cast);
        MatchVehicle? replacement = null;
        switch (invalid)
        {
            case "distant": clicked.SetPose(Fixture.StartPosition + Vector3.UnitX * 20, 0); break;
            case "destroyed": clicked.Health = 0; break;
            case "replaced":
                // Same numeric identity in a new world object must not inherit an
                // already-armed operation against the previous object instance.
                var current = new VehicleFleet(VehicleRoster.LoadDefault());
                current.Add(near);
                replacement = TargetVehicle(current, clicked.Guid, clicked.TransientId, 3);
                current.Add(replacement);
                world.Set("Fleet", current);
                break;
        }
        world.Sent.Clear();
        Action complete = world.TakeTimer();
        complete();
        complete();

        Assert.Null(world.Cast);
        Assert.Equal(0, world.Get<long>("ConsumeBusyUntil"));
        Assert.Same(world.Item, world.Inventory.Items[world.Item.Guid]);
        Assert.Equal(1u, world.Item.Count);
        Assert.Equal(5000f, near.Fuel);
        Assert.Equal(5000f, clicked.Fuel);
        if (replacement is not null) Assert.Equal(5000f, replacement.Fuel);
        Assert.Contains(world.Sent, p => p.Length >= 2 && p[0] == 0xcf && p[1] == 3);
        Assert.Contains(world.Sent, p => p.Length == ContainerError.Length && p[0] == 0xc8 && p[1] == 3
            && BitConverter.ToUInt32(p, 11) == (uint)ContainerErrorCode.InteractionValidationFailed);
        Assert.DoesNotContain(world.Sent, IsVehicleFuelUpdate);
    }

    private static MatchVehicle TargetVehicle(VehicleFleet fleet, ulong guid, uint transient, float distance) =>
        new(guid, transient, fleet.Roster.Require(1), Fixture.StartPosition + Vector3.UnitX * distance,
            0, 100000, 5000);

    private static bool IsVehicleFuelUpdate(byte[] packet) =>
        packet.Length > 26 && packet[0] == 0x8d && BitConverter.ToUInt32(packet, 14) == 50;
}
