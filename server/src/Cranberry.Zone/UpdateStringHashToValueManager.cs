using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>
/// August 0xfc updates one client setting without replacing the complete table.
/// FUN_140a63f70 reads name, value, flag; dispatcher case 0xfc calls FUN_140b80030.
/// </summary>
public sealed record UpdateStringHashToValueManager(string Name, string Value, bool Flag = false)
{
    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(ZoneOpcodes.UpdateStringHashToValueManager);
        writer.WriteString(Name);
        writer.WriteString(Value);
        writer.WriteBool(Flag);
    }
}
