using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>
/// <c>CharacterState.InteractionStart</c>. The August dispatcher routes cf02 to
/// 140ccfd50, whose record reader is 140a30370. It replaces one subject-owned
/// interaction state; 140cd0d90 publishes its localized label and duration through
/// UpdateCharacterStateTimerDataSource. See docs/interaction-ownership-20260926.md.
/// The established writer remains 67 bytes; the native empty-string reader consumes
/// 66 bytes and tolerates the trailing zero. This is not an original-server capture.
/// </summary>
public sealed record InteractionStart(
    ulong CharacterGuid,
    int DurationMilliseconds,
    uint StringId,
    uint AnimationId)
{
    public const byte Opcode = ZoneOpcodes.CharacterStateBase;
    public const byte SubOpcode = 0x02;
    public const int Length = 67;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
        writer.WriteUInt32((uint)Math.Max(DurationMilliseconds, 0));
        writer.WriteUInt32(0);
        writer.WriteUInt64(0);
        writer.WriteUInt64(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(StringId);
        writer.WriteUInt32(AnimationId);

        // Native tail: four u32 fields and one empty length-prefixed string (20 bytes).
        // Preserve the existing extra zero padding byte; no second string is decoded.
        for (int index = 0; index < 21; index++)
        {
            writer.WriteByte(0);
        }
    }
}

/// <summary>
/// <c>CharacterState.InteractionStop</c>: 10 bytes, two one-byte header fields and
/// a character GUID. Verified by August handler 140ccff80 (subject router 140cd07c0).
/// It clears the same current record used by InteractionStart, then publishes zero
/// duration. There is no cast-kind or instance token to distinguish an older stop
/// from a newer start. This trace does not establish original duplicate-stop counts
/// or whether the original server refused, queued, or interrupted competing actions.
/// </summary>
public sealed record InteractionStop(ulong CharacterGuid)
{
    public const byte Opcode = ZoneOpcodes.CharacterStateBase;
    public const byte SubOpcode = 0x03;
    public const int Length = 10;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
    }
}
