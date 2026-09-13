using System.Numerics;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Combat;

public sealed class TimedReloadTests
{
    private const ulong Gun = 0x3100_0000_0000_000C;
    private const uint Pump = 1374;
    private const uint Shells = 1511;
    private readonly List<WeaponArmResult> _results = [];

    [Fact]
    public void InterruptBeforeTheFirstShellPreservesAllAmmunition()
    {
        var (session, ammo, pending) = Start();
        Send(session, ammo, Interrupt(Gun), 400);
        Assert.Null(session.Reload);
        Assert.Equal(0, session.Shooter.AmmoOf(Gun));
        Assert.Equal(6, ammo.Count(Shells));
        Assert.Equal(0u, BitConverter.ToUInt32(Assert.Single(_results).Reply!, 18));
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, 9000, Gun, ammo.Inventory));
        Assert.Equal(6, ammo.Count(Shells));
    }

    [Fact]
    public void InterruptAfterTwoShellsKeepsExactlyTwoShells()
    {
        var (session, ammo, pending) = Start();
        Step(session, ammo, pending, 800);
        Step(session, ammo, pending, 1600);
        Send(session, ammo, Interrupt(Gun), 1700);
        Assert.Equal(2, session.Shooter.AmmoOf(Gun));
        Assert.Equal(4, ammo.Count(Shells));
        byte[] reply = Assert.Single(_results).Reply!;
        Assert.Equal(2u, BitConverter.ToUInt32(reply, 18));
        Assert.Equal(4u, BitConverter.ToUInt32(reply, 22));
        Assert.Equal(4ul, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(reply.AsSpan(26)));
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, 2400, Gun, ammo.Inventory));
    }

    [Fact]
    public void AReloadRequestCannotLoadOrFireFutureShells()
    {
        var (session, ammo, pending) = Start();
        Send(session, ammo, ShootingPacketBuilder.Fire(Gun, 0, 0, 0, [1]), 400);
        Assert.Contains("magazine empty", Assert.Single(_results).Line, StringComparison.Ordinal);
        Assert.Equal(0, session.Shooter.ShotsFired);
        Assert.Equal(6, ammo.Count(Shells));
        Assert.Same(pending, session.Reload);
    }

    [Fact]
    public void FiringALoadedShellStopsTheRemainingLoop()
    {
        var (session, ammo, pending) = Start();
        Step(session, ammo, pending, 800);
        Send(session, ammo, ShootingPacketBuilder.Fire(Gun, 0, 0, 0, [1]), 900);
        Assert.True(Assert.Single(_results).LaunchRelay);
        Assert.Null(session.Reload);
        Assert.Equal(0, session.Shooter.AmmoOf(Gun));
        Assert.Equal(5, ammo.Count(Shells));
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, 1600, Gun, ammo.Inventory));
    }

    [Fact]
    public void DuplicateRequestDoesNotResetTheDeadlineOrCreateAnotherTimer()
    {
        var (session, ammo, pending) = Start();
        Send(session, ammo, ShootingPacketBuilder.ReloadRequest(Gun), 500);
        Assert.Null(Assert.Single(_results).ReloadWork);
        Assert.Same(pending, session.Reload);
        Assert.Equal(800, pending.DueAtMs);
        Step(session, ammo, pending, 800);
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, 800, Gun, ammo.Inventory));
        Assert.Equal(1, session.Shooter.AmmoOf(Gun));
    }

    [Fact]
    public void AnOldCallbackCannotCompleteANewReloadOfTheSameGun()
    {
        var (session, ammo, old) = Start();
        Send(session, ammo, Interrupt(Gun), 100);
        Send(session, ammo, ShootingPacketBuilder.ReloadRequest(Gun), 200);
        PendingWeaponReload current = session.Reload!;
        Assert.NotSame(old, current);
        Assert.Null(WeaponFireArm.AdvanceReload(session, old, 1000, Gun, ammo.Inventory));
        Assert.Equal(0, session.Shooter.AmmoOf(Gun));
        Step(session, ammo, current, 1000);
        Assert.Equal(1, session.Shooter.AmmoOf(Gun));
    }

    [Fact]
    public void RetryDoesNotRejectAnInProgressReloadWhenTheReserveHasChanged()
    {
        var (session, ammo, pending) = Start();
        ammo.Take(Shells, 6, []);
        Send(session, ammo, ShootingPacketBuilder.ReloadRequest(Gun), 400);
        var retry = Assert.Single(_results);
        Assert.Equal(WeaponReplyPackets.SubReload, Assert.IsType<byte[]>(retry.Reply)[5]);
        Assert.Equal(1ul, session.Shooter.ReloadCountOf(Gun)); // a retry only repeats the acknowledgement
        Assert.Null(retry.ReloadWork);
        Assert.Same(pending, session.Reload);
        Step(session, ammo, pending, 800);
        Assert.Null(session.Reload);
        Assert.Equal(0, session.Shooter.AmmoOf(Gun));
    }

    [Fact]
    public void ChangingWeaponCancelsWithoutSpendingReserve()
    {
        var (session, ammo, pending) = Start();
        Assert.NotNull(WeaponFireArm.AdvanceReload(session, pending, 800, Gun + 1, ammo.Inventory));
        Assert.Null(session.Reload);
        Assert.Equal(6, ammo.Count(Shells));
    }

    [Fact]
    public void DroppedWeaponsAndReplacedInventoriesReceiveNoLateReply()
    {
        var (session, ammo, pending) = Start();
        ammo.Inventory.RemoveUnits(Gun, 1);
        WeaponArmResult dropped = WeaponFireArm.AdvanceReload(session, pending, 800, Gun, ammo.Inventory)!.Value;
        Assert.Null(dropped.Reply);
        Assert.Null(session.Reload);
        Assert.Equal(6, ammo.Count(Shells));

        (session, ammo, pending) = Start();
        WeaponArmResult replaced = WeaponFireArm.AdvanceReload(session, pending, 800, Gun, null)!.Value;
        Assert.Null(replaced.Reply);
        Assert.Null(session.Reload);
        Assert.Equal(6, ammo.Count(Shells));
    }

    [Fact]
    public void ReserveIsReadWhenTheShellCompletes()
    {
        var (session, ammo, pending) = Start();
        ammo.Take(Shells, 5, []);
        Step(session, ammo, pending, 800);
        Assert.Equal(1, session.Shooter.AmmoOf(Gun));
        Assert.Equal(0, ammo.Count(Shells));
        Assert.Null(session.Reload);
    }

    [Fact]
    public void LateDispatchLoadsOneShellAndWaitsForTheNext()
    {
        var (session, ammo, pending) = Start();
        Step(session, ammo, pending, 5000);
        Assert.Equal(1, session.Shooter.AmmoOf(Gun));
        Assert.Equal(5800, pending.DueAtMs);
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, 5000, Gun, ammo.Inventory));
    }

    [Fact]
    public void MalformedAndUnrelatedInterruptsCannotCancelTheReload()
    {
        var (session, ammo, pending) = Start();
        Send(session, ammo, ShootingPacketBuilder.Header(0x09), 100);
        Assert.Same(pending, session.Reload);
        Assert.Equal(1, session.Undecodable);
        Send(session, ammo, Interrupt(Gun + 1), 100);
        Assert.Same(pending, session.Reload);
    }

    private (SessionCombat Session, PlayerAmmoContext Ammo, PendingWeaponReload Pending) Start()
    {
        ulong next = 0x9200_0000_0000_0001;
        var inventory = new PlayerInventory(0x1001, () => next++);
        inventory.Bootstrap();
        next = Gun;
        InventoryItemInstance gun = inventory.CreateInstance(Pump, 1);
        inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        next = 0x9300_0000_0000_0001;
        inventory.TryStow(inventory.CreateInstance(Shells, 6));
        var ammo = new PlayerAmmoContext(inventory, 0x1001, AmmoOptions.Default);
        var session = new SessionCombat();
        Send(session, ammo, ShootingPacketBuilder.ReloadRequest(Gun), 0);
        return (session, ammo, Assert.IsType<PendingWeaponReload>(Assert.Single(_results).ReloadWork));
    }

    private void Send(SessionCombat session, PlayerAmmoContext ammo, byte[] packet, long now) =>
        WeaponFireArm.Handle(session, packet, CombatOptions.Default, Pump, Gun, Vector3.Zero, now, _results, ammo);

    private static void Step(SessionCombat session, PlayerAmmoContext ammo, PendingWeaponReload pending, long now) =>
        Assert.NotNull(WeaponFireArm.AdvanceReload(session, pending, now, Gun, ammo.Inventory));

    private static byte[] Interrupt(ulong guid) => [.. ShootingPacketBuilder.Header(0x09), .. BitConverter.GetBytes(guid)];
}
