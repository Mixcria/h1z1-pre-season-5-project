using Cranberry.Transport;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private bool HandleVehicleDriverAbility(SoeConnection connection, GatewaySessionState state,
        ReadOnlySpan<byte> payload)
    {
        if (VehicleDriverControls.TryReadRuntimeFailure(payload, out uint failedAbility,
                out uint status, out ulong failedSource, out ulong failedTarget)
            && failedSource == state.Guid && state.VehicleEngineRuntimeGuid == failedTarget
            && state.Fleet?.TryGet(failedTarget, out var failedVehicle) == true
            && failedVehicle.DriverGuid == state.Guid
            && failedAbility == VehicleDriverControls.EngineAbility(failedVehicle.Definition.VehicleId))
        {
            _log.Warn($"{connection} vehicles: client rejected motor runtime {failedAbility} for {failedTarget}, status {status}");
            SetDriverEngine(connection, state, failedVehicle, false);
            return true;
        }
        if (!VehicleDriverControls.TryRead(payload, out uint ability, out uint key, out bool on,
                out ulong source, out ulong target)
            || state.Fleet is not VehicleFleet fleet || !fleet.TryGetForOccupant(state.Guid, out var vehicle)
            || vehicle.DriverGuid != state.Guid || vehicle.OwnerGuid != state.Guid || vehicle.Health == 0
            || (on && (source != state.Guid || target != vehicle.Guid)))
            return false;

        if (key == VehicleDriverControls.EngineKey
            && ability == VehicleDriverControls.EngineAbility(vehicle.Definition.VehicleId))
        {
            SetDriverEngine(connection, state, vehicle, on);
            return true;
        }
        if (key == VehicleDriverControls.HeadlightsKey
            && ability == VehicleDriverControls.HeadlightsAbility(vehicle.Definition.VehicleId)
            && vehicle.Inventory.HasSlot(18))
        {
            SetVehicleHeadlights(connection, state, vehicle, on);
            SendTunnel(connection, writer => writer.WriteRaw(VehicleDriverControls.Ability(ability, key, on)));
            return true;
        }
        if (key == VehicleDriverControls.SirenKey && ability == VehicleDriverControls.SirenAbility
            && vehicle.Definition.VehicleId == 3 && vehicle.Inventory.HasSlot(35))
        {
            SetVehicleSiren(connection, state, vehicle, on);
            SendTunnel(connection, writer => writer.WriteRaw(VehicleDriverControls.Ability(ability, key, on)));
            return true;
        }
        if (key != VehicleDriverControls.HornKey || ability != VehicleDriverControls.HornAbility
            || !vehicle.Inventory.HasSlot(36)) return false;

        SetVehicleHorn(connection, state, vehicle, on);
        SendTunnel(connection, writer => writer.WriteRaw(VehicleDriverControls.Ability(ability, key, on)));
        return true;
    }

    private void SetDriverEngine(SoeConnection connection, GatewaySessionState state, MatchVehicle vehicle, bool on,
        bool clientRuntime = false)
    {
        bool wasOn = vehicle.EngineOn;
        if (!on)
        {
            state.Ignition.Cancel(state.Guid);
            ReleaseBoost(connection, state, vehicle, "engine switched off");
            vehicle.EngineOn = false;
        }
        else
        {
            state.Ignition.TryStart(vehicle, state.Guid, false, Environment.TickCount64,
                state.MatchSeed, _options.VehicleFleet, _options.VehicleIgnition);
        }
        // MotorRun requests do not set the actor's engine-enabled bit. Native 140c95340
        // ignores a local-player origin entirely, including that movement gate; this must
        // be a server command even when the request came from the driver's local runtime.
        // Only a state transition is broadcast, so a duplicate request cannot restart audio.
        if (wasOn != vehicle.EngineOn || vehicle.EngineOn != on)
            SendVehicleControlToViewers(connection, state, vehicle,
                VehicleEngine.ServerIssued(vehicle.Guid, vehicle.EngineOn).WriteTo);
        if (clientRuntime) state.VehicleEngineRuntimeGuid = on ? vehicle.Guid : 0;
        SyncDriverEngineRuntime(connection, state, vehicle);
        _log.Info($"{connection} vehicles: driver engine {(vehicle.EngineOn ? "on" : "off")} for {vehicle.Guid}");
    }

    private void SetVehicleHorn(SoeConnection connection, GatewaySessionState state, MatchVehicle vehicle, bool on)
    {
        if (vehicle.HornOn == on) return;
        vehicle.HornOn = on;
        uint effect = VehicleDriverControls.HornEffect(vehicle.Definition.VehicleId);
        if (on)
            SendVehicleControlToViewers(connection, state, vehicle, new AddEffectTagCompositeEffect(vehicle.Guid, effect).WriteTo);
        else
            SendVehicleControlToViewers(connection, state, vehicle, new RemoveEffectTagCompositeEffect(vehicle.Guid, effect).WriteTo);
        _log.Info($"{connection} vehicles: horn {(on ? "on" : "off")} for {vehicle.Guid}");
    }

    private void SyncDriverEngineRuntime(SoeConnection connection, GatewaySessionState state, MatchVehicle vehicle)
    {
        if (vehicle.DriverGuid != state.Guid) return;
        // a0/06 rebuilds the manager but leaves its active abilities and runtimes intact.
        // Reinitializing on every inventory refresh creates duplicate native abilities.
        if (vehicle.EngineOn && state.VehicleEngineRuntimeGuid != vehicle.Guid)
        {
            state.VehicleEngineRuntimeGuid = vehicle.Guid;
            SendTunnel(connection, writer => writer.WriteRaw(VehicleDriverControls.StartEngineRuntime(vehicle, state.Guid)));
        }
        else if (!vehicle.EngineOn) StopDriverEngineRuntime(connection, state, vehicle);
        SendTunnel(connection, writer => writer.WriteRaw(VehicleDriverControls.Ability(
            VehicleDriverControls.EngineAbility(vehicle.Definition.VehicleId), VehicleDriverControls.EngineKey, vehicle.EngineOn)));
    }

    private void StopDriverEngineRuntime(SoeConnection connection, GatewaySessionState state, MatchVehicle vehicle)
    {
        if (state.VehicleEngineRuntimeGuid != vehicle.Guid) return;
        state.VehicleEngineRuntimeGuid = 0;
        // MotorRun is an independent, non-expiring client effect. Destroying its ability
        // alone waits for a later client removal request and can leave the motor active.
        SendTunnel(connection, writer => writer.WriteRaw(VehicleDriverControls.RemoveMotorEffect(vehicle, state.Guid)));
        SendTunnel(connection, writer => writer.WriteRaw(VehicleDriverControls.StopEngineRuntime(vehicle.Definition.VehicleId)));
        SendTunnel(connection, writer => writer.WriteRaw(VehicleDriverControls.Ability(
            VehicleDriverControls.EngineAbility(vehicle.Definition.VehicleId), VehicleDriverControls.EngineKey, false)));
    }

    private void SetVehicleHeadlights(SoeConnection connection, GatewaySessionState state, MatchVehicle vehicle, bool on)
    {
        if (vehicle.HeadlightsOn == on) return;
        vehicle.HeadlightsOn = on;
        uint effect = VehicleDriverControls.HeadlightsEffect(vehicle.Definition.VehicleId);
        if (on)
            SendVehicleControlToViewers(connection, state, vehicle, new AddEffectTagCompositeEffect(vehicle.Guid, effect).WriteTo);
        else
            SendVehicleControlToViewers(connection, state, vehicle, new RemoveEffectTagCompositeEffect(vehicle.Guid, effect).WriteTo);
        _log.Info($"{connection} vehicles: headlights {(on ? "on" : "off")} for {vehicle.Guid}");
    }

    private void SetVehicleSiren(SoeConnection connection, GatewaySessionState state, MatchVehicle vehicle, bool on)
    {
        if (vehicle.SirenOn == on) return;
        vehicle.SirenOn = on;
        if (on)
            SendVehicleControlToViewers(connection, state, vehicle,
                new AddEffectTagCompositeEffect(vehicle.Guid, VehicleDriverControls.SirenEffect).WriteTo);
        else
            SendVehicleControlToViewers(connection, state, vehicle,
                new RemoveEffectTagCompositeEffect(vehicle.Guid, VehicleDriverControls.SirenEffect).WriteTo);
        _log.Info($"{connection} vehicles: police siren {(on ? "on" : "off")} for {vehicle.Guid}");
    }

    private void SendVehicleActiveEffects(SoeConnection connection, MatchVehicle vehicle)
    {
        if (vehicle.Health == 0)
        {
            SendVehicleWreckState(connection, vehicle);
            return;
        }
        if (vehicle.HeadlightsOn)
            SendTunnel(connection, new AddEffectTagCompositeEffect(vehicle.Guid,
                VehicleDriverControls.HeadlightsEffect(vehicle.Definition.VehicleId)).WriteTo);
        if (vehicle.HornOn)
            SendTunnel(connection, new AddEffectTagCompositeEffect(vehicle.Guid,
                VehicleDriverControls.HornEffect(vehicle.Definition.VehicleId)).WriteTo);
        if (vehicle.SirenOn)
            SendTunnel(connection, new AddEffectTagCompositeEffect(vehicle.Guid,
                VehicleDriverControls.SirenEffect).WriteTo);
    }

    private void StopVehicleEngineForViewers(SoeConnection connection, GatewaySessionState state, MatchVehicle vehicle)
    {
        vehicle.EngineOn = false;
        KeyValuePair<GatewaySessionState, SoeConnection>[] viewers = _sharedLootMembership.TryGetValue(state, out ulong matchId)
            ? _sharedLootMatches[matchId].Members.ToArray() : [new(state, connection)];
        foreach (var (viewer, link) in viewers)
            if (link.State == ConnectionState.Open) StopDriverEngineRuntime(link, viewer, vehicle);
        SendVehicleControlToViewers(connection, state, vehicle, VehicleEngine.ServerIssued(vehicle.Guid, false).WriteTo);
    }

    private void SendVehicleControlToViewers(SoeConnection connection, GatewaySessionState state,
        MatchVehicle vehicle, Action<Cranberry.Protocol.PacketWriter> write)
    {
        KeyValuePair<GatewaySessionState, SoeConnection>[] viewers = _sharedLootMembership.TryGetValue(state, out ulong matchId)
            ? _sharedLootMatches[matchId].Members.ToArray() : [new(state, connection)];
        foreach (var (viewer, link) in viewers)
            if (link.State == ConnectionState.Open && (viewer == state || vehicle.SeatOf(viewer.Guid) >= 0
                || viewer.StreamedVehicles.IsSpawned(vehicle.Guid))) SendTunnel(link, write);
    }
}
