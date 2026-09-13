namespace Cranberry.Harness.Protocol;

/// <summary>
/// Channels 2 and 3 carry no opcode and nothing in this repository decodes them (docs/71 §1, §9,
/// §14.3). The harness therefore <b>replays recorded bytes verbatim</b> rather than synthesising
/// a movement stream: a synthesised delta stream would be the harness's invention, and a server
/// that accepted it would have proved nothing about the client.
///
/// The embedded samples are from <c>wire-20260829-184346.txt</c>. <see cref="FromCapture"/> loads
/// a larger pool from any capture on disk when a scenario wants one.
/// </summary>
public sealed class MovementReplay
{
    private readonly IReadOnlyList<byte[]> _packets;
    private int _cursor;

    public MovementReplay(IReadOnlyList<byte[]> packets)
    {
        if (packets.Count == 0)
        {
            throw new ArgumentException("A movement replay needs at least one recorded packet.", nameof(packets));
        }

        _packets = packets;
    }

    public int Count => _packets.Count;

    /// <summary>The next recorded packet, wrapping round at the end of the pool.</summary>
    public byte[] Next()
    {
        byte[] packet = _packets[_cursor];
        _cursor = (_cursor + 1) % _packets.Count;
        return (byte[])packet.Clone();
    }

    public void Rewind() => _cursor = 0;

    /// <summary>
    /// The single 49-byte channel-2 packet the client sends at +0.067 s after ClientBeginZoning.
    /// Both healthy and hung clients send exactly this one (docs/71 §12.1), which is precisely why
    /// its presence proves nothing and the <i>second</i> packet is the signal.
    /// </summary>
    public static byte[] FirstZoningPacket() => Convert.FromHexString(FirstZoningHex);

    /// <summary>Channel-2 player movement, recorded mid-descent.</summary>
    public static MovementReplay PlayerMovement() => new([.. PlayerMovementHex.Select(Convert.FromHexString)]);

    /// <summary>Channel-3 managed-object movement, recorded while parachuting.</summary>
    public static MovementReplay ManagedMovement() => new([.. ManagedMovementHex.Select(Convert.FromHexString)]);

    /// <summary>
    /// Reads channel-2 (or channel-3) client packets out of a <c>wire-*.txt</c> capture. Nothing is
    /// decoded: the bytes go on the wire exactly as the client sent them.
    /// <para>
    /// <paramref name="notBefore"/> and <paramref name="notAfter"/> select a time window by the
    /// capture's own <c>HH:MM:SS.mmm</c> first column. A scenario uses them to pick a stretch of a
    /// real session — the post-landing walk rather than the descent, say — because the position a
    /// replayed packet reports is the position the server will believe the player is standing at,
    /// and that is the whole point of a loot-streaming scenario.
    /// </para>
    /// </summary>
    public static MovementReplay FromCapture(
        string capturePath,
        byte channel,
        int max = 4096,
        string? notBefore = null,
        string? notAfter = null)
    {
        var packets = new List<byte[]>();
        foreach (string line in File.ReadLines(capturePath))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split('|');
            if (parts.Length < 6)
            {
                continue;
            }

            if (parts[3].Trim() != "c2s" || parts[2].Trim() != AugustClient.GatewayProtocolName)
            {
                continue;
            }

            string at = parts[0].Trim();
            if ((notBefore is not null && string.CompareOrdinal(at, notBefore) < 0)
                || (notAfter is not null && string.CompareOrdinal(at, notAfter) > 0))
            {
                continue;
            }

            string hex = parts[5].Trim();
            if (hex.Length < 2)
            {
                continue;
            }

            byte header = Convert.FromHexString(hex[..2])[0];
            if (header >> 5 != channel)
            {
                continue;
            }

            packets.Add(Convert.FromHexString(hex));
            if (packets.Count >= max)
            {
                break;
            }
        }

        if (packets.Count == 0)
        {
            throw new InvalidOperationException(
                $"No channel-{channel} client packets in '{capturePath}'"
                + (notBefore is null && notAfter is null ? "." : $" between {notBefore ?? "the start"} and {notAfter ?? "the end"}."));
        }

        return new MovementReplay(packets);
    }

    private const string FirstZoningHex =
        "46FF1F5F0D38020085018B62942E06246A0300000000000000000000000000000000000000000000000000";

    private static readonly string[] PlayerMovementHex =
    [
        "460002F73D3802053A03A92A07A1",
        "4602000D3E3802057DD402543206D5B03B",
        "46FE01333E3802051DD4026C320695B03B30D8A73F00000B01A84A01000000",
        "460002F13E380205BA08990000",
        "46FF092E3F380205C6000475D1025C3106D5AE3BA09332400000AB051B029A0100000050",
        "460100453F380205C4",
        "460300C93F3802050510DDCF022C2F06C5AD3B",
        "46FE01DC3F380205ADCF02CC2E06A5AD3BA09332400000AB0513034202000000",
        "46FE012E4038020525CF02A42E0655AD3BA0933240000073050050000000",
        "4601004A403802050511",
        "46FE01444638020525CF02A42E064DAD3BB91334400000000000000000",
        "46FE03DD4638020525CF02A42E064DAD3B1EB911C00000000000000000230799321DD9",
    ];

    private static readonly string[] ManagedMovementHex =
    [
        "669008FF1F5CB63802002511BDDA021C7E189DB73B0000000000000000000000002203220322032203000000000000000000",
        "669008FF0195B63802000510BDDA020C7E189DB73B00000000000000EB0428000000",
        "669008FE01AFB6380200BDDA02CC7D189DB73B00000000000000EB04B0000000",
        "669008FE018AB7380200BDDA02347B189DB73BF22C4EB0000000EB049201080000",
        "669008FE0105C0380200BDDA02A434188DB53B5F044A3C120109098305FA0472025029",
        "669008FE01FBCA380200D5F402CCFC167D3E3B301E003DAA0309DB0283069211115B0111",
        "66900801007EB63802000511",
    ];
}
