using System.Numerics;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Combat;

/// <summary>
/// docs/121 - the server-side half of the shooting audit: the magazine resync is paid for out of
/// the bag (D333), a reload's replies are owed at the weapon's own <c>RELOAD_TIME_MS</c> (D334),
/// and a shot tells the bystanders' relay to start on the corroborated hint and stop on the
/// trigger-up (D335). Every packet is the client's own captured bytes or the builder the wire
/// tests already use.
/// </summary>
public sealed class ShootingRetailAuditTests
{
    private const ulong CapturedAk = 0x3100_0000_0000_000Cul;
    private const uint AkItemId = 2229;
    private const uint SevenSixTwo = 2325;
    private const uint PumpItemId = 1374;
    private const uint BuckshotShell = 1511;
    private const uint Fists = 85;

    /// <summary>17:51:37.390 - <c>82 1f</c> carrying <c>82 01 FireStateUpdate</c> state 17.</summary>
    private const string CapturedFireState =
        "82000000001F01000000100000008292DEF216010C000000000000311100";

    /// <summary>17:51:37.395 - the bare <c>82 20 WeaponFireHint</c> for projectile 1.</summary>
    private const string CapturedHintOne =
        "8292DEF216200C00000000000031FF8C1BCF433C16A141F9CDE2C4"
        + "0100000001000000F616623F068F703E34DFCF3E00000000";

    // ---------------------------------------------------------------- D333 the resync pays

    [Fact]
    public void TheMagazineResyncTakesTheClipOutOfTheBag()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = Session(AkItemId, SevenSixTwo, 60);
        var results = new List<WeaponArmResult>();

        Handle(session, ammo, Convert.FromHexString(CapturedFireState), results, AkItemId);

        WeaponArmResult state = results.Last(r => r.Line.Contains("82 01", StringComparison.Ordinal));
        Assert.Contains("MAGAZINE RESYNC", state.Line, StringComparison.Ordinal);
        Assert.Contains("PAID FROM THE BAG (30 of item 2325 taken, 30 left", state.Line, StringComparison.Ordinal);
        Assert.Equal(30, session.Shooter.AmmoOf(CapturedAk));
        Assert.Equal(30, ammo.Count(SevenSixTwo));

