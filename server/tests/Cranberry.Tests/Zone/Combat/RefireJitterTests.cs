using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone.Combat;

public sealed class RefireJitterTests
{
    [Fact]
    public void QuantizedReceiveTimesDoNotDiscardNormallyPacedAkShots()
    {
        var shooter = new ShooterCombatState();
        var options = new CombatOptions { ShippedRefireGate = true, RefireJitterMs = 16 };
        shooter.DeclareWeapon(1, 2229, 30);
        uint projectile = 0;
        foreach (long time in new long[] { 1000, 1148, 1289, 1449, 1590, 1740 })
            Assert.Equal(FireVerdict.Accepted,
                shooter.Fire(new WeaponFire(1, 0, 0, 0, [++projectile]), 2229, time, options).Verdict);
        Assert.Equal(24, shooter.AmmoOf(1));
    }

    [Fact]
    public void EarlyShotsCannotAccumulateAFasterSustainedRate()
    {
        var shooter = new ShooterCombatState();
        var options = new CombatOptions { ShippedRefireGate = true, RefireJitterMs = 16 };
        shooter.DeclareWeapon(1, 2229, 30);
        Assert.Equal(FireVerdict.Accepted, Fire(1000));
        Assert.Equal(FireVerdict.Accepted, Fire(1130));
        Assert.Equal(FireVerdict.RateOfFire, Fire(1260));
        Assert.Equal(FireVerdict.Accepted, Fire(1290));
        Assert.Equal(27, shooter.AmmoOf(1));
        FireVerdict Fire(long at) => shooter.Fire(new WeaponFire(1, 0, 0, 0, [(uint)at]),
            2229, at, options).Verdict;
    }
}
