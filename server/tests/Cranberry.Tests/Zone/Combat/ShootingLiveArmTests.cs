using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone.Combat;

// docs/81 §8 item 9, and the closing of D52. WeaponFireArm is what ZoneService calls instead of
// logging a 0x82 and returning. What is pinned here is the promise the owner has to be able to
// trust: every packet produces exactly one log line, a decode failure still prints its untruncated
// hex and does nothing, and no damage is ever paid without a shot behind it.
public sealed class ShootingLiveArmTests
{
    private const ulong Rifle = 0x3100_0000_0000_0001;
    private const uint ArFifteen = AugustHeldWeapon.ItemDefinitionId;

    [Fact]
    public void EveryPacketProducesExactlyOneLine()
    {
        var session = new SessionCombat();
        var results = new List<WeaponArmResult>();

        Handle(session, ShootingPacketBuilder.Fire(Rifle, 0, 0, 0, [1]), results);

        WeaponArmResult only = Assert.Single(results);
        Assert.StartsWith(WeaponFireArm.Tag, only.Line, StringComparison.Ordinal);
        Assert.Contains("82 03 Fire", only.Line, StringComparison.Ordinal);
        Assert.Contains("AR-15", only.Line, StringComparison.Ordinal);
        Assert.Contains("ammo 29/30", only.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadablePacketPrintsItsFullHexAndDoesNothing()
    {
        // The pre-D52 behaviour, deliberately preserved on the failure arm: docs/56 §1.3 counted
        // ZERO 0x82 packets in 96 sessions, so these bytes are the whole point of the exercise.
        var session = new SessionCombat();
        var results = new List<WeaponArmResult>();
        byte[] runt = [ZoneOpcodes.WeaponBase, 0, 0, 0, 0, WeaponBaseDecoder.SubFire, 0xAA, 0xBB];

        Handle(session, runt, results);

        WeaponArmResult only = Assert.Single(results);
        Assert.Contains("EVIDENCE", only.Line, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexString(runt), only.Line, StringComparison.Ordinal);
        Assert.Null(only.Marker);
        Assert.Equal(1, session.Undecodable);
    }

    [Fact]
    public void MultiWeaponLogsTheWrapperAndThenEachMember()
    {
        var session = new SessionCombat();
        var results = new List<WeaponArmResult>();

        Handle(
            session,
            ShootingPacketBuilder.MultiWeapon(
                ShootingPacketBuilder.FireStateUpdate(Rifle, WeaponBaseDecoder.EmptyFireState),
                ShootingPacketBuilder.Fire(Rifle, 0, 0, 0, [1])),
            results);

        Assert.Equal(3, results.Count);
        Assert.Contains("MultiWeapon", results[0].Line, StringComparison.Ordinal);
        Assert.Contains("members=2", results[0].Line, StringComparison.Ordinal);
        Assert.Contains("magazine is DRY", results[1].Line, StringComparison.Ordinal);
        Assert.Contains("82 03 Fire", results[2].Line, StringComparison.Ordinal);
    }

    [Fact]
    public void AHitOnAPracticeTargetPaysTheRetailDamageAndReturnsAMarker()
    {
        var session = new SessionCombat();
        PracticeTarget target = session.Targets.Spawn(Vector3.Zero, 0f, 1, 4f, 10_000)[0];
        var results = new List<WeaponArmResult>();

        Handle(session, ShootingPacketBuilder.Fire(Rifle, 0, 0, 0, [1]), results);
        Handle(session, ShootingPacketBuilder.HitReport(1, target.WorldGuid, "SPINE"), results);

        WeaponArmResult hit = Assert.Single(results);
        Assert.Equal(7_500, target.Health);
        Assert.Same(target, hit.HitTarget);
        Assert.False(hit.Killed);
        Assert.NotNull(hit.Marker);
        Assert.Equal(0x01, hit.Marker!.Flags);   // enemy, flesh
    }

    [Fact]
    public void FourBodyShotsPutAPracticeTargetDown()
    {
        var session = new SessionCombat();
        PracticeTarget target = session.Targets.Spawn(Vector3.Zero, 0f, 1, 4f, 10_000)[0];
        var results = new List<WeaponArmResult>();
        bool killed = false;

        for (uint shot = 0; shot < 4; shot++)
        {
            Handle(session, ShootingPacketBuilder.Fire(Rifle, 0, 0, 0, [shot]), results, nowMs: shot * 200);
            Handle(session, ShootingPacketBuilder.HitReport(shot, target.WorldGuid, "SPINE"), results,
                nowMs: shot * 200);
            killed |= results[0].Killed;
        }

        Assert.True(killed);
        Assert.False(target.IsAlive);
        Assert.Equal(4, session.Shooter.HitsRegistered);
        Assert.Equal(10_000, session.Shooter.DamageDealt);
    }

    [Fact]
    public void AHeadshotMarkerCarriesTheHeadshotBit()
    {
        var session = new SessionCombat();
        PracticeTarget target = session.Targets.Spawn(Vector3.Zero, 0f, 1, 4f, 10_000)[0];
        var results = new List<WeaponArmResult>();

        Handle(session, ShootingPacketBuilder.Fire(Rifle, 0, 0, 0, [1]), results);
        Handle(session, ShootingPacketBuilder.HitReport(1, target.WorldGuid, "HEAD"), results);

        Assert.True(results[0].Killed);
        Assert.Equal(0x15, results[0].Marker!.Flags); // enemy, headshot, killed
    }

    [Fact]
    public void TheMarkerStillGoesOutWhenTheDamageModelIsOff()
    {
        // The owner's own design rule: the marker is UI, so seeing the X confirms the whole hit path
        // even in a diagnostic run.
        var session = new SessionCombat();
        PracticeTarget target = session.Targets.Spawn(Vector3.Zero, 0f, 1, 4f, 10_000)[0];
        var results = new List<WeaponArmResult>();
        CombatOptions options = CombatOptions.Default with { EnableCombatDamage = false };

        Handle(session, ShootingPacketBuilder.Fire(Rifle, 0, 0, 0, [1]), results, options: options);
        Handle(session, ShootingPacketBuilder.HitReport(1, target.WorldGuid, "SPINE"), results,
            options: options);

        Assert.NotNull(results[0].Marker);
        Assert.Equal(10_000, target.Health);
        Assert.Contains("damage model OFF", results[0].Line, StringComparison.Ordinal);
    }

    [Fact]
    public void AHitWithNoShotBehindItIsRefusedAndSaysWhy()
    {
        var session = new SessionCombat();
        PracticeTarget target = session.Targets.Spawn(Vector3.Zero, 0f, 1, 4f, 10_000)[0];
        var results = new List<WeaponArmResult>();

        Handle(session, ShootingPacketBuilder.HitReport(1, target.WorldGuid, "HEAD"), results);

        Assert.Equal(10_000, target.Health);
        Assert.Null(results[0].Marker);
        Assert.Contains("anti-replay", results[0].Line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUnknownLayoutDecodesAndLogsWithoutTouchingHealth()
    {
        var session = new SessionCombat();
        PracticeTarget target = session.Targets.Spawn(Vector3.Zero, 0f, 1, 4f, 10_000)[0];
        var results = new List<WeaponArmResult>();
        CombatOptions options = CombatOptions.Default with { Layout = WeaponFireLayout.Unknown };

        Handle(session, ShootingPacketBuilder.Fire(Rifle, 0, 0, 0, [1]), results, options: options);
        Handle(session, ShootingPacketBuilder.HitReport(1, target.WorldGuid, "HEAD"), results,
            options: options);

        Assert.Equal(10_000, target.Health);
        Assert.Contains("NO DAMAGE", results[0].Line, StringComparison.Ordinal);
        Assert.Contains("location=\"HEAD\"", results[0].Line, StringComparison.Ordinal);
    }

    [Fact]
    public void AReloadRefillsTheMagazineTheServerIsTracking()
    {
        var session = new SessionCombat();
        var results = new List<WeaponArmResult>();

        Handle(session, ShootingPacketBuilder.Fire(Rifle, 0, 0, 0, [1]), results);
        Assert.Equal(29, session.Shooter.AmmoOf(Rifle));

        Handle(session, ShootingPacketBuilder.ReloadRequest(Rifle), results);
        Assert.Equal(30, session.Shooter.AmmoOf(Rifle));
        Assert.Contains("magazine refilled to 30", results[0].Line, StringComparison.Ordinal);
    }

    [Fact]
    public void ASubTheLoopDoesNotActOnKeepsThePreExistingTrace()
    {
        var session = new SessionCombat();
        var results = new List<WeaponArmResult>();

        // 82 20 WeaponFireHint - sent bare, and nothing in this lane answers it.
        Handle(session, ShootingPacketBuilder.Header(WeaponBaseDecoder.SubWeaponFireHint), results);

        Assert.Contains("sub=0x20", results[0].Line, StringComparison.Ordinal);
        Assert.Null(results[0].Marker);
    }

    [Fact]
    public void TheSwitchDescriptionNamesEveryArm()
    {
        string description = CombatOptions.Default.Describe();

        Assert.Contains("0x82=ON", description, StringComparison.Ordinal);
        Assert.Contains("layout=Z1Candidate", description, StringComparison.Ordinal);
        Assert.Contains("damage=ON", description, StringComparison.Ordinal);
        Assert.Contains("hitMarker=ON", description, StringComparison.Ordinal);
        Assert.Contains("melee 0xa0=ON", description, StringComparison.Ordinal);
        Assert.Contains("practiceTarget=off", description, StringComparison.Ordinal);

        // ...and the reverts still describe themselves.
        Assert.Contains(
            "practiceTarget=ON",
            (CombatOptions.Default with { PracticeTarget = true }).Describe(),
            StringComparison.Ordinal);
        Assert.Contains(
            "melee 0xa0=off",
            (CombatOptions.Default with { MeleeDamage = false }).Describe(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAnExactOneOrZeroMovesASwitch()
    {
        var environment = new Dictionary<string, string?>
        {
            [CombatOptions.EnabledVariable] = "true",           // a typo leaves the default
            [CombatOptions.DamageVariable] = "0",
            [CombatOptions.PracticeTargetVariable] = "1",
            [CombatOptions.LayoutVariable] = "0",
        };

        CombatOptions options = CombatOptions.FromEnvironment(
            name => environment.TryGetValue(name, out string? value) ? value : null);

        Assert.True(options.Enabled);
        Assert.False(options.EnableCombatDamage);
        Assert.True(options.PracticeTarget);
        Assert.Equal(WeaponFireLayout.Unknown, options.Layout);
        Assert.True(options.SendHitMarker);                     // untouched, so the default stands

        // Wave 13 (docs/107 s2): the 82 0c arm is ON by default and
        // CRANBERRY_WEAPON_FIREMODE_REPLY=0 is its revert.
        Assert.True(options.SwitchFireModeReply);
        Assert.False(CombatOptions.FromEnvironment(
            name => name == CombatOptions.SwitchFireModeReplyVariable ? "0" : null)
            .SwitchFireModeReply);
        Assert.Contains(
            "fireModeReply=ON", CombatOptions.Default.Describe(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- melee on the fire path (report 2)

    /// <summary>
    /// <b>The August client melees with the WEAPON fire path, not the abilities family (docs/89
    /// addendum).</b> A fists trigger-down (<c>82 01 state=17</c>) resolves a swing on the nearest
    /// practice dummy, exactly as the owner's 2026-09-03 19:48 session showed the fists sending only
    /// <c>82 01</c> and never an <c>82 03</c> or an <c>a0</c>.
    /// </summary>
    [Fact]
    public void AFistsTriggerDownSwingsAndHitsThePracticeDummy()
    {
        var session = new SessionCombat { MeleeHeading = 0f };
        PracticeTarget target = session.Targets.Spawn(Vector3.Zero, 0f, 1, 2f, 10_000)[0];
        var results = new List<WeaponArmResult>();

        // heldWeaponItemDefinitionId 0 = the fists (an empty active hand).
        WeaponFireArm.Handle(
            session,
            ShootingPacketBuilder.FireStateUpdate(0x3100_0000_0000_000C, fireState: 17),
            CombatOptions.Default,
            heldWeaponItemDefinitionId: 0,
            Vector3.Zero,
            0,
            results);

        WeaponArmResult swing = Assert.Single(results);
        Assert.Contains("melee swing", swing.Line, StringComparison.Ordinal);
        Assert.Same(target, swing.HitTarget);
        Assert.Equal(9_000, target.Health);   // 1000 base fist damage on the 10,000 bar
        Assert.NotNull(swing.Marker);
        Assert.Equal(1, session.MeleeSwings);
    }

    /// <summary>A gun's own <c>82 01 state=17</c> is NOT a melee swing - it is gated on a melee item,
    /// and the gun does its damage through the <c>82 03 Fire</c> that follows.</summary>
    [Fact]
    public void AGunsTriggerDownIsNotAMeleeSwing()
    {
        var session = new SessionCombat();
        PracticeTarget target = session.Targets.Spawn(Vector3.Zero, 0f, 1, 4f, 10_000)[0];
        var results = new List<WeaponArmResult>();

        WeaponFireArm.Handle(
            session,
            ShootingPacketBuilder.FireStateUpdate(Rifle, fireState: 17),
            CombatOptions.Default,
            heldWeaponItemDefinitionId: ArFifteen,
            Vector3.Zero,
            0,
            results);

        WeaponArmResult line = Assert.Single(results);
        Assert.DoesNotContain("melee swing", line.Line, StringComparison.Ordinal);
        Assert.Equal(10_000, target.Health);
        Assert.Equal(0, session.MeleeSwings);
    }

    /// <summary><c>CRANBERRY_MELEE_ON_TRIGGER=0</c> is a real revert: the fists' trigger-down no
    /// longer swings.</summary>
    [Fact]
    public void TurningMeleeOnTriggerOffStopsTheFistSwing()
    {
        var session = new SessionCombat();
        PracticeTarget target = session.Targets.Spawn(Vector3.Zero, 0f, 1, 4f, 10_000)[0];
        var results = new List<WeaponArmResult>();

        WeaponFireArm.Handle(
            session,
            ShootingPacketBuilder.FireStateUpdate(0x3100_0000_0000_000C, fireState: 17),
            CombatOptions.Default with { MeleeOnTrigger = false },
            heldWeaponItemDefinitionId: 0,
            Vector3.Zero,
            0,
            results);

        WeaponArmResult line = Assert.Single(results);
        Assert.DoesNotContain("melee swing", line.Line, StringComparison.Ordinal);
        Assert.Equal(10_000, target.Health);
        Assert.Contains("meleeOnTrigger=ON", CombatOptions.Default.Describe(), StringComparison.Ordinal);
    }

    private static void Handle(
        SessionCombat session,
        byte[] packet,
        List<WeaponArmResult> results,
        long nowMs = 0,
        CombatOptions? options = null) =>
        WeaponFireArm.Handle(
            session,
            packet,
            options ?? CombatOptions.Default,
            ArFifteen,
            Vector3.Zero,
            nowMs,
            results);
}
