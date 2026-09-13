using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone.DevConsole;

/// <summary>
/// The built-in /vehicle sender FUN_141292bb0 uses 09 9b 04, not ExecuteCommand.
/// Layout: FUN_141269e70, with the final u32 written by FUN_140a12cc0.
/// Captured live on 2026-09-06 at 18:46:23.759 (37-byte request, vehicle 1).
/// </summary>
public readonly record struct NativeVehicleCommand(
    uint VehicleId, byte FactionId, Vector3 Position, float Yaw, uint Unknown,
    bool AutoMount, uint RewardSetId, uint TrailingUnknown)
{
    public const ushort SubOpcode = 0x049b;
    public const int Length = 37;

    public static bool Matches(ReadOnlySpan<byte> payload) => payload.Length >= 3
        && payload[0] is ConsoleOpcodes.CommandBase or ConsoleOpcodes.AdminBase
        && payload[1] == 0x9b && payload[2] == 0x04;

    public static NativeVehicleCommand Parse(ReadOnlySpan<byte> payload)
    {
        if (!Matches(payload) || payload.Length != Length)
            throw new PacketFormatException($"Native /vehicle requires {Length} bytes, received {payload.Length}.");
        var reader = new PacketReader(payload);
        reader.Skip(3);
        uint id = reader.ReadUInt32();
        byte faction = reader.ReadByte();
        var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        float yaw = reader.ReadSingle();
        uint unknown = reader.ReadUInt32();
        byte mount = reader.ReadByte();
        uint reward = reader.ReadUInt32();
        uint trailing = reader.ReadUInt32();
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
            || !float.IsFinite(yaw) || mount > 1 || faction > 3)
            throw new PacketFormatException("Native /vehicle contains an invalid position, heading, faction or auto-mount flag.");
        return new(id, faction, position, yaw, unknown, mount == 1, reward, trailing);
    }
}