        // The stack that shrank is announced - 11 03 ItemUpdate, exactly as a reload announces it.
        byte[] update = Assert.Single(state.Replies!);
        Assert.Equal(0x11, update[0]);
        Assert.Equal(3, BitConverter.ToUInt16(update, 1));
    }

    [Fact]
    public void AShortBagPaysWhatItHasAndTheMagazineIsPartial()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = Session(AkItemId, SevenSixTwo, 10);
        var results = new List<WeaponArmResult>();

        Handle(session, ammo, Convert.FromHexString(CapturedFireState), results, AkItemId);

        Assert.Equal(10, session.Shooter.AmmoOf(CapturedAk));
        Assert.Equal(0, ammo.Count(SevenSixTwo));
        Assert.Contains("is now 10/30", results.Last().Line, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNothingInTheBagTheResyncIsWithheldAndTheShotIsRefused()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = Session(AkItemId, SevenSixTwo, 0);
        var results = new List<WeaponArmResult>();

        Handle(session, ammo, Convert.FromHexString(CapturedFireState), results, AkItemId);
        Assert.Contains("MAGAZINE RESYNC WITHHELD", results.Last().Line, StringComparison.Ordinal);
        Assert.Contains("hint 15204", results.Last().Line, StringComparison.Ordinal);
        Assert.Equal(0, session.Shooter.AmmoOf(CapturedAk));
        Assert.Null(results.Last().Replies);

        Handle(session, ammo, ShootingPacketBuilder.Fire(CapturedAk, 0, 0, 0, [1]), results, AkItemId);
        Assert.Contains("Fire REFUSED", results.Last().Line, StringComparison.Ordinal);
        Assert.Contains("82 1e FireRejected", results.Last().Line, StringComparison.Ordinal);
    }

    [Fact]
    public void TurningTheBagPaymentOffHandsTheClipOutFreeAsD223Did()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = Session(AkItemId, SevenSixTwo, 60);
        var results = new List<WeaponArmResult>();
        CombatOptions free = CombatOptions.Default with { MagazineResyncFromBag = false };

        Handle(session, ammo, Convert.FromHexString(CapturedFireState), results, AkItemId, options: free);

        Assert.Contains("is now 30/30", results.Last().Line, StringComparison.Ordinal);
        Assert.DoesNotContain("PAID", results.Last().Line, StringComparison.Ordinal);
        Assert.Equal(30, session.Shooter.AmmoOf(CapturedAk));
        Assert.Equal(60, ammo.Count(SevenSixTwo));
    }

    [Fact]
    public void TheResyncHappensOncePerInstanceEvenWithAFullBag()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = Session(AkItemId, SevenSixTwo, 90);
        var results = new List<WeaponArmResult>();

        Handle(session, ammo, Convert.FromHexString(CapturedFireState), results, AkItemId);
        Handle(session, ammo, ShootingPacketBuilder.Fire(CapturedAk, 0, 0, 0, [1]), results, AkItemId, nowMs: 500);
        Handle(session, ammo, Convert.FromHexString(CapturedFireState), results, AkItemId, nowMs: 1000);

        Assert.Equal(29, session.Shooter.AmmoOf(CapturedAk));
        Assert.Equal(60, ammo.Count(SevenSixTwo));
        Assert.DoesNotContain("MAGAZINE RESYNC", results.Last().Line, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- D334 the timed reload

    [Fact]
    public void AMagazineReloadIsOwedAtTheWeaponsOwnReloadTime()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = Session(AkItemId, SevenSixTwo, 60);
        var results = new List<WeaponArmResult>();

        Handle(session, ammo, ShootingPacketBuilder.ReloadRequest(CapturedAk), results, AkItemId);

        WeaponArmResult reload = Assert.Single(results);
        PendingWeaponReload pending = Assert.IsType<PendingWeaponReload>(reload.ReloadWork);
        Assert.Equal(3900, pending.DueAtMs);
        Assert.Null(reload.Replies);
        Assert.Equal(3900, RetailBalance.ReloadTimeMs(AkItemId));
        Assert.Equal(0, session.Shooter.AmmoOf(CapturedAk));
        Assert.Equal(60, ammo.Count(SevenSixTwo));
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, 3899, CapturedAk, ammo.Inventory));
        WeaponArmResult completed = WeaponFireArm.AdvanceReload(
            session, pending, 3900, CapturedAk, ammo.Inventory)!.Value;
        Assert.Contains("loaded 30 round(s)", completed.Line, StringComparison.Ordinal);
        Assert.NotNull(completed.Replies);
        Assert.Equal(30, session.Shooter.AmmoOf(CapturedAk));
        Assert.Equal(30, ammo.Count(SevenSixTwo));
        Assert.Null(session.Reload);
    }

    [Fact]
    public void ThePumpsShellsLandOneReloadTimeApart()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = Session(PumpItemId, BuckshotShell, 6);
        var results = new List<WeaponArmResult>();

        Handle(session, ammo, ShootingPacketBuilder.ReloadRequest(CapturedAk), results, PumpItemId);

        WeaponArmResult reload = Assert.Single(results);
        PendingWeaponReload pending = Assert.IsType<PendingWeaponReload>(reload.ReloadWork);
        Assert.Equal(0, session.Shooter.AmmoOf(CapturedAk));
        for (int i = 1; i <= 6; i++)
        {
            Assert.Null(WeaponFireArm.AdvanceReload(session, pending, i * 800 - 1, CapturedAk, ammo.Inventory));
            WeaponArmResult completed = WeaponFireArm.AdvanceReload(
                session, pending, i * 800, CapturedAk, ammo.Inventory)!.Value;
            Assert.NotNull(completed.Replies);
            Assert.Equal(i, session.Shooter.AmmoOf(CapturedAk));
            Assert.Equal(6 - i, ammo.Count(BuckshotShell));
        }
        Assert.Null(session.Reload);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CapturedAkReloadCompletesAtTheClientTables2800Ms(int mode)
    {
        var (session, ammo) = Session(AkItemId, SevenSixTwo, 60);
        session.Shooter.EnsureDeclared(CapturedAk, AkItemId, 0);
        session.Shooter.SelectFireMode(CapturedAk, 0, (byte)mode);
        var results = new List<WeaponArmResult>();
        Handle(session, ammo, ShootingPacketBuilder.ReloadRequest(CapturedAk), results, AkItemId,
            options: CombatOptions.Default with { ShippedReloadTime = true });
        var pending = Assert.IsType<PendingWeaponReload>(Assert.Single(results).ReloadWork);
        Assert.Equal(2800, pending.DueAtMs);
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, 2799, CapturedAk, ammo.Inventory));
        Assert.Equal(0, session.Shooter.AmmoOf(CapturedAk));
        Assert.NotNull(WeaponFireArm.AdvanceReload(session, pending, 2800, CapturedAk, ammo.Inventory));
        Assert.Equal(30, session.Shooter.AmmoOf(CapturedAk));
        Assert.Equal(30, ammo.Count(SevenSixTwo));
    }

    [Fact]
    public void ImmediateReloadHasNoStaleRetryWindowAfterUnload()
    {
        var (session, ammo) = Session(AkItemId, SevenSixTwo, 60);
        var results = new List<WeaponArmResult>();
        var options = CombatOptions.Default with { TimedReload = false };
        Handle(session, ammo, ShootingPacketBuilder.ReloadRequest(CapturedAk), results, AkItemId,
            options: options);
        Assert.Equal(30, session.Shooter.Unload(CapturedAk));
        Handle(session, ammo, ShootingPacketBuilder.ReloadRequest(CapturedAk), results, AkItemId,
            nowMs: 1, options: options);
        Assert.Equal(30, session.Shooter.AmmoOf(CapturedAk));
        Assert.Equal(0, ammo.Count(SevenSixTwo));
        Assert.NotEmpty(Assert.Single(results).Replies!);
    }

    [Fact]
    public void TurningTheTimerOffAnswersInsideTheRequest()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = Session(AkItemId, SevenSixTwo, 60);
        var results = new List<WeaponArmResult>();

        Handle(
            session, ammo, ShootingPacketBuilder.ReloadRequest(CapturedAk), results, AkItemId,
            options: CombatOptions.Default with { TimedReload = false });

        Assert.Null(results.Single().ReplyDelaysMs);
        Assert.DoesNotContain("RELOAD_TIME_MS", results.Single().Line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScheduleGroupsSameDelayRepliesInOrderAndTreatsMissingDelaysAsImmediate()
    {
        byte[] a = [1], b = [2], c = [3], d = [4];

        IReadOnlyList<(int DelayMs, byte[][] Packets)> groups =
            WeaponReplySchedule.Group([a, b, c, d], [800, 800, 1600, 1600]);
        Assert.Equal(2, groups.Count);
        Assert.Equal(800, groups[0].DelayMs);
        Assert.Equal([a, b], groups[0].Packets);
        Assert.Equal(1600, groups[1].DelayMs);
        Assert.Equal([c, d], groups[1].Packets);

        IReadOnlyList<(int DelayMs, byte[][] Packets)> immediate = WeaponReplySchedule.Group([a, b], null);
        Assert.Single(immediate);
        Assert.Equal(0, immediate[0].DelayMs);

        IReadOnlyList<(int DelayMs, byte[][] Packets)> ragged = WeaponReplySchedule.Group([a, b, c], [500]);
        Assert.Equal(2, ragged.Count);
        Assert.Equal(500, ragged[0].DelayMs);
        Assert.Equal(0, ragged[1].DelayMs);
        Assert.Equal([b, c], ragged[1].Packets);
    }

    // ---------------------------------------------------------------- D335 the bystanders' relay

    [Fact]
    public void ACorroboratedHintStartsTheRelayAtTheShotsOwnAim()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = Session(AkItemId, SevenSixTwo, 60);
        var results = new List<WeaponArmResult>();

        Handle(session, ammo, Convert.FromHexString(CapturedFireState), results, AkItemId);
        Assert.Equal(PeerFireRelay.None, results.Last().Relay);

        Handle(
            session, ammo,
            ShootingPacketBuilder.Fire(CapturedAk, 414.21521f, 20.135857f, -1814.4366f, [1]),
            results, AkItemId);
        Assert.Equal(PeerFireRelay.None, results.Last().Relay);

        Handle(session, ammo, Convert.FromHexString(CapturedHintOne), results, AkItemId);
        WeaponArmResult hint = results.Single();
        Assert.Contains("corroborated", hint.Line, StringComparison.Ordinal);
        Assert.Equal(PeerFireRelay.Start, hint.Relay);

        // origin (414.2152, 20.1359, -1814.4366) + direction (0.8831, 0.2349, 0.4060) x 100.
        Assert.Equal(414.2152f + (0.8831f * 100f), hint.RelayAimPoint.X, 1);
        Assert.Equal(20.1359f + (0.2349f * 100f), hint.RelayAimPoint.Y, 1);
        Assert.Equal(-1814.4366f + (0.4060f * 100f), hint.RelayAimPoint.Z, 1);

        Handle(session, ammo, ShootingPacketBuilder.FireStateUpdate(CapturedAk, 0), results, AkItemId);
        Assert.Equal(PeerFireRelay.Stop, results.Single().Relay);
    }

    /// <summary>
    /// <b>D315 (docs/125 §6) - <c>82 15 04 0b ProjectileLaunch</c> rides the ACCEPTED trigger.</b>
    /// docs/121 row 52 had this packet as "never sent"; the owner's server sends it on every shot
    /// and every throw. The arm's job is to say WHICH packets earned one, and it is exactly the
    /// accepted <c>82 03 Fire</c>s: not the hint, not the trigger-up, and above all not a refusal -
    /// a dry click relays nothing.
    /// </summary>
    [Fact]
    public void EveryAcceptedFireOwesItsViewersAProjectileLaunch()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = Session(AkItemId, SevenSixTwo, 2);
        var results = new List<WeaponArmResult>();

        Handle(session, ammo, Convert.FromHexString(CapturedFireState), results, AkItemId);
        Assert.DoesNotContain(results, r => r.LaunchRelay);

        Handle(
            session, ammo,
            ShootingPacketBuilder.Fire(CapturedAk, 414.21521f, 20.135857f, -1814.4366f, [1]),
            results, AkItemId);
        WeaponArmResult shot = results.Last();
        Assert.Contains("FIRED", shot.Line, StringComparison.Ordinal);
        Assert.True(shot.LaunchRelay);

        Handle(session, ammo, Convert.FromHexString(CapturedHintOne), results, AkItemId);
        Assert.False(results.Last().LaunchRelay);

        Handle(session, ammo, ShootingPacketBuilder.FireStateUpdate(CapturedAk, 0), results, AkItemId);
        Assert.False(results.Last().LaunchRelay);
    }

    /// <summary>
    /// The other half of D315: a REFUSED trigger relays nothing. The magazine was set to one round,
    /// so the second pull is an empty-magazine refusal and must carry no launch - otherwise a
    /// bystander sees a shot the shooter was told he did not take.
    /// </summary>
    [Fact]
    public void ARefusedFireRelaysNoProjectileLaunch()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = Session(AkItemId, SevenSixTwo, 0);
        var results = new List<WeaponArmResult>();

        Handle(
            session, ammo,
            ShootingPacketBuilder.Fire(CapturedAk, 1, 2, 3, [1]),
            results, AkItemId);
        WeaponArmResult first = results.Last();

        // Under GunsSpawnEmpty the very first pull declares an empty magazine and is refused.
        if (first.Line.Contains("REFUSED", StringComparison.Ordinal))
        {
            Assert.False(first.LaunchRelay);
            return;
        }

        Assert.True(first.LaunchRelay);
        Handle(
            session, ammo,
            ShootingPacketBuilder.Fire(CapturedAk, 1, 2, 3, [2]),
            results, AkItemId, nowMs: 20_000);
        WeaponArmResult refused = results.Last();
        Assert.Contains("REFUSED", refused.Line, StringComparison.Ordinal);
        Assert.False(refused.LaunchRelay);
    }

    [Fact]
    public void AnUncorroboratedHintAndAnEmptyHandRelayNothing()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = Session(AkItemId, SevenSixTwo, 60);
        var results = new List<WeaponArmResult>();

        // No 82 03 accepted for projectile 1 - the hint names nothing this server fired.
        Handle(session, ammo, Convert.FromHexString(CapturedHintOne), results, AkItemId);
        Assert.Contains("refused or never arrived", results.Single().Line, StringComparison.Ordinal);
        Assert.Equal(PeerFireRelay.None, results.Single().Relay);

        // The fists' trigger-up is not a gun's: nothing to stop.
        Handle(session, ammo, ShootingPacketBuilder.FireStateUpdate(CapturedAk, 0), results, Fists);
        Assert.Equal(PeerFireRelay.None, results.Single().Relay);
    }

    // ---------------------------------------------------------------- switches and banners

    [Fact]
    public void TheTwoCombatSwitchesReadTheEnvironmentAndTheBannerNamesThem()
    {
        Assert.True(CombatOptions.Default.MagazineResyncFromBag);
        Assert.True(CombatOptions.Default.TimedReload);

        CombatOptions reverted = CombatOptions.FromEnvironment(name => name switch
        {
            CombatOptions.MagazineResyncFromBagVariable => "0",
            CombatOptions.TimedReloadVariable => "0",
            _ => null,
        });
        Assert.False(reverted.MagazineResyncFromBag);
        Assert.False(reverted.TimedReload);

        Assert.Contains("magazineResync=ON(fromBag=ON)", CombatOptions.Default.Describe(), StringComparison.Ordinal);
        Assert.Contains("timedReload=ON", CombatOptions.Default.Describe(), StringComparison.Ordinal);
        Assert.Contains("timedReload=off", reverted.Describe(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- scaffolding

    private static (SessionCombat Session, PlayerAmmoContext Ammo) Session(
        uint gunItemId, uint ammoItemId, int rounds)
    {
        ulong next = 0x9200_0000_0000_0001;
        var inventory = new PlayerInventory(0x1001, () => next++);
        inventory.Bootstrap();

        next = CapturedAk;
        InventoryItemInstance gun = inventory.CreateInstance(gunItemId, 1);
        inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        next = 0x9300_0000_0000_0001;

        if (rounds > 0)
        {
            inventory.TryStow(inventory.CreateInstance(ammoItemId, (uint)rounds));
        }

        return (new SessionCombat(), new PlayerAmmoContext(inventory, 0x1001, AmmoOptions.Default));
    }

    private static void Handle(
        SessionCombat session,
        PlayerAmmoContext ammo,
        byte[] packet,
        List<WeaponArmResult> results,
        uint heldItemId,
        long nowMs = 0,
        CombatOptions? options = null) =>
        WeaponFireArm.Handle(
            session,
            packet,
            options ?? CombatOptions.Default,
            heldItemId,
            CapturedAk,
            Vector3.Zero,
            nowMs,
            results,
            ammo);
}
