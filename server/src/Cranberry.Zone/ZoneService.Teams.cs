using Cranberry.Protocol;
using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // Server-owned matchmaking policy: fill the earliest incomplete squad, never move a member
    // because another player leaves, and retain eliminated members until they leave the match.
    private readonly Dictionary<GatewaySessionState, GasPackets.DeathInfo> _pendingTeamDeaths = [];
    private readonly Dictionary<GatewaySessionState, int> _teamsRemainingSent = [];
    private readonly Dictionary<GatewaySessionState, long> _teamPoseSent = [];

    private uint ClientTeamId(GatewaySessionState state) => 0x4000_0000u + checked((uint)TeamGuid(state));

    private GroupMemberHudStatus TeamHudStatus(GatewaySessionState state)
    {
        byte slot = _sharedLootMatches[_sharedLootMembership[state]].TeamColorSlots[state];
        bool driving = state.Fleet?.TryGetForOccupant(state.Guid, out var vehicle) == true
            && vehicle.DriverGuid == state.Guid;
        byte health = (byte)((ulong)Math.Min(state.Hitpoints, _options.Gas.MaxHitpoints) * 100 / _options.Gas.MaxHitpoints);
        var equipment = state.Inventory?.EquipmentSlots;
        uint heldItem = equipment?.TryGetValue(BodySlots.RightHand, out var held) == true
            ? held.DefinitionId : PlayerInventory.SurvivorFistsItemDefinitionId;
        bool armor = equipment?.Values.Any(item => ArmourModel.TierOf(item.DefinitionId) != ArmourTier.None) == true;
        bool helmet = equipment?.Values.Any(item => ArmourModel.IsHelmet(item.DefinitionId)) == true;
        return new(state.Guid, slot, state.DeathSent || state.Hitpoints == 0, driving, health,
            heldItem, armor, helmet);
    }

    private void PublishTeamHudStatus(GatewaySessionState state)
    {
        if (!IsTeamMode(state) || !_sharedLootMembership.TryGetValue(state, out ulong id)) return;
        var status = TeamHudStatus(state);
        foreach (var member in TeamMembers(state))
            if (_sharedLootMatches[id].Members.TryGetValue(member, out var connection)
                && connection.State == ConnectionState.Open)
                SendTunnel(connection, status.WriteTo);
    }

    private GroupRoster TeamRoster(GatewaySessionState state)
    {
        var members = TeamMembers(state);
        return new(ClientTeamId(state), members[0].Guid,
            [.. members.Select((member, index) => new GroupMember(member.Guid, member.CharacterName,
                WorldStreamPosition(member) ?? Vector3.Zero, (uint)index))]);
    }

    private void SendTeamRoster(SoeConnection connection, GatewaySessionState state)
    {
        if (!IsTeamMode(state) || !_sharedLootMembership.ContainsKey(state)) return;
        var roster = TeamRoster(state);
        SendTunnel(connection, roster.WriteTo);
        SendTeamRosterKills(connection, state, roster);
    }

    private void PublishTeamRoster(GatewaySessionState state)
    {
        if (!IsTeamMode(state) || !_sharedLootMembership.TryGetValue(state, out ulong id)) return;
        var roster = TeamRoster(state);
        foreach (var member in TeamMembers(state))
            if (_sharedLootMatches[id].Members.TryGetValue(member, out var connection)
                && connection.State == ConnectionState.Open
                && member.Match is MatchStep.Lobby or MatchStep.Dropping or MatchStep.InMatch or MatchStep.Ended)
            {
                SendTunnel(connection, roster.WriteTo);
                SendTeamRosterKills(connection, state, roster);
            }
    }

    private void PublishTeamPose(GatewaySessionState state)
    {
        if (!IsTeamMode(state) || !_sharedLootMembership.TryGetValue(state, out ulong id)
            || state.Match != MatchStep.InMatch || state.DeathSent) return;
        long now = Environment.TickCount64;
        if (_teamPoseSent.TryGetValue(state, out long previous) && now - previous < 1000) return;
        _teamPoseSent[state] = now;
        var members = TeamMembers(state);
        var position = state.Fleet?.TryGetForOccupant(state.Guid, out var car) == true
            ? car.Position : WorldStreamPosition(state) ?? Vector3.Zero;
        var update = new GroupMemberUpdate(new(state.Guid, state.CharacterName,
            position, (uint)Array.IndexOf(members, state)));
        var status = TeamHudStatus(state);
        foreach (var member in members)
            if (_sharedLootMatches[id].Members.TryGetValue(member, out var connection)
                && connection.State == ConnectionState.Open)
            {
                SendTunnel(connection, update.WriteTo);
                // Rebuilding a GroupMembers row omits UiColor. Restore status after the row.
                SendTunnel(connection, status.WriteTo);
            }
    }

    private static int TeamSize(GatewaySessionState state) => state.BountyAdmission.Mode switch
    {
        MatchMode.Duos => 2,
        MatchMode.Fives => 5,
        _ => 1,
    };

    private static bool IsTeamMode(GatewaySessionState state) => TeamSize(state) > 1;

    private ulong TeamGuid(GatewaySessionState state) =>
        _sharedLootMembership.TryGetValue(state, out ulong id)
            && _sharedLootMatches.TryGetValue(id, out var match)
            && match.Teams.TryGetValue(state, out ulong team) ? team : state.Guid;

    private GatewaySessionState[] TeamMembers(GatewaySessionState state)
    {
        if (!_sharedLootMembership.TryGetValue(state, out ulong id)) return [state];
        var match = _sharedLootMatches[id];
        ulong team = TeamGuid(state);
        return [.. match.Members.Keys.Where(member => match.Teams.GetValueOrDefault(member) == team)];
    }

    private bool AreTeammates(GatewaySessionState left, GatewaySessionState right) =>
        IsTeamMode(left) && left.BountyAdmission.MatchId != 0
        && left.BountyAdmission.MatchId == right.BountyAdmission.MatchId
        && _sharedLootMembership.ContainsKey(left) && _sharedLootMembership.ContainsKey(right)
        && TeamGuid(left) == TeamGuid(right);

    private static bool IsSharedAlive(GatewaySessionState state, SoeConnection connection) =>
        connection.State == ConnectionState.Open && !state.DeathSent
        && state.Match != MatchStep.Menu && (state.Match != MatchStep.Ended || state.VictorySent);

    private int CountSharedTeams(SharedLootMatch match) => match.Members
        .Where(pair => IsSharedAlive(pair.Key, pair.Value))
        .Select(pair => match.Teams[pair.Key]).Distinct().Count() + (CountCombatBots(match) > 0 ? 1 : 0);

    private uint TeamForfeitPlacement(GatewaySessionState state)
    {
        if (!_sharedLootMembership.TryGetValue(state, out ulong id)) return 1;
        ulong own = TeamGuid(state);
        var match = _sharedLootMatches[id];
        return (uint)(1 + match.Members.Where(pair => IsSharedAlive(pair.Key, pair.Value))
            .Select(pair => match.Teams[pair.Key]).Where(team => team != own).Distinct().Count());
    }

    private void AssignMatchTeam(SharedLootMatch match, GatewaySessionState state)
    {
        int size = TeamSize(state);
        ulong team = 0;
        uint partyId = state.MatchPartyId;
        if (size > 1)
        {
            // Party members can arrive on different transfer acknowledgements. Reserve their
            // seats from the first arrival so an intervening solo entrant cannot split them.
            if (partyId != 0 && match.PartyTeams.TryGetValue(partyId, out var reservation))
                team = reservation.Team;
            if (team == 0)
            {
                int needed = partyId == 0 ? 1 : Math.Max(1, state.MatchPartySize);
                team = match.Teams.GroupBy(pair => pair.Value)
                    .Where(group => group.All(pair => pair.Key.BountyAdmission.Mode == state.BountyAdmission.Mode
                            && !pair.Key.DeathSent)
                        && group.Count(pair => pair.Key.MatchPartyId == 0)
                            + match.PartyTeams.Values.Where(value => value.Team == group.Key).Sum(value => value.Size)
                            + needed <= size)
                    .Select(group => group.Key).FirstOrDefault();
            }
        }
        if (team == 0) team = ++match.NextTeam;
        if (size > 1 && partyId != 0)
            match.PartyTeams.TryAdd(partyId, (team, Math.Max(1, state.MatchPartySize)));
        match.Teams.Add(state, team);
        // Color is a one-based native slot, independent of current roster ordering.
        // Removing a player must not recolor surviving teammates.
        var occupied = match.Teams.Where(pair => pair.Value == team && !ReferenceEquals(pair.Key, state))
            .Select(pair => match.TeamColorSlots[pair.Key]).ToHashSet();
        match.TeamColorSlots.Add(state, Enumerable.Range(1, size).Select(slot => (byte)slot)
            .First(slot => !occupied.Contains(slot)));
        match.HadMultipleTeams |= match.Teams.Values.Distinct().Skip(1).Any();
    }

    private void WriteMatchPopulation(PacketWriter writer, GatewaySessionState state)
    {
        if (IsTeamMode(state) && _sharedLootMembership.TryGetValue(state, out ulong id))
            new TeamsRemaining(AliveCount(state), CountSharedTeams(_sharedLootMatches[id])).WriteTo(writer);
        else
            GameModeHud.WritePlayersRemaining(writer, AliveCount(state));
    }

    private bool PublishTeamPopulation(SoeConnection connection, GatewaySessionState state, int alive)
    {
        if (!IsTeamMode(state) || !_sharedLootMembership.TryGetValue(state, out ulong id)) return false;
        int teams = CountSharedTeams(_sharedLootMatches[id]);
        if (state.AliveSent == alive && _teamsRemainingSent.GetValueOrDefault(state, -1) == teams) return true;
        state.AliveSent = alive;
        _teamsRemainingSent[state] = teams;
        SendTunnel(connection, new TeamsRemaining(alive, teams).WriteTo);
        if (!state.Score.Settled) PublishScore(connection, state);
        return true;
    }

    private bool DeferTeamDeath(GatewaySessionState state, GasPackets.DeathInfo info)
    {
        if (!IsTeamMode(state) || !_sharedLootMembership.ContainsKey(state)) return false;
        _pendingTeamDeaths[state] = info;
        state.Match = MatchStep.Ended;
        state.EndedAtMs = 0; // Await the team's result; no automatic return while teammates play.
        return true;
    }

    private void ResolveTeamResults(SharedLootMatch match)
    {
        var aliveTeams = match.Members.Where(pair => IsSharedAlive(pair.Key, pair.Value))
            .Select(pair => match.Teams[pair.Key]).ToHashSet();
        if (CountCombatBots(match) > 0) aliveTeams.Add(ulong.MaxValue); // Hostile bot team.
        foreach (var (member, connection) in match.Members.ToArray())
        {
            if (!_pendingTeamDeaths.TryGetValue(member, out var death)
                || aliveTeams.Contains(match.Teams[member])) continue;
            _pendingTeamDeaths.Remove(member);
            uint placement = (uint)aliveTeams.Count + 1;
            if (connection.State == ConnectionState.Open)
            {
                SendTunnel(connection, (death with { Rank = (int)placement - 1 }).WriteTo);
                PublishAliveCount(connection, member, CountSharedAlive(match));
            }
            CompleteBountyResult(connection, member, placement);
            CompleteRankedScore(connection, member, placement);
            BeginEndedHold(connection, member, "team eliminated");
        }
        if (aliveTeams.Count != 1 || !match.HadMultipleTeams) return;
        ulong winner = aliveTeams.Single();
        foreach (var (member, connection) in match.Members.ToArray())
        {
            if (!IsTeamMode(member) || match.Teams[member] != winner
                || connection.State != ConnectionState.Open || member.Score.Settled
                || member.Match is not (MatchStep.Dropping or MatchStep.InMatch or MatchStep.Ended)) continue;
            SendVictory(connection, member);
            _pendingTeamDeaths.Remove(member);
        }
    }
}
