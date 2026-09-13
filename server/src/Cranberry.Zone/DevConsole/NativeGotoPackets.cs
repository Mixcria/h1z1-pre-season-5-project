using Cranberry.Protocol;

namespace Cranberry.Zone.DevConsole;

/// <summary>
/// Original August /goto and /warp requests, emitted by FUN_141288d30.
/// FUN_1412696f0 writes 09 36 04, target u64, NPC definition u32, String8 name,
/// and a final u64 at object+0x90 whose meaning is not established.
/// FUN_1412698b0 writes the waypoint variant: 09 38 04, target u64, String8 name.
/// Evidence: out/ghidra-aug/native-goto-20260906. Neither request contains coordinates.
/// </summary>
public sealed record NativeGotoRequest(
    bool Waypoint, ulong TargetGuid, uint NpcDefinitionId, string TargetName, ulong TrailingValue)
{
    public const ushort GotoSub = 0x0436;
    public const ushort WaypointSub = 0x0438;

    public static bool Matches(ReadOnlySpan<byte> payload) =>
        payload.Length >= 3 && payload[0] is ConsoleOpcodes.CommandBase or ConsoleOpcodes.AdminBase
        && (ushort)(payload[1] | payload[2] << 8) is GotoSub or WaypointSub;

    public static NativeGotoRequest Parse(ReadOnlySpan<byte> payload)
    {
        if (!Matches(payload)) throw new PacketFormatException("Not an August native goto request.");
        var reader = new PacketReader(payload);
        reader.ReadByte();
        bool waypoint = reader.ReadUInt16() == WaypointSub;
        ulong guid = reader.ReadUInt64();
        uint definition = waypoint ? 0 : reader.ReadUInt32();
        string name = reader.ReadString();
        ulong trailing = waypoint ? 0 : reader.ReadUInt64();
        if (!reader.AtEnd) throw new PacketFormatException("Unexpected trailing native goto bytes.");
        return new(waypoint, guid, definition, name, trailing);
    }
}
