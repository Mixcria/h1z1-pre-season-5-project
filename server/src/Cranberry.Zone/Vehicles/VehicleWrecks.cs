namespace Cranberry.Zone.Vehicles;

public sealed partial class VehicleFleet
{
    private readonly PriorityQueue<MatchVehicle, long> _wrecks = new();

    public void ScheduleWreck(MatchVehicle vehicle, long nowMs)
    {
        if (vehicle.Health != 0 || vehicle.WreckExpiresAtMs.HasValue
            || !TryGet(vehicle.Guid, out var held) || !ReferenceEquals(held, vehicle)) return;
        long expires = nowMs + VehicleCombatBalance.WreckLifetimeMs;
        vehicle.WreckExpiresAtMs = expires;
        _wrecks.Enqueue(vehicle, expires);
    }

    // No fleet scan or allocation on idle ticks; the first viewer pumping the shared fleet
    // removes each due wreck once. The deadline survives the shooter's death or disconnect.
    public IReadOnlyList<MatchVehicle> ReapWrecks(long nowMs)
    {
        List<MatchVehicle>? removed = null;
        while (_wrecks.TryPeek(out var vehicle, out long expires) && nowMs >= expires)
        {
            _wrecks.Dequeue();
            if (vehicle.Health != 0 || !TryGet(vehicle.Guid, out var held)
                || !ReferenceEquals(held, vehicle)) continue;
            _byGuid.Remove(vehicle.Guid);
            _byTransient.Remove(vehicle.TransientId);
            (removed ??= []).Add(vehicle);
        }
        return (IReadOnlyList<MatchVehicle>?)removed ?? [];
    }
}
