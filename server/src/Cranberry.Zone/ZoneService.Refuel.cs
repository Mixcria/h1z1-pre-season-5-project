using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private const float RefuelReach = 4f;

    private static bool CanRefuelVehicle(GatewaySessionState state, MatchVehicle vehicle)
    {
        if (state.Fleet is not { } fleet || !fleet.TryGet(vehicle.Guid, out var current)
            || !ReferenceEquals(current, vehicle) || vehicle.Health == 0
            || vehicle.Fuel >= fleet.Options.MaxFuel) return false;
        if (fleet.TryGetForOccupant(state.Guid, out var occupied))
            return ReferenceEquals(occupied, vehicle);
        return state.Movement.Player?.Position is Vector3 position
            && Vector3.DistanceSquared(position, vehicle.Position) <= RefuelReach * RefuelReach;
    }

    private static MatchVehicle? FindRefuelVehicle(GatewaySessionState state, ulong targetGuid)
    {
        if (state.Fleet is not { } fleet) return null;
        if (targetGuid != 0 && targetGuid != state.Guid)
            return fleet.TryGet(targetGuid, out var requested) && CanRefuelVehicle(state, requested)
                ? requested : null;
        if (fleet.TryGetForOccupant(state.Guid, out var occupied))
            return CanRefuelVehicle(state, occupied) ? occupied : null;
        return fleet.Vehicles.Where(vehicle => CanRefuelVehicle(state, vehicle))
            .OrderBy(vehicle => Vector3.DistanceSquared(state.Movement.Player!.Position!.Value, vehicle.Position))
            .FirstOrDefault();
    }

    private void RefuelWithCan(SoeConnection connection, GatewaySessionState state, MatchVehicle vehicle)
    {
        uint previous = (uint)Math.Max(0f, vehicle.Fuel);
        float added = state.Fuel.Refuel(state.Fleet!, vehicle,
            new VehicleFuelOptions { RefuelAmount = state.Fleet!.Options.MaxFuel * 0.25f });
        KeyValuePair<GatewaySessionState, SoeConnection>[] members =
            _sharedLootMembership.TryGetValue(state, out ulong id)
                ? _sharedLootMatches[id].Members.ToArray() : [new(state, connection)];
        foreach (var (viewer, link) in members)
            if (link.State == ConnectionState.Open
                && (ReferenceEquals(viewer, state) || vehicle.SeatOf(viewer.Guid) >= 0
                    || viewer.StreamedVehicles.IsStreamed(vehicle.Guid)))
                SendTunnel(link, new CharacterResourceUpdate(vehicle.Guid, AugustFuelFacts.ResourceId,
                    AugustFuelFacts.ResourceType, (uint)vehicle.Fuel, previous).WriteTo);
        _log.Info($"{connection} refuel: vehicle {vehicle.Guid} +{added:0} -> {vehicle.Fuel:0}");
    }

    private void PublishConsumedVehicleFuel(SoeConnection connection, GatewaySessionState state,
        MatchVehicle vehicle, ulong itemGuid, InventoryItem? remaining)
    {
        KeyValuePair<GatewaySessionState, SoeConnection>[] members =
            _sharedLootMembership.TryGetValue(state, out ulong id)
                ? _sharedLootMatches[id].Members.ToArray() : [new(state, connection)];
        foreach (var (viewer, link) in members)
        {
            if (link.State != ConnectionState.Open
                || (!ReferenceEquals(viewer, state) && vehicle.SeatOf(viewer.Guid) < 0)) continue;
            if (remaining is null) SendTunnel(link, new ItemDelete(vehicle.Guid, itemGuid).WriteTo);
            else SendTunnel(link, new ItemAdd(vehicle.Guid, remaining).WriteTo);
            SendTunnel(link, vehicle.Inventory.ToContainers().WriteTo);
        }
    }
}
