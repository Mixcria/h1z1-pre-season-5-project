using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Combat;

public sealed class ShotgunReloadClientTests
{
    [Theory]
    [InlineData(1ul)]
    [InlineData(256ul)]
    [InlineData(257ul)]
    public void CounterMatchesTheNativeExpectedValueAcrossAByteBoundary(ulong nextCounter)
    {
        var client = new AugustPumpReloadClient(0);
        client.Apply(WeaponReplyPackets.Reload(0, 100, 0, 0, 6, nextCounter - 1));
        client.BeginReload();
        client.Apply(WeaponReplyPackets.Reload(0, 100, 1, 0, 6, nextCounter));
        Assert.Equal(nextCounter, client.Acknowledged);
        Assert.Equal(6, client.CachedReserve);
        Assert.True(client.CompleteShell());
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(2, 8)]
    [InlineData(0, 3)]
    public void OneRequestKeepsTheNativePumpLoopGoingUntilFullOrOutOfReserve(int magazine, int reserve)
    {
        var (session, ammo, gun) = Create(magazine, reserve);
        var client = new AugustPumpReloadClient(magazine);
        client.BeginReload();
        var result = Send(session, ammo, gun, ShootingPacketBuilder.ReloadRequest(gun), 0);
        var pending = Assert.IsType<PendingWeaponReload>(result.ReloadWork);
        client.Apply(result.Reply!);
        Assert.Equal(1ul, client.Acknowledged);
        Assert.Equal(reserve, client.CachedReserve); // BE1 made this zero and stopped the first loop.
        Assert.Equal(magazine, session.Shooter.AmmoOf(gun));

        int loads = Math.Min(6 - magazine, reserve);
        for (int shell = 1; shell <= loads; shell++)
        {
            Assert.True(client.CompleteShell());
            var completion = Assert.IsType<WeaponArmResult>(WeaponFireArm.AdvanceReload(
                session, pending, pending.DueAtMs, gun, ammo.Inventory));
            foreach (byte[] reply in completion.Replies!.Where(p => p[0] == 0x82)) client.Apply(reply);
            Assert.Equal(magazine + shell, client.Magazine);
            Assert.Equal(magazine + shell, session.Shooter.AmmoOf(gun));
            Assert.Equal(reserve - shell, ammo.Count(1511));
            if (shell < loads) Assert.Same(pending, completion.ReloadWork);
        }

        Assert.False(client.CompleteShell());
        Assert.Null(session.Reload);
    }

