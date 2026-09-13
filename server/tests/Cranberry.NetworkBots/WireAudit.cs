using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Cranberry.NetworkBots;

// Opt-in packet metadata at each bridge boundary. No application bytes or credentials are logged.
internal static class WireAudit
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Stream> Streams = [];
    private static readonly ConcurrentDictionary<int, int> Ports = new();
    private static Timer? _timer;
    private static string? _path;
    private sealed class Stream
    {
        public long Packets, Bytes;
        public readonly Dictionary<int, long> Opcodes = [];
        public readonly Queue<object> Recent = [];
    }
    public static bool Enabled => _path is not null;
    public static bool Selected(int index) => index is 0 or 1 or 150;
    public static void Start(string output)
    {
        _path = Path.Combine(output, "wire-audit.jsonl");
        _timer = new Timer(_ => Snapshot(), null, 1000, 1000);
    }
    public static void Tunnel(int index, byte channel, string stage, ReadOnlyMemory<byte> bytes)
    {
        if (channel != 1) return;
        if (stage.StartsWith("gateway-port-", StringComparison.Ordinal))
            Ports[int.Parse(stage[13..])] = index;
        Record($"tunnel-{index}-{stage}", bytes.Span);
    }
    public static void Server(System.Net.IPEndPoint peer, bool incoming, ReadOnlySpan<byte> bytes)
    {
        if (Ports.TryGetValue(peer.Port, out int index)) Record($"soe-{index}-{(incoming ? "receive" : "send")}", bytes);
    }
    public static void Record(string stage, ReadOnlySpan<byte> bytes)
    {
        int opcode = bytes.Length >= 2 && bytes[0] == 0 ? bytes[1] : -1;
        int sequence = bytes.Length >= 4 && opcode is 9 or 13 or 17 or 21
            ? BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]) : -1;
        lock (Gate)
        {
            if (!Streams.TryGetValue(stage, out var stream)) Streams[stage] = stream = new();
            stream.Packets++; stream.Bytes += bytes.Length;
            stream.Opcodes[opcode] = stream.Opcodes.GetValueOrDefault(opcode) + 1;
            if (stream.Recent.Count >= 32) stream.Recent.Dequeue();
            stream.Recent.Enqueue(new { at = Environment.TickCount64, opcode, sequence, size = bytes.Length });
        }
    }
    private static void Snapshot()
    {
        lock (Gate)
        {
            File.AppendAllText(_path!, JsonSerializer.Serialize(new { at = Environment.TickCount64,
                streams = Streams.ToDictionary(p => p.Key, p => new { p.Value.Packets, p.Value.Bytes, p.Value.Opcodes, p.Value.Recent }) }) + Environment.NewLine);
        }
    }
}
