using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone.Combat;

public sealed class CombatActionIdentityTests
{
    [Fact]
    public void DuplicateProjectilesRemainSpentAfterHitExpiryAndRingEviction()
    {
        var shooter = new ShooterCombatState();
        shooter.DeclareWeapon(10, 2425, 30);
        WeaponFire first = new(10, 0, 0, 0, [1]);
        Assert.Equal(FireVerdict.Accepted, shooter.Fire(first, 2425, 0, CombatOptions.Default).Verdict);
        Assert.True(shooter.TryConsumeHint(1, 0, 1000, out _));
        for (uint id = 2; id <= 26; id++)
            Assert.Equal(FireVerdict.Accepted, shooter.Fire(new(10, 0, 0, 0, [id]), 2425, id * 1000, CombatOptions.Default).Verdict);
        Assert.Equal(FireVerdict.DuplicateProjectile, shooter.Fire(first, 2425, 100_000, CombatOptions.Default).Verdict);
        Assert.False(shooter.TryConsumeHint(1, 100_000, 1000, out _));
        Assert.Equal(4, shooter.AmmoOf(10));
        shooter.Reset();
        shooter.DeclareWeapon(20, 2425, 30);
        Assert.Equal(FireVerdict.Accepted, shooter.Fire(first with { WeaponGuid = 20 }, 2425, 101_000, CombatOptions.Default).Verdict);
    }

    [Fact]
    public void DuplicatePelletsAndMixedReplayedShotsAreRefusedAtomically()
    {
        var shooter = new ShooterCombatState();
        shooter.DeclareWeapon(10, 1374, 6);
        Assert.Equal(FireVerdict.DuplicateProjectile, shooter.Fire(new(10, 0, 0, 0, [1, 1]), 1374, 0, CombatOptions.Default).Verdict);
        Assert.Equal(6, shooter.AmmoOf(10));
        Assert.Equal(0, shooter.LiveHints);
        Assert.Equal(FireVerdict.Accepted, shooter.Fire(new(10, 0, 0, 0, [1, 2]), 1374, 1000, CombatOptions.Default).Verdict);
        Assert.Equal(FireVerdict.DuplicateProjectile, shooter.Fire(new(10, 0, 0, 0, [3, 2]), 1374, 2000, CombatOptions.Default).Verdict);
        Assert.False(shooter.HasLiveHint(3, 2000, 10_000));
        Assert.Equal(5, shooter.AmmoOf(10));
    }

    [Fact]
    public void AnAimHintCannotBorrowAnotherWeaponsProjectileOrReplayAShotgunSubset()
    {
        var session = new SessionCombat();
        session.Shooter.DeclareWeapon(10, 1374, 6);
        session.Shooter.DeclareWeapon(20, 2425, 30);
        session.Shooter.Fire(new(10, 0, 0, 0, [1, 2]), 1374, 1000, CombatOptions.Default);
        WeaponFireHint Hint(ulong guid, params uint[] ids) => new(guid, 0, 0, 0, 0,
            ids.Select(id => new WeaponFireHintEntry(id, 1, 0, 0, [])).ToArray());
        Assert.False(session.Shooter.TryPresentFire(Hint(20, 1), 1000, 10_000, out _));
        Assert.False(session.Shooter.TryPresentFire(Hint(10, 999, 2), 1000, 10_000, out _));
        Assert.True(session.Shooter.TryPresentFire(Hint(10, 1), 1000, 10_000, out _));
        Assert.False(session.Shooter.TryPresentFire(Hint(10, 2), 1000, 10_000, out _));
        Assert.True(session.Shooter.TryConsumeHint(1, 1000, 10_000, out _));
        Assert.True(session.Shooter.TryConsumeHint(2, 1000, 10_000, out _));
    }

    [Fact]
    public void AnExpiredOrUnknownAimHintCannotCreatePlayback()
    {
        var shooter = new ShooterCombatState();
        shooter.DeclareWeapon(10, 2425, 30);
        shooter.Fire(new(10, 0, 0, 0, [1]), 2425, 1000, CombatOptions.Default);
        var hint = new WeaponFireHint(10, 0, 0, 0, 0, [new(1, 1, 0, 0, [])]);
        Assert.False(shooter.TryPresentFire(hint, 999, 1000, out _));
        Assert.False(shooter.TryPresentFire(hint, 2001, 1000, out _));
        Assert.Equal(29, shooter.AmmoOf(10));
    }

    [Fact]
    public void ChangingWeaponsRetiresPlaybackButPreservesTheBulletInFlight()
    {
        var shooter = new ShooterCombatState();
        shooter.DeclareWeapon(10, 2425, 30);
        shooter.Fire(new(10, 0, 0, 0, [1]), 2425, 1000, CombatOptions.Default);
        shooter.EndWeaponPresentation(10);
        var hint = new WeaponFireHint(10, 0, 0, 0, 0, [new(1, 1, 0, 0, [])]);
        Assert.False(shooter.TryPresentFire(hint, 1001, 1000, out _));
        Assert.True(shooter.TryConsumeHint(1, 1001, 1000, out _));
        Assert.False(shooter.TryConsumeHint(1, 1001, 1000, out _));
    }
}
