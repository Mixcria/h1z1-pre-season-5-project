using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    [Fact]
    public void VehicleSpawnsShareOneFleetPerMatch()
    {
        using var f = new Fixture();
        var first = f.Add(1, Vector3.Zero);
        var second = f.Add(2, Vector3.Zero);
        var separate = f.Add(3, Vector3.Zero, matchId: 2);
        foreach (var c in new[] { first, second, separate })
            Call(f.Service, "SpawnNearbyVehicles", c, c.Tag, "test", new List<Action>());
        Assert.NotNull(Get<VehicleFleet>(first.Tag!, "Fleet"));
        Assert.Same(Get<VehicleFleet>(first.Tag!, "Fleet"), Get<VehicleFleet>(second.Tag!, "Fleet"));
        Assert.NotSame(Get<VehicleFleet>(first.Tag!, "Fleet"), Get<VehicleFleet>(separate.Tag!, "Fleet"));
        var fleet = Get<VehicleFleet>(first.Tag!, "Fleet");
        var car = fleet.Vehicles.First();
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(car.Guid, 1, 0, 10000, out _, out _));
        Call(f.Service, "LeaveSharedLoot", first.Tag);
        Assert.Equal(0ul, car.DriverGuid);
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(car.Guid, 2, 0, 12000, out _, out _));
    }

    [Fact]
    public void VehicleExplosionHurtsNearbyPlayersAndPassengersOnceWithinItsMatch()
    {
        using var f = new Fixture();
        var driver = f.Add(1, Vector3.Zero);
        var passenger = f.Add(2, new(100, 0, 0)); // retained on-foot position must not spare a passenger
        var near = f.Add(3, new(6, 0, 0));
        var far = f.Add(4, new(12, 0, 0));
        var otherMatch = f.Add(5, Vector3.Zero, matchId: 2);
        var session = f.Service.ForVehicleTest(driver);
        var car = session.EnterMatchWithCar();
        Assert.Equal(VehicleActionResult.Ok, session.Fleet.TryEnter(car.Guid, 2, 1, Environment.TickCount64 + 2000, out _, out _));
        Set(passenger.Tag!, "Fleet", session.Fleet);
        Call(f.Service, "ApplyVehicleDamage", driver, driver.Tag, car, 100000u, "test impact", false, null);
        Assert.Equal(0u, Get<uint>(driver.Tag!, "Hitpoints"));
        Assert.Equal(0u, Get<uint>(passenger.Tag!, "Hitpoints"));
        Assert.True(Get<bool>(passenger.Tag!, "DeathSent"));
        Assert.Equal(6000u, Get<uint>(near.Tag!, "Hitpoints"));
        Assert.Equal(10000u, Get<uint>(far.Tag!, "Hitpoints"));
        Assert.Equal(10000u, Get<uint>(otherMatch.Tag!, "Hitpoints"));
        Assert.True(car.IsEmpty);
        Call(f.Service, "ApplyVehicleDamage", driver, driver.Tag, car, 100000u, "duplicate impact", false, null);
        Assert.Equal(6000u, Get<uint>(near.Tag!, "Hitpoints"));
    }

    [Fact]
    public void SimultaneousBlastDeathsCannotAwardVictoryToAnotherBlastVictim()
    {
        using var f = new Fixture();
        var driver = f.Add(1, Vector3.Zero);
        var near = f.Add(2, new(1, 0, 0));
        var car = f.Service.ForVehicleTest(driver).EnterMatchWithCar();
        Call(f.Service, "ApplyVehicleDamage", driver, driver.Tag, car, 100000u, "test impact", false, null);
        Assert.True(Get<bool>(driver.Tag!, "DeathSent"));
        Assert.True(Get<bool>(near.Tag!, "DeathSent"));
        Assert.False(Get<bool>(driver.Tag!, "VictorySent"));
        Assert.False(Get<bool>(near.Tag!, "VictorySent"));
    }

    [Fact]
    public void AcceptedAr15HitTakesOneVehiclePointAndCannotReplay()
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero);
        var car = f.Service.ForVehicleTest(shooter).EnterMatchWithCar();
        f.Weapon(shooter, ShootingPacketBuilder.Fire(0x3100000000000001, 0, 0, 0, [42]), 42000);
        var hit = ShootingPacketBuilder.HitReport(42, car.Guid, "SPINE");
        f.Weapon(shooter, hit, 42000);
        Assert.Equal(99000u, car.Health);
        f.Weapon(shooter, hit, 42001);
        Assert.Equal(99000u, car.Health);
    }

    [Theory]
    [InlineData(0, 10000u)]
    [InlineData(3, 10000u)]
    [InlineData(6, 4000u)]
    [InlineData(8, 0u)]
    [InlineData(50, 0u)]
    public void VehicleBlastFallsOffToZeroAtTheEdge(float distance, uint expected) =>
        Assert.Equal(expected, new VehicleDamageOptions().ExplosionDamageAt(distance));
}
