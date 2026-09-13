using System.Buffers.Binary;
using Cranberry.Protocol;

namespace Cranberry.Zone.Match;

// August 1148: native senders 1413f09e0/1413e9470 and 1413f1cf0/1413e9af0.
// These are MatchHistory packets (67), not the unrelated LeaderboardBase (8f).
public readonly record struct SelectLeaderboardRequest(ulong RequestId, MatchMode Mode, uint Tier, uint Division, bool AroundMe)
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out SelectLeaderboardRequest request)
    {
        request = default;
        if (packet.Length != 42 || packet[0] != 0x67 || packet[1] != 0x0a) return false;
        uint mode = BinaryPrimitives.ReadUInt32LittleEndian(packet[18..]);
        uint tier = BinaryPrimitives.ReadUInt32LittleEndian(packet[30..]);
        uint division = BinaryPrimitives.ReadUInt32LittleEndian(packet[34..]);
        uint filter = BinaryPrimitives.ReadUInt32LittleEndian(packet[38..]);
        if (mode is < 1 or > 3 || tier > 7 || division > 5 || filter > 1) return false;
        request = new(BinaryPrimitives.ReadUInt64LittleEndian(packet[2..]), LeaderboardPackets.Mode(mode), tier, division, filter == 0);
        return true;
    }
}

public readonly record struct PlayerTopTenRequest(ulong RequestId, ulong CharacterGuid, MatchMode Mode)
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out PlayerTopTenRequest request)
    {
        request = default;
        if (packet.Length != 26 || packet[0] != 0x67 || packet[1] != 0x1d) return false;
        uint mode = BinaryPrimitives.ReadUInt32LittleEndian(packet[18..]);
        ulong guid = BinaryPrimitives.ReadUInt64LittleEndian(packet[10..]);
        if (mode is < 1 or > 3 || guid == 0) return false;
        request = new(BinaryPrimitives.ReadUInt64LittleEndian(packet[2..]), guid, LeaderboardPackets.Mode(mode));
        return true;
    }
}

public static class LeaderboardPackets
{
    public static MatchMode Mode(uint value) => value switch { 1 => MatchMode.Solo, 2 => MatchMode.Duos, 3 => MatchMode.Fives, _ => MatchMode.Unknown };
    public static uint Category(MatchMode mode) => mode switch { MatchMode.Solo => 1, MatchMode.Duos => 2, MatchMode.Fives => 3, _ => 0 };

    /// <summary>1413f1210 case 0b, row reader 1413eb6d0, applier 141656b10.</summary>
    public static void WriteLeaderboard(PacketWriter writer, ulong requestId, ulong viewerGuid, string viewerAccountKey,
        IReadOnlyList<RankedLeaderboardEntry> rows)
    {
        if (rows.Count > RankedLeaderboard.PageSize) throw new ArgumentOutOfRangeException(nameof(rows));
        writer.WriteByte(0x67); writer.WriteByte(0x0b);
        writer.WriteUInt64(requestId); writer.WriteUInt64(viewerGuid);
        writer.WriteInt32(rows.Count);
        foreach (var row in rows)
        {
            writer.WriteUInt64(row.Identity.CharacterGuid);
            writer.WriteUInt32(row.Position);
            writer.WriteString(row.Identity.Name);
            writer.WriteUInt32((uint)row.Profile.Points);
            // Seven u32s after the name: Score and six reserved words. Older notes missed one.
            for (int i = 0; i < 6; i++) writer.WriteUInt32(0);
            writer.WriteBool(row.Identity.AccountKey == viewerAccountKey);
        }
    }

    /// <summary>1413eb340 -> 14165bc40 replaces MatchOtherPlayer and its best-ten table.</summary>
    public static void WritePlayerTopTen(PacketWriter writer, PlayerTopTenRequest request, RankedLeaderboardEntry? player)
    {
        writer.WriteByte(0x67); writer.WriteByte(0x1e);
        writer.WriteUInt64(request.RequestId); writer.WriteUInt64(request.CharacterGuid);
        writer.WriteString(player?.Identity.Name ?? string.Empty);
        writer.WriteUInt32(Category(request.Mode));
        writer.WriteUInt32((uint)(player?.Badge.Tier ?? RankedTier.Unranked));
        writer.WriteUInt32((uint)(player?.Badge.Division ?? 5));
        writer.WriteUInt32((uint)(player?.Profile.Points ?? 0));
        WriteBestTen(writer, player?.Profile);
    }

    public static void WriteBestTen(PacketWriter writer, RankedProfile? profile)
    {
        writer.WriteInt32(profile?.Best.Length ?? 0);
        if (profile is null) return;
        foreach (var result in profile.Best)
        {
            writer.WriteUInt64(0); // Match-detail lookup is a separate service.
            writer.WriteUInt32((uint)result.Points);
            writer.WriteUInt32(result.Placement); writer.WriteUInt32((uint)result.Kills); writer.WriteUInt32(0);
            writer.WriteUInt32(result.Placement); writer.WriteUInt32((uint)result.Kills); writer.WriteUInt32(0);
        }
    }
}

/// <summary>One bounded burst shared by all ranking requests on an authenticated connection.</summary>
public sealed class LeaderboardRequestBudget
{
    private double _tokens = 12;
    private long? _lastMs;
    public bool TryTake(long nowMs)
    {
        if (_lastMs is long last) _tokens = Math.Min(12, _tokens + Math.Max(0, nowMs - last) / 250.0);
        _lastMs = nowMs;
        if (_tokens < 1) return false;
        _tokens--;
        return true;
    }
}
