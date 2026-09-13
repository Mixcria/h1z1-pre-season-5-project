using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Combat;

// docs/81 §3, §4e. The owner's arbitration: what the server trusts the client for, what it
// recomputes, and the order of the refusal ladder. These are the rules that stop a misread or a
// replayed packet paying damage.
public sealed class ShootingArbitrationTests
{
    private const ulong Rifle = 0x3100_0000_0000_0001;
    private const uint ArFifteen = AugustHeldWeapon.ItemDefinitionId;   // 2425

    private static WeaponFire Shot(params uint[] ids) => new(Rifle, 10f, 20f, 30f, ids);

    [Fact]
    public void AShotRecordsOneFireHintPerProjectile()
    {
        var state = new ShooterCombatState();
        state.DeclareWeapon(Rifle, ArFifteen);

        FireResult result = state.Fire(Shot(1, 2, 3, 4), ArFifteen, nowMs: 1_000, CombatOptions.Default);

        Assert.Equal(FireVerdict.Accepted, result.Verdict);
        Assert.Equal(4, result.Projectiles);
        Assert.Equal(4, state.LiveHints);
        Assert.Equal(29, result.AmmoLeft);   // CLIP_SIZE 30 on the August sheet
        Assert.Equal(30, result.ClipSize);
    }

    [Fact]
    public void AHitMustNameAProjectileFromAShotTheServerAccepted()
    {
        // The anti-replay rule, and the reason a misread hit report can never do damage.
        var state = new ShooterCombatState();
        state.DeclareWeapon(Rifle, ArFifteen);

        Assert.False(state.TryConsumeHint(7, nowMs: 0, CombatOptions.Default.FireHintLifetimeMs, out _));

        state.Fire(Shot(7), ArFifteen, nowMs: 0, CombatOptions.Default);
        Assert.True(state.TryConsumeHint(7, 0, CombatOptions.Default.FireHintLifetimeMs, out FireHint hint));
        Assert.Equal(ArFifteen, hint.ItemDefinitionId);
        Assert.Equal(10f, hint.X);
    }

    [Fact]
    public void AHintIsConsumedSoADuplicatedReportCannotBePaidTwice()
    {
        var state = new ShooterCombatState();
        state.Fire(Shot(9), ArFifteen, nowMs: 0, CombatOptions.Default);

        Assert.True(state.TryConsumeHint(9, 0, 10_000, out _));
        Assert.False(state.TryConsumeHint(9, 0, 10_000, out _));
    }

    [Fact]
    public void AHintExpires()
    {
        var state = new ShooterCombatState();
        state.Fire(Shot(4), ArFifteen, nowMs: 1_000, CombatOptions.Default);

        Assert.False(state.TryConsumeHint(4, nowMs: 1_000 + 10_001, lifetimeMs: 10_000, out _));
    }

    [Fact]
    public void AnEmptyMagazineRefusesTheShot()
    {
        var state = new ShooterCombatState();
        state.DeclareWeapon(Rifle, ArFifteen);
        long now = 0;

        for (int i = 0; i < 30; i++)
        {
            Assert.Equal(
                FireVerdict.Accepted,
                state.Fire(Shot((uint)i), ArFifteen, now, CombatOptions.Default).Verdict);
            now += 200;   // clear of the 120 ms gate
        }

        Assert.Equal(0, state.AmmoOf(Rifle));
        Assert.Equal(FireVerdict.EmptyMagazine, state.Fire(Shot(99), ArFifteen, now, CombatOptions.Default).Verdict);

        Assert.True(state.Reload(Rifle));
        Assert.Equal(30, state.AmmoOf(Rifle));
        Assert.Equal(FireVerdict.Accepted, state.Fire(Shot(99), ArFifteen, now, CombatOptions.Default).Verdict);
    }

