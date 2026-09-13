using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private Vector3? GameplayDamagePosition(GatewaySessionState state) =>
        state.Fleet?.TryGetForOccupant(state.Guid, out var mounted) == true
            ? mounted.Position : WorldStreamPosition(state);

    private bool TryDamageFuelCanWithBullet(SoeConnection connection, GatewaySessionState state, WeaponArmResult shot)
    {
        if (!state.Loot.TryGet(shot.TargetGuid, out var can)
            || !AugustFuelFacts.IsRefuelItem(can.ItemDefinitionId)) return false;
        if (!_options.Combat.EnableCombatDamage || state.Match != MatchStep.InMatch
            || state.DeathSent || state.Hitpoints == 0 || shot.UnresolvedHit is not { } hit
            || state.Movement.Player?.Position is not Vector3 player) return true;
        Vector3 muzzle = new(shot.AcceptedFire.X, shot.AcceptedFire.Y, shot.AcceptedFire.Z);
        Vector3 impact = new(hit.X, hit.Y, hit.Z);
        if (!(Vector3.Distance(player, can.Position) <= _options.Combat.MaxHitDistance)
            || !(Vector3.Distance(muzzle, can.Position) <= _options.Combat.MaxHitDistance)
            || !(Vector3.Distance(impact, can.Position) <= 2f)) return true;
        FuelCanDurability durability;
        if (state.StreamedLoot.TryGetClaimKey(can.WorldGuid, out var key)
            && _sharedLootMembership.TryGetValue(state, out var matchId))
        {
            var cans = _sharedLootMatches[matchId].FuelCans;
            if (!cans.TryGetValue(key, out durability!)) cans.Add(key, durability = new());
        }
        else
        {
            if (!state.FuelCans.TryGetValue(can.WorldGuid, out durability!))
                state.FuelCans.Add(can.WorldGuid, durability = new());
        }
        if (!durability.Hit(state.Guid, shot.AcceptedFire))
        {
            _log.Info($"{connection} explosives: fuel can {can.WorldGuid} hit {durability.Hits}/{FuelCanDurability.ShotsToExplode}");
            return true;
        }
        // The fire hint was consumed by WeaponFireArm. Claim the shared item before any
        // damage callback, so another pellet, player or pickup cannot explode it twice.
        if (!TryClaimSharedLoot(state, can.WorldGuid, out _)) return true;
        state.FuelCans.Remove(can.WorldGuid);
        state.StreamedLoot.NoteTaken(can.WorldGuid);
        state.FullNpcSent.Remove(can.WorldGuid);
        SendTunnel(connection, new RemovePlayer(can.WorldGuid).WriteTo);
        if (_options.SendProximateItems) SendProximateItems(connection, state);
        ExplodeWorldObject(connection, state, can.Position);
        _log.Info($"{connection} explosives: fuel can {can.WorldGuid} detonated after accepted projectile {hit.ProjectileId}");
        return true;
    }

    private void ExplodeWorldObject(SoeConnection connection, GatewaySessionState attacker, Vector3 position,
        bool playEffect = true)
    {
        SharedLootMatch? match = _sharedLootMembership.TryGetValue(attacker, out ulong id)
            ? _sharedLootMatches[id] : null;
        KeyValuePair<GatewaySessionState, SoeConnection>[] members = match is null
            ? [new(attacker, connection)] : match.Members.ToArray();
        uint attackerHealth = attacker.Hitpoints;
        if (match is not null) match.DamageBatchDepth++;
        try
        {
            foreach (var (victim, link) in members)
            {
                if (link.State != ConnectionState.Open || victim.Match != MatchStep.InMatch) continue;
                Vector3? location = GameplayDamagePosition(victim);
                float distance = location is Vector3 point ? Vector3.Distance(point, position) : float.PositiveInfinity;
                if (playEffect && distance <= _options.VehicleRenderDistance)
                    SendTunnel(link, new PlayWorldCompositeEffect(victim.Guid, WorldExplosive.EffectId, position).WriteTo);
                uint damage = WorldExplosive.DamageAt(distance);
                if (damage == 0 || victim.Hitpoints == 0 || victim.DeathSent || victim.DevConsole.Invulnerable) continue;
                victim.LastDamageWeapon = 0;
                victim.LastDamageHeadshot = false;
                bool credited = victim.Guid != attacker.Guid;
                ApplyDamage(link, victim, damage, DamageCause.Explosion,
                    credited ? attacker.Guid : 0, credited ? attacker.CharacterName : null, attackerHealth);
            }
            if (attacker.Fleet is { } fleet)
                foreach (var vehicle in fleet.Vehicles.ToArray())
                {
                    uint damage = WorldExplosive.DamageAt(Vector3.Distance(vehicle.Position, position), 50_000);
                    if (vehicle.Health > 0 && damage > 0)
                        ApplyVehicleDamage(connection, attacker, vehicle, damage, "barrel/fuel explosion", attacker: attacker);
                }
            foreach (var target in attacker.Combat.Targets.All)
            {
                uint damage = WorldExplosive.DamageAt(Vector3.Distance(target.Position, position));
                if (target.IsAlive && damage > 0 && target.Damage((int)damage, Environment.TickCount64))
                    KillPracticeTarget(connection, attacker, target);
            }
        }
        finally
        {
            if (match is not null)
            {
                match.DamageBatchDepth--;
                if (match.AliveCountPending) PublishSharedAlive(match);
            }
        }
    }
}
