using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private sealed record MenuVehiclePreview(ulong Guid, uint VehicleId, LightweightToFullVehicle FullState);

    /// <summary>Apply the selected/default paint after a successful driver ownership transition.</summary>
    private void ApplyDriverVehicleSkin(SoeConnection connection, GatewaySessionState state, MatchVehicle vehicle)
    {
        if (vehicle.DriverGuid != state.Guid || vehicle.OwnerGuid != state.Guid) return;
        uint shader = state.VehicleSkins.ShaderFor(vehicle.Definition.VehicleId);
        if (vehicle.SkinShaderGroup == shader) return;

        // Retain the last driver's choice for exit and later stream-ins. Selection ownership
        // is validated by the existing menu/economy reconciliation path, per character.
        vehicle.SkinShaderGroup = shader;
        // Preserve the existing zero-shader policy: a native reset for those families is unproven.
        if (shader != 0)
            SendVehicleControlToViewers(connection, state, vehicle, writer =>
                VehicleSkinPackets.WriteNotify(writer, vehicle.Guid, state.Guid, shader));
    }

    private void HandleVehicleSkinRequest(SoeConnection connection, GatewaySessionState state, ReadOnlySpan<byte> payload)
    {
        var request = VehicleSkinRequest.Parse(payload);
        switch (request.SubOpcode)
        {
            case 2:
                if (_economy is not null && !ReadAccountEconomy(state).Owns(request.ItemId))
                {
                    _log.Warn($"{connection} refused unowned vehicle skin {request.ItemId}");
                    return;
                }
                if (!state.VehicleSkins.Set(request.VehicleId, request.ModPoint, request.ItemId))
                {
                    _log.Warn($"{connection} refused incompatible vehicle skin {request.VehicleId}/{request.ModPoint}/{request.ItemId}");
                    return;
                }
                state.VehicleSkins.Save(_options.Skins.WardrobeStoreRoot, state.Guid, _log.Warn);
                SendVehicleSkinManager(connection, state);
                ApplyPreviewVehicleSkin(connection, state);
                return;
            case 3:
                state.VehicleSkins.Unset(request.VehicleId, request.ModPoint);
                state.VehicleSkins.Save(_options.Skins.WardrobeStoreRoot, state.Guid, _log.Warn);
                SendVehicleSkinManager(connection, state);
                ApplyPreviewVehicleSkin(connection, state);
                return;
            case 4:
                SendVehicleSkinManager(connection, state);
                return;
            case 6:
                if (state.Match != MatchStep.Menu) return;
                SelectPreviewVehicle(connection, state, request.VehicleId);
                return;
        }
    }

    private void SendVehicleSkinManager(SoeConnection connection, GatewaySessionState state) =>
        SendTunnel(connection, writer => VehicleSkinPackets.WriteManager(writer, state.VehicleSkins.Snapshot()));

    private void ApplyPreviewVehicleSkin(SoeConnection connection, GatewaySessionState state)
    {
        if (state.VehiclePreview is not { } preview) return;
        uint shader = state.VehicleSkins.ShaderFor(preview.VehicleId);
        if (shader != 0)
            SendTunnel(connection, writer => VehicleSkinPackets.WriteNotify(writer, preview.Guid, state.Guid, shader));
        else
            // Recreate the base mesh: sending no shader left the previously selected paint visible.
            SelectPreviewVehicle(connection, state, preview.VehicleId);
    }

    private void SelectPreviewVehicle(SoeConnection connection, GatewaySessionState state, uint vehicleId)
    {
        if (state.VehiclePreview is { } previous)
        {
            SendTunnel(connection, writer => VehicleSkinPackets.WritePreviewResponse(writer, 0));
            SendTunnel(connection, new RemovePlayer(previous.Guid).WriteTo);
            state.VehiclePreview = null;
        }
        if (vehicleId == 0)
        {
            SendTunnel(connection, writer => VehicleSkinPackets.WritePreviewResponse(writer, 0));
            return;
        }
        if (vehicleId is not (1 or 2 or 5 or 13))
        {
            SendTunnel(connection, writer => VehicleSkinPackets.WritePreviewResponse(writer, 0));
            return;
        }
        // The 2017 reference menu's offroader mark, decoded from its 16:49:47.791 spawn.
        var position = new Vector3(-19.94f, 506.45f, 276.45f);
        uint sequence = ++state.VehiclePreviewSequence;
        ulong guid = 0x4700_0000_0000_0000ul + sequence;
        uint transientId = 3_000_000 + sequence;
        bool parachute = vehicleId == 13;
        MatchVehicle? vehicle = parachute ? null : new MatchVehicle(guid, transientId,
            VehicleRosterData.Value.Require(vehicleId), position, 0f, 100_000, 7_500);
        uint modelId = vehicle?.Definition.ModelId ?? _options.ParachuteModelId;
        var preview = new MenuVehiclePreview(guid, vehicleId, vehicle is null
            ? new LightweightToFullVehicle(transientId, guid) : VehicleFullState.Create(vehicle));
        state.VehiclePreview = preview;
        SendVehicleSkinManager(connection, state);
        SendTunnel(connection, new AddLightweightVehicle(guid, transientId,
            modelId, position, vehicle?.Rotation ?? new Vector4(0, 0, 0, 1), vehicleId, 0,
            PositionUpdate: PositionUpdateBlock.AtRest(position, 0),
            ShaderParameterGroupId: parachute ? _options.ParachuteShaderParameterGroupId : 0,
            BodyShaderGroupId: state.VehicleSkins.ShaderFor(vehicleId)).WriteTo);
        SendTunnel(connection, preview.FullState.WriteTo);
        // The selection callback resolves an actor GUID; publish it after the actor exists.
        SendTunnel(connection, writer => VehicleSkinPackets.WritePreviewResponse(writer, guid));
        _log.Info($"{connection} vehicle preview {vehicleId}: actor {guid}, model {modelId}");
    }
}
