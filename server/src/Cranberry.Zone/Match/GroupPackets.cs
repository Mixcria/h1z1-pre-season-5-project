using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone.Match;

/// <summary>August group member: FUN_140d5ede0, 140d60420, 140a40000 and 140d5f180.</summary>
public sealed record GroupMember(ulong CharacterGuid, string Name, Vector3 Position, uint MemberIndex = 0)
{
    public void WriteTo(PacketWriter writer)
    {
        writer.WriteUInt64(CharacterGuid);
        // The same identity reader as the self record; the first string is the displayed name.
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteString(Name);
        writer.WriteString(string.Empty);
        writer.WriteString(string.Empty);
        writer.WriteString(string.Empty);
        writer.WriteUInt64(0);
        writer.WriteByte(0);
        writer.WriteString(string.Empty);
        // Job/profile record, followed by the member runtime fields. Retain constructor
        // defaults for fields whose meaning is unproven; speaking is bit 4 of +0x168.
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(1);
        writer.WriteUInt32(0); // +0x164
        writer.WriteByte(0);   // +0x168 flags
        writer.WriteUInt32(0); // +0x16c
        writer.WriteUInt32(0); // +0x170
        writer.WriteUInt32(0); // +0x174 encounter
        writer.WriteUInt64(0); // +0x178
        writer.WriteUInt32(uint.MaxValue); // +0x180
        writer.WriteSingle(Position.X);
        writer.WriteSingle(Position.Y);
        writer.WriteSingle(Position.Z);
        writer.WriteSingle(0);
        writer.WriteSingle(0);
        writer.WriteSingle(0);
        writer.WriteSingle(1);
        writer.WriteUInt64(0); // +0x1a0
        writer.WriteUInt32(uint.MaxValue); // +0x1a8
        writer.WriteUInt32(MemberIndex); // +0x1ac member ordering, FUN_140d7a410
        writer.WriteUInt32(uint.MaxValue); // +0x1b4
    }
}

/// <summary>
/// Full group roster: dispatcher 140d6c290 case 12, handler 140d6c610 and reader 140d5e930.
/// The header is three bytes, not the u32 execution fields in older protocol schemas.
/// </summary>
public sealed record GroupRoster(uint GroupId, ulong LeaderGuid, IReadOnlyList<GroupMember> Members)
{
    public const byte Opcode = 0x13;
    public const byte SubOpcode = 0x12;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteByte(2);
        writer.WriteUInt32(GroupId);
        writer.WriteUInt64(LeaderGuid);
        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteString(string.Empty);
        writer.WriteUInt32(0);
        writer.WriteUInt32((uint)Members.Count);
        foreach (var member in Members)
        {
            writer.WriteUInt64(member.CharacterGuid); // roster dictionary key, 140d634a0
            member.WriteTo(writer);
        }
        writer.WriteUInt32(0);
    }
}

/// <summary>August 140d6ec00: header, message/error u32, then the group ID u32.</summary>
public sealed record RemoveGroup(uint GroupId)
{
    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(GroupRoster.Opcode);
        writer.WriteByte(0x16);
        writer.WriteByte(2);
        writer.WriteUInt32(0);
        writer.WriteUInt32(GroupId);
    }
}

/// <summary>August 140d70cf0: 13/14/execute followed directly by one group member.</summary>
public sealed record GroupMemberUpdate(GroupMember Member)
{
    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(GroupRoster.Opcode);
        writer.WriteByte(0x14);
        writer.WriteByte(2);
        Member.WriteTo(writer);
    }
}
