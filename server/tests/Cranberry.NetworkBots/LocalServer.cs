using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using Cranberry.Login;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Match;
using Cranberry.Zone.Loot;

namespace Cranberry.NetworkBots;

// A disposable real LoginService + ZoneService, each on its normal SOE listener thread.
// Only initial roster/configuration are fixtures. Client actions cross real sockets; the
// hosted TLS endpoint feeds the same listeners through their bounded in-process bridge.
internal sealed class LocalServer : IDisposable, ITransportLog, IPacketRecorder
{
    private readonly SoeListener _login, _gateway;
    private readonly ConcurrentDictionary<SoeConnection, byte> _connections = new();
    private readonly StreamWriter _log;
    public Vector3 Landing { get; }
    public long Messages, Bytes, MaxPendingBytes, MaxPendingPackets;
    public readonly ConcurrentQueue<string> Errors = new();
    public readonly ConcurrentQueue<SetupEvent> SetupEvents = new();
    internal sealed record SetupEvent(string Kind, string Remote, uint Tick, ulong Target = 0);
    public Func<uint>? Tick;
    private readonly long[] _incomingPoseAge = new long[10001], _outgoingPoseAge = new long[10001];
    public object PoseTimings => new { incoming = Quantiles(_incomingPoseAge), outgoing = Quantiles(_outgoingPoseAge) };

    public static string TicketFor(int index) => $"loopback-network-fixture-{index:D5}";
    public ZoneService Zone { get; }
    private Task<object>? _diagnosticSnapshot;
    public Task<object> CaptureProductionDiagnostics()
    {
        if (_diagnosticSnapshot is { IsCompleted: false }) return _diagnosticSnapshot;
        var source = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        _diagnosticSnapshot = source.Task;
        _gateway.Post(() =>
        {
            try { source.TrySetResult(new { Transport = _gateway.CaptureDiagnostics(16), Zone = Zone.CaptureDiagnostics(16) }); }
            catch (Exception ex) { source.TrySetException(ex); }
        });
        return source.Task;
    }

