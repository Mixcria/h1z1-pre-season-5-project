using Cranberry.Protocol;

namespace Cranberry.Zone.Movement;

/// <summary>
/// The <c>valueType</c> byte of a wire stat entry. <c>GetStat</c> (<c>FUN_140c4beb0</c>) is the only
/// thing that interprets it and there is no per-stat type table: <c>1</c> means the two values are
/// IEEE <c>float32</c>, anything else means <c>int32</c> (docs/40 §1.3, §7.1).
/// </summary>
public enum CharacterStatValueType : byte
{
    /// <summary><c>base</c> and <c>modifier</c> are <c>int32</c>.</summary>
    Integer = 0,

    /// <summary><c>base</c> and <c>modifier</c> are IEEE <c>float32</c>.</summary>
    Float = 1,
}

/// <summary>
/// One entry of the stat list both stat packets carry — reader <c>FUN_140a30280</c>, 13 bytes:
/// <c>u32 statId; u8 valueType; u32 base; u32 modifier</c> (docs/40 §7.1).
/// <para>
/// The client stores it in the per-character 133-bucket map at <c>entity+0x3dd0</c> (bucket =
/// <c>statId % 0x85</c>) and every reader takes the **effective** value as
/// <c>base + modifier</c>. The two-field split is what a temporary gameplay effect is meant to use:
/// leave <see cref="Base"/> alone and move <see cref="Modifier"/> (docs/40 §8.1).
/// </para>
/// <para>
/// A <c>valueType</c> above 1 makes the reader stop after the type byte, so the two values would be
/// absent from the stream — <see cref="WriteTo"/> therefore refuses to emit one.
/// </para>
/// </summary>
/// <param name="StatId">The <c>CharacterStatDefinitions</c> ordinal; see <see cref="CharacterStatId"/>.</param>
/// <param name="ValueType">1 for float, 0 for int (docs/40 §7.1).</param>
/// <param name="Base">Raw 32 bits of the base value, interpreted per <paramref name="ValueType"/>.</param>
/// <param name="Modifier">Raw 32 bits of the modifier, added to the base by <c>GetStat</c>.</param>
public readonly record struct CharacterStat(
    uint StatId,
    CharacterStatValueType ValueType,
    uint Base,
    uint Modifier)
{
    /// <summary>Bytes one entry occupies: <c>4 + 1 + 4 + 4</c>.</summary>
    public const int Length = 13;

    /// <summary>A float-typed stat — what every speed modifier and every <c>*Time</c> stat is.</summary>
    public static CharacterStat Float(uint statId, float baseValue, float modifier = 0f) =>
        new(
            statId,
            CharacterStatValueType.Float,
            BitConverter.SingleToUInt32Bits(baseValue),
            BitConverter.SingleToUInt32Bits(modifier));

    /// <summary>An int-typed stat (<c>valueType = 0</c>); no movement stat uses this form.</summary>
    public static CharacterStat Integer(uint statId, int baseValue, int modifier = 0) =>
        new(
            statId,
            CharacterStatValueType.Integer,
            unchecked((uint)baseValue),
            unchecked((uint)modifier));

    /// <summary>
    /// What <c>GetStat</c> would return for this entry: <c>base + modifier</c>, added as two floats
    /// when <see cref="ValueType"/> is <see cref="CharacterStatValueType.Float"/> and as two
    /// <c>int32</c> otherwise (docs/40 §1.3).
    /// </summary>
    public float EffectiveValue => ValueType == CharacterStatValueType.Float
        ? BitConverter.UInt32BitsToSingle(Base) + BitConverter.UInt32BitsToSingle(Modifier)
        : unchecked((int)Base) + (float)unchecked((int)Modifier);

    /// <summary>Writes the 13 bytes <c>FUN_140a30280</c> reads.</summary>
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (ValueType is not (CharacterStatValueType.Integer or CharacterStatValueType.Float))
        {
            // FUN_140a30280 stops reading after the type byte when valueType > 1, so an entry like
            // this would desynchronise the rest of the list.
            throw new InvalidOperationException(
                $"Stat {StatId} has valueType {(byte)ValueType}; the August reader only accepts 0 or 1.");
        }

        writer.WriteUInt32(StatId);
        writer.WriteByte((byte)ValueType);
        writer.WriteUInt32(Base);
        writer.WriteUInt32(Modifier);
    }

    /// <summary>Writes <c>u32 count</c> followed by the entries — the shape both stat packets use.</summary>
    public static void WriteList(PacketWriter writer, IReadOnlyList<CharacterStat> stats)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(stats);
        writer.WriteUInt32((uint)stats.Count);
        foreach (CharacterStat stat in stats)
        {
            stat.WriteTo(writer);
        }
    }
}