    [Fact]
    public void TheRateOfFireGateUsesTheWeaponsOwnRefireTime()
    {
        var state = new ShooterCombatState();
        state.DeclareWeapon(Rifle, ArFifteen);

        Assert.Equal(FireVerdict.Accepted, state.Fire(Shot(1), ArFifteen, 1_000, CombatOptions.Default).Verdict);

        FireResult tooSoon = state.Fire(Shot(2), ArFifteen, 1_050, CombatOptions.Default);
        Assert.Equal(FireVerdict.RateOfFire, tooSoon.Verdict);
        Assert.Equal(120, tooSoon.GateMs);
        Assert.Equal(50, tooSoon.SinceLastShotMs);
        Assert.Equal(29, state.AmmoOf(Rifle));   // a refused shot costs no ammunition

        Assert.Equal(FireVerdict.Accepted, state.Fire(Shot(3), ArFifteen, 1_120, CombatOptions.Default).Verdict);
    }

    [Fact]
    public void TheFirstShotOfAMatchIsNeverGatedByTheClockStartingAtZero()
    {
        // The match clock starts at 0, so "LastFireAtMs == 0 means never fired" would have made the
        // second shot of a match look like the first.
        var state = new ShooterCombatState();

        Assert.Equal(FireVerdict.Accepted, state.Fire(Shot(1), ArFifteen, 0, CombatOptions.Default).Verdict);
        Assert.Equal(FireVerdict.RateOfFire, state.Fire(Shot(2), ArFifteen, 10, CombatOptions.Default).Verdict);
    }

    [Fact]
    public void TheHintRingSurvivesMoreShotsThanItsCapacity()
    {
        var state = new ShooterCombatState();
        long now = 0;

        for (uint i = 0; i < 40; i++)
        {
            state.Reload(Rifle);   // the ring is what is under test, not the magazine
            state.Fire(Shot(i), ArFifteen, now, CombatOptions.Default);
            now += 200;
        }

        Assert.Equal(ShooterCombatState.HintCapacity, state.LiveHints);
        Assert.True(state.TryConsumeHint(39, now, 10_000_000, out _));   // the newest is there
        Assert.False(state.TryConsumeHint(0, now, 10_000_000, out _));   // the oldest rolled off
    }

    // ---- the hit rule ---------------------------------------------------------------------

    [Fact]
    public void ARifleHeadshotIsLethalOnABareHead()
    {
        var armour = default(Armour);
        HitOutcome outcome = Resolve(ref armour, weapon: 6, "HEAD");

        Assert.Equal(RetailBalance.FullHealthUnits, outcome.DamageUnits);
        Assert.True(outcome.Headshot);
        Assert.False(outcome.DamagedArmour);
    }

    [Fact]
    public void AHelmetAbsorbsTheFirstHeadshotAndTheSecondKills()
    {
        // The retail two-tap, and Daybreak's own stated design.
        var armour = default(Armour);
        Assert.True(ArmourModel.IsHelmet(FirstHelmetId()));

        HitOutcome first = Resolve(ref armour, 6, "HEAD", helmetItemId: FirstHelmetId());
        Assert.Equal(0, first.DamageUnits);
        Assert.True(first.DamagedArmour);
        Assert.True(first.BrokeArmour);
        Assert.False(first.Bleeds);

        HitOutcome second = Resolve(ref armour, 6, "HEAD", helmetItemId: FirstHelmetId());
        Assert.Equal(RetailBalance.FullHealthUnits, second.DamageUnits);
    }

    [Fact]
    public void AHuntingRifleHeadshotGoesThroughAHelmet()
    {
        var armour = default(Armour);
        HitOutcome outcome = Resolve(ref armour, weapon: 1373, "HEAD", helmetItemId: FirstHelmetId());

        Assert.Equal(RetailBalance.FullHealthUnits, outcome.DamageUnits);
        Assert.False(outcome.DamagedArmour);   // the helmet is not even consumed
    }

    [Fact]
    public void AShotgunPelletToTheHeadIsABodyPellet()
    {
        // One pellet of seventeen is not a "headshot" in the TWO-TAP sense: it neither kills outright
        // nor consumes a helmet, and it pays exactly the body-pellet damage. The marker bit still
        // says head, which is the owner's own behaviour and is honest - the shooter did hit a head.
        var armour = default(Armour);
        HitOutcome outcome = Resolve(ref armour, weapon: 1374, "HEAD", helmetItemId: FirstHelmetId());

        Assert.Equal(706, outcome.DamageUnits);
        Assert.False(outcome.DamagedArmour);
        Assert.True(armour.HelmetIntact || armour.HeadItemId == 0);
    }

