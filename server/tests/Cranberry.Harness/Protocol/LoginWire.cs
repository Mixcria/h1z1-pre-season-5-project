using Cranberry.Harness.Wire;

namespace Cranberry.Harness.Protocol;

/// <summary>One roster row out of a CharacterSelectInfoReply.</summary>
public sealed record RosterCharacter(ulong EntityKey, ulong ServerId, ulong Field3, uint Status, byte[] Payload)
{
    public const uint StatusAvailable = 1;
}

/// <summary>What the server answered on the login link, parsed only as far as the harness needs.</summary>
public sealed record CharacterSelectInfo(uint Status, bool Flag, IReadOnlyList<RosterCharacter> Characters);

/// <summary>
/// The gateway handoff carried inside a successful CharacterLoginReply (docs/05): where to
/// connect, the ticket to present, and the RC4 key to arm with.
/// </summary>
public sealed record GatewayHandoff(
    uint GatewayId,
    string Address,
    string Ticket,
    byte[] Key,
    uint CipherMode,
    ulong Guid)
{
    public const byte FamilyTag = 0xA6;
    public const byte SubTag = 0x0D;
    public const uint CipherNone = 0;
    public const uint CipherRc4 = 3;

    public bool UsesRc4 => CipherMode == CipherRc4;
}

/// <summary>The reply to a CharacterLoginRequest.</summary>
public sealed record CharacterLoginOutcome(ulong EntityKey, ulong ServerId, uint Status, GatewayHandoff? Gateway)
{
    public const uint StatusSuccess = 1;

    public bool Succeeded => Status == StatusSuccess && Gateway is not null;
}

/// <summary>
/// Builds the four requests the August client sends on its login link and parses the three
/// replies the harness has to act on. Byte layouts are those of the reference capture
/// wire-20260829-184346 (docs/71 §2); nothing here is derived from the server's own encoders.
/// </summary>
public static class LoginWire
{
    /// <summary>August create form, FUN_140f0c160 / FUN_140f0c3d0; used only when QA explicitly requests a new character.</summary>
    public static byte[] CreateCharacter(string name)
    {
        var body = new WireWriter();
        body.U8(2).LeU32(3).LeU32(270).LeU32(2).CountedString(name)
            .LeU32(664).LeU32(2).LeU32(2).CountedString("Windows 10 Home")
            .CountedString("6.2").CountedString(AugustClient.Version).CountedString("Live");
        byte[] payload = body.ToArray();
        return new WireWriter().U8(5).LeU64(1).LeU32((uint)payload.Length).Raw(payload).ToArray();
    }
    /// <summary>
    /// The client's opening message: <c>01 | str sessionId | str fingerprint | u32 ×4</c>.
    /// The session-id string is <c>"0"</c> when the game is launched from the shortcut and a
    /// 64-character launcher token otherwise; both were accepted (docs/71 §2).
    /// </summary>
    public static byte[] LoginRequest(string sessionId, string fingerprint)
    {
        var w = new WireWriter(1024);
        w.U8(LoginOpcodes.LoginRequest)
            .CountedString(sessionId)
            .CountedString(fingerprint)
            .LeU32(0).LeU32(0).LeU32(0).LeU32(0);
        return w.ToArray();
    }

    /// <summary>One byte, <c>0D</c>.</summary>
    public static byte[] ServerListRequest() => [LoginOpcodes.ServerListRequest];

    /// <summary>One byte, <c>0B</c>.</summary>
    public static byte[] CharacterSelectInfoRequest() => [LoginOpcodes.CharacterSelectInfoRequest];

    /// <summary>One byte, <c>03</c>. The client sends this ~7 s after the gateway world is up.</summary>
    public static byte[] Logout() => [LoginOpcodes.Logout];

    /// <summary>
    /// <c>07 | u64 entityKey | u64 serverId | u32 count | context</c>. The context block is
    /// replayed verbatim from the reference capture (see <see cref="AugustLoginContext.Bytes"/>)
    /// rather than re-synthesised: docs/71 §14 records that the field boundaries inside it are not
    /// settled, and an invented context is not client behaviour.
    /// </summary>
    public static byte[] CharacterLoginRequest(ulong entityKey, ulong serverId, ReadOnlySpan<byte> context)
    {
        var w = new WireWriter(160);
        w.U8(LoginOpcodes.CharacterLoginRequest)
            .LeU64(entityKey)
            .LeU64(serverId)
            .CountedBytes(context);
        return w.ToArray();
    }

