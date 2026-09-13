using System.Numerics;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Combat;

public sealed class ReloadRefusalRecoveryTests
{
    [Fact]
    public void DelayedOldHandRequestGetsTerminalReplyWithoutChangingTheNewHandsReload()
    {
        var (session, ammo, gun) = Create();
        var started = Request(session, ammo, gun.Guid, gun.Guid, gun.DefinitionId, 100);
        var pending = Assert.IsType<PendingWeaponReload>(started.ReloadWork);
        ulong oldHand = gun.Guid + 100;

        var refused = Request(session, ammo, oldHand, gun.Guid, gun.DefinitionId, 200);

        Assert.Equal(WeaponReplyPackets.ReloadRejected(oldHand), refused.Reply);
        Assert.Same(pending, session.Reload);
        Assert.Equal(5, session.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(20, ammo.Count(AmmoTypes.AmmoItemFor(gun.DefinitionId)));
        Assert.Null(refused.ReloadWork);
        Assert.Null(refused.Replies);
        Assert.Equal(started.Reply, Request(session, ammo, gun.Guid, gun.Guid, gun.DefinitionId, 300).Reply);
    }

    [Fact]
    public void OverlappingRequestGetsTerminalReplyWithoutSpendingEitherWeaponsAmmunition()
    {
        var (session, ammo, gun) = Create();
        var started = Request(session, ammo, gun.Guid, gun.Guid, gun.DefinitionId, 100);
        var pending = Assert.IsType<PendingWeaponReload>(started.ReloadWork);
        var next = ammo.Inventory.CreateInstance(1374, 1);
        session.Shooter.DeclareWeapon(next.Guid, next.DefinitionId, 2);

        var refused = Request(session, ammo, next.Guid, next.Guid, next.DefinitionId, 200);

        Assert.Equal(WeaponReplyPackets.ReloadRejected(next.Guid), refused.Reply);
        Assert.Same(pending, session.Reload);
        Assert.Equal(5, session.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(2, session.Shooter.AmmoOf(next.Guid));
        Assert.Equal(20, ammo.Count(AmmoTypes.AmmoItemFor(gun.DefinitionId)));
        Assert.Null(refused.ReloadWork);
        Assert.Null(refused.Replies);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownReloadGetsTerminalReplyWithoutInventingAWeapon(bool bagFed)
    {
        var (session, ammo, _) = Create();
        const ulong missing = 9999;
        var refused = Request(session, bagFed ? ammo : null, missing, missing, 0, 100);
        Assert.Equal(WeaponReplyPackets.ReloadRejected(missing), refused.Reply);
        Assert.Equal(-1, session.Shooter.AmmoOf(missing));
        Assert.Null(session.Reload);
    }

    private static (SessionCombat, PlayerAmmoContext, InventoryItemInstance) Create()
    {
        ulong next = 100;
        var inventory = new PlayerInventory(1, () => ++next);
        inventory.Bootstrap();
        var gun = inventory.CreateInstance(2425, 1);
        inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        Assert.True(inventory.TryStow(inventory.CreateInstance(AmmoTypes.AmmoItemFor(gun.DefinitionId), 20)));
        var session = new SessionCombat();
        session.Shooter.DeclareWeapon(gun.Guid, gun.DefinitionId, 5);
        return (session, new PlayerAmmoContext(inventory, 1, AmmoOptions.Default), gun);
    }

    private static WeaponArmResult Request(SessionCombat session, PlayerAmmoContext? ammo,
        ulong requested, ulong held, uint heldItem, long now)
    {
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(session, ShootingPacketBuilder.ReloadRequest(requested), CombatOptions.Default,
            heldItem, held, Vector3.Zero, now, results, ammo);
        return Assert.Single(results);
    }
}
