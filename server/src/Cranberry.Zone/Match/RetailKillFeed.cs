using Cranberry.Protocol;

namespace Cranberry.Zone.Match;

public sealed record KillFeedPlayer(ulong Guid, string Name, RankBadge Badge)
{
    internal void WriteTo(PacketWriter w)
    {
        w.WriteUInt64(Guid);
        w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt32(0);
        w.WriteString(Name); w.WriteString(string.Empty); w.WriteString(string.Empty); w.WriteString(string.Empty);
        w.WriteUInt64(0);
        w.WriteByte(0); // signed compact integer, record+0x120
        w.WriteUInt32(0); w.WriteUInt32(0);
        w.WriteUInt32((uint)Badge.Tier); w.WriteUInt32((uint)Badge.Division);
    }
}

/// <summary>August ce/000e, FUN_140bb0310 → 140bb9290 → OnKillSpamGameMode.</summary>
public sealed record RetailKillFeed(KillFeedPlayer Victim, KillFeedPlayer? Killer,
    uint WeaponItemId = 0, uint DamageCause = 0, bool Headshot = false, string DeathMessage = "",
    string SelfDeathMessage = "")
{
    public void WriteTo(PacketWriter w)
    {
        w.WriteByte(0xce); w.WriteUInt16(0x0e);
        w.WriteByte(Killer is null ? (byte)0 : (byte)8); // signed compact integer 0/1
        w.WriteUInt32(Killer is null ? 1u : 2u);
        Victim.WriteTo(w); Killer?.WriteTo(w); // victim FIRST, killer SECOND
        // FUN_140bb9290 resolves this item's IMAGE_SET_ID for the feed icon, including skins.
        w.WriteUInt32(WeaponItemId); w.WriteUInt32(DamageCause);
        w.WriteUInt32(0); w.WriteUInt32(0); w.WriteSingle(0); w.WriteBool(Headshot);
        w.WriteString(Killer is not null && DeathMessage.Length == 0 ? "BR.PlayerKilledPlayerWith" : DeathMessage);
        w.WriteString(SelfDeathMessage);
    }
}
