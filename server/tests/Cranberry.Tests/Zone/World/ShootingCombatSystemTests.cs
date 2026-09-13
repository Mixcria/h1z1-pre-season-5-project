using System.Numerics;
using Cranberry.Tests.Zone.Combat;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// docs/81 §3, §4. The whole loop through a real Match: a Fire, a ProjectileHitReport, the refusal
// ladder, and damage arriving at the ONE resolver. What is pinned here is that nothing writes health
// except Match.ResolveDamage, and that every gate refuses rather than guessing.
public sealed class ShootingCombatSystemTests
{
    private const ulong Rifle = 0x3100_0000_0000_0001;
    private const uint ArFifteen = AugustHeldWeapon.ItemDefinitionId;

    [Fact]
    public void FourArFifteenBodyShotsKillAPlayerThroughTheOneResolver()
    {
        var harness = new MatchHarness();
        (MatchPlayer shooter, MatchPlayer victim) = TwoPlayers(harness);

        for (int shot = 0; shot < 4; shot++)
        {
            FireAndHit(harness, shooter, victim, projectileId: (uint)shot, "SPINE");
            harness.Step(6);   // 300 ms - clear of the AR-15's own 120 ms refire gate
        }

        Assert.Equal(0, victim.Health);
        Assert.Equal(LifeState.Dead, victim.Life);
        Assert.Equal(1, harness.Match.Deaths);
        Assert.Equal(1u, shooter.Kills);
        Assert.Equal(4, harness.Match.Combat.HitsResolved);

        // One death, one ce 04. The single-resolver promise, asserted rather than assumed.
        Assert.Equal(1, MatchHarness.SinkOf(victim).CountOf(0xce, 0x0004));
    }

    [Fact]
    public void ThreeShotsLeaveAQuarterOfTheBarStanding()
    {
        var harness = new MatchHarness();
        (MatchPlayer shooter, MatchPlayer victim) = TwoPlayers(harness);

        for (int shot = 0; shot < 3; shot++)
        {
            FireAndHit(harness, shooter, victim, (uint)shot, "SPINE");
            harness.Step(6);
        }

        // Three AR-15 body shots are 7,500 of the 10,000 bar, so the fourth is the kill. The bar
        // itself reads a little under 2,500 because the wound the first shot opened has been
        // bleeding at 50 units a second ever since - which is the point of the retail model.
        Assert.Equal(7_500, shooter.Combat.DamageDealt);
        Assert.InRange(victim.Health, 2_300, 2_500);
        Assert.True(victim.IsAlive);
    }

    [Fact]
    public void AHeadshotOnABareHeadIsOneShot()
    {
        var harness = new MatchHarness();
        (MatchPlayer shooter, MatchPlayer victim) = TwoPlayers(harness);

        FireAndHit(harness, shooter, victim, 1, "HEAD");

        Assert.Equal(0, victim.Health);
        Assert.Equal(1u, shooter.Kills);
    }

    [Fact]
    public void AHelmetTurnsAHeadshotIntoTheRetailTwoTap()
    {
        var harness = new MatchHarness();
        (MatchPlayer shooter, MatchPlayer victim) = TwoPlayers(harness);
        victim.WornHelmetItemId = FirstHelmetId();

        FireAndHit(harness, shooter, victim, 1, "HEAD");
        Assert.Equal(10_000, victim.Health);          // absorbed
        Assert.True(victim.IsAlive);
        Assert.False(victim.Armour.HelmetIntact);     // ...and the helmet is gone

        harness.Step(6);
        FireAndHit(harness, shooter, victim, 2, "HEAD");
        Assert.Equal(0, victim.Health);               // the second one kills
    }

    [Fact]
    public void AHitWithNoAcceptedShotBehindItIsRefused()
    {
        var harness = new MatchHarness();
        (MatchPlayer shooter, MatchPlayer victim) = TwoPlayers(harness);

        Post(harness, shooter, ShootingPacketBuilder.HitReport(42, victim.AccountGuid, "SPINE"));
        harness.Step();

        Assert.Equal(CombatRefusal.NoFireHint, harness.Match.Combat.Last.Refusal);
        Assert.Equal(10_000, victim.Health);
    }