    [Fact]
    public void ExplicitInterruptAndFiringAfterTwoPredictedShellsCannotLoadAFutureShell()
    {
        var (session, ammo, gun) = Create(0, 6);
        var client = new AugustPumpReloadClient(0);
        client.BeginReload();
        var start = Send(session, ammo, gun, ShootingPacketBuilder.ReloadRequest(gun), 0);
        var pending = start.ReloadWork!;
        client.Apply(start.Reply!);
        for (int shell = 0; shell < 2; shell++)
        {
            Assert.True(client.CompleteShell());
            var step = WeaponFireArm.AdvanceReload(session, pending, pending.DueAtMs, gun, ammo.Inventory)!.Value;
            client.Apply(Assert.Single(step.Replies!, p => p[0] == 0x82));
        }

        client.Interrupt(); // FUN_142291750 -> FUN_14148a920 emits the actual 82/09.
        var stopped = Send(session, ammo, gun,
            [.. ShootingPacketBuilder.Header(0x09), .. BitConverter.GetBytes(gun)], 1700);
        client.Apply(stopped.Reply!);
        Assert.Equal(2, client.Magazine);
        Assert.False(client.CompleteShell());
        var fired = Send(session, ammo, gun, ShootingPacketBuilder.Fire(gun, 0, 0, 0, [1]), 1750);
        Assert.True(fired.LaunchRelay);
        Assert.Null(session.Reload);
        Assert.Equal(1, session.Shooter.AmmoOf(gun));
        Assert.Equal(4, ammo.Count(1511));
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, 2400, gun, ammo.Inventory));
    }

    [Theory]
    [InlineData(1374u, 6)]
    [InlineData(2425u, 30)]
    public void ImmediateFallbackUsesAnAuthoritativeCountEvenWhenItsCounterMatches(uint itemId, int clip)
    {
        var session = new SessionCombat();
        session.Shooter.DeclareWeapon(100, itemId, 0);
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(session, ShootingPacketBuilder.ReloadRequest(100), CombatOptions.Default,
            itemId, 100, Vector3.Zero, 0, results);
        byte[] reply = Assert.Single(results).Reply!;
        Assert.Equal(0u, BitConverter.ToUInt32(reply, 14));
        Assert.Equal((uint)clip, BitConverter.ToUInt32(reply, 18));
        Assert.Equal(1ul, BinaryPrimitives.ReadUInt64LittleEndian(reply.AsSpan(26)));
    }

    [Fact]
    public void RemovingRemainingReserveStopsTheClientWithoutPredictingAnotherShell()
    {
        var (session, ammo, gun) = Create(0, 6);
        var client = new AugustPumpReloadClient(0);
        client.BeginReload();
        var start = Send(session, ammo, gun, ShootingPacketBuilder.ReloadRequest(gun), 0);
        var pending = start.ReloadWork!;
        client.Apply(start.Reply!);
        for (int shell = 0; shell < 2; shell++)
        {
            Assert.True(client.CompleteShell());
            var step = WeaponFireArm.AdvanceReload(session, pending, pending.DueAtMs, gun, ammo.Inventory)!.Value;
            client.Apply(Assert.Single(step.Replies!, p => p[0] == 0x82));
        }
        Assert.Equal(4, client.CachedReserve);
        Assert.Equal(4, ammo.Take(1511, 4, []));
        var stopped = WeaponFireArm.AdvanceReload(session, pending, pending.DueAtMs, gun, ammo.Inventory)!.Value;
        client.Apply(stopped.Reply!);
        // FUN_1414887e0 does not refresh this cache for authoritative zero-delta snapshots.
        Assert.Equal(4, client.CachedReserve);
        byte[] rejection = Assert.Single(stopped.Replies!);
        Assert.Equal(WeaponReplyPackets.SubReloadRejected, rejection[5]);
        client.Apply(rejection);
        Assert.False(client.CompleteShell());
        Assert.Equal(2, client.Magazine);
        Assert.Equal(2, session.Shooter.AmmoOf(gun));
        Assert.Equal(0, ammo.Count(1511));
        Assert.Null(session.Reload);
    }

    [Fact]
    public void SpendingTheLastAvailableShellAlsoStopsPredictionAgainstAnOldLargerReserve()
    {
        var (session, ammo, gun) = Create(0, 6);
        var client = new AugustPumpReloadClient(0);
        client.BeginReload();
        var start = Send(session, ammo, gun, ShootingPacketBuilder.ReloadRequest(gun), 0);
        var pending = start.ReloadWork!;
        client.Apply(start.Reply!);
        Assert.Equal(5, ammo.Take(1511, 5, []));
        Assert.True(client.CompleteShell());
        var completed = WeaponFireArm.AdvanceReload(session, pending, pending.DueAtMs, gun, ammo.Inventory)!.Value;
        foreach (byte[] reply in completed.Replies!.Where(p => p[0] == 0x82)) client.Apply(reply);
        Assert.Equal(1, client.Magazine);
        Assert.False(client.CompleteShell());
        Assert.Equal(1, session.Shooter.AmmoOf(gun));
        Assert.Equal(0, ammo.Count(1511));
        Assert.Null(session.Reload);
    }

    private static (SessionCombat, PlayerAmmoContext, ulong) Create(int magazine, int reserve)
    {
        ulong next = 100;
        var inventory = new PlayerInventory(1, () => ++next);
        inventory.Bootstrap();
        var gun = inventory.CreateInstance(1374, 1);
        inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        Assert.True(inventory.TryStow(inventory.CreateInstance(1511, (uint)reserve)));
        var session = new SessionCombat();
        session.Shooter.DeclareWeapon(gun.Guid, 1374, magazine);
        return (session, new PlayerAmmoContext(inventory, 1, AmmoOptions.Default), gun.Guid);
    }

    private static WeaponArmResult Send(SessionCombat session, PlayerAmmoContext ammo, ulong gun,
        byte[] packet, long now)
    {
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(session, packet, CombatOptions.Default, 1374, gun, Vector3.Zero, now, results, ammo);
        return Assert.Single(results);
    }
}

// An independent, deliberately small transcription of August FUN_1414887e0 (reply applier),
// FUN_141487990 (cached reserve), and FUN_142292de0/142292cb0/14148aac0 (shell loop).
// Reads wire bytes as the native client does; this is not a native animation/playtest harness.
internal sealed class AugustPumpReloadClient(int magazine)
{
    public int Magazine { get; private set; } = magazine;
    public int CachedReserve { get; private set; }
    public ulong Acknowledged { get; private set; }
    private ulong _expected;
    private bool _reloading;

    public void BeginReload() { _expected = Acknowledged + 1; _reloading = true; }
    public void Interrupt() => _reloading = false;

    public void Apply(byte[] reply)
    {
        if (reply[5] == WeaponReplyPackets.SubReloadRejected)
        {
            _reloading = false; // FUN_140dc0880 -> FUN_142291750 for active reload states.
            return;
        }
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(reply.AsSpan(26));
        if (_expected == Acknowledged && Acknowledged == count) return;
        if (count == _expected && _reloading && BitConverter.ToUInt32(reply, 14) > 0)
            CachedReserve = checked((int)BitConverter.ToUInt32(reply, 22));
        else
        {
            if (_expected <= count) Magazine = checked((int)BitConverter.ToUInt32(reply, 18));
            _expected = count;
        }
        Acknowledged = count;
    }

    public bool CompleteShell()
    {
        if (!_reloading || Magazine >= 6 || CachedReserve <= 0)
        {
            _reloading = false;
            return false;
        }
        Magazine++;
        CachedReserve--;
        return true;
    }
}
