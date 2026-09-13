using Cranberry.Protocol;

namespace Cranberry.Login;

/// <summary>One entry of the account-feature list (body layout: u32, bool, u32, string).</summary>
public sealed record AccountFeature(uint Id, uint Value1, bool Flag, uint Value2, string Text);

/// <summary>
/// Server → client answer to <see cref="LoginRequest"/>. Layout measured from the August
/// client's unserializer (`FUN_14211ea50`, derivation log 2026-08-27). Field names are ours;
/// the client only exposes their roles: the first bool decides whether the session lives.
/// </summary>
public sealed record LoginReply
{
    public const byte Opcode = 0x02;

    public bool LoggedIn { get; init; } = true;
    public uint Status { get; init; } = 1;
    public uint ResultCode { get; init; } = 1;
    public bool FlagA { get; init; }
    public bool FlagB { get; init; }
    public string Text1 { get; init; } = string.Empty;
    public IReadOnlyList<AccountFeature> Features { get; init; } = [];
    public byte[] Payload { get; init; } = [];
    public IReadOnlyList<KeyValuePair<string, string>> Pairs { get; init; } = [];
    /// <summary>
    /// ISO country code used by the August client's data-centre mapper. This is the
    /// <c>IpCountryCode</c> member named by the client's document serializer.
    /// </summary>
    public string IpCountryCode { get; init; } = string.Empty;

    /// <summary>The block that is new in LoginUdp_14: one string, one flag, eight strings.</summary>
    public string ExtraText { get; init; } = string.Empty;
    public bool ExtraFlag { get; init; }
    public string[] ExtraTexts { get; init; } = new string[8];

    public void WriteTo(PacketWriter w)
    {
        w.WriteByte(Opcode);
        w.WriteBool(LoggedIn);
        w.WriteUInt32(Status);
        w.WriteUInt32(ResultCode);
        w.WriteBool(FlagA);
        w.WriteBool(FlagB);
        w.WriteString(Text1);

        w.WriteInt32(Features.Count);
        foreach (AccountFeature feature in Features)
        {
            w.WriteUInt32(feature.Id);
            w.WriteUInt32(feature.Value1);
            w.WriteBool(feature.Flag);
            w.WriteUInt32(feature.Value2);
            w.WriteString(feature.Text);
        }

        w.WriteCountedBytes(Payload);

        w.WriteInt32(Pairs.Count);
        foreach (KeyValuePair<string, string> pair in Pairs)
        {
            w.WriteString(pair.Key);
            w.WriteString(pair.Value);
        }

        w.WriteString(IpCountryCode);

        w.WriteString(ExtraText);
        w.WriteBool(ExtraFlag);
        for (int i = 0; i < 8; i++)
        {
            w.WriteString(i < ExtraTexts.Length ? ExtraTexts[i] ?? string.Empty : string.Empty);
        }
    }
}
