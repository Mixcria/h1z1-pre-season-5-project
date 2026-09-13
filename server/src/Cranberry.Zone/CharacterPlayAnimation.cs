using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>
/// Character.PlayAnimation (0f04), August reader 140c21610 and applier 140c63bc0.
/// Sends an animation-network message with an optional control parameter. Duration
/// sets MeleeDuration in seconds; it does not change clip playback speed.
/// </summary>
public sealed record CharacterPlayAnimation(ulong ActorGuid, string AnimationName,
    uint DurationMs = 1000, string ParameterName = "", float ParameterValue = 0,
    uint ClientTime = 0, byte Flags = 0)
{
    public const byte Opcode = ZoneOpcodes.CharacterBase;
    public const byte SubOpcode = 4;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrEmpty(AnimationName);
        ArgumentNullException.ThrowIfNull(ParameterName);
        if (ActorGuid == 0 || AnimationName.Contains('\0') || ParameterName.Contains('\0')
            || !float.IsFinite(ParameterValue))
            throw new ArgumentException("Invalid animation target or parameter.");

        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(ActorGuid);
        ClientPackedName.Write(writer, AnimationName);
        writer.WriteUInt32(ClientTime);
        writer.WriteByte(Flags);
        writer.WriteUInt32(DurationMs);
        ClientPackedName.Write(writer, ParameterName);
        writer.WriteSingle(ParameterValue);
    }
}
