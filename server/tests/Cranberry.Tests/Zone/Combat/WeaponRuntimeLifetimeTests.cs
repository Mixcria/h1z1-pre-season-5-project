using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone.Combat;

public sealed class WeaponRuntimeLifetimeTests
{
    [Fact]
    public void DeclaringMoreThanEightItemsPreservesLoadedGunsAndFiringState()
    {
        var shooter = new ShooterCombatState();
        shooter.DeclareWeapon(1, 2229, 10);
        shooter.Load(1, 7);
        shooter.SelectFireMode(1, 2, 1);
        Assert.Equal(FireVerdict.Accepted,
            shooter.Fire(new WeaponFire(1, 0, 0, 0, [42]), 2229, 1000, CombatOptions.Default).Verdict);
        shooter.SpendDurability(1, 5, 2000);

        // Fists, a complete gun kit and repeat grants exceed the original eight-entry table.
        for (ulong guid = 2; guid <= 40; guid++) shooter.DeclareWeapon(guid, 2425, 3);

        Assert.Equal(40, shooter.WeaponCount);
        Assert.False(shooter.EnsureDeclared(1, 2229, 0));
        Assert.Equal(16, shooter.AmmoOf(1));
        Assert.Equal(1995, shooter.DurabilityOf(1));
        Assert.Equal(1ul, shooter.ReloadCountOf(1));
        Assert.Equal(2, shooter.FireGroupOf(1));
        Assert.Equal(1, shooter.FireModeOf(1));
        Assert.Equal(FireVerdict.RateOfFire,
            shooter.Fire(new WeaponFire(1, 0, 0, 0, [43]), 2229, 1001, CombatOptions.Default).Verdict);
        Assert.Equal(16, shooter.AmmoOf(1));
    }

    [Fact]
    public void ForgettingMiddleWeaponPreservesOtherInstancesAndReusesSpace()
    {
        var shooter = new ShooterCombatState();
        shooter.DeclareWeapon(1, 2229, 11);
        shooter.DeclareWeapon(2, 1374, 3);
        shooter.DeclareWeapon(3, 2425, 17);
        Assert.True(shooter.ForgetWeapon(2));
        Assert.False(shooter.ForgetWeapon(2));
        Assert.False(shooter.Knows(2));
        Assert.Equal(2, shooter.WeaponCount);
        Assert.Equal(11, shooter.AmmoOf(1));
        Assert.Equal(17, shooter.AmmoOf(3));
        shooter.DeclareWeapon(4, 1997, 5);
        Assert.Equal(3, shooter.WeaponCount);
        Assert.Equal(5, shooter.AmmoOf(4));
        Assert.Equal(17, shooter.AmmoOf(3));
    }

    [Fact]
    public void ForgettingDroppedWeaponDoesNotDiscardItsInFlightProjectile()
    {
        var shooter = new ShooterCombatState();
        shooter.DeclareWeapon(1, 2229, 30);
        shooter.Fire(new WeaponFire(1, 1, 2, 3, [42]), 2229, 1000, CombatOptions.Default);
        shooter.ForgetWeapon(1);
        Assert.True(shooter.TryConsumeHint(42, 1100, 1000, out var hint));
        Assert.Equal(2229u, hint.ItemDefinitionId);
        Assert.False(shooter.TryConsumeHint(42, 1100, 1000, out _));
    }

    [Fact]
    public void ResetClearsAnExpandedTableAndProjectileHints()
    {
        var shooter = new ShooterCombatState();
        for (ulong guid = 1; guid <= 40; guid++) shooter.DeclareWeapon(guid, 2229, 10);
        shooter.Fire(new WeaponFire(1, 0, 0, 0, [42]), 2229, 1000, CombatOptions.Default);
        shooter.Reset();
        Assert.Equal(0, shooter.WeaponCount);
        Assert.Equal(0, shooter.LiveHints);
        for (ulong guid = 1; guid <= 40; guid++) Assert.False(shooter.Knows(guid));
        shooter.DeclareWeapon(1, 2229, 7);
        Assert.Equal(1, shooter.WeaponCount);
        Assert.Equal(7, shooter.AmmoOf(1));
    }
}
