using Cranberry.Transport;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Commands;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private bool TryHandleNativeVehicleCommand(
        SoeConnection connection, GatewaySessionState state, byte[] payload)
    {
        if (!NativeVehicleCommand.Matches(payload)) return false;
        if (!TryAuthorizeNativeConsole(connection, state, "vehicle")) return true;
        NativeVehicleCommand request = NativeVehicleCommand.Parse(payload);
        ConsoleReply reply;
        if (VehicleCommands.ResolveVehicle(request.VehicleId.ToString(System.Globalization.CultureInfo.InvariantCulture)) is null)
            reply = ConsoleReply.Usage("/vehicle <id> -- 1 offroader, 2 pickup, 3 policecar, 5 atv; /vehicle list shows the client roster");
        else if (state.ChuteGuid != 0)
            reply = ConsoleReply.Failed("land before spawning a vehicle");
        else if (request.RewardSetId != 0 || request.FactionId != 0 || request.Unknown != 0 || request.TrailingUnknown != 0)
            reply = ConsoleReply.Failed("vehicle reward sets, factions and extra spawn options are not implemented; use /vehicle <id> 0 [auto mount]");
        else
            // The native client already raycasts the aimed-at surface. Preserve its position and
            // heading instead of substituting the fallback /car offset or stale channel-2 pose.
            reply = SpawnConsoleVehicleAt(connection, state, request.VehicleId,
                request.Position, request.Yaw, request.AutoMount);
        NativeConsoleReply(connection, state, reply);
        _log.Info($"{connection} native console: /vehicle id={request.VehicleId} "
            + $"position={FormatPosition(request.Position)} yaw={request.Yaw} autoMount={request.AutoMount} "
            + $"faction={request.FactionId} -> {string.Join("; ", reply.Lines)}");
        return true;
    }
}