    [Fact]
    public void ADuplicatedHitReportIsPaidOnlyOnce()
    {
        var harness = new MatchHarness();
        (MatchPlayer shooter, MatchPlayer victim) = TwoPlayers(harness);

        Post(harness, shooter, ShootingPacketBuilder.Fire(
            Rifle, shooter.Position.X, shooter.Position.Y, shooter.Position.Z, [5]));
        Post(harness, shooter, ShootingPacketBuilder.HitReport(5, victim.AccountGuid, "SPINE"));
        Post(harness, shooter, ShootingPacketBuilder.HitReport(5, victim.AccountGuid, "SPINE"));
        harness.Step();

        Assert.Equal(7_500, victim.Health);
        Assert.Equal(CombatRefusal.NoFireHint, harness.Match.Combat.Last.Refusal);
        _ = shooter;
    }

    [Fact]
    public void AHitOnATargetTheShooterWasNeverToldAboutIsRefused()
    {
        var harness = new MatchHarness();
        MatchPlayer shooter = harness.AddPlayer("Shooter");
        MatchPlayer victim = harness.AddPlayer("Victim");
        shooter.HeldWeaponItemDefinitionId = ArFifteen;
        // Deliberately NOT shooter.View.MarkKnown(victim.Id).

        FireAndHit(harness, shooter, victim, 1, "SPINE");

        Assert.Equal(CombatRefusal.TargetNotStreamed, harness.Match.Combat.Last.Refusal);
        Assert.Equal(10_000, victim.Health);
    }

    [Fact]
    public void AHitAcrossTheMapIsRefused()
    {
        var harness = new MatchHarness();
        (MatchPlayer shooter, MatchPlayer victim) = TwoPlayers(harness);
        harness.MoveTo(victim, shooter.Position + new Vector3(5_000, 0, 0));

        FireAndHit(harness, shooter, victim, 1, "SPINE");

        Assert.Equal(CombatRefusal.OutOfRange, harness.Match.Combat.Last.Refusal);
        Assert.Equal(10_000, victim.Health);
    }

    [Fact]
    public void APlayerCannotShootThemselves()
    {
        var harness = new MatchHarness();
        (MatchPlayer shooter, _) = TwoPlayers(harness);

        Post(harness, shooter, ShootingPacketBuilder.Fire(
            Rifle, shooter.Position.X, shooter.Position.Y, shooter.Position.Z, [1]));
        Post(harness, shooter, ShootingPacketBuilder.HitReport(1, shooter.AccountGuid, "HEAD"));
        harness.Step();

        Assert.Equal(CombatRefusal.SelfHit, harness.Match.Combat.Last.Refusal);
        Assert.Equal(10_000, shooter.Health);
    }

    [Fact]
    public void AnUnknownLayoutDecodesAndLogsButNeverDamages()
    {
        // The one-word revert for "shooting went wrong". The decode still happens, so the evidence
        // keeps arriving; only the damage stops.
        var harness = new MatchHarness(MatchSettings.Default with
        {
            Combat = CombatOptions.Default with { Layout = WeaponFireLayout.Unknown },
        });
        (MatchPlayer shooter, MatchPlayer victim) = TwoPlayers(harness);

        FireAndHit(harness, shooter, victim, 1, "HEAD");

        Assert.Equal(CombatRefusal.LayoutNotTrusted, harness.Match.Combat.Last.Refusal);
        Assert.Equal(10_000, victim.Health);
    }

    [Fact]
    public void DamageCanBeSwitchedOffWithoutSwitchingOffTheResolution()
    {
        var harness = new MatchHarness(MatchSettings.Default with
        {
            Combat = CombatOptions.Default with { EnableCombatDamage = false },
        });
        (MatchPlayer shooter, MatchPlayer victim) = TwoPlayers(harness);

        FireAndHit(harness, shooter, victim, 1, "SPINE");

        Assert.True(harness.Match.Combat.Last.Accepted);
        Assert.Equal(2_500, harness.Match.Combat.Last.Outcome.DamageUnits);
        Assert.Equal(10_000, victim.Health);   // ...but nothing moved
    }

    [Fact]
    public void MultiWeaponMembersAreDispatchedIndividually()
    {
        var harness = new MatchHarness();
        (MatchPlayer shooter, MatchPlayer victim) = TwoPlayers(harness);

        Post(harness, shooter, ShootingPacketBuilder.MultiWeapon(
            ShootingPacketBuilder.FireStateUpdate(Rifle, 1),
            ShootingPacketBuilder.Fire(
                Rifle, shooter.Position.X, shooter.Position.Y, shooter.Position.Z, [3]),
            ShootingPacketBuilder.HitReport(3, victim.AccountGuid, "SPINE")));
        harness.Step();

        Assert.Equal(7_500, victim.Health);
        Assert.Equal(1, harness.Match.Combat.HitsResolved);
        _ = shooter;
    }

