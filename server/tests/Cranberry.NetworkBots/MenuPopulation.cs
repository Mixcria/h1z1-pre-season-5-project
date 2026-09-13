using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Cranberry.Harness;
using Cranberry.Harness.Behaviour;
using Cranberry.Harness.Protocol;
using Cranberry.Harness.Soe;
using Cranberry.Launcher.Core;

namespace Cranberry.NetworkBots;

// Full authenticated login and menu bootstrap, with idle keepalives/time synchronization.
// Observations retain counters only; every connection still drains/decrypts all server bytes.
internal sealed class MenuPopulation(int count, int offset, int loginPort, TlsFixtureAddress? tls = null) : IAsyncDisposable
{
    private readonly Entry[] _entries = Enumerable.Range(offset, count).Select(i => new Entry(i)).ToArray();
    private int _connected;
    private readonly CancellationTokenSource _stop = new();
    private Task? _polling;
    private long _statusPolls, _statusFailures;
    private sealed class Entry(int index)
    {
        public int Index = index;
        public HarnessClient? Client;
        public HttpClient? Http;
        public GameTunnel? Tunnel;
        public long NextStatusAt;
        public long Messages, Bytes, MatchPackets, LastReceivedMs;
        public void Observe(ObservedPacket packet, TimeSpan _)
        {
            Interlocked.Increment(ref Messages); Interlocked.Add(ref Bytes, packet.Bytes.Length);
            Interlocked.Exchange(ref LastReceivedMs, Environment.TickCount64);
            if (packet.IsZone(0xd5) || packet.IsZone(0x78) || packet.IsZone(0x82) && packet.SubOpcodeU8 == 0x15)
                Interlocked.Increment(ref MatchPackets);
        }
    }

    public async Task Connect(CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(32);
        await Task.WhenAll(_entries.Select(async entry =>
        {
            await slots.WaitAsync(ct);
            try
            {
                var endpoint = new IPEndPoint(IPAddress.Loopback, loginPort);
                string ticket = LocalServer.TicketFor(entry.Index);
                IPEndPoint? gateway = null;
                if (tls is not null)
                {
                    var access = await TlsFixture.Connect(tls, entry.Index, ct);
                    entry.Http = access.Http; entry.Tunnel = access.Tunnel;
                    endpoint = new(IPAddress.Loopback, access.Tunnel.LoginPort);
                    gateway = new(IPAddress.Loopback, access.Tunnel.GatewayPort); ticket = access.Launch.Ticket;
                }
                entry.Client = new HarnessClient(new HarnessOptions
                {
                    LoginEndPoint = endpoint, GatewayEndPointOverride = gateway,
                    LoginTicket = ticket, CharacterIndex = 0,
                    RetainLedger = false, JournalCapacity = 8, Seed = 8000 + entry.Index,
                    LowFrequencyIdlePolling = true,
                    ObservePacket = entry.Observe, Timings = new ClientTimings { ReplayMovement = false },
                });
                await entry.Client.ConnectAsync(ct);
                await entry.Client.ExpectAsync(HarnessMilestone.MenuClientFinishedLoadingSent,
                    TimeSpan.FromSeconds(120), cancellationToken: ct);
                if (WireAudit.Enabled && WireAudit.Selected(entry.Index))
                    entry.Client.GatewayLink!.ObserveDatagram = (incoming, bytes) => WireAudit.Record($"soe-{entry.Index}-{(incoming ? "receive" : "send")}", bytes.Span);
                int complete = Interlocked.Increment(ref _connected);
                if (complete % 100 == 0 || complete == count) Console.WriteLine($"Menu ready: {complete}/{count}");
            }
            finally { slots.Release(); }
        }));
        if (tls is not null) _polling = PollLauncher();
    }

