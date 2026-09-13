using System.Numerics;
using System.Reflection;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.DevConsole;

public partial class ConsoleIntegrationTests
{
    [Theory]
    [InlineData("car", "", 1u)]
    [InlineData("car", "offroader", 1u)]
    [InlineData("car", "pickup", 2u)]
    [InlineData("car", "policecar", 3u)]
    [InlineData("car", "atv", 5u)]
    [InlineData("spawncar", "copcar", 3u)]
    [InlineData("spawnvehicle", "jeep", 1u)]
    [InlineData("spawnvehicle", "pickuptruck", 2u)]
    public void ConsoleSpawnsAHealthyVehicleWithSeventyFivePercentFuelWithoutLosingThePlan(
        string command, string arguments, uint vehicleId)
    {
        var (service, connection, recorder) = DevelopmentMatch();
        Assert.Null(Fleet(connection));
        int before = SentCount(recorder);

        SendExecuteCommand(service, connection, command, arguments);

        var fleet = Member<VehicleFleet>(connection, "Fleet");
        var car = Assert.Single(fleet.Vehicles, v => v.AnchorInstanceId == 0);
        var planned = fleet.Vehicles.Where(v => v.AnchorInstanceId != 0).Select(v => v.Guid).ToArray();
        Assert.NotEmpty(planned);
        Assert.Equal(vehicleId, car.Definition.VehicleId);
        Assert.Equal(new Vector3(103, 20, 100), car.Position);
        Assert.Equal(fleet.Options.MaxHealth, car.Health);
        Assert.Equal(fleet.Options.MaxFuel * 0.75f, car.Fuel);
        Assert.True(Member<MatchVehicleStream>(connection, "StreamedVehicles").IsStreamed(car.Guid));
        Assert.True(Member<bool>(connection, "VehicleObserverRegistered"));
        Assert.Contains(ConsoleLines(recorder, before), line => line.StartsWith("+ spawned"));
        var lightweight = Assert.Single(Sent(recorder, before), p => p[1] == ZoneOpcodes.AddLightweightVehicle);
        Assert.Single(Sent(recorder, before), p => p[1] == ZoneOpcodes.LightweightToFullVehicle);
        var reader = new PacketReader(Payload(lightweight));
        Assert.Equal(ZoneOpcodes.AddLightweightVehicle, reader.ReadByte());
        Assert.Equal(car.Guid, reader.ReadUInt64());

        // Execute the actual scheduled parking path after /car has initialized the fleet.
        // It retains all pads and its NoteSpawned check cannot duplicate the explicit car.
        var burst = new List<Action>();
        typeof(ZoneService).GetMethod("SpawnNearbyVehicles", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [connection, connection.Tag, "console regression", burst]);
        before = SentCount(recorder);
        foreach (Action action in burst) action();
        Assert.Same(fleet, Fleet(connection));
        Assert.Equal(planned, fleet.Vehicles.Where(v => v.AnchorInstanceId != 0).Select(v => v.Guid));
        Assert.DoesNotContain(Sent(recorder, before), p =>
            p[1] == ZoneOpcodes.AddLightweightVehicle && BitConverter.ToUInt64(p, 2) == car.Guid);
    }

    [Fact]
    public void ConsoleCarAlsoWorksWhenAutomaticVehiclesAreDisabled()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions { Console = TestConsole, SendVehicles = false });
        EnterStep(connection, "InMatch");
        var movement = Member<SessionMovementState>(connection, "Movement");
        movement.ApplyPlayer(ClientMovementUpdate.Parse(Convert.FromHexString("020018F6B21C00000000")));
        movement.PinPlayer(new Vector3(100, 20, 100));
        int before = SentCount(recorder);

        SendExecuteCommand(service, connection, "spawnvehicle", "atv");

        Assert.Single(Member<VehicleFleet>(connection, "Fleet").Vehicles);
        Assert.Single(Sent(recorder, before), p => p[1] == ZoneOpcodes.AddLightweightVehicle);
        Assert.True(Member<bool>(connection, "VehicleObserverRegistered"));
    }

    [Fact]
    public void ConsoleCarUsesTheOccupiedVehiclePoseInsteadOfTheOldPlayerPose()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        var occupied = service.ForVehicleTest(connection).EnterMatchWithCar();
        // The test car is at the origin, while the retained channel-2 player is at 100/20/100.
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "car", "atv");
        var spawned = Assert.Single(Member<VehicleFleet>(connection, "Fleet").Vehicles, v => v.Guid != occupied.Guid);
        Assert.Equal(occupied.Position + new Vector3(3, 0, 0), spawned.Position);
        Assert.Equal(occupied.Yaw, spawned.Yaw);
        Assert.Contains(ConsoleLines(recorder, before), line => line.StartsWith("+ spawned"));
    }

    [Fact]
    public void ConsoleCarWhileParachutingExplainsWhyItCannotUseTheStalePlayerPose()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        SetSessionMember(connection, "ChuteGuid", 123UL);
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "car", "atv");
        Assert.Null(Fleet(connection));
        Assert.StartsWith("- land before spawning", Assert.Single(ConsoleLines(recorder, before)));
        Assert.All(Sent(recorder, before), packet => Assert.True(IsConsolePrint(packet)));
    }

    [Fact]
    public void ConsoleCarRejectsExtraArgumentsWithoutCreatingAnything()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "car", "atv 20");
        Assert.Null(Fleet(connection));
        Assert.StartsWith("?", Assert.Single(ConsoleLines(recorder, before)));
        Assert.All(Sent(recorder, before), packet => Assert.True(IsConsolePrint(packet)));
    }

    [Fact]
    public void CarsSeparatesMapPopulationFromClientStreamingAndConsoleSpawns()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        SendExecuteCommand(service, connection, "car", "atv");
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "cars");
        var lines = ConsoleLines(recorder, before);
        Assert.Contains(lines, line => line.Contains("from map pads, 1 console"));
        Assert.Contains(lines, line => line.Contains("1 streamed to you; 0 occupied; 0 destroyed"));
    }
}
