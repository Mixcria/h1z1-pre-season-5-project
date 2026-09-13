using Cranberry.Protocol;

namespace Cranberry.Zone.Progression;

/// <summary>
/// One SetExperience award, read by August <c>FUN_140cf7410</c>: four u32 values, a victim
/// GUID and a counted UTF-8 victim name. Award 1 resolves to the retail "Killer" message.
/// </summary>
public sealed record ExperienceAward(
    uint Amount,
    uint Count,
    uint AwardId,
    uint SecondaryId,
    ulong VictimGuid,
    string VictimName)
{
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt32(Amount);
        writer.WriteUInt32(Count);
        writer.WriteUInt32(AwardId);
        writer.WriteUInt32(SecondaryId);
        writer.WriteUInt64(VictimGuid);
        writer.WriteString(VictimName);
    }
}

/// <summary>
/// August <c>Experience.SetExperienceRanks</c> (87 02). The native parsers
/// <c>FUN_140cf7720/140cf6d00</c> read a counted set of rank tables, with one XP threshold and
/// exactly four reward descriptors per rank. Table 0 backs the AccountExperience UI.
/// </summary>
public sealed record SetExperienceRanks(IReadOnlyList<uint> Thresholds)
{
    public const byte Opcode = ZoneOpcodes.ExperienceBase;
    public const byte SubOpcode = 0x02;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(Thresholds);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteInt32(1); // One account progression table.
        writer.WriteUInt32(0); // Table key.
        writer.WriteInt32(Thresholds.Count);
        foreach (uint threshold in Thresholds)
        {
            writer.WriteUInt32(threshold);
            for (int descriptor = 0; descriptor < 4; descriptor++)
            {
                writer.WriteUInt32(0); // Name string ID.
                writer.WriteUInt32(0); // Reward set ID.
                writer.WriteUInt32(0); // Image set ID.
                writer.WriteInt32(0); // No reward item rows.
            }
        }
    }
}
