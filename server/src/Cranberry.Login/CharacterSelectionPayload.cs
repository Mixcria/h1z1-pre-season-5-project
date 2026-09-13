using Cranberry.Protocol;

namespace Cranberry.Login;

/// <summary>
/// The game-facing blob inside one <see cref="CharacterEntry"/>. The fixed prefix is consumed
/// by `FUN_140f0cff0`; the field roles used below are confirmed by its listener
/// (`FUN_140f192a0`) and the character data-source column switch at `FUN_14156f1a0`.
/// The two nested collections are intentionally empty until their element layouts are needed.
/// </summary>
public sealed record CharacterSelectionPayload(
    string Name,
    byte EmpireId,
    uint BattleRank,
    uint NextBattleRankPercent,
    uint HeadId,
    uint ModelId,
    uint Gender,
    uint ProfileId,
    uint Field7,
    uint Field8,
    ulong Field9)
{
    /// <summary>The shader-parameter group selected by the character-create screen.</summary>
    public uint SkinToneId => Field7;

    /// <summary>The row selected from the client's <c>HairMappings.txt</c>.</summary>
    public uint HairId => Field8;

    public static CharacterSelectionPayload FromCreate(CharacterCreatePayload create) =>
        new(
            Name: create.Name,
            EmpireId: create.EmpireId,
            BattleRank: 1,
            NextBattleRankPercent: 0,
            HeadId: create.HeadId,
            // The create UI calls this value Profile. Using it for both client-facing columns
            // is provisional until a live roster shows which model definition the UI selects.
            ModelId: create.ProfileId,
            Gender: create.Gender,
            ProfileId: create.ProfileId,
            Field7: create.SkinToneId,
            Field8: create.HairId,
            Field9: 0);

    /// <summary>
    /// Parses the game-facing roster blob written by <see cref="WriteTo"/>. The two nested
    /// collections are deliberately required to be empty: their element readers have not yet
    /// been derived for ClientProtocol_1148, and accepting a non-zero count would silently shift
    /// every appearance field that follows it.
    /// </summary>
    public static CharacterSelectionPayload Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        string name = reader.ReadString();
        byte empireId = reader.ReadByte();
        uint battleRank = reader.ReadUInt32();
        uint nextBattleRankPercent = reader.ReadUInt32();
        uint headId = reader.ReadUInt32();
        uint modelId = reader.ReadUInt32();
        uint gender = reader.ReadUInt32();
        uint profileId = reader.ReadUInt32();
        uint field7 = reader.ReadUInt32();
        uint field8 = reader.ReadUInt32();

        int firstCount = reader.ReadInt32();
        if (firstCount != 0)
        {
            throw new PacketFormatException(
                $"CharacterSelectionPayload first nested collection has unsupported count {firstCount}.");
        }

        int secondCount = reader.ReadInt32();
        if (secondCount != 0)
        {
            throw new PacketFormatException(
                $"CharacterSelectionPayload second nested collection has unsupported count {secondCount}.");
        }

        ulong field9 = reader.ReadUInt64();
        if (!reader.AtEnd)
        {
            throw new PacketFormatException(
                $"CharacterSelectionPayload has {reader.Remaining} trailing byte(s).");
        }

        return new CharacterSelectionPayload(
            name,
            empireId,
            battleRank,
            nextBattleRankPercent,
            headId,
            modelId,
            gender,
            profileId,
            field7,
            field8,
            field9);
    }

    public byte[] ToArray()
    {
        using var writer = new PacketWriter();
        WriteTo(writer);
        return writer.Written.ToArray();
    }

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteString(Name);
        writer.WriteByte(EmpireId);
        writer.WriteUInt32(BattleRank);
        writer.WriteUInt32(NextBattleRankPercent);
        writer.WriteUInt32(HeadId);
        writer.WriteUInt32(ModelId);
        writer.WriteUInt32(Gender);
        writer.WriteUInt32(ProfileId);
        writer.WriteUInt32(Field7);
        writer.WriteUInt32(Field8);

        // FUN_140a55fa0 and FUN_140f0e4d0 each begin with an i32 element count.
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        writer.WriteUInt64(Field9);
    }
}
