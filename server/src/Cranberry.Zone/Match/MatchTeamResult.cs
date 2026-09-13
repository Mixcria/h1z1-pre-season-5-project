using Cranberry.Protocol;

namespace Cranberry.Zone.Match;

/// <summary>
/// August 67/22 replaces MatchTeamMember, the Duos/Fives end-screen member badges.
/// FUN_1413ed8e0 reads the list; FUN_1413ec340 reads each row; FUN_1416626b0 applies it.
/// The first u64 after Name is unused by the table applier and retains its constructor zero.
/// </summary>
public sealed record MatchTeamResult(IReadOnlyList<MatchTeamResultMember> Members)
{
    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(0x67);
        writer.WriteByte(0x22);
        writer.WriteUInt32(checked((uint)Members.Count));
        foreach (var member in Members)
        {
            writer.WriteString(member.Name);
            writer.WriteUInt64(0);
            writer.WriteUInt64(member.CharacterGuid);
            writer.WriteUInt32(member.Rank);
            writer.WriteUInt32(member.Kills);
            writer.WriteUInt32(checked(member.Kills * 1_000));
            writer.WriteUInt32(0); // assists are not yet tracked
            writer.WriteUInt32(0);
            writer.WriteUInt32(member.TotalScore);
            writer.WriteBool(member.IsMyself);
        }
    }
}

public sealed record MatchTeamResultMember(string Name, ulong CharacterGuid, uint Rank,
    uint Kills, uint TotalScore, bool IsMyself);

/// <summary>
/// ce/1a prepares the current world's MatchEndWindow panel list, including native groups.
/// FUN_140bbb020 consumes exactly u8/u16/bool, then calls FUN_1413dcc20; the bool is not
/// consumed by that callee. Finished 67/08 triggers the window only after this preparation.
/// </summary>
public static class PrepareTeamResultScreen
{
    public static void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(0xce);
        writer.WriteUInt16(0x1a);
        writer.WriteBool(true);
    }
}
