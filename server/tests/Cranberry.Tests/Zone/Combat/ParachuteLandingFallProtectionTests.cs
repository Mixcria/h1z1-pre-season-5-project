using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    private static byte[] LandingExitSignal(bool dismiss) => dismiss
        ? [ZoneOpcodes.VehicleBase, VehicleDismiss.SubOpcode, 0, 0, 0, 0, 0, 0, 0, 0]
        : [ZoneOpcodes.MountBase, DismountRequest.SubOpcode, 0];

    private static byte[] LandingCollision(ulong actor, uint amount,
        CollisionDamageCause cause = CollisionDamageCause.FallDamage)
    {
        using var writer = new PacketWriter();
        new CollisionDamageReport(actor, actor, amount, cause, Vector3.Zero).WriteTo(writer);
        return writer.Written.ToArray();
    }

    private static long LandOnRoof(Fixture fixture, SoeConnection player, bool dismiss = true)
    {
        object state = player.Tag!;
        fixture.Service.Post = _ => { };
        Set(state, "Authenticated", true);
        Set(state, "Released", true);
        Set(state, "ReleasedAtMs", Environment.TickCount64 - 60_000);
        Set(state, "ChuteGuid", 0x2001UL);
        Set(state, "MountRequested", true);
        long before = Environment.TickCount64;
        fixture.Service.ForVehicleTest(player).Deliver(LandingExitSignal(dismiss));
        long until = Get<long>(state, "ParachuteFallProtectedUntilMs");
        Assert.InRange(until, before + 10_000, Environment.TickCount64 + 10_000);
        Assert.Equal(0UL, Get<ulong>(state, "ChuteGuid"));
        return until;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParachuteLandingProtectsRoofDropsForExactlyTenSeconds(bool dismiss)
    {
        using var f = new Fixture();
        var player = f.Add(1, new(100, 40, 100));
        long until = LandOnRoof(f, player, dismiss);
        var session = f.Service.ForVehicleTest(player);
        foreach (long offset in new long[] { -10_000, -5_000, -1 })
            session.CollisionAt(LandingCollision(1, 42_637), until + offset);
        Assert.Equal(10_000u, f.Health(player));

        // Suppressed lethal reports must not seed the cumulative collision peak.
        session.CollisionAt(LandingCollision(1, 2500), until);
        Assert.Equal(7500u, f.Health(player));
        session.CollisionAt(LandingCollision(1, 2500), until + 1);
        Assert.Equal(7500u, f.Health(player));
    }

    [Fact]
    public void OrdinaryFallsAndDuplicateLandingSignalsDoNotExtendLandingProtection()
    {
        using var f = new Fixture();
        var player = f.Add(1, new(100, 40, 100));
        long until = LandOnRoof(f, player);
        var session = f.Service.ForVehicleTest(player);
        session.Deliver(LandingExitSignal(false));
        session.Deliver(LandingExitSignal(true));
        session.CollisionAt(LandingCollision(1, 9000), until - 1);
        Assert.Equal(until, Get<long>(player.Tag!, "ParachuteFallProtectedUntilMs"));
        session.CollisionAt(LandingCollision(1, 9000), until);
        Assert.Equal(1000u, f.Health(player));
        Assert.Equal(until, Get<long>(player.Tag!, "ParachuteFallProtectedUntilMs"));
    }

    [Theory]
    [InlineData(DamageCause.Bullet)]
    [InlineData(DamageCause.Melee)]
    [InlineData(DamageCause.ToxicGas)]
    [InlineData(DamageCause.BombingRun)]
    public void LandingFallProtectionDoesNotGrantCombatInvulnerability(DamageCause cause)
    {
        using var f = new Fixture();
        var player = f.Add(1, new(100, 40, 100));
        LandOnRoof(f, player);
        Assert.False(f.Service.ForTest(player).Damage(1000, cause));
        Assert.Equal(9000u, f.Health(player));
    }

    [Fact]
    public void LandingFallProtectionDoesNotProtectCarsOrPlayerVehicleCollisions()
    {
        using var f = new Fixture(new ZoneOptions
        {
            VehicleDamage = new VehicleDamageOptions
            {
                MinimumCollisionDamage = 0, CollisionDamageMultiplier = 1, MaximumCollisionDamage = 10_000,
            },
        });
        var player = f.Add(1, new(100, 40, 100));
        var session = f.Service.ForVehicleTest(player);
        long until = LandOnRoof(f, player);
        var car = session.EnterMatchWithCar();
        session.CollisionAt(LandingCollision(car.Guid, 2500), until - 100);
        Assert.Equal(97_500u, car.Health);
        session.CollisionAt(LandingCollision(1, 1000, CollisionDamageCause.VehicleCollision), until - 100);
        Assert.Equal(9000u, f.Health(player));
    }

    [Fact]
    public void APlayerWhoHasNotLandedGetsNoNewProtectionFromAnExitSignal()
    {
        using var f = new Fixture();
        var player = f.Add(1, new(100, 40, 100));
        Set(player.Tag!, "Authenticated", true);
        Set(player.Tag!, "Released", true);
        Set(player.Tag!, "ReleasedAtMs", Environment.TickCount64 - 60_000);
        var session = f.Service.ForVehicleTest(player);
        session.Deliver(LandingExitSignal(true));
        Assert.Equal(0L, Get<long>(player.Tag!, "ParachuteFallProtectedUntilMs"));
        session.Deliver(LandingCollision(1, 2500));
        Assert.Equal(7500u, f.Health(player));
    }

    [Fact]
    public void StartingAnotherParachuteAndAbandoningAMatchClearTheOldDeadline()
    {
        using var f = new Fixture();
        var player = f.Add(1, new(100, 40, 100));
        long previous = LandOnRoof(f, player);
        Call(f.Service, "SendParachute", player, player.Tag, new Vector4(100, 100, 100, 1));
        Assert.Equal(0L, Get<long>(player.Tag!, "ParachuteFallProtectedUntilMs"));
        Set(player.Tag!, "ParachuteFallProtectedUntilMs", previous);
        Call(f.Service, "AbandonMatch", player, player.Tag, "fall protection regression");
        Assert.Equal(0L, Get<long>(player.Tag!, "ParachuteFallProtectedUntilMs"));
    }
}