    [Fact]
    public void MakeshiftArmourEatsOneBodyShotAndLaminatedEatsTwo()
    {
        var makeshift = default(Armour);
        HitOutcome one = Resolve(ref makeshift, 6, "SPINE", bodyItemId: 2204);
        Assert.Equal(0, one.DamageUnits);
        Assert.True(one.BrokeArmour);
        Assert.Equal(2_500, Resolve(ref makeshift, 6, "SPINE", bodyItemId: 2204).DamageUnits);

        var laminated = default(Armour);
        Assert.False(Resolve(ref laminated, 6, "SPINE", bodyItemId: 2271).BrokeArmour);
        Assert.True(Resolve(ref laminated, 6, "SPINE", bodyItemId: 2271).BrokeArmour);
        Assert.Equal(2_500, Resolve(ref laminated, 6, "SPINE", bodyItemId: 2271).DamageUnits);
    }

    [Fact]
    public void TheHeadSetIsHeadGlassesAndNeckAndNothingElse()
    {
        Assert.True(HitRule.IsHead("HEAD"));
        Assert.True(HitRule.IsHead("head"));
        Assert.True(HitRule.IsHead("GLASSES"));
        Assert.True(HitRule.IsHead("NECK"));
        Assert.False(HitRule.IsHead("SPINE_UPPER"));
        Assert.False(HitRule.IsHead(string.Empty));
    }

    [Fact]
    public void ABodyShotBleedsAndAnAbsorbedOneDoesNot()
    {
        var bare = default(Armour);
        Assert.True(Resolve(ref bare, 6, "SPINE").Bleeds);

        var plated = default(Armour);
        Assert.False(Resolve(ref plated, 6, "SPINE", bodyItemId: 2204).Bleeds);
    }

    // ---- bleeding and healing ---------------------------------------------------------------

    [Fact]
    public void BleedingRisesAtMostOncePerSecond()
    {
        // A shotgun blast is 8-12 separate hit reports; without the cooldown one trigger pull takes
        // a player from no bleed to the maximum in a single frame.
        var armour = default(Armour);
        var medical = default(MedicalState);

        Assert.True(medical.Wound(ref armour, wearingArmour: false, nowMs: 0));
        Assert.Equal(1, medical.Bleed);

        for (int i = 1; i < 12; i++)
        {
            Assert.False(medical.Wound(ref armour, false, nowMs: i * 50));
        }

        Assert.Equal(1, medical.Bleed);
        Assert.True(medical.Wound(ref armour, false, nowMs: 1_000));
        Assert.Equal(2, medical.Bleed);
    }

    [Fact]
    public void ArmourHalvesBleedingByCountingRatherThanRolling()
    {
        var armour = default(Armour);
        armour.Wearing(2271);
        var medical = default(MedicalState);

        Assert.False(medical.Wound(ref armour, wearingArmour: true, nowMs: 0));      // 1st: parity
        Assert.True(medical.Wound(ref armour, true, nowMs: 2_000));                  // 2nd: rises
        Assert.Equal(1, medical.Bleed);
        Assert.False(medical.Wound(ref armour, true, nowMs: 4_000));
        Assert.True(medical.Wound(ref armour, true, nowMs: 6_000));
        Assert.Equal(2, medical.Bleed);
    }

    [Fact]
    public void BleedDrainsFiftyUnitsPerStatePerSecond()
    {
        var armour = default(Armour);
        var medical = default(MedicalState);
        medical.Wound(ref armour, false, nowMs: 0);

        Assert.Equal(0, medical.PumpBleed(500));        // not due yet
        Assert.Equal(50, medical.PumpBleed(1_000));
        Assert.Equal(0, medical.PumpBleed(1_500));
        Assert.Equal(50, medical.PumpBleed(2_000));

        medical.StopBleeding(ref armour);
        Assert.Equal(0, medical.PumpBleed(3_000));
        Assert.Equal(0, medical.Bleed);
    }

