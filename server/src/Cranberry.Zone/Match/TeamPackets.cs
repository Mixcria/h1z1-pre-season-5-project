using Cranberry.Protocol;

namespace Cranberry.Zone.Match;

/// <summary>
/// August FUN_140bb1330 reads ce/u16 0a, players into +0x1c, then teams into +0x18.
/// FUN_140bba510 case 10 updates the same player counter as ce09 and the team counter.
/// </summary>
public sealed record TeamsRemaining(int Players, int Teams)
{
    public const byte Opcode = 0xce;
    public const ushort SubOpcode = 0x0a;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Players);
        ArgumentOutOfRangeException.ThrowIfNegative(Teams);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteUInt32((uint)Players);
        writer.WriteUInt32((uint)Teams);
    }
}