    public static CharacterSelectInfo ParseCharacterSelectInfoReply(ReadOnlySpan<byte> message)
    {
        var r = new WireReader(message);
        byte opcode = r.U8();
        if (opcode != LoginOpcodes.CharacterSelectInfoReply)
        {
            throw new WireFormatException(
                $"Expected CharacterSelectInfoReply (0x0C), got {LoginOpcodes.Name(opcode)}.");
        }

        uint status = r.LeU32();
        bool flag = r.Bool();
        uint count = r.LeU32();
        if (count > 512)
        {
            throw new WireFormatException($"CharacterSelectInfoReply claims {count} characters.");
        }

        var characters = new List<RosterCharacter>((int)count);
        for (uint i = 0; i < count; i++)
        {
            characters.Add(new RosterCharacter(
                EntityKey: r.LeU64(),
                ServerId: r.LeU64(),
                Field3: r.LeU64(),
                Status: r.LeU32(),
                Payload: r.CountedBytes().ToArray()));
        }

        return new CharacterSelectInfo(status, flag, characters);
    }

    public static CharacterLoginOutcome ParseCharacterLoginReply(ReadOnlySpan<byte> message)
    {
        var r = new WireReader(message);
        byte opcode = r.U8();
        if (opcode != LoginOpcodes.CharacterLoginReply)
        {
            throw new WireFormatException(
                $"Expected CharacterLoginReply (0x08), got {LoginOpcodes.Name(opcode)}.");
        }

        ulong entityKey = r.LeU64();
        ulong serverId = r.LeU64();
        uint status = r.LeU32();
        byte[] payload = r.CountedBytes().ToArray();
        GatewayHandoff? handoff = payload.Length == 0 ? null : ParseGatewayHandoff(payload);
        return new CharacterLoginOutcome(entityKey, serverId, status, handoff);
    }

    public static GatewayHandoff ParseGatewayHandoff(ReadOnlySpan<byte> payload)
    {
        var r = new WireReader(payload);
        byte family = r.U8();
        byte sub = r.U8();
        if (family != GatewayHandoff.FamilyTag || sub != GatewayHandoff.SubTag)
        {
            throw new WireFormatException(
                $"CharacterLoginReply payload starts {family:x2} {sub:x2}, expected "
                + $"{GatewayHandoff.FamilyTag:x2} {GatewayHandoff.SubTag:x2}.");
        }

        return new GatewayHandoff(
            GatewayId: r.LeU32(),
            Address: r.CountedString(),
            Ticket: r.CountedString(),
            Key: r.CountedBytes().ToArray(),
            CipherMode: r.LeU32(),
            Guid: r.LeU64());
    }
}

/// <summary>
/// The 97-byte CharacterLoginRequest context recorded from the August client in
/// <c>wire-20260829-184346.txt</c> at 18:44:13.021. Replayed verbatim (docs/71 §1 and §14.4: the
/// field boundaries inside it are not settled from the captures, so re-synthesising it would put
/// bytes on the wire the client never sent). Readable content: locale "en_us", OS
/// "Windows 10 Home", platform "6.2", client version "0.0.118.208059", environment "Live".
/// </summary>
public static class AugustLoginContext
{
    private const string Hex =
        "05000000656E5F757308000000000000000000000000000000000000000000010000003000000000020000"
        + "000F00000057696E646F777320313020486F6D6503000000362E320E000000302E302E3131382E3230383035"
        + "39040000004C69766501";

    public static byte[] Bytes => Convert.FromHexString(Hex);

    /// <summary>
    /// The SystemFingerprint XML the client sends in its LoginRequest is a machine-identity blob;
    /// the harness sends a clearly-labelled stand-in of the same shape instead of replaying the
    /// owner's actual hardware hashes. The server accepts any fingerprint (LoginService parses it
    /// as an opaque string), and docs/71 §2 records that both observed shapes were accepted.
    /// </summary>
    public const string Fingerprint =
        "<SystemFingerprint VideoCardId=\"cranberry-harness\" NetworkCardId=\"cranberry-harness\" "
        + "HarddriveId=\"\" ComputerName=\"cranberry-harness\">"
        + "<NetworkAdapters IsList=\"1\"><NetworkAdapter Id=\"cranberry-harness\"/></NetworkAdapters>"
        + "<HardDrives IsList=\"1\"/></SystemFingerprint>";

    /// <summary>The shortcut-launch session-id string (docs/71 §2).</summary>
    public const string SessionId = "0";
}
