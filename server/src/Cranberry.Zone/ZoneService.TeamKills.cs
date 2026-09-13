using Cranberry.Transport;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private void PublishKillScore(SoeConnection connection, GatewaySessionState state, ulong victim, string victimName)
    {
        AwardKillExperience(connection, state, victim, victimName);
        PublishScore(connection, state);
        if (!IsTeamMode(state) || !_sharedLootMembership.TryGetValue(state, out ulong id)) return;

        var members = TeamMembers(state);
        var changed = TeamRoster(state).Members.Single(member => member.CharacterGuid == state.Guid);
        // Every member's MatchResult includes the squad total. A practice target's death
        // does not change player population, so population updates cannot refresh it.
        foreach (var member in members)
            if (_sharedLootMatches[id].Members.TryGetValue(member, out var link)
                && link.State == ConnectionState.Open)
            {
                SendTeamMemberKills(link, changed, state);
                if (!ReferenceEquals(member, state) && !member.Score.Settled)
                    PublishScore(link, member);
            }
    }

    private void SendTeamMemberKills(SoeConnection connection, GroupMember member, GatewaySessionState state)
    {
        // 11/4a changes the native map; 13/14 refreshes the SQL-backed name row.
        SendTunnel(connection, new PlayerKillCountUpdate(member.CharacterGuid, checked((uint)state.Score.Kills)).WriteTo);
        SendTunnel(connection, new GroupMemberUpdate(member).WriteTo);
        SendTunnel(connection, TeamHudStatus(state).WriteTo);
    }

    private void SendTeamRosterKills(SoeConnection connection, GatewaySessionState state, GroupRoster roster)
    {
        var states = TeamMembers(state).ToDictionary(member => member.Guid);
        // A new client needs the whole snapshot, including zero counts for a new match.
        // The roster must precede this: the native stat handler requires group membership.
        foreach (var member in roster.Members)
            SendTeamMemberKills(connection, member, states[member.CharacterGuid]);
    }
}
