using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private void SyncDrawnWeaponReloadCounter(SoeConnection connection, GatewaySessionState state,
        InventoryItemInstance instance)
    {
        ulong count = state.Combat.Shooter.ReloadCountOf(instance.Guid);
        int magazine = state.Combat.Shooter.AmmoOf(instance.Guid);
        uint ammoItemId = AmmoTypes.AmmoItemFor(instance.DefinitionId);
        if (count == 0 || magazine < 0 || ammoItemId == 0 || state.Inventory is not { } inventory)
            return;

        // An ItemAdd tail carries magazine, groups and idle state, but no reload counters.
        // Reconcile the counter after drawing or unloading, including an item previously
        // recreated by a placement change, so expected=ack+1 matches our next acknowledgement.
        // Keep the server's ammunition, durability, firing deadlines and counter intact.
        int reserve = new PlayerAmmoContext(inventory, state.Guid, _options.Combat.Ammo).Count(ammoItemId);
        byte[] reply = WeaponReplyPackets.Reload(WeaponReplyPackets.ImmediateGameTime, instance.Guid,
            projectileCount: 0, ammoCount: (uint)magazine, inventoryAmmoCount: (uint)Math.Max(0, reserve),
            reloadCount: count);
        SendTunnel(connection, writer => writer.WriteRaw(reply));
    }

    private void ScheduleReload(SoeConnection connection, GatewaySessionState state, PendingWeaponReload pending)
    {
        if (!ReferenceEquals(state.Combat.Reload, pending))
        {
            return;
        }

        int delay = (int)Math.Clamp(pending.DueAtMs - Environment.TickCount64, 1, int.MaxValue);
        if (!Later(connection, delay, () =>
        {
            if (!ReferenceEquals(state.Combat.Reload, pending))
            {
                return;
            }

            if (!ReferenceEquals(connection.Tag, state) || state.DeathSent
                || state.Match == MatchStep.Ended
                || !ReferenceEquals(state.Inventory, pending.Ammo.Inventory))
            {
                state.Combat.Reload = null;
                return;
            }

            WeaponArmResult? result = WeaponFireArm.AdvanceReload(state.Combat, pending,
                Environment.TickCount64, HeldItemGuid(state), state.Inventory);
            if (result is { } completed)
            {
                state.WeaponArmResults.Clear();
                state.WeaponArmResults.Add(completed);
                DrainCombatArm(connection, state);
            }
            else if (ReferenceEquals(state.Combat.Reload, pending))
            {
                ScheduleReload(connection, state, pending);
            }
        }))
        {
            // No dispatcher appeared after the request: retain all unspent ammunition.
            state.Combat.Reload = null;
        }
    }

    private void StopReloadForVehicleEntry(SoeConnection connection, GatewaySessionState state)
    {
        // F pickup can continue a reload. Accepted vehicle entry still replaces the player's
        // control/ability context, so end it explicitly; rejected entries and cargo access do not.
        if (state.Combat.Reload is not { } pending
            || WeaponFireArm.CancelReload(state.Combat, pending.WeaponGuid,
                "entered vehicle", stopClientReload: true) is not { } stopped)
            return;

        state.WeaponArmResults.Clear();
        state.WeaponArmResults.Add(stopped);
        DrainCombatArm(connection, state);
    }

    private void StopReloadIfWeaponChanged(SoeConnection connection, GatewaySessionState state)
    {
        if (state.Combat.Reload is not { } pending || pending.WeaponGuid == HeldItemGuid(state))
        {
            return;
        }

        WeaponArmResult? stopped = WeaponFireArm.CancelReload(state.Combat, pending.WeaponGuid, "changed weapon");
        // The peer still names the OLD hand here. Interrupt it before the dress replaces
        // its arsenal; never relabel the cancellation with the newly selected item.
        if (stopped is { } interrupted) RelayPeerWeaponUpdate(state, interrupted);
        if (stopped?.Reply is { } reply && state.Inventory?.Items.ContainsKey(pending.WeaponGuid) == true)
        {
            SendTunnel(connection, writer => writer.WriteRaw(reply));
        }
    }

    private static void TrackWeaponDraw(GatewaySessionState state)
    {
        ulong guid = HeldItemGuid(state);
        if (state.Combat.Draw.WeaponGuid == guid) return;
        uint weaponId = RetailBalance.WeaponDefinitionIdFor(HeldItemDefinitionId(state));
        var timing = state.Weapons.EquipmentTiming(weaponId);
        state.Combat.Draw.Select(guid, timing.Equip, timing.Unequip, Environment.TickCount64);
    }
}