    public LocalServer(int population, string output, bool individualAccounts = false,
        LocalAccountDirectory? suppliedAccounts = null, IReadOnlyList<string>? accountIds = null, int matchPopulation = 0,
        int simultaneousMatches = 1)
    {
        _log = new StreamWriter(Path.Combine(output, "server.log")) { AutoFlush = true };
        var tickets = new GatewayTicketRegistry();
        var roster = new CharacterRosterStore();
        var accounts = suppliedAccounts ?? (individualAccounts ? new LocalAccountDirectory(allowLoopbackDevelopment: false) : null);
        for (int i = 0; i < population; i++)
        {
            var character = roster.Create(1, new CharacterCreatePayload(2, 3, 270, 2, $"NetBot{i:D3}", 664, 2, 2,
                "Windows", "6.2", GatewayLoginRequest.AugustVersion, "Test"));
            if (accounts is not null)
            {
                string account = accountIds?[i] ?? $"netbot-{i:D5}";
                accounts.Register(account, TicketFor(i));
                accounts.BindCharacter(account, character.EntityKey);
            }
        }
        var spawns = Z2LootSpawns.LoadDefault();
        Landing = spawns.Points.ToArray().Where(p => p.HasArea && Math.Abs(p.X) < 1500 && Math.Abs(p.Z) < 1500).First().Position;
        var zone = new ZoneService(this, this, tickets, new ZoneOptions
        {
            LobbyCountdownMs = 8000,
            PublicQueue = matchPopulation > 0 ? new PublicQueueOptions
                { WaitMs = 3000, MinPlayers = Math.Min(150, matchPopulation), MaxPlayers = 150,
                    MaxAllocatedMatches = Math.Max(2, simultaneousMatches) } : null,
            Drop = new DropOptions { Enabled = false }, MatchDropSpawn = new Vector4(Landing, 1),
            DropAltitude = Landing.Y + 120,
            MatchSeed = 20170907,
            GiveStarterWeapon = true, StarterWeaponItemDefinitionId = 2425,
        });
        Zone = zone;
        zone.DiagnosticsEnabled = true;
        _gateway = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), zone, this,
            new SoeListenerOptions { ObserveDatagram = WireAudit.Enabled ? WireAudit.Server : null, EnableDiagnostics = true });
        zone.Post = _gateway.Post;
        _gateway.Start();
        var login = new LoginService(this, this, Convert.FromBase64String("F70IaxuU8C/w7FPXY1ibXw=="),
            roster, tickets, "127.0.0.1:" + _gateway.LocalEndPoint.Port, accounts: accounts);
        _login = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), login, this);
        _login.Start();
    }

    public IPEndPoint LoginEndPoint => _login.LocalEndPoint;
    public SoeListener LoginListener => _login;
    public SoeListener GatewayListener => _gateway;
    public IPEndPoint GatewayEndPoint => _gateway.LocalEndPoint;
    public int GatewayConnections => _gateway.ConnectionCount;
    public int LoginConnections => _login.ConnectionCount;
    public object TransportDiagnostics => new { login = _login.Diagnostics, gateway = _gateway.Diagnostics };
    public bool IsEnabled(TransportLogLevel level) => level >= TransportLogLevel.Info;
    public void Log(TransportLogLevel level, string message)
    {
        if (level >= TransportLogLevel.Error) Errors.Enqueue(message);
        lock (_log) _log.WriteLine(level + " " + message);
    }
    public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
    public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
    public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
    {
        Interlocked.Increment(ref Messages); Interlocked.Add(ref Bytes, bytes.Length);
        _connections.TryAdd(connection, 0);
        // RecordMessage runs on the connection's owning listener, so these reads are coherent.
        long pendingBytes = connection.PendingBytes, pendingPackets = connection.PendingDatagrams;
        if (pendingBytes > MaxPendingBytes) Interlocked.Exchange(ref MaxPendingBytes, pendingBytes);
        if (pendingPackets > MaxPendingPackets) Interlocked.Exchange(ref MaxPendingPackets, pendingPackets);
        if (Tick is not null)
        {
            if (direction == "c2s" && bytes.Length >= 4 && bytes[0] == 6)
            {
                if (bytes[1] == 0x86 && bytes[2] == 6)
                    SetupEvents.Enqueue(new("select", connection.RemoteEndPoint.ToString(), Tick()));
                else if (bytes[1] == 9 && bytes[2] == 0x42 && bytes[3] == 0)
                    SetupEvents.Enqueue(new("console", connection.RemoteEndPoint.ToString(), Tick()));
            }
            else if (direction == "s2c" && bytes.Length >= 20 && bytes[0] == 5
                && bytes[1] == 0x11 && bytes[2] == 2 && bytes[3] == 0
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) == 1429)
                SetupEvents.Enqueue(new("ammo", connection.RemoteEndPoint.ToString(), Tick(),
                    BinaryPrimitives.ReadUInt64LittleEndian(bytes[4..])));
            if (direction == "c2s" && bytes.Length >= 8 && bytes[0] == 0x46)
                Age(_incomingPoseAge, BinaryPrimitives.ReadUInt32LittleEndian(bytes[3..]));
            else if (direction == "s2c" && bytes.Length > 10 && bytes[0] == 5 && bytes[1] == 0x78)
            {
                _ = BotWire.VarInt(bytes[2..], out int length);
                Age(_outgoingPoseAge, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(4 + length)..]));
            }
        }
    }
    private void Age(long[] histogram, uint sent)
    {
        int age = unchecked((int)(Tick!() - sent));
        if (age is >= 0 and <= 120000) Interlocked.Increment(ref histogram[Math.Min(age, 10000)]);
    }
    private static object Quantiles(long[] histogram)
    {
        long total = histogram.Sum(), sum = 0;
        int p95 = -1, p99 = -1;
        for (int i = 0; i < histogram.Length && total > 0; i++)
        {
            sum += histogram[i];
            if (p95 < 0 && sum >= Math.Ceiling(total * .95)) p95 = i;
            if (p99 < 0 && sum >= Math.Ceiling(total * .99)) { p99 = i; break; }
        }
        return new { samples = total, p95Ms = p95, p99Ms = p99, capMs = 10000 };
    }
    public long Resends => _connections.Keys.Sum(c => c.DatagramsResent);
    public long ReplacedPoses => _connections.Keys.Sum(c => c.LatestMessagesReplaced);
    public long CommittedPoses => _connections.Keys.Sum(c => c.LatestMessagesCommitted);
    public int PendingPoses => _connections.Keys.Sum(c => c.PendingLatestMessages);
    public void Info(string message) { lock (_log) _log.WriteLine(message); }
    public void Warn(string message) { lock (_log) _log.WriteLine("WARN " + message); }
    public void Error(string message, Exception? error = null)
    { Errors.Enqueue(message); lock (_log) _log.WriteLine("ERROR " + message + " " + error); }
    public void Dispose() { _login.Dispose(); _gateway.Dispose(); _log.Dispose(); }
}
