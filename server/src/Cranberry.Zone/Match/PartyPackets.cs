using System.Diagnostics.CodeAnalysis;
using Cranberry.Protocol;

namespace Cranberry.Zone.Match;

/// <summary>August FUN_140d60420: guid, generic identity, signed byte kind, platform string.</summary>
public sealed record PartyCharacter(ulong Guid, SelfIdentity Identity, byte Kind = 1, string PlatformId = "")
{
    internal static PartyCharacter Read(ref PacketReader reader)
    {
        ulong guid = reader.ReadUInt64();
        var identity = new SelfIdentity
        {
            Value0 = reader.ReadUInt32(), Value1 = reader.ReadUInt32(), Value2 = reader.ReadUInt32(),
            Name = reader.ReadString(), Text1 = reader.ReadString(), Text2 = reader.ReadString(),
            Text3 = reader.ReadString(), Value3 = reader.ReadUInt64(),
        };
        return new(guid, identity, reader.ReadByte(), reader.ReadString());
    }

    internal void WriteTo(PacketWriter writer)
    {
        writer.WriteUInt64(Guid);
        writer.WriteUInt32(Identity.Value0);
        writer.WriteUInt32(Identity.Value1);
        writer.WriteUInt32(Identity.Value2);
        writer.WriteString(Identity.Name);
        writer.WriteString(Identity.Text1);
        writer.WriteString(Identity.Text2);
        writer.WriteString(Identity.Text3);
        writer.WriteUInt64(Identity.Value3);
        writer.WriteByte(Kind);
        writer.WriteString(PlatformId);
    }
}

/// <summary>August invite body FUN_140d5ece0, shared verbatim by Invite and Join.</summary>
public sealed record PartyInviteData(ulong Token, uint Kind, PartyCharacter Source, PartyCharacter Target, uint GroupId)
{
    internal static PartyInviteData Read(ref PacketReader reader) => new(reader.ReadUInt64(), reader.ReadUInt32(),
        PartyCharacter.Read(ref reader), PartyCharacter.Read(ref reader), reader.ReadUInt32());

    internal void WriteTo(PacketWriter writer)
    {
        writer.WriteUInt64(Token);
        writer.WriteUInt32(Kind);
        Source.WriteTo(writer);
        Target.WriteTo(writer);
        writer.WriteUInt32(GroupId);
    }
}

/// <summary>
/// 1148 GroupsBase has byte-sized sub/execute fields, unlike older protocol schemas.
/// Invite loader FUN_140d64d20 and Join handler FUN_140d6dec0 establish every field below.
/// Execute=1 delivers an invitation; JoinState=1 accepts and 2 declines (FUN_140d76710).
/// </summary>
public sealed record PartyPacket(byte SubOpcode, byte Execute, uint Error, uint Context,
    PartyInviteData? Invite = null, uint JoinState = 0, bool Flag = false)
{
    public const byte InviteSub = 1;
    public const byte JoinSub = 2;
    public const byte LeaveSub = 4;

    public static bool TryParse(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out PartyPacket? packet)
    {
        packet = null;
        if (payload.Length < 7 || payload.Length > 4096) return false;
        try
        {
            var reader = new PacketReader(payload);
            if (reader.ReadByte() != ZoneOpcodes.GroupsBase) return false;
            byte sub = reader.ReadByte(), execute = reader.ReadByte();
            uint error = reader.ReadUInt32();
            if (sub == LeaveSub)
            {
                if (!reader.AtEnd) return false;
                packet = new(sub, execute, error, 0);
                return true;
            }
            if (sub is not (InviteSub or JoinSub)) return false;
            uint joinState = sub == JoinSub ? reader.ReadUInt32() : 0;
            uint context = reader.ReadUInt32();
            PartyInviteData invite = PartyInviteData.Read(ref reader);
            bool flag = sub == JoinSub && reader.ReadBool();
            if (!reader.AtEnd) return false;
            packet = new(sub, execute, error, context, invite, joinState, flag);
            return true;
        }
        catch (PacketFormatException) { return false; }
    }

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(ZoneOpcodes.GroupsBase);
        writer.WriteByte(SubOpcode);
        writer.WriteByte(Execute);
        writer.WriteUInt32(Error);
        if (SubOpcode == LeaveSub) return;
        if (SubOpcode == JoinSub) writer.WriteUInt32(JoinState);
        writer.WriteUInt32(Context);
        Invite!.WriteTo(writer);
        if (SubOpcode == JoinSub) writer.WriteBool(Flag);
    }
}