    [Fact]
    public void ABandageGivesTenHitPointsOverTenSecondsWithNoSecondWindUp()
    {
        // The cast bar has already run the apply time; delaying the first tick again would make a
        // 3 s bandage 3 s of bar and then 3 s of nothing.
        Medical? row = MedicalModel.For(2423);
        Assert.NotNull(row);

        var medical = default(MedicalState);
        medical.BeginHeal(row.Value, nowMs: 5_000);

        Assert.Equal(100, medical.PumpHeal(5_000));   // immediate
        int total = 100;

        for (int i = 1; i < 10; i++)
        {
            total += medical.PumpHeal(5_000 + (i * 1_000));
        }

        Assert.Equal(1_000, total);                   // 10 HP
        Assert.Equal(0, medical.PumpHeal(50_000));    // and it is finished
    }

    [Fact]
    public void AShotCancelsARunningHeal()
    {
        Medical? row = MedicalModel.For(2424);
        Assert.NotNull(row);

        var medical = default(MedicalState);
        medical.BeginHeal(row.Value, 0);
        Assert.Equal(100, medical.PumpHeal(0));

        medical.CancelHeal();
        Assert.Equal(0, medical.PumpHeal(1_000));
    }

    // ---- the practice target ----------------------------------------------------------------

    [Fact]
    public void PracticeTargetsStandInFrontOfTheSpawnerOnItsOwnHeight()
    {
        var pack = new PracticeTargetPack();
        IReadOnlyList<PracticeTarget> spawned = pack.Spawn(
            new Vector3(100, 50, 200), heading: 0f, count: 2, distance: 4f, health: 10_000);

        Assert.Equal(2, spawned.Count);
        Assert.Equal(50, spawned[0].Position.Y);                 // the spawner's own height
        Assert.Equal(204, spawned[0].Position.Z, 3);             // 4 units ahead at heading 0
        Assert.Equal(206, spawned[1].Position.Z, 3);
        Assert.NotEqual(spawned[0].WorldGuid, spawned[1].WorldGuid);
        Assert.NotEqual(spawned[0].TransientId, spawned[1].TransientId);
        Assert.Same(spawned[0], pack.Find(spawned[0].WorldGuid));
    }

    [Fact]
    public void APracticeTargetDiesThroughTheRealDamageArithmetic()
    {
        var pack = new PracticeTargetPack();
        PracticeTarget target = pack.Spawn(Vector3.Zero, 0f, 1, 4f, 10_000)[0];

        for (int i = 0; i < 3; i++)
        {
            Assert.False(target.Damage(2_500, nowMs: i));   // AR-15 body shots
            Assert.True(target.IsAlive);
        }

        Assert.True(target.Damage(2_500, nowMs: 4));         // the fourth kills
        Assert.False(target.IsAlive);
        Assert.Equal(0, target.Health);
        Assert.False(target.Damage(2_500, nowMs: 5));        // a corpse does not die twice

        Assert.False(target.DespawnDue(nowMs: 5_000, afterMs: 10_000));
        Assert.True(target.DespawnDue(nowMs: 20_000, afterMs: 10_000));
    }

    /// <summary>
    /// Ordinary world entry creates no diagnostic human actor. A shooting session can explicitly
    /// enable the existing single-target spawn without changing its health or packet behavior.
    /// </summary>
    [Fact]
    public void TheSpawnerBuildsNothingByDefaultAndOneTargetWhenExplicitlyEnabled()
    {
        var off = new PracticeTargetPack();

        Assert.Empty(PracticeTargetSpawner.Build(
            off, Vector3.Zero, 0f, CombatOptions.Default));
        Assert.Equal(0, off.Count);

        var pack = new PracticeTargetPack();

        IReadOnlyList<PracticeTargetSpawn> burst = PracticeTargetSpawner.Build(
            pack, Vector3.Zero, 0f, CombatOptions.Default with { PracticeTarget = true });

        Assert.Single(burst);
        Assert.Equal(1, pack.Count);
        Assert.Equal(10_000, burst[0].Target.MaxHealth);
    }

    private static uint FirstHelmetId() =>
        AugustArmourFacts.All.First(row => row.ItemClass == AugustArmourFacts.HeadClass).ItemId;

    private static HitOutcome Resolve(
        ref Armour armour,
        uint weapon,
        string location,
        uint bodyItemId = 0,
        uint helmetItemId = 0,
        double distance = 0) =>
        HitRule.Resolve(
            ref armour,
            bodyItemId,
            helmetItemId,
            weapon,
            location,
            distance,
            CombatOptions.Default.UnmappedWeaponBodyUnits);
}
