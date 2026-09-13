using System.Numerics;
using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone.Combat;

public sealed class ArrivalCadenceTests
{
    private const ulong Gun = 0x3100_0000_0000_000C;
    private const uint Item = 2229;
    private const int Gate = 145;
    private static readonly CombatOptions Options = new() { ShippedRefireGate = true, RefireJitterMs = 16 };

    // The September 3 August Fire from AugustShotWireTests. Only timestamp/projectile identity
    // vary; the native body, wrapper, weapon identity and origin are retained.
    private static byte[] NativeFire(uint time, uint projectile)
    {
        byte[] wrapper = Convert.FromHexString("82000000001F01000000260000008292DEF216030C000000000000318C1BCF433C16A141F9CDE2C4010000000100000000000000");
        var members = new List<Range>();
        Assert.True(WeaponBaseDecoder.TryReadMultiWeapon(wrapper, members));
        byte[] fire = wrapper[Assert.Single(members)];
        BitConverter.GetBytes(time).CopyTo(fire, 1);
        BitConverter.GetBytes(projectile).CopyTo(fire, 30);
        return fire;
    }

    private static SessionCombat Session()
    {
        var session = new SessionCombat();
        session.Shooter.DeclareWeapon(Gun, Item, 30);
        return session;
    }

    private static int Dispatch(SessionCombat session, long now, params byte[][] packets)
    {
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(session, packets.Length == 1 ? packets[0] : ShootingPacketBuilder.MultiWeapon(packets),
            Options, Item, Gun, Vector3.Zero, now, results);
        return results.Count(result => result.LaunchRelay);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeTimestampSpacedShotsSurviveOneArrivalBatch(bool multiWeapon)
    {
        var session = Session();
        byte[] first = NativeFire(1000, 1), second = NativeFire(1145, 2);
        int accepted = multiWeapon ? Dispatch(session, 5000, first, second)
            : Dispatch(session, 5000, first) + Dispatch(session, 5000, second);
        Assert.Equal(2, accepted);
        Assert.Equal(28, session.Shooter.AmmoOf(Gun));
        Assert.True(session.Shooter.TryConsumeHint(1, 5000, 10000, out _));
        Assert.True(session.Shooter.TryConsumeHint(2, 5000, 10000, out _));
    }

    [Theory]
    [InlineData(0u, 145u)]
    [InlineData(1000u, 0u)]
    [InlineData(1000u, 1000u)]
    [InlineData(1000u, 999u)]
    [InlineData(1000u, 1001u)]
    [InlineData(1000u, 5000u)]
    [InlineData(1000u, 2147484648u)]
    public void LegacyReversedEqualAndLargeClockChangesGrantNoRecovery(uint first, uint second)
    {
        var session = Session();
        Assert.Equal(1, Dispatch(session, 5000, NativeFire(first, 1), NativeFire(second, 2)));
        Assert.Equal(29, session.Shooter.AmmoOf(Gun));
        Assert.False(session.Shooter.HasLiveHint(2, 5000, 10000));
    }

    [Fact]
    public void ClientClockWrapUsesOnlyTheSmallForwardDelta()
    {
        var session = Session();
        uint first = uint.MaxValue - 50;
        Assert.Equal(2, Dispatch(session, 5000, NativeFire(first, 1), NativeFire(unchecked(first + Gate), 2)));
    }

    [Fact]
    public void OneRecoveredIntervalIsDebtAndCannotBecomeAnUnlimitedBurst()
    {
        var session = Session();
        Assert.Equal(2, Dispatch(session, 5000, NativeFire(1000, 1), NativeFire(1145, 2)));
        for (uint id = 3; id <= 9; id++)
            Assert.Equal(0, Dispatch(session, 5000, NativeFire(1000 + (id - 1) * Gate, id)));
        // Until the borrowed server interval has elapsed there is no second credit, even when
        // client timestamps claim correct spacing. Refused packets do not advance either clock.
        Assert.Equal(0, Dispatch(session, 5000 + Gate - 1, NativeFire(1290, 3)));
        Assert.Equal(1, Dispatch(session, 5000 + Gate, NativeFire(1290, 3)));
        Assert.Equal(0, Dispatch(session, 5000 + Gate, NativeFire(1435, 4)));
        Assert.Equal(1, Dispatch(session, 5000 + 3 * Gate, NativeFire(1435, 4)));
        Assert.Equal(1, Dispatch(session, 5000 + 3 * Gate, NativeFire(1580, 5)));
        Assert.Equal(25, session.Shooter.AmmoOf(Gun));
    }

    [Fact]
    public void IdleTimeDoesNotBankMoreThanOneInterval()
    {
        var session = Session();
        Assert.Equal(1, Dispatch(session, 5000, NativeFire(1000, 1)));
        Assert.Equal(2, Dispatch(session, 100000, NativeFire(96000, 2), NativeFire(96145, 3),
            NativeFire(96290, 4), NativeFire(96435, 5)));
        Assert.Equal(27, session.Shooter.AmmoOf(Gun));
    }

    [Fact]
    public void RecoveredCreditCannotRepeatAProjectileEvenAfterItsHitWasConsumed()
    {
        var session = Session();
        Assert.Equal(1, Dispatch(session, 5000, NativeFire(1000, 1)));
        Assert.True(session.Shooter.TryConsumeHint(1, 5000, 10000, out _));
        Assert.Equal(0, Dispatch(session, 5000, NativeFire(1145, 1)));
        Assert.Equal(1, Dispatch(session, 5000, NativeFire(1145, 2)));
        Assert.Equal(28, session.Shooter.AmmoOf(Gun));
    }

    [Fact]
    public void RecoveryDoesNotBypassAnEmptyMagazine()
    {
        var session = Session();
        Assert.Equal(1, Dispatch(session, 5000, NativeFire(1000, 1)));
        session.Shooter.SetMagazine(Gun, 0);
        Assert.Equal(0, Dispatch(session, 5000, NativeFire(1145, 2)));
        Assert.Equal(0, session.Shooter.AmmoOf(Gun));
    }

    [Fact]
    public void WorldResetClearsThePerWeaponClock()
    {
        var session = Session();
        Assert.Equal(2, Dispatch(session, 5000, NativeFire(1000, 1), NativeFire(1145, 2)));
        session.Shooter.Reset();
        session.Shooter.DeclareWeapon(Gun, Item, 30);
        Assert.Equal(1, Dispatch(session, 5000, NativeFire(1145, 1)));
        Assert.Equal(0, Dispatch(session, 5000, NativeFire(1145, 2)));
        Assert.Equal(1, Dispatch(session, 5000, NativeFire(1290, 2)));
    }

    [Fact]
    public void ContinuousTimestampClaimsCannotExceedOneExtraShotAboveServerCadence()
    {
        var session = Session();
        uint time = 1000, projectile = 1;
        Assert.Equal(1, Dispatch(session, 5000, NativeFire(time, projectile++)));
        time += Gate;
        for (int elapsed = 0; elapsed <= Gate * 10; elapsed++)
        {
            // Every millisecond, claim another correctly spaced local shot. Only the bounded
            // server deadline decides how many of these untrusted claims may actually spend ammo.
            if (Dispatch(session, 5000 + elapsed, NativeFire(time, projectile++)) != 0) time += Gate;
            Assert.InRange(session.Shooter.ShotsFired, 1, 2 + elapsed / Gate);
        }
        Assert.Equal(12, session.Shooter.ShotsFired);
        Assert.Equal(18, session.Shooter.AmmoOf(Gun));
    }

    [Fact]
    public void DrawDeadlineStillRejectsAnOtherwiseEligibleRecovery()
    {
        var session = Session();
        Assert.Equal(1, Dispatch(session, 5000, NativeFire(1000, 1)));
        session.Draw.Select(Gun, 550, 150, 5000);
        Assert.Equal(0, Dispatch(session, 5000, NativeFire(1145, 2)));
        Assert.Equal(29, session.Shooter.AmmoOf(Gun));
        Assert.False(session.Shooter.HasLiveHint(2, 5000, 10000));
    }

    [Fact]
    public void RepeatedDeclarationsAndOtherWeaponsDoNotEraseTheBorrowedDeadline()
    {
        var session = Session();
        Assert.Equal(2, Dispatch(session, 5000, NativeFire(1000, 1), NativeFire(1145, 2)));
        Assert.False(session.Shooter.EnsureDeclared(Gun, Item, 30));
        session.Shooter.DeclareWeapon(Gun + 1, Item, 30);
        Assert.Equal(0, Dispatch(session, 5000, NativeFire(1290, 3)));
        Assert.Equal(FireVerdict.Accepted, session.Shooter.Fire(new WeaponFire(Gun + 1, 0, 0, 0, [4]),
            Item, 5000, Options, clientGameTime: 1000).Verdict);
        Assert.Equal(FireVerdict.RateOfFire, session.Shooter.Fire(new WeaponFire(Gun + 1, 0, 0, 0, [5]),
            Item, 5000, Options, clientGameTime: 1000).Verdict);
    }

    [Fact]
    public void DuplicatePelletsCannotUseRecoveryCredit()
    {
        var session = Session();
        Assert.Equal(1, Dispatch(session, 5000, NativeFire(1000, 1)));
        Assert.Equal(0, Dispatch(session, 5000,
            ShootingPacketBuilder.Fire(Gun, 0, 0, 0, [2, 2], gameTime: 1145)));
        Assert.Equal(29, session.Shooter.AmmoOf(Gun));
    }
}
