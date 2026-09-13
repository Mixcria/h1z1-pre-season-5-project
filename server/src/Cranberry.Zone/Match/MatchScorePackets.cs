using Cranberry.Protocol;

namespace Cranberry.Zone.Match;

/// <summary>67/08 populates retail MatchResult, used by the HUD and result slides.</summary>
public sealed record MatchScoreUpdate(ulong CharacterGuid, ulong MatchId, uint GameMode,
    uint Kills, uint Placement = 0, bool Finished = false, uint PlayerCount = 1,
    uint TeamSize = 1, uint? TeamCount = null, uint? TeamKills = null,
    uint? LivingTeamMembers = null, bool? PlayerFinished = null, uint? PlayerPlacement = null,
    uint GroupId = 0, uint Experience = 0)
{
    public const int Length = 109;
    public void WriteTo(PacketWriter w)
    {
        uint killPoints = checked(Kills * 1_000);
        uint teamKills = TeamKills ?? Kills;
        uint teamKillPoints = checked(teamKills * 1_000);
        uint rankPoints = Finished ? (uint)RankedScoring.PlacementPoints(Placement) : 0;
        w.WriteByte(0x67); w.WriteByte(8); w.WriteUInt64(CharacterGuid); w.WriteUInt64(MatchId);
        w.WriteUInt32(PlayerPlacement ?? Placement); w.WriteUInt32(Placement); w.WriteUInt32(rankPoints);
        w.WriteUInt32(Kills); w.WriteUInt32(killPoints); w.WriteUInt32(1_000);
        w.WriteUInt32(0); w.WriteUInt32(0); // individual assists, assist score
        w.WriteUInt32(checked(killPoints + rankPoints));
        w.WriteUInt32(teamKills); w.WriteUInt32(teamKillPoints); w.WriteUInt32(0); w.WriteUInt32(0);
        w.WriteUInt32(PlayerCount); w.WriteUInt32(GameMode); w.WriteUInt32(TeamSize);
        w.WriteUInt32(TeamCount ?? PlayerCount); w.WriteUInt32(GroupId); w.WriteUInt32(Experience);
        w.WriteBool(false); w.WriteUInt32(0); w.WriteUInt32(0); // trial, currency (not awarded here)
        w.WriteBool(PlayerFinished ?? Finished); w.WriteBool(Finished);
        w.WriteUInt32(LivingTeamMembers ?? (Finished ? 0u : 1u));
    }
}

public sealed record MatchRankUpdate(ulong CharacterGuid, ulong MatchId, RankBadge Badge)
{
    public void WriteTo(PacketWriter w)
    {
        w.WriteByte(0x67); w.WriteByte(9); w.WriteUInt64(CharacterGuid); w.WriteUInt64(MatchId);
        w.WriteUInt32((uint)Badge.Tier); w.WriteUInt32((uint)Badge.Division);
    }
}
