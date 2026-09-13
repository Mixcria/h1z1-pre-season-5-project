using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Combat;

public sealed class ReloadActionOrderingTests
{
    [Theory]
    [InlineData(2425u, 30)]
    [InlineData(1374u, 1)]
    public void FirstFireAtDueCommitsBeforeArbitrationAndReportsPostShotAmmo(uint itemId, int committed)
    {
        var (session, ammo, gun, pending) = Start(itemId, 0, 40);
        WeaponArmResult result = Send(session, ammo, gun, itemId,
            ShootingPacketBuilder.Fire(gun, 0, 0, 0, [1]), pending.DueAtMs);
        Assert.True(result.LaunchRelay);
        Assert.Equal(committed - 1, session.Shooter.AmmoOf(gun));
        Assert.Equal(40 - committed, ammo.Count(AmmoTypes.AmmoItemFor(itemId)));
        Assert.Null(session.Reload);
        Assert.Null(result.ReloadWork);
        Assert.All(ReloadPackets(result), p => Assert.Equal((uint)(committed - 1), BitConverter.ToUInt32(p, 18)));
        Assert.NotEmpty(ReloadPackets(result));
        Assert.Contains(AllPackets(result), p => p[0] == 0x11); // reserve/durability updates retained
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, pending.DueAtMs + 5000, gun, ammo.Inventory));
        Assert.Equal(committed - 1, session.Shooter.AmmoOf(gun));
    }

    [Theory]
    [InlineData(2425u)]
    [InlineData(1374u)]
    public void FireBeforeDueCannotBorrowThePendingAmmunition(uint itemId)
    {
        var (session, ammo, gun, pending) = Start(itemId, 0, 40);
        var result = Send(session, ammo, gun, itemId,
            ShootingPacketBuilder.Fire(gun, 0, 0, 0, [1]), pending.DueAtMs - 1);
        Assert.False(result.LaunchRelay);
        Assert.Equal(0, session.Shooter.AmmoOf(gun));
        Assert.Equal(40, ammo.Count(AmmoTypes.AmmoItemFor(itemId)));
        Assert.Same(pending, session.Reload);
    }

    [Theory]
    [InlineData(2425u, 30)]
    [InlineData(1374u, 1)]
    public void NativeCompletionNoticeAtDueCannotCancelTheCompletedLoad(uint itemId, int committed)
    {
        var (session, ammo, gun, pending) = Start(itemId, 0, 40);
        var result = Send(session, ammo, gun, itemId, Interrupt(gun), pending.DueAtMs);
        Assert.Equal(committed, session.Shooter.AmmoOf(gun));
        Assert.Equal(40 - committed, ammo.Count(AmmoTypes.AmmoItemFor(itemId)));
        Assert.Null(session.Reload);
        Assert.Null(result.ReloadWork);
        var packets = ReloadPackets(result).ToArray();
        Assert.NotEmpty(packets);
        Assert.All(packets, p => Assert.Equal((uint)committed, BitConverter.ToUInt32(p, 18)));
        var counters = packets.Select(p => BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(26))).ToArray();
        Assert.Equal(counters.Order(), counters); // completed shell before terminal cancellation
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, pending.DueAtMs + 5000, gun, ammo.Inventory));
    }

    [Fact]
    public void EarlyInterruptAndItsStaleCallbackCannotLoadAReplacementReload()
    {
        var (session, ammo, gun, pending) = Start(2425, 0, 40);
        Send(session, ammo, gun, 2425, Interrupt(gun), pending.DueAtMs - 1);
        Assert.Equal(0, session.Shooter.AmmoOf(gun));
        Assert.Equal(40, ammo.Count(AmmoTypes.AmmoItemFor(2425)));
        Assert.Null(session.Reload);
        var replacement = Send(session, ammo, gun, 2425, ShootingPacketBuilder.ReloadRequest(gun), pending.DueAtMs).ReloadWork!;
        Assert.NotSame(pending, replacement);
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, replacement.DueAtMs, gun, ammo.Inventory));
        Assert.Equal(0, session.Shooter.AmmoOf(gun));
        Assert.Same(replacement, session.Reload);
    }

    [Fact]
    public void ShootingExistingRoundsBeforeDueCancelsWithoutFillingTheMagazine()
    {
        var (session, ammo, gun, pending) = Start(2425, 5, 40);
        var result = Send(session, ammo, gun, 2425,
            ShootingPacketBuilder.Fire(gun, 0, 0, 0, [1]), pending.DueAtMs - 1);
        Assert.True(result.LaunchRelay);
        Assert.Equal(4, session.Shooter.AmmoOf(gun));
        Assert.Equal(40, ammo.Count(AmmoTypes.AmmoItemFor(2425)));
        Assert.Null(session.Reload);
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, pending.DueAtMs, gun, ammo.Inventory));
    }

    [Fact]
    public void DueActionUsesTheActualRemainingReserve()
    {
        var (session, ammo, gun, pending) = Start(2425, 0, 40);
        Assert.Equal(38, ammo.Take(AmmoTypes.AmmoItemFor(2425), 38, []));
        var result = Send(session, ammo, gun, 2425,
            ShootingPacketBuilder.Fire(gun, 0, 0, 0, [1]), pending.DueAtMs);
        Assert.True(result.LaunchRelay);
        Assert.Equal(1, session.Shooter.AmmoOf(gun));
        Assert.Equal(0, ammo.Count(AmmoTypes.AmmoItemFor(2425)));
        Assert.All(ReloadPackets(result), p => Assert.Equal(1u, BitConverter.ToUInt32(p, 18)));
    }

    [Fact]
    public void ExhaustedReserveAtDueDoesNotGrantAFreeShot()
    {
        var (session, ammo, gun, pending) = Start(2425, 0, 40);
        ammo.Take(AmmoTypes.AmmoItemFor(2425), 40, []);
        var result = Send(session, ammo, gun, 2425,
            ShootingPacketBuilder.Fire(gun, 0, 0, 0, [1]), pending.DueAtMs);
        Assert.False(result.LaunchRelay);
        Assert.Equal(0, session.Shooter.AmmoOf(gun));
        Assert.Null(session.Reload);
        Assert.Contains(AllPackets(result), p => p[0] == 0x82 && p[5] == WeaponReplyPackets.SubReloadRejected);
    }

    [Fact]
    public void MalformedOrOtherWeaponActionsCannotCommitThisReload()
    {
        var (session, ammo, gun, pending) = Start(2425, 0, 40);
        Send(session, ammo, gun, 2425, ShootingPacketBuilder.Header(3), pending.DueAtMs);
        Send(session, ammo, gun, 2425, ShootingPacketBuilder.Header(9), pending.DueAtMs);
        Send(session, ammo, gun, 2425, ShootingPacketBuilder.Fire(gun + 1, 0, 0, 0, [1]), pending.DueAtMs);
        Send(session, ammo, gun, 2425, Interrupt(gun + 1), pending.DueAtMs);
        Assert.Equal(0, session.Shooter.AmmoOf(gun));
        Assert.Equal(40, ammo.Count(AmmoTypes.AmmoItemFor(2425)));
        Assert.Same(pending, session.Reload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterFireInOneNativeBundleCannotBeOverwrittenByTheEarlierCompletion(bool firstMemberFires)
    {
        var (session, ammo, gun, pending) = Start(2425, 5, 40);
        byte[] first = firstMemberFires ? ShootingPacketBuilder.Fire(gun, 0, 0, 0, [1]) : Interrupt(gun);
        byte[] second = ShootingPacketBuilder.Fire(gun, 0, 0, 0, [2]);
        BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(1), 10000);
        BinaryPrimitives.WriteUInt32LittleEndian(second.AsSpan(1), 10000u +
            (uint)RetailBalance.RefireGateMs(2425, CombatOptions.Default.RefireFloorMs, 0, CombatOptions.Default.ShippedRefireGate));
        byte[] packet = ShootingPacketBuilder.MultiWeapon(first, second);
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(session, packet, CombatOptions.Default, 2425, gun,
            Vector3.Zero, pending.DueAtMs, results, ammo);
        Assert.Equal(firstMemberFires ? 2 : 1, results.Count(r => r.LaunchRelay));
        int expected = firstMemberFires ? 28 : 29;
        Assert.Equal(expected, session.Shooter.AmmoOf(gun));
        var snapshots = results.SelectMany(ReloadPackets).ToArray();
        Assert.NotEmpty(snapshots);
        Assert.All(snapshots, p => Assert.Equal((uint)expected, BitConverter.ToUInt32(p, 18)));
        Assert.Equal(15, ammo.Count(AmmoTypes.AmmoItemFor(2425)));
        Assert.Null(session.Reload);
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, pending.DueAtMs + 5000, gun, ammo.Inventory));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EarlyCancellationSnapshotCannotRestoreALaterBunchedShot(bool firstMemberFires, bool capturedOptions)
    {
        CombatOptions options = Options(capturedOptions);
        var (session, ammo, gun, pending) = Start(2425, 5, 40, options);
        byte[] first = firstMemberFires ? TimedFire(gun, 1, 10000) : Interrupt(gun);
        byte[] second = TimedFire(gun, 2, 10000u + (uint)RetailBalance.RefireGateMs(
            2425, options.RefireFloorMs, 0, options.ShippedRefireGate));
        var results = new List<WeaponArmResult>();

        WeaponFireArm.Handle(session, ShootingPacketBuilder.MultiWeapon(first, second, second),
            options, 2425, gun, Vector3.Zero, pending.DueAtMs - 1, results, ammo);

        int fired = firstMemberFires ? 2 : 1;
        Assert.Equal(fired, results.Count(r => r.LaunchRelay)); // replayed projectile spends nothing
        Assert.Equal(5 - fired, session.Shooter.AmmoOf(gun));
        Assert.Equal(40, ammo.Count(AmmoTypes.AmmoItemFor(2425)));
        byte[] snapshot = Assert.Single(results.SelectMany(ReloadPackets));
        Assert.Equal((uint)(5 - fired), BitConverter.ToUInt32(snapshot, 18));
        Assert.Equal(session.Shooter.ReloadCountOf(gun), BitConverter.ToUInt64(snapshot, 26));
        Assert.Null(session.Reload);
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, pending.DueAtMs, gun, ammo.Inventory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacementReloadWithinABundleKeepsCountersAndFinalAmmoThroughEarlyFire(bool capturedOptions)
    {
        CombatOptions options = Options(capturedOptions);
        var (session, ammo, gun, old) = Start(2425, 5, 40, options);
        uint gate = (uint)RetailBalance.RefireGateMs(2425, options.RefireFloorMs, 0, options.ShippedRefireGate);
        byte[] bundle = ShootingPacketBuilder.MultiWeapon(Interrupt(gun),
            ShootingPacketBuilder.ReloadRequest(gun), TimedFire(gun, 1, 10000), TimedFire(gun, 2, 10000 + gate));
        var results = new List<WeaponArmResult>();

        WeaponFireArm.Handle(session, bundle, options, 2425, gun, Vector3.Zero, old.DueAtMs - 1, results, ammo);

        Assert.Equal(2, results.Count(r => r.LaunchRelay));
        Assert.Equal(3, session.Shooter.AmmoOf(gun));
        Assert.Equal(40, ammo.Count(AmmoTypes.AmmoItemFor(2425)));
        byte[][] snapshots = results.SelectMany(ReloadPackets).ToArray();
        Assert.Equal(3, snapshots.Length); // old cancel, replacement acknowledgement, replacement cancel
        Assert.All(snapshots, p => Assert.Equal(3u, BitConverter.ToUInt32(p, 18)));
        Assert.Equal(new ulong[] { 2, 3, 4 }, snapshots.Select(p => BitConverter.ToUInt64(p, 26)));
        PendingWeaponReload replacement = Assert.Single(results, r => r.ReloadWork is not null).ReloadWork!;
        Assert.NotSame(old, replacement);
        Assert.Null(session.Reload);
        Assert.Null(WeaponFireArm.AdvanceReload(session, old, replacement.DueAtMs, gun, ammo.Inventory));
        Assert.Null(WeaponFireArm.AdvanceReload(session, replacement, replacement.DueAtMs, gun, ammo.Inventory));
    }

    [Fact]
    public void BundleRepairLeavesPumpPredictionAcknowledgementIntact()
    {
        var (session, ammo, gun, pending) = Start(1374, 2, 6);
        byte[] bundle = ShootingPacketBuilder.MultiWeapon(ShootingPacketBuilder.ReloadRequest(gun),
            TimedFire(gun, 1, 10000));
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(session, bundle, CombatOptions.Default, 1374, gun,
            Vector3.Zero, pending.DueAtMs - 1, results, ammo);

        Assert.Equal(1, session.Shooter.AmmoOf(gun));
        byte[][] snapshots = results.SelectMany(ReloadPackets).ToArray();
        Assert.Equal(2, snapshots.Length);
        Assert.Equal(1u, BitConverter.ToUInt32(snapshots[0], 14));
        Assert.Equal(2u, BitConverter.ToUInt32(snapshots[0], 18)); // initial prediction inputs, not a snapshot
        Assert.Equal(6u, BitConverter.ToUInt32(snapshots[0], 22));
        Assert.Equal(0u, BitConverter.ToUInt32(snapshots[1], 14));
        Assert.Equal(1u, BitConverter.ToUInt32(snapshots[1], 18));
        Assert.Null(session.Reload);
        Assert.Equal(6, ammo.Count(1511));
    }

    private static CombatOptions Options(bool captured) => captured
        ? CombatOptions.Default with { MagazineResync = false, ShippedRefireGate = true,
            ShippedReloadTime = true, RefireJitterMs = 16 }
        : CombatOptions.Default;

    private static byte[] TimedFire(ulong gun, uint projectile, uint gameTime) =>
        ShootingPacketBuilder.Fire(gun, 0, 0, 0, [projectile], gameTime);

    private static (SessionCombat, PlayerAmmoContext, ulong, PendingWeaponReload) Start(uint itemId, int magazine, int reserve,
        CombatOptions? options = null)
    {
        ulong next = 100;
        var inventory = new PlayerInventory(1, () => ++next);
        inventory.Bootstrap();
        var gun = inventory.CreateInstance(itemId, 1);
        inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        Assert.True(inventory.TryStow(inventory.CreateInstance(AmmoTypes.AmmoItemFor(itemId), (uint)reserve)));
        var session = new SessionCombat();
        session.Shooter.DeclareWeapon(gun.Guid, itemId, magazine);
        var ammo = new PlayerAmmoContext(inventory, 1, AmmoOptions.Default);
        var start = Send(session, ammo, gun.Guid, itemId, ShootingPacketBuilder.ReloadRequest(gun.Guid), 100, options);
        return (session, ammo, gun.Guid, Assert.IsType<PendingWeaponReload>(start.ReloadWork));
    }

    private static byte[] Interrupt(ulong gun) => [.. ShootingPacketBuilder.Header(9), .. BitConverter.GetBytes(gun)];
    private static IEnumerable<byte[]> AllPackets(WeaponArmResult result) =>
        (result.Reply is { } first ? new[] { first } : []).Concat(result.Replies ?? []);
    private static IEnumerable<byte[]> ReloadPackets(WeaponArmResult result) => AllPackets(result)
        .Where(p => p.Length == 34 && p[0] == 0x82 && p[5] == WeaponReplyPackets.SubReload);
    private static WeaponArmResult Send(SessionCombat session, PlayerAmmoContext ammo, ulong gun,
        uint itemId, byte[] packet, long now, CombatOptions? options = null)
    {
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(session, packet, options ?? CombatOptions.Default, itemId, gun, Vector3.Zero, now, results, ammo);
        return Assert.Single(results);
    }
}
