using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // August ActorCompositeEffectDefinitions.xml: minor/moderate/severe human loops.
    private static uint BleedEffectFor(byte severity) => severity switch
    {
        0 => 0, <= 2 => 5106, <= 4 => 5042, _ => 5105,
    };

    private void WoundPlayer(SoeConnection connection, GatewaySessionState victim,
        GatewaySessionState attacker, uint weapon)
        => WoundPlayer(connection, victim, attacker.Guid, attacker.CharacterName, attacker.Hitpoints, weapon);

    private void WoundPlayer(SoeConnection connection, GatewaySessionState victim,
        ulong attackerGuid, string attackerName, uint attackerHealth, uint weapon)
    {
        if (!CanTakeGameplayDamage(victim)) return;
        byte previous = victim.PlayerMedical.Bleed;
        bool wasBleeding = victim.PlayerMedical.Bleeding;
        if (!victim.PlayerMedical.Wound(ref victim.BleedArmour, false, Environment.TickCount64)) return;
        victim.WoundAttacker = attackerGuid;
        victim.WoundAttackerName = attackerName;
        victim.WoundAttackerHealth = attackerHealth;
        victim.WoundWeapon = weapon;
        PublishPlayerBleeding(connection, victim, previous);
        if (wasBleeding) return;
        int generation = ++victim.BleedGeneration;
        int world = victim.WorldGeneration;
        void Tick()
        {
            if (victim.BleedGeneration != generation || victim.WorldGeneration != world
                || !victim.PlayerMedical.Bleeding) return;
            if (victim.Match != MatchStep.InMatch || victim.DeathSent || victim.Hitpoints == 0 || victim.VictorySent)
            {
                StopPlayerBleeding(connection, victim);
                return;
            }
            int amount = victim.PlayerMedical.PumpBleed(Environment.TickCount64);
            if (amount > 0 && CanTakeGameplayDamage(victim))
            {
                victim.LastDamageWeapon = victim.WoundWeapon;
                victim.LastDamageHeadshot = false;
                ApplyDamage(connection, victim, (uint)amount, DamageCause.Bullet,
                    victim.WoundAttacker, victim.WoundAttackerName, victim.WoundAttackerHealth);
            }
            if (victim.BleedGeneration == generation && victim.PlayerMedical.Bleeding)
                Later(connection, (int)MedicalModel.TickIntervalMs, Tick);
        }
        Later(connection, (int)MedicalModel.TickIntervalMs, Tick);
    }

    private void StopPlayerBleeding(SoeConnection connection, GatewaySessionState state)
    {
        byte previous = state.PlayerMedical.Bleed;
        state.BleedGeneration++;
        state.PlayerMedical.StopBleeding(ref state.BleedArmour);
        state.WoundAttacker = 0;
        state.WoundAttackerName = null;
        state.WoundAttackerHealth = state.WoundWeapon = 0;
        if (previous != 0 || state.BleedEffect != 0 || state.BleedHudInstance != 0)
            PublishPlayerBleeding(connection, state, previous);
    }

    private void PublishPlayerBleeding(SoeConnection connection, GatewaySessionState state, byte previous)
    {
        // Resource 21/21 is authored as Bleeding in the August Resources.txt.
        SendTunnel(connection, new CharacterResourceUpdate(state.Guid, 21, 21,
            state.PlayerMedical.Bleed, previous).WriteTo);
        if (state.BleedHudInstance != 0)
            SendTunnel(connection, new RemoveMedicalEffect(state.Guid, state.BleedHudInstance).WriteTo);
        state.BleedHudInstance = 0;
        // The stock HUD defines these five named tags. Mapping the existing five
        // server levels in increasing order is adapter policy, not a recovered
        // original wound/severity rule (see the medical restoration notes).
        (uint Id, uint Name) tag = state.PlayerMedical.Bleed switch
        {
            1 => (120107, 9034), 2 => (120111, 1110), 3 => (120112, 14116),
            4 => (120113, 9035), >= 5 => (120114, 14117), _ => (0, 0),
        };
        if (tag.Id != 0)
        {
            state.BleedHudInstance = NextMedicalEffectInstance(state);
            SendTunnel(connection, new MedicalEffectTag(state.Guid, state.BleedHudInstance, tag.Id, tag.Name).WriteTo);
        }
        uint effect = BleedEffectFor(state.PlayerMedical.Bleed);
        if (state.BleedEffect == effect) return;
        uint old = state.BleedEffect;
        state.BleedEffect = effect;
        void Send(SoeConnection link)
        {
            if (old != 0) SendTunnel(link, new RemoveEffectTagCompositeEffect(state.Guid, old).WriteTo);
            if (effect != 0) SendTunnel(link, new AddEffectTagCompositeEffect(state.Guid, effect).WriteTo);
        }
        Send(connection);
        if (state.Peer is not { } peer) return;
        var viewers = new List<PeerViewer>();
        _peers.CollectViewers(peer, viewers);
        if (viewers.Count == 0) return;

        // The same effect bytes apply to every viewer. Use the existing ordered peer
        // stream so a simultaneous wound burst does not flush a partial movement
        // bundle and create a separate reliable datagram for every tiny effect.
        // Self resources/effects above remain immediate; remove still precedes add.
        byte[]? remove = null, add = null;
        if (old != 0)
        {
            using var packet = new PacketWriter(RemoveEffectTagCompositeEffect.Length);
            new RemoveEffectTagCompositeEffect(state.Guid, old).WriteTo(packet);
            remove = packet.Written.ToArray();
        }
        if (effect != 0)
        {
            using var packet = new PacketWriter(AddEffectTagCompositeEffect.Length);
            new AddEffectTagCompositeEffect(state.Guid, effect).WriteTo(packet);
            add = packet.Written.ToArray();
        }
        foreach (var viewer in viewers)
            if (viewer.Viewer.Sink is SessionPeerSink sink && sink.IsOpen)
            {
                if (remove is not null) sink.Send(remove);
                if (add is not null) sink.Send(add);
            }
    }
}
