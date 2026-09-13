using System.Numerics;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // Managed vehicle movement updates the car while the on-foot pose can stay at the entry
    // point. Gas and airstrike damage must follow the occupied vehicle, including passengers.
    private Vector3? EnvironmentalDamagePosition(GatewaySessionState state)
    {
        if (state.ChuteGuid != 0 && state.MountRequested)
        {
            // Channel 2 may still name the staging/ground position while the rider descends.
            // An unresolved chute pose is unknown, not a reason to reuse that stale position.
            return state.Movement.TryGetManaged(_options.ParachuteTransientId, out var chute)
                && chute.Guid == state.ChuteGuid ? chute.Movement?.Position : null;
        }
        return state.Fleet?.TryGetForOccupant(state.Guid, out var vehicle) == true
            ? vehicle.Position : state.Movement.Player?.Position;
    }
}
