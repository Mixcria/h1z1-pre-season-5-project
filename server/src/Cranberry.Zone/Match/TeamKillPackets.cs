using Cranberry.Protocol;

namespace Cranberry.Zone.Match;

/// <summary>
/// August ClientUpdate 11/4a: u32 metric, u32 value, u64 character. Metric 1 is
/// cumulative player kills (140a342a0, 140afc660 case 4a, 140d79520). The group
/// manager stores it separately from the roster; a member refresh publishes it
/// to the GroupMembers.PlayerKills column used by the squad HUD.
/// </summary>
public sealed record PlayerKillCountUpdate(ulong CharacterGuid, uint Kills)
{
    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(0x11);
        writer.WriteUInt16(0x004a);
        writer.WriteUInt32(1);
        writer.WriteUInt32(Kills);
        writer.WriteUInt64(CharacterGuid);
    }
}
