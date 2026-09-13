using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private void SendHostedSpectatorRoster(SoeConnection connection, GatewaySessionState state, bool force = false)
    {
        if (!state.HostedObserverActive || !CanHostedObserve(connection, state,
                ResolveConsoleTier(connection, state) >= ConsoleTier.Owner, state.BountyWorldId)
            || _hostedGames.GetGame(state.BountyWorldId) is not { IsActive: true } game
            || HostedMode(game.GameModeId) != state.BountyAdmission.Mode)
        {
            ClearHostedSpectatorRoster(connection, state);
            return;
        }
        long now = Environment.TickCount64;
        if (!force && now < state.NextHostedSpectatorRosterMs) return;
        var players = new List<SpectatorPlayerRow>();
        foreach (var member in HostedMembers(game).Select(pair => pair.Value)
            .Where(member => member.BountyAdmission.MatchId == state.BountyAdmission.MatchId
                && member.BountyAdmission.Mode == state.BountyAdmission.Mode && !member.LogoutPrepared
                && member.Match is MatchStep.InMatch or MatchStep.Ended).OrderBy(member => member.Guid))
        {
            // Observe the character's body, never another admin's private free-camera pose.
            MatchVehicle? occupied = !member.HostedObserverActive
                && member.Fleet?.TryGetForOccupant(member.Guid, out MatchVehicle? seated) == true ? seated : null;
            Vector3? position = occupied?.Position ?? (member.HostedObserverActive ? member.Movement.Player?.Position : WorldStreamPosition(member));
            if (position is not Vector3 at || !float.IsFinite(at.X) || !float.IsFinite(at.Y) || !float.IsFinite(at.Z)) continue;
            byte health = member.DeathSent || member.VictorySent || member.Match == MatchStep.Ended || member.Hitpoints == 0
                ? (byte)0 : (byte)Math.Clamp(Math.Ceiling(member.Hitpoints * 100d / _options.Gas.MaxHitpoints), 1, 100);
            string name = member.CharacterName;
            if (name.Length > 80) name = name[..80];
            players.Add(new(member.Guid, name, health,
                SpectatorRosterPacket.HeadingFromRadians(occupied?.Yaw ?? member.Movement.Player?.Orientation ?? 0),
                SpectatorRosterPacket.Coordinate(at.X), SpectatorRosterPacket.Coordinate(at.Y), SpectatorRosterPacket.Coordinate(at.Z)));
            if (players.Count == SpectatorRosterPacket.MaximumPlayers) break;
        }
        SendTunnel(connection, new SpectatorRosterPacket(players).WriteTo);
        state.HostedSpectatorRosterSent = true;
        state.NextHostedSpectatorRosterMs = now + 1000;
    }

    private void ClearHostedSpectatorRoster(SoeConnection connection, GatewaySessionState state)
    {
        if (state.HostedSpectatorRosterSent && connection.State == ConnectionState.Open)
            SendTunnel(connection, new SpectatorRosterPacket([]).WriteTo);
        state.HostedSpectatorRosterSent = false;
        state.NextHostedSpectatorRosterMs = 0;
    }
}
