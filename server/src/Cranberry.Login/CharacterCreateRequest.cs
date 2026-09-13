using Cranberry.Protocol;

namespace Cranberry.Login;

/// <summary>
/// The form the client fills in on the create screen, as it serialises it into the request
/// payload (`FUN_140f0c160` + `FUN_140f0c3d0`, derivation log 2026-08-27). Field roles are
/// provisional until confirmed on the wire; the names below describe the wire shape only.
/// </summary>
public sealed record CharacterCreatePayload(
    byte EmpireId,
    uint HeadId,
    uint ProfileId,
    uint Gender,
    string Name,
    uint SkinToneId,
    uint HairId,
    uint RuntimeValue,
    string OperatingSystem,
    string PlatformVersion,
    string ClientVersion,
    string Environment)
{
    public static CharacterCreatePayload Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var result = new CharacterCreatePayload(
            r.ReadByte(),
            r.ReadUInt32(),
            r.ReadUInt32(),
            r.ReadUInt32(),
            r.ReadString(),
            r.ReadUInt32(),
            r.ReadUInt32(),
            r.ReadUInt32(),
            r.ReadString(),
            r.ReadString(),
            r.ReadString(),
            r.ReadString());
        if (!r.AtEnd)
        {
            throw new PacketFormatException(
                $"CharacterCreatePayload has {r.Remaining} trailing byte(s).");
        }

        return result;
    }

    public override string ToString() =>
        $"empire={EmpireId} head={HeadId} profile={ProfileId} gender={Gender} name='{Name}' " +
        $"skinTone={SkinToneId} hair={HairId} runtime={RuntimeValue} os='{OperatingSystem}' " +
        $"platform='{PlatformVersion}' client='{ClientVersion}' environment='{Environment}'";
}

/// <summary>
/// Client → server, opcode 0x05. Wire order from the client's own field writer
/// (`FUN_14211daa0`): u8 id, u64 server id, u32 length + payload bytes.
/// </summary>
public sealed record CharacterCreateRequest(ulong ServerId, byte[] Payload)
{
    public const byte Opcode = 0x05;

    /// <summary>Parses the body that follows the opcode byte.</summary>
    public static CharacterCreateRequest Parse(ReadOnlySpan<byte> body)
    {
        var r = new PacketReader(body);
        ulong serverId = r.ReadUInt64();
        byte[] payload = r.ReadCountedBytes().ToArray();
        if (!r.AtEnd)
        {
            throw new PacketFormatException(
                $"CharacterCreateRequest has {r.Remaining} trailing byte(s).");
        }

        return new CharacterCreateRequest(serverId, payload);
    }
}
