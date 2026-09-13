using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private void ResetPlayerVitals(SoeConnection connection, GatewaySessionState state)
    {
        CancelMedicalCast(connection, state, "starting a new match phase");
        ClearHealingHud(connection, state);
        StopPlayerBleeding(connection, state);
        CancelVehicleComponentRemoval(connection, state);
        state.PlayerVitalsGeneration++;
        uint previousHealth = state.Hitpoints;
        state.Hitpoints = _options.Gas.MaxHitpoints;
        state.DeathSent = false;
        state.VictorySent = false;
        state.EndedAtMs = 0;
        state.AliveSent = null;
        PublishPlayerHealth(connection, state, previousHealth);
    }

    private Vector3? MedicalCastPosition(GatewaySessionState state)
    {
        if (state.Fleet is VehicleFleet fleet
            && fleet.TryGetForOccupant(state.Guid, out MatchVehicle? vehicle)) return vehicle.Position;
        if (state.ChuteGuid != 0 && state.MountRequested
            && state.Movement.TryGetManaged(_options.ParachuteTransientId, out ManagedEntityMovementState? chute))
            return chute.Movement?.Position;
        return state.Movement.Player?.Position;
    }

    private void CancelMedicalCastIfMoved(SoeConnection connection, GatewaySessionState state, Vector3? position)
    {
        if (state.PendingMedicalCast is not { } cast) return;
        if (cast.WorldGeneration != state.WorldGeneration || !ReferenceEquals(cast.Inventory, state.Inventory))
        {
            CancelMedicalCast(connection, state, "world or inventory no longer active", notify: false);
            return;
        }
        if (cast.HasMoved(position))
            CancelMedicalCast(connection, state, "player moved during application");
    }

    private void CancelMedicalCast(SoeConnection connection, GatewaySessionState state, string reason, bool notify = true)
    {
        if (state.PendingMedicalCast is null) return;
        state.PendingMedicalCast = null;
        state.ConsumeBusyUntil = 0;
        if (notify) SendInteractionStops(connection, state);
        _log.Info($"{connection} medical: cast cancelled - {reason}; item retained, no heal started");
    }
}
