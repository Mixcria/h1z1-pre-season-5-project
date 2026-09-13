using Cranberry.Zone.Combat;
using Cranberry.Zone.World;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private void StopCombatPresentationOnDeath(GatewaySessionState state)
    {
        ulong reloadGuid = state.Combat.Reload?.WeaponGuid ?? 0;
        if (reloadGuid != 0) WeaponFireArm.CancelReload(state.Combat, reloadGuid, "death");
        if (!_options.Peers.Relay || !_options.Peers.FireRelay
            || state.Peer is not { HeldWeaponItemGuid: not 0 } subject) return;
        state.Combat.Shooter.NoteTriggerStop(subject.HeldWeaponItemGuid);
        _peers.CollectViewers(subject, _peerViewers);
        foreach (var viewer in _peerViewers)
        {
            if (reloadGuid == subject.HeldWeaponItemGuid)
                viewer.Viewer.Sink.Send(RemoteWeaponPackets.ReloadInterrupt(viewer.TransientId, reloadGuid));
            viewer.Viewer.Sink.Send(RemoteWeaponPackets.FireState(viewer.TransientId,
                subject.HeldWeaponItemGuid, false, new System.Numerics.Vector4(subject.Position, 1)));
        }
    }

    private void RelayPeerWeaponUpdate(GatewaySessionState state, in WeaponArmResult result)
    {
        if (!_options.Peers.Relay || !_options.Peers.FireRelay
            || state.Peer is not PeerSession subject || state.DeathSent || state.Hitpoints == 0
            || result.RelayWeaponGuid == 0 || result.RelayWeaponGuid != subject.HeldWeaponItemGuid
            || result.RemoteUpdate is not { } type) return;

        // Retain only persistent selection. Replaying old shots/chambers/reload starts on
        // interest entry would restart an action with an unproved native clock/phase.
        if (type == RemoteWeaponPackets.WeaponUpdateType.SwitchFireMode)
        {
            subject.HeldFireGroupIndex = result.RelayFireGroup;
            subject.HeldFireModeIndex = result.RelayFireMode;
        }
        _peers.CollectViewers(subject, _peerViewers);
        foreach (var viewer in _peerViewers)
        {
            uint owner = viewer.TransientId;
            ulong item = result.RelayWeaponGuid;
            byte[] packet = type switch
            {
                RemoteWeaponPackets.WeaponUpdateType.SwitchFireMode =>
                    RemoteWeaponPackets.SwitchFireMode(owner, item, checked((sbyte)result.RelayFireGroup), checked((sbyte)result.RelayFireMode)),
                RemoteWeaponPackets.WeaponUpdateType.Chamber => RemoteWeaponPackets.Chamber(owner, item),
                RemoteWeaponPackets.WeaponUpdateType.ChamberInterrupt => RemoteWeaponPackets.ChamberInterrupt(owner, item),
                RemoteWeaponPackets.WeaponUpdateType.Reload => RemoteWeaponPackets.Reload(owner, item),
                RemoteWeaponPackets.WeaponUpdateType.ReloadInterrupt => RemoteWeaponPackets.ReloadInterrupt(owner, item),
                // FUN_141499040 reads the bool but posts the same loop-end event either way.
                RemoteWeaponPackets.WeaponUpdateType.ReloadLoopEnd => RemoteWeaponPackets.ReloadLoopEnd(owner, item, false),
                _ => throw new InvalidOperationException($"Unsupported combat relay {type}"),
            };
            viewer.Viewer.Sink.Send(packet);
        }
    }
}
