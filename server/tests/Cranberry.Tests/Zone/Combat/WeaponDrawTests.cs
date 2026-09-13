using System.Numerics;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Combat;

public sealed class WeaponDrawTests
{
    [Fact]
    public void RapidSwitchesReplaceTheDeadlineAndSameSlotDoesNotRestartIt()
    {
        var draw = new WeaponDrawState();
        Assert.True(draw.Select(1, 750, 150, 1000));
        Assert.Equal(1750, draw.ReadyAtMs);
        Assert.False(draw.Select(1, 750, 150, 1100));
        Assert.Equal(1750, draw.ReadyAtMs);
        Assert.True(draw.Select(2, 550, 150, 1200));
        Assert.Equal(1900, draw.ReadyAtMs);
        Assert.True(draw.Select(1, 750, 150, 1300));
        Assert.False(draw.IsReady(2199));
        Assert.True(draw.IsReady(2200));
        draw.Clear();
        Assert.True(draw.IsReady(0));
    }

    [Fact]
    public void EarlyShotIsRefundedWithoutAmmoOrHintAndTheBoundaryShotWorks()
    {
        var session = new SessionCombat();
        session.Shooter.DeclareWeapon(42, 2425, 30);
        session.Draw.Select(42, 750, 150, 1000);
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(session, ShootingPacketBuilder.Fire(42, 0, 0, 0, [1]),
            CombatOptions.Default, 2425, 42, Vector3.Zero, 1749, results, null);
        Assert.NotNull(Assert.Single(results).Reply);
        Assert.False(results[0].LaunchRelay);
        Assert.Equal(30, session.Shooter.AmmoOf(42));
        Assert.Equal(0, session.Shooter.LiveHints);

        WeaponFireArm.Handle(session, ShootingPacketBuilder.Fire(42, 0, 0, 0, [2]),
            CombatOptions.Default, 2425, 42, Vector3.Zero, 1750, results, null);
        Assert.True(Assert.Single(results).LaunchRelay);
        Assert.Equal(29, session.Shooter.AmmoOf(42));
        Assert.Equal(1, session.Shooter.LiveHints);
    }

    [Theory]
    [InlineData(2235u, 1f)]
    [InlineData(2236u, 1f)]
    [InlineData(2237u, 2f)]
    public void FasterGrenadesShareTheClientAndServerFuse(uint item, float seconds)
    {
        Assert.True(AugustThrowables.TryGet(item, out var fact));
        Assert.Equal(seconds, fact.FuseSeconds);
        Assert.Equal(fact.ProjectileLifespanSeconds, Assert.Single(Z1ProjectileTable.RecordsFor(true, 30f),
            row => row.ProjectileId == fact.ProjectileId).Lifespan);
        var session = new SessionCombat();
        var fire = new WeaponFire(42, 0, 0, 0, [1]);
        ThrowableArm.Throw(session, CombatOptions.Default, fire, item, 1000, 0);
        Assert.Single(session.Grenades.Live).Place(new Vector3(10, 0, 0), ImpactSource.ContactReport);
        Assert.Empty(session.Grenades.Overdue(1000 + (long)(seconds * 1000) + 149, 150));
        Assert.Single(session.Grenades.Overdue(1000 + (long)(seconds * 1000) + 150, 150));
    }
}
