using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private bool TryHandleNativeGotoCommand(
        SoeConnection connection, GatewaySessionState state, byte[] payload)
    {
        if (!NativeGotoRequest.Matches(payload)) return false;
        if (!TryAuthorizeNativeConsole(connection, state, "goto")) return true;
        NativeGotoRequest request;
        try
        {
            request = NativeGotoRequest.Parse(payload);
        }
        catch (PacketFormatException)
        {
            NativeConsoleReply(connection, state, ConsoleReply.Failed("malformed native /goto request"));
            return true;
        }

        ConsoleReply reply;
        if (state.Fleet?.TryGetForOccupant(state.Guid, out _) == true)
            reply = ConsoleReply.Failed("exit your vehicle before using /goto");
        else if (state.ChuteGuid != 0)
            reply = ConsoleReply.Failed("land before using /goto");
        else if (request.Waypoint)
            reply = ConsoleReply.Failed("waypoint positions are not tracked by this server; use /goto <player name or guid>");
        else if (request.NpcDefinitionId != 0)
            // Existing server NPCs all advertise definition id zero. A model id or
            // vehicle family id must not be substituted for a native NPC definition.
            reply = ConsoleReply.Failed($"no spawned NPC has definition {request.NpcDefinitionId}; /goto npc accepts a spawned target name or guid");
        else
            reply = NativeGotoTarget(connection, state, request);

        NativeConsoleReply(connection, state, reply);
        return true;
    }

    private ConsoleReply NativeGotoTarget(
        SoeConnection connection, GatewaySessionState state, NativeGotoRequest request)
    {
        var candidates = new List<(ulong Guid, string Name, Vector3 Position)>();
        foreach (var peer in _throwableSessions.Values)
        {
            if (peer.Connection.State != ConnectionState.Open || peer.State.Match != MatchStep.InMatch
                || (!ReferenceEquals(peer.State, state)
                    && (state.BountyAdmission.MatchId == 0
                        || peer.State.BountyAdmission.MatchId != state.BountyAdmission.MatchId
                        || peer.State.BountyWorldId != state.BountyWorldId))) continue;
            MatchVehicle? occupied = peer.State.Fleet?.TryGetForOccupant(peer.State.Guid, out var vehicle) == true
                ? vehicle : null;
            if ((occupied?.Position ?? peer.State.Movement.Player?.Position) is Vector3 position)
                candidates.Add((peer.State.Guid, peer.State.CharacterName, position));
        }
        foreach (var target in state.Combat.Targets.All)
            candidates.Add((target.WorldGuid, target.Name, target.Position));
        if (state.Fleet is { } fleet)
            foreach (var vehicle in fleet.Vehicles.Where(vehicle => vehicle.Health > 0))
                candidates.Add((vehicle.Guid, vehicle.Definition.Name, vehicle.Position));

        List<(ulong Guid, string Name, Vector3 Position)> matches;
        if (request.TargetGuid != 0)
            matches = candidates.Where(candidate => candidate.Guid == request.TargetGuid).ToList();
        else if (!string.IsNullOrWhiteSpace(request.TargetName))
        {
            matches = candidates.Where(candidate => candidate.Name.Equals(
                request.TargetName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
                matches = candidates.Where(candidate => candidate.Name.Contains(
                    request.TargetName, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else
            return ConsoleReply.Failed("no admin target selected; use /goto <player or NPC name/guid>");

        if (matches.Count == 0)
            return ConsoleReply.Failed("goto target not found in your current match");
        if (matches.Count > 1)
            return ConsoleReply.Failed("goto target is ambiguous; use its full name or guid");
        return ConsoleTeleport(connection, state, matches[0].Position);
    }
}
