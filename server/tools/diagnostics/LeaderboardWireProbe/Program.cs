using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cranberry.Harness;
using Cranberry.Harness.Protocol;

// Explicit local menu-only smoke probe. The normal loopback development identity must
// already be enabled. Never creates an account, presses PLAY, or settles a match.
string output = Path.GetFullPath(args.ElementAtOrDefault(0) ?? throw new ArgumentException("Supply a new report path."));
if (File.Exists(output)) throw new ArgumentException("Report already exists.");
if (System.Diagnostics.Process.GetProcessesByName("H1Z1").Length != 0)
    throw new InvalidOperationException("Close the native client before probing its local development identity.");
int port = int.Parse(args.ElementAtOrDefault(1) ?? "20042");
var responses = Channel.CreateBounded<byte[]>(32);
await using var client = new HarnessClient(new HarnessOptions
{
    LoginEndPoint = new IPEndPoint(IPAddress.Loopback, port),
    ObservePacket = (packet, _) =>
    {
        if (packet.Kind == ObservedKind.ZoneTunnel && packet.Bytes.Length > 2 && packet.Bytes[1] == 0x67)
            responses.Writer.TryWrite(packet.Bytes[1..]);
    },
});
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
await client.ConnectAsync(deadline.Token);
var results = new List<object>();
for (uint mode = 1; mode <= 3; mode++)
{
    byte[] select = new byte[42];
    select[0] = 0x67; select[1] = 0x0a;
    BinaryPrimitives.WriteUInt32LittleEndian(select.AsSpan(18), mode);
    BinaryPrimitives.WriteUInt32LittleEndian(select.AsSpan(30), 7);
    BinaryPrimitives.WriteUInt32LittleEndian(select.AsSpan(34), 1);
    // Zero filter is the native around-me operation.
    byte[] page = await Request(select, 0x0b);
    var rows = Rows(page);
    if (rows.Count > 0 && !rows.Any(r => r.Guid == client.SelfGuid && r.Highlighted))
        throw new InvalidDataException("Around-me page omitted the authenticated player's highlight.");
    byte[] other = new byte[26];
    other[0] = 0x67; other[1] = 0x1d;
    BinaryPrimitives.WriteUInt64LittleEndian(other.AsSpan(10), client.SelfGuid);
    BinaryPrimitives.WriteUInt32LittleEndian(other.AsSpan(18), mode);
    byte[] detail = await Request(other, 0x1e);
    using var reader = new BinaryReader(new MemoryStream(detail), Encoding.UTF8);
    reader.BaseStream.Position = 10;
    if (reader.ReadUInt64() != client.SelfGuid) throw new InvalidDataException("Wrong clicked-player GUID.");
    _ = Name(reader);
    if (reader.ReadUInt32() != mode) throw new InvalidDataException("Wrong detail mode.");
    uint tier = reader.ReadUInt32(), division = reader.ReadUInt32(), points = reader.ReadUInt32();
    int best = reader.ReadInt32();
    if (best is < 0 or > 10 || reader.BaseStream.Position + 36 * best != detail.Length)
        throw new InvalidDataException("Misaligned top-ten detail.");
    if (rows.FirstOrDefault(r => r.Guid == client.SelfGuid) is { } me && me.Points != points)
        throw new InvalidDataException("Leaderboard and clicked-player score differ.");
    BinaryPrimitives.WriteUInt32LittleEndian(select.AsSpan(30), tier);
    BinaryPrimitives.WriteUInt32LittleEndian(select.AsSpan(34), division);
    BinaryPrimitives.WriteUInt32LittleEndian(select.AsSpan(38), 1);
    var top = Rows(await Request(select, 0x0b));
    byte[] personal = (byte[])other.Clone(); personal[1] = 0x06;
    byte[] personalReply = await Request(personal, 0x07);
    if (personalReply.Length < 127 || BinaryPrimitives.ReadUInt32LittleEndian(personalReply.AsSpan(22)) != 5
        || BinaryPrimitives.ReadUInt32LittleEndian(personalReply.AsSpan(26)) != 17867)
        throw new InvalidDataException("Personal ranking season metadata differs.");
    results.Add(new { mode, aroundMeRows = rows.Count, topRows = top.Count, tier, division, points,
        bestTenRows = best, leaderboardBytes = page.Length, detailBytes = detail.Length, personalBytes = personalReply.Length });
}
await client.QuitAsync(playSeconds: 0, cancellationToken: deadline.Token);
string report = JsonSerializer.Serialize(new { loopbackUdpVerified = true, matchStarted = false, results },
    new JsonSerializerOptions { WriteIndented = true });
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, report);
Console.WriteLine(report);

async Task<byte[]> Request(byte[] payload, byte reply)
{
    client.GatewayLink!.Send(GatewayWire.Tunnel(0, payload));
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
    timeout.CancelAfter(TimeSpan.FromSeconds(5));
    while (true)
    {
        byte[] packet = await responses.Reader.ReadAsync(timeout.Token);
        if (packet[1] == reply) return packet;
    }
}

static string Name(BinaryReader reader)
{
    int length = reader.ReadInt32();
    if (length is < 0 or > 128) throw new InvalidDataException("Invalid name length.");
    byte[] bytes = reader.ReadBytes(length);
    if (bytes.Length != length) throw new EndOfStreamException();
    return Encoding.UTF8.GetString(bytes);
}

static List<Row> Rows(byte[] packet)
{
    using var reader = new BinaryReader(new MemoryStream(packet), Encoding.UTF8);
    reader.BaseStream.Position = 18;
    int count = reader.ReadInt32();
    if (count is < 0 or > 50) throw new InvalidDataException("Unbounded leaderboard page.");
    List<Row> rows = [];
    for (int i = 0; i < count; i++)
    {
        ulong guid = reader.ReadUInt64(); uint rank = reader.ReadUInt32(); _ = Name(reader);
        uint points = reader.ReadUInt32();
        for (int word = 0; word < 6; word++) _ = reader.ReadUInt32();
        rows.Add(new(guid, rank, points, reader.ReadBoolean()));
    }
    if (reader.BaseStream.Position != packet.Length) throw new InvalidDataException("Misaligned leaderboard row.");
    return rows;
}

sealed record Row(ulong Guid, uint Rank, uint Points, bool Highlighted);
