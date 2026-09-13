using Cranberry.Transport;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private void SendTeamResults(SoeConnection connection, GatewaySessionState state, uint placement)
    {
        if (!IsTeamMode(state)) return;
        uint placementPoints = checked((uint)RankedScoring.PlacementPoints(placement));
        var result = new MatchTeamResult([.. TeamMembers(state).Select(member =>
            new MatchTeamResultMember(member.CharacterName, member.Guid,
                member.Score.PlayerPlacement ?? placement, checked((uint)member.Score.Kills),
                checked((uint)member.Score.KillPoints + placementPoints), member.Guid == state.Guid))]);
        // Final 67/08 opens the native group results UI. Its member table must already exist.
        SendTunnel(connection, PrepareTeamResultScreen.WriteTo);
        SendTunnel(connection, result.WriteTo);
    }
}
