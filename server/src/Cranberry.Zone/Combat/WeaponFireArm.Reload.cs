using Cranberry.Zone.Inventory;

namespace Cranberry.Zone.Combat;

/// <summary>
/// A reload moves ammunition only when a step completes. The object is also the cancellation
/// token: a callback for an old object cannot complete a newer reload of the same weapon.
/// </summary>
public sealed class PendingWeaponReload
{
    internal PendingWeaponReload(ulong weaponGuid, uint itemId, uint ammoItemId,
        PlayerAmmoContext ammo, int intervalMs, bool shellByShell, long nowMs)
    {
        WeaponGuid = weaponGuid;
        ItemId = itemId;
        AmmoItemId = ammoItemId;
        Ammo = ammo;
        IntervalMs = intervalMs;
        ShellByShell = shellByShell;
        DueAtMs = nowMs + intervalMs;
    }

    public ulong WeaponGuid { get; }
    public uint ItemId { get; }
    public uint AmmoItemId { get; }
    public PlayerAmmoContext Ammo { get; }
    public int IntervalMs { get; }
    public bool ShellByShell { get; }
    public long DueAtMs { get; internal set; }
}

public static partial class WeaponFireArm
{
    /// <summary>
    /// Completes at most one due step, using the current inventory. Early, cancelled, and
    /// duplicate callbacks cannot spend ammunition. The caller supplies a monotonic clock.
    /// </summary>
    public static WeaponArmResult? AdvanceReload(SessionCombat session, PendingWeaponReload pending,
        long nowMs, ulong heldWeaponGuid, PlayerInventory? inventory)
    {
        if (!ReferenceEquals(session.Reload, pending))
        {
            return null;
        }

        if (!ReferenceEquals(inventory, pending.Ammo.Inventory)
            || !inventory.Items.ContainsKey(pending.WeaponGuid))
        {
            session.Reload = null;
            return new WeaponArmResult($"{Tag} reload discarded: weapon removed or inventory replaced",
                null, false, null);
        }

        if (heldWeaponGuid != pending.WeaponGuid)
        {
            return CancelReload(session, pending.WeaponGuid, "weapon no longer held", stopClientReload: true);
        }

        if (nowMs < pending.DueAtMs)
        {
            return null;
        }

        int magazine = session.Shooter.AmmoOf(pending.WeaponGuid);
        int room = RetailBalance.ClipSize(pending.ItemId) - magazine;
        if (magazine < 0 || room <= 0 || pending.Ammo.Count(pending.AmmoItemId) <= 0)
        {
            return CancelReload(session, pending.WeaponGuid, "magazine full or reserve empty", stopClientReload: true);
        }

        var replies = new List<byte[]>();
        int taken = pending.Ammo.Take(pending.AmmoItemId, pending.ShellByShell ? 1 : room, replies);
        if (taken <= 0)
        {
            return CancelReload(session, pending.WeaponGuid, "reserve unavailable", stopClientReload: true);
        }

        session.Shooter.Load(pending.WeaponGuid, taken);
        replies.Add(ReloadSnapshot(session, pending));
        bool more = pending.ShellByShell
            && session.Shooter.AmmoOf(pending.WeaponGuid) < RetailBalance.ClipSize(pending.ItemId)
            && pending.Ammo.Count(pending.AmmoItemId) > 0;
        if (more)
        {
            // A late dispatcher must not complete several shells in one instant.
            pending.DueAtMs = nowMs + pending.IntervalMs;
        }
        else
        {
            session.Reload = null;
            if (pending.ShellByShell
                && session.Shooter.AmmoOf(pending.WeaponGuid) < RetailBalance.ClipSize(pending.ItemId))
            {
                // Reserve may have shrunk after the initial acknowledgement cached it client-side.
                // A partially filled pump must leave its native loop when the server runs out.
                replies.Add(WeaponReplyPackets.ReloadRejected(pending.WeaponGuid));
            }
        }

        return new WeaponArmResult(
            $"{Tag} reload weapon={pending.WeaponGuid} loaded {taken} round(s); "
            + $"magazine {session.Shooter.AmmoOf(pending.WeaponGuid)}, reserve {pending.Ammo.Count(pending.AmmoItemId)}",
            null, false, null, Replies: replies)
        {
            ReloadWork = more ? pending : null, RelayWeaponGuid = pending.WeaponGuid,
            RemoteUpdate = !more && pending.ShellByShell ? RemoteWeaponPackets.WeaponUpdateType.ReloadLoopEnd : null,
        };
    }

    /// <summary>Ends a reload and reports only the ammunition already loaded.</summary>
    public static WeaponArmResult? CancelReload(SessionCombat session, ulong weaponGuid, string reason,
        bool stopClientReload = false)
    {
        if (session.Reload is not { } pending || pending.WeaponGuid != weaponGuid)
        {
            return null;
        }

        session.Reload = null;
        // The client's applier needs a newer reload count even when no shell completed.
        session.Shooter.Load(weaponGuid, 0);
        return new WeaponArmResult(
            $"{Tag} reload weapon={weaponGuid} stopped: {reason}; no pending rounds consumed",
            null, false, null, ReloadSnapshot(session, pending),
            // A count snapshot alone does not leave native reload states 10/11/12. If the
            // server ended the reload (e.g. its remaining shells were dropped), use the
            // existing rejection handler to stop prediction against the now-stale reserve.
            Replies: stopClientReload ? [WeaponReplyPackets.ReloadRejected(weaponGuid)] : null)
        {
            RelayWeaponGuid = weaponGuid, RemoteUpdate = RemoteWeaponPackets.WeaponUpdateType.ReloadInterrupt,
        };
    }

    private static byte[] ReloadAcknowledgement(SessionCombat session, PendingWeaponReload pending) =>
        // This field becomes the client's predicted load delta, not merely a projectile count.
        // A magazine acknowledgement must stage no new round. The pump requires the positive
        // branch to cache its current reserve (141487990); its native loop predicts one shell
        // independently of this field (142292cb0). Neither acknowledgement changes server ammo.
        ReloadSnapshot(session, pending, projectileCount: pending.ShellByShell ? 1u : 0u);

    // Authoritative snapshots use zero predicted delta: even if a new local request made this
    // count the expected value, FUN_1414887e0 must apply the actual magazine, not stage a round.
    private static byte[] ReloadSnapshot(SessionCombat session, PendingWeaponReload pending,
        uint projectileCount = 0) =>
        WeaponReplyPackets.Reload(WeaponReplyPackets.ImmediateGameTime, pending.WeaponGuid,
            projectileCount,
            ammoCount: (uint)Math.Max(0, session.Shooter.AmmoOf(pending.WeaponGuid)),
            inventoryAmmoCount: (uint)Math.Max(0, pending.Ammo.Count(pending.AmmoItemId)),
            reloadCount: session.Shooter.ReloadCountOf(pending.WeaponGuid));

    private static WeaponArmResult OnReloadInterrupt(SessionCombat session, ReadOnlySpan<byte> packet,
        in WeaponBaseHeader header)
    {
        ReadOnlySpan<byte> body = WeaponBaseDecoder.Body(packet);
        if (body.Length < sizeof(ulong))
        {
            session.Undecodable++;
            return Unreadable(packet, "ReloadInterrupt", header);
        }

        ulong guid = BitConverter.ToUInt64(body);
        return CancelReload(session, guid, "82 09 ReloadInterrupt")
            ?? new WeaponArmResult($"{Tag} 82 09 ReloadInterrupt weapon={guid} - no matching reload",
                null, false, null);
    }
}
