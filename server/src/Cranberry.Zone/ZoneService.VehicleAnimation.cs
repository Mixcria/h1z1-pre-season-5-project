using System.Diagnostics.CodeAnalysis;
using Cranberry.Transport;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // Native 140e7f6f0 emits animation deltas only for the local physics controller.
    // Use the same driver/coasting lease as managed movement, including its registration.
    private static bool TryGetVehicleSimulator(GatewaySessionState state, ulong guid,
        [NotNullWhen(true)] out MatchVehicle? vehicle)
    {
        vehicle = null;
        if (state.Fleet is not { } fleet || !fleet.TryGet(guid, out var candidate)
            || candidate.Health == 0
            || !fleet.TryGetForSimulator(candidate.TransientId, state.Guid, out var controlled)
            || !state.Movement.TryGetManaged(candidate.TransientId, out var managed)
            || managed.Guid != guid)
            return false;
        vehicle = controlled;
        return true;
    }

    private void HandleVehicleAnimation(SoeConnection connection, GatewaySessionState state,
        ReadOnlySpan<byte> payload)
    {
        if (!VehicleStateData.TryParse(payload, out var delta)
            // This exact client's sender supplies zero. Other producer meanings remain unknown.
            || delta.Value != 0
            || !TryGetVehicleSimulator(state, delta.VehicleGuid, out var vehicle)
            || !vehicle.Animation.TryApply(delta, out var next))
            return;

        vehicle.Animation = next;
        // The maps change presentation, never authoritative fuel, condition, parts or ownership.
        foreach (var (viewer, link) in VehicleMembers(connection, state))
        {
            if (ReferenceEquals(viewer, state) || link.State != ConnectionState.Open
                || (vehicle.SeatOf(viewer.Guid) < 0 && !viewer.StreamedVehicles.IsSpawned(vehicle.Guid)))
                continue;
            SendTunnel(link, delta.WriteTo);
        }
    }

    private void SendRetainedVehicleAnimation(SoeConnection connection, MatchVehicle vehicle)
    {
        if (vehicle.Health > 0 && (vehicle.Animation.ListA.Count != 0 || vehicle.Animation.ListB.Count != 0))
            SendTunnel(connection, vehicle.Animation.ToPacket(vehicle.Guid).WriteTo);
    }
}
