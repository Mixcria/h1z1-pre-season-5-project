namespace Cranberry.Zone.Vehicles;

/// <summary>The live car state sent on spawn and on the client's full-data request.</summary>
public static class VehicleFullState
{
    public static IReadOnlyList<CharacterResource> ResourcesFor(MatchVehicle car) =>
    [
        new(0, AugustFuelFacts.ResourceId, AugustFuelFacts.ResourceType,
            (uint)Math.Clamp(car.Fuel, 0, uint.MaxValue), (uint)Math.Clamp(car.Fuel, 0, uint.MaxValue)),
        new(1, AugustVehicleDamageFacts.ConditionResourceId, AugustVehicleDamageFacts.ConditionResourceType,
            car.Health, car.Health),
    ];

    public static LightweightToFullVehicle Create(MatchVehicle car) =>
        new(car.TransientId, car.Guid, ResourcesFor(car), car.Occupants(), car.EngineOn);
}
