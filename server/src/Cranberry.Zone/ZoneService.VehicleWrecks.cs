using Cranberry.Transport;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private void SendVehicleWreckState(SoeConnection connection, MatchVehicle vehicle)
    {
        uint model = VehicleCombatBalance.DestroyedModel(vehicle.Definition.VehicleId);
        if (vehicle.Health != 0 || model == 0) return;
        SendTunnel(connection, new VehicleDestroyedPacket(vehicle.Guid, model).WriteTo);
        // The model replacement clears attached damage effects. Attach the native corpse loop
        // afterwards, without a second explosion on a late join or return to streaming range.
        uint effect = VehicleCombatBalance.CorpseEffect(vehicle.Definition.VehicleId);
        if (effect != 0) SendTunnel(connection, new PlayDialogEffect(vehicle.Guid, effect).WriteTo);
    }

    private static bool CanSendVehicleSpawn(VehicleFleet fleet, MatchVehicle vehicle) =>
        fleet.TryGet(vehicle.Guid, out var held) && ReferenceEquals(held, vehicle)
        && (vehicle.WreckExpiresAtMs is not { } expires || Environment.TickCount64 < expires);

    private void ReapVehicleWrecks(SoeConnection connection, GatewaySessionState state,
        VehicleFleet fleet, long nowMs)
    {
        var removed = fleet.ReapWrecks(nowMs);
        if (removed.Count == 0) return;
        KeyValuePair<GatewaySessionState, SoeConnection>[] viewers = _sharedLootMembership.TryGetValue(state, out ulong id)
            ? _sharedLootMatches[id].Members.ToArray() : [new(state, connection)];
        foreach (var vehicle in removed)
        {
            foreach (var (viewer, link) in viewers)
            {
                if (!viewer.StreamedVehicles.NoteEvicted(vehicle.Guid)) continue;
                viewer.Movement.RemoveManagedEntity(vehicle.TransientId);
                if (link.State == ConnectionState.Open)
                    SendTunnel(link, new RemovePlayer(vehicle.Guid).WriteTo);
            }
            _log.Info($"{connection} vehicles: removed expired wreck {vehicle.Guid}");
        }
    }
}