    private async Task PollLauncher()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            do
            {
                long now = Environment.TickCount64;
                await Parallel.ForEachAsync(_entries.Where(e => e.Http is not null && e.NextStatusAt <= now),
                    new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = _stop.Token }, async (entry, ct) =>
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(5000);
                        try
                        {
                            using var response = await entry.Http!.GetAsync("api/state", deadline.Token);
                            response.EnsureSuccessStatusCode();
                            string json = await response.Content.ReadAsStringAsync(deadline.Token);
                            if (!json.Contains("\"me\"", StringComparison.Ordinal)) throw new InvalidDataException("Invalid launcher state.");
                            Interlocked.Increment(ref _statusPolls);
                        }
                        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or OperationCanceledException)
                        { if (!ct.IsCancellationRequested) Interlocked.Increment(ref _statusFailures); }
                        entry.NextStatusAt = now + 3000 + entry.Index % 100;
                    });
            } while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public bool Healthy => _connected == count && Interlocked.Read(ref _statusFailures) == 0 && _entries.All(e =>
        e.Client?.GatewayLink?.CloseCause == LinkCloseCause.None && e.MatchPackets == 0
        && e.Tunnel?.Completion.IsCompleted != true
        && Environment.TickCount64 - Interlocked.Read(ref e.LastReceivedMs) < 15000);

    public object Summary() => new
    {
        requested = count, connected = _connected,
        loopbackTls = tls is not null, statusPolls = Interlocked.Read(ref _statusPolls), statusFailures = Interlocked.Read(ref _statusFailures),
        uniqueCharacters = _entries.Select(e => e.Client?.SelfGuid ?? 0).Where(g => g != 0).Distinct().Count(),
        open = _entries.Count(e => e.Client?.GatewayLink?.CloseCause == LinkCloseCause.None),
        matchPackets = _entries.Sum(e => Interlocked.Read(ref e.MatchPackets)),
        messages = _entries.Sum(e => Interlocked.Read(ref e.Messages)),
        bytes = _entries.Sum(e => Interlocked.Read(ref e.Bytes)),
        stalestReplyMs = _entries.Max(e => Environment.TickCount64 - Interlocked.Read(ref e.LastReceivedMs)),
        faults = _entries.Where(e => e.Client?.GatewayLink is { CloseCause: not LinkCloseCause.None })
            .Select(e => new { e.Index, e.Client!.GatewayLink!.Fault }).Take(12).ToArray(),
    };

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); if (_polling is not null) await _polling;
        foreach (Entry entry in _entries)
        {
            if (entry.Client is not null) await entry.Client.DisposeAsync();
            if (entry.Tunnel is not null) await entry.Tunnel.DisposeAsync();
            entry.Http?.Dispose();
        }
        _stop.Dispose();
    }

    public static async Task<int> Run(string[] args)
    {
        string? Value(string key) { int at = Array.IndexOf(args, key); return at < 0 ? null : args[at + 1]; }
        int count = int.Parse(Value("--menu-bots") ?? "2000");
        int offset = int.Parse(Value("--bots") ?? "150");
        int seconds = int.Parse(Value("--seconds") ?? "180");
        if (count is < 1 or > 7000 || seconds is < 5 or > 3600) throw new ArgumentException("menu-bots 1..7000; seconds 5..3600");
        var fixture = JsonSerializer.Deserialize<FixtureAddress>(File.ReadAllText(Value("--fixture")
            ?? throw new ArgumentException("A separate loopback --fixture is required")))!;
        if (!fixture.IndividualAccounts || fixture.Population < count + offset)
            throw new ArgumentException("Fixture must provision individual accounts for this population");
        string output = Value("--output") ?? throw new ArgumentException("--output required");
        Directory.CreateDirectory(output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15 + count / 100 + seconds / 60));
        await using var menu = new MenuPopulation(count, offset, fixture.LoginPort, fixture.Tls);
        var timer = Stopwatch.StartNew();
        var samples = new List<object>();
        string? failure = null;
        bool healthyThroughout = true;
        try
        {
            await menu.Connect(deadline.Token);
            double admittedSeconds = timer.Elapsed.TotalSeconds;
            while (timer.Elapsed.TotalSeconds - admittedSeconds < seconds)
            {
                await Task.Delay(5000, deadline.Token);
                healthyThroughout &= menu.Healthy;
                samples.Add(new { elapsedSeconds = timer.Elapsed.TotalSeconds, healthy = menu.Healthy, population = menu.Summary() });
                Console.WriteLine($"Menu idle: {timer.Elapsed.TotalSeconds - admittedSeconds:F0}s; healthy={menu.Healthy}");
            }
        }
        catch (Exception ex) { failure = ex.ToString(); }
        bool pass = failure is null && menu.Healthy && healthyThroughout;
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        {
            pass, failure, menuPopulation = menu.Summary(), idleSeconds = seconds, elapsedSeconds = timer.Elapsed.TotalSeconds,
            separateFixtureProcess = true, samples, completedUtc = DateTimeOffset.UtcNow,
            limits = new[] { "Independent protocol clients on host loopback; native rendering and WAN capacity are not measured." },
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Menu run: {(pass ? "PASS" : "FAIL")}; {output}");
        return pass ? 0 : 1;
    }
}
