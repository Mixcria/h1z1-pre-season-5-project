using Cranberry.Protocol;

namespace Cranberry.Login;

/// <summary>
/// Inner application request A6 01 sent from the character-create name field. Both text
/// fields are fixed-string objects in the client, serialized as counted UTF-8 strings.
/// </summary>
public sealed record NameValidationRequest(string Name, string Context)
{
    public const byte Family = 0xA6;
    public const byte SubOpcode = 0x01;

    public static NameValidationRequest Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        byte family = reader.ReadByte();
        byte subOpcode = reader.ReadByte();
        if (family != Family || subOpcode != SubOpcode)
        {
            throw new PacketFormatException(
                $"Expected name-validation A6 01, got {family:X2} {subOpcode:X2}.");
        }

        string name = reader.ReadString();
        string context = reader.ReadString();
        if (!reader.AtEnd)
        {
            throw new PacketFormatException(
                $"NameValidationRequest has {reader.Remaining} trailing byte(s).");
        }

        return new NameValidationRequest(name, context);
    }
}

/// <summary>
/// Inner application reply A6 02: the two request strings followed by a u32 result.
/// The August callback treats result 1 as success and every other value as failure.
/// </summary>
public sealed record NameValidationReply(string Name, string Context, uint Result = 1)
{
    public const byte Family = 0xA6;
    public const byte SubOpcode = 0x02;
    public const uint Success = 1;
    public const uint NameTaken = 2;
    public const uint InvalidName = 3;
    public const uint ProfaneName = 4;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Family);
        writer.WriteByte(SubOpcode);
        writer.WriteString(Name);
        writer.WriteString(Context);
        writer.WriteUInt32(Result);
    }
}
