using Cranberry.Zone;
using Cranberry.Zone.World;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed partial class VehicleDamageIntegrationTests
{
    [Theory]
    [InlineData(1u, true)]
    [InlineData(2u, true)]
    [InlineData(3u, true)]
    [InlineData(5u, true)]
    [InlineData(1u, false)]
    [InlineData(3u, false)]
    public void HighSpeedExitIgnoresRiderContactAndFallReports(uint family, bool driver)
    {
        var (service, connection, _) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(family, asDriver: driver);
        car.LastSpeed = 60;
        Assert.True(session.Exit());
        long exit = session.ExitProtectedUntilMs - ZoneService.VehicleExitCollisionGraceMs;

        // Both recorded September 6 exit hits and a lethal native terrain-settle report.
        session.CollisionAt(Collision(session.Guid, car.Guid, 2589, CollisionDamageCause.VehicleCollision), exit + 25);
        session.CollisionAt(Collision(session.Guid, car.Guid, 5653, CollisionDamageCause.VehicleCollision), exit + 26);
        session.CollisionAt(Collision(session.Guid, session.Guid, 42637, CollisionDamageCause.FallDamage), exit + 100);
        session.CollisionAt(Collision(session.Guid, car.Guid, 10000, CollisionDamageCause.VehicleCollision), session.ExitProtectedUntilMs - 1);
        Assert.Equal(10000u, session.Hitpoints);
        Assert.Equal(-1, car.SeatOf(session.Guid));
    }

    [Fact]
    public void ExitProtectionDoesNotProtectTheVehicleOrBlockUnrelatedDamage()
    {
        var (service, connection, _) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        Assert.True(session.Exit());
        session.Deliver(Collision(car.Guid, session.Guid, 18956, CollisionDamageCause.VehicleCollision));
        Assert.Equal(81044u, car.Health);
        Assert.Equal(10000u, session.Hitpoints);

        var combat = service.ForTest(connection);
        Assert.False(combat.Damage(1000, DamageCause.Bullet));
        Assert.False(combat.Damage(1000, DamageCause.ToxicGas));
        Assert.Equal(8000u, session.Hitpoints);
    }

    [Theory]
    [InlineData(CollisionDamageCause.FallDamage)]
    [InlineData(CollisionDamageCause.VehicleCollision)]
    public void LaterImpactsApplyNormallyAndSuppressedReportsDoNotSeedTheDamageBurst(CollisionDamageCause cause)
    {
        var (service, connection, _) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        Assert.True(session.Exit());
        long until = session.ExitProtectedUntilMs;
        session.CollisionAt(Collision(session.Guid, car.Guid, 9000, cause), until - 100);
        session.CollisionAt(Collision(session.Guid, car.Guid, 2500, cause), until);
        Assert.Equal(7500u, session.Hitpoints);
    }

    [Fact]
    public void RefusedDismountAndSeatChangeDoNotGrantOrExtendProtection()
    {
        var (service, connection, _) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        car.LastInteractionMs = Environment.TickCount64;
        Assert.True(session.Exit()); // Request handled, but fleet refuses its cooldown.
        Assert.Equal(0L, session.ExitProtectedUntilMs);
        car.LastInteractionMs = long.MinValue;
        session.Deliver(SeatChange(car.Guid, 1));
        Assert.Equal(0L, session.ExitProtectedUntilMs);
        car.LastInteractionMs = long.MinValue;
        Assert.True(session.Exit());
        long until = session.ExitProtectedUntilMs;
        Assert.False(session.Exit()); // Already on foot.
        Assert.Equal(until, session.ExitProtectedUntilMs);
    }
}
