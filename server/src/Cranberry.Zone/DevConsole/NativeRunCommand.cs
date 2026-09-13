using Cranberry.Protocol;

namespace Cranberry.Zone.DevConsole;

/// <summary>
/// Native /run: FUN_14128fc60 -> sender FUN_141265430 -> serializer FUN_141269bb0.
/// Its reply is read by FUN_14129ad10 case 0x4c6 with the same seven-byte layout.
/// Zero clears the override; positive values are absolute metres per second.
/// </summary>
public static class NativeRunCommand
{
    public const ushort SubOpcode = 0x04c6;
    public const int Length = 7;
    /// <summary>Server bound for an explicit debug request; this is not a retail speed default.</summary>
    public const float MaximumMetresPerSecond = 1000f;

    public static bool Matches(ReadOnlySpan<byte> payload) => payload.Length >= 3
        && payload[0] is ConsoleOpcodes.CommandBase or ConsoleOpcodes.AdminBase
        && payload[1] == 0xc6 && payload[2] == 0x04;

    public static float Parse(ReadOnlySpan<byte> payload)
    {
        if (!Matches(payload) || payload.Length != Length)
            throw new PacketFormatException($"Native /run requires {Length} bytes, received {payload.Length}.");
        var reader = new PacketReader(payload);
        reader.Skip(3);
        float speed = reader.ReadSingle();
        if (!float.IsFinite(speed) || speed < 0 || speed > MaximumMetresPerSecond)
            throw new PacketFormatException($"Native /run speed must be finite and within 0..{MaximumMetresPerSecond} m/s.");
        return speed;
    }

    public static void WriteReply(PacketWriter writer, float speed)
    {
        writer.WriteByte(ConsoleOpcodes.CommandBase);
        writer.WriteUInt16(SubOpcode);
        writer.WriteSingle(speed);
    }
}
