using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private void ExplodeVehicle(SoeConnection connection, GatewaySessionState state,
        in VehicleDamageOutcome outcome, GatewaySessionState? attacker)
    {
        var vehicle = outcome.Vehicle;
        state.Fleet?.ScheduleWreck(vehicle, Environment.TickCount64);
        PublishVehicleOccupants(connection, state, vehicle);
        SetVehicleHorn(connection, state, vehicle, false);
        SetVehicleHeadlights(connection, state, vehicle, false);
        SetVehicleSiren(connection, state, vehicle, false);
        SharedLootMatch? match = _sharedLootMembership.TryGetValue(state, out ulong id)
            ? _sharedLootMatches[id] : null;
        KeyValuePair<GatewaySessionState, SoeConnection>[] members = match is null
            ? [new(state, connection)] : match.Members.ToArray();
        uint effect = VehicleCombatBalance.ExplosionEffect(vehicle.Definition.VehicleId);
        uint attackerHealth = attacker?.Hitpoints ?? 0;
        if (match is not null) match.DamageBatchDepth++;
        try
        {
            foreach (var (victim, link) in members)
            {
                if (link.State != ConnectionState.Open || victim.Match != MatchStep.InMatch) continue;
                bool occupant = outcome.EvictedOccupants.Contains(victim.Guid);
                Vector3? where = occupant ? vehicle.Position
                    : victim.Fleet?.TryGetForOccupant(victim.Guid, out var mounted) == true
                        ? mounted.Position : victim.Movement.Player?.Position;
                float distance = where is Vector3 at ? Vector3.Distance(at, vehicle.Position) : float.PositiveInfinity;

                if (occupant || victim.StreamedVehicles.IsSpawned(vehicle.Guid) || ReferenceEquals(victim, state))
                    SendTunnel(link, VehicleEngine.ServerIssued(vehicle.Guid, false).WriteTo);
                if (effect != 0 && (occupant || distance <= _options.VehicleRenderDistance || ReferenceEquals(victim, state)))
                    SendTunnel(link, new PlayWorldCompositeEffect(vehicle.Guid, effect, vehicle.Position).WriteTo);
                victim.Boost.Clear(vehicle.Guid);
                if (occupant)
                {
                    ResetVehicleRiderPose(victim, vehicle);
                    victim.Ignition.Cancel(victim.Guid);
                    SendVehicleClearingBurst(link, victim, vehicle);
                    victim.VehicleEntry.NoteExited();
                    victim.Movement.RemoveManagedEntity(vehicle.TransientId);
                }
                else if (vehicle.CoastingOwnerGuid == victim.Guid)
                {
                    victim.Movement.RemoveManagedEntity(vehicle.TransientId);
                    SendTunnel(link, new ManagedObjectResponseControl(false, vehicle.Guid).WriteTo);
                    SendTunnel(link, writer => CharacterManagedObject.Release(vehicle.Guid).WriteTo(writer));
                    SendTunnel(link, VehicleManagedLocation.At(vehicle).WriteTo);
                    SendTunnel(link, VehiclePoseRelay.Parked(vehicle).WriteTo);
                }
                if (occupant || victim.StreamedVehicles.IsSpawned(vehicle.Guid) || ReferenceEquals(victim, state))
                    SendVehicleWreckState(link, vehicle);
                if (victim.DeathSent || victim.Hitpoints == 0 || victim.DevConsole.Invulnerable) continue;
                uint damage = _options.VehicleDamage.ExplosionDamageAt(distance);
                if (occupant) damage = Math.Max(damage, _options.VehicleDamage.WreckOccupantDamage);
                if (damage == 0) continue;
                victim.LastDamageWeapon = 0;
                victim.LastDamageHeadshot = false;
                bool credited = attacker is not null && attacker.Guid != victim.Guid;
                ApplyDamage(link, victim, damage, DamageCause.Vehicle,
                    credited ? attacker!.Guid : 0, credited ? attacker!.CharacterName : null, attackerHealth);
            }
        }
        finally
        {
            vehicle.EndCoast();
            if (match is not null)
            {
                match.DamageBatchDepth--;
                if (match.AliveCountPending) PublishSharedAlive(match);
            }
        }
        _log.Info($"{connection} vehicles: {vehicle.Guid} exploded at zero health; effect={effect}, blast={_options.VehicleDamage.ExplosionDamage} units/{_options.VehicleDamage.ExplosionRadius}m");
    }
}
