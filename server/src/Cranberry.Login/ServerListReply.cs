using System.Globalization;
using System.Text;
using Cranberry.Protocol;

namespace Cranberry.Login;

/// <summary>
/// One server in the list (`Login::ClientGameServerData`). Wire order measured from the August
/// client's reader (`FUN_1413dcfe0` + `FUN_1413dcf20` + the loop in `FUN_1413dd430`); the roles
/// come from `FUN_140f29430`, which copies the parsed entry into the client's server record, and
/// from the login-host search `FUN_140f263c0` (derivation log 2026-08-27).
/// </summary>
public sealed record GameServerEntry
{
    /// <summary>State bit 0: the host is locked and needs the bypass right.</summary>
    public const byte StateLocked = 1;

    /// <summary>State bit 1: the host is out.</summary>
    public const byte StateDown = 2;

    public ulong Id { get; init; } = 1;

    /// <summary>Display name. Not consulted by the login-host search.</summary>
    public string Name { get; init; } = "Cranberry";

    /// <summary>Copied to the client record at +0x04; no consumer found yet.</summary>
    public uint Field3 { get; init; }

    /// <summary>Not copied into the client record by `FUN_140f29430`.</summary>
    public string Text4 { get; init; } = string.Empty;

    /// <summary>Copied to the client record at +0x0c; no consumer found yet.</summary>
    public uint Field5 { get; init; }

    /// <summary>Copied to the client record at +0x84; no consumer found yet.</summary>
    public uint Field6 { get; init; }

    /// <summary>Region shown for the host (`ServerInfo` document).</summary>
    public string Region { get; init; } = string.Empty;

    public string Subregion { get; init; } = string.Empty;

    /// <summary>Not copied into the client record by `FUN_140f29430`.</summary>
    public string Text8 { get; init; } = string.Empty;

    /// <summary>Not copied into the client record by `FUN_140f29430`.</summary>
    public bool Flag9 { get; init; }

    /// <summary>0 = open. See <see cref="StateLocked"/> and <see cref="StateDown"/>.</summary>
    public byte State { get; init; }

    /// <summary>The client prints this as "Pop".</summary>
    public uint Population { get; init; }

    /// <summary>The `Population` document (see <see cref="PopulationInfo"/>).</summary>
    public PopulationInfo Info { get; init; } = new();

    /// <summary>The per-entry flag the client logs as "allowed" and requires for a login host.</summary>
    public bool IsAllowed { get; init; } = true;

    public void WriteTo(PacketWriter w)
    {
        w.WriteUInt64(Id);
        w.WriteString(Name);
        w.WriteUInt32(Field3);
        w.WriteString(Text4);
        w.WriteUInt32(Field5);
        w.WriteUInt32(Field6);
        w.WriteString(ServerInfoDocument());
        w.WriteString(Text8);
        w.WriteBool(Flag9);
        w.WriteByte(State);
        w.WriteUInt32(Population);
        w.WriteString(Info.ToDocument());
        w.WriteBool(IsAllowed);
    }

    /// <summary>`&lt;ServerInfo Region=".." Subregion=".."/&gt;` — attributes read by `FUN_140f29430` via `FUN_140f23530`.</summary>
    public string ServerInfoDocument()
    {
        if (Region.Length == 0 && Subregion.Length == 0)
        {
            return string.Empty;
        }

        var doc = new AttributeDocument("ServerInfo");
        doc.Add("Region", Region);
        doc.Add("Subregion", Subregion);
        return doc.ToString();
    }
}

/// <summary>
/// The document the client parses out of the entry's last string (`FUN_140f24690`, element
/// `Population`). Attribute names are the client's own; every one is optional to its parser
/// (a missing attribute keeps the default and only sets a soft error flag).
/// </summary>
public sealed record PopulationInfo
{
    /// <summary>Required for a login host (`FUN_140f263c0` checks record+0x9a).</summary>
    public bool IsLogin { get; init; } = true;

    public bool IsWhitelisted { get; init; }

    public bool IsEvent { get; init; }

    /// <summary>"PL": when set, the auto-login path skips the host if the measured ping to <see cref="PingAddress"/> is above <see cref="BadPingMs"/>.</summary>
    public bool PingLocked { get; init; }

    public bool PopulationLocked { get; init; }

    public int Mode { get; init; }

    /// <summary>The search picks the qualifying host with the lowest value.</summary>
    public int PercentCapacity { get; init; }

    public int QueuePercentCapacity { get; init; }

    /// <summary>"GP" / "BP": the client's ping thresholds (its defaults are 50 and 200).</summary>
    public int GoodPingMs { get; init; } = 50;

    public int BadPingMs { get; init; } = 200;

    public string PingAddress { get; init; } = string.Empty;

    public string Rulesets { get; init; } = string.Empty;

    /// <summary>"DC": matched against the client's selected data centre when it has one.</summary>
    public string DataCenter { get; init; } = string.Empty;

    public string ToDocument()
    {
        var doc = new AttributeDocument("Population");
        doc.Add("Mode", Mode);
        doc.Add("IsLogin", IsLogin);
        doc.Add("IsWL", IsWhitelisted);
        doc.Add("IsEvt", IsEvent);
        doc.Add("PL", PingLocked);
        doc.Add("PopLock", PopulationLocked);
        doc.Add("PctCap", PercentCapacity);
        doc.Add("QPctCap", QueuePercentCapacity);
        doc.Add("GP", GoodPingMs);
        doc.Add("BP", BadPingMs);
        doc.Add("PingAdr", PingAddress);
        doc.Add("Rulesets", Rulesets);
        doc.Add("DC", DataCenter);
        return doc.ToString();
    }
}

/// <summary>A single self-closing element with attributes, which is all the client reads from these strings.</summary>
internal sealed class AttributeDocument
{
    private readonly StringBuilder _text;

    public AttributeDocument(string element)
    {
        _text = new StringBuilder("<").Append(element);
    }

    public void Add(string name, string value)
    {
        _text.Append(' ').Append(name).Append("=\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '&':
                    _text.Append("&amp;");
                    break;
                case '<':
                    _text.Append("&lt;");
                    break;
                case '>':
                    _text.Append("&gt;");
                    break;
                case '"':
                    _text.Append("&quot;");
                    break;
                default:
                    _text.Append(c);
                    break;
            }
        }

        _text.Append('"');
    }

    public void Add(string name, int value) => Add(name, value.ToString(CultureInfo.InvariantCulture));

    public void Add(string name, bool value) => Add(name, value ? "1" : "0");

    public override string ToString() => _text.ToString() + "/>";
}

/// <summary>Server → client, opcode 0x0E: the list the client shows / picks from.</summary>
public sealed record ServerListReply(IReadOnlyList<GameServerEntry> Servers)
{
    public const byte Opcode = 0x0E;

    public void WriteTo(PacketWriter w)
    {
        w.WriteByte(Opcode);
        w.WriteInt32(Servers.Count);
        foreach (GameServerEntry server in Servers)
        {
            server.WriteTo(w);
        }
    }
}