    [Fact]
    public void AShotgunBlastPaysEveryPelletAndNotJustTheFirst()
    {
        var harness = new MatchHarness();
        (MatchPlayer shooter, MatchPlayer victim) = TwoPlayers(harness);
        shooter.HeldWeaponItemDefinitionId = 1374;   // 12GA pump

        uint[] pellets = Enumerable.Range(1, 17).Select(id => (uint)id).ToArray();
        Post(harness, shooter, ShootingPacketBuilder.Fire(
            Rifle, shooter.Position.X, shooter.Position.Y, shooter.Position.Z, pellets));

        foreach (uint pellet in pellets)
        {
            Post(harness, shooter, ShootingPacketBuilder.HitReport(pellet, victim.AccountGuid, "SPINE"));
        }

        harness.Step();

        // The systematic 17-pellet blast retains the reconstructed 120 HP full-hit budget.
        Assert.Equal(0, victim.Health);
        Assert.Equal(17, harness.Match.Combat.HitsResolved);
    }

    [Fact]
    public void AnUndecodablePacketIsCountedAndDoesNothing()
    {
        var harness = new MatchHarness();
        (MatchPlayer shooter, MatchPlayer victim) = TwoPlayers(harness);

        byte[] runt = [ZoneOpcodes.WeaponBase, 0, 0, 0, 0, WeaponBaseDecoder.SubFire, 1, 2, 3];
        Post(harness, shooter, runt);
        harness.Step();

        Assert.True(harness.Match.Combat.Undecodable > 0);
        Assert.Equal(10_000, victim.Health);
    }

    [Fact]
    public void BleedingKeepsDrainingAfterTheShotAndCreditsTheShooter()
    {
        var harness = new MatchHarness();
        (MatchPlayer shooter, MatchPlayer victim) = TwoPlayers(harness);

        FireAndHit(harness, shooter, victim, 1, "SPINE");
        Assert.Equal(7_500, victim.Health);
        Assert.Equal(1, victim.Medical.Bleed);

        harness.StepSeconds(2);   // two 1 Hz bleed ticks at 50 units each
        Assert.Equal(7_400, victim.Health);
        Assert.Equal(shooter.Id, victim.LastWoundedBy);
    }

    [Fact]
    public void GasDoesNotBleed()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer("Alone");
        player.Health = 10_000;

        harness.Match.Damage(player, 500, DamageCause.ToxicGas);
        harness.Step();

        Assert.Equal(9_500, player.Health);
        Assert.Equal(0, player.Medical.Bleed);
    }

    [Fact]
    public void HealingQueuesThroughTheSameResolverAndNeverOverfills()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer("Alone");
        player.Health = 5_000;

        harness.Match.Heal(player, 2_000);
        harness.Step();
        Assert.Equal(7_000, player.Health);

        harness.Match.Heal(player, 99_000);
        harness.Step();
        Assert.Equal(10_000, player.Health);   // clamped at the bar, and no death from a negative
        Assert.True(player.IsAlive);
    }

    private static (MatchPlayer Shooter, MatchPlayer Victim) TwoPlayers(MatchHarness harness)
    {
        MatchPlayer shooter = harness.AddPlayer("Shooter");
        MatchPlayer victim = harness.AddPlayer("Victim");
        shooter.HeldWeaponItemDefinitionId = ArFifteen;
        shooter.View.MarkKnown(victim.Id);
        return (shooter, victim);
    }

    /// <summary>
    /// One trigger pull and its hit report. The muzzle is the shooter's own position, because the
    /// range gate measures from the muzzle the client declared to where the VICTIM was at the shot's
    /// tick - a fire from the origin against players standing at the staging spawn is 4,900 units
    /// and is correctly refused.
    /// </summary>
    private static void FireAndHit(
        MatchHarness harness, MatchPlayer shooter, MatchPlayer victim, uint projectileId, string where)
    {
        Post(harness, shooter, ShootingPacketBuilder.Fire(
            Rifle, shooter.Position.X, shooter.Position.Y, shooter.Position.Z, [projectileId]));
        Post(harness, shooter, ShootingPacketBuilder.HitReport(projectileId, victim.AccountGuid, where));
        harness.Step();
    }

    private static void Post(MatchHarness harness, MatchPlayer shooter, byte[] packet) =>
        harness.Match.Post(
            new Command(CommandKind.Fire, shooter.Slot, EntityId.None, 0, 0, 0), packet);

    private static uint FirstHelmetId() =>
        AugustArmourFacts.All.First(row => row.ItemClass == AugustArmourFacts.HeadClass).ItemId;
}
