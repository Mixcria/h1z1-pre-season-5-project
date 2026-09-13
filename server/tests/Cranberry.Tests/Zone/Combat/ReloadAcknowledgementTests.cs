using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Combat;

public sealed class ReloadAcknowledgementTests
{
    [Theory]
    [InlineData(2425u, 5, 20, 0u)]
    [InlineData(1374u, 0, 6, 1u)]
    public void StartAcknowledgesCurrentCountsWithoutSpendingOrDelayingTheLoad(
        uint itemId, int magazine, int reserve, uint predictedDelta)
    {
        var (session, ammo, gun) = Create(itemId, magazine, reserve);
        WeaponArmResult start = Request(session, ammo, gun, itemId, 100);
        var pending = Assert.IsType<PendingWeaponReload>(start.ReloadWork);
        byte[] reply = Assert.IsType<byte[]>(start.Reply);
        AssertSnapshot(reply, gun, predictedDelta, magazine, reserve, 1);
        Assert.Null(start.Replies); // no inventory delta at acknowledgement
        Assert.Equal(magazine, session.Shooter.AmmoOf(gun));
        Assert.Equal(reserve, ammo.Count(AmmoTypes.AmmoItemFor(itemId)));

        long due = pending.DueAtMs;
        WeaponArmResult retry = Request(session, ammo, gun, itemId, 400);
        Assert.Equal(reply, retry.Reply);
        Assert.Null(retry.ReloadWork);
        Assert.Same(pending, session.Reload);
        Assert.Equal(due, pending.DueAtMs);
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, due - 1, gun, ammo.Inventory));

        WeaponArmResult completion = Assert.IsType<WeaponArmResult>(
            WeaponFireArm.AdvanceReload(session, pending, due, gun, ammo.Inventory));
        int loaded = pending.ShellByShell ? 1 : reserve;
        AssertSnapshot(Assert.Single(completion.Replies!, p => p[0] == 0x82), gun, 0,
            magazine + loaded, reserve - loaded, 2);
        Assert.Equal(magazine + loaded, session.Shooter.AmmoOf(gun));
        Assert.Equal(reserve - loaded, ammo.Count(AmmoTypes.AmmoItemFor(itemId)));
        if (pending.ShellByShell)
        {
            long secondDue = pending.DueAtMs;
            WeaponArmResult shellRetry = Request(session, ammo, gun, itemId, due + 20);
            AssertSnapshot(shellRetry.Reply!, gun, 1, 1, reserve - 1, 2);
            Assert.Null(shellRetry.ReloadWork);
            Assert.Same(pending, session.Reload);
            Assert.Equal(secondDue, pending.DueAtMs);
            var second = Assert.IsType<WeaponArmResult>(
                WeaponFireArm.AdvanceReload(session, pending, secondDue, gun, ammo.Inventory));
            AssertSnapshot(Assert.Single(second.Replies!, p => p[0] == 0x82), gun, 0, 2, reserve - 2, 3);
        }
    }

    [Theory]
    [InlineData(2425u)]
    [InlineData(1374u)]
    public void AcknowledgementAllowsTheActualReloadInterruptToEndWithoutFutureRounds(uint itemId)
    {
        var (session, ammo, gun) = Create(itemId, 0, 6);
        WeaponArmResult start = Request(session, ammo, gun, itemId, 100);
        var pending = Assert.IsType<PendingWeaponReload>(start.ReloadWork);
        var results = new List<WeaponArmResult>();
        byte[] interrupt = [.. ShootingPacketBuilder.Header(0x09), .. BitConverter.GetBytes(gun)];
        WeaponFireArm.Handle(session, interrupt, CombatOptions.Default, itemId, gun,
            Vector3.Zero, 200, results, ammo);
        AssertSnapshot(Assert.Single(results).Reply!, gun, 0, 0, 6, 2);
        Assert.Null(session.Reload);
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, pending.DueAtMs, gun, ammo.Inventory));
        Assert.Equal(0, session.Shooter.AmmoOf(gun));
        Assert.Equal(6, ammo.Count(AmmoTypes.AmmoItemFor(itemId)));
    }

    private static (SessionCombat Session, PlayerAmmoContext Ammo, ulong Gun) Create(
        uint itemId, int magazine, int reserve)
    {
        ulong next = 100;
        var inventory = new PlayerInventory(1, () => ++next);
        inventory.Bootstrap();
        InventoryItemInstance gun = inventory.CreateInstance(itemId, 1);
        inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        Assert.True(inventory.TryStow(inventory.CreateInstance(AmmoTypes.AmmoItemFor(itemId), (uint)reserve)));
        var session = new SessionCombat();
        session.Shooter.DeclareWeapon(gun.Guid, itemId, magazine);
        return (session, new PlayerAmmoContext(inventory, 1, AmmoOptions.Default), gun.Guid);
    }

    private static WeaponArmResult Request(SessionCombat session, PlayerAmmoContext ammo,
        ulong gun, uint itemId, long now)
    {
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(session, ShootingPacketBuilder.ReloadRequest(gun), CombatOptions.Default,
            itemId, gun, Vector3.Zero, now, results, ammo);
        return Assert.Single(results);
    }

    private static void AssertSnapshot(byte[] packet, ulong gun, uint delta, int magazine,
        int reserve, ulong counter)
    {
        Assert.Equal(0x82, packet[0]);
        Assert.Equal(WeaponReplyPackets.SubReload, packet[5]);
        Assert.Equal(gun, BitConverter.ToUInt64(packet, 6));
        Assert.Equal(delta, BitConverter.ToUInt32(packet, 14));
        Assert.Equal((uint)magazine, BitConverter.ToUInt32(packet, 18));
        Assert.Equal((uint)reserve, BitConverter.ToUInt32(packet, 22));
        Assert.Equal(counter, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(26)));
    }
}
