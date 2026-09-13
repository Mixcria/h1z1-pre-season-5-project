using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone.Combat;

/// <summary>
/// The inventory instance in RHand is combat authority. A parsed <c>82 03</c> still names an
/// item guid, but that wire guid may be stale after a hotbar switch and must never create a new
/// shooter-side weapon runtime on its own.
/// </summary>
public sealed class ActiveWeaponAuthorityTests
{
    private const ulong ActiveRifle = 0x3100_0000_0000_0042;
    private const ulong StaleRifle = 0x3100_0000_0000_0043;
    private const uint ArFifteen = AugustHeldWeapon.ItemDefinitionId;

    [Fact]
    public void AFireFromTheActiveHandInstanceIsDeclaredAndAccepted()
    {
        var session = new SessionCombat();
        var results = new List<WeaponArmResult>();

        Handle(
            session,
            ShootingPacketBuilder.Fire(ActiveRifle, 1f, 2f, 3f, [101]),
            ActiveRifle,
            results);

        WeaponArmResult fire = Assert.Single(results);
        Assert.DoesNotContain("REFUSED", fire.Line, StringComparison.Ordinal);
        Assert.True(session.Shooter.Knows(ActiveRifle));
        Assert.Equal(1, session.Shooter.ShotsFired);
        Assert.Equal(0, session.Shooter.ShotsRefused);
        Assert.Equal(1, session.Shooter.LiveHints);
        Assert.Equal(29, session.Shooter.AmmoOf(ActiveRifle));
    }

    [Fact]
    public void AFireFromAStaleInstanceIsRefusedBeforeItCanBeDeclaredOrFired()
    {
        var session = new SessionCombat();
        var results = new List<WeaponArmResult>();
        session.Shooter.DeclareWeapon(ActiveRifle, ArFifteen);

        Handle(
            session,
            ShootingPacketBuilder.Fire(StaleRifle, 1f, 2f, 3f, [202]),
            ActiveRifle,
            results);

        WeaponArmResult refusal = Assert.Single(results);
        Assert.Contains("REFUSED", refusal.Line, StringComparison.Ordinal);
        Assert.Contains("no shot was declared", refusal.Line, StringComparison.Ordinal);
        Assert.True(session.Shooter.Knows(ActiveRifle));
        Assert.False(session.Shooter.Knows(StaleRifle));
        Assert.Equal(0, session.Shooter.ShotsFired);
        Assert.Equal(1, session.Shooter.ShotsRefused);
        Assert.Equal(0, session.Shooter.LiveHints);
        Assert.Equal(30, session.Shooter.AmmoOf(ActiveRifle));
        Assert.Equal(-1, session.Shooter.AmmoOf(StaleRifle));
    }

    private static void Handle(
        SessionCombat session,
        byte[] packet,
        ulong activeHandGuid,
        List<WeaponArmResult> results) =>
        WeaponFireArm.Handle(
            session,
            packet,
            CombatOptions.Default,
            ArFifteen,
            activeHandGuid,
            Vector3.Zero,
            nowMs: 1_000,
            results: results);
}
