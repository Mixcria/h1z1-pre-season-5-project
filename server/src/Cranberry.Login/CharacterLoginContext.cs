using Cranberry.Protocol;

namespace Cranberry.Login;

/// <summary>
/// One entry in the context list written by `FUN_140f0e260`; the element writer
/// `FUN_140f0cb20` emits two u32 values followed by two bytes.
/// </summary>
public sealed record CharacterLoginContextEntry(
    uint Value1,
    uint Value2,
    byte Flag1,
    byte Flag2);

/// <summary>The client/runtime block shared in shape with character creation.</summary>
public sealed record CharacterLoginRuntime(
    uint RuntimeValue,
    string OperatingSystem,
    string PlatformVersion,
    string ClientVersion,
    string Environment);

/// <summary>
/// The counted context inside an August <see cref="CharacterLoginRequest"/>. Only names proven
/// by the request assembler are assigned semantic roles; the platform-specific members remain
/// numbered so the wire contract can be strict without inventing meanings.
/// </summary>
public sealed record CharacterLoginContext(
    string Locale,
    uint LocaleId,
    uint GatewayId,
    byte Flag1,
    string Text1,
    uint Value1,
    byte Flag2,
    uint Value2,
    string Text2,
    IReadOnlyList<CharacterLoginContextEntry> Entries,
    CharacterLoginRuntime Runtime,
    byte Flag3)
{
    public static CharacterLoginContext Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        string locale = r.ReadString();
        uint localeId = r.ReadUInt32();
        uint gatewayId = r.ReadUInt32();
        byte flag1 = r.ReadByte();
        string text1 = r.ReadString();
        uint value1 = r.ReadUInt32();
        byte flag2 = r.ReadByte();
        uint value2 = r.ReadUInt32();
        string text2 = r.ReadString();

        int count = r.ReadInt32();
        const int EntryWireSize = 10;
        if (count < 0 || count > r.Remaining / EntryWireSize)
        {
            throw new PacketFormatException(
                $"CharacterLoginContext entry count {count} does not fit in {r.Remaining} byte(s).");
        }

        var entries = new CharacterLoginContextEntry[count];
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = new CharacterLoginContextEntry(
                r.ReadUInt32(),
                r.ReadUInt32(),
                r.ReadByte(),
                r.ReadByte());
        }

        var runtime = new CharacterLoginRuntime(
            r.ReadUInt32(),
            r.ReadString(),
            r.ReadString(),
            r.ReadString(),
            r.ReadString());
        byte flag3 = r.ReadByte();
        if (!r.AtEnd)
        {
            throw new PacketFormatException(
                $"CharacterLoginContext has {r.Remaining} trailing byte(s).");
        }

        return new CharacterLoginContext(
            locale,
            localeId,
            gatewayId,
            flag1,
            text1,
            value1,
            flag2,
            value2,
            text2,
            entries,
            runtime,
            flag3);
    }

    public override string ToString() =>
        $"locale='{Locale}' localeId={LocaleId} gatewayId={GatewayId} " +
        $"flags=[{Flag1},{Flag2},{Flag3}] values=[{Value1},{Value2}] " +
        $"texts=['{Text1}','{Text2}'] entries={Entries.Count} " +
        $"runtime={Runtime.RuntimeValue} os='{Runtime.OperatingSystem}' " +
        $"platform='{Runtime.PlatformVersion}' client='{Runtime.ClientVersion}' " +
        $"environment='{Runtime.Environment}'";
}
